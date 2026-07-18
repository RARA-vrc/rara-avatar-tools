// RARA アバター軽量化・Quest/iOS対応ツール - 統合ウィンドウ(実装者C所有)
// 1つのウィンドウで PC軽量化 と Quest/iOS対応変換 の両方を実行する入口。既定は「両方」。
//   ・旧2ツール(RARA/PC軽量化ツール・RARA/Quest対応コンバーター)はエンジンを共有しつつ当面併存する。
//   ・元プレファブは非破壊。ユーザーは 生成→調整→再生成 を何度も繰り返すため、再実行は必ず安全
//     (前回の複製は自動で作り直され、蓄積・破壊しない)。
//
// 画面構成:
//   ヘッダー → アバター選択 → ターゲットチップ(PC/Quest, 既定で両方) → プリセット
//   → ① 診断 → ② 構成整理(トグル/SkinnedMesh統合) → ③ PhysBone → ④ テクスチャ → ⑤ ポリゴン
//   → ⑥ マテリアル(PC列/Quest列) → ⑦ 実行
//
// 委譲先:
//   ・診断値の計測  : AvatarStudioDiagnostics.Analyze → StudioDiagnosis(実装者B)
//   ・診断表の描画  : AvatarStudioDualDiagnosisPanel.Draw(実装者B)
//   ・各プレビュー  : AvatarStudioPreviewPanels.Draw*Panel(実装者B)
//   ・設定モデル/永続化/マッピング: AvatarStudioSettings / AvatarStudioSettingsIO / AvatarStudioMapping(実装者A)
//   ・実行と結果表示: AvatarStudioExecution(実装者C)
//
// プレビューパネルは QuestConvertSettings / PCOptimizeSettings を引数で要求する。これらは
// このウィンドウが毎フレーム AvatarStudioMapping で現在の設定から作り直して渡す(Bのファイルはマッピング非依存)。
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using RARA.PCOptimizer;
using RARA.QuestConverter;

namespace RARA.AvatarStudio
{
    /// <summary>PC軽量化とQuest/iOS対応を1画面で扱う統合ウィンドウ。</summary>
    public class AvatarStudioWindow : EditorWindow
    {
        // ---- 状態(スクリプトリロード後も選択を保持) ----
        [SerializeField] private VRCAvatarDescriptor _avatar;
        private AvatarStudioSettings _settings;
        private AvatarStudioRunResult _lastRun;

        // H1: リネームで取り残された生成複製の警告(アバター選択時・再スキャン時・実行時に更新)。
        private System.Collections.Generic.List<string> _orphanWarnings;

        // 実装者Bのプレビュー用キャッシュ(重い計算を入力シグネチャで再利用。実行後に Bump、アバター切替で Clear)。
        private readonly AvatarStudioPreviewCache _cache = new AvatarStudioPreviewCache();

        // 直近の診断結果(null=未診断)。アバター/対象の変更で無効化し、次に診断ステップを開いたとき再計測する。
        private StudioDiagnosis _diag;

        // 他ツール/エイリアスメニューから開いたときに適用したい対象(nullは変更なし)。アバター未選択でも
        // 保持し、設定が読み込まれた時点(SetAvatar / OnGUI)で一度だけ適用してクリアする。
        private bool? _pendingTargetPC;
        private bool? _pendingTargetQuest;

        // ---- UIスクロール ----
        private Vector2 _mainScroll;
        private Vector2 _pcReportScroll;
        private Vector2 _questReportScroll;

        // RARA メニューは2項目だけ: 「PC軽量化ツール」と「Quest対応ツール」。どちらも同じ統合ウィンドウを
        // 対象(PC / Quest)を絞って開くだけで、ウィンドウ内でいつでも対象を切り替えられる。
        // 引数なし Open() はメニューには出さず、他コードからの互換用エントリとして残す。
        public static void Open()
        {
            var window = GetWindow<AvatarStudioWindow>();
            window.titleContent = new GUIContent("RARA アバター軽量化・Quest/iOS対応ツール");
            window.minSize = new Vector2(560f, 640f);
            window.Show();
        }

        /// <summary>統合ウィンドウを開く(唯一のメニュー項目。PC/Quest の対象はウィンドウ内のチップで切替)。</summary>
        [MenuItem("RARA/アバター軽量化・Quest・iOS対応ツール", priority = 100)]
        public static void OpenFromMenu()
        {
            OpenInternal(null, null, null);
        }

        /// <summary>指定アバターを対象にウィンドウを開く(他ツールからの遷移用。対象は保存済みの設定に従う)。</summary>
        public static void Open(GameObject avatar)
        {
            OpenInternal(avatar, null, null);
        }

        /// <summary>指定アバターを対象に、PC/Quest の対象を明示してウィンドウを開く。</summary>
        public static void Open(GameObject avatar, bool pcTarget, bool questTarget)
        {
            OpenInternal(avatar, pcTarget, questTarget);
        }

        private static void OpenInternal(GameObject avatar, bool? pcTarget, bool? questTarget)
        {
            var window = GetWindow<AvatarStudioWindow>();
            window.titleContent = new GUIContent("RARA アバター軽量化・Quest/iOS対応ツール");
            window.minSize = new Vector2(560f, 640f);
            window._pendingTargetPC = pcTarget;
            window._pendingTargetQuest = questTarget;
            if (avatar != null)
            {
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    window.SetAvatar(descriptor);
                }
            }
            window.ApplyPendingTargets();
            window.Show();
            window.Focus();
        }

        /// <summary>保留中の対象指定(エイリアス/他ツール遷移)を設定へ一度だけ適用する。設定未読込なら次回まで保持。</summary>
        private void ApplyPendingTargets()
        {
            if (_settings == null) return;
            if (!_pendingTargetPC.HasValue && !_pendingTargetQuest.HasValue) return;
            if (_pendingTargetPC.HasValue) _settings.targetPC = _pendingTargetPC.Value;
            if (_pendingTargetQuest.HasValue) _settings.targetQuest = _pendingTargetQuest.Value;
            _pendingTargetPC = null;
            _pendingTargetQuest = null;
            _diag = null; // 対象が変わったら再診断。
            Save();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("RARA アバター軽量化・Quest/iOS対応ツール");
            if (_avatar != null && _settings == null)
            {
                _settings = AvatarStudioSettingsIO.LoadSettings(_avatar.gameObject);
            }
        }

        private void SetAvatar(VRCAvatarDescriptor descriptor)
        {
            _avatar = descriptor;
            _settings = descriptor != null ? AvatarStudioSettingsIO.LoadSettings(descriptor.gameObject) : null;
            _lastRun = null;
            _diag = null;
            _cache.Clear();
            ApplyPendingTargets(); // エイリアス/他ツール遷移で指定された対象を、設定読込直後に反映する。
            RescanOrphans();
        }

        private void Save()
        {
            if (_avatar != null && _settings != null)
            {
                AvatarStudioSettingsIO.SaveSettings(_avatar.gameObject, _settings);
            }
        }

        /// <summary>H1: リネームで取り残された生成複製をシーンから走査してキャッシュする。</summary>
        private void RescanOrphans()
        {
            _orphanWarnings = _avatar != null
                ? AvatarStudioExecution.ScanOrphanClones(_avatar.gameObject.scene, _avatar.gameObject)
                : null;
        }

        private void OnGUI()
        {
            AvatarStudioUI.EnsureStyles();

            using (var sv = new EditorGUILayout.ScrollViewScope(_mainScroll))
            {
                _mainScroll = sv.scrollPosition;

                DrawHeader();
                DrawAvatarPicker();

                if (_avatar == null)
                {
                    EditorGUILayout.HelpBox(
                        "シーンに配置したアバター(VRCAvatarDescriptor)を選んでください。"
                        + "元アバターは変更せず、PC用『_Opt』とQuest用『_Quest』の複製を生成します。",
                        MessageType.Info);
                    return;
                }

                if (_settings == null)
                {
                    _settings = AvatarStudioSettingsIO.LoadSettings(_avatar.gameObject);
                    ApplyPendingTargets(); // アバターを先に選んだ状態でエイリアスから開いた場合の保険。
                }

                // プレビューパネルへ渡すエンジン設定を現在の設定から毎フレーム作り直す(実装者Bの要求どおり)。
                GameObject root = _avatar.gameObject;
                PCOptimizeSettings pc = AvatarStudioMapping.BuildPCOptimizeSettings(_settings);
                QuestConvertSettings quest = AvatarStudioMapping.BuildQuestConvertSettings(_settings);

                // 診断は要約行(DrawSummaryLine)を描く前に確定させる。診断ステップ内で遅延計算すると、
                // 同一フレームの Layout(_diag==null で少ないコントロール)と Repaint(計算後 _diag!=null で
                // 多いコントロール)で要約行のコントロール数が食い違い、「Getting control N's position in a
                // group with only N controls」の IMGUI 例外が出る。ここで先に埋めれば毎フレーム一貫する。
                if (_diag == null)
                {
                    _diag = AvatarStudioDiagnostics.Analyze(_avatar, _settings.targetPC, _settings.targetQuest, quest);
                }

                DrawSummaryLine();
                DrawTargetChips();
                DrawPresets();

                EditorGUILayout.Space(6f);

                Step(1, "診断(PC / Quest 現在値と目標)", "Diagnose", true, () => DrawDiagnoseSection(quest));
                Step(2, "構成整理(トグル / SkinnedMesh統合)", "Structure", false, () => DrawStructureSection(root));
                Step(3, "PhysBone 整理", "PhysBone", false, () => DrawPhysBoneSection(root));
                Step(4, "テクスチャ縮小", "Texture", false, () => DrawTextureSection(root, quest));
                Step(5, "ポリゴン削減(Quest)", "Polygon", false, () => DrawPolygonSection(root, quest));
                Step(6, "マテリアル(PC / Quest)", "Material", false, () => DrawMaterialSection(root, pc, quest));
                Step(7, "実行", "Execute", true, DrawExecuteSection);
                Step(8, "ヘルプ / はじめての方へ", "Help", false, DrawHelpSection);
            }
        }

        // ==============================================================
        // ヘッダー・アバター選択
        // ==============================================================

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("RARA アバター軽量化・Quest/iOS対応ツール", AvatarStudioUI.TitleLabel);
            EditorGUILayout.LabelField(
                "1つのウィンドウで PC軽量化 と Quest/iOS対応変換 をまとめて実行します(既定は両方)。"
                + "元アバターは非破壊。何度でも生成→調整→再生成でき、古い複製は自動で作り直されます。",
                AvatarStudioUI.PurposeLabel);
            EditorGUILayout.Space(4f);
        }

        private void DrawAvatarPicker()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                    new GUIContent("アバター", "シーン上のアバター(VRCAvatarDescriptor)"),
                    _avatar, typeof(VRCAvatarDescriptor), true);
                if (EditorGUI.EndChangeCheck() && picked != _avatar)
                {
                    SetAvatar(picked);
                }

                if (GUILayout.Button(new GUIContent("シーンから検出", "現在のシーンにあるアバター(VRCAvatarDescriptor)を自動で探して選択します"), GUILayout.Width(110f)))
                {
                    DetectAvatarFromScene();
                }
            }
        }

        /// <summary>アクティブシーンから最初の VRCAvatarDescriptor を探して選択する。複数あるときは1体目を採用。</summary>
        private void DetectAvatarFromScene()
        {
            var candidates = new System.Collections.Generic.List<VRCAvatarDescriptor>();
            foreach (VRCAvatarDescriptor d in UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true))
            {
                if (d != null && !EditorUtility.IsPersistent(d)) candidates.Add(d);
            }

            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("シーンから検出",
                    "シーンにアバター(VRCAvatarDescriptor)が見つかりませんでした。アバターをシーンに配置してから再度お試しください。", "OK");
                return;
            }

            // 生成複製(_Opt / _Quest)は避け、元アバターらしきものを優先する。
            VRCAvatarDescriptor chosen = null;
            foreach (VRCAvatarDescriptor d in candidates)
            {
                string n = d.gameObject.name;
                if (!n.EndsWith("_Opt", StringComparison.Ordinal) && !n.EndsWith("_Quest", StringComparison.Ordinal))
                {
                    chosen = d;
                    break;
                }
            }
            if (chosen == null) chosen = candidates[0];

            SetAvatar(chosen);
            EditorGUIUtility.PingObject(chosen.gameObject);
        }

        /// <summary>ヘッダー直下に出す常設の要約行(アバター / PC総合 / Quest総合 / 推定サイズ)。直近診断のキャッシュを読むだけで再計測しない。</summary>
        private void DrawSummaryLine()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("アバター: " + (_avatar != null ? _avatar.gameObject.name : "(未選択)"),
                    EditorStyles.miniBoldLabel, GUILayout.MinWidth(140f));

                if (_diag == null)
                {
                    EditorGUILayout.LabelField("診断: 未実行(① 診断を開くと計測します)", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    return;
                }

                if (_settings.targetPC)
                {
                    SummaryChip("PC", AvatarStudioDiagnostics.DisplayRating(_diag.pcOverallRating),
                        AvatarStudioDiagnostics.RatingColor(_diag.pcOverallRating));
                }
                if (_settings.targetQuest)
                {
                    SummaryChip("Quest", AvatarStudioDiagnostics.DisplayRating(_diag.questOverallRating),
                        AvatarStudioDiagnostics.RatingColor(_diag.questOverallRating));

                    if (_diag.questRaw != null && _diag.questRaw.sizeEstimate != null)
                    {
                        bool over = _diag.questRaw.sizeEstimate.overCap;
                        Color prev = GUI.color;
                        GUI.color = over ? AvatarStudioUI.OverLimitColor : GUI.color;
                        EditorGUILayout.LabelField(
                            "推定サイズ 約" + _diag.questRaw.sizeEstimate.estimatedDownloadMB.ToString("F1") + "MB",
                            EditorStyles.miniLabel, GUILayout.Width(150f));
                        GUI.color = prev;
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        private static void SummaryChip(string label, string rating, Color color)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(40f));
            Color prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(rating, EditorStyles.miniBoldLabel, GUILayout.Width(76f));
            GUI.color = prev;
        }

        // ==============================================================
        // ターゲットチップ(PC / Quest, 既定で両方)
        // ==============================================================

        private void DrawTargetChips()
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("対象", GUILayout.Width(36f));

                bool pc = AvatarStudioUI.TargetChip("PC", _settings.targetPC, 120f);
                bool quest = AvatarStudioUI.TargetChip("Quest / iOS", _settings.targetQuest, 140f);
                if (pc != _settings.targetPC || quest != _settings.targetQuest)
                {
                    _settings.targetPC = pc;
                    _settings.targetQuest = quest;
                    _diag = null; // 対象が変わったら再診断。
                    Save();
                }
                GUILayout.FlexibleSpace();
            }
            if (!_settings.targetPC && !_settings.targetQuest)
            {
                EditorGUILayout.HelpBox("対象が選ばれていません。PC か Quest(または両方)を選んでください。", MessageType.Warning);
            }
        }

        // ==============================================================
        // プリセット
        // ==============================================================

        private void DrawPresets()
        {
            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("プリセット", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("おまかせQuest対応(Questだけなら まずこれ)", "Questのみ / 目標Poor / 透過は乗算・加算で近似(ストッキング等の透けも保持)。完全に消したいマテリアルは⑥マテリアルの各行で『非表示』を選択。ポリゴン削減は既定オフ・必要なときにパネルで有効化"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.RoughQuest);
                }
                if (GUILayout.Button(new GUIContent("PCを最低Poorまで軽量化(Very Poor回避)", "PCのみ / 目標Poor"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.PcPoor);
                }
                if (GUILayout.Button(new GUIContent("PCをGoodまで軽量化", "PCのみ / 目標Good"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.PcGood);
                }
                if (GUILayout.Button(new GUIContent("フル両対応(PC+Quest)", "PC(Good)とQuest(Poor)の両方(推奨)"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.FullBoth);
                }
            }
        }

        private enum Preset { RoughQuest, PcPoor, PcGood, FullBoth }

        private void ApplyPreset(Preset preset)
        {
            if (_settings == null) return;
            switch (preset)
            {
                case Preset.RoughQuest:
                    _settings.targetPC = false;
                    _settings.targetQuest = true;
                    _settings.questGoalRank = (int)QuestTargetRank.Poor;
                    // ポリゴン削減(Meshia連携)は既定オフ。プリセットでは有効化せず、目標ランクの目安三角形数のみ用意する
                    // (超過時はポリゴンパネルから明示的に有効化してもらう)。
                    _settings.questEnableMeshiaSimplification = false;
                    _settings.questMeshiaTargetTriangles = QuestRankToTriangles(QuestTargetRank.Poor);
                    // 透過は既定で「近似(乗算/加算のParticlesシェーダー)」。ストッキング等の透けも保持され、
                    // 意図せず不可視化されない。完全に消したい場合は⑥マテリアルの各行で「非表示」を個別選択する。
                    _settings.transparentHandling = TransparentHandling.Emulate;
                    break;

                case Preset.PcPoor:
                    _settings.targetPC = true;
                    _settings.targetQuest = false;
                    _settings.pcTargetRank = PCTargetRank.Poor;
                    break;

                case Preset.PcGood:
                    _settings.targetPC = true;
                    _settings.targetQuest = false;
                    _settings.pcTargetRank = PCTargetRank.Good;
                    break;

                case Preset.FullBoth:
                    _settings.targetPC = true;
                    _settings.targetQuest = true;
                    _settings.pcTargetRank = PCTargetRank.Good;
                    _settings.questGoalRank = (int)QuestTargetRank.Poor;
                    // ポリゴン削減(Meshia連携)は既定オフ。プリセットでは有効化しない(超過時はパネルから有効化)。
                    _settings.questEnableMeshiaSimplification = false;
                    _settings.questMeshiaTargetTriangles = QuestRankToTriangles(QuestTargetRank.Poor);
                    _settings.transparentHandling = TransparentHandling.Emulate;
                    break;
            }
            _diag = null; // 対象・目標が変わったら再診断。
            Save();
            GUI.FocusControl(null);
        }

        /// <summary>Quest目標ランクの目安三角形数(エンジンのポリゴン削減目安に準拠)。</summary>
        private static int QuestRankToTriangles(QuestTargetRank rank)
        {
            switch (rank)
            {
                case QuestTargetRank.Excellent: return 7500;
                case QuestTargetRank.Good: return 10000;
                case QuestTargetRank.Medium: return 15000;
                case QuestTargetRank.Poor: return 20000;
                default: return 20000;
            }
        }

        // ==============================================================
        // ① 診断
        // ==============================================================

        private void DrawDiagnoseSection(QuestConvertSettings quest)
        {
            // 初回(または対象変更後)の自動計測は OnGUI が要約行より前に済ませている(_diag は基本 non-null)。
            // ここでは計測せず、下の「再診断」ボタンで明示的に再計測できるようにする。

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("PC / Quest の現在値を計測し、目標ランクと比較します。", AvatarStudioUI.MiniWrapLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("再診断"), GUILayout.Width(90f)))
                {
                    _diag = AvatarStudioDiagnostics.Analyze(_avatar, _settings.targetPC, _settings.targetQuest, quest);
                }
            }

            if (_diag == null || !_diag.HasAny)
            {
                EditorGUILayout.HelpBox("対象(PC / Quest)を選ぶと診断値を表示します。", MessageType.Info);
                return;
            }

            if (AvatarStudioDualDiagnosisPanel.Draw(_diag, _settings, _settings.targetPC, _settings.targetQuest))
            {
                // 目標ランクの変更を保存(計測値は変わらないため再計測は不要)。
                Save();
            }

            DrawGoalGuideRouting(_diag);
        }

        // ==============================================================
        // 目標達成ガイド(超過項目 → 対応ステップへの誘導)
        //   旧ウィンドウの GetGoalActionForCategory(SuggestionForStat)を移植し、各超過項目に
        //   「→ ステップnで対応」ボタンを添えて該当ステップを開く。
        // ==============================================================

        private void DrawGoalGuideRouting(StudioDiagnosis diag)
        {
            if (diag == null || !diag.HasAny) return;

            int pcGoal = (int)_settings.pcTargetRank;
            int questGoal = _settings.questGoalRank;

            var over = new System.Collections.Generic.List<StudioMetricRow>();
            foreach (StudioMetricRow row in diag.rows)
            {
                bool pcOver = _settings.targetPC && row.hasPcStat
                    && AvatarStudioDiagnostics.IsOverGoal(row.pcRating, pcGoal);
                bool questOver = _settings.targetQuest
                    && (row.questOverLimit || AvatarStudioDiagnostics.IsOverGoal(row.questRating, questGoal));
                if (pcOver || questOver) over.Add(row);
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("目標達成ガイド", EditorStyles.boldLabel);

                if (over.Count == 0)
                {
                    Color prev = GUI.color;
                    GUI.color = AvatarStudioUI.UploadOkColor;
                    EditorGUILayout.HelpBox("選択中の目標ランクを満たしています。実行して複製を生成できます。", MessageType.Info);
                    GUI.color = prev;
                    return;
                }

                EditorGUILayout.LabelField("目標を超えている項目です。右の「→ ステップnで対応」で対応セクションを開けます。", AvatarStudioUI.MiniWrapLabel);

                foreach (StudioMetricRow row in over)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        Color prev = GUI.color;
                        GUI.color = AvatarStudioUI.OverLimitColor;
                        EditorGUILayout.LabelField("・" + row.label + ": " + SuggestionForStat(row.label), AvatarStudioUI.WrapLabel);
                        GUI.color = prev;

                        int step = StepForStat(row.label);
                        if (step > 0)
                        {
                            if (GUILayout.Button(new GUIContent("→ ステップ" + step + "で対応"), GUILayout.Width(140f)))
                            {
                                AvatarStudioUI.SetFoldOpen(StepFoldKey(step), true);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>診断項目ラベルに対応する推奨アクション文言(旧 GetGoalActionForCategory の移植)。</summary>
        private static string SuggestionForStat(string label)
        {
            switch (label)
            {
                case "三角数(ポリゴン)":
                    return "AAO隠面メッシュ削除・不要衣装をQuest除外・ポリゴン削減(Meshia連携)で目標へ(削減はビルド時)";
                case "スキンメッシュ数":
                case "メッシュ数":
                    return "構成整理で『常時表示』またはSkinnedMesh統合";
                case "マテリアルスロット数":
                    return "アトラス統合・トグル整理・SkinnedMesh統合";
                case "テクスチャメモリ(MB)":
                    return "テクスチャ縮小(必要ならQuestは10MB自動調整)";
                case "ボーン数":
                    return "AAOのTrace and Optimizeで未使用ボーンを自動削減";
                case "PhysBoneコンポーネント数":
                case "PhysBone対象Transform数":
                case "PhysBoneコライダー数":
                case "PhysBone衝突チェック数":
                    return "PhysBone整理で選別・マージ(Quest上限へ自動調整も可)";
                case "コンタクト数":
                    return "不要なコンタクト(VRCContact)を削除";
                case "コンストレイント数":
                    return "不要なコンストレイントを削除・統合";
                case "パーティクルシステム数":
                    return "不要なパーティクルシステムを削除";
                case "アニメーター数":
                    return "不要なアニメーター/サブアバターを整理";
                default:
                    return "各ステップで削減してください";
            }
        }

        /// <summary>診断項目ラベルに対応する対応ステップ番号(0=対応する専用ステップなし)。</summary>
        private static int StepForStat(string label)
        {
            switch (label)
            {
                case "三角数(ポリゴン)": return 5;   // ポリゴン削減
                case "スキンメッシュ数": return 2;   // 構成整理
                case "メッシュ数": return 2;          // 構成整理
                case "マテリアルスロット数": return 6; // マテリアル(アトラス)
                case "テクスチャメモリ(MB)": return 4; // テクスチャ縮小
                case "ボーン数": return 7;            // 実行(AAO Trace and Optimize)
                case "PhysBoneコンポーネント数":
                case "PhysBone対象Transform数":
                case "PhysBoneコライダー数":
                case "PhysBone衝突チェック数": return 3; // PhysBone整理
                default: return 0;                    // 専用ステップなし
            }
        }

        /// <summary>ステップ番号に対応する Fold キー(OnGUI の Step 呼び出しと一致させる)。</summary>
        private static string StepFoldKey(int step)
        {
            switch (step)
            {
                case 2: return "Structure";
                case 3: return "PhysBone";
                case 4: return "Texture";
                case 5: return "Polygon";
                case 6: return "Material";
                case 7: return "Execute";
                default: return "Diagnose";
            }
        }

        // ==============================================================
        // ② 構成整理(トグル / SkinnedMesh統合)
        // ==============================================================

        private void DrawStructureSection(GameObject root)
        {
            bool changed = false;
            // トグルの非表示固定で減る専有アセットのサイズチップ用に Quest 変換設定(縮小計画含む)を渡す。
            QuestConvertSettings toggleQuest = AvatarStudioMapping.BuildQuestConvertSettings(_settings);
            changed |= AvatarStudioPreviewPanels.DrawTogglePanel(root, _settings, toggleQuest, _cache);
            EditorGUILayout.Space(4f);
            changed |= AvatarStudioPreviewPanels.DrawSkinnedMeshMergePanel(root, _settings, _cache);

            // Quest固有: 除外パス・隠面メッシュ削減(実装者Bのパネルを構成整理へ配置)。
            if (_settings.targetQuest)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Quest / iOS", EditorStyles.boldLabel);
                changed |= AvatarStudioQuestPanels.DrawQuestExcludePanel(_settings, root);
                EditorGUILayout.Space(4f);
                changed |= AvatarStudioQuestPanels.DrawHiddenMeshPanel(_settings, root);
            }

            if (changed) Save();
        }

        // ==============================================================
        // ③ PhysBone
        // ==============================================================

        private void DrawPhysBoneSection(GameObject root)
        {
            if (AvatarStudioPreviewPanels.DrawPhysBonePanel(root, _settings, _cache)) Save();
        }

        // ==============================================================
        // ④ テクスチャ(PC列 / Quest列)
        // ==============================================================

        private void DrawTextureSection(GameObject root, QuestConvertSettings quest)
        {
            bool changed = false;
            if (_settings.targetPC)
            {
                EditorGUILayout.LabelField("PC", EditorStyles.boldLabel);
                changed |= AvatarStudioPreviewPanels.DrawPCTexturePanel(root, _settings, _cache);
            }
            if (_settings.targetQuest)
            {
                if (_settings.targetPC) EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Quest / iOS", EditorStyles.boldLabel);
                changed |= AvatarStudioPreviewPanels.DrawQuestTexturePanel(root, _settings, quest, _cache);
            }
            if (!_settings.targetPC && !_settings.targetQuest)
            {
                EditorGUILayout.HelpBox("対象を選ぶとテクスチャ縮小の候補を表示します。", MessageType.Info);
            }
            if (changed) Save();
        }

        // ==============================================================
        // ⑤ ポリゴン削減(Quest固有)
        // ==============================================================

        private void DrawPolygonSection(GameObject root, QuestConvertSettings quest)
        {
            if (!_settings.targetQuest)
            {
                EditorGUILayout.HelpBox("ポリゴン削減は Quest/iOS 対象のときに設定できます。上部の対象で Quest を選んでください。", MessageType.Info);
                return;
            }
            if (AvatarStudioPreviewPanels.DrawPolygonPanel(root, _settings, quest, _cache)) Save();
        }

        // ==============================================================
        // ⑥ マテリアル(PC列: アトラス / Quest列: 変換方法)
        // ==============================================================

        private void DrawMaterialSection(GameObject root, PCOptimizeSettings pc, QuestConvertSettings quest)
        {
            bool changed = false;
            if (_settings.targetPC)
            {
                EditorGUILayout.LabelField("PC(アトラス統合)", EditorStyles.boldLabel);
                changed |= AvatarStudioPreviewPanels.DrawPCAtlasPanel(root, _settings, pc, _cache);
            }
            if (_settings.targetQuest)
            {
                if (_settings.targetPC) EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Quest / iOS(マテリアル変換)", EditorStyles.boldLabel);
                changed |= AvatarStudioPreviewPanels.DrawQuestMaterialPanel(_avatar, _settings, quest, _cache);

                // Quest固有: アトラス統合・表情デカール(実装者Bのパネルをマテリアルへ配置)。
                EditorGUILayout.Space(4f);
                changed |= AvatarStudioQuestPanels.DrawQuestAtlasPanel(_settings, root);
                EditorGUILayout.Space(4f);
                changed |= AvatarStudioQuestPanels.DrawExpressionDecalPanel(_settings, root);

                // Quest変換設定(シェーダー/テクスチャ形式など)は実行の直前に確認できるよう末尾へ。
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Quest / iOS 変換設定(実行前の最終確認)", EditorStyles.boldLabel);
                changed |= AvatarStudioQuestPanels.DrawQuestConvertSettingsPanel(_settings, root);
            }
            if (!_settings.targetPC && !_settings.targetQuest)
            {
                EditorGUILayout.HelpBox("対象を選ぶとマテリアルのプレビューを表示します。", MessageType.Info);
            }
            if (changed) Save();
        }

        // ==============================================================
        // ⑦ 実行
        // ==============================================================

        private void DrawExecuteSection()
        {
            // H1: リネームで取り残された複製の警告(実行前に表示)。
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("取り残された複製を再確認", "元アバターのリネームで残った古い複製がないかシーンを再走査します"), GUILayout.Width(180f)))
                {
                    RescanOrphans();
                }
            }
            if (_orphanWarnings != null && _orphanWarnings.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "元アバターのリネームで取り残された可能性のある複製があります(自動削除はしません。不要なら手動で削除してください):\n・"
                    + string.Join("\n・", _orphanWarnings.ToArray()),
                    MessageType.Warning);
            }

            // 実行オプション(プレファブ保存・AAO付与)。共有設定なのでどちらの対象にも効く。
            EditorGUILayout.LabelField("実行オプション", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                bool savePrefab = EditorGUILayout.ToggleLeft(
                    new GUIContent("生成した複製をプレファブとして保存する",
                        "PC『_Opt.prefab』/ Quest『_Quest.prefab』を安定パスへ上書き保存します(非破壊: 元アバターは無改変)"),
                    _settings.savePrefab);
                bool ensureTao = EditorGUILayout.ToggleLeft(
                    new GUIContent("AAO(Trace and Optimize)を複製へ付与する",
                        "無ければ複製へ追加し、ビルド時にメッシュ/スロット統合・未使用ボーン削減を有効にします(AAO=Avatar Optimizer。別途導入する無料の最適化ツール)"),
                    _settings.ensureTraceAndOptimize);
                if (savePrefab != _settings.savePrefab || ensureTao != _settings.ensureTraceAndOptimize)
                {
                    _settings.savePrefab = savePrefab;
                    _settings.ensureTraceAndOptimize = ensureTao;
                    Save();
                }
            }

            EditorGUILayout.Space(2f);

            // プリフライト「この実行で行われること」
            EditorGUILayout.LabelField("この実行で行われること", EditorStyles.boldLabel);
            var preflight = AvatarStudioExecution.BuildPreflight(_settings);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                foreach (string line in preflight)
                {
                    EditorGUILayout.LabelField("・" + line, AvatarStudioUI.WrapLabel);
                }
            }

            using (new EditorGUI.DisabledScope(!_settings.targetPC && !_settings.targetQuest))
            {
                if (GUILayout.Button(new GUIContent("実行(選択した対象へ生成)"), GUILayout.Height(30f)))
                {
                    RunAll();
                }
            }

            EditorGUILayout.LabelField(
                "実行しても元アバターは変更されません。何度でも再実行でき、前回の複製は自動で作り直されます。",
                AvatarStudioUI.MiniWrapLabel);

            // 直近の実行結果(レポート + 前後比較テーブル + 変換結果チェックのサムネイル)。
            AvatarStudioExecution.DrawResult(_lastRun, this, ref _pcReportScroll, ref _questReportScroll);

            // アップロード手順ガイド(PC/Quest 両対応)。RunAll 後に目立つよう常設。実行前でも生成済み複製があれば使える。
            AvatarStudioUploadGuide.Draw(_avatar, _lastRun);
        }

        private void RunAll()
        {
            Save(); // 最新の設定で実行する。

            // 実行前確認(プリフライト要約 + Androidビルドターゲットの注意をダイアログに提示)。
            if (!AvatarStudioExecution.ConfirmRun(_settings)) return;

            _lastRun = AvatarStudioExecution.RunAll(_avatar, _settings);
            _cache.Bump();   // 生成でシーンが変わったためプレビューを次回無効化。
            _diag = null;    // 生成物確認のため再診断を促す。
            RescanOrphans();
            Repaint();
        }

        // ==============================================================
        // ⑧ ヘルプ / はじめての方へ
        // ==============================================================

        private void DrawHelpSection()
        {
            // (a) ツール概要(3行)
            EditorGUILayout.LabelField("かんたんな説明", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "・元アバターは壊さず、軽量なPC用『_Opt』とQuest/iOS用『_Quest』の複製を自動で作るツールです。\n"
                    + "・診断で今の重さを見て、テクスチャ・メッシュ・マテリアル・PhysBone・ポリゴンを減らして重さを下げます。\n"
                    + "・アップロードするのは生成された複製です。何度でも作り直せます(複製は増え続けません)。",
                    AvatarStudioUI.WrapLabel);
            }

            // (b) 軽くなる仕組み(処理 → 効果。ビルド時に効くもの・要AAOのものは明記)
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("軽くなる仕組み(何をすると何が減るか)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "・常時表示(トグル固定): 表示アニメを常時ONに固定 → AAO(Avatar Optimizer。別途導入する無料の最適化ツール)がビルド時に同一メッシュへ結合でき、SkinnedMesh数が減る(要AAO)\n"
                    + "・非表示除去(削除): 使わない衣装をEditorOnly化して除去 → ビルドから外れSkinnedMesh数が減る(AAO不要)\n"
                    + "・SkinnedMesh統合: 顔(口パク・まばたき)以外を1つにまとめる → SkinnedMesh数が減る(Questの上限2に対応。結合はビルド時にAAOが実施)\n"
                    + "・アトラス統合: 複数マテリアルのテクスチャを1枚にまとめる → マテリアルスロット数が減る(同一メッシュ内は変換時、メッシュをまたぐ統合はビルド時にAAOが実施)\n"
                    + "・テクスチャ縮小: 解像度を下げる → テクスチャメモリ(VRAM)とダウンロードサイズが減る\n"
                    + "・ポリゴン削減: 頂点を間引く(表情のブレンドシェイプとUVは保持) → 三角数が減る\n"
                    + "・PhysBone整理: 揺れもの設定を選別・マージ → PhysBoneコンポーネント数が減る\n"
                    + "・透過処理: Questには半透明シェーダーが無いため、乗算/加算のParticlesシェーダーで見た目を近似(ランク削減ではなくQuest対応のための処理)",
                    AvatarStudioUI.WrapLabel);
            }

            // (b') AAO(Avatar Optimizer)との連携。統合・削除の実体はビルド(NDMF)時にAAOが担当する分業設計。
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("AAO(Avatar Optimizer)との連携", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "・本ツールは anatawa12 氏の AAO(Avatar Optimizer。別途導入する無料の最適化ツール)と分業し、生成した複製にのみ Trace and Optimize / Merge Skinned Mesh / Remove Mesh By BlendShape を自動で追加・設定します(元アバターには追加しません)。\n"
                    + "・SkinnedMesh統合と隠れメッシュ削減の実体はアップロード/Play時のビルド(NDMF=アップロード/Play時に自動で走る最適化の仕組み)で実行されるため、生成直後の複製やエディタ上の数値には反映されません。\n"
                    + "・AAO未導入でも本ツールは動作し、該当機能はスキップして導入案内を表示します。感謝: AAO(MIT License / anatawa12 氏)。",
                    AvatarStudioUI.WrapLabel);
                if (GUILayout.Button(new GUIContent("AAOの公式ドキュメントを開く",
                    "AAO(Avatar Optimizer)の公式ドキュメントを開きます: " + AvatarStudioUI.AAODocsUrl)))
                {
                    Application.OpenURL(AvatarStudioUI.AAODocsUrl);
                }
            }

            // Quest/iOS版の割り切り(短く)
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Quest/iOS版の割り切り", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "・半透明・アウトライン・ファー・屈折・マットキャップ等は完全再現できません(質感は簡略化されます)。\n"
                    + "・透過の多いデザインは『乗算/加算で近似』か『非表示』に割り切ります(⑥マテリアルの各行で選べます)。\n"
                    + "・目標ランクを厳しくするほど削るメッシュ・機能が増えます(見た目とのトレードオフ)。",
                    AvatarStudioUI.WrapLabel);
            }

            // (c) 迷ったら(3ステップ)
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("迷ったら(3ステップ)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "1. プリセットを押す(Questだけなら『雑にQuest対応』、PCとQuest両方なら『フル両対応』)。\n"
                    + "2. ⑦ 実行で複製を生成する。\n"
                    + "3. 生成された複製を VRChat SDK の Build & Test で確認する(最終的なランクと見た目はSDKで確認)。",
                    AvatarStudioUI.WrapLabel);
            }

            // 実測レポートの案内(見積りではなく、ビルド/Play時の最終複製を実測する仕組み)。
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("実測レポートとは", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "生成した _Opt / _Quest 複製を ▶️(Play)するか、VRChat SDK で Build & Test / Upload すると、"
                    + "MA・AAO・lilToon 等の適用後・EditorOnly 除去後の“最終的な複製そのもの”を SDK と同じ計算で実測し、"
                    + "総合/項目別ランク・目標との比較・元アバターとの差・実測ビルドサイズ(Questは10MB判定)を「実測レポート」に表示します。"
                    + "▶️(Play)やビルドのたびに自動で開きます(この動作はレポート上部のトグルで切り替えられます)。",
                    AvatarStudioUI.WrapLabel);
            }

            EditorGUILayout.Space(4f);
            DrawRankThresholdTable();

            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(
                "準備の注意: ポリゴン削減は Meshia 連携(ビルド時に適用)で行えますが、大幅にポリゴンを減らす場合は、"
                + "先に外部ツール(Blender等)でメッシュを整えておくと仕上がりが安定します。Meshia は最後の微調整に使うのが推奨です。",
                MessageType.Info);

            // (e) 不具合報告 / Open β。X(Twitter) DM または メールで受付。
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("不具合報告・要望(Open β)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "本ツールはオープンベータ(Open β)として公開中です。不具合報告・要望は X(Twitter) の DM または メール へお願いします。",
                    AvatarStudioUI.WrapLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Xで報告(DM)",
                        "X(Twitter) を開きます: " + AvatarStudioUI.BugReportXUrl)))
                    {
                        Application.OpenURL(AvatarStudioUI.BugReportXUrl);
                    }
                    if (GUILayout.Button(new GUIContent("メールで報告",
                        "メール作成を開きます: " + AvatarStudioUI.BugReportMailUrl)))
                    {
                        Application.OpenURL(AvatarStudioUI.BugReportMailUrl);
                    }
                }
            }
        }

        /// <summary>4段階ランクの主要項目しきい値の参照表(VRChat の代表的な上限。読み取り専用の目安)。</summary>
        private void DrawRankThresholdTable()
        {
            EditorGUILayout.LabelField("目標ランクの目安(主要項目のしきい値)", EditorStyles.boldLabel);

            // {項目, Excellent, Good, Medium, Poor} の順。PC/Quest で異なる代表値。
            string[][] pcRows =
            {
                new[] { "三角数(PC)", "32,000", "70,000", "70,000", "70,000" },
                new[] { "スキンメッシュ数(PC)", "1", "2", "8", "16" },
                new[] { "マテリアルスロット(PC)", "4", "8", "16", "32" },
                new[] { "PhysBoneコンポーネント(PC)", "4", "8", "16", "32" },
            };
            string[][] questRows =
            {
                new[] { "三角数(Quest)", "7,500", "10,000", "15,000", "20,000" },
                new[] { "スキンメッシュ数(Quest)", "1", "1", "2", "2" },
                new[] { "マテリアルスロット(Quest)", "1", "1", "2", "4" },
                new[] { "PhysBoneコンポーネント(Quest)", "0", "4", "6", "8" },
                new[] { "テクスチャメモリ(Quest)", "10MB", "18MB", "25MB", "40MB" },
            };

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                DrawRankTableHeader();
                foreach (string[] r in pcRows) DrawRankTableRow(r);
                EditorGUILayout.Space(2f);
                foreach (string[] r in questRows) DrawRankTableRow(r);
            }
            EditorGUILayout.LabelField("※ 代表値の目安です。実際の判定は VRChat SDK の診断結果に従ってください。Questのアップロード可否は圧縮後10MB・展開後40MBの両上限で決まり、ランクでは決まりません。Very Poor は既定でフォールバック表示になります。",
                AvatarStudioUI.MiniWrapLabel);
        }

        private static void DrawRankTableHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.Width(200f));
                EditorGUILayout.LabelField("Excellent", EditorStyles.miniBoldLabel, GUILayout.Width(72f));
                EditorGUILayout.LabelField("Good", EditorStyles.miniBoldLabel, GUILayout.Width(72f));
                EditorGUILayout.LabelField("Medium", EditorStyles.miniBoldLabel, GUILayout.Width(72f));
                EditorGUILayout.LabelField("Poor", EditorStyles.miniBoldLabel, GUILayout.Width(72f));
                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawRankTableRow(string[] cells)
        {
            if (cells == null || cells.Length < 5) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(cells[0], EditorStyles.miniLabel, GUILayout.Width(200f));
                EditorGUILayout.LabelField(cells[1], EditorStyles.miniLabel, GUILayout.Width(72f));
                EditorGUILayout.LabelField(cells[2], EditorStyles.miniLabel, GUILayout.Width(72f));
                EditorGUILayout.LabelField(cells[3], EditorStyles.miniLabel, GUILayout.Width(72f));
                EditorGUILayout.LabelField(cells[4], EditorStyles.miniLabel, GUILayout.Width(72f));
                GUILayout.FlexibleSpace();
            }
        }

        // ==============================================================
        // ステップ描画ヘルパー(GUILayoutの入れ子を必ず釣り合わせる)
        // ==============================================================

        /// <summary>番号付きの折りたたみステップ。開いていれば box 内で body を描画する。body の例外は捕捉する。</summary>
        private void Step(int number, string title, string foldKey, bool defaultOpen, Action body)
        {
            bool open = AvatarStudioUI.Fold(foldKey, AvatarStudioUI.Circled(number) + " " + title, defaultOpen);
            if (!open) return;

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                try
                {
                    body();
                }
                catch (UnityEngine.ExitGUIException)
                {
                    // IMGUIの正当なGUI中断シグナル(オブジェクトピッカー等)。エラー扱いせず再送出する。
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    EditorGUILayout.HelpBox(
                        "この項目の描画中にエラーが発生しました(Consoleを確認してください): " + ex.Message,
                        MessageType.Error);
                }
            }
            EditorGUILayout.Space(2f);
        }
    }
}
#endif
