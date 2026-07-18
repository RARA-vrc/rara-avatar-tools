// RARA アバター軽量化・Quest/iOS対応ツール(統合ウィンドウ) - デュアル診断パネル
// AvatarStudioDiagnostics.Analyze が作った StudioDiagnosis を、PC(Windows)と Quest/iOS(Android)の
// 2列で並記するテーブルとして描画する。ターゲット別の目標ランクピッカー(PC=pcTargetRank / Quest=questGoalRank)を
// 提供し、各項目の現在ランクを閾値色で塗り、目標を超過した項目を強調する。
//
// 【契約(Implementer A の AvatarStudioSettings)】このパネルは次の2フィールドのみを参照・編集する:
//   ・PCTargetRank pcTargetRank        … PC目標ランク(Excellent=0..Poor=3)
//   ・int          questGoalRank       … Quest目標ランクのインデックス(Excellent=0..Poor=3)
// フィールド名が最終契約と異なる場合は compile 側で読み替えること(api_gaps に記載)。
//
// 描画のみ(READ-ONLY レンダラ)。診断計測は C の「1診断」実行時に AvatarStudioDiagnostics が行う。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RARA.PCOptimizer;
using RARA.QuestConverter;

namespace RARA.AvatarStudio
{
    /// <summary>
    /// デュアル診断テーブルの描画(静的)。goal ランクの変更があれば true を返す(呼び出し側=C が SaveSettings する)。
    /// </summary>
    public static class AvatarStudioDualDiagnosisPanel
    {
        private const float LabelColWidth = 172f;
        private const float ValueColWidth = 96f;
        private const float RankColWidth = 92f;
        private const int MaxListEntries = 10;

        /// <summary>
        /// デュアル診断テーブル一式を描画する。
        /// diag が null(未診断)なら案内だけ出す。showPC / showQuest はターゲットチップに対応(その列を出すか)。
        /// 戻り値: 目標ランクピッカーが変更されたら true。
        /// </summary>
        public static bool Draw(StudioDiagnosis diag, AvatarStudioSettings settings, bool showPC, bool showQuest)
        {
            bool changed = false;
            if (settings == null) return false;

            // ---- 目標ランクピッカー(ターゲット別) ----
            changed |= DrawGoalPickers(settings, showPC, showQuest);

            if (diag == null || !diag.HasAny)
            {
                EditorGUILayout.HelpBox("「1. 診断」を実行すると、PC と Quest/iOS の現在値・ランクをここに並記します。", MessageType.Info);
                return changed;
            }

            EditorGUILayout.Space(4f);

            // ---- 総合ランクの要約 ----
            DrawOverallSummary(diag, settings, showPC, showQuest);

            EditorGUILayout.Space(2f);

            // ---- 比較テーブル ----
            DrawComparisonTable(diag, settings, showPC, showQuest);

            // ---- Quest の詳細(非モバイルマテリアル・非対応コンポーネント・テクスチャ警告・サイズ推定) ----
            if (showQuest && diag.questRaw != null)
            {
                EditorGUILayout.Space(4f);
                DrawQuestDetails(diag.questRaw);
            }

            // ---- 注意 / エラー ----
            if (diag.notes != null && diag.notes.Count > 0)
            {
                EditorGUILayout.Space(2f);
                foreach (string note in diag.notes)
                {
                    if (!string.IsNullOrEmpty(note)) EditorGUILayout.HelpBox(note, MessageType.Warning);
                }
            }

            // H5: AAO・統合はビルド時適用のため、生成直後・診断時の数値は実アップロード後より控えめに出る旨の注記。
            EditorGUILayout.LabelField(
                "※ SkinnedMesh統合・トグル固定・Trace and Optimize などは VRChat ビルド時(AAO/NDMF)に適用されます。" +
                "そのため、この診断値や生成直後の数値は、実際のアップロード後よりやや多め(控えめな削減)に見えることがあります。",
                EditorStyles.wordWrappedMiniLabel);

            return changed;
        }

        // ================================================================
        // 目標ランクピッカー
        // ================================================================
        private static bool DrawGoalPickers(AvatarStudioSettings settings, bool showPC, bool showQuest)
        {
            bool changed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("目標ランク", "PC と Quest/iOS それぞれの目標パフォーマンスランク。超過項目を強調します。"),
                    GUILayout.Width(72f));

                if (showPC)
                {
                    GUILayout.Label("PC", GUILayout.Width(24f));
                    int pcIndex = Mathf.Clamp((int)settings.pcTargetRank, 0, AvatarStudioDiagnostics.GoalRankNames.Length - 1);
                    int newPc = GUILayout.Toolbar(pcIndex, AvatarStudioDiagnostics.GoalRankNames);
                    if (newPc != pcIndex)
                    {
                        settings.pcTargetRank = (PCTargetRank)newPc;
                        changed = true;
                    }
                }

                if (showQuest)
                {
                    GUILayout.Label("Quest", GUILayout.Width(40f));
                    int qIndex = Mathf.Clamp(settings.questGoalRank, 0, AvatarStudioDiagnostics.GoalRankNames.Length - 1);
                    int newQ = GUILayout.Toolbar(qIndex, AvatarStudioDiagnostics.GoalRankNames);
                    if (newQ != qIndex)
                    {
                        settings.questGoalRank = newQ;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        // ================================================================
        // 総合ランク要約
        // ================================================================
        private static void DrawOverallSummary(StudioDiagnosis diag, AvatarStudioSettings settings, bool showPC, bool showQuest)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                if (showPC)
                {
                    EditorGUILayout.LabelField("PC総合", GUILayout.Width(52f));
                    DrawRatingChip(diag.pcOverallRating, GUILayout.Width(96f));
                    if (AvatarStudioDiagnostics.IsOverGoal(diag.pcOverallRating, (int)settings.pcTargetRank))
                        WarnMini("目標未達");
                    GUILayout.Space(12f);
                }
                if (showQuest)
                {
                    EditorGUILayout.LabelField("Quest総合", GUILayout.Width(62f));
                    DrawRatingChip(diag.questOverallRating, GUILayout.Width(96f));
                    if (!diag.questCanUpload)
                        WarnMini("アップロード不可(サイズ上限超過)");
                    else if (AvatarStudioDiagnostics.NormalizeRating(diag.questOverallRating) == "verypoor")
                        WarnMini("Very Poor(既定で非表示/揺れ物停止)");
                    else if (AvatarStudioDiagnostics.IsOverGoal(diag.questOverallRating, settings.questGoalRank))
                        WarnMini("目標未達");
                }
                GUILayout.FlexibleSpace();
            }
        }

        // ================================================================
        // 比較テーブル
        // ================================================================
        private static void DrawComparisonTable(StudioDiagnosis diag, AvatarStudioSettings settings, bool showPC, bool showQuest)
        {
            int pcGoal = (int)settings.pcTargetRank;
            int questGoal = settings.questGoalRank;

            // ヘッダー
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.Width(LabelColWidth));
                if (showPC)
                {
                    EditorGUILayout.LabelField("PC 現在値", EditorStyles.miniBoldLabel, GUILayout.Width(ValueColWidth));
                    EditorGUILayout.LabelField("PC ランク", EditorStyles.miniBoldLabel, GUILayout.Width(RankColWidth));
                }
                if (showQuest)
                {
                    EditorGUILayout.LabelField("Quest 現在値", EditorStyles.miniBoldLabel, GUILayout.Width(ValueColWidth));
                    EditorGUILayout.LabelField("Quest ランク", EditorStyles.miniBoldLabel, GUILayout.Width(RankColWidth));
                }
                GUILayout.FlexibleSpace();
            }

            foreach (StudioMetricRow row in diag.rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // 項目名(PC目標の閾値をツールチップに載せる)
                    string tip = row.tooltip ?? string.Empty;
                    if (showPC && row.hasPcStat)
                    {
                        int limit = PCRankLimits.GetLimit(settings.pcTargetRank, row.pcStat);
                        tip += string.Format("\nPC {0} 目標上限: {1}",
                            AvatarStudioDiagnostics.GoalRankNames[Mathf.Clamp(pcGoal, 0, 3)], limit);
                    }
                    EditorGUILayout.LabelField(new GUIContent(row.label, tip), GUILayout.Width(LabelColWidth));

                    if (showPC)
                    {
                        bool pcOver = row.hasPcStat && AvatarStudioDiagnostics.IsOverGoal(row.pcRating, pcGoal);
                        DrawValueCell(row.pcValueText, pcOver);
                        DrawRankCell(row.hasPcStat ? row.pcRating : string.Empty, pcOver, row.hasPcStat ? "-" : "対象外");
                    }
                    if (showQuest)
                    {
                        bool questOver = row.questOverLimit || AvatarStudioDiagnostics.IsOverGoal(row.questRating, questGoal);
                        DrawValueCell(row.questValueText, questOver);
                        DrawRankCell(row.questRating, questOver, "-");
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        /// <summary>現在値セル。目標超過なら赤字。</summary>
        private static void DrawValueCell(string text, bool over)
        {
            Color prev = GUI.color;
            if (over) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
            EditorGUILayout.LabelField(string.IsNullOrEmpty(text) ? "-" : text, GUILayout.Width(ValueColWidth));
            GUI.color = prev;
        }

        /// <summary>ランクセル。ランク色で塗り、目標超過なら「⚠」を前置する。空ランクは placeholder。</summary>
        private static void DrawRankCell(string rating, bool over, string placeholder)
        {
            if (string.IsNullOrEmpty(rating))
            {
                Color p = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                EditorGUILayout.LabelField(placeholder, GUILayout.Width(RankColWidth));
                GUI.color = p;
                return;
            }
            Color prev = GUI.color;
            GUI.color = over ? AvatarStudioDiagnostics.OverLimitColor : AvatarStudioDiagnostics.RatingColor(rating);
            string label = (over ? "⚠ " : string.Empty) + AvatarStudioDiagnostics.DisplayRating(rating);
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(RankColWidth));
            GUI.color = prev;
        }

        private static void DrawRatingChip(string rating, params GUILayoutOption[] options)
        {
            Color prev = GUI.color;
            GUI.color = AvatarStudioDiagnostics.RatingColor(rating);
            EditorGUILayout.LabelField(AvatarStudioDiagnostics.DisplayRating(rating), EditorStyles.boldLabel, options);
            GUI.color = prev;
        }

        private static void WarnMini(string text)
        {
            Color prev = GUI.color;
            GUI.color = AvatarStudioDiagnostics.OverLimitColor;
            EditorGUILayout.LabelField("⚠ " + text, EditorStyles.miniBoldLabel, GUILayout.Width(200f));
            GUI.color = prev;
        }

        // ================================================================
        // Quest 詳細(非モバイルマテリアル・非対応コンポーネント・テクスチャ警告・サイズ推定)
        // ================================================================
        private static void DrawQuestDetails(DiagnosticsResult quest)
        {
            // サイズ推定
            SizeEstimateResult size = quest.sizeEstimate;
            if (size != null)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("ダウンロードサイズ(概算)", GUILayout.Width(180f));
                    Color prev = GUI.color;
                    // 圧縮後(10MB)
                    if (size.overCap) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
                    EditorGUILayout.LabelField(
                        string.Format("圧縮後 約 {0:F1} / {1} MB", size.estimatedDownloadMB, QuestLimits.HardDownloadSizeCapMB),
                        EditorStyles.boldLabel, GUILayout.Width(150f));
                    // 展開後(40MB)
                    GUI.color = size.overUncompressedCap ? AvatarStudioDiagnostics.OverLimitColor : prev;
                    EditorGUILayout.LabelField(
                        string.Format("展開後 約 {0:F1} / {1} MB", size.estimatedUncompressedMB, QuestLimits.HardUncompressedSizeCapMB),
                        EditorStyles.boldLabel, GUILayout.Width(160f));
                    GUI.color = prev;
                    GUILayout.FlexibleSpace();
                }
                if (size.overCap || size.overUncompressedCap)
                {
                    string which = size.overCap && size.overUncompressedCap
                        ? "圧縮後" + QuestLimits.HardDownloadSizeCapMB + "MB・展開後" + QuestLimits.HardUncompressedSizeCapMB + "MBの両方"
                        : (size.overCap ? "圧縮後" + QuestLimits.HardDownloadSizeCapMB + "MB"
                                        : "展開後" + QuestLimits.HardUncompressedSizeCapMB + "MB(圧縮後とは独立した上限)");
                    EditorGUILayout.HelpBox("Android のサイズ上限(" + which +
                        ")を超過する見込みです。テクスチャ縮小(ステップ4)で削減してください。※概算値です。", MessageType.Warning);
                }
            }

            // 非モバイルシェーダーのマテリアル
            if (quest.nonMobileMaterials != null && quest.nonMobileMaterials.Count > 0)
            {
                EditorGUILayout.LabelField(string.Format("Quest非対応シェーダーのマテリアル: {0} 件(マテリアル変換=ステップ6で対応)",
                    quest.nonMobileMaterials.Count), EditorStyles.miniLabel);
                DrawObjectList(quest.nonMobileMaterials);
            }

            // Android非対応コンポーネント
            if (quest.unsupportedComponents != null && quest.unsupportedComponents.Count > 0)
            {
                EditorGUILayout.LabelField(string.Format("Android非対応コンポーネント: {0} 件(変換時に削除されます)",
                    quest.unsupportedComponents.Count), EditorStyles.miniLabel);
                DrawObjectList(quest.unsupportedComponents);
            }

            // テクスチャ警告
            if (quest.textureWarnings != null && quest.textureWarnings.Count > 0)
            {
                EditorGUILayout.LabelField(string.Format("テクスチャ設定の警告: {0} 件", quest.textureWarnings.Count), EditorStyles.miniLabel);
                int shown = 0;
                foreach (string w in quest.textureWarnings)
                {
                    if (string.IsNullOrEmpty(w)) continue;
                    if (shown >= MaxListEntries) { EditorGUILayout.LabelField("  ...", EditorStyles.miniLabel); break; }
                    EditorGUILayout.LabelField("  ・" + w, EditorStyles.wordWrappedMiniLabel);
                    shown++;
                }
            }
        }

        /// <summary>Object 参照リストを読み取り専用の ObjectField 群で表示する(先頭 MaxListEntries 件 + 「他N件」)。</summary>
        private static void DrawObjectList<T>(List<T> items) where T : Object
        {
            if (items == null) return;
            int shown = 0;
            int nonNullTotal = 0; // 「他N件」は null を除いた実件数から算出する(null混入時の水増しを防ぐ)。
            using (new EditorGUI.DisabledScope(true))
            {
                foreach (T item in items)
                {
                    if (item == null) continue;
                    nonNullTotal++;
                    if (shown >= MaxListEntries) continue;
                    EditorGUILayout.ObjectField(item, typeof(T), false);
                    shown++;
                }
            }
            int remainder = nonNullTotal - shown;
            if (remainder > 0)
                EditorGUILayout.LabelField(string.Format("  ...他 {0} 件", remainder), EditorStyles.miniLabel);
        }
    }
}
#endif
