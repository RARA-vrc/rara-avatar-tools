// RARA アバター軽量化・Quest/iOS対応ツール(統合ウィンドウ) - プレビューパネル群
// 各パネルは公開エンジン API を呼んで「実行前のプレビュー(概算)」を描画し、対応する
// AvatarStudioSettings のフィールドを編集する(変更があれば戻り値 true → C が SaveSettings)。
// エンジンは一切改変せず、READ-ONLY のプレビュー API のみを呼ぶ。重い計算は入力シグネチャで
// キャッシュし、毎フレーム再計算しない(設定変更・アバター変更・世代更新で無効化される)。
//
// 【呼ぶ公開エンジン API(すべて READ-ONLY プレビュー)】
//   ToggleConsolidator.DetectToggleGroups / SkinnedMeshMergePlanner.BuildPlan /
//   ComponentRemover.PreviewPhysBoneMerge(+CollectPhysBoneTogglePaths) /
//   PCTexturePlanner.BuildSuggestions(+EstimateTextureMemoryMB, ApplySuggestionsToPlan) /
//   QuestSizeEstimator.Estimate / Decimation.PolygonBudgetPlanner.BuildPlan /
//   PCMaterialAtlasser.PreviewPlan / AvatarQuestConverter.PreviewMaterials
//
// 【契約(Implementer A)】参照・編集する AvatarStudioSettings メンバー:
//   共有: toggleChoices, mergeSkinnedMeshesMode, skinnedMeshMergeOptOutPaths,
//         mergePhysBones, physBoneLooseMerge, physBoneRemovePaths
//   PC : pcTargetRank, pcEnableAtlas, pcTexturePlan
//   Quest: transparentHandling, shaderTarget, materialOverrides, questTextureSizePlan,
//          questEnableMeshiaSimplification, questMeshiaTargetTriangles(ポリゴン削減はMeshia連携)
//   (いずれも AvatarStudioSettings.cs で確認済みの実名。)
//
// 【エンジン設定の受け渡し】エンジンのプレビュー API が QuestConvertSettings / PCOptimizeSettings を要求する
//   4パネル(Questテクスチャ・ポリゴン・PCアトラス・Questマテリアル)は、その設定を引数で受け取る。
//   構築は C 側が A の AvatarStudioMapping.BuildQuestConvertSettings / BuildPCOptimizeSettings で行い、
//   毎フレーム現在の AvatarStudioSettings から作り直して渡す(このファイルはマッピングに依存しない)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RARA.PCOptimizer;
using RARA.QuestConverter;
using RARA.QuestConverter.Decimation;
using VRC.SDK3.Avatars.Components;

namespace RARA.AvatarStudio
{
    /// <summary>
    /// プレビュー結果の入力シグネチャ付きキャッシュ。パネルごとに (signature, result) を保持し、
    /// シグネチャ不一致のときのみ再計算する。generation を上げると全キャッシュが次回無効化される
    /// (RunAll 実行後などに C が Bump する)。
    /// </summary>
    public sealed class AvatarStudioPreviewCache
    {
        /// <summary>強制無効化の世代カウンタ。</summary>
        public int generation;

        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private struct Entry { public string sig; public object val; }

        /// <summary>panel キーの結果を返す。sig が変わっていれば build() で再計算する(例外は握って null)。</summary>
        public T GetOrBuild<T>(string panel, string sig, Func<T> build) where T : class
        {
            string full = generation + "|" + sig;
            if (_map.TryGetValue(panel, out Entry e) && e.sig == full && e.val is T cached) return cached;

            T val = null;
            try { val = build(); }
            catch (Exception ex) { Debug.LogError("[RARA AvatarStudio] プレビュー計算に失敗しました(" + panel + "): " + ex); }
            _map[panel] = new Entry { sig = full, val = val };
            return val;
        }

        /// <summary>全キャッシュを次回無効化する(世代を進める)。</summary>
        public void Bump() { generation++; }

        /// <summary>キャッシュを完全に破棄する(アバター切り替え時など)。</summary>
        public void Clear() { _map.Clear(); }
    }

    /// <summary>統合ウィンドウのプレビューパネル群(静的)。各 Draw は変更があれば true を返す。</summary>
    public static class AvatarStudioPreviewPanels
    {
        private const int MaxRows = 60;

        /// <summary>トグル一覧を専有削減量(展開後)の大きい順に並べ替えるか(セッション内で保持するUI状態)。</summary>
        private static bool _sortToggleBySize;

        // Quest(Android)PhysBoneコンポーネントのランク別上限のうち、Excellent/Good は公開定数が無い
        // (QuestLimits は Medium/Poor のみ公開)ため、SDK 3.10.4 の
        // StatsLevels/Android/{Excellent,Good}_Android.asset の physBone.componentCount を直接引用してハードコードする。
        // Medium/Poor は QuestLimits.MediumPhysBoneComponents / PoorPhysBoneComponents を使う。
        private const int QuestExcellentPhysBoneComponents = 0;
        private const int QuestGoodPhysBoneComponents = 4;

        // ポリゴン削減の目標ランク別プリセット(Quest StatsLevels の polyCount)。Medium/Poor は公開定数
        // (QuestLimits)を、Excellent/Good は公開定数が無いため SDK の StatsLevels に合わせてハードコードする
        // (旧QuestConverterウィンドウの DecimationRankPresets と同一の検証済み値)。
        private static readonly int[] DecimationRankPresets = { 7500, 10000, QuestLimits.MediumPolygons, QuestLimits.PoorPolygons };
        private static readonly GUIContent[] DecimationRankLabels =
        {
            new GUIContent("Excellent 7,500", "最も厳しい目標。7,500ポリゴン以下(顔・髪を強く保護すると到達できないことがあります)"),
            new GUIContent("Good 10,000", "10,000ポリゴン以下"),
            new GUIContent("Medium 15,000", "Questの既定表示ランク。15,000ポリゴン以下"),
            new GUIContent("Poor 20,000", "Poor 圏内の上限。20,000ポリゴン以下(まずはここを目標にすると崩れにくい)"),
        };

        // Questマテリアル状態バッジ色(旧QuestConverterウィンドウの DrawMaterialBadges と同一値)。
        private static readonly Color BadgeTransparentColor = new Color(0.95f, 0.55f, 0.25f);
        private static readonly Color BadgeCutoutColor = new Color(0.85f, 0.72f, 0.25f);
        private static readonly Color BadgeParticleColor = new Color(0.3f, 0.72f, 0.85f);
        private static readonly Color BadgeAnimationColor = new Color(0.5f, 0.62f, 0.95f);
        private static readonly Color BadgeTmpColor = new Color(0.8f, 0.5f, 0.85f);
        private static readonly Color BadgeBrokenColor = new Color(0.95f, 0.35f, 0.35f);
        private static readonly Color BadgeMobileColor = new Color(0.35f, 0.78f, 0.42f);
        private static readonly Color BadgeComponentColor = new Color(0.95f, 0.55f, 0.7f);

        // 状態バッジ用の遅延生成スタイル(ドメインリロードでnullに戻るため都度null判定で再生成)。
        private static GUIStyle _badgeLabel;
        private static GUIStyle BadgeStyle
        {
            get
            {
                if (_badgeLabel == null) _badgeLabel = new GUIStyle(EditorStyles.miniBoldLabel) { richText = false };
                return _badgeLabel;
            }
        }

        /// <summary>短い色付きバッジを1つ描画する(旧ウィンドウ DrawBadge と同じ体裁)。</summary>
        private static void DrawBadge(string text, Color color, string tooltip)
        {
            Color prev = GUI.color;
            GUI.color = color;
            GUILayout.Label(new GUIContent(text, tooltip), BadgeStyle, GUILayout.ExpandWidth(false));
            GUI.color = prev;
        }

        // ================================================================
        // 1. 衣装・トグル整理(ToggleConsolidator.DetectToggleGroups)
        // ================================================================
        public static bool DrawTogglePanel(GameObject avatarRoot, AvatarStudioSettings s, QuestConvertSettings quest, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }
            if (quest == null) quest = new QuestConvertSettings();

            List<ToggleGroup> groups = cache.GetOrBuild(
                "toggle",
                avatarRoot.GetInstanceID().ToString(),
                () => ToggleConsolidator.DetectToggleGroups(avatarRoot));

            if (groups == null) { EditorGUILayout.HelpBox("トグルの検出に失敗しました(コンソール参照)。", MessageType.Warning); return false; }

            // [1.5.1] 対象オブジェクトが EditorOnly(常時ビルド除外)/ Quest除外(Quest出力時のみ)のトグルは一覧から隠す。
            HashSet<Transform> toggleExcludedRoots = BuildExcludedRoots(avatarRoot, s, cache);
            int toggleHidden = 0;
            groups = FilterBuildExcludedGroups(avatarRoot, groups, toggleExcludedRoots, ref toggleHidden);

            if (groups.Count == 0)
            {
                EditorGUILayout.LabelField("切り替え(トグル)対象の衣装・アクセサリは見つかりませんでした。", EditorStyles.wordWrappedMiniLabel);
                DrawBuildExcludedHiddenNote(toggleHidden);
                return false;
            }

            EditorGUILayout.LabelField(
                "維持=従来どおり切替 / 常時表示=常にON固定(AAOがメッシュ・スロットを統合可能に) / 非表示除去=メッシュごと除去。",
                EditorStyles.wordWrappedMiniLabel);
            DrawBuildExcludedHiddenNote(toggleHidden);

            bool changed = false;
            EnsureList(ref s.toggleChoices);
            EnsureList(ref s.questTextureSizePlan);
            string[] labels = { "トグル維持", "常時表示", "非表示除去(削除)" };

            // サイズ寄与(グループ+アバター単位でキャッシュ)と、参考用の全体推定(テクスチャパネルと同じ "questsize" スロットを共有)。
            SizeEstimateResult est = cache.GetOrBuild("questsize",
                avatarRoot.GetInstanceID() + "|" + HashTexturePlan(s.questTextureSizePlan),
                () => QuestSizeEstimator.Estimate(avatarRoot, quest));
            Dictionary<string, ToggleGroupSizeInfo> sizes = cache.GetOrBuild("togglesize",
                avatarRoot.GetInstanceID() + "|" + HashToggleGroupIds(groups),
                () => QuestSizeEstimator.EstimateToggleGroupExclusiveSizes(avatarRoot, quest, groups));

            // セクション見出しガイド: 現在の推定(圧縮後/展開後)+超過分+「大きい順に削除候補」。
            HashSet<string> deleteCandidateIds = DrawToggleSizeGuide(est, sizes, groups);

            // サイズの大きい順に並べ替えるトグル。
            using (new EditorGUILayout.HorizontalScope())
            {
                _sortToggleBySize = EditorGUILayout.ToggleLeft(
                    new GUIContent("削減量(展開後)の大きい順に並べ替え",
                        "各グループを「非表示除去」にしたときに減る専有アセットの展開後サイズが大きい順に並べます"),
                    _sortToggleBySize, GUILayout.Width(260f));
                GUILayout.FlexibleSpace();
            }

            // 一括操作: 現在の表示状態で全トグルを固定 / すべて維持に戻す。
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("現在の表示状態で固定(全トグル)",
                    "表示中のトグルは「常時表示」、非表示のトグルは「非表示除去(削除)」に一括設定します"), GUILayout.Height(22f)))
                {
                    foreach (ToggleGroup g in groups)
                    {
                        if (g == null || string.IsNullOrEmpty(g.id)) continue;
                        SetToggleChoice(s.toggleChoices, g.id, g.defaultActive ? ToggleLockChoice.LockVisible : ToggleLockChoice.LockHidden);
                    }
                    changed = true;
                }
                if (GUILayout.Button(new GUIContent("すべてトグル維持に戻す",
                    "検出中のトグルをすべて「トグル維持」に戻します"), GUILayout.Height(22f)))
                {
                    foreach (ToggleGroup g in groups)
                    {
                        if (g == null || string.IsNullOrEmpty(g.id)) continue;
                        SetToggleChoice(s.toggleChoices, g.id, ToggleLockChoice.Keep);
                    }
                    changed = true;
                }
            }

            // 並べ替え(サイズ降順)。共有のみ・未計算は 0 として後方へ。
            List<ToggleGroup> ordered = groups;
            if (_sortToggleBySize && sizes != null)
            {
                ordered = new List<ToggleGroup>(groups);
                ordered.Sort((a, b) => ToggleGroupUncompressed(sizes, b).CompareTo(ToggleGroupUncompressed(sizes, a)));
            }

            int shown = 0;
            foreach (ToggleGroup g in ordered)
            {
                if (g == null) continue;
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField(string.Format("...他 {0} 件", ordered.Count - MaxRows), EditorStyles.miniLabel); break; }
                using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string src = string.IsNullOrEmpty(g.source) ? "" : "[" + g.source + "]";
                        string stateText = g.defaultActive ? "表示中" : "非表示";
                        EditorGUILayout.LabelField(new GUIContent(g.label + " " + src + " (現在: " + stateText + ")", g.id),
                            GUILayout.MinWidth(140f), GUILayout.MaxWidth(260f));
                        GUILayout.Label(string.Format("Renderer {0}", g.rendererCount), EditorStyles.miniLabel, GUILayout.Width(84f));

                        int cur = (int)GetToggleChoice(s.toggleChoices, g.id);
                        int next = GUILayout.Toolbar(cur, labels, GUILayout.Width(210f));
                        if (next != cur)
                        {
                            SetToggleChoice(s.toggleChoices, g.id, (ToggleLockChoice)next);
                            changed = true;
                        }
                        GUILayout.FlexibleSpace();
                        DrawTogglePingButton(avatarRoot, g);
                    }
                    DrawToggleGroupSizeChip(g, sizes, deleteCandidateIds);
                }
            }

            DrawToggleProjectedNote(groups, s.toggleChoices);
            EditorGUILayout.LabelField("※ 統合はビルド時(AvatarOptimizer)に適用されます。数値は概算です。", EditorStyles.miniLabel);
            return changed;
        }

        /// <summary>サイズ辞書からグループの専有展開後寄与を返す(未計算・null は 0)。</summary>
        private static float ToggleGroupUncompressed(Dictionary<string, ToggleGroupSizeInfo> sizes, ToggleGroup group)
        {
            if (sizes == null || group == null || string.IsNullOrEmpty(group.id)) return 0f;
            ToggleGroupSizeInfo info;
            return sizes.TryGetValue(group.id, out info) && info != null ? info.exclusiveUncompressedMB : 0f;
        }

        /// <summary>アバターのトグルグループ集合を表すサイズ寄与キャッシュ用シグネチャ。</summary>
        private static string HashToggleGroupIds(List<ToggleGroup> groups)
        {
            if (groups == null) return "0";
            var sb = new System.Text.StringBuilder();
            sb.Append(groups.Count);
            foreach (ToggleGroup g in groups) sb.Append('|').Append(g != null ? g.id : "null");
            return sb.ToString();
        }

        /// <summary>
        /// トグルグループの「非表示除去で減る専有アセット」サイズチップを描画する。
        /// 未計算=計測中、専有なし=共有のみ注記、それ以外=圧縮後/展開後の削減量。削除候補は強調色。
        /// </summary>
        private static void DrawToggleGroupSizeChip(ToggleGroup group, Dictionary<string, ToggleGroupSizeInfo> sizes, HashSet<string> deleteCandidateIds)
        {
            if (sizes == null) { EditorGUILayout.LabelField("非表示除去の削減量: 計測中...", EditorStyles.miniLabel); return; }
            ToggleGroupSizeInfo info;
            if (string.IsNullOrEmpty(group.id) || !sizes.TryGetValue(group.id, out info) || info == null)
            {
                EditorGUILayout.LabelField("非表示除去の削減量: -", EditorStyles.miniLabel);
                return;
            }
            if (info.sharedOnly)
            {
                EditorGUILayout.LabelField("非表示除去で約 圧縮後-0.0 MB / 展開後-0.0 MB(共有テクスチャのみ・単独削減効果小)", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            bool isCandidate = deleteCandidateIds != null && deleteCandidateIds.Contains(group.id);
            Color prev = GUI.color;
            if (isCandidate) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
            EditorGUILayout.LabelField(
                (isCandidate ? "★ " : "") + "非表示除去で約 圧縮後-" + info.exclusiveDownloadMB.ToString("F1") +
                " MB / 展開後-" + info.exclusiveUncompressedMB.ToString("F1") + " MB",
                EditorStyles.wordWrappedMiniLabel);
            GUI.color = prev;
        }

        /// <summary>
        /// トグルセクションの見出しガイドを描画し、「削除候補」として強調するグループIDの集合を返す。
        /// 現在の推定(圧縮後/展開後)と超過分を示し、超過時は専有削減量の大きい順に上限を満たすまで列挙する。
        /// </summary>
        private static HashSet<string> DrawToggleSizeGuide(SizeEstimateResult est, Dictionary<string, ToggleGroupSizeInfo> sizes, List<ToggleGroup> groups)
        {
            var candidateIds = new HashSet<string>(StringComparer.Ordinal);
            if (est == null) return candidateIds;
            if (sizes == null) { EditorGUILayout.LabelField("グループごとの削減量を計測中...", EditorStyles.miniLabel); return candidateIds; }

            bool overC = est.estimatedDownloadMB > QuestLimits.HardDownloadSizeCapMB;
            bool overU = est.estimatedUncompressedMB > QuestLimits.HardUncompressedSizeCapMB;
            string head = "現在の推定: 圧縮後 " + est.estimatedDownloadMB.ToString("F1") + "/" + QuestLimits.HardDownloadSizeCapMB +
                          "MB, 展開後 " + est.estimatedUncompressedMB.ToString("F1") + "/" + QuestLimits.HardUncompressedSizeCapMB + "MB";
            if (!overC && !overU)
            {
                EditorGUILayout.HelpBox(head + "\n両上限とも収まっています。トグルの非表示除去でさらに削減できます。", MessageType.Info);
                return candidateIds;
            }

            float overCompressed = Mathf.Max(0f, est.estimatedDownloadMB - QuestLimits.HardDownloadSizeCapMB);
            float overUncompressed = Mathf.Max(0f, est.estimatedUncompressedMB - QuestLimits.HardUncompressedSizeCapMB);

            var groupById = new Dictionary<string, ToggleGroup>(StringComparer.Ordinal);
            foreach (ToggleGroup g in groups)
            {
                if (g != null && !string.IsNullOrEmpty(g.id) && !groupById.ContainsKey(g.id)) groupById[g.id] = g;
            }
            var ranked = new List<ToggleGroupSizeInfo>();
            foreach (KeyValuePair<string, ToggleGroupSizeInfo> kv in sizes)
            {
                if (kv.Value != null && !kv.Value.sharedOnly && kv.Value.exclusiveUncompressedMB > 0f) ranked.Add(kv.Value);
            }
            ranked.Sort((a, b) => b.exclusiveUncompressedMB.CompareTo(a.exclusiveUncompressedMB));

            var sb = new System.Text.StringBuilder();
            sb.Append(head).Append("\n超過分: ");
            if (overC) sb.Append("圧縮後 +").Append(overCompressed.ToString("F1")).Append("MB ");
            if (overU) sb.Append("展開後 +").Append(overUncompressed.ToString("F1")).Append("MB");
            sb.Append('\n');

            if (ranked.Count == 0)
            {
                sb.Append("単独除去で減らせる専有アセットを持つトグルグループはありません(共有アセット中心)。メッシュ削減・ブレンドシェイプ整理を検討してください。");
                EditorGUILayout.HelpBox(sb.ToString(), MessageType.Warning);
                return candidateIds;
            }

            sb.Append("大きい順に削除候補(非表示除去で減る専有アセット):");
            float cumC = 0f, cumU = 0f;
            bool covered = false;
            int listed = 0;
            const int guideMax = 6;
            for (int i = 0; i < ranked.Count; i++)
            {
                ToggleGroupSizeInfo info = ranked[i];
                bool needMore = (overC && cumC < overCompressed) || (overU && cumU < overUncompressed);
                // 既に選んだ候補の祖先/子孫は、親を非表示にすれば子のサブツリーも一緒に消えるため、
                // 合計に足すと専有アセットを二重計上してしまう(カバー量を過大に見積もる)。候補から除外する。
                bool isCandidate = needMore && !OverlapsSelectedCandidate(info.groupId, candidateIds);
                if (isCandidate)
                {
                    candidateIds.Add(info.groupId);
                    cumC += info.exclusiveDownloadMB;
                    cumU += info.exclusiveUncompressedMB;
                }
                if (listed < guideMax)
                {
                    ToggleGroup g;
                    string label = groupById.TryGetValue(info.groupId, out g) && g != null
                        ? (string.IsNullOrEmpty(g.label) ? info.groupId : g.label) : info.groupId;
                    sb.Append("\n・").Append(label)
                      .Append(" 圧縮後-").Append(info.exclusiveDownloadMB.ToString("F1"))
                      .Append(" / 展開後-").Append(info.exclusiveUncompressedMB.ToString("F1")).Append("MB");
                    if (isCandidate) sb.Append("  ← 候補");
                    listed++;
                }
                if (!covered && (!overC || cumC >= overCompressed) && (!overU || cumU >= overUncompressed)) covered = true;
            }
            sb.Append(covered
                ? "\n→ 上位 " + candidateIds.Count + " 件の非表示除去で両上限をカバーできる見込みです。"
                : "\n→ 全候補を非表示除去してもテクスチャ縮小・メッシュ削減の併用が必要な見込みです。");
            EditorGUILayout.HelpBox(sb.ToString(), MessageType.Warning);
            return candidateIds;
        }

        /// <summary>
        /// groupId(=アバタールート相対のオブジェクトパス)が、既に選んだ候補のいずれかと
        /// 同一または祖先/子孫(サブツリー)関係にあるか。親を非表示にすれば子のサブツリーも一緒に
        /// 消えるため、両方を合計に足すと専有アセットを二重計上してしまう。それを防ぐための重複判定。
        /// </summary>
        private static bool OverlapsSelectedCandidate(string groupId, HashSet<string> selected)
        {
            if (string.IsNullOrEmpty(groupId) || selected == null) return false;
            foreach (string sel in selected)
            {
                if (string.IsNullOrEmpty(sel)) continue;
                if (string.Equals(groupId, sel, StringComparison.Ordinal)) return true;
                // "A/B" は "A/B/C" の祖先(区切りは '/')。どちら向きの入れ子も重複とみなす。
                if (groupId.Length > sel.Length && groupId.StartsWith(sel, StringComparison.Ordinal) && groupId[sel.Length] == '/') return true;
                if (sel.Length > groupId.Length && sel.StartsWith(groupId, StringComparison.Ordinal) && sel[groupId.Length] == '/') return true;
            }
            return false;
        }

        /// <summary>トグルグループの代表オブジェクトをシーンでピン表示するボタン(解決できなければ無効化)。</summary>
        private static void DrawTogglePingButton(GameObject avatarRoot, ToggleGroup group)
        {
            Transform resolved = null;
            if (avatarRoot != null && group != null && group.objectPaths != null)
            {
                foreach (string path in group.objectPaths)
                {
                    resolved = QuestCompat.FindByPath(avatarRoot.transform, path);
                    if (resolved != null) break;
                }
            }
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当オブジェクトをハイライト表示します"), GUILayout.Width(36f)))
                    EditorGUIUtility.PingObject(resolved.gameObject);
            }
        }

        /// <summary>固定するトグル数(表示固定n/非表示固定m)の予測ノートを描画する。</summary>
        private static void DrawToggleProjectedNote(List<ToggleGroup> groups, List<ToggleGroupChoice> choices)
        {
            if (groups == null) return;
            int lockVisible = 0, lockHidden = 0;
            foreach (ToggleGroup g in groups)
            {
                if (g == null || string.IsNullOrEmpty(g.id)) continue;
                ToggleLockChoice choice = GetToggleChoice(choices, g.id);
                if (choice == ToggleLockChoice.LockVisible) lockVisible++;
                else if (choice == ToggleLockChoice.LockHidden) lockHidden++;
            }
            if (lockVisible + lockHidden == 0)
            {
                EditorGUILayout.LabelField("固定するトグルはありません(すべて維持)。", EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.LabelField(
                string.Format("固定するトグル数 {0}(表示固定 {1} / 非表示固定 {2})。表示固定はAAO結合対象、非表示固定はメッシュ削除になります。",
                    lockVisible + lockHidden, lockVisible, lockHidden),
                EditorStyles.wordWrappedMiniLabel);
        }

        private static ToggleLockChoice GetToggleChoice(List<ToggleGroupChoice> list, string groupId)
        {
            foreach (ToggleGroupChoice c in list)
                if (c != null && c.groupId == groupId) return c.choice;
            return ToggleLockChoice.Keep;
        }

        private static void SetToggleChoice(List<ToggleGroupChoice> list, string groupId, ToggleLockChoice choice)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].groupId == groupId)
                {
                    if (choice == ToggleLockChoice.Keep) list.RemoveAt(i); // Keep は既定=エントリ削除
                    else list[i].choice = choice;
                    return;
                }
            }
            if (choice != ToggleLockChoice.Keep)
                list.Add(new ToggleGroupChoice { groupId = groupId, choice = choice });
        }

        // ================================================================
        // 2. SkinnedMesh統合(SkinnedMeshMergePlanner.BuildPlan)
        // ================================================================

        /// <summary>統合モードのドロップダウン表示(SkinnedMeshMergeMode の並び順: しない/顔以外/グループ)。</summary>
        private static readonly string[] SmrMergeModeLabels = { "統合しない", "顔以外を統合(推奨)", "グループ指定で統合" };

        /// <summary>グループ指定モードの行ごとの選択肢(0=統合しない、1..8=グループn)。</summary>
        private static readonly string[] SmrGroupChoiceLabels =
        {
            "統合しない", "グループ1", "グループ2", "グループ3", "グループ4",
            "グループ5", "グループ6", "グループ7", "グループ8",
        };

        public static bool DrawSkinnedMeshMergePanel(GameObject avatarRoot, AvatarStudioSettings s, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }

            bool changed = false;
            EnsureList(ref s.skinnedMeshMergeOptOutPaths);
            EnsureList(ref s.skinnedMeshMergeOverdrawTrimPaths);
            EnsureList(ref s.skinnedMeshMergeMaterialAnimDisablePaths);
            EnsureList(ref s.smrMergeGroups);

            // モードで次フレームの描画量(表 or 案内 / opt-out or グループ選択)が変わるため、モードは描画前に捕捉する。
            // これで同一フレーム内の Layout/Repaint でコントロール数が食い違うのを防ぐ(反映は次回OnGUIから)。
            SkinnedMeshMergeMode capturedMode = s.mergeSkinnedMeshesMode;

            // モード選択ドロップダウン(常時1コントロール。値変更は次回OnGUIへ反映)。
            int modeIdx = EditorGUILayout.Popup(
                new GUIContent("SkinnedMesh統合",
                    "統合しない / 顔以外を統合(顔以外のSMRを1つへ) / グループ指定(レンダラーをグループ1..8ごとに統合)。顔(口パク・まばたき)は常に自動保護されます。"),
                (int)capturedMode, SmrMergeModeLabels);
            if (modeIdx != (int)capturedMode)
            {
                s.mergeSkinnedMeshesMode = (SkinnedMeshMergeMode)modeIdx;
                changed = true;
                cache.Bump(); // キャッシュ済みのプラン/プレビューを無効化する
            }

            // 無効時: レンダラーごとに同じ「統合しない」行を並べず、1つの案内 + 目立つ有効化ボタンだけを見せる(既存のquick-enable)。
            if (capturedMode == SkinnedMeshMergeMode.None)
            {
                // [S1] 「2まで削減」は顔(自動保護)+統合1本の結果値。どの基準の上限に当たるかは対象で異なるため、
                // PC(SkinnedMesh上限 Excellent 1 / Good 2)と Quest(Poor上限 2)を対象に応じて明示する。
                int pcSmrExcellent = PCRankLimits.GetLimit(PCTargetRank.Excellent, PCRankLimits.PCStat.SkinnedMeshes);
                int pcSmrGood = PCRankLimits.GetLimit(PCTargetRank.Good, PCRankLimits.PCStat.SkinnedMeshes);
                string smrBasis;
                if (s.targetPC && s.targetQuest)
                    smrBasis = "PCのSkinnedMesh上限は Excellent " + pcSmrExcellent + " / Good " + pcSmrGood +
                        "、QuestはPoor上限 " + QuestLimits.PoorSkinnedMeshes + " が基準です";
                else if (s.targetPC)
                    smrBasis = "PCのSkinnedMesh上限は Excellent " + pcSmrExcellent + " / Good " + pcSmrGood + " が基準です";
                else if (s.targetQuest)
                    smrBasis = "QuestのSkinnedMesh上限は Poor " + QuestLimits.PoorSkinnedMeshes + " が基準です";
                else
                    smrBasis = "対象(PC / Quest)を選ぶと基準の上限を表示します";
                EditorGUILayout.HelpBox(
                    "SkinnedMesh統合は無効です。『顔以外を統合』にすると、顔以外の SkinnedMeshRenderer を" +
                    "ビルド時に1つへ統合し、SkinnedMesh数を(顔+統合1本で)概ね2まで削減できます" +
                    "(表示/非表示トグルは無効化されます)。" + smrBasis + "。",
                    MessageType.Info);
                if (GUILayout.Button(new GUIContent("顔以外を統合を有効にする",
                    "顔(口パク・まばたき)以外の SkinnedMeshRenderer を1つへ統合するモードに切り替えます"), GUILayout.Height(28f)))
                {
                    s.mergeSkinnedMeshesMode = SkinnedMeshMergeMode.MergeExceptFace;
                    changed = true;
                    cache.Bump();
                }
                return changed;
            }

            bool byGroup = capturedMode == SkinnedMeshMergeMode.MergeByGroup;
            // [1.5.1] EditorOnly(常時ビルド除外)/ Quest除外(Quest出力時)のレンダラーは統合対象外・一覧非表示・概算除外にする。
            HashSet<Transform> mergeExcludedRoots = BuildExcludedRoots(avatarRoot, s, cache);
            string sig = avatarRoot.GetInstanceID() + "|" + (int)capturedMode + "|" + HashPaths(s.skinnedMeshMergeOptOutPaths)
                + "|" + HashPaths(s.skinnedMeshMergeOverdrawTrimPaths)
                + "|" + HashPaths(s.skinnedMeshMergeMaterialAnimDisablePaths)
                + "|" + (byGroup ? HashGroups(s.smrMergeGroups) : "-")
                + "|" + (s.targetPC ? "PC" : "-")
                + "|" + (s.targetQuest ? "Q" + HashPaths(s.questExcludePaths) : "P");
            // [1.9.0] Quest単独時のみ上描きの「何も描かない」推定を渡す(疑似影/アウトラインは自動で非表示化 → 自動削除して統合の見込み)。
            //   PCを含む場合(PC単独・PC+Quest両対応)は PC が非表示化しないため null(=null スロットのみ自動削除)にする。
            //   両対応時に Quest 推定を使うと、PCでは残る可視の上描き([B])が「自動削除」に倒れてチェックが消え、
            //   PC 側の統合オプトインが塞がれる(=概算がPC実数より少なく出る)。null にして [B] チェックを出せるようにする。
            //   sig に targetPC を含めるのは、Quest対応のまま PC を付け外ししたときにこの推定切り替えを反映させるため。
            System.Func<Material, bool> hiddenOverdrawEstimate = (s.targetQuest && !s.targetPC)
                ? (System.Func<Material, bool>)SkinnedMeshMergePlanner.EstimateHiddenOverdrawForQuest
                : null;
            SkinnedMeshMergePlan plan = cache.GetOrBuild(
                "smr", sig,
                () => SkinnedMeshMergePlanner.BuildPlan(
                    avatarRoot, capturedMode, s.skinnedMeshMergeOptOutPaths, byGroup ? s.smrMergeGroups : null, mergeExcludedRoots,
                    s.skinnedMeshMergeOverdrawTrimPaths, hiddenOverdrawEstimate, s.skinnedMeshMergeMaterialAnimDisablePaths));

            if (plan == null) { EditorGUILayout.HelpBox("SkinnedMesh統合プレビューの計算に失敗しました。", MessageType.Warning); return changed; }

            if (byGroup)
            {
                EditorGUILayout.LabelField(
                    "各レンダラーにグループ(1..8)を割り当てると、グループ単位で1つのメッシュへ統合します。" +
                    "顔は自動保護(割り当て不可)、未割り当ては統合しません。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(string.Format("統合前 SMR {0} → 統合後(概算) {1}", plan.beforeCount, plan.expectedCount), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }

            int shown = 0;
            int mergeHidden = 0;
            foreach (SkinnedMeshMergeRow row in plan.rows)
            {
                if (row == null) continue;
                if (row.isBuildExcluded) { mergeHidden++; continue; } // ビルド除外は一覧から隠す
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                using (new EditorGUILayout.HorizontalScope())
                {
                    Color prev = GUI.color;
                    if (row.isFace) GUI.color = new Color(0.6f, 0.85f, 1f);
                    else if (row.willMerge) GUI.color = new Color(0.6f, 0.9f, 0.6f);
                    EditorGUILayout.LabelField(new GUIContent(row.rendererName, row.rendererPath), GUILayout.MinWidth(120f), GUILayout.MaxWidth(220f));
                    GUI.color = prev;

                    if (byGroup)
                    {
                        if (row.isFace)
                        {
                            GUILayout.Label(new GUIContent("顔(自動保護)", "顔メッシュは常に分離維持され、グループへ割り当てできません"),
                                EditorStyles.miniLabel, GUILayout.Width(110f));
                        }
                        else if (row.canAssign)
                        {
                            int cur = GetSmrGroup(s.smrMergeGroups, row.rendererPath);
                            int next = EditorGUILayout.Popup(cur, SmrGroupChoiceLabels, GUILayout.Width(110f));
                            if (next != cur)
                            {
                                SetSmrGroup(s.smrMergeGroups, row.rendererPath, next);
                                changed = true;
                            }
                        }
                        // 割り当て不可(Cloth/メッシュ無し等)は選択肢を出さず、理由ラベルのみ表示する。
                    }
                    else
                    {
                        // 顔以外を統合: 統合対象になり得る行(顔でない・メッシュあり)だけ opt-out トグルを出す。
                        bool mergeable = !row.isFace && row.willMerge || IsOptedOut(s.skinnedMeshMergeOptOutPaths, row.rendererPath);
                        if (mergeable)
                        {
                            bool optOut = IsOptedOut(s.skinnedMeshMergeOptOutPaths, row.rendererPath);
                            bool newOptOut = GUILayout.Toggle(optOut, "統合しない", GUILayout.Width(90f));
                            if (newOptOut != optOut)
                            {
                                TogglePath(s.skinnedMeshMergeOptOutPaths, row.rendererPath, newOptOut);
                                changed = true;
                            }
                        }
                        // [1.9.0][B] 可視の上描きが残るレンダラーは「上描き削除して統合」を選べる(前髪影などの効果は消える)。
                        if (row.canOverdrawTrim)
                        {
                            bool trim = IsOptedOut(s.skinnedMeshMergeOverdrawTrimPaths, row.rendererPath);
                            bool newTrim = GUILayout.Toggle(trim,
                                new GUIContent("上描き削除して統合", "多重描画の上描きスロットを削除して統合します。前髪影などの重ね描き効果は失われます"),
                                GUILayout.Width(130f));
                            if (newTrim != trim)
                            {
                                TogglePath(s.skinnedMeshMergeOverdrawTrimPaths, row.rendererPath, newTrim);
                                changed = true;
                                cache.Bump();
                            }
                        }
                        // [1.10.0][A] マテリアルアニメーションの波及で統合できないレンダラーは、それを無効化して統合できる(演出は固定)。
                        if (row.canDisableMaterialAnim)
                        {
                            bool disable = IsOptedOut(s.skinnedMeshMergeMaterialAnimDisablePaths, row.rendererPath);
                            bool newDisable = GUILayout.Toggle(disable,
                                new GUIContent("マテリアルアニメ無効化して統合",
                                    "このレンダラーに向いた material.* アニメ(エミッション切り替え等)を複製側で無効化し統合できるようにします。切り替え演出は動かなくなります。元アバターは無改変です"),
                                GUILayout.Width(180f));
                            if (newDisable != disable)
                            {
                                TogglePath(s.skinnedMeshMergeMaterialAnimDisablePaths, row.rendererPath, newDisable);
                                changed = true;
                                cache.Bump();
                            }
                        }
                    }
                    GUILayout.Label(row.reason ?? "", EditorStyles.miniLabel, GUILayout.MinWidth(120f));
                    GUILayout.FlexibleSpace();
                }
            }

            // グループ指定モード: グループごとのまとめ行 + 期待総数の内訳(グループ + 顔 + 未統合)。
            if (byGroup)
            {
                DrawSmrGroupSummary(plan);
            }

            DrawBuildExcludedHiddenNote(mergeHidden);
            EditorGUILayout.LabelField("※ 実際の統合はビルド時(AvatarOptimizer)に行われます。統合後SMR数は概算です。", EditorStyles.miniLabel);
            return changed;
        }

        /// <summary>グループ指定モードの、グループ別まとめ(グループn: m枚→1枚)と期待総数の内訳を描画する。</summary>
        private static void DrawSmrGroupSummary(SkinnedMeshMergePlan plan)
        {
            if (plan == null) return;

            EditorGUILayout.Space(2f);
            int groupTargets = 0; // 2枚以上で実際に統合されるグループ数(各→1枚)
            foreach (SkinnedMeshMergeGroup g in plan.mergeGroups)
            {
                if (g == null) continue;
                int m = g.sourcePaths != null ? g.sourcePaths.Count : 0;
                if (m >= 2)
                {
                    EditorGUILayout.LabelField(string.Format("グループ{0}: {1}枚 → 1枚", g.groupIndex, m), EditorStyles.miniLabel);
                    groupTargets++;
                }
                else
                {
                    EditorGUILayout.LabelField(string.Format("グループ{0}: {1}枚(2枚以上で統合されます)", g.groupIndex, m), EditorStyles.miniLabel);
                }
            }

            int faceN = 0;
            foreach (SkinnedMeshMergeRow row in plan.rows)
            {
                if (row != null && !row.isBuildExcluded && row.isFace) faceN++;
            }
            int unassigned = plan.expectedCount - groupTargets - faceN;
            if (unassigned < 0) unassigned = 0;
            EditorGUILayout.LabelField(
                string.Format("内訳: グループ統合 {0} + 顔(自動保護) {1} + 統合しない {2} = 統合後(概算) {3} 枚",
                    groupTargets, faceN, unassigned, plan.expectedCount),
                EditorStyles.miniLabel);
        }

        /// <summary>smrMergeGroups から path のグループ番号を返す(未割り当ては 0)。</summary>
        private static int GetSmrGroup(List<SmrMergeGroupAssignment> list, string path)
        {
            if (list == null || string.IsNullOrEmpty(path)) return 0;
            foreach (SmrMergeGroupAssignment a in list)
                if (a != null && a.rendererPath == path) return a.groupIndex;
            return 0;
        }

        /// <summary>path のグループ番号を設定する。0(統合しない)は既定なのでエントリを削除する。</summary>
        private static void SetSmrGroup(List<SmrMergeGroupAssignment> list, string path, int groupIndex)
        {
            if (list == null || string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].rendererPath == path)
                {
                    if (groupIndex < 1) list.RemoveAt(i);      // 0=統合しない=既定 → エントリ削除
                    else list[i].groupIndex = groupIndex;
                    return;
                }
            }
            if (groupIndex >= 1) list.Add(new SmrMergeGroupAssignment { rendererPath = path, groupIndex = groupIndex });
        }

        /// <summary>グループ割り当てのハッシュ(キャッシュシグネチャ用。順不同でも同集合は同値になるよう加算合成)。</summary>
        private static int HashGroups(List<SmrMergeGroupAssignment> list)
        {
            if (list == null) return 0;
            int h = 0;
            foreach (SmrMergeGroupAssignment a in list)
            {
                if (a == null) continue;
                int e = (a.rendererPath != null ? a.rendererPath.GetHashCode() : 0) * 31 + a.groupIndex;
                h += e; // 加算合成 = 並び順に依存しない
            }
            return h;
        }

        // ================================================================
        // 3. PhysBone(ComponentRemover.PreviewPhysBoneMerge)
        // ================================================================
        public static bool DrawPhysBonePanel(GameObject avatarRoot, AvatarStudioSettings s, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }

            bool changed = false;
            EnsureList(ref s.physBoneRemovePaths);
            EnsureList(ref s.questPhysBoneNoMergePaths);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool merge = GUILayout.Toggle(s.mergePhysBones,
                    new GUIContent("同じ設定の揺れものをまとめて数を削減",
                        "同一設定のPhysBoneチェーンを1つにまとめ、PhysBoneコンポーネント数を減らします。上限を超えそうなときに有効化してください"),
                    "Button", GUILayout.Width(260f));
                if (merge != s.mergePhysBones) { s.mergePhysBones = merge; changed = true; }
                using (new EditorGUI.DisabledScope(!s.mergePhysBones))
                {
                    bool loose = GUILayout.Toggle(s.physBoneLooseMerge, "設定が異なるチェーンもマージ(先頭の設定に統一)", "Button", GUILayout.Width(260f));
                    if (loose != s.physBoneLooseMerge) { s.physBoneLooseMerge = loose; changed = true; }
                }
            }

            // 変換時と同じ入力(マージ除外パス・Quest除外サブツリー)でプレビューを取る。
            // これらを空/nullにすると、変換では残る/除外されるPhysBoneが概算に反映されず、
            // 8本ゲートを誤って通過(過小評価)したり過剰に警告(過大評価)したりする。
            List<Transform> excludedRoots = s.targetQuest ? ResolveExcludedRoots(avatarRoot, s.questExcludePaths) : null;
            string sig = avatarRoot.GetInstanceID() + "|" + s.physBoneLooseMerge + "|" + HashPaths(s.physBoneRemovePaths)
                + "|" + HashPaths(s.questPhysBoneNoMergePaths) + "|" + (s.targetQuest ? HashPaths(s.questExcludePaths) : "-");
            PhysBonePreview preview = cache.GetOrBuild(
                "physbone", sig,
                () => ComponentRemover.PreviewPhysBoneMerge(
                    avatarRoot,
                    ComponentRemover.CollectPhysBoneTogglePaths(avatarRoot),
                    s.physBoneRemovePaths,
                    s.questPhysBoneNoMergePaths,
                    s.physBoneLooseMerge,
                    excludedRoots));

            if (preview == null)
            {
                EditorGUILayout.HelpBox("PhysBoneプレビューの計算に失敗しました。", MessageType.Warning);
                // プレビュー失敗時も削除指定の一覧・「戻す」だけは出す。ここで return すると
                // 下(通常時)の DrawPhysBoneRemoveList に到達せず、削除にしたPhysBoneを再Activeに戻せなくなるため。
                changed |= DrawPhysBoneRemoveList(avatarRoot, s.physBoneRemovePaths);
                return changed;
            }

            // 選択後に残るコンポーネント数(マージ設定・削除選択を反映した概算)と、対象ごとの上限メーター。
            int projected = s.mergePhysBones ? preview.projectedComponentCount : preview.nonMergedComponentCount;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(string.Format("現在 {0} 個 → 選択後(概算) {1} 個", preview.currentComponentCount, projected), EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    // [S1] 自動選択の基準上限は対象で決める: Quest対象ならQuest上限(8本)、
                    // PC単独なら目標ランクのPCコンポーネント上限(Excellent4/Good8/Medium16/Poor32)。
                    bool autoQuestBasis = s.targetQuest;
                    int autoCap = autoQuestBasis
                        ? QuestLimits.PoorPhysBoneComponents
                        : PCRankLimits.GetLimit(s.pcTargetRank, PCRankLimits.PCStat.PhysBoneComponents);
                    string autoRankName = AvatarStudioDiagnostics.GoalRankNames[Mathf.Clamp((int)s.pcTargetRank, 0, 3)];
                    string autoBasisLabel = autoQuestBasis
                        ? "Quest上限(" + autoCap + "本)"
                        : "PC " + autoRankName + "上限(" + autoCap + "本)";
                    string autoButtonLabel = autoQuestBasis
                        ? "優先度で自動選択(Quest " + autoCap + "本まで)"
                        : "優先度で自動選択(PC " + autoRankName + " " + autoCap + "本まで)";
                    string autoLadder = autoQuestBasis
                        ? QuestPhysBoneLadderText()
                        : "PC ランク別コンポーネント上限: Excellent " + PCRankLimits.GetLimit(PCTargetRank.Excellent, PCRankLimits.PCStat.PhysBoneComponents) +
                          " / Good " + PCRankLimits.GetLimit(PCTargetRank.Good, PCRankLimits.PCStat.PhysBoneComponents) +
                          " / Medium " + PCRankLimits.GetLimit(PCTargetRank.Medium, PCRankLimits.PCStat.PhysBoneComponents) +
                          " / Poor " + PCRankLimits.GetLimit(PCTargetRank.Poor, PCRankLimits.PCStat.PhysBoneComponents);
                    using (new EditorGUI.DisabledScope(preview.rows == null || preview.rows.Count == 0))
                    {
                        if (GUILayout.Button(new GUIContent(autoButtonLabel,
                            "髪・胸などの名前から優先度の高い揺れものを、" + autoBasisLabel +
                            "内で残し、残りを削除にします(現在の削除選択は置き換えられます)。\n" + autoLadder),
                            GUILayout.Width(250f)))
                        {
                            if (AutoSelectPhysBonesForCap(avatarRoot, s, autoCap, autoBasisLabel)) changed = true;
                        }
                    }
                }

                // 対象(PC / Quest)ごとに、選択後コンポーネント数を上限メーター・ランク別ラダー・次ランク目安とあわせて色付き表示する。
                if (s.targetPC)
                {
                    DrawPhysBoneCapLine("PC", projected,
                        PCRankLimits.GetLimit(s.pcTargetRank, PCRankLimits.PCStat.PhysBoneComponents),
                        PCRankLimits.GetLimit(PCTargetRank.Poor, PCRankLimits.PCStat.PhysBoneComponents));
                    DrawPhysBoneRankLadder("PC", projected,
                        PCRankLimits.GetLimit(PCTargetRank.Excellent, PCRankLimits.PCStat.PhysBoneComponents),
                        PCRankLimits.GetLimit(PCTargetRank.Good, PCRankLimits.PCStat.PhysBoneComponents),
                        PCRankLimits.GetLimit(PCTargetRank.Medium, PCRankLimits.PCStat.PhysBoneComponents),
                        PCRankLimits.GetLimit(PCTargetRank.Poor, PCRankLimits.PCStat.PhysBoneComponents));
                    // [LIMITS CLARITY] PhysBone上限の要点(目標/Poor上限とVery Poorの崖)を1行で明示する。
                    EditorGUILayout.LabelField(string.Format(
                        "PhysBone上限: 目標{0} {1} / Poor上限 {2}(超過するとVery Poor: PhysBone・コンタクト・コンストレイントが全停止し得ます)",
                        AvatarStudioDiagnostics.GoalRankNames[Mathf.Clamp((int)s.pcTargetRank, 0, 3)],
                        PCRankLimits.GetLimit(s.pcTargetRank, PCRankLimits.PCStat.PhysBoneComponents),
                        PCRankLimits.GetLimit(PCTargetRank.Poor, PCRankLimits.PCStat.PhysBoneComponents)),
                        EditorStyles.wordWrappedMiniLabel);
                }
                if (s.targetQuest)
                {
                    DrawPhysBoneCapLine("Quest/iOS", projected,
                        QuestLimits.MediumPhysBoneComponents, QuestLimits.PoorPhysBoneComponents);
                    DrawPhysBoneRankLadder("Quest/iOS", projected,
                        QuestExcellentPhysBoneComponents, QuestGoodPhysBoneComponents,
                        QuestLimits.MediumPhysBoneComponents, QuestLimits.PoorPhysBoneComponents);
                    // [LIMITS CLARITY] Quest は Poor上限を超えると分野単位で揺れものが全停止する崖を1行で明示する。
                    EditorGUILayout.LabelField(string.Format(
                        "Quest PhysBone上限: Poor上限 {0}(超過分野があると揺れもの全停止)", QuestLimits.PoorPhysBoneComponents),
                        EditorStyles.wordWrappedMiniLabel);
                }
                if (!s.targetPC && !s.targetQuest)
                    EditorGUILayout.LabelField("対象(PC / Quest)を選ぶと上限メーターを表示します。", EditorStyles.miniLabel);
            }

            // Quest対象で上限超過のときは、モバイルで全PhysBoneが停止する旨を赤く警告する。
            if (s.targetQuest && projected > QuestLimits.PoorPhysBoneComponents)
            {
                EditorGUILayout.HelpBox(
                    "モバイルでは上限超過で全てのPhysBone・コンタクト・コンストレイントが無効化されます(Quest上限 " + QuestLimits.PoorPhysBoneComponents +
                    "本 / 現在の選択後 " + projected + "本)。「優先度で自動選択」か各行の「削除」で " +
                    QuestLimits.PoorPhysBoneComponents + "本以下にしてください。",
                    MessageType.Error);
            }

            DrawBuildExcludedHiddenNote(preview.hiddenExcludedCount);

            if (preview.rows != null)
            {
                int shown = 0;
                foreach (PhysBonePreviewRow row in preview.rows)
                {
                    if (row == null) continue;
                    if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                    // 1行=1 HorizontalScope。中央ラベルは MaxWidth で頭打ちし、削除トグルの直前に
                    // FlexibleSpace を置くことで、グループ行・単独行いずれも削除チェックボックスを
                    // 同じ右端の列に揃える(中央ラベル幅の差でチェックボックス位置がズレないように)。
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (row.isGroup)
                        {
                            int n = row.memberPaths != null ? row.memberPaths.Count : 0;
                            Color prev = GUI.color; GUI.color = new Color(0.6f, 0.9f, 0.6f);
                            EditorGUILayout.LabelField(string.Format("マージ {0}本 → 1本", n),
                                EditorStyles.miniBoldLabel, GUILayout.Width(120f));
                            GUI.color = prev;
                            string label = row.memberLabels != null ? string.Join(", ", row.memberLabels) : "";
                            GUILayout.Label(new GUIContent(Trunc(label, 40), label), EditorStyles.miniLabel, GUILayout.MinWidth(100f), GUILayout.MaxWidth(280f));
                            if (row.looseMerged)
                                GUILayout.Label(new GUIContent("(ルーズ)", "設定が異なるチェーンを先頭へ統一"), EditorStyles.miniLabel, GUILayout.Width(56f));
                            GUILayout.FlexibleSpace();
                            DrawPhysBonePingButton(avatarRoot, row.parentPath);
                            // グループのマージ除外(このグループを1本へまとめず、各PhysBoneを残す)
                            bool noMerge = AllRemoved(s.questPhysBoneNoMergePaths, row.memberPaths);
                            bool newNoMerge = GUILayout.Toggle(noMerge, new GUIContent("マージしない", "このグループを1本へ統合せず、各PhysBoneを個別に残します"), GUILayout.Width(90f));
                            if (newNoMerge != noMerge) { SetAllRemoved(s.questPhysBoneNoMergePaths, row.memberPaths, newNoMerge); changed = true; cache.Bump(); }
                            // [S4] 残す(右端の固定列)。チェック=残す(稼働)に統一。UIのみ反転し、内部は physBoneRemovePaths のまま。
                            bool keep = !AllRemoved(s.physBoneRemovePaths, row.memberPaths);
                            bool newKeep = GUILayout.Toggle(keep, new GUIContent("残す", "オフにするとこのグループのPhysBoneをすべて削除します(揺れなくなります)"), GUILayout.Width(52f));
                            if (newKeep != keep) { SetAllRemoved(s.physBoneRemovePaths, row.memberPaths, !newKeep); changed = true; }
                        }
                        else
                        {
                            GUILayout.Label("単独", EditorStyles.miniLabel, GUILayout.Width(120f));
                            GUILayout.Label(new GUIContent(Trunc(row.singlePath ?? "", 40), row.singlePath), EditorStyles.miniLabel, GUILayout.MinWidth(100f), GUILayout.MaxWidth(280f));
                            if (!string.IsNullOrEmpty(row.skipReason))
                                GUILayout.Label(new GUIContent(Trunc(row.skipReason, 24), row.skipReason), EditorStyles.miniLabel, GUILayout.Width(150f));
                            GUILayout.FlexibleSpace();
                            DrawPhysBonePingButton(avatarRoot, row.singlePath);
                            // 手動でマージ除外した(=強制的に単独へ分離した)PhysBoneは、ここで解除して再びマージ可能に戻せる。
                            if (IsOptedOut(s.questPhysBoneNoMergePaths, row.singlePath))
                            {
                                bool newNoMerge = GUILayout.Toggle(true, new GUIContent("マージしない", "このPhysBoneをマージ対象へ戻すにはチェックを外します"), GUILayout.Width(90f));
                                if (!newNoMerge) { TogglePath(s.questPhysBoneNoMergePaths, row.singlePath, false); changed = true; cache.Bump(); }
                            }
                            // [S4] 残す(右端の固定列)。チェック=残す(稼働)に統一。UIのみ反転し、内部は physBoneRemovePaths のまま。
                            bool keep = !IsOptedOut(s.physBoneRemovePaths, row.singlePath);
                            bool newKeep = GUILayout.Toggle(keep, new GUIContent("残す", "オフにするとこのPhysBoneを削除します(揺れなくなります)"), GUILayout.Width(52f));
                            if (newKeep != keep) { TogglePath(s.physBoneRemovePaths, row.singlePath, !newKeep); changed = true; }
                        }
                    }
                }
            }

            // 「削除」にしたPhysBoneは共有プランナー(ComponentRemover.PlanPhysBoneMerge)が
            // 「存在しないもの」として扱いプレビュー行から除くため、上の表からは消える。
            // その状態ではチェックを外す先が無く再Activeにできないので、削除指定を別枠で一覧し
            // 「戻す」で復元できるようにする(旧QuestConverterウィンドウと同じ round-trip 対策)。
            changed |= DrawPhysBoneRemoveList(avatarRoot, s.physBoneRemovePaths);

            // [1.11.0][D][C] Avatar Dynamics 5制約メーター + コンタクト(残す/削除・自動選定)
            changed |= DrawDynamicsMetersAndContacts(avatarRoot, s, preview, cache);

            // [S1] Quest上限の注記は PC単独(Quest非対象)では意味がないため隠す。共通の注意だけ常時表示する。
            string physBoneNote =
                "Transform数は目安です(VRChatの正式カウントとは一致しません)。「残す」を外した PhysBone は変換後アバターから除かれ、残りは全て残ります。";
            if (s.targetQuest)
                physBoneNote += "モバイルは上限が厳しいため、Quest対象では上限内(" + QuestLimits.PoorPhysBoneComponents + "本)に収めてください。";
            physBoneNote += "\n※ PhysBoneマージは本ツール独自実装です(AAOのMerge PhysBoneは未使用)。";
            EditorGUILayout.HelpBox(physBoneNote, MessageType.None);
            return changed;
        }

        /// <summary>
        /// 削除指定(physBoneRemovePaths)の全エントリを一覧し、各行に「戻す」ボタンを出す。変更があれば true。
        /// 共有プランナーは削除指定分をプレビュー行・予測数から除く(ComponentRemover.PlanPhysBoneMerge)ため、
        /// この一覧が無いと一度「削除」にしたPhysBoneを再びActive(残す)へ戻せない。
        /// 現在のアバターで解決できないパス(改名・別アバター等)は黄色の「(見つかりません)」を添える。
        /// </summary>
        private static bool DrawPhysBoneRemoveList(GameObject avatarRoot, List<string> removePaths)
        {
            if (removePaths == null || removePaths.Count == 0) return false;

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("削除指定(変換時に削除され、揺れなくなります。「戻す」で復元できます)", EditorStyles.miniBoldLabel);

            Color defaultColor = GUI.color;
            int restoreIndex = -1;
            int shown = 0;
            for (int i = 0; i < removePaths.Count; i++)
            {
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                string path = removePaths[i];
                string shownPath = string.IsNullOrEmpty(path) ? "(空)" : path;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent("・" + Trunc(shownPath, 60), shownPath),
                        EditorStyles.miniLabel, GUILayout.MinWidth(120f), GUILayout.MaxWidth(360f));
                    if (!PhysBoneIdentityPathExists(avatarRoot, path))
                    {
                        GUI.color = AvatarStudioUI.NoteYellowColor;
                        EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUI.color = defaultColor;
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("戻す", "削除指定を解除します(プレビューの一覧に戻ります)"), GUILayout.Width(52f)))
                        restoreIndex = i;
                }
            }

            if (restoreIndex >= 0)
            {
                removePaths.RemoveAt(restoreIndex);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 削除指定パスが現在のアバター上で実在するか(旧ウィンドウの PhysBoneIdentityPathExists と同一規則)。
        /// GetPhysBoneIdentityPath は 対象GameObjectのPhysBoneが1個なら "#序数" 無しの素のパス、
        /// 2個以上なら "パス#序数" を返す。RemoveSelectedPhysBones が素の文字列で厳密照合するため、
        /// ここでも同じ規則で判定する(緩い一致だと変換で削除されないのに「解決可能」と過大報告してしまう)。
        /// </summary>
        private static bool PhysBoneIdentityPathExists(GameObject avatarRoot, string identityPath)
        {
            if (avatarRoot == null || identityPath == null) return false;

            Transform whole = QuestCompat.FindByPath(avatarRoot.transform, identityPath);
            if (whole != null)
            {
                // パス全体が解決できた = "#序数" 無しの識別パス。PhysBoneがちょうど1個のときのみ一致する。
                return whole.GetComponents<VRC.Dynamics.VRCPhysBoneBase>().Length == 1;
            }

            int hash = identityPath.LastIndexOf('#');
            if (hash < 0) return false;
            if (!int.TryParse(identityPath.Substring(hash + 1), out int pbIndex) || pbIndex < 0) return false;
            Transform target = QuestCompat.FindByPath(avatarRoot.transform, identityPath.Substring(0, hash));
            if (target == null) return false;
            // "#序数" 付きは PhysBoneが2個以上のときにのみ生成される
            int count = target.GetComponents<VRC.Dynamics.VRCPhysBoneBase>().Length;
            return count > 1 && pbIndex < count;
        }

        /// <summary>PhysBone識別パス(グループは親パス、単独は識別パス)を解決してTransformをPing表示するボタン。</summary>
        private static void DrawPhysBonePingButton(GameObject avatarRoot, string identityPath)
        {
            Transform resolved = ResolvePhysBoneIdentityTransform(avatarRoot, identityPath);
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当オブジェクトをハイライト表示します"), GUILayout.Width(36f)))
                    EditorGUIUtility.PingObject(resolved.gameObject);
            }
        }

        /// <summary>識別パス(相対パス。同一GameObjectに複数ある場合は "#序数" 付き)からTransformを解決する(見つからなければnull)。</summary>
        private static Transform ResolvePhysBoneIdentityTransform(GameObject avatarRoot, string identityPath)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(identityPath)) return null;
            Transform direct = QuestCompat.FindByPath(avatarRoot.transform, identityPath);
            if (direct != null) return direct;
            int hash = identityPath.LastIndexOf('#');
            if (hash < 0) return null;
            return QuestCompat.FindByPath(avatarRoot.transform, identityPath.Substring(0, hash));
        }

        /// <summary>
        /// 選択後コンポーネント数 n を対象の上限とあわせて色付き表示する
        /// (目標以下=緑 / 上限以下=黄 / 上限超過=赤)。
        /// </summary>
        private static void DrawPhysBoneCapLine(string label, int n, int goalMax, int hardMax)
        {
            Color color = n <= goalMax ? new Color(0.6f, 0.9f, 0.6f)
                : n <= hardMax ? new Color(1f, 0.85f, 0.35f)
                : AvatarStudioDiagnostics.OverLimitColor;
            Color prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(
                string.Format("{0}: 選択後コンポーネント数 {1} 個 / {0}上限 {2}(目標 {3})", label, n, hardMax, goalMax),
                EditorStyles.miniBoldLabel);
            GUI.color = prev;
        }

        /// <summary>
        /// 選択後コンポーネント数 n を、対象のランク別コンポーネント上限ラダー(1行)と、
        /// 次のランクまでの削減目安つきで色付き表示する。到達ランクで色分けする
        /// (Excellent/Good=緑 / Medium/Poor=黄 / Poor超過=赤)。ceilings は昇順(excellent≤good≤medium≤poor)。
        /// </summary>
        private static void DrawPhysBoneRankLadder(string label, int n, int excellent, int good, int medium, int poor)
        {
            EditorGUILayout.LabelField(
                string.Format("{0} ランク別コンポーネント上限: Excellent {1} / Good {2} / Medium {3} / Poor {4}",
                    label, excellent, good, medium, poor),
                EditorStyles.miniLabel);

            int[] ceil = { excellent, good, medium, poor };
            string[] names = { "Excellent", "Good", "Medium", "Poor" };
            int achieved = 4; // Poor 超過
            for (int i = 0; i < 4; i++) { if (n <= ceil[i]) { achieved = i; break; } }

            string guidance;
            Color color;
            if (achieved == 0)
            {
                guidance = string.Format("{0}: {1} 圏内({2}個以下)", label, names[0], ceil[0]);
                color = new Color(0.6f, 0.9f, 0.6f);
            }
            else
            {
                int next = achieved == 4 ? 3 : achieved - 1; // 一つ上のランク(超過時は Poor 復帰)
                guidance = string.Format("{0}: 現在{1}個 → {2}({3}個以下)まであと{4}個削減",
                    label, n, names[next], ceil[next], n - ceil[next]);
                color = achieved <= 1 ? new Color(0.6f, 0.9f, 0.6f)
                    : achieved == 4 ? AvatarStudioDiagnostics.OverLimitColor
                    : new Color(1f, 0.85f, 0.35f);
            }

            Color prevGuide = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(guidance, EditorStyles.miniBoldLabel);
            GUI.color = prevGuide;
        }

        /// <summary>Quest(Android)PhysBoneコンポーネントのランク別上限を1行にまとめた文字列(Medium/Poorは公開定数)。</summary>
        private static string QuestPhysBoneLadderText()
        {
            return string.Format("Quest ランク別コンポーネント上限: Excellent {0} / Good {1} / Medium {2} / Poor {3}",
                QuestExcellentPhysBoneComponents, QuestGoodPhysBoneComponents,
                QuestLimits.MediumPhysBoneComponents, QuestLimits.PoorPhysBoneComponents);
        }

        /// <summary>
        /// [1.11.0][D][C] Avatar Dynamics の5制約(コンポーネント/影響Transform/コライダー/衝突チェック/コンタクト)の
        /// 使用量メーターを、対象(Quest優先、無ければPC)に合わせて色付き表示し、続けてコンタクトの残す/削除一覧と
        /// 「頭・手優先で自動選定」ボタンを描画する。変更があれば true。
        /// PhysBoneの4項目は現在の選択(削除指定を反映したプレビュー行=残る揺れもの)から、コンタクトは
        /// 削除指定を反映して見積もる。SDKと同じ式を使うが表示は概算(正式カウントとは端数が異なることがある)。
        /// </summary>
        private static bool DrawDynamicsMetersAndContacts(GameObject avatarRoot, AvatarStudioSettings s, PhysBonePreview preview, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null || preview == null) return false;
            bool changed = false;
            EnsureList(ref s.contactRemovePaths);

            // 対象の上限(Quest優先。PC単独なら目標ランク)。両方非対象なら Quest 表示にフォールバック。
            bool questBasis = s.targetQuest || !s.targetPC;
            AvatarDynamicsLimits limits = questBasis ? AvatarDynamicsLimits.Quest() : AvatarDynamicsLimits.Pc(s.pcTargetRank);
            string basisName = questBasis ? "Quest" : "PC " + AvatarStudioDiagnostics.GoalRankNames[Mathf.Clamp((int)s.pcTargetRank, 0, 3)];

            List<Transform> excludedRoots = s.targetQuest ? ResolveExcludedRoots(avatarRoot, s.questExcludePaths) : null;

            // --- PhysBone 4項目の使用量(残る揺れもの = プレビュー行。削除指定は既にプレビューへ反映済み)---
            var map = AvatarDynamicsCost.BuildPhysBoneMap(avatarRoot);
            var colliders = AvatarDynamicsCost.BuildAvatarColliderSet(avatarRoot, excludedRoots);
            var units = AvatarDynamicsCost.BuildKeptUnits(preview.rows, map, s.mergePhysBones, p => true);
            AvatarDynamicsUsage usage = AvatarDynamicsCost.ComputeUsageForUnits(units, avatarRoot.transform, colliders);

            // --- コンタクト一覧(残す/削除)---
            ContactPreview contacts = cache.GetOrBuild("contacts",
                avatarRoot.GetInstanceID() + "|" + (s.targetQuest ? HashPaths(s.questExcludePaths) : "-"),
                () => ContactSelection.PreviewContacts(avatarRoot, excludedRoots));

            var contactRemoveSet = new HashSet<string>(s.contactRemovePaths, StringComparer.Ordinal);
            int contactsKept = 0;
            if (contacts != null && contacts.rows != null)
            {
                foreach (ContactPreviewRow row in contacts.rows)
                {
                    if (row != null && row.counted && !string.IsNullOrEmpty(row.path) && !contactRemoveSet.Contains(row.path)) contactsKept++;
                }
            }
            usage.contacts = contactsKept;

            // --- 5制約メーター ---
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Avatar Dynamics 5制約(" + basisName + "上限。どれか1つでも超過すると実機で揺れもの・コンタクト・コンストレイントが全停止)", EditorStyles.boldLabel);
                DrawDynamicsMeterLine("PhysBoneコンポーネント", usage.physBoneComponents, limits.physBoneComponents);
                DrawDynamicsMeterLine("影響Transform", usage.physBoneTransforms, limits.physBoneTransforms);
                DrawDynamicsMeterLine("コライダー", usage.physBoneColliders, limits.physBoneColliders);
                DrawDynamicsMeterLine("衝突チェック", usage.physBoneCollisionChecks, limits.physBoneCollisionChecks);
                DrawDynamicsMeterLine("コンタクト", usage.contacts, limits.contacts);
                EditorGUILayout.LabelField("PhysBoneの3項目(影響Transform・コライダー・衝突チェック)はSDKと同じ式による概算です。", EditorStyles.wordWrappedMiniLabel);
            }

            // --- コンタクト(VRCContact)残す/削除 + 自動選定 ---
            if (contacts != null && contacts.rows != null && contacts.rows.Count > 0)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(string.Format("コンタクト(残す {0} / 上限 {1})", contactsKept, limits.contacts), EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("頭・手優先で自動選定(" + limits.contacts + "以内)",
                            "頭なで等のレシーバーを優先して残し、コンタクト数を " + limits.contacts + " 以内へ絞ります(ローカル専用は無料のため常に残します)。"),
                            GUILayout.Width(240f)))
                        {
                            List<string> newRemove = ContactSelection.AutoSelectContacts(contacts, limits.contacts, out int keptC, out bool wasBinding);
                            bool ok = EditorUtility.DisplayDialog("コンタクト自動選定",
                                "頭・手系のレシーバーを優先して残し、コンタクト数を " + limits.contacts + " 以内へ絞ります。\n\n" +
                                "残すカウント対象: " + keptC + " / " + limits.contacts +
                                (wasBinding ? "\n(上限を超えたコンタクトを削除にしました)" : "\n(すべて上限内です)") +
                                "\n\n設定しますか?",
                                "設定する", "キャンセル");
                            if (ok)
                            {
                                s.contactRemovePaths.Clear();
                                s.contactRemovePaths.AddRange(newRemove);
                                changed = true;
                            }
                        }
                    }
                    DrawBuildExcludedHiddenNote(contacts.hiddenExcludedCount);

                    int shownC = 0;
                    foreach (ContactPreviewRow row in contacts.rows)
                    {
                        if (row == null || string.IsNullOrEmpty(row.path)) continue;
                        if (shownC++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(row.isReceiver ? "受信" : "送信", EditorStyles.miniLabel, GUILayout.Width(40f));
                            GUILayout.Label(new GUIContent(Trunc(row.path, 46), row.path), EditorStyles.miniLabel, GUILayout.MinWidth(100f), GUILayout.MaxWidth(300f));
                            if (!row.counted)
                                GUILayout.Label(new GUIContent("無料(ローカル)", "ローカル専用コンタクトはカウント対象外(常に残ります)"), EditorStyles.miniLabel, GUILayout.Width(96f));
                            GUILayout.FlexibleSpace();
                            DrawPhysBonePingButton(avatarRoot, row.path);
                            using (new EditorGUI.DisabledScope(!row.counted))
                            {
                                bool keep = !IsOptedOut(s.contactRemovePaths, row.path);
                                bool newKeep = GUILayout.Toggle(keep, new GUIContent("残す", "オフにするとこのコンタクトを変換時に削除します"), GUILayout.Width(52f));
                                if (newKeep != keep) { TogglePath(s.contactRemovePaths, row.path, !newKeep); changed = true; }
                            }
                        }
                    }
                }
            }

            return changed;
        }

        /// <summary>1制約分のメーター行(上限以下=緑 / 超過=赤)。値・上限を色付きで表示する。</summary>
        private static void DrawDynamicsMeterLine(string label, int value, int limit)
        {
            bool over = value > limit;
            Color color = over ? AvatarStudioDiagnostics.OverLimitColor : new Color(0.6f, 0.9f, 0.6f);
            Color prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(string.Format("{0}: {1} / 上限 {2}{3}", label, value, limit, over ? "  超過!" : ""), EditorStyles.miniBoldLabel);
            GUI.color = prev;
        }

        /// <summary>
        /// 名前の優先度(髪・胸など)が高い順に、指定した上限 cap 本内で残すPhysBoneを自動選択する。
        /// cap は対象で決まる(Quest対象=Quest上限8 / PC単独=目標ランクのPCコンポーネント上限)。
        /// 本ツールはKeepAll方式のため、選ばれなかったPhysBoneを physBoneRemovePaths へ登録して削除にする
        /// (= 上位 cap 本の「まとまり」だけが残るように削除リストを作り直す)。変更があれば true。
        /// basisLabel はダイアログに出す基準の説明(例: 「Quest上限(8本)」「PC Good上限(8本)」)。
        /// </summary>
        private static bool AutoSelectPhysBonesForCap(GameObject avatarRoot, AvatarStudioSettings s, int cap, string basisLabel)
        {
            if (avatarRoot == null || s == null) return false;
            EnsureList(ref s.physBoneRemovePaths);

            // 全PhysBoneを採点対象にするため、削除指定を空にしたプレビューを取り直す
            // (削除済みは行に現れないため、削除選択の入ったキャッシュ済みプレビューは使わない)。
            PhysBonePreview full;
            try
            {
                // 変換時と同じ入力(マージ除外パス・Quest除外サブツリー)で採点する。
                // 空/nullにすると、変換では別扱い/除外されるPhysBoneが行構成に反映されず、
                // 「上位8本」の残し集合が実際の変換結果と一致しなくなる(誤ったPhysBoneが残る)。
                full = ComponentRemover.PreviewPhysBoneMerge(
                    avatarRoot,
                    ComponentRemover.CollectPhysBoneTogglePaths(avatarRoot),
                    new List<string>(),
                    s.questPhysBoneNoMergePaths,
                    s.physBoneLooseMerge,
                    ResolveExcludedRoots(avatarRoot, s.questExcludePaths));
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA AvatarStudio] PhysBone自動選択の計算に失敗しました: " + ex);
                return false;
            }
            if (full == null || full.rows == null || full.rows.Count == 0)
            {
                EditorUtility.DisplayDialog("優先度で自動選択", "対象となるPhysBoneが見つかりませんでした。", "OK");
                return false;
            }

            // 優先度昇順(小さいほど高優先)。同点はグループ優先、さらにメンバー数が多い順(QuestConverterと同一)。
            var sorted = new List<PhysBonePreviewRow>();
            foreach (PhysBonePreviewRow row in full.rows) if (row != null) sorted.Add(row);
            sorted.Sort((a, b) =>
            {
                int c = a.priorityScore.CompareTo(b.priorityScore);
                if (c != 0) return c;
                if (a.isGroup != b.isGroup) return a.isGroup ? -1 : 1;
                int ac = a.memberPaths != null ? a.memberPaths.Count : 0;
                int bc = b.memberPaths != null ? b.memberPaths.Count : 0;
                return bc.CompareTo(ac);
            });

            // [1.11.0][A] コンポーネント数だけでなく、影響Transform数・コライダー数・衝突チェック数の
            // 4上限をすべて満たすよう貪欲に残す行を選ぶ(SDKと同じ式でマージ後の形を見積もる)。
            // 上限を超える行は選ばず、後続のより小さい行が入る余地を残す(best-fit)。
            AvatarDynamicsLimits limits = s.targetQuest
                ? AvatarDynamicsLimits.Quest()
                : AvatarDynamicsLimits.Pc(s.pcTargetRank);
            // メーター(DrawDynamicsMetersAndContacts)と同じコライダー集合で見積もるため、Quest除外サブツリーを渡す。
            List<Transform> excludedRoots = s.targetQuest ? ResolveExcludedRoots(avatarRoot, s.questExcludePaths) : null;
            List<PhysBonePreviewRow> selectedRows = AvatarDynamicsCost.GreedySelectRows(
                avatarRoot, sorted, s.mergePhysBones, limits, excludedRoots, out AvatarDynamicsUsage keptUsage, out string binding);

            var keepPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (PhysBonePreviewRow row in selectedRows) AddRowPaths(row, keepPaths);

            // 残す集合に入らなかった全PhysBoneを削除指定にする(= physBoneRemovePaths を作り直す)。
            var newRemove = new List<string>();
            foreach (PhysBonePreviewRow row in sorted)
            {
                var rowPaths = new List<string>();
                AddRowPaths(row, rowPaths);
                foreach (string p in rowPaths)
                    if (!keepPaths.Contains(p) && !newRemove.Contains(p)) newRemove.Add(p);
            }

            if (SamePathSet(s.physBoneRemovePaths, newRemove))
            {
                EditorUtility.DisplayDialog("優先度で自動選択",
                    "既に上限内の選択になっています(変更はありません)。", "OK");
                return false;
            }

            bool ok = EditorUtility.DisplayDialog("優先度で自動選択",
                "名前の優先度が高い順に、" + basisLabel + "を基準に、コンポーネント" + limits.physBoneComponents +
                "・影響Transform " + limits.physBoneTransforms + "・コライダー " + limits.physBoneColliders +
                "・チェック " + limits.physBoneCollisionChecks + " をすべて満たすよう残すPhysBoneを選定します" +
                "(現在の削除選択は置き換えられます)。\n\n" +
                "選定後(概算): コンポーネント " + keptUsage.physBoneComponents + " / 影響Transform " + keptUsage.physBoneTransforms +
                " / コライダー " + keptUsage.physBoneColliders + " / チェック " + keptUsage.physBoneCollisionChecks + "\n" +
                "制約になったのは: " + binding + "\n\n設定しますか?",
                "設定する", "キャンセル");
            if (!ok) return false;

            s.physBoneRemovePaths.Clear();
            s.physBoneRemovePaths.AddRange(newRemove);
            return true;
        }

        /// <summary>
        /// Quest除外パス(questExcludePaths)を元アバター上で解決した除外サブツリーのルート集合。
        /// AvatarQuestConverter は変換時に該当サブツリーを EditorOnly 化してPhysBone処理から外すため、
        /// 元アバターに対するプレビューを変換結果に一致させるには excludedRoots として渡す必要がある
        /// (AvatarQuestConverter.ResolveExcludedRoots と同じ規則。ルート自身を指すパスは無視)。
        /// </summary>
        private static List<Transform> ResolveExcludedRoots(GameObject avatarRoot, List<string> excludePaths)
        {
            var excluded = new List<Transform>();
            if (avatarRoot == null || excludePaths == null) return excluded;
            foreach (string path in excludePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                Transform target = QuestCompat.FindByPath(avatarRoot.transform, path);
                if (target != null && target != avatarRoot.transform) excluded.Add(target);
            }
            return excluded;
        }

        // ================================================================
        // [1.5.1] ビルド除外(EditorOnly / Quest除外)の一覧非表示 共有ヘルパー
        // ================================================================

        /// <summary>
        /// このアバターの Quest除外サブツリー ルート集合を(キャッシュ経由で)解決して返す。
        /// EditorOnly は常にビルド除外だがタグで別途判定するため、ここには含めない。
        /// Quest除外(questExcludePaths)は Quest出力時のみ有効(PC出力では EditorOnly のみで判定される)。
        /// </summary>
        private static HashSet<Transform> BuildExcludedRoots(GameObject avatarRoot, AvatarStudioSettings s, AvatarStudioPreviewCache cache)
        {
            bool quest = s != null && s.targetQuest;
            string key = avatarRoot.GetInstanceID() + "|" + (quest ? "Q|" + HashPaths(s.questExcludePaths) : "P");
            return cache.GetOrBuild("qxroots", key,
                () => quest ? QuestCompat.ResolveExcludedRoots(avatarRoot.transform, s.questExcludePaths) : new HashSet<Transform>());
        }

        /// <summary>groups から、対象オブジェクトがビルド除外(EditorOnly / Quest除外)のトグルを除いたリストを返す(非表示件数を加算)。</summary>
        private static List<ToggleGroup> FilterBuildExcludedGroups(GameObject avatarRoot, List<ToggleGroup> groups, HashSet<Transform> excludedRoots, ref int hiddenCount)
        {
            if (groups == null) return new List<ToggleGroup>();
            var visible = new List<ToggleGroup>(groups.Count);
            foreach (ToggleGroup g in groups)
            {
                if (g != null && !string.IsNullOrEmpty(g.id))
                {
                    Transform gt = QuestCompat.FindByPath(avatarRoot.transform, g.id);
                    if (gt != null && QuestCompat.IsBuildExcluded(gt, avatarRoot.transform, excludedRoots)) { hiddenCount++; continue; }
                }
                visible.Add(g);
            }
            return visible;
        }

        /// <summary>EditorOnly / Quest除外(ビルド除外)で一覧から隠した件数を1行の注記で示す(0件なら何も描画しない)。仕様[C]。</summary>
        private static void DrawBuildExcludedHiddenNote(int hiddenCount)
        {
            if (hiddenCount <= 0) return;
            EditorGUILayout.LabelField(
                string.Format("EditorOnly/Quest除外のため {0} 件を非表示(ビルドに含まれないため)", hiddenCount),
                EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>プレビュー行のPhysBone識別パス(グループは全メンバー、単独は1本)を into へ追加する。</summary>
        private static void AddRowPaths(PhysBonePreviewRow row, ICollection<string> into)
        {
            if (row == null) return;
            if (row.isGroup)
            {
                if (row.memberPaths != null)
                    foreach (string p in row.memberPaths) if (!string.IsNullOrEmpty(p)) into.Add(p);
            }
            else if (!string.IsNullOrEmpty(row.singlePath)) into.Add(row.singlePath);
        }

        /// <summary>2つのパスリストが(順序を無視して)同じ集合かどうか。変更検出に使う。</summary>
        private static bool SamePathSet(List<string> a, List<string> b)
        {
            int ac = a != null ? a.Count : 0;
            int bc = b != null ? b.Count : 0;
            if (ac != bc) return false;
            if (ac == 0) return true;
            var set = new HashSet<string>(a, StringComparer.Ordinal);
            foreach (string x in b) if (!set.Contains(x)) return false;
            return true;
        }

        // ================================================================
        // 4a. PCテクスチャ(PCTexturePlanner.BuildSuggestions)
        // ================================================================
        public static bool DrawPCTexturePanel(GameObject avatarRoot, AvatarStudioSettings s, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }

            bool changed = false;
            EnsureList(ref s.pcTexturePlan);

            float currentMB = cache.GetOrBuild("pctexmem", avatarRoot.GetInstanceID().ToString(),
                () => new float[] { PCTexturePlanner.EstimateTextureMemoryMB(avatarRoot) })?[0] ?? 0f;
            int limit = PCRankLimits.GetLimit(s.pcTargetRank, PCRankLimits.PCStat.TextureMemoryMB);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Color prev = GUI.color;
                if (currentMB > limit) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
                EditorGUILayout.LabelField(string.Format("PCテクスチャメモリ: 約 {0:F1} MB / {1}目標上限 {2} MB",
                    currentMB, AvatarStudioDiagnostics.GoalRankNames[Mathf.Clamp((int)s.pcTargetRank, 0, 3)], limit), EditorStyles.boldLabel);
                GUI.color = prev;
                GUILayout.FlexibleSpace();
            }

            List<PCTexturePlanner.PCTextureSuggestion> suggestions = cache.GetOrBuild(
                "pctex", avatarRoot.GetInstanceID() + "|" + (int)s.pcTargetRank,
                () => PCTexturePlanner.BuildSuggestions(avatarRoot, s.pcTargetRank));

            if (suggestions == null || suggestions.Count == 0)
            {
                EditorGUILayout.LabelField("目標に対する縮小提案はありません(または既に十分小さいです)。", EditorStyles.miniLabel);
                return changed;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(string.Format("縮小提案 {0} 件", suggestions.Count), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("全て縮小計画へ追加", GUILayout.Width(160f)))
                {
                    int applied = PCTexturePlanner.ApplySuggestionsToPlan(suggestions, s.pcTexturePlan);
                    if (applied > 0) changed = true;
                }
                if (currentMB > limit)
                {
                    if (GUILayout.Button(new GUIContent("目標まで自動調整",
                        "目標(" + limit + "MB)を下回るよう、削減効果の大きいテクスチャから順に縮小計画へ登録します(概算・元テクスチャは変更しません)"),
                        GUILayout.Width(140f)))
                    {
                        if (RunPCTextureBudgetFit(suggestions, s.pcTexturePlan, currentMB, limit)) changed = true;
                    }
                }
                using (new EditorGUI.DisabledScope(s.pcTexturePlan.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("縮小計画をクリア", "登録済みの縮小計画をすべて削除します"), GUILayout.Width(120f)))
                    {
                        s.pcTexturePlan.Clear();
                        changed = true;
                    }
                }
            }

            changed |= DrawTextureSuggestionRows(suggestions, s.pcTexturePlan);
            EditorGUILayout.LabelField("※ VRAM は概算です(SDKのテクスチャメモリ値へアンカー)。元テクスチャは変更せず、縮小コピーを生成します。", EditorStyles.miniLabel);
            return changed;
        }

        /// <summary>
        /// 削減効果の大きい順に、目標MBを下回る見込みになるまで縮小計画へ登録する(概算。旧 PCOptimizer の RunTextureBudgetFit を移植)。
        /// 元テクスチャは変更しない。1件以上登録できたら true。
        /// </summary>
        private static bool RunPCTextureBudgetFit(List<PCTexturePlanner.PCTextureSuggestion> suggestions, List<TextureSizePlanEntry> plan, float currentMB, int targetMB)
        {
            var sorted = new List<PCTexturePlanner.PCTextureSuggestion>();
            foreach (PCTexturePlanner.PCTextureSuggestion sug in suggestions)
                if (sug != null && sug.texture != null && sug.suggestedSize > 0) sorted.Add(sug);
            sorted.Sort((a, b) => b.saveMB.CompareTo(a.saveMB));

            float remaining = currentMB;
            int applied = 0;
            foreach (PCTexturePlanner.PCTextureSuggestion sug in sorted)
            {
                if (remaining <= targetMB) break;
                if (UpsertTexturePlan(plan, sug.texture, sug.suggestedSize)) { remaining -= sug.saveMB; applied++; }
            }
            if (applied > 0)
            {
                EditorUtility.DisplayDialog("目標まで自動調整",
                    applied + " 件のテクスチャを縮小計画に登録しました(概算で約 " + remaining.ToString("F1") + " MB 見込み)。\n" +
                    "元テクスチャは変更しません。", "OK");
            }
            return applied > 0;
        }

        private static bool DrawTextureSuggestionRows(List<PCTexturePlanner.PCTextureSuggestion> suggestions, List<TextureSizePlanEntry> plan)
        {
            bool changed = false;
            int shown = 0;
            foreach (PCTexturePlanner.PCTextureSuggestion sug in suggestions)
            {
                if (sug == null || sug.texture == null) continue;
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(sug.texture, typeof(Texture2D), false, GUILayout.MinWidth(120f), GUILayout.MaxWidth(200f));
                    GUILayout.Label(string.Format("{0}→{1}px  -{2:F1}MB", sug.currentSize, sug.suggestedSize, sug.saveMB),
                        EditorStyles.miniLabel, GUILayout.Width(160f));
                    bool planned = IsPlanned(plan, sug.texture, sug.suggestedSize);
                    using (new EditorGUI.DisabledScope(planned))
                    {
                        if (GUILayout.Button(planned ? "追加済" : "追加", GUILayout.Width(64f)))
                        {
                            if (UpsertTexturePlan(plan, sug.texture, sug.suggestedSize)) changed = true;
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
            return changed;
        }

        // ================================================================
        // 4b. Questテクスチャ / ダウンロードサイズ(QuestSizeEstimator.Estimate)
        // ================================================================
        public static bool DrawQuestTexturePanel(GameObject avatarRoot, AvatarStudioSettings s, QuestConvertSettings quest, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }
            if (quest == null) quest = new QuestConvertSettings();

            bool changed = false;
            EnsureList(ref s.questTextureSizePlan);

            string sig = avatarRoot.GetInstanceID() + "|" + HashTexturePlan(s.questTextureSizePlan);
            SizeEstimateResult est = cache.GetOrBuild("questsize", sig, () => QuestSizeEstimator.Estimate(avatarRoot, quest));
            if (est == null) { EditorGUILayout.HelpBox("Questサイズ推定に失敗しました。", MessageType.Warning); return changed; }

            // 圧縮後(10MB)・展開後(40MB)の2上限を並べて表示する。
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Color prev = GUI.color;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (est.overCap) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
                    EditorGUILayout.LabelField(string.Format("圧縮後(概算) 約 {0:F1} MB / 上限 {1} MB(テクスチャ {2:F1} / メッシュ {3:F1} / アニメ {4:F1})",
                        est.estimatedDownloadMB, QuestLimits.HardDownloadSizeCapMB, est.textureDownloadMB, est.meshDownloadMB, est.animationDownloadMB),
                        EditorStyles.boldLabel);
                    GUI.color = prev;
                    GUILayout.FlexibleSpace();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (est.overUncompressedCap) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
                    EditorGUILayout.LabelField(string.Format("展開後(概算) 約 {0:F1} MB / 上限 {1} MB(圧縮後とは独立した上限。テクスチャ {2:F1} / メッシュ {3:F1} / アニメ {4:F1})",
                        est.estimatedUncompressedMB, QuestLimits.HardUncompressedSizeCapMB, est.textureUncompressedMB, est.meshUncompressedMB, est.animationUncompressedMB),
                        EditorStyles.boldLabel);
                    GUI.color = prev;
                    GUILayout.FlexibleSpace();
                }
            }

            // 一括操作: 上限内まで自動調整(いずれかの上限超過見込み時のみ) / 縮小計画をクリア。
            using (new EditorGUILayout.HorizontalScope())
            {
                bool showBudgetFit = est.overCap || est.overUncompressedCap ||
                    est.estimatedDownloadMB > QuestLimits.HardDownloadSizeCapMB ||
                    est.estimatedUncompressedMB > QuestLimits.HardUncompressedSizeCapMB;
                using (new EditorGUI.DisabledScope(!showBudgetFit))
                {
                    if (GUILayout.Button(new GUIContent("上限内まで自動調整(圧縮後10MB・展開後40MB)",
                        "推定サイズが圧縮後" + QuestLimits.HardDownloadSizeCapMB + "MB・展開後" + QuestLimits.HardUncompressedSizeCapMB +
                        "MBの両方に収まるよう、テクスチャの縮小計画を自動で作成します(元テクスチャは変更しません)"),
                        GUILayout.Width(260f)))
                    {
                        if (RunQuestBudgetFit(avatarRoot, quest, s.questTextureSizePlan, est)) changed = true;
                    }
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(s.questTextureSizePlan.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("縮小計画をクリア", "登録済みの縮小計画をすべて削除します"), GUILayout.Width(120f)))
                    {
                        s.questTextureSizePlan.Clear();
                        changed = true;
                    }
                }
            }

            DrawQuestTopTextures(est);

            if (est.suggestions != null && est.suggestions.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("縮小・削減提案", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("テクスチャ縮小提案を全て追加", GUILayout.Width(200f)))
                    {
                        int applied = 0;
                        foreach (SizeSuggestion sug in est.suggestions)
                        {
                            if (sug == null || sug.texture == null || sug.recommendedMaxSize <= 0) continue;
                            if (UpsertTexturePlan(s.questTextureSizePlan, sug.texture, sug.recommendedMaxSize)) applied++;
                        }
                        if (applied > 0) changed = true;
                    }
                }

                int shown = 0;
                foreach (SizeSuggestion sug in est.suggestions)
                {
                    if (sug == null) continue;
                    if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (sug.texture != null)
                        {
                            using (new EditorGUI.DisabledScope(true))
                                EditorGUILayout.ObjectField(sug.texture, typeof(Texture), false, GUILayout.MinWidth(110f), GUILayout.MaxWidth(180f));
                        }
                        GUILayout.Label(new GUIContent(Trunc(sug.description ?? "", 60), sug.description), EditorStyles.miniLabel, GUILayout.MinWidth(160f));
                        if (sug.savingMB > 0f) GUILayout.Label(string.Format("-{0:F1}MB", sug.savingMB), EditorStyles.miniLabel, GUILayout.Width(64f));
                        if (sug.texture != null && sug.recommendedMaxSize > 0)
                        {
                            bool planned = IsPlanned(s.questTextureSizePlan, sug.texture, sug.recommendedMaxSize);
                            using (new EditorGUI.DisabledScope(planned))
                                if (GUILayout.Button(planned ? "追加済" : sug.recommendedMaxSize + "pxへ", GUILayout.Width(80f)))
                                    if (UpsertTexturePlan(s.questTextureSizePlan, sug.texture, sug.recommendedMaxSize)) changed = true;
                        }
                        GUILayout.FlexibleSpace();
                    }
                }
            }

            EditorGUILayout.LabelField("※ すべて概算の目安値です。元テクスチャは変更せず、変換時に縮小コピーを生成します。", EditorStyles.miniLabel);
            return changed;
        }

        /// <summary>ダウンロードサイズの大きいテクスチャ上位を一覧表示する(Android上書きなしは黄色バッジ)。est.textures は降順ソート済み。</summary>
        private static void DrawQuestTopTextures(SizeEstimateResult est)
        {
            List<TextureSizeInfo> textures = est != null ? est.textures : null;
            int total = textures != null ? textures.Count : 0;
            if (total == 0) return;

            int shown = Mathf.Min(total, 12);
            EditorGUILayout.LabelField(string.Format("ダウンロードサイズの大きいテクスチャ 上位 {0}/{1}", shown, total), EditorStyles.miniBoldLabel);

            Color defaultColor = GUI.color;
            for (int i = 0; i < shown; i++)
            {
                TextureSizeInfo info = textures[i];
                if (info == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(info.texture, typeof(Texture), false, GUILayout.MinWidth(110f), GUILayout.MaxWidth(180f));
                    GUILayout.Label(string.Format("{0:F2} MB / {1} / 最大{2}px", info.downloadMB, info.formatLabel, info.currentAndroidMaxSize),
                        EditorStyles.miniLabel, GUILayout.MinWidth(140f));
                    GUILayout.FlexibleSpace();
                    if (!info.hasAndroidOverride)
                    {
                        GUI.color = AvatarStudioUI.NoteYellowColor;
                        GUILayout.Label(new GUIContent("Android上書きなし", "このテクスチャにはAndroid向けインポート上書きが無く、ASTC 6x6相当として見積もっています"),
                            EditorStyles.miniLabel, GUILayout.Width(105f));
                        GUI.color = defaultColor;
                    }
                    else
                    {
                        GUILayout.Label(new GUIContent("Android上書きあり",
                            "元アセットにAndroid向けインポート上書きが設定されています(見積もりは反映済み)。本ツールは元インポート設定を変更しません。"),
                            EditorStyles.miniLabel, GUILayout.Width(105f));
                    }
                }
            }
            GUI.color = defaultColor;
        }

        /// <summary>
        /// 推定サイズが圧縮後10MB・展開後40MBの両上限に収まるよう、テクスチャ縮小計画を自動作成する(旧QuestConverterウィンドウの RunBudgetFit を移植)。
        /// PlanBudgetFit で計画→確認ダイアログ→studioPlan(s.questTextureSizePlan)へ登録する。元テクスチャは変更しない。1件以上登録できたら true。
        /// 上限ちょうどを狙うと見積もり誤差で超えやすいため、5%のマージンを取った目標で計画する。
        /// </summary>
        private static bool RunQuestBudgetFit(GameObject avatarRoot, QuestConvertSettings quest, List<TextureSizePlanEntry> studioPlan, SizeEstimateResult est)
        {
            if (avatarRoot == null || studioPlan == null) return false;
            if (quest == null) quest = new QuestConvertSettings();

            const string title = "上限内まで自動調整(圧縮後10MB・展開後40MB)";
            float targetMB = QuestLimits.HardDownloadSizeCapMB * 0.95f;
            float targetUncompressedMB = QuestLimits.HardUncompressedSizeCapMB * 0.95f;
            List<BudgetFitStep> plan;
            try
            {
                EditorUtility.DisplayProgressBar(title, "縮小プランを計算しています...", 0.2f);
                plan = QuestSizeEstimator.PlanBudgetFit(avatarRoot, quest, targetMB, targetUncompressedMB);
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA AvatarStudio] 自動調整プランの作成に失敗しました: " + ex);
                EditorUtility.DisplayDialog(title, "縮小プランの作成中にエラーが発生しました:\n" + ex.Message, "OK");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (plan == null || plan.Count == 0)
            {
                EditorUtility.DisplayDialog(title,
                    "これ以上テクスチャ縮小で下げられません。\n展開後40MBがメッシュ・ブレンドシェイプ支配の場合はテクスチャ縮小だけでは収まりません。" +
                    "『縮小・削減提案』のQuest除外や、メッシュ・ブレンドシェイプの削減も検討してください。", "OK");
                return false;
            }

            const int dialogStepMax = 12;
            float totalSavingMB = 0f;
            float totalUncompressedSavingMB = 0f;
            var steps = new System.Text.StringBuilder();
            for (int i = 0; i < plan.Count; i++)
            {
                BudgetFitStep step = plan[i];
                if (step == null) continue;
                totalSavingMB += step.savingMB;
                totalUncompressedSavingMB += step.uncompressedSavingMB;
                if (i < dialogStepMax)
                    steps.AppendLine("・" + (step.texture != null ? step.texture.name : "(不明)") + " " +
                        step.fromSize + "px→" + step.toSize + "px (圧縮後-" + step.savingMB.ToString("F1") +
                        "MB / 展開後-" + step.uncompressedSavingMB.ToString("F1") + "MB)");
            }
            if (plan.Count > dialogStepMax) steps.AppendLine("・他 " + (plan.Count - dialogStepMax) + " 件");

            string projection;
            bool stillOver = false;
            if (est != null)
            {
                float projCompressed = Mathf.Max(0f, est.estimatedDownloadMB - totalSavingMB);
                float projUncompressed = Mathf.Max(0f, est.estimatedUncompressedMB - totalUncompressedSavingMB);
                stillOver = projCompressed > QuestLimits.HardDownloadSizeCapMB || projUncompressed > QuestLimits.HardUncompressedSizeCapMB;
                projection = "\n調整後の見込み: 圧縮後 約" + projCompressed.ToString("F1") + " / " + QuestLimits.HardDownloadSizeCapMB +
                             "MB, 展開後 約" + projUncompressed.ToString("F1") + " / " + QuestLimits.HardUncompressedSizeCapMB + "MB";
                if (stillOver)
                    projection += "\n※ テクスチャ縮小だけでは上限に届きません(残りはメッシュ・ブレンドシェイプ支配)。" +
                                  "Quest除外・メッシュ削減・ブレンドシェイプ整理の併用が必要です。";
            }
            else
            {
                projection = "\n合計削減見込み: 圧縮後 約" + totalSavingMB.ToString("F1") + "MB / 展開後 約" +
                             totalUncompressedSavingMB.ToString("F1") + "MB";
            }

            // 縮小フロアに達して上限に届かない場合は、見出しを「収まる」ではなく「可能な範囲で登録」に変える。
            string header = stillOver
                ? "テクスチャを最小まで縮小しても上限(圧縮後" + QuestLimits.HardDownloadSizeCapMB + "MB・展開後" + QuestLimits.HardUncompressedSizeCapMB +
                  "MB)には届きませんが、可能な範囲で次の " + plan.Count + " 件のテクスチャを縮小計画に登録します:\n\n"
                : "推定サイズが上限(圧縮後" + QuestLimits.HardDownloadSizeCapMB + "MB・展開後" + QuestLimits.HardUncompressedSizeCapMB +
                  "MB)以下になるよう、次の " + plan.Count + " 件のテクスチャを縮小計画に登録します:\n\n";

            bool ok = EditorUtility.DisplayDialog(title,
                header + steps + projection + "\n\n" +
                "元テクスチャは変更しません(変換時に縮小コピーを生成します)。「縮小計画をクリア」でいつでも取り消せます。\n\n登録しますか?",
                "登録する", "キャンセル");
            if (!ok) return false;

            int applied = 0;
            foreach (BudgetFitStep step in plan)
            {
                if (step == null || step.texture == null || step.toSize <= 0) continue;
                if (UpsertTexturePlan(studioPlan, step.texture, step.toSize)) applied++;
            }
            EditorUtility.DisplayDialog(title,
                applied + " 件のテクスチャを縮小計画に登録しました(削減見込み 圧縮後 約" + totalSavingMB.ToString("F1") +
                "MB / 展開後 約" + totalUncompressedSavingMB.ToString("F1") + "MB)。\n元テクスチャは変更されません。", "OK");
            return applied > 0;
        }

        // ================================================================
        // 5. ポリゴン削減(Meshia連携)
        //   ポリゴン削減は Meshia Mesh Simplification へ委譲する。目標三角形数を設定し、変換時に複製へ
        //   Cascading コンポーネントを付与する(実際の削減はビルド時=NDMF)。自前の配分計画方式は 1.6.0 で廃止。
        // ================================================================
        public static bool DrawPolygonPanel(GameObject avatarRoot, AvatarStudioSettings s, QuestConvertSettings quest, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }
            if (quest == null) quest = new QuestConvertSettings();

            bool changed = false;

            // 旧バージョンの削減計画が残っていれば移行済みである旨を通知(適用はしない)。
            if (s.questDecimationPlan != null && s.questDecimationPlan.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "ポリゴン削減は Meshia 連携へ移行しました(旧バージョンの削減計画は適用されません)。", MessageType.Info);
            }

            // Meshia(+Modular Avatar)未導入時は導入案内。
            if (!MeshiaCompat.IsCascadingAvailable())
            {
                EditorGUILayout.HelpBox(
                    "ポリゴン削減には Meshia Mesh Simplification(無料)と Modular Avatar の導入が必要です。導入後にこのパネルから設定できます。",
                    MessageType.Info);
                if (GUILayout.Button(new GUIContent("Meshia を導入(VPM)",
                    "Meshia の導入ページを開きます: " + AvatarStudioUI.MeshiaDocsUrl), GUILayout.Width(200f)))
                {
                    Application.OpenURL(AvatarStudioUI.MeshiaDocsUrl);
                }
                return false;
            }

            // 目標ランク(プリセット)+ カスタム。
            int presetIndex = GetDecimationPresetIndex(s.questMeshiaTargetTriangles);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("目標ランク", "到達したいポリゴン数の目安。ボタンで目標三角形数が設定されます"), GUILayout.Width(72f));
                int newIndex = GUILayout.Toolbar(presetIndex, DecimationRankLabels);
                if (newIndex != presetIndex && newIndex >= 0 && newIndex < DecimationRankPresets.Length)
                {
                    s.questMeshiaTargetTriangles = DecimationRankPresets[newIndex];
                    changed = true;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("目標三角形数", GUILayout.Width(84f));
                int newTarget = EditorGUILayout.IntField(s.questMeshiaTargetTriangles, GUILayout.Width(90f));
                if (newTarget < 1) newTarget = 1;
                if (newTarget != s.questMeshiaTargetTriangles) { s.questMeshiaTargetTriangles = newTarget; changed = true; }
            }

            // 現在の三角形数 vs 目標(数え方は診断と同一=EditorOnly/除外を反映)。
            int currentTris = cache.GetOrBuild("polytris", avatarRoot.GetInstanceID().ToString(),
                () => new int[] { PolygonBudgetPlanner.CountCurrentTriangles(avatarRoot, quest) })?[0] ?? 0;
            bool over = currentTris > s.questMeshiaTargetTriangles;
            Color prevColor = GUI.color;
            GUI.color = over ? AvatarStudioUI.OverLimitColor : AvatarStudioUI.UploadOkColor;
            EditorGUILayout.LabelField(
                string.Format("現在の三角形数 {0:N0} / 目標 {1:N0}", currentTris, s.questMeshiaTargetTriangles),
                EditorStyles.wordWrappedLabel);
            GUI.color = prevColor;

            // 選択中アバターに既に Cascading があるか(手動付与・変換済み複製など)。存在判定はアバター単位でキャッシュ。
            bool hasCascading = cache.GetOrBuild("meshiacascading", avatarRoot.GetInstanceID().ToString(),
                () => new bool[] { MeshiaCompat.FindCascading(avatarRoot) != null })?[0] ?? false;
            if (hasCascading)
            {
                Component existing = MeshiaCompat.FindCascading(avatarRoot);
                int cur = existing != null ? MeshiaCompat.GetCascadingTarget(existing) : -1;
                EditorGUILayout.HelpBox(
                    "このアバターには既に Meshia 簡略化が付いています" +
                    (cur > 0 ? "(現在の目標 " + cur.ToString("N0") + " 三角形)" : "") + "。Inspectorで調整できます。",
                    MessageType.Info);
                if (GUILayout.Button(new GUIContent(
                    "Meshiaの目標を更新(目標: " + s.questMeshiaTargetTriangles.ToString("N0") + ")",
                    "既存の Meshia 簡略化コンポーネントの全体目標を更新します"), GUILayout.Width(240f)))
                {
                    if (existing != null) MeshiaCompat.UpdateCascadingTarget(existing, s.questMeshiaTargetTriangles);
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool enable = GUILayout.Toggle(s.questEnableMeshiaSimplification,
                        "変換時に複製へ Meshia 簡略化を追加", "Button", GUILayout.Width(240f));
                    if (enable != s.questEnableMeshiaSimplification) { s.questEnableMeshiaSimplification = enable; changed = true; }
                }
                if (!s.questEnableMeshiaSimplification)
                {
                    EditorGUILayout.LabelField(
                        "オフのときは複製へ Meshia は付与されません(ポリゴン削減は行われません)。", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.LabelField(
                "削減はビルド時(NDMF)に適用されます。エディタの数値には出ませんが、即時実測には反映されます。",
                EditorStyles.wordWrappedMiniLabel);
            return changed;
        }

        /// <summary>目標三角形数がプリセット(7500/10000/15000/20000)のどれかなら添字、なければ-1(カスタム)。</summary>
        private static int GetDecimationPresetIndex(int target)
        {
            for (int i = 0; i < DecimationRankPresets.Length; i++)
                if (DecimationRankPresets[i] == target) return i;
            return -1;
        }

        // ================================================================
        // 6a. PCマテリアルアトラス(PCMaterialAtlasser.PreviewPlan)
        // ================================================================

        /// <summary>アウトライン統合モードの選択肢(OutlineUnifyMode の並び 0=しない / 1=外して統合 / 2=付きに統一 と一致)。</summary>
        private static readonly GUIContent[] PcAtlasOutlineModeLabels =
        {
            new GUIContent("統合しない", "アウトライン有無が異なるマテリアルは統合しません"),
            new GUIContent("外して統合(推奨)", "プレーンlilToonへ揃えます。外す=服の輪郭線が消えますが、瞳・顔に黒縁は付きません"),
            new GUIContent("付きに統一", "アウトライン付き側へ揃えます。付き=輪郭の無かった部分に付きます(瞳・顔は自動回避)"),
        };

        private static readonly GUIContent[] PcAtlasSizeLabels = { new GUIContent("1024"), new GUIContent("2048(推奨)"), new GUIContent("4096") };
        private static readonly int[] PcAtlasSizeValues = { 1024, 2048, 4096 };

        public static bool DrawPCAtlasPanel(GameObject avatarRoot, AvatarStudioSettings s, PCOptimizeSettings pc, AvatarStudioPreviewCache cache)
        {
            if (avatarRoot == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }

            bool changed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                bool enable = GUILayout.Toggle(s.pcEnableAtlas, "PCマテリアルをアトラス統合(実験的)", "Button", GUILayout.Width(230f));
                if (enable != s.pcEnableAtlas) { s.pcEnableAtlas = enable; changed = true; }
            }

            if (!s.pcEnableAtlas || pc == null)
            {
                EditorGUILayout.LabelField("互換マテリアルを1枚のアトラスへ統合し、スロット数・テクスチャメモリを削減します。", EditorStyles.miniLabel);
                return changed;
            }

            EnsureList(ref s.pcAtlasExcludeGuids);

            // アトラス最大サイズ + 統合オプション。いずれも変更で統合プレビューを作り直す。
            using (new EditorGUILayout.HorizontalScope())
            {
                int newMax = EditorGUILayout.IntPopup(new GUIContent("アトラス最大サイズ", "統合後のアトラステクスチャの最大サイズ(px)"),
                    s.pcAtlasMaxSize, PcAtlasSizeLabels, PcAtlasSizeValues, GUILayout.Width(280f));
                if (newMax != s.pcAtlasMaxSize) { s.pcAtlasMaxSize = newMax; changed = true; cache.Bump(); }
                GUILayout.FlexibleSpace();
            }

            bool newColorOnly = EditorGUILayout.ToggleLeft(
                new GUIContent("テクスチャ無し(色だけ)の材質も統合", "メインテクスチャを持たない単色マテリアルも、その色をアトラスのセルへ焼き込んで統合対象にします(見た目は近似)"),
                s.pcAtlasColorOnlyMaterials);
            if (newColorOnly != s.pcAtlasColorOnlyMaterials) { s.pcAtlasColorOnlyMaterials = newColorOnly; changed = true; cache.Bump(); }

            bool newBakeEmission = EditorGUILayout.ToggleLeft(
                new GUIContent("エミッションをアトラスへ焼き込む", "エミッション色/マップをエミッション用アトラスのセルへ焼き込みます(発光を維持)"),
                s.pcAtlasBakeEmissionMask);
            if (newBakeEmission != s.pcAtlasBakeEmissionMask) { s.pcAtlasBakeEmissionMask = newBakeEmission; changed = true; cache.Bump(); }

            bool newIgnoreCull = EditorGUILayout.ToggleLeft(
                new GUIContent("カリング差を無視", "カリング(片面/両面)の違いを無視して統合します。統合後は両面描画(Cull Off)になります"),
                s.pcAtlasIgnoreCull);
            if (newIgnoreCull != s.pcAtlasIgnoreCull) { s.pcAtlasIgnoreCull = newIgnoreCull; changed = true; cache.Bump(); }

            // アウトライン(輪郭線)の扱い: しない / 外して統合(推奨) / 付きに統一。
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("アウトライン",
                    "アトラス統合時のアウトライン(輪郭線)の扱い。外す=服の輪郭線が消える / 付き=輪郭の無かった部分に付く(瞳・顔は自動回避)"),
                    GUILayout.Width(110f));
                int cur = Mathf.Clamp((int)s.pcAtlasOutlineUnifyMode, 0, PcAtlasOutlineModeLabels.Length - 1);
                int next = EditorGUILayout.Popup(cur, PcAtlasOutlineModeLabels, GUILayout.Width(230f));
                if (next != cur)
                {
                    s.pcAtlasOutlineUnifyMode = (OutlineUnifyMode)next;
                    changed = true;
                    cache.Bump(); // モード変更で統合プレビューを作り直す
                }
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.HelpBox(
                "アトラス統合時はマットキャップを外します(UV再配置でマットキャップのマスクが壊れ、白飛びするため)。" +
                "映り込みを残したいマテリアルは『アトラス除外』に指定してください。",
                MessageType.Info);

            // 統合プレビューのキャッシュキーには、プランに影響する全設定(サイズ・単色/エミッション焼き込み・
            // カリング無視・アウトライン・除外GUID)を含める。いずれかの変更で再計算される。
            string atlasSig = avatarRoot.GetInstanceID() + "|" + s.pcEnableAtlas + "|" + s.pcAtlasMaxSize
                + "|" + s.pcAtlasColorOnlyMaterials + "|" + s.pcAtlasBakeEmissionMask + "|" + s.pcAtlasIgnoreCull
                + "|" + (int)s.pcAtlasOutlineUnifyMode + "|" + HashPaths(s.pcAtlasExcludeGuids);
            PCMaterialAtlasser.AtlasPlan plan = cache.GetOrBuild(
                "pcatlas", atlasSig,
                () => PCMaterialAtlasser.PreviewPlan(avatarRoot, pc));

            if (plan == null) { EditorGUILayout.HelpBox("アトラス統合プレビューの計算に失敗しました。", MessageType.Warning); return changed; }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(string.Format("候補マテリアル {0} → 統合後(概算) {1}", plan.candidateMaterialCount, plan.projectedMaterialCount), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }

            if (plan.groups.Count > 0)
            {
                EditorGUILayout.LabelField(string.Format("統合予定グループ {0} 件", plan.groups.Count), EditorStyles.miniBoldLabel);
                int shown = 0;
                foreach (PCMaterialAtlasser.AtlasPlanGroup g in plan.groups)
                {
                    if (g == null) continue;
                    if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                    string members = g.memberNames != null ? string.Join(", ", g.memberNames) : "";
                    string extra = "";
                    if (g.twoSided) extra += " [両面化]";
                    if (g.outlineAdded != null && g.outlineAdded.Count > 0) extra += " [アウトライン付与:" + g.outlineAdded.Count + "]";
                    if (g.outlineRemoved != null && g.outlineRemoved.Count > 0) extra += " [アウトライン除去:" + g.outlineRemoved.Count + "]";
                    if (g.matcapRemoved != null && g.matcapRemoved.Count > 0) extra += " [マットキャップは外れます]";
                    EditorGUILayout.LabelField(new GUIContent("・" + Trunc(members, 70) + extra, members), EditorStyles.wordWrappedMiniLabel);
                }
            }

            if (plan.blocked.Count > 0)
            {
                EditorGUILayout.LabelField(string.Format("統合できない/単独 {0} 件", plan.blocked.Count), EditorStyles.miniBoldLabel);
                int shown = 0;
                foreach (PCMaterialAtlasser.AtlasPlanBlocked b in plan.blocked)
                {
                    if (b == null) continue;
                    if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                    string text = "・" + b.name + " — " + b.reason + (string.IsNullOrEmpty(b.hint) ? "" : "(" + b.hint + ")");
                    EditorGUILayout.LabelField(new GUIContent(Trunc(text, 90), text), EditorStyles.wordWrappedMiniLabel);
                }
            }

            changed |= DrawPcAtlasExcludeList(avatarRoot, s, cache);

            EditorGUILayout.LabelField("※ トグル固定・AAO結合の前段のため概算です(最終スロット数はレンダラー結合にも依存)。", EditorStyles.miniLabel);
            return changed;
        }

        /// <summary>
        /// アバターが使うマテリアルごとにアトラス除外を切り替えるリスト(旧 PCOptimizer の DrawAtlasExcludeList を移植)。
        /// 除外指定は s.pcAtlasExcludeGuids(アセットGUID)へ書き、変更時は統合プレビューを作り直す。変更があれば true。
        /// </summary>
        private static bool DrawPcAtlasExcludeList(GameObject avatarRoot, AvatarStudioSettings s, AvatarStudioPreviewCache cache)
        {
            List<Material> materials = cache.GetOrBuild("pcatlasmats", avatarRoot.GetInstanceID().ToString(),
                () => CollectAvatarMaterials(avatarRoot));
            if (materials == null || materials.Count == 0) return false;

            bool changed = false;
            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("アトラスから除外するマテリアル", EditorStyles.miniBoldLabel);
            int shown = 0;
            foreach (Material mat in materials)
            {
                if (mat == null) continue;
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                string guid = GetAssetGuid(mat);
                bool hasGuid = !string.IsNullOrEmpty(guid);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(mat, typeof(Material), false, GUILayout.MinWidth(120f), GUILayout.MaxWidth(200f));
                    using (new EditorGUI.DisabledScope(!hasGuid))
                    {
                        bool excluded = hasGuid && s.pcAtlasExcludeGuids.Contains(guid);
                        bool newExcluded = EditorGUILayout.ToggleLeft(new GUIContent("除外", "このマテリアルをアトラス統合の対象から外します"), excluded, GUILayout.Width(60f));
                        if (hasGuid && newExcluded != excluded)
                        {
                            if (newExcluded) s.pcAtlasExcludeGuids.Add(guid);
                            else s.pcAtlasExcludeGuids.Remove(guid);
                            changed = true;
                            cache.Bump(); // 除外指定の変更で統合プレビューを作り直す
                        }
                    }
                    string reason = PcAtlasIneligibleReason(mat);
                    if (!string.IsNullOrEmpty(reason))
                    {
                        Color prev = GUI.color;
                        GUI.color = AvatarStudioUI.NoteYellowColor;
                        EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                        GUI.color = prev;
                    }
                    else GUILayout.FlexibleSpace();
                }
            }
            return changed;
        }

        /// <summary>アバターが使用するマテリアル一覧(重複なし)を集める(アトラス除外指定用。READ-ONLY)。</summary>
        private static List<Material> CollectAvatarMaterials(GameObject avatarRoot)
        {
            var seen = new HashSet<Material>();
            var list = new List<Material>();
            if (avatarRoot == null) return list;
            foreach (Renderer r in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                foreach (Material m in r.sharedMaterials)
                    if (m != null && seen.Add(m)) list.Add(m);
            }
            return list;
        }

        /// <summary>マテリアルがアトラス統合に向かない理由の簡易プレビュー(安価に分かる範囲だけ)。無ければnull。</summary>
        private static string PcAtlasIneligibleReason(Material mat)
        {
            if (mat == null || mat.shader == null) return "シェーダー欠落";
            if (mat.shader.name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0) return "TMP(統合不可)";
            return null;
        }

        // ================================================================
        // 6b. Questマテリアル変換プレビュー(AvatarQuestConverter.PreviewMaterials)
        // ================================================================
        // MaterialOverride の並び順(Auto..MatCapLit)と一致させること(index を (MaterialOverride) へキャストして使う)
        private static readonly GUIContent[] OverrideLabels =
        {
            new GUIContent("自動(推奨)", "診断結果から最適な変換方法を自動で選びます。迷ったらこのままでOK"),
            new GUIContent("Toon Standard", "VRChat/Mobile/Toon Standard へ変換(不透明。影ランプ・ノーマル・エミッション対応)"),
            new GUIContent("Toon Lit", "VRChat/Mobile/Toon Lit へ変換(最軽量。陰影はテクスチャへベイク)"),
            new GUIContent("加算(半透明)", "Particles/Additive へ変換。黒が透明になる加算合成(光り物・ホロ向け)"),
            new GUIContent("乗算(半透明)", "Particles/Multiply へ変換。白が透明になる乗算合成(チーク・頬染め向け)"),
            new GUIContent("非表示", "Quest版では不可視マテリアルに差し替えて見えなくします"),
            new GUIContent("変換しない", "元のマテリアルのまま残します(非対応シェーダーのままだと Quest では正しく表示されない原因になります)"),
            new GUIContent("MatCap Lit(金属向け)", "VRChat/Mobile/MatCap Lit へ変換。金属パーツ向け(乗算合成のマットキャップ・不透明のみ・アトラス統合外でスロットを1つ使います)"),
        };

        // 半透明(TransparentHandling)の日本語ラベル(enum の並び Emulate, Hide, Opaque と一致させること)。
        private static readonly string[] TransparentHandlingLabels =
        {
            "自動で半透明を再現(推奨)", "非表示にする(最軽量)", "不透明に変換",
        };

        public static bool DrawQuestMaterialPanel(VRCAvatarDescriptor avatar, AvatarStudioSettings s, QuestConvertSettings quest, AvatarStudioPreviewCache cache)
        {
            if (avatar == null || s == null) { EditorGUILayout.HelpBox("アバターを選択してください。", MessageType.Info); return false; }
            if (quest == null) quest = new QuestConvertSettings();

            bool changed = false;
            EnsureList(ref s.materialOverrides);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("変換先シェーダー", "Toon Standard=影ランプ等対応 / Toon Lit=最軽量"), GUILayout.Width(110f));
                var newTarget = (QuestShaderTarget)EditorGUILayout.EnumPopup(s.shaderTarget, GUILayout.Width(140f));
                if (!Equals(newTarget, s.shaderTarget)) { s.shaderTarget = newTarget; changed = true; }

                EditorGUILayout.LabelField(new GUIContent("半透明", "半透明マテリアルのQuest版での扱い"), GUILayout.Width(48f));
                int trCur = Mathf.Clamp((int)s.transparentHandling, 0, TransparentHandlingLabels.Length - 1);
                int trNew = EditorGUILayout.Popup(trCur, TransparentHandlingLabels, GUILayout.Width(200f));
                if (trNew != trCur) { s.transparentHandling = (TransparentHandling)trNew; changed = true; }
            }

            // 見え方トラブルの常設注意書き(恒久対応=この行で変換方法を指定して再生成 / 応急処置=生成複製の
            // シェーダー直変更)。静的テキストのみ・無条件表示なので、同一OnGUIの Layout/Repaint でコントロール数は食い違わない。
            EditorGUILayout.HelpBox(
                "見えるべきものが消えた/消えるべきものが見えている場合: 該当マテリアルの行で変換方法を選び直して再生成してください" +
                "(見せたい→Toon Standard(不透明)や乗算・加算 / 消したい→非表示)。" +
                "急ぎの場合は生成された複製のマテリアルのシェーダーを直接 VRChat/Mobile/Toon Standard 等へ変更しても表示できます" +
                "(ただし再生成で上書きされるため、恒久対応はこの行での指定を推奨)。",
                MessageType.None);

            // quest は OnGUI 冒頭で s から値コピー済みのスナップショット。上のドロップダウンで
            // s.shaderTarget / s.transparentHandling を変えても quest 側は旧値のままなので、sig が
            // 新値へ動く一方 PreviewMaterials が旧 quest を読み、プレビューが1フレーム前の設定で
            // 作られてしまう。同フレームで最新設定を quest へ反映してから rows を組む。
            quest.shaderTarget = s.shaderTarget;
            quest.transparentHandling = s.transparentHandling;

            string sig = avatar.GetInstanceID() + "|" + (int)s.shaderTarget + "|" + (int)s.transparentHandling + "|" + HashOverrides(s.materialOverrides);
            List<MaterialPreviewRow> rows = cache.GetOrBuild("questmat", sig, () => AvatarQuestConverter.PreviewMaterials(avatar, quest));

            if (rows == null) { EditorGUILayout.HelpBox("マテリアルプレビューの計算に失敗しました。", MessageType.Warning); return changed; }
            if (rows.Count == 0) { EditorGUILayout.LabelField("マテリアルが見つかりませんでした。", EditorStyles.miniLabel); return changed; }

            // [1.5.1] EditorOnly / Quest除外配下のみで使われ、一覧・変換から外れるマテリアル件数([C])。
            // GetOrBuild は参照型のみキャッシュ可のため int[] で包んでキャッシュする(毎リペイントの再集計を避ける)。
            int[] matHidden = cache.GetOrBuild("questmatexcluded",
                avatar.GetInstanceID() + "|" + HashPaths(quest.questExcludePaths),
                () => new[] { AvatarQuestConverter.CountBuildExcludedOnlyMaterials(avatar, quest) });
            if (matHidden != null) DrawBuildExcludedHiddenNote(matHidden[0]);

            // 非表示(不可視マテリアル)になる予定の件数を数え、あれば透過ドロップダウン直下に案内を出す。
            // 件数は同一OnGUIサイクル内で不変なキャッシュ済み rows から算出するため、Layout/Repaint で
            // コントロール数は食い違わない(該当行の個別ボタンは下のループで出す)。
            int hiddenCount = 0;
            foreach (MaterialPreviewRow r in rows) if (IsPlannedHidden(r)) hiddenCount++;
            if (hiddenCount > 0)
            {
                EditorGUILayout.HelpBox(
                    "Quest版で非表示(不可視)になる予定のマテリアルが " + hiddenCount + " 件あります。" +
                    "非表示になった衣類(ストッキング等)は、下の一覧の「不透明にする」/「乗算(半透明)へ変換」から個別に表示方法を選べます。",
                    MessageType.Info);
            }

            int shown = 0;
            foreach (MaterialPreviewRow row in rows)
            {
                if (row == null || row.material == null) continue;
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField(string.Format("...他 {0} 件", rows.Count - MaxRows), EditorStyles.miniLabel); break; }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(row.material, typeof(Material), false, GUILayout.MinWidth(110f), GUILayout.MaxWidth(180f));

                    // 変換方法上書き
                    string guid = GetAssetGuid(row.material);
                    int cur = (int)GetOverrideMode(s.materialOverrides, guid);
                    int next = EditorGUILayout.Popup(cur, OverrideLabels, GUILayout.Width(120f));
                    if (next != cur && !string.IsNullOrEmpty(guid))
                    {
                        SetOverrideMode(s.materialOverrides, guid, (MaterialOverride)next);
                        changed = true;
                    }

                    Color prev = GUI.color;
                    if (row.isBrokenShader) GUI.color = AvatarStudioDiagnostics.OverLimitColor;
                    else if (row.isMobileAlready) GUI.color = new Color(0.6f, 0.9f, 0.6f);
                    GUILayout.Label(new GUIContent(Trunc(row.plannedAction ?? "", 40), row.plannedAction), EditorStyles.miniLabel, GUILayout.MinWidth(130f), GUILayout.MaxWidth(210f));
                    GUI.color = prev;

                    DrawQuestMaterialBadges(row);
                    GUILayout.FlexibleSpace();

                    // 非表示(不可視)になる予定の行には、その場で表示方法を選べるショートカットを出す。
                    // ストッキング等が「雑にQuest対応」等で不可視化された場合に、不透明/乗算で見えるよう戻せる。
                    // 押下は上書き(materialOverrides)を書き込むだけ。sig(HashOverrides)が変わり次回OnGUIで
                    // プレビューが再計算される(=非表示解消で次サイクルからボタンは消える)。
                    if (!string.IsNullOrEmpty(guid) && IsPlannedHidden(row))
                    {
                        if (GUILayout.Button(new GUIContent("不透明にする",
                            "このマテリアルを Toon Standard へ変換して不透明で表示します(透過は失われますが確実に見えます)"),
                            GUILayout.Width(92f)))
                        {
                            SetOverrideMode(s.materialOverrides, guid, MaterialOverride.ToonStandard);
                            changed = true;
                        }
                        if (GUILayout.Button(new GUIContent("乗算(半透明)へ変換",
                            "このマテリアルを乗算パーティクルへ変換します(ストッキング等の薄い透け感を近似再現します)"),
                            GUILayout.Width(130f)))
                        {
                            SetOverrideMode(s.materialOverrides, guid, MaterialOverride.ParticleMultiply);
                            changed = true;
                        }
                    }
                }
            }

            EditorGUILayout.LabelField("※ 実変換前のプレビューです。「自動(推奨)」以外を選ぶと自動判定より優先されます。", EditorStyles.miniLabel);
            return changed;
        }

        /// <summary>
        /// マテリアルの状態を短い色付きバッジで表示する(透過/カットアウト/パーティクル/アニメ使用/
        /// メニュー・ギミック参照/TMP/破損/対応済)。旧QuestConverterウィンドウの DrawMaterialBadges と同一分類。
        /// </summary>
        private static void DrawQuestMaterialBadges(MaterialPreviewRow row)
        {
            if (row.transparency == QuestCompat.TransparencyClass.Transparent)
                DrawBadge(row.suppressTransparentHide ? "透過(保護)" : "透過", BadgeTransparentColor,
                    "アルファブレンド透過。Questのメッシュでは透過表示できません(パーティクル系か非表示で対応)");
            else if (row.transparency == QuestCompat.TransparencyClass.Cutout)
                DrawBadge("カットアウト", BadgeCutoutColor,
                    "アルファ抜き。Questでは不透明として変換されます(Toon Standardにカットアウトはありません)");

            if (row.usedByParticle) DrawBadge("パーティクル", BadgeParticleColor, "パーティクル系レンダラーが使用しています");
            if (row.usedByAnimation) DrawBadge("アニメ使用", BadgeAnimationColor, "アニメーションのマテリアル差し替えで参照されています");
            if (row.usedByComponent)
            {
                string tip = "Renderer以外のコンポーネント(MA Material Setter等)から参照されているマテリアル。メニューで切り替わる衣装・目・表情差分など";
                if (!string.IsNullOrEmpty(row.componentSource)) tip += "\n参照元: " + row.componentSource;
                DrawBadge("メニュー・ギミック参照", BadgeComponentColor, tip);
            }
            if (row.isTMP) DrawBadge("TMP", BadgeTmpColor, "TextMeshPro用マテリアル(変換不可。Quest除外を推奨)");
            if (row.isBrokenShader) DrawBadge("破損", BadgeBrokenColor, "シェーダーが欠落または壊れています(修正してから再変換してください)");
            if (row.isMobileAlready) DrawBadge("対応済", BadgeMobileColor, "既にQuest対応シェーダーです(変換不要)");
        }

        /// <summary>
        /// 予定処理(plannedAction)がこのマテリアルを Quest 版で「非表示(不可視マテリアル)」にするか。
        /// AvatarQuestConverter.BuildPlannedAction の非表示分岐はいずれも「非表示化」で始まる文字列を返す
        /// (手動指定 / 表情デカール / アウトライン / 疑似影 / 透過Hide)。唯一まぎらわしい非該当
        /// 「不透明として変換(…非表示化しない)」は「不透明」で始まるため誤検出しない。メッシュ用途は
        /// 非表示だがパーティクル用途は変換される複合(「非表示化 / パーティクル用: …」)も、メッシュが
        /// 不可視のため非表示として扱う。plannedAction を単一の真実として参照する(判定ロジックは複製しない)。
        /// </summary>
        private static bool IsPlannedHidden(MaterialPreviewRow row)
        {
            return row != null && row.plannedAction != null
                && row.plannedAction.StartsWith("非表示化", StringComparison.Ordinal);
        }

        // ================================================================
        // 共有ヘルパ
        // ================================================================
        private static void EnsureList<T>(ref List<T> list) { if (list == null) list = new List<T>(); }

        private static bool IsOptedOut(List<string> list, string path)
        {
            if (list == null || string.IsNullOrEmpty(path)) return false;
            return list.Contains(path);
        }

        private static void TogglePath(List<string> list, string path, bool present)
        {
            if (string.IsNullOrEmpty(path)) return;
            bool has = list.Contains(path);
            if (present && !has) list.Add(path);
            else if (!present && has) list.Remove(path);
        }

        private static bool AllRemoved(List<string> removeList, List<string> paths)
        {
            if (paths == null || paths.Count == 0) return false;
            foreach (string p in paths) if (!removeList.Contains(p)) return false;
            return true;
        }

        private static void SetAllRemoved(List<string> removeList, List<string> paths, bool removed)
        {
            if (paths == null) return;
            foreach (string p in paths) TogglePath(removeList, p, removed);
        }

        private static string GetAssetGuid(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            string path = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
        }

        private static MaterialOverride GetOverrideMode(List<MaterialOverrideEntry> list, string guid)
        {
            if (string.IsNullOrEmpty(guid)) return MaterialOverride.Auto;
            foreach (MaterialOverrideEntry e in list)
                if (e != null && e.materialGuid == guid) return e.mode;
            return MaterialOverride.Auto;
        }

        private static void SetOverrideMode(List<MaterialOverrideEntry> list, string guid, MaterialOverride mode)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].materialGuid == guid)
                {
                    // excludeFromAtlas 等の他フラグが立っていなければ Auto はエントリ削除。
                    if (mode == MaterialOverride.Auto && !list[i].excludeFromAtlas) { list.RemoveAt(i); return; }
                    list[i].mode = mode;
                    return;
                }
            }
            if (mode != MaterialOverride.Auto)
                list.Add(new MaterialOverrideEntry { materialGuid = guid, mode = mode });
        }

        // ---- テクスチャ縮小計画(GUID参照。より小さい目標を優先) ----
        private static bool UpsertTexturePlan(List<TextureSizePlanEntry> plan, Texture texture, int targetSize)
        {
            if (texture == null || targetSize <= 0) return false;
            string guid = GetAssetGuid(texture);
            if (string.IsNullOrEmpty(guid)) return false;
            foreach (TextureSizePlanEntry e in plan)
            {
                if (e != null && e.textureGuid == guid)
                {
                    if (e.targetSize <= 0 || targetSize < e.targetSize) { e.targetSize = targetSize; return true; }
                    return false;
                }
            }
            plan.Add(new TextureSizePlanEntry { textureGuid = guid, targetSize = targetSize });
            return true;
        }

        private static bool IsPlanned(List<TextureSizePlanEntry> plan, Texture texture, int targetSize)
        {
            if (texture == null || plan == null) return false;
            string guid = GetAssetGuid(texture);
            if (string.IsNullOrEmpty(guid)) return false;
            foreach (TextureSizePlanEntry e in plan)
                if (e != null && e.textureGuid == guid && e.targetSize > 0 && e.targetSize <= targetSize) return true;
            return false;
        }

        // ---- シグネチャ用の軽量ハッシュ ----
        private static int HashPaths(List<string> list)
        {
            if (list == null) return 0;
            int h = 17;
            foreach (string x in list) h = h * 31 + (x != null ? x.GetHashCode() : 0);
            return h;
        }

        private static int HashTexturePlan(List<TextureSizePlanEntry> list)
        {
            if (list == null) return 0;
            int h = 17;
            foreach (TextureSizePlanEntry e in list)
            {
                if (e == null) { h = h * 31; continue; }
                h = h * 31 + (e.textureGuid != null ? e.textureGuid.GetHashCode() : 0);
                h = h * 31 + e.targetSize;
            }
            return h;
        }

        private static int HashOverrides(List<MaterialOverrideEntry> list)
        {
            if (list == null) return 0;
            int h = 17;
            foreach (MaterialOverrideEntry e in list)
            {
                if (e == null) { h = h * 31; continue; }
                h = h * 31 + (e.materialGuid != null ? e.materialGuid.GetHashCode() : 0);
                h = h * 31 + (int)e.mode;
            }
            return h;
        }

        private static string Trunc(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
#endif
