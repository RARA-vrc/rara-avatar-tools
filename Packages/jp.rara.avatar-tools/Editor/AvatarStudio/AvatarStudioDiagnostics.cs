// RARA アバター軽量化・Quest/iOS対応ツール(統合ウィンドウ) - 診断エンジン
// 「PC(Windows)基準」と「Quest/iOS(Android)基準」の両方でパフォーマンス統計を算出し、
// 1つの StudioDiagnosis へまとめる。実装は既存2ツールのエンジンを壊さないよう、
//   ・PC基準は PCOptimizerWindow.ComputePCPerformance と同じ手順の「studio-local コピー」で計測する
//     (PCOptimizerWindow は private のため参照できない。ロジックのみ複製。数値は PC軽量化ツールと一致する)。
//   ・Quest基準は公開 API の RARA.QuestConverter.QuestDiagnostics.Analyze を呼ぶ(SDKのモバイル基準)。
//   ・EditorOnly 除外は公開ヘルパ RARA.QuestConverter.QuestCompat.StripEditorOnlySubtrees を使う。
// このファイルは Assembly-CSharp-Editor(asmdef無し)でコンパイルされ、RARA.QuestConverter /
// RARA.PCOptimizer の public 型を直接参照できる。診断は完全 READ-ONLY(一時複製のみ計測、元アバター無改変)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using RARA.PCOptimizer;
using RARA.QuestConverter;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace RARA.AvatarStudio
{
    /// <summary>
    /// 統合ウィンドウのデュアル診断テーブル1行。PC(Windows)基準と Quest/iOS(Android)基準の
    /// 「現在値・項目別ランク」を並記する。PC側は PCRankLimits.PCStat で目標ランク閾値を引ける。
    /// </summary>
    public sealed class StudioMetricRow
    {
        /// <summary>表示名(日本語)。</summary>
        public string label;
        /// <summary>補足ツールチップ。</summary>
        public string tooltip;

        /// <summary>PC目標ランク閾値の参照キー(hasPcStat==false のとき無効)。</summary>
        public PCRankLimits.PCStat pcStat;
        /// <summary>この行がPC閾値(PCRankLimits)で比較できるか(アニメーター/パーティクル等はfalse)。</summary>
        public bool hasPcStat;
        /// <summary>テクスチャメモリ(MB)行か(表示・整形用)。</summary>
        public bool isMB;

        // ---- PC(Windows)基準 ----
        public bool pcHasValue;
        public float pcValue;
        public string pcValueText = "-";
        /// <summary>PCのSDK項目別ランク("Excellent"/"Good"/"Medium"/"Poor"/"VeryPoor")。未計測は空。</summary>
        public string pcRating = string.Empty;

        // ---- Quest/iOS(Android)基準 ----
        public bool questHasValue;
        public float questValue;
        public string questValueText = "-";
        /// <summary>QuestのSDK項目別ランク。未計測は空。</summary>
        public string questRating = string.Empty;
        /// <summary>Quest側で VeryPoor(Androidアップロード不可の上限超過)か。</summary>
        public bool questOverLimit;
    }

    /// <summary>統合診断の結果一式(PC + Quest)。UIはこれ1つを描画すればよい。</summary>
    public sealed class StudioDiagnosis
    {
        /// <summary>PC基準を計測したか(target に PC が含まれていたか)。</summary>
        public bool pcIncluded;
        /// <summary>Quest基準を計測したか(target に Quest が含まれていたか)。</summary>
        public bool questIncluded;

        /// <summary>PC(Windows)総合ランク。</summary>
        public string pcOverallRating = string.Empty;
        /// <summary>PCテクスチャメモリ(MB)。</summary>
        public float pcTextureMemoryMB;

        /// <summary>Quest(Android)総合ランク。</summary>
        public string questOverallRating = string.Empty;
        /// <summary>Quest総合ランクが VeryPoor でなければ true(Androidへアップロード可)。</summary>
        public bool questCanUpload;

        /// <summary>デュアル比較テーブル(PC/Quest 並記)。</summary>
        public readonly List<StudioMetricRow> rows = new List<StudioMetricRow>();

        /// <summary>Quest診断の生結果(非モバイルマテリアル・非対応コンポーネント・テクスチャ警告・サイズ推定)。Quest未計測時はnull。</summary>
        public DiagnosticsResult questRaw;

        /// <summary>診断中に発生した注意・エラーメッセージ(UIへそのまま表示してよい)。</summary>
        public readonly List<string> notes = new List<string>();

        /// <summary>いずれかの基準で計測できたか。</summary>
        public bool HasAny { get { return pcIncluded || questIncluded; } }
    }

    /// <summary>
    /// 統合ウィンドウの診断エンジン(静的)。PC基準は studio-local 計測、Quest基準は公開 QuestDiagnostics。
    /// 例外はUIへ投げず、notes へメッセージを残して安全側(空/既定)に倒す。
    /// </summary>
    public static class AvatarStudioDiagnostics
    {
        // ================================================================
        // デュアル比較テーブルの項目定義(PC/Quest 共通の12項目 + Quest固有2項目)
        // ================================================================
        private struct MetricDef
        {
            public string label;
            public string tooltip;
            public PCRankLimits.PCStat pcStat;
            public bool hasPcStat;
            public AvatarPerformanceCategory category;   // PC/Quest 双方の SDK 統計カテゴリ
            public string questCategoryLabel;            // DiagnosticsRow.category とのマッチ用
            public bool isMB;

            public MetricDef(string label, string tooltip, PCRankLimits.PCStat pcStat, bool hasPcStat,
                AvatarPerformanceCategory category, string questCategoryLabel, bool isMB = false)
            {
                this.label = label;
                this.tooltip = tooltip;
                this.pcStat = pcStat;
                this.hasPcStat = hasPcStat;
                this.category = category;
                this.questCategoryLabel = questCategoryLabel;
                this.isMB = isMB;
            }
        }

        // Quest カテゴリラベルは QuestDiagnostics.AddRow の第3引数、PCカテゴリは PCOptimizerWindow.StatDefs と一致させる。
        private static readonly MetricDef[] Metrics =
        {
            new MetricDef("三角数(ポリゴン)", "メッシュの三角ポリゴン数。PCは Good/Medium/Poor いずれも70,000が上限。Questはポリゴン削減で削減",
                PCRankLimits.PCStat.Triangles, true, AvatarPerformanceCategory.PolyCount, "ポリゴン数"),
            new MetricDef("スキンメッシュ数", "SkinnedMeshRendererの数。トグル整理・SkinnedMesh統合・アトラス統合で削減",
                PCRankLimits.PCStat.SkinnedMeshes, true, AvatarPerformanceCategory.SkinnedMeshCount, "スキンメッシュ数"),
            new MetricDef("メッシュ数", "MeshRendererの数",
                PCRankLimits.PCStat.MeshRenderers, true, AvatarPerformanceCategory.MeshCount, "基本メッシュ数"),
            new MetricDef("マテリアルスロット数", "全レンダラーのマテリアルスロット合計。アトラス統合・トグル整理・SkinnedMesh統合で削減",
                PCRankLimits.PCStat.MaterialSlots, true, AvatarPerformanceCategory.MaterialCount, "マテリアルスロット数"),
            new MetricDef("テクスチャメモリ(MB)", "テクスチャのVRAM使用量。テクスチャ縮小で削減",
                PCRankLimits.PCStat.TextureMemoryMB, true, AvatarPerformanceCategory.TextureMegabytes, "テクスチャメモリ(MB)", true),
            new MetricDef("ボーン数", "スキニングに使うボーン数。AAOのTrace and Optimizeで未使用分を自動削減",
                PCRankLimits.PCStat.Bones, true, AvatarPerformanceCategory.BoneCount, "ボーン数"),
            new MetricDef("PhysBoneコンポーネント数", "VRCPhysBoneの数。マージ・削除で削減",
                PCRankLimits.PCStat.PhysBoneComponents, true, AvatarPerformanceCategory.PhysBoneComponentCount, "PhysBoneコンポーネント数"),
            new MetricDef("PhysBone対象Transform数", "PhysBoneが揺らすTransformの合計",
                PCRankLimits.PCStat.PhysBoneTransforms, true, AvatarPerformanceCategory.PhysBoneTransformCount, "PhysBone対象Transform数"),
            new MetricDef("PhysBoneコライダー数", "VRCPhysBoneColliderの数",
                PCRankLimits.PCStat.PhysBoneColliders, true, AvatarPerformanceCategory.PhysBoneColliderCount, "PhysBoneコライダー数"),
            new MetricDef("PhysBone衝突チェック数", "PhysBoneの衝突判定の総回数",
                PCRankLimits.PCStat.PhysBoneCollisionChecks, true, AvatarPerformanceCategory.PhysBoneCollisionCheckCount, "PhysBone衝突チェック数"),
            new MetricDef("コンタクト数", "VRCContact(非ローカル)の数",
                PCRankLimits.PCStat.Contacts, true, AvatarPerformanceCategory.ContactCount, "コンタクト数"),
            new MetricDef("コンストレイント数", "VRCConstraintの数",
                PCRankLimits.PCStat.Constraints, true, AvatarPerformanceCategory.ConstraintsCount, "コンストレイント数"),
            // ---- Quest 固有(PC閾値の比較対象なし。Quest列のみ表示) ----
            new MetricDef("アニメーター数", "Animatorの数(Quest基準のみ集計)",
                default(PCRankLimits.PCStat), false, AvatarPerformanceCategory.AnimatorCount, "アニメーター数"),
            new MetricDef("パーティクルシステム数", "ParticleSystemの数(Quest基準のみ集計)",
                default(PCRankLimits.PCStat), false, AvatarPerformanceCategory.ParticleSystemCount, "パーティクルシステム数"),
        };

        /// <summary>4段階の目標ランク名(PC/Quest 共通。Excellent が最良)。ゴールピッカーの並びと一致させる。</summary>
        public static readonly string[] GoalRankNames = { "Excellent", "Good", "Medium", "Poor" };

        // ================================================================
        // 診断本体
        // ================================================================

        /// <summary>
        /// アバターを PC / Quest 両基準で診断し、デュアル比較テーブルを組み立てて返す。
        /// includePC / includeQuest はターゲットチップ(PC / Quest)に対応する。両方 false のときは空の結果。
        /// questSettings は Quest 基準の除外パス(questExcludePaths)を反映するために渡す(null可 = 除外なし)。
        /// 例外は投げず、失敗は notes へ残す(元アバターは一切変更しない)。
        /// </summary>
        public static StudioDiagnosis Analyze(VRCAvatarDescriptor avatar, bool includePC, bool includeQuest, QuestConvertSettings questSettings)
        {
            var diag = new StudioDiagnosis { pcIncluded = includePC, questIncluded = includeQuest };

            if (avatar == null)
            {
                diag.notes.Add("診断対象のアバターが指定されていません。");
                return diag;
            }

            // ---- PC(Windows)基準 ----
            var pcCategoryCells = new Dictionary<AvatarPerformanceCategory, StatCell>();
            if (includePC)
            {
                try
                {
                    ComputePcPerformance(avatar.gameObject, pcCategoryCells,
                        out string pcOverall, out float pcTexMB);
                    diag.pcOverallRating = pcOverall;
                    diag.pcTextureMemoryMB = pcTexMB;
                }
                catch (Exception ex)
                {
                    Debug.LogError("[RARA AvatarStudio] PC基準診断で例外が発生しました: " + ex);
                    diag.notes.Add("PC基準診断に失敗しました: " + ex.Message);
                    pcCategoryCells.Clear();
                }
            }

            // ---- Quest/iOS(Android)基準 ----
            DiagnosticsResult questResult = null;
            if (includeQuest)
            {
                try
                {
                    questResult = QuestDiagnostics.Analyze(avatar, questSettings ?? new QuestConvertSettings());
                    diag.questRaw = questResult;
                    diag.questOverallRating = questResult.overallRating;
                    diag.questCanUpload = questResult.canUploadToAndroid;
                    if (questResult.textureWarnings != null)
                    {
                        // QuestDiagnostics は致命的失敗時に textureWarnings へエラー文を入れて返す。
                        // それらは UI のテクスチャ警告欄に出るため notes へは重複追加しない。
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[RARA AvatarStudio] Quest基準診断で例外が発生しました: " + ex);
                    diag.notes.Add("Quest基準診断に失敗しました: " + ex.Message);
                }
            }

            // Quest 行を category ラベルで引けるよう辞書化する。
            var questByLabel = new Dictionary<string, DiagnosticsRow>(StringComparer.Ordinal);
            if (questResult != null && questResult.perfRows != null)
            {
                foreach (DiagnosticsRow r in questResult.perfRows)
                {
                    if (r != null && !string.IsNullOrEmpty(r.category)) questByLabel[r.category] = r;
                }
            }

            // ---- デュアル比較テーブルを組み立てる ----
            foreach (MetricDef def in Metrics)
            {
                // Quest固有項目はQuest未計測なら行自体を出さない。
                if (!def.hasPcStat && !includeQuest) continue;

                var row = new StudioMetricRow
                {
                    label = def.label,
                    tooltip = def.tooltip,
                    pcStat = def.pcStat,
                    hasPcStat = def.hasPcStat,
                    isMB = def.isMB,
                };

                if (includePC && def.hasPcStat && pcCategoryCells.TryGetValue(def.category, out StatCell pc))
                {
                    row.pcHasValue = pc.hasValue;
                    row.pcValue = pc.value;
                    row.pcValueText = FormatValue(pc.value, pc.hasValue, def.isMB);
                    row.pcRating = pc.rating;
                }

                if (includeQuest && questByLabel.TryGetValue(def.questCategoryLabel, out DiagnosticsRow qr))
                {
                    row.questValueText = string.IsNullOrEmpty(qr.value) ? "-" : qr.value;
                    row.questHasValue = row.questValueText != "-";
                    row.questRating = qr.rating;
                    row.questOverLimit = qr.overLimit;
                }

                diag.rows.Add(row);
            }

            return diag;
        }

        // ================================================================
        // PC(Windows)基準の計測(PCOptimizerWindow.ComputePCPerformance の studio-local コピー)
        //   PCOptimizerWindow は編集禁止・private のため、ロジックのみ複製する。数値は PC軽量化ツールと一致する。
        // ================================================================

        /// <summary>統計セル(現在値・SDKランク・計測有無)。</summary>
        private struct StatCell
        {
            public bool hasValue;
            public float value;
            public string rating;
        }

        /// <summary>
        /// Windows(PC)基準でパフォーマンス統計を算出する。EditorOnly サブツリーを除去した一時複製に対して
        /// 計測し(アップロード時の除去に合わせる)、元アバターは変更しない。結果はカテゴリ別に categoryOut へ入れる。
        /// </summary>
        private static void ComputePcPerformance(
            GameObject avatarRoot,
            Dictionary<AvatarPerformanceCategory, StatCell> categoryOut,
            out string overallRating,
            out float textureMemoryMB)
        {
            overallRating = string.Empty;
            textureMemoryMB = 0f;
            if (avatarRoot == null) return;

            GameObject temp = UnityEngine.Object.Instantiate(avatarRoot);
            temp.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                // アップロード時に除去される EditorOnly サブツリーを取り除く(公開ヘルパを使用)。
                QuestCompat.StripEditorOnlySubtrees(temp);
                RefreshConstraintGroups(temp);

                // false => Windows(PC)基準のレベルセットで評価する。
                var stats = new AvatarPerformanceStats(false);
                AvatarPerformance.CalculatePerformanceStats(avatarRoot.name, temp, stats, false);

                foreach (MetricDef def in Metrics)
                {
                    PerformanceRating rating = stats.GetPerformanceRatingForCategory(def.category);
                    bool has = ReadStatValue(stats, def.category, out float value);
                    categoryOut[def.category] = new StatCell { hasValue = has, value = value, rating = rating.ToString() };
                    if (def.category == AvatarPerformanceCategory.TextureMegabytes && has) textureMemoryMB = value;
                }

                overallRating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall).ToString();
            }
            finally
            {
                if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        /// <summary>SDK統計カテゴリに対応する値を読む(Nullableは未計測扱いでfalse)。</summary>
        private static bool ReadStatValue(AvatarPerformanceStats stats, AvatarPerformanceCategory category, out float value)
        {
            switch (category)
            {
                case AvatarPerformanceCategory.PolyCount: return FromInt(stats.polyCount, out value);
                case AvatarPerformanceCategory.SkinnedMeshCount: return FromInt(stats.skinnedMeshCount, out value);
                case AvatarPerformanceCategory.MeshCount: return FromInt(stats.meshCount, out value);
                case AvatarPerformanceCategory.MaterialCount: return FromInt(stats.materialCount, out value);
                case AvatarPerformanceCategory.TextureMegabytes: return FromFloat(stats.textureMegabytes, out value);
                case AvatarPerformanceCategory.BoneCount: return FromInt(stats.boneCount, out value);
                case AvatarPerformanceCategory.PhysBoneComponentCount: return FromInt(stats.physBone?.componentCount, out value);
                case AvatarPerformanceCategory.PhysBoneTransformCount: return FromInt(stats.physBone?.transformCount, out value);
                case AvatarPerformanceCategory.PhysBoneColliderCount: return FromInt(stats.physBone?.colliderCount, out value);
                case AvatarPerformanceCategory.PhysBoneCollisionCheckCount: return FromInt(stats.physBone?.collisionCheckCount, out value);
                case AvatarPerformanceCategory.ContactCount: return FromInt(stats.contactCount, out value);
                case AvatarPerformanceCategory.ConstraintsCount: return FromInt(stats.constraintsCount, out value);
                case AvatarPerformanceCategory.AnimatorCount: return FromInt(stats.animatorCount, out value);
                case AvatarPerformanceCategory.ParticleSystemCount: return FromInt(stats.particleSystemCount, out value);
                default: value = 0f; return false;
            }
        }

        private static bool FromInt(int? source, out float value)
        {
            value = source.HasValue ? source.Value : 0f;
            return source.HasValue;
        }

        private static bool FromFloat(float? source, out float value)
        {
            value = source.HasValue ? source.Value : 0f;
            return source.HasValue;
        }

        /// <summary>
        /// VRC.Dynamics.VRCConstraintManager.Sdk_ManuallyRefreshGroups(VRCConstraintBase[]) をリフレクションで呼ぶ。
        /// 型は internal のため直接参照できない。見つからない場合は続行する(コンストレイント数がやや不正確になり得る)。
        /// PCOptimizerWindow / QuestDiagnostics と同じ手順の studio-local コピー。
        /// </summary>
        private static void RefreshConstraintGroups(GameObject root)
        {
            var constraints = root.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true);
            if (constraints == null || constraints.Length == 0) return;

            var managerType = QuestCompat.FindType("VRC.Dynamics.VRCConstraintManager");
            var method = managerType != null
                ? managerType.GetMethod(
                    "Sdk_ManuallyRefreshGroups",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(VRC.Dynamics.VRCConstraintBase[]) },
                    null)
                : null;
            if (method != null)
            {
                try { method.Invoke(null, new object[] { constraints }); }
                catch (Exception ex) { Debug.LogWarning("[RARA AvatarStudio] コンストレイントグループの更新に失敗しました: " + ex.Message); }
            }
        }

        private static string FormatValue(float value, bool hasValue, bool isMB)
        {
            if (!hasValue) return "-";
            return isMB
                ? value.ToString("F1", CultureInfo.InvariantCulture) + " MB"
                : value.ToString("N0", CultureInfo.InvariantCulture);
        }

        // ================================================================
        // ランク表示・色・比較(旧2ウィンドウと同じ配色。studio-local コピー)
        // ================================================================

        /// <summary>ランク文字列を正規化("Very Poor"→"verypoor")。</summary>
        public static string NormalizeRating(string rating)
        {
            return (rating ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        /// <summary>ランク文字列の表示名("VeryPoor"→"Very Poor")。未知・空は「不明」。</summary>
        public static string DisplayRating(string rating)
        {
            switch (NormalizeRating(rating))
            {
                case "excellent": return "Excellent";
                case "good": return "Good";
                case "medium": return "Medium";
                case "poor": return "Poor";
                case "verypoor": return "Very Poor";
                default: return string.IsNullOrEmpty(rating) ? "不明" : rating;
            }
        }

        /// <summary>ランク文字列に対応する表示色(旧2ウィンドウと同一。不明は白)。</summary>
        public static Color RatingColor(string rating)
        {
            switch (NormalizeRating(rating))
            {
                case "excellent": return new Color(0.35f, 0.9f, 0.5f);
                case "good": return new Color(0.6f, 0.9f, 0.35f);
                case "medium": return new Color(1f, 0.85f, 0.35f);
                case "poor": return new Color(1f, 0.6f, 0.25f);
                case "verypoor": return new Color(1f, 0.4f, 0.4f);
                default: return Color.white;
            }
        }

        /// <summary>上限超過(VeryPoor)を強調する赤。</summary>
        public static Color OverLimitColor { get { return new Color(1f, 0.4f, 0.4f); } }

        /// <summary>
        /// ランク文字列を PerformanceRating の序数へ変換する(None=0, Excellent=1, Good=2, Medium=3, Poor=4, VeryPoor=5)。
        /// 小さいほど良い。未知・空は 0 を返す。
        /// </summary>
        public static int RatingOrdinal(string rating)
        {
            switch (NormalizeRating(rating))
            {
                case "excellent": return 1;
                case "good": return 2;
                case "medium": return 3;
                case "poor": return 4;
                case "verypoor": return 5;
                default: return 0;
            }
        }

        /// <summary>
        /// ゴールランクのインデックス(0=Excellent..3=Poor)を PerformanceRating 序数(1..4)へ変換する。
        /// </summary>
        public static int GoalIndexToOrdinal(int goalIndex)
        {
            return Mathf.Clamp(goalIndex, 0, GoalRankNames.Length - 1) + 1;
        }

        /// <summary>
        /// rating(現在ランク)が goalIndex(0=Excellent..3=Poor)より悪い(=目標超過)か。
        /// 未計測(序数0)は超過扱いしない。
        /// </summary>
        public static bool IsOverGoal(string rating, int goalIndex)
        {
            int ord = RatingOrdinal(rating);
            if (ord <= 0) return false;
            return ord > GoalIndexToOrdinal(goalIndex);
        }
    }
}
#endif
