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

        // 統合メニュー: 本体(100) → PC/Quest エイリアス(101/102) → 旧ツールは submenu(1000番台)で最後。
        [MenuItem("RARA/アバター軽量化・Quest・iOS対応ツール", priority = 100)]
        public static void Open()
        {
            var window = GetWindow<AvatarStudioWindow>();
            window.titleContent = new GUIContent("RARA アバター軽量化・Quest/iOS対応ツール");
            window.minSize = new Vector2(560f, 640f);
            window.Show();
        }

        /// <summary>PC軽量化エイリアス。統合ウィンドウをPC対象のみで開く(旧「RARA/PC軽量化ツール」の入口を継承)。</summary>
        [MenuItem("RARA/PC軽量化ツール", priority = 101)]
        public static void OpenPcAlias()
        {
            Open(null, true, false);
        }

        /// <summary>Quest対応エイリアス。統合ウィンドウをQuest対象のみで開く(旧「RARA/Quest対応コンバーター」の入口を継承)。</summary>
        [MenuItem("RARA/Quest対応コンバーター", priority = 102)]
        public static void OpenQuestAlias()
        {
            Open(null, false, true);
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

                DrawSummaryLine();
                DrawTargetChips();
                DrawPresets();

                EditorGUILayout.Space(6f);

                // プレビューパネルへ渡すエンジン設定を現在の設定から毎フレーム作り直す(実装者Bの要求どおり)。
                GameObject root = _avatar.gameObject;
                PCOptimizeSettings pc = AvatarStudioMapping.BuildPCOptimizeSettings(_settings);
                QuestConvertSettings quest = AvatarStudioMapping.BuildQuestConvertSettings(_settings);

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
                if (GUILayout.Button(new GUIContent("雑にQuest対応", "Questのみ / 目標Poor / 透過は自動再現(ポリゴン削減は既定オフ・必要なときにパネルで有効化)"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.RoughQuest);
                }
                if (GUILayout.Button(new GUIContent("PCをPoorに", "PCのみ / 目標Poor"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.PcPoor);
                }
                if (GUILayout.Button(new GUIContent("PCをGoodに", "PCのみ / 目標Good"), GUILayout.Height(24f)))
                {
                    ApplyPreset(Preset.PcGood);
                }
                if (GUILayout.Button(new GUIContent("フル両対応(おすすめ)", "PC(Good)とQuest(Poor)の両方"), GUILayout.Height(24f)))
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
                    // ポリゴン削減は既定オフ。プリセットでは有効化せず、目標ランクの目安三角形数のみ用意する
                    // (超過時はポリゴンパネルの琥珀ヒントから明示的に有効化してもらう)。
                    _settings.questEnableDecimation = false;
                    _settings.questDecimationTargetTriangles = QuestRankToTriangles(QuestTargetRank.Poor);
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
                    // ポリゴン削減は既定オフ。プリセットでは有効化しない(超過時はパネルの琥珀ヒントから有効化)。
                    _settings.questEnableDecimation = false;
                    _settings.questDecimationTargetTriangles = QuestRankToTriangles(QuestTargetRank.Poor);
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
            // 初回(または対象変更後)は自動で計測する。診断ステップは既定で開いているため、
            // ボタン描画より前に計測して同一フレーム内のラベルを一貫させる。
            if (_diag == null)
            {
                _diag = AvatarStudioDiagnostics.Analyze(_avatar, _settings.targetPC, _settings.targetQuest, quest);
            }

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
                    return "AAO隠面メッシュ削除・不要衣装をQuest除外・ポリゴン削減で目標へ配分";
                case "スキンメッシュ数":
                case "メッシュ数":
                    return "構成整理で『表示で固定』またはSkinnedMesh統合";
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
            changed |= AvatarStudioPreviewPanels.DrawTogglePanel(root, _settings, _cache);
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
                        "無ければ複製へ追加し、ビルド時にメッシュ/スロット統合・未使用ボーン削減を有効にします"),
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
            EditorGUILayout.LabelField("このツールでできること / できないこと / 諦めること を先に把握しておくと、設定で迷いにくくなります。",
                AvatarStudioUI.MiniWrapLabel);

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("できること", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・PC(Windows): テクスチャ縮小・マテリアルのアトラス統合・トグル固定・SkinnedMesh統合・PhysBone整理・AAO付与でランクを改善\n"
                    + "・Quest/iOS: シェーダーのMobile変換(質感をテクスチャへベイク)・影ランプ生成・エミッション変換・半透明の自動再現\n"
                    + "・Quest/iOS: 隠面メッシュ削減・ポリゴン削減・不要オブジェクトのQuest除外・表情デカール(チーク/涙/ハイライト)の再現/非表示\n"
                    + "・元アバターは非破壊。PC用『_Opt』とQuest用『_Quest』の複製を生成(繰り返し再生成しても複製は蓄積しない)",
                    AvatarStudioUI.WrapLabel);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("できないこと(Quest/iOS)", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・メッシュの半透明(Quest仕様: 透過はParticles系のみ)・カットアウト(アルファ抜き)\n"
                    + "・アウトライン・ファー・ラメ・屈折などの特殊効果\n"
                    + "・リムライト/マットキャップ等の質感の完全再現(リムは変換設定で近似のみ)\n"
                    + "・ダウンロードサイズ10MB超の回避・Very Poor アバターのアップロード",
                    AvatarStudioUI.WrapLabel);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("諦めること(割り切り)", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・Quest版はPC版と完全に同じ見た目にはならない(質感の簡略化は前提)\n"
                    + "・透過を多用したデザインは不透明化/非表示のいずれかへ割り切る\n"
                    + "・目標ランクを厳しくするほど、削るメッシュ・機能は増える(見た目とのトレードオフ)",
                    AvatarStudioUI.WrapLabel);
            }

            EditorGUILayout.Space(4f);
            DrawRankThresholdTable();

            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(
                "準備の注意: このツールにもQuest向けのポリゴン削減はありますが、大幅にポリゴンを減らす場合は、"
                + "先に外部ツール(Blender等)でメッシュを整えておくと仕上がりが安定します。ポリゴン削減は最後の微調整に使うのがおすすめです。",
                MessageType.Info);
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
            EditorGUILayout.LabelField("※ 代表値の目安です。実際の判定は VRChat SDK の診断結果に従ってください。Quest は Very Poor だとアップロードできません。",
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
