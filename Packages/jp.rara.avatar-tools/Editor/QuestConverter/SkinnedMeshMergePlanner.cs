// RARA Quest Converter / PC軽量化ツール 共有 - SkinnedMesh統合プランナー
// 複製アバターの SkinnedMeshRenderer を「顔(分離維持)」「統合対象」「除外(理由あり)」へ分類し、
// AAO MergeSkinnedMesh を1つのターゲットへ集約するための計画(SkinnedMeshMergePlan)を作る。
//
// 【方針(研究の結論)】
//  ・顔(ビセーム/まばたき)メッシュは分離を維持する。VRCAvatarDescriptor が VisemeSkinnedMesh /
//    eyelidsSkinnedMesh を PreserveProperties(ミューテーション)で登録しているだけでは AAO の自動統合から
//    除外されないため、明示的に分離対象として扱い、統合ソースから外す(実行側で AAO の除外にも登録する)。
//  ・それ以外の SkinnedMeshRenderer は原則すべて1つへ統合する(ユーザー方針: 表情以外の
//    表示/非表示トグルはもう不要 = 常時表示ロック)。ただし Cloth(布シミュレーション)・EditorOnly
//    (ビルド除外)・ユーザーが個別除外したものは統合しない(理由を提示する)。
//
// このファイルは Assembly-CSharp-Editor(asmdef無し)でコンパイルされ、RARA.QuestConverter と
// RARA.PCOptimizer の両ツールから参照される(同一アセンブリ)。AAO 型は一切参照しない(実行は
// AAOMeshMergeHelper がリフレクションで行う)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>
    /// SkinnedMesh統合モード。JsonUtility では int としてシリアライズされる。
    /// 【移行】保存済みJSONにキーが無い(旧ユーザー)場合の既定は None(=0)。
    /// 新規設定の既定は各ウィンドウの LoadSettings 移行ガードで MergeExceptFace に設定する。
    /// </summary>
    public enum SkinnedMeshMergeMode
    {
        /// <summary>統合しない(従来どおり。SMR数・スロット数は削減されない)。</summary>
        None = 0,

        /// <summary>顔(ビセーム/まばたき)以外の全 SkinnedMeshRenderer を1つへ統合する(推奨)。</summary>
        MergeExceptFace = 1,

        /// <summary>
        /// グループ指定で統合。ユーザーが各レンダラーへ割り当てたグループ番号(1..8)ごとに別々の
        /// 統合セットを作り、グループ単位で1つのメッシュへまとめる。顔は常に自動保護(割り当て不可)、
        /// 未割り当てのレンダラーは統合しない。旧値(None=0/MergeExceptFace=1)は不変。
        /// </summary>
        MergeByGroup = 2,
    }

    /// <summary>SkinnedMesh統合で使えるグループ番号の最大値(グループ1..8)。</summary>
    public static class SmrMergeGroupLimits
    {
        /// <summary>割り当て可能なグループ番号の最大値(1..MaxGroup)。</summary>
        public const int MaxGroup = 8;
    }

    /// <summary>
    /// 「グループ指定で統合」モードでの、レンダラー1件のグループ割り当て(rendererPath→groupIndex)。
    /// JsonUtility で往復できるよう public フィールドのみ。groupIndex は 1..8(0/未満は未割り当て扱い)。
    /// QuestConverter に置く共有 CLR 型で、PCOptimizeSettings / QuestConvertSettings の両方が
    /// 同一インスタンス参照で持ち、両エンジンが読む(skinnedMeshMergeOptOutPaths と同じ運用)。
    /// </summary>
    [System.Serializable]
    public class SmrMergeGroupAssignment
    {
        /// <summary>アバタールート相対のレンダラーパス(QuestCompat.GetRelativePath 準拠)。</summary>
        public string rendererPath;

        /// <summary>割り当て先グループ番号(1..8)。0以下は未割り当て(=統合しない)。</summary>
        public int groupIndex;
    }

    /// <summary>「グループ指定で統合」モードで作られる、1グループ分の統合セット(結果)。</summary>
    public class SkinnedMeshMergeGroup
    {
        /// <summary>このグループの番号(1..8)。</summary>
        public int groupIndex;

        /// <summary>このグループに統合されるレンダラーの相対パス(顔・除外・未割り当ては含まない)。</summary>
        public List<string> sourcePaths = new List<string>();
    }

    /// <summary>SkinnedMesh統合プレビューの1行(レンダラー1件の扱い)。</summary>
    public class SkinnedMeshMergeRow
    {
        /// <summary>アバタールート相対のレンダラーパス(QuestCompat.GetRelativePath 準拠)。</summary>
        public string rendererPath;

        /// <summary>レンダラーの GameObject 名(UI表示用)。</summary>
        public string rendererName;

        /// <summary>このレンダラーを統合ターゲットへまとめるか(true=統合 / false=分離維持)。</summary>
        public bool willMerge;

        /// <summary>統合する/しない の理由(日本語。UI表示用)。</summary>
        public string reason;

        /// <summary>顔(ビセーム/まばたき)メッシュか(UI強調・カウント用)。</summary>
        public bool isFace;

        /// <summary>EditorOnly サブツリー(ビルドで除去)か。最終カウントから除外する。</summary>
        public bool isEditorOnly;

        /// <summary>
        /// ビルド除外(EditorOnly タグ、または Quest除外登録の配下)か。true の行は統合対象外で、
        /// 一覧からも非表示にし、統合前/後の概算SMR数からも除外する(ビルドに含まれないため)。
        /// </summary>
        public bool isBuildExcluded;

        /// <summary>ブレンドシェイプ数(UI表示用)。</summary>
        public int blendShapeCount;

        /// <summary>
        /// 「グループ指定で統合」モードでの割り当てグループ番号(1..8)。0=未割り当て/対象外。
        /// 顔・除外行は常に 0(顔は自動保護で割り当て不可)。他モードでは常に 0。
        /// </summary>
        public int groupIndex;

        /// <summary>
        /// 統合に割り当て可能なレンダラーか(顔でない・EditorOnlyでない・メッシュあり・Clothなし)。
        /// UI(グループ指定モード)で、割り当て可能な行にだけグループ選択を出すために使う。
        /// opt-out(個別除外)は MergeExceptFace 専用のため、この判定には含めない。
        /// </summary>
        public bool canAssign;
    }

    /// <summary>
    /// SkinnedMesh統合計画。プレビュー表示にも実行(AAOMeshMergeHelper)にも使う。
    /// </summary>
    public class SkinnedMeshMergePlan
    {
        /// <summary>レンダラーごとの扱い(顔・統合・除外)。UIのプレビュー表に使う。</summary>
        public List<SkinnedMeshMergeRow> rows = new List<SkinnedMeshMergeRow>();

        /// <summary>
        /// 統合ソースにするレンダラーのパス(willMerge==true のもの)。
        /// MergeExceptFace モードで使う単一統合セット。MergeByGroup モードでは空で、代わりに
        /// <see cref="mergeGroups"/> をグループ単位で使う。
        /// </summary>
        public List<string> mergeSourcePaths = new List<string>();

        /// <summary>
        /// 「グループ指定で統合」モードでの、グループ番号ごとの統合セット(昇順)。
        /// MergeExceptFace / None モードでは空。実行側(AAOMeshMergeHelper)は各グループを1つの
        /// ターゲット(RARA_MergedMesh_G{n})へ統合する(メンバーが2件未満のグループは統合しない)。
        /// </summary>
        public List<SkinnedMeshMergeGroup> mergeGroups = new List<SkinnedMeshMergeGroup>();

        /// <summary>分離維持する顔レンダラーのパス(AAO の自動統合からも除外させる対象)。</summary>
        public List<string> faceRendererPaths = new List<string>();

        /// <summary>統合前の(ビルドに残る=EditorOnlyでない)SkinnedMeshRenderer 数。</summary>
        public int beforeCount;

        /// <summary>統合後に期待される SkinnedMeshRenderer 数。</summary>
        public int expectedCount;

        /// <summary>
        /// 実際に統合が行われるか(いずれかの統合セットが2件以上のときのみ true)。
        /// MergeExceptFace は単一セット(mergeSourcePaths)、MergeByGroup は各グループを見る。
        /// </summary>
        public bool WillMergeAnything
        {
            get
            {
                if (mergeSourcePaths.Count >= 2) return true;
                foreach (SkinnedMeshMergeGroup g in mergeGroups)
                {
                    if (g != null && g.sourcePaths.Count >= 2) return true;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// 複製アバターの SkinnedMeshRenderer を分類し、SkinnedMesh統合計画を作る(読み取り専用)。
    /// QuestConverter・PCOptimizer の両ツールから使う共有プランナー。
    /// </summary>
    public static class SkinnedMeshMergePlanner
    {
        /// <summary>
        /// avatarRoot 配下の各 SkinnedMeshRenderer を分類して統合計画を返す(グループ指定なし。旧シグネチャ)。
        /// mode==None のときは全行を「統合しない」で返す(実行側もスキップする)。
        /// optOutPaths はユーザーが個別に統合から除外したレンダラーの相対パス集合(null可)。
        /// mode==MergeByGroup で groupAssignments を渡さないこの版では、割り当てが無いため何も統合されない。
        /// </summary>
        public static SkinnedMeshMergePlan BuildPlan(GameObject avatarRoot, SkinnedMeshMergeMode mode, IEnumerable<string> optOutPaths)
        {
            return BuildPlan(avatarRoot, mode, optOutPaths, null, null);
        }

        /// <summary>
        /// avatarRoot 配下の各 SkinnedMeshRenderer を分類して統合計画を返す。
        /// mode==None のときは全行を「統合しない」で返す(実行側もスキップする)。
        /// optOutPaths はユーザーが個別に統合から除外したレンダラーの相対パス集合(null可)。
        /// groupAssignments は「グループ指定で統合」モードでのレンダラー→グループ割り当て(null可)。
        /// mode!=MergeByGroup のときは無視される。顔は常に自動保護で、どのモードでも割り当て不可。
        /// excludedRoots は Quest除外サブツリーのルート集合(プレビューで元アバターを走査する面用。null可)。
        /// これに含まれる/EditorOnly のレンダラーは isBuildExcluded=true にして統合対象外・一覧非表示・概算除外にする。
        /// 変換時はクローンで Quest除外が既に EditorOnly 化済みのため null でよい(EditorOnly タグで判定される)。
        /// </summary>
        public static SkinnedMeshMergePlan BuildPlan(GameObject avatarRoot, SkinnedMeshMergeMode mode, IEnumerable<string> optOutPaths, IEnumerable<SmrMergeGroupAssignment> groupAssignments, HashSet<Transform> excludedRoots = null)
        {
            var plan = new SkinnedMeshMergePlan();
            if (avatarRoot == null) return plan;

            var optOut = new HashSet<string>();
            if (optOutPaths != null)
            {
                foreach (string p in optOutPaths)
                {
                    if (!string.IsNullOrEmpty(p)) optOut.Add(p);
                }
            }

            // グループ割り当て(rendererPath→groupIndex 1..8)。MergeByGroup のときだけ使う。
            var groupByPath = new Dictionary<string, int>();
            if (mode == SkinnedMeshMergeMode.MergeByGroup && groupAssignments != null)
            {
                foreach (SmrMergeGroupAssignment a in groupAssignments)
                {
                    if (a == null || string.IsNullOrEmpty(a.rendererPath)) continue;
                    if (a.groupIndex < 1 || a.groupIndex > SmrMergeGroupLimits.MaxGroup) continue;
                    groupByPath[a.rendererPath] = a.groupIndex; // 後勝ち(通常は一意)
                }
            }
            // グループ番号→統合セット(昇順で plan.mergeGroups に積むため一時保持)。
            var groupSets = new Dictionary<int, SkinnedMeshMergeGroup>();

            HashSet<SkinnedMeshRenderer> faceRenderers = CollectFaceRenderers(avatarRoot);

            SkinnedMeshRenderer[] renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer smr in renderers)
            {
                if (smr == null) continue;
                string path = QuestCompat.GetRelativePath(avatarRoot.transform, smr.transform);
                if (path == null) continue; // avatarRoot 配下でない(通常ありえない)

                bool editorOnly = QuestCompat.IsEditorOnly(smr.transform);
                // ビルド除外 = EditorOnly タグ、または Quest除外登録の配下(プレビューで元アバターを走査する面用)。
                bool buildExcluded = QuestCompat.IsBuildExcluded(smr.transform, avatarRoot.transform, excludedRoots);
                bool isFace = faceRenderers.Contains(smr);
                Mesh mesh = smr.sharedMesh;

                bool cloth = HasCloth(smr);
                // [A] 多重描画: マテリアルスロット数がサブメッシュ数を超えるレンダラー(例: 髪 = mat_hair +
                //     FakeShadow の2スロット・1サブメッシュ)。Unity はこのメッシュを追加スロット分だけ多重に描画するが、
                //     AAO MergeSkinnedMesh はビルド時に FlattenMultiPassRendering で多重描画を実体化(サブメッシュを複製)
                //     するため、統合するとポリゴン数が増える(PCの70,000境界を越えて即 Very Poor になる等)。統合しなければ
                //     ネイティブの多重描画のまま残り SDK はポリゴン数を増やさない。よって統合対象から自動除外する
                //     (1.0.9の非統合スロット方針を、ビルド時のマージが打ち消さないようにするガード)。
                bool multiPass = mesh != null && smr.sharedMaterials != null && smr.sharedMaterials.Length > mesh.subMeshCount;

                var row = new SkinnedMeshMergeRow
                {
                    rendererPath = path,
                    rendererName = smr.gameObject.name,
                    isFace = isFace,
                    isEditorOnly = editorOnly,
                    isBuildExcluded = buildExcluded,
                    blendShapeCount = mesh != null ? mesh.blendShapeCount : 0,
                    // 割り当て可能=顔でない・ビルド除外でない・メッシュあり・Clothなし・多重描画でない(opt-outは含めない)。
                    canAssign = !isFace && !buildExcluded && mesh != null && !cloth && !multiPass,
                };

                if (mode == SkinnedMeshMergeMode.None)
                {
                    row.willMerge = false;
                    row.reason = "統合しない(SkinnedMesh統合が無効)";
                }
                else if (isFace)
                {
                    row.willMerge = false;
                    row.reason = "顔メッシュ(ビセーム/まばたき)のため分離を維持(表情を保持)";
                }
                else if (buildExcluded)
                {
                    row.willMerge = false;
                    row.reason = editorOnly
                        ? "EditorOnly(ビルド除外)のため統合対象外"
                        : "Quest除外(ビルド除外)のため統合対象外";
                }
                else if (mesh == null)
                {
                    row.willMerge = false;
                    row.reason = "メッシュが未設定のため統合対象外";
                }
                else if (cloth)
                {
                    row.willMerge = false;
                    row.reason = "Clothコンポーネントがあるため統合対象外(布シミュレーションを保持)";
                }
                else if (multiPass)
                {
                    // [A] 多重描画は統合すると実体化されてポリゴンが増えるため、どの統合モードでも自動除外する。
                    row.willMerge = false;
                    row.reason = "多重描画(スロット数>サブメッシュ数)のため統合しません(統合するとビルド時にポリゴン数が増えるため)";
                }
                else if (mode == SkinnedMeshMergeMode.MergeByGroup)
                {
                    // グループ指定モード: 割り当てがあればそのグループへ、無ければ統合しない(opt-outは見ない)。
                    int g;
                    if (groupByPath.TryGetValue(path, out g))
                    {
                        row.willMerge = true;
                        row.groupIndex = g;
                        row.reason = "グループ" + g + "に統合";
                        SkinnedMeshMergeGroup set;
                        if (!groupSets.TryGetValue(g, out set))
                        {
                            set = new SkinnedMeshMergeGroup { groupIndex = g };
                            groupSets[g] = set;
                        }
                        set.sourcePaths.Add(path);
                    }
                    else
                    {
                        row.willMerge = false;
                        row.reason = "グループ未指定のため統合しない";
                    }
                }
                else if (optOut.Contains(path))
                {
                    row.willMerge = false;
                    row.reason = "ユーザーが統合対象から除外";
                }
                else
                {
                    row.willMerge = true;
                    row.reason = "他メッシュと1つに統合";
                }

                if (isFace) plan.faceRendererPaths.Add(path);
                // 単一統合セット(MergeExceptFace)のみ mergeSourcePaths に積む。グループ指定は mergeGroups で持つ。
                if (row.willMerge && mode != SkinnedMeshMergeMode.MergeByGroup) plan.mergeSourcePaths.Add(path);
                plan.rows.Add(row);
            }

            // グループ統合セットを番号昇順で確定する(実行順・表示順を安定化)。
            if (mode == SkinnedMeshMergeMode.MergeByGroup)
            {
                var indices = new List<int>(groupSets.Keys);
                indices.Sort();
                foreach (int g in indices) plan.mergeGroups.Add(groupSets[g]);
            }

            // [B] マテリアルプロパティアニメーションの波及ガード。統合セット内でソースごとの
            //     material.* アニメーション集合が食い違う場合、アニメ対象のレンダラーを統合対象から外す
            //     (統合すると AAO がそのアニメを統合先メッシュ全体へ波及させ、意図しない部分にも適用されるため)。
            //     counts 算出より前に走らせ、除外を概算SMR数へ正しく反映する。
            var rowByPath = new Dictionary<string, SkinnedMeshMergeRow>(StringComparer.Ordinal);
            foreach (SkinnedMeshMergeRow r in plan.rows)
            {
                if (r != null && !string.IsNullOrEmpty(r.rendererPath)) rowByPath[r.rendererPath] = r;
            }
            ApplyPropertyAnimationMismatchGuard(avatarRoot, mode, plan, rowByPath);

            // 統合前(ビルドに残る)SMR数と、統合後の期待SMR数を算出する。
            int surviving = 0; // ビルドに残る(EditorOnly / Quest除外でない)レンダラー総数
            foreach (SkinnedMeshMergeRow row in plan.rows)
            {
                if (row.isBuildExcluded) continue;
                surviving++;
            }
            plan.beforeCount = surviving;

            // 削減数 = 各統合セット(2件以上)について (メンバー数-1)。2件未満のセットは統合されず削減0。
            int reduction = 0;
            if (mode == SkinnedMeshMergeMode.MergeByGroup)
            {
                foreach (SkinnedMeshMergeGroup g in plan.mergeGroups)
                {
                    if (g != null && g.sourcePaths.Count >= 2) reduction += g.sourcePaths.Count - 1;
                }
            }
            else
            {
                int mergeCount = plan.mergeSourcePaths.Count;
                if (mergeCount >= 2) reduction = mergeCount - 1;
            }
            plan.expectedCount = surviving - reduction;

            return plan;
        }

        /// <summary>
        /// VRCAvatarDescriptor が参照する顔レンダラー(リップシンクの口メッシュ・まばたき用メッシュ)を集める。
        /// これらは分離を維持し、統合ソースからも AAO の自動統合からも外す。
        /// </summary>
        private static HashSet<SkinnedMeshRenderer> CollectFaceRenderers(GameObject avatarRoot)
        {
            var set = new HashSet<SkinnedMeshRenderer>();
            VRCAvatarDescriptor descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor == null) return set;

            // リップシンク(ビセーム/あごブレンドシェイプ)用の口メッシュ
            if ((descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape ||
                 descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape) &&
                descriptor.VisemeSkinnedMesh != null)
            {
                set.Add(descriptor.VisemeSkinnedMesh);
            }

            // アイトラッキングのまばたき(ブレンドシェイプ方式)用メッシュ
            if (descriptor.enableEyeLook)
            {
                VRCAvatarDescriptor.CustomEyeLookSettings eye = descriptor.customEyeLookSettings;
                if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes &&
                    eye.eyelidsSkinnedMesh != null)
                {
                    set.Add(eye.eyelidsSkinnedMesh);
                }
            }

            return set;
        }

        /// <summary>この SkinnedMeshRenderer と同じ GameObject に Unity Cloth が付いているか。</summary>
        private static bool HasCloth(SkinnedMeshRenderer smr)
        {
            if (smr == null) return false;
            return smr.GetComponent<Cloth>() != null;
        }

        // ================================================================
        // [B] マテリアルプロパティアニメーションの波及ガード
        // ================================================================

        /// <summary>比較用の空集合(このパスに material.* アニメが無いときに使う。生成を避ける)。</summary>
        private static readonly HashSet<string> EmptyMatAnimSet = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 各統合セット(顔以外を統合=単一 / グループ指定=各グループ)について、ソースごとの material.* アニメーション
        /// 集合が食い違う場合、アニメ対象(material.* アニメを持つ)レンダラーを統合から自動除外する。
        /// 全ソースが同一集合(空集合含む)なら波及しないため統合を許可する。走査失敗時は従来動作(除外しない)。
        /// </summary>
        private static void ApplyPropertyAnimationMismatchGuard(GameObject avatarRoot, SkinnedMeshMergeMode mode, SkinnedMeshMergePlan plan, Dictionary<string, SkinnedMeshMergeRow> rowByPath)
        {
            if (avatarRoot == null || plan == null || mode == SkinnedMeshMergeMode.None) return;

            // 統合が2件以上のセットが無ければガード不要(scan もしない=軽量化)。
            bool anyMultiMember = plan.mergeSourcePaths.Count >= 2;
            if (!anyMultiMember)
            {
                foreach (SkinnedMeshMergeGroup g in plan.mergeGroups)
                {
                    if (g != null && g.sourcePaths.Count >= 2) { anyMultiMember = true; break; }
                }
            }
            if (!anyMultiMember) return;

            Dictionary<string, HashSet<string>> matAnim = CollectAnimatedMaterialProperties(avatarRoot);
            if (matAnim == null) return; // 走査失敗 → 安全側で従来動作(誤って統合を止めない)
            if (matAnim.Count == 0) return; // material.* アニメが1つも無ければ波及の懸念なし

            if (mode == SkinnedMeshMergeMode.MergeByGroup)
            {
                foreach (SkinnedMeshMergeGroup g in plan.mergeGroups)
                {
                    if (g == null) continue;
                    GuardMaterialAnimationMismatchInSet(g.sourcePaths, matAnim, rowByPath);
                }
            }
            else
            {
                GuardMaterialAnimationMismatchInSet(plan.mergeSourcePaths, matAnim, rowByPath);
            }
        }

        /// <summary>
        /// setPaths(1つの統合セット)内で、ソースごとの material.* アニメ集合が全て一致しなければ、アニメ対象
        /// (集合が非空)のパスを setPaths から取り除き、対応する行を「統合しない」へ更新する(理由を提示)。
        /// </summary>
        private static void GuardMaterialAnimationMismatchInSet(List<string> setPaths, Dictionary<string, HashSet<string>> matAnim, Dictionary<string, SkinnedMeshMergeRow> rowByPath)
        {
            if (setPaths == null || setPaths.Count < 2) return;

            // AAO と同じ判定粒度: material. を除いたプロパティ名の集合をソース間で比較する。
            HashSet<string> reference = GetMatAnimSet(matAnim, setPaths[0]);
            bool mismatch = false;
            for (int i = 1; i < setPaths.Count; i++)
            {
                if (!reference.SetEquals(GetMatAnimSet(matAnim, setPaths[i]))) { mismatch = true; break; }
            }
            if (!mismatch) return; // 全ソース同一集合 → 波及しないため統合を許可

            // アニメ対象(集合が非空 = そのメッシュに material.* アニメが向いている)を統合から外す。
            var toRemove = new List<string>();
            foreach (string p in setPaths)
            {
                if (GetMatAnimSet(matAnim, p).Count > 0) toRemove.Add(p);
            }
            foreach (string p in toRemove)
            {
                setPaths.Remove(p);
                if (rowByPath != null && rowByPath.TryGetValue(p, out SkinnedMeshMergeRow row) && row != null)
                {
                    row.willMerge = false;
                    row.groupIndex = 0;
                    row.reason = "マテリアルアニメーションが統合先全体へ波及するため統合しません";
                }
            }
        }

        /// <summary>path の material.* アニメ集合を返す(無ければ空集合)。</summary>
        private static HashSet<string> GetMatAnimSet(Dictionary<string, HashSet<string>> matAnim, string path)
        {
            if (matAnim != null && path != null && matAnim.TryGetValue(path, out HashSet<string> s) && s != null) return s;
            return EmptyMatAnimSet;
        }

        /// <summary>
        /// avatarRoot から到達可能な全アニメーションクリップを走査し、SkinnedMeshRenderer への material.* プロパティ
        /// アニメーションを、レンダラーパス → プロパティ名集合(material. を除いた名前)として返す。収集範囲・作法は
        /// ComponentRemover.CollectPhysBoneTogglePaths / AAOMeshMergeHelper.CollectAnimatedBlendShapeKeys と同じ
        /// (ルートのコントローラー + 子コンポーネント参照のパスを前置)。走査失敗時は null を返す(呼び出し側は
        /// 安全側で除外しない)。AAO の material-animation-differently 警告と同じ「material.」始まりのプロパティを見る。
        /// </summary>
        private static Dictionary<string, HashSet<string>> CollectAnimatedMaterialProperties(GameObject root)
        {
            if (root == null) return null;
            try
            {
                var byPath = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

                var seenClips = new HashSet<AnimationClip>();
                foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(root))
                {
                    if (controller == null) continue;
                    foreach (AnimationClip clip in controller.animationClips)
                    {
                        if (clip == null || !seenClips.Add(clip)) continue;
                        AddMaterialPropsFromClip(clip, string.Empty, byPath);
                    }
                }

                // 子オブジェクトの Animator / MA Merge Animator 等が参照するコントローラーは、バインディングパスが
                // そのコンポーネント基準の可能性があるため、位置を前置したパスも追加する(判定が広がる=統合を控える
                // 方向にしか働かないため安全側)。
                var seenPrefixed = new HashSet<string>();
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    if (component.transform == root.transform) continue;
                    string prefix = QuestCompat.GetRelativePath(root.transform, component.transform);
                    if (string.IsNullOrEmpty(prefix)) continue;

                    var serializedObject = new SerializedObject(component);
                    SerializedProperty property = serializedObject.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var controller = property.objectReferenceValue as RuntimeAnimatorController;
                        if (controller == null) continue;
                        foreach (AnimationClip clip in controller.animationClips)
                        {
                            if (clip == null) continue;
                            if (!seenPrefixed.Add(clip.GetInstanceID() + "|" + prefix)) continue;
                            AddMaterialPropsFromClip(clip, prefix, byPath);
                        }
                    }
                }
                return byPath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// クリップ内の SkinnedMeshRenderer 向け material.&lt;prop&gt; フロートバインディングを、prefix を前置した
        /// レンダラーパスへ集約する(値は material. を除いたプロパティ名の集合)。
        /// </summary>
        private static void AddMaterialPropsFromClip(AnimationClip clip, string prefix, Dictionary<string, HashSet<string>> byPath)
        {
            const string MaterialPrefix = "material.";
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == null || !typeof(SkinnedMeshRenderer).IsAssignableFrom(binding.type)) continue;
                if (binding.propertyName == null ||
                    !binding.propertyName.StartsWith(MaterialPrefix, StringComparison.Ordinal)) continue;
                string prop = binding.propertyName.Substring(MaterialPrefix.Length);
                if (prop.Length == 0) continue;

                string path = binding.path ?? string.Empty;
                string full;
                if (prefix.Length == 0) full = path;
                else full = path.Length == 0 ? prefix : prefix + "/" + path;

                if (!byPath.TryGetValue(full, out HashSet<string> set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    byPath[full] = set;
                }
                set.Add(prop);
            }
        }
    }
}
#endif
