// RARA アバター軽量化・Quest/iOS対応ツール - 実行(RunAll)とレポート描画(実装者C所有)
// 1回の「実行」で、選択ターゲット(PC / Quest / 両方)に対して旧2ツールのエンジンを順に呼び出す。
//
// 反復生成の安全性(検証済みハザード対策):
//   H1 リネームガード: 実行前にシーンを走査し、元アバターのリネームで取り残された
//      "{名前}_Opt" / "{名前}_Quest" 複製を名前付きで警告する。
//   H2 元アバターの非アクティブ化を避ける + 各パイプライン後に元アバターを再アクティブ化する。
//      (Quest変換設定は Mapping 側で deactivateOriginal=false を強制する。ここでも保険で再アクティブ化。)
//   H3 Quest分岐は常に QuestConvert モード(Mapping が強制。ここでは分岐のみ)。
//   H4 生成物の「未使用(orphaned)」件数はエンジンのレポートに含まれるため、レポートをそのまま表示して可視化する。
//   H5 AAO(Trace and Optimize)系はビルド時適用のため、実行直後の数値は控えめに出る点を後注で明示する。
//   H6 Quest複製のプレファブ保存はスタジオ側で追加する(QuestConverterのGenerated配下・安定パス上書き)。
//
// 冪等性: PCOptimizer / AvatarQuestConverter はそれぞれ実行のたびに前回の同名複製を掃除して作り直し、
//         生成アセットは安定パスへGUID保持のまま上書きする。よって RunAll を何度呼んでも複製は蓄積せず、
//         元プレファブの内容も一切破壊しない。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using RARA.QuestConverter; // ConversionReport / QuestConvertSettings / QuestConverterUtility / AvatarQuestConverter
using RARA.PCOptimizer;    // PCOptimizeSettings / PCOptimizer

namespace RARA.AvatarStudio
{
    /// <summary>1回の実行の結果(生成物・レポート・警告)。ウィンドウのレポート描画に渡す。</summary>
    internal sealed class AvatarStudioRunResult
    {
        public bool ranPC;
        public bool ranQuest;

        public GameObject pcClone;              // 生成された _Opt 複製(失敗時 null)
        public GameObject questClone;           // 生成された _Quest 複製(失敗時 null)
        public string questPrefabPath;          // H6: 保存したQuest複製プレファブのパス(未保存なら null)

        public ConversionReport pcReport;
        public ConversionReport questReport;

        // 実行前後の診断(前後比較テーブル用)。before は元アバター、after は各生成複製を再診断した結果。
        public StudioDiagnosis beforeDiag;
        public StudioDiagnosis pcAfterDiag;
        public StudioDiagnosis questAfterDiag;

        // 変換結果チェック(Quest): 元マテリアル ← → 変換後マテリアルのペア(サムネイル比較用)。遅延構築。
        public List<AvatarStudioResultPair> questResultPairs;

        public readonly List<string> renameWarnings = new List<string>(); // H1
        public readonly List<string> preflight = new List<string>();      // この実行で行われること

        public bool AnyErrors
        {
            get
            {
                return (pcReport != null && pcReport.HasErrors) || (questReport != null && questReport.HasErrors);
            }
        }

        public bool AnyRan { get { return ranPC || ranQuest; } }
    }

    /// <summary>変換結果チェック(Quest)の1行: 元マテリアルと変換後マテリアルのペア(旧QuestConverterのConvertedPairRow移植)。</summary>
    internal sealed class AvatarStudioResultPair
    {
        public Material source;             // 元マテリアル(特定できない場合はnull)
        public Material converted;          // 変換後マテリアル(出力フォルダ配下)
        public string note;                 // レポートから抽出した一言メモ(該当なしはnull)
        public Texture2D sourcePreview;     // AssetPreviewのキャッシュ(非同期ロード完了後に保持)
        public Texture2D convertedPreview;  // 同上
    }

    /// <summary>スタジオの実行ロジック。ウィンドウ(AvatarStudioWindow)から呼ばれる。</summary>
    internal static class AvatarStudioExecution
    {
        // ==============================================================
        // プリフライト(この実行で行われること)
        // ==============================================================

        /// <summary>実行前サマリー「この実行で行われること」の箇条書きを組み立てる。設定は変更しない。</summary>
        internal static List<string> BuildPreflight(AvatarStudioSettings s)
        {
            var lines = new List<string>();
            if (s == null)
            {
                lines.Add("設定が読み込まれていません。");
                return lines;
            }

            if (!s.targetPC && !s.targetQuest)
            {
                lines.Add("対象が選ばれていません。上部のチップで PC か Quest(または両方)を選んでください。");
                return lines;
            }

            if (s.targetPC)
            {
                lines.Add("PC(Windows): 元アバターを非破壊で複製し『{名前}_Opt』を生成します(目標ランク: "
                    + s.pcTargetRank + ")。マテリアル複製・テクスチャ/アトラス整理・トグル/SkinnedMesh統合・PhysBone整理・AAO付与を行い、プレファブ(_Opt.prefab)も保存します。");
            }

            if (s.targetQuest)
            {
                string decim = s.questEnableMeshiaSimplification
                    ? "ポリゴン削減あり(Meshia連携・目標 " + s.questMeshiaTargetTriangles + " 三角形・ビルド時適用)"
                    : "ポリゴン削減なし";
                lines.Add("Quest/iOS(Android): 元アバターを非破壊で複製し『{名前}_Quest』を生成します(目標ランク: "
                    + (QuestTargetRank)s.questGoalRank + ")。シェーダー/テクスチャのQuest対応変換・透過処理(" + TransparentLabel(s.transparentHandling)
                    + ")・" + decim + "・トグル/SkinnedMesh統合・PhysBone整理を行い、Quest複製プレファブも保存します。");
            }

            if (s.targetQuest && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                lines.Add("注意: 現在のビルドターゲットは Android ではありません(" + EditorUserBuildSettings.activeBuildTarget
                    + ")。Quest/iOS 複製の生成自体は可能ですが、テクスチャ圧縮やサイズ推定を正確に確認・アップロードするには "
                    + "File > Build Settings で Android へ切り替えてください。");
            }

            lines.Add("元アバターは一切変更しません。前回生成した『_Opt』『_Quest』複製は自動で作り直され、複製が蓄積することはありません。");
            lines.Add("AAO(Trace and Optimize)によるメッシュ/スロット統合や非表示メッシュ削減はビルド時に反映されるため、実行直後のシーン上の数値は最終結果より控えめに出ます。");
            return lines;
        }

        // ==============================================================
        // 実行前確認ダイアログ(プリフライト要約を提示して最終確認)
        // ==============================================================

        /// <summary>
        /// 実行前に「この実行で行われること」をダイアログで提示し、続行の可否を尋ねる。
        /// OK で true。設定不備(対象未選択)は理由を出して false を返す。
        /// </summary>
        internal static bool ConfirmRun(AvatarStudioSettings s)
        {
            if (s == null)
            {
                EditorUtility.DisplayDialog("実行できません", "設定が読み込まれていません。", "OK");
                return false;
            }
            if (!s.targetPC && !s.targetQuest)
            {
                EditorUtility.DisplayDialog("実行できません",
                    "対象が選ばれていません。上部のチップで PC か Quest(または両方)を選んでください。", "OK");
                return false;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("以下の内容で複製を生成します。元アバターは変更しません。");
            sb.AppendLine();
            foreach (string line in BuildPreflight(s))
            {
                sb.Append("・").AppendLine(line);
            }
            sb.AppendLine();
            sb.Append("続行しますか?");

            return EditorUtility.DisplayDialog("実行の確認", sb.ToString(), "実行する", "キャンセル");
        }

        private static string TransparentLabel(TransparentHandling t)
        {
            switch (t)
            {
                case TransparentHandling.Emulate: return "自動再現";
                case TransparentHandling.Hide: return "非表示";
                case TransparentHandling.Opaque: return "不透明化";
                default: return t.ToString();
            }
        }

        // ==============================================================
        // H1: リネームで取り残された生成複製の検出(警告のみ・削除しない)
        // ==============================================================

        /// <summary>
        /// シーン内(非アクティブ含む)の VRCAvatarDescriptor を走査し、名前が "{基底}_Opt" / "{基底}_Quest"
        /// なのに対応する元アバター "{基底}" がシーンに見当たらない複製を、リネームで取り残された孤児として
        /// 名前付きで列挙する。現在の対象アバター自身とその複製は対象外。
        /// </summary>
        internal static List<string> ScanOrphanClones(Scene scene, GameObject currentAvatar)
        {
            var warnings = new List<string>();
            if (!scene.IsValid()) return warnings;

            // シーン内の全 VRCAvatarDescriptor 名を集める(元アバター候補の集合)。
            var descriptorNames = new HashSet<string>(StringComparer.Ordinal);
            var descriptors = new List<VRCAvatarDescriptor>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (VRCAvatarDescriptor d in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                {
                    if (d == null) continue;
                    descriptors.Add(d);
                    descriptorNames.Add(d.gameObject.name);
                }
            }

            string currentName = currentAvatar != null ? currentAvatar.name : null;
            string[] suffixes = { "_Opt", "_Quest" };

            foreach (VRCAvatarDescriptor d in descriptors)
            {
                GameObject go = d.gameObject;
                if (go == currentAvatar) continue;

                string trimmed = StripDuplicateSuffix(go.name);
                foreach (string suffix in suffixes)
                {
                    if (!trimmed.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    string baseName = trimmed.Substring(0, trimmed.Length - suffix.Length);
                    if (baseName.Length == 0) continue;

                    // 対応する元アバターがシーンに居れば孤児ではない(現在の対象アバターも元とみなす)。
                    bool hasSource = descriptorNames.Contains(baseName)
                        || string.Equals(baseName, currentName, StringComparison.Ordinal);
                    if (!hasSource)
                    {
                        warnings.Add(go.name);
                    }
                    break;
                }
            }
            return warnings;
        }

        /// <summary>Unityの重複サフィックス " (1)" 等を末尾から取り除く。</summary>
        private static string StripDuplicateSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
            string s = name;
            // 末尾が " (数字)" の間だけ繰り返し除去する。
            while (s.Length > 3 && s[s.Length - 1] == ')')
            {
                int open = s.LastIndexOf(" (", StringComparison.Ordinal);
                if (open < 0) break;
                string inner = s.Substring(open + 2, s.Length - (open + 2) - 1);
                int _;
                if (inner.Length > 0 && int.TryParse(inner, out _))
                {
                    s = s.Substring(0, open);
                }
                else
                {
                    break;
                }
            }
            return s;
        }

        // ==============================================================
        // 実行(RunAll)
        // ==============================================================

        /// <summary>
        /// 選択ターゲットに対して PC軽量化 / Quest変換 を順に実行する。冪等・非破壊。
        /// 例外は各パイプラインごとに捕捉し、片方が失敗しても他方は実行する。
        /// </summary>
        internal static AvatarStudioRunResult RunAll(VRCAvatarDescriptor avatar, AvatarStudioSettings settings)
        {
            var result = new AvatarStudioRunResult();
            if (settings != null)
            {
                result.preflight.AddRange(BuildPreflight(settings));
            }

            if (avatar == null)
            {
                var r = new ConversionReport();
                r.Error("対象アバターが指定されていません。シーン上のアバターを選んでください。");
                result.pcReport = r;
                return result;
            }
            if (settings == null)
            {
                var r = new ConversionReport();
                r.Error("設定が読み込まれていません。");
                result.pcReport = r;
                return result;
            }

            GameObject sourceGo = avatar.gameObject;

            // H1: リネームで取り残された生成複製を警告(削除はしない)。
            result.renameWarnings.AddRange(ScanOrphanClones(sourceGo.scene, sourceGo));

            // H2: 実行前に元アバターを確実にアクティブへ(前回の変換で非アクティブのままだと計測がずれるため)。
            EnsureActive(sourceGo);

            if (!settings.targetPC && !settings.targetQuest)
            {
                var r = new ConversionReport();
                r.Error("対象(PC / Quest)が選ばれていません。");
                result.pcReport = r;
                return result;
            }

            // 前後比較テーブル用: 実行前(元アバター)の診断を取得。元アバターは非破壊のためこのまま基準に使える。
            try
            {
                QuestConvertSettings beforeQuest = AvatarStudioMapping.BuildQuestConvertSettings(settings);
                result.beforeDiag = AvatarStudioDiagnostics.Analyze(avatar, settings.targetPC, settings.targetQuest, beforeQuest);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            // --- PC軽量化 ---
            if (settings.targetPC)
            {
                result.ranPC = true;
                var pcReport = new ConversionReport();
                result.pcReport = pcReport;
                try
                {
                    PCOptimizeSettings pcSettings = AvatarStudioMapping.BuildPCOptimizeSettings(settings);
                    // 完全修飾: using RARA.PCOptimizer があるため素の "PCOptimizer" は名前空間に解決される。
                    result.pcClone = RARA.PCOptimizer.PCOptimizer.Optimize(sourceGo, pcSettings, pcReport);

                    // 前後比較テーブル用: 生成した _Opt 複製をPC基準で再診断する。
                    if (result.pcClone != null)
                    {
                        var pcDesc = result.pcClone.GetComponent<VRCAvatarDescriptor>();
                        if (pcDesc != null)
                        {
                            try { result.pcAfterDiag = AvatarStudioDiagnostics.Analyze(pcDesc, true, false, null); }
                            catch (Exception dex) { Debug.LogException(dex); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    pcReport.Error("PC軽量化中に予期しない例外が発生しました: " + ex.Message);
                }
                finally
                {
                    EnsureActive(sourceGo); // H2: パイプライン後に元アバターを再アクティブ化。
                }
            }

            // --- Quest/iOS変換 ---
            if (settings.targetQuest)
            {
                result.ranQuest = true;
                var questReport = new ConversionReport();
                result.questReport = questReport;
                try
                {
                    QuestConvertSettings questSettings = AvatarStudioMapping.BuildQuestConvertSettings(settings);
                    result.questClone = AvatarQuestConverter.Convert(avatar, questSettings, questReport);

                    // H6: Quest複製のプレファブをスタジオ側で保存(安定パス上書き)。
                    if (result.questClone != null)
                    {
                        result.questPrefabPath = SaveQuestPrefab(result.questClone, sourceGo.name, questSettings, questReport);

                        // 前後比較テーブル用: 生成した _Quest 複製をQuest基準で再診断する。
                        var qDesc = result.questClone.GetComponent<VRCAvatarDescriptor>();
                        if (qDesc != null)
                        {
                            try { result.questAfterDiag = AvatarStudioDiagnostics.Analyze(qDesc, false, true, questSettings); }
                            catch (Exception dex) { Debug.LogException(dex); }
                        }

                        // 変換結果チェック用: 元マテリアル ← → 変換後マテリアルのペアを構築(サムネイル比較)。
                        try { result.questResultPairs = BuildResultPairs(result.questClone, sourceGo, questReport, questSettings); }
                        catch (Exception pex) { Debug.LogException(pex); }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    questReport.Error("Quest変換中に予期しない例外が発生しました: " + ex.Message);
                }
                finally
                {
                    EnsureActive(sourceGo); // H2: 元アバターを再アクティブ化(deactivateOriginal=false のはずだが保険)。
                }
            }

            return result;
        }

        /// <summary>GameObjectが非アクティブなら Undo 付きでアクティブへ戻す。</summary>
        private static void EnsureActive(GameObject go)
        {
            if (go == null || go.activeSelf) return;
            Undo.RecordObject(go, "元アバターを再アクティブ化");
            go.SetActive(true);
        }

        // ==============================================================
        // H6: Quest複製プレファブの保存(QuestConverter の Generated 配下・安定パス上書き)
        // ==============================================================

        /// <summary>
        /// Quest複製をプレファブとして保存する。保存先は Convert が生成に使うフォルダと同じ
        /// "{outputFolder}/{元名}/{複製名}.prefab"。既存があれば同一パス上書き(反復生成でファイルが増えない)。
        /// </summary>
        private static string SaveQuestPrefab(GameObject clone, string sourceName, QuestConvertSettings questSettings, ConversionReport report)
        {
            try
            {
                string outputRoot = (questSettings != null && !string.IsNullOrEmpty(questSettings.outputFolder))
                    ? questSettings.outputFolder
                    : "Assets/RARA/QuestConverter/Generated";
                outputRoot = outputRoot.Replace('\\', '/').TrimEnd('/');

                string avatarFolder = outputRoot + "/" + QuestConverterUtility.SanitizeAssetName(sourceName);
                QuestConverterUtility.EnsureFolder(avatarFolder);

                string prefabPath = avatarFolder + "/" + QuestConverterUtility.SanitizeAssetName(clone.name) + ".prefab";
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(clone, prefabPath);
                if (saved != null)
                {
                    report.Info("Quest複製のプレファブを保存しました(非破壊・上書き): " + prefabPath);
                    return prefabPath;
                }

                report.Warn("Quest複製のプレファブ保存に失敗しました: " + prefabPath);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                report.Warn("Quest複製のプレファブ保存で例外が発生しました: " + ex.Message);
                return null;
            }
        }

        // ==============================================================
        // レポート描画(実行結果 + 前後比較はレポート本文の before→after 行に含まれる)
        // ==============================================================

        /// <summary>直近の実行結果(プリフライト・警告・レポート・前後比較・変換結果チェック)を描画する。GUILayoutの入れ子は必ず釣り合う。</summary>
        internal static void DrawResult(AvatarStudioRunResult result, EditorWindow window, ref Vector2 pcScroll, ref Vector2 questScroll)
        {
            if (result == null || !result.AnyRan) return;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("実行結果", EditorStyles.boldLabel);

            // 総合サマリー。
            string summary = result.AnyErrors
                ? "エラーがあります。下のレポートを確認してください。"
                : "完了しました。生成された複製をアップロードしてください(元アバターは無改変です)。";
            EditorGUILayout.HelpBox(summary, result.AnyErrors ? MessageType.Error : MessageType.Info);

            // 実測レポートへの導線(前回ビルド実測サイズ + レポートを開く)。
            DrawMeasureReportLink(result);

            // H1: リネーム孤児の警告。
            if (result.renameWarnings.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "元アバターのリネームで取り残された可能性のある複製があります(自動削除はしません。不要なら手動で削除してください):\n・"
                    + string.Join("\n・", result.renameWarnings.ToArray()),
                    MessageType.Warning);
            }

            if (result.ranPC)
            {
                DrawPipelineBlock("PC(_Opt)", result.pcClone, null, result.pcReport, ref pcScroll);
            }
            if (result.ranQuest)
            {
                DrawPipelineBlock("Quest/iOS(_Quest)", result.questClone, result.questPrefabPath, result.questReport, ref questScroll);
            }

            // 変換前後の比較テーブル(PC / Quest 列。目標との突き合わせ)。
            DrawBeforeAfterTables(result);

            // 変換結果チェック(Quest): 元マテリアル ← → 変換後マテリアルのサムネイル比較。
            DrawResultCheckGrid(result, window);

            // 見た目トラブル時の二段構え案内(恒久=⑥マテリアルのQuestパネルで変換方法を指定して再生成 /
            // 応急=生成された _Quest 複製のシェーダーを直変更)。Quest を生成したときのみ表示。result は描画中に
            // 不変なので、条件付きでも同一OnGUIの Layout/Repaint でコントロール数は食い違わない(静的テキスト)。
            if (result.ranQuest)
            {
                EditorGUILayout.HelpBox(
                    "見た目がおかしいときは: 見えるべきものが消えた/消えるべきものが見えている場合は、⑥マテリアルのQuestパネルで" +
                    "該当マテリアルの変換方法を選び直して再生成してください(見せたい→Toon Standard(不透明)や乗算・加算 / 消したい→非表示)。" +
                    "急ぎの場合は生成された _Quest 複製のマテリアルのシェーダーを直接 VRChat/Mobile/Toon Standard 等へ変更しても表示できます" +
                    "(再生成で上書きされるため、恒久対応はパネルでの指定を推奨)。" +
                    "アップロード前に Build & Test で実機相当の見え方を確認してください。",
                    MessageType.Info);
            }

            // H5: AAO のビルド時適用に関する後注。
            EditorGUILayout.HelpBox(
                "注: AAO(Trace and Optimize)によるメッシュ/スロット統合や非表示メッシュ削減はビルド時(アップロード時)に反映されます。"
                + "上のレポートの『適用後』数値はシーン上の暫定値のため、実際のビルド後はさらに改善する場合があります。",
                MessageType.Info);

            // R4: MA Merge Armature を使っている複製では、ボーン数・PhysBoneマージ機会はビルド時の統合後にしか
            // 確定しないため、レポートの表示値が「統合前」の暫定値である旨を後注で明示する。
            if ((result.pcClone != null && MACompatAudit.HasMergeArmature(result.pcClone))
                || (result.questClone != null && MACompatAudit.HasMergeArmature(result.questClone)))
            {
                EditorGUILayout.HelpBox(MACompatAudit.MergeArmatureNote, MessageType.Info);
            }
        }

        /// <summary>
        /// 実測レポートへの導線。生成複製に対応する「前回ビルド実測」サイズ(あれば)を出し、
        /// レポートウィンドウを開くボタンを添える。実測は ▶️/SDKビルド時に AvatarStudioMeasureHook が記録する。
        /// </summary>
        private static void DrawMeasureReportLink(AvatarStudioRunResult result)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("前回ビルド実測", EditorStyles.miniBoldLabel, GUILayout.Width(90f));

                string pcSize = FindLastBuildSizeLabel(result != null ? result.pcClone : null, false);
                string questSize = FindLastBuildSizeLabel(result != null ? result.questClone : null, true);

                if (pcSize == null && questSize == null)
                {
                    EditorGUILayout.LabelField(
                        "未取得(▶️PlayまたはSDKの Build & Test / Upload で実測されます)",
                        AvatarStudioUI.MiniWrapLabel);
                }
                else
                {
                    if (pcSize != null) EditorGUILayout.LabelField(pcSize, EditorStyles.miniLabel, GUILayout.Width(170f));
                    if (questSize != null) EditorGUILayout.LabelField(questSize, EditorStyles.miniLabel, GUILayout.Width(220f));
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("実測レポートを開く",
                    "ビルド/Play時に実測した最終複製のパフォーマンス・実測ビルドサイズを一覧表示します"),
                    GUILayout.Width(140f)))
                {
                    AvatarStudioMeasureReportWindow.Open();
                }
            }
        }

        /// <summary>生成複製名から実測ストアの前回ビルドサイズ行(なければ null)を作る。_Quest は10MB判定も添える。</summary>
        private static string FindLastBuildSizeLabel(GameObject clone, bool isQuest)
        {
            if (clone == null) return null;
            MeasuredAvatar m = AvatarStudioMeasureStore.Find(clone.name);
            if (m == null || m.buildSizeBytes < 0) return null;

            float mb = m.buildSizeBytes / (1024f * 1024f);
            string head = isQuest ? "Quest " : "PC ";
            string body = mb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            if (isQuest) body += mb > 10f ? "(10MB超)" : "(10MB以内)";
            return head + body;
        }

        /// <summary>1パイプラインぶんの結果ブロック(生成物・プレファブ・レポート)を描画する。</summary>
        private static void DrawPipelineBlock(string label, GameObject clone, string prefabPath, ConversionReport report, ref Vector2 scroll)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

                if (clone != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(new GUIContent("生成された複製"), clone, typeof(GameObject), true);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("複製は生成されませんでした(レポートを確認してください)。", MessageType.Warning);
                }

                if (!string.IsNullOrEmpty(prefabPath))
                {
                    EditorGUILayout.LabelField("保存プレファブ", prefabPath, AvatarStudioUI.MiniWrapLabel);
                }

                if (report != null)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("レポートをコピー"), GUILayout.Width(120f)))
                        {
                            EditorGUIUtility.systemCopyBuffer = report.ToText();
                        }
                    }

                    Color prev = GUI.color;
                    using (var sv = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.Height(160f)))
                    {
                        scroll = sv.scrollPosition;
                        if (report.entries.Count == 0)
                        {
                            EditorGUILayout.LabelField("(レポート項目なし)", EditorStyles.miniLabel);
                        }
                        foreach (ConversionReport.Entry entry in report.entries)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUI.color = AvatarStudioUI.SeverityColor(entry.severity);
                                GUILayout.Label(AvatarStudioUI.SeverityGlyph(entry.severity), GUILayout.Width(20f));
                                GUI.color = prev;
                                EditorGUILayout.LabelField(entry.message ?? string.Empty, AvatarStudioUI.WrapLabel);
                            }
                        }
                    }
                    GUI.color = prev;
                }
            }
        }

        // ==============================================================
        // 変換前後の比較テーブル(PC / Quest。生成複製 vs 元アバターの再診断)
        // ==============================================================

        /// <summary>変換前後の診断を PC / Quest それぞれのテーブルで並べる(取得できた対象のみ)。</summary>
        private static void DrawBeforeAfterTables(AvatarStudioRunResult result)
        {
            if (result.beforeDiag == null) return;

            if (result.ranPC && result.pcAfterDiag != null)
            {
                DrawOneTargetBeforeAfter("変換前後の比較(PC / Windows 基準)", result.beforeDiag, result.pcAfterDiag, true);
            }
            if (result.ranQuest && result.questAfterDiag != null)
            {
                DrawOneTargetBeforeAfter("変換前後の比較(Quest / iOS 基準)", result.beforeDiag, result.questAfterDiag, false);
            }
        }

        /// <summary>1ターゲットぶんの前後比較テーブル(項目 / 変換前 / 変換後 / 判定)。before/after は同じ項目定義順で並ぶ。</summary>
        private static void DrawOneTargetBeforeAfter(string title, StudioDiagnosis before, StudioDiagnosis after, bool isPC)
        {
            // after を項目ラベルで引けるよう辞書化(after は片側の対象のみのため行数が before と一致しないことがある)。
            var afterByLabel = new Dictionary<string, StudioMetricRow>(StringComparer.Ordinal);
            foreach (StudioMetricRow r in after.rows)
            {
                if (r != null && !string.IsNullOrEmpty(r.label)) afterByLabel[r.label] = r;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(160f));
                    EditorGUILayout.LabelField("変換前", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                    EditorGUILayout.LabelField("変換後", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                    EditorGUILayout.LabelField("判定", EditorStyles.miniBoldLabel, GUILayout.Width(120f));
                    GUILayout.FlexibleSpace();
                }

                foreach (StudioMetricRow b in before.rows)
                {
                    if (b == null || string.IsNullOrEmpty(b.label)) continue;
                    if (isPC && !b.hasPcStat) continue; // PC列はPC閾値を持つ項目のみ。
                    if (!afterByLabel.TryGetValue(b.label, out StudioMetricRow a)) continue;

                    string beforeText = isPC ? b.pcValueText : b.questValueText;
                    string afterText = isPC ? a.pcValueText : a.questValueText;
                    string afterRating = isPC ? a.pcRating : a.questRating;

                    // 判定: after ランクを色付き表示(Quest上限超は赤)。ランク不明は「-」。
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(b.label, EditorStyles.miniLabel, GUILayout.MinWidth(160f));
                        EditorGUILayout.LabelField(string.IsNullOrEmpty(beforeText) ? "-" : beforeText, EditorStyles.miniLabel, GUILayout.Width(90f));
                        EditorGUILayout.LabelField(string.IsNullOrEmpty(afterText) ? "-" : afterText, EditorStyles.miniLabel, GUILayout.Width(90f));

                        Color prev = GUI.color;
                        bool hardOver = !isPC && a.questOverLimit;
                        GUI.color = hardOver
                            ? AvatarStudioUI.OverLimitColor
                            : AvatarStudioDiagnostics.RatingColor(afterRating);
                        string judge = string.IsNullOrEmpty(afterRating)
                            ? "-"
                            : (hardOver ? "⚠ " : string.Empty) + AvatarStudioDiagnostics.DisplayRating(afterRating);
                        EditorGUILayout.LabelField(judge, EditorStyles.miniBoldLabel, GUILayout.Width(120f));
                        GUI.color = prev;
                        GUILayout.FlexibleSpace();
                    }
                }

                // 総合ランクの前後。
                using (new EditorGUILayout.HorizontalScope())
                {
                    string beforeOverall = isPC ? before.pcOverallRating : before.questOverallRating;
                    string afterOverall = isPC ? after.pcOverallRating : after.questOverallRating;
                    EditorGUILayout.LabelField("総合ランク", EditorStyles.miniBoldLabel, GUILayout.MinWidth(160f));
                    DrawRatingMini(beforeOverall, 90f);
                    DrawRatingMini(afterOverall, 90f);
                    EditorGUILayout.LabelField(string.Empty, GUILayout.Width(120f));
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private static void DrawRatingMini(string rating, float width)
        {
            Color prev = GUI.color;
            GUI.color = AvatarStudioDiagnostics.RatingColor(rating);
            EditorGUILayout.LabelField(AvatarStudioDiagnostics.DisplayRating(rating), EditorStyles.miniBoldLabel, GUILayout.Width(width));
            GUI.color = prev;
        }

        // ==============================================================
        // 変換結果チェック(Quest): 元マテリアル ← → 変換後マテリアルのサムネイル比較
        //   旧 QuestConverterWindowSections の ConvertedPairRow パターンを移植(元ウィンドウは無改変のまま複製)。
        // ==============================================================

        private const int ResultCheckRowMax = 30;

        /// <summary>変換結果チェックのサムネイル比較グリッド。フォルドアウトで開いたときだけ描画する。</summary>
        private static void DrawResultCheckGrid(AvatarStudioRunResult result, EditorWindow window)
        {
            if (!result.ranQuest || result.questResultPairs == null || result.questResultPairs.Count == 0) return;

            EditorGUILayout.Space(4f);
            if (!AvatarStudioUI.Fold("ResultCheck", "変換結果チェック(元 ← → 変換後)", false)) return;

            int shown = Mathf.Min(result.questResultPairs.Count, ResultCheckRowMax);
            EditorGUILayout.LabelField(
                "変換されたマテリアルの見比べ一覧です(" + shown + "/" + result.questResultPairs.Count + " 件)。サムネイルをクリックするとピン表示します。",
                AvatarStudioUI.MiniWrapLabel);
            for (int i = 0; i < shown; i++)
            {
                DrawResultPairRow(result.questResultPairs[i]);
            }
            if (result.questResultPairs.Count > shown)
            {
                EditorGUILayout.LabelField("(残り " + (result.questResultPairs.Count - shown) + " 件は省略。全件はレポートを確認してください)", EditorStyles.miniLabel);
            }

            // AssetPreview は非同期生成のため、ロード中は再描画を要求してサムネイルを差し替える。
            if (window != null && AssetPreview.IsLoadingAssetPreviews()) window.Repaint();
        }

        /// <summary>
        /// 生成アバターのレンダラーから出力フォルダ配下のマテリアルを集め、名前の前方一致({元名}_Quest)で
        /// 元アバターのマテリアルとペアにする。特定できない場合は変換後のみ表示する。
        /// </summary>
        private static List<AvatarStudioResultPair> BuildResultPairs(GameObject questClone, GameObject originalGo, ConversionReport report, QuestConvertSettings questSettings)
        {
            var pairs = new List<AvatarStudioResultPair>();
            if (questClone == null) return pairs;

            string outputRoot = (questSettings != null && !string.IsNullOrEmpty(questSettings.outputFolder))
                ? questSettings.outputFolder
                : "Assets/RARA/QuestConverter/Generated";
            outputRoot = outputRoot.Replace('\\', '/').TrimEnd('/') + "/";

            // 元アバターの全マテリアル(名前照合用)。
            var sourceMaterials = new List<Material>();
            if (originalGo != null)
            {
                foreach (Renderer rr in originalGo.GetComponentsInChildren<Renderer>(true))
                {
                    if (rr == null || rr.sharedMaterials == null) continue;
                    foreach (Material m in rr.sharedMaterials)
                    {
                        if (m != null && !sourceMaterials.Contains(m)) sourceMaterials.Add(m);
                    }
                }
            }

            var seen = new HashSet<Material>();
            foreach (Renderer renderer in questClone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.sharedMaterials == null) continue;
                foreach (Material mat in renderer.sharedMaterials)
                {
                    if (mat == null || !seen.Add(mat)) continue;
                    string path = AssetDatabase.GetAssetPath(mat);
                    if (string.IsNullOrEmpty(path) ||
                        !path.Replace('\\', '/').StartsWith(outputRoot, StringComparison.Ordinal))
                    {
                        continue; // 出力フォルダ配下の生成マテリアルのみ対象。
                    }

                    var pair = new AvatarStudioResultPair
                    {
                        converted = mat,
                        source = FindSourceMaterialForConverted(mat.name, sourceMaterials),
                    };
                    pair.note = BuildResultNote(pair, report);
                    pairs.Add(pair);
                }
            }
            pairs.Sort((a, b) => string.CompareOrdinal(a.converted.name, b.converted.name));
            return pairs;
        }

        /// <summary>変換後マテリアル名 "{元名}_Quest…" から元マテリアルを推定する(最長一致を採用)。</summary>
        private static Material FindSourceMaterialForConverted(string convertedName, List<Material> sourceMaterials)
        {
            if (string.IsNullOrEmpty(convertedName) || sourceMaterials == null) return null;

            Material best = null;
            int bestLength = -1;
            foreach (Material m in sourceMaterials)
            {
                if (m == null) continue;
                string rawName = m.name;
                string sanitized = QuestConverterUtility.SanitizeAssetName(rawName);
                bool matched =
                    convertedName.StartsWith(rawName + "_Quest", StringComparison.Ordinal) ||
                    convertedName.StartsWith(sanitized + "_Quest", StringComparison.Ordinal);
                if (matched && rawName.Length > bestLength)
                {
                    best = m;
                    bestLength = rawName.Length;
                }
            }
            return best;
        }

        /// <summary>レポートからこのマテリアルに関する一言メモを抽出する(警告・エラーを優先)。</summary>
        private static string BuildResultNote(AvatarStudioResultPair pair, ConversionReport report)
        {
            if (report == null || pair == null) return null;
            string key = pair.source != null ? pair.source.name
                : (pair.converted != null ? pair.converted.name : null);
            if (string.IsNullOrEmpty(key)) return null;

            string firstInfo = null;
            string genericInfo = null;
            foreach (ConversionReport.Entry entry in report.entries)
            {
                string message = entry.message;
                if (string.IsNullOrEmpty(message) || message.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                if (entry.severity != ConversionReport.Severity.Info)
                {
                    return ToSingleLineNote(message);
                }
                if (message.IndexOf("へ変換しました", StringComparison.Ordinal) >= 0)
                {
                    if (genericInfo == null) genericInfo = message;
                }
                else if (firstInfo == null)
                {
                    firstInfo = message;
                }
            }
            string picked = firstInfo != null ? firstInfo : genericInfo;
            return picked != null ? ToSingleLineNote(picked) : null;
        }

        private static string ToSingleLineNote(string message)
        {
            const int maxLength = 90;
            string line = message.Replace("\r", string.Empty).Replace('\n', ' ');
            return line.Length <= maxLength ? line : line.Substring(0, maxLength) + "…";
        }

        /// <summary>変換結果チェックの1行(元サムネイル → 変換後サムネイル + 名前・メモ)を描画する。</summary>
        private static void DrawResultPairRow(AvatarStudioResultPair pair)
        {
            if (pair == null || pair.converted == null) return;
            using (new EditorGUILayout.HorizontalScope(GUI.skin.box))
            {
                pair.sourcePreview = DrawResultThumb(pair.source, pair.sourcePreview, "元マテリアル");
                GUILayout.Label("→", GUILayout.Width(18f), GUILayout.Height(64f));
                pair.convertedPreview = DrawResultThumb(pair.converted, pair.convertedPreview, "変換後マテリアル");
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(
                        "元: " + (pair.source != null ? pair.source.name : "(特定できません)"),
                        EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField("変換後: " + pair.converted.name, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(pair.note))
                    {
                        EditorGUILayout.LabelField(pair.note, AvatarStudioUI.MiniWrapLabel);
                    }
                }
            }
        }

        /// <summary>64pxのマテリアルサムネイルを描画し、ロード完了後のプレビューを返す(ロード中はミニサムネイルで代用)。</summary>
        private static Texture2D DrawResultThumb(Material material, Texture2D cached, string label)
        {
            Rect rect = GUILayoutUtility.GetRect(64f, 64f, GUILayout.Width(64f), GUILayout.Height(64f));
            if (material == null)
            {
                GUI.Label(rect, new GUIContent("なし", label + "は特定できませんでした(アトラス統合の生成物など)"), EditorStyles.centeredGreyMiniLabel);
                return null;
            }

            if (cached == null) cached = AssetPreview.GetAssetPreview(material); // ロード中はnull
            Texture tex = cached != null ? (Texture)cached : AssetPreview.GetMiniThumbnail(material);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);

            if (GUI.Button(rect, new GUIContent(string.Empty, label + ": " + material.name + "(クリックでピン表示)"), GUIStyle.none))
            {
                EditorGUIUtility.PingObject(material);
            }
            return cached;
        }
    }
}
#endif
