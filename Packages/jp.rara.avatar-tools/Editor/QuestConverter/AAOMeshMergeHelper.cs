// RARA Quest Converter / PC軽量化ツール 共有 - AAO MergeSkinnedMesh 注入ヘルパー
// SkinnedMeshMergePlan に従い、複製アバターへ「統合ターゲット GameObject + AAO MergeSkinnedMesh」を
// 1つ追加し、統合対象レンダラーをソースとして登録する。実際のメッシュ結合・ブレンドシェイプ改名・
// マテリアルスロット再マップ・アニメーションバインディング書き換えは AAO+NDMF がビルド時に行う
// (Play/Upload 時。保存プレファブは統合前の見た目のまま = RemoveMeshByBlendShape と同じ挙動)。
//
// 【重要】AAO の型は com.anatawa12.avatar-optimizer.* アセンブリにあり、Assembly-CSharp-Editor
// (このプロジェクトは asmdef 無し)からはコンパイル時に参照できない。よって AAO コンポーネントの
// 追加・設定はすべてリフレクションで行う(AAOMeshRemovalHelper と同じ作法)。AAO への using /
// 型参照は絶対に書かないこと。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>
    /// AAO(AvatarOptimizer)の MergeSkinnedMesh をリフレクション経由で注入し、複数の
    /// SkinnedMeshRenderer を1つのターゲットへ統合する。付与は複製アバターに対して行う。
    /// </summary>
    public static class AAOMeshMergeHelper
    {
        // ---- AAO 型のフルネーム(リフレクション解決用。AAO 未導入なら FindType が null を返す)----
        private const string MergeSkinnedMeshTypeName = "Anatawa12.AvatarOptimizer.MergeSkinnedMesh";
        private const string TraceAndOptimizeTypeName = "Anatawa12.AvatarOptimizer.TraceAndOptimize";
        private const string FreezeBlendShapeTypeName = "Anatawa12.AvatarOptimizer.FreezeBlendShape";

        /// <summary>統合ターゲットとして生成する GameObject 名(顔以外を統合=単一セット)。</summary>
        public const string MergeTargetName = "RARA_MergedMesh";

        /// <summary>グループ指定モードで、グループ n の統合ターゲットに付ける名前を返す(RARA_MergedMesh_G{n})。</summary>
        public static string MergeTargetNameForGroup(int groupIndex) => MergeTargetName + "_G" + groupIndex;

        // AAO BlendShapeMode の列挙インデックス(MergeSameName=0, RenameToAvoidConflict=1, TraditionalCompability=2)。
        // 既定は RenameToAvoidConflict: 同名ブレンドシェイプを別名化して衝突を避け、各アニメを個別に追従させる
        // (表情・シェイプが安全に保たれる。研究の結論)。
        private const int BlendShapeModeRenameToAvoidConflict = 1;

        /// <summary>
        /// plan に従って複製 cloneRoot へ統合ターゲット + MergeSkinnedMesh を1つ追加し、
        /// plan.mergeSourcePaths のレンダラーをソースとして登録する。さらに Trace and Optimize を
        /// 確保し、顔レンダラーを AAO の自動統合からも除外する(明示ソースに入れない + 除外登録)。
        /// 統合ソースが2件未満・AAO未導入・失敗時は Warn を出して 0 を返す(絶対に例外を投げない)。
        /// 戻り値: 登録できた統合ソース数(0=統合しなかった)。
        /// </summary>
        public static int ApplyMergeSkinnedMesh(GameObject cloneRoot, SkinnedMeshMergePlan plan, ConversionReport report)
        {
            if (report == null) report = new ConversionReport();
            if (cloneRoot == null || plan == null) return 0;

            bool byGroup = plan.mergeGroups != null && plan.mergeGroups.Count > 0;

            // どのセットも2件未満なら統合しても意味が無い(1件はそのまま残す)。
            if (!plan.WillMergeAnything)
            {
                if (!byGroup && plan.mergeSourcePaths.Count == 1)
                {
                    report.Info("SkinnedMesh統合: 統合対象が1件のみのため統合をスキップしました(そのまま残します)。");
                }
                return 0;
            }

            Type msmType = QuestCompat.FindType(MergeSkinnedMeshTypeName);
            if (msmType == null)
            {
                report.Warn("AvatarOptimizer(AAO)が見つからないため、SkinnedMeshの統合をスキップしました。VCC等でAvatarOptimizerを導入するか、統合を無効にしてください。");
                return 0;
            }

            // [B] 同名ブレンドシェイプ衝突の自動固定に使う情報を、統合ターゲット生成前に一度だけ収集する。
            //  ・animatedKeys: アニメーションで使われている blendShape(キー = "path\nname")。
            //    null は走査失敗を表し、その場合は安全側で一切自動固定しない(アニメを壊さない)。
            //  ・protectedShapeNames: ビセーム/まばたきのシェイプ名(誤固定防止。MMDは IsMmdStandardMorph で別途判定)。
            HashSet<string> animatedKeys = CollectAnimatedBlendShapeKeys(cloneRoot, report);
            HashSet<string> protectedShapeNames = CollectProtectedFaceShapeNames(cloneRoot);

            int totalMerged;
            if (byGroup)
            {
                // --- グループ指定モード: グループごとに別々の統合ターゲットを作る ---
                totalMerged = 0;
                int mergedGroups = 0;
                foreach (SkinnedMeshMergeGroup group in plan.mergeGroups)
                {
                    if (group == null) continue;
                    List<SkinnedMeshRenderer> sources = ResolveSources(cloneRoot, group.sourcePaths, report);
                    if (sources.Count < 2)
                    {
                        report.Info($"SkinnedMesh統合: グループ{group.groupIndex} は統合対象が2件未満のため統合しませんでした(そのまま残します)。");
                        continue;
                    }
                    if (CreateMergeTarget(cloneRoot, msmType, sources, MergeTargetNameForGroup(group.groupIndex), animatedKeys, protectedShapeNames, report))
                    {
                        totalMerged += sources.Count;
                        mergedGroups++;
                    }
                }
                if (mergedGroups == 0) return 0;
                report.Info($"SkinnedMesh統合(グループ指定)を追加しました: {mergedGroups}グループ / 顔以外の{totalMerged}メッシュをグループ単位で統合(ビルド時にAAOが結合)。統合後の想定SkinnedMesh数: {plan.beforeCount}→{plan.expectedCount}。");
            }
            else
            {
                // --- 顔以外を統合(単一セット。従来動作) ---
                List<SkinnedMeshRenderer> sources = ResolveSources(cloneRoot, plan.mergeSourcePaths, report);
                if (sources.Count < 2)
                {
                    report.Warn("SkinnedMesh統合: 複製内で解決できた統合対象が2件未満のため統合しませんでした。");
                    return 0;
                }
                if (!CreateMergeTarget(cloneRoot, msmType, sources, MergeTargetName, animatedKeys, protectedShapeNames, report)) return 0;
                totalMerged = sources.Count;
                report.Info($"SkinnedMesh統合を追加しました: 顔以外の{sources.Count}メッシュを1つ('{MergeTargetName}')へ統合(ビルド時にAAOが結合)。統合後の想定SkinnedMesh数: {plan.beforeCount}→{plan.expectedCount}。マテリアルスロットは同一マテリアルのサブメッシュがビルド時に重複排除されます。");
            }

            // --- Trace and Optimize を確保(EditSkinnedMeshComponent はこの存在下でビルド時に処理される) ---
            AAOMeshRemovalHelper.EnsureTraceAndOptimize(cloneRoot, report);

            // --- 顔レンダラーを AAO の自動統合からも除外する(明示ソースに入れていないが、
            //     VisemeSkinnedMesh/eyelidsSkinnedMesh 参照だけでは自動統合から外れないため) ---
            ExcludeFaceFromAutoMerge(cloneRoot, plan, report);

            report.Info("注: この統合はNDMFビルド時(Play/アップロード)に反映されます。保存された複製プレファブは統合前のメッシュ・スロット数のまま表示されます(既存のブレンドシェイプ削除・自動統合と同じ挙動)。");
            return totalMerged;
        }

        /// <summary>
        /// paths を複製内で SkinnedMeshRenderer として解決する(見つからない/型不一致はWarnしてスキップ、重複除去)。
        /// </summary>
        private static List<SkinnedMeshRenderer> ResolveSources(GameObject cloneRoot, List<string> paths, ConversionReport report)
        {
            var sources = new List<SkinnedMeshRenderer>();
            if (paths == null) return sources;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                Transform t = QuestCompat.FindByPath(cloneRoot.transform, path);
                if (t == null)
                {
                    report.Warn($"統合対象のレンダラーが複製内に見つかりませんでした(スキップ): {path}");
                    continue;
                }
                var smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr == null)
                {
                    report.Warn($"統合対象がSkinnedMeshRendererではないためスキップしました: {path}");
                    continue;
                }
                if (!sources.Contains(smr)) sources.Add(smr);
            }
            return sources;
        }

        /// <summary>
        /// 統合ターゲット GameObject(targetName)+ 空の SkinnedMeshRenderer + MergeSkinnedMesh を作り、
        /// sources を統合ソースとして登録する。成功なら true。失敗時は生成したGOを破棄して false を返す
        /// (絶対に例外を投げない。1グループの失敗が他グループを止めないようにする)。
        /// </summary>
        private static bool CreateMergeTarget(GameObject cloneRoot, Type msmType, List<SkinnedMeshRenderer> sources, string targetName, HashSet<string> animatedKeys, HashSet<string> protectedShapeNames, ConversionReport report)
        {
            GameObject targetGo = null;
            try
            {
                // --- 統合ターゲット GameObject + 空の SkinnedMeshRenderer を作る ---
                targetGo = new GameObject(targetName);
                Undo.RegisterCreatedObjectUndo(targetGo, "RARA SkinnedMesh Merge");
                targetGo.transform.SetParent(cloneRoot.transform, false);
                // 新規 GO なので、このターゲットが GameObject 上で唯一かつ最初の SkinnedMeshRenderer になる
                // (ObjectMappingContext がバインディングを再ポイントする前提条件を満たす)。
                SkinnedMeshRenderer targetSmr = Undo.AddComponent<SkinnedMeshRenderer>(targetGo);

                // --- MergeSkinnedMesh を付与し、既定挙動バージョン2を固定する ---
                Component msm = Undo.AddComponent(targetGo, msmType);
                if (msm == null)
                {
                    report.Warn($"MergeSkinnedMeshの追加に失敗したためSkinnedMesh統合をスキップしました('{targetName}')。");
                    UnityEngine.Object.DestroyImmediate(targetGo);
                    return false;
                }
                TryInitialize(msm, msmType, 2);

                // blendShapeMode=RenameToAvoidConflict / skipEnablementMismatchedRenderers=false を明示設定する。
                ConfigureMergeOptions(msm, report);

                // --- 統合ソースを登録する(公開API優先、失敗時 SerializedObject へフォールバック) ---
                if (!AddSources(msm, msmType, sources, report))
                {
                    report.Warn($"SkinnedMesh統合ソースの登録に失敗しました('{targetName}')。手動でMerge Skinned Meshの対象を設定してください。");
                    UnityEngine.Object.DestroyImmediate(targetGo);
                    return false;
                }

                // [A] 統合先SMRに Root Bone / Anchor Override(必要なら localBounds)を設定し、
                //     ビルド時の「Root Bone未設定」「Anchor Override未設定」警告を解消する。
                ConfigureMergeTargetHygiene(cloneRoot, targetSmr, sources, report);

                // [B] 統合対象間で同名ブレンドシェイプの現在値が食い違うものを、安全なら現在値で固定して衝突を解消する
                //     (見た目は変わらず、ビルド時の「BlendShapeの値が揃っていません」警告を解消する)。
                ResolveSameNameBlendShapeConflicts(cloneRoot, sources, animatedKeys, protectedShapeNames, report);

                EditorUtility.SetDirty(msm);
                return true;
            }
            catch (Exception ex)
            {
                report.Warn($"SkinnedMesh統合の設定に失敗しました('{targetName}'): {ex.Message}。統合を無効にするか手動でMerge Skinned Meshを設定してください。");
                if (targetGo != null) UnityEngine.Object.DestroyImmediate(targetGo);
                return false;
            }
        }

        /// <summary>
        /// blendShapeMode を RenameToAvoidConflict に、skipEnablementMismatchedRenderers を false に設定する。
        /// (Initialize(2) の既定に沿うが、Reset 未実行等でフィールド既定が残る場合に備えて明示的に上書きする。)
        /// </summary>
        private static void ConfigureMergeOptions(Component msm, ConversionReport report)
        {
            try
            {
                var so = new SerializedObject(msm);
                SerializedProperty blendMode = so.FindProperty("blendShapeMode");
                if (blendMode != null) blendMode.enumValueIndex = BlendShapeModeRenameToAvoidConflict;
                SerializedProperty skip = so.FindProperty("skipEnablementMismatchedRenderers");
                if (skip != null) skip.boolValue = false;
                so.ApplyModifiedProperties();
            }
            catch (Exception ex)
            {
                report.Warn($"MergeSkinnedMeshのオプション設定に失敗しました(既定値のまま続行): {ex.Message}");
            }
        }

        /// <summary>
        /// 統合ソース SkinnedMeshRenderer 群を MergeSkinnedMesh へ登録する。
        /// ルート1: 公開API SourceSkinnedMeshRenderers.UnionWith(IEnumerable&lt;SkinnedMeshRenderer&gt;)。
        /// ルート2: SerializedObject で renderersSet.mainSet へ直接追加(ネスト0の複製が前提)。
        /// いずれかで登録できたら true。
        /// </summary>
        private static bool AddSources(Component msm, Type msmType, List<SkinnedMeshRenderer> sources, ConversionReport report)
        {
            // ルート1: 公開API(PrefabSafeSetAccessor<SkinnedMeshRenderer>.UnionWith)
            try
            {
                PropertyInfo accessorProp = msmType.GetProperty("SourceSkinnedMeshRenderers", BindingFlags.Public | BindingFlags.Instance);
                if (accessorProp != null)
                {
                    object accessor = accessorProp.GetValue(msm);
                    if (accessor != null)
                    {
                        MethodInfo unionWith = accessor.GetType().GetMethod("UnionWith", BindingFlags.Public | BindingFlags.Instance);
                        if (unionWith != null)
                        {
                            unionWith.Invoke(accessor, new object[] { sources });
                            EditorUtility.SetDirty(msm);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Warn($"AAO公開APIでの統合ソース登録に失敗したためSerializedObjectで再試行します: {ex.Message}");
            }

            // ルート2: SerializedObject で renderersSet.mainSet へ直接追加
            try
            {
                var so = new SerializedObject(msm);
                SerializedProperty mainSet = so.FindProperty("renderersSet.mainSet");
                if (mainSet == null || !mainSet.isArray)
                {
                    report.Warn("AAOの統合ソース集合(renderersSet.mainSet)が取得できず統合ソースを設定できませんでした。手動で設定してください。");
                    return false;
                }

                var existing = new HashSet<int>();
                for (int i = 0; i < mainSet.arraySize; i++)
                {
                    var refObj = mainSet.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (refObj != null) existing.Add(refObj.GetInstanceID());
                }
                foreach (SkinnedMeshRenderer smr in sources)
                {
                    if (smr == null || existing.Contains(smr.GetInstanceID())) continue;
                    int idx = mainSet.arraySize;
                    mainSet.arraySize = idx + 1;
                    mainSet.GetArrayElementAtIndex(idx).objectReferenceValue = smr;
                    existing.Add(smr.GetInstanceID());
                }
                so.ApplyModifiedProperties();
                return true;
            }
            catch (Exception ex)
            {
                report.Warn($"SkinnedMesh統合ソースの設定に失敗しました: {ex.Message}。手動で設定してください。");
                return false;
            }
        }

        /// <summary>
        /// 顔レンダラー(plan.faceRendererPaths)を Trace and Optimize の除外(debugOptions.exclusions)へ
        /// 追加し、AAO の自動統合が顔を巻き込まないようにする。T&O が無い/失敗しても致命的ではない
        /// (顔は明示ソースに入れていないため、通常は自動統合の対象になっても孤立バケットとして残る)。
        /// </summary>
        private static void ExcludeFaceFromAutoMerge(GameObject cloneRoot, SkinnedMeshMergePlan plan, ConversionReport report)
        {
            if (plan.faceRendererPaths == null || plan.faceRendererPaths.Count == 0) return;

            Type taoType = QuestCompat.FindType(TraceAndOptimizeTypeName);
            if (taoType == null) return;

            try
            {
                Component tao = cloneRoot.GetComponentInChildren(taoType, true);
                if (tao == null) return;

                var faceGos = new List<GameObject>();
                foreach (string path in plan.faceRendererPaths)
                {
                    Transform t = QuestCompat.FindByPath(cloneRoot.transform, path);
                    if (t != null) faceGos.Add(t.gameObject);
                }
                if (faceGos.Count == 0) return;

                var so = new SerializedObject(tao);
                SerializedProperty exclusions = so.FindProperty("debugOptions.exclusions");
                if (exclusions == null || !exclusions.isArray) return;

                var existing = new HashSet<int>();
                for (int i = 0; i < exclusions.arraySize; i++)
                {
                    var refObj = exclusions.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (refObj != null) existing.Add(refObj.GetInstanceID());
                }
                foreach (GameObject go in faceGos)
                {
                    if (go == null || existing.Contains(go.GetInstanceID())) continue;
                    int idx = exclusions.arraySize;
                    exclusions.arraySize = idx + 1;
                    exclusions.GetArrayElementAtIndex(idx).objectReferenceValue = go;
                    existing.Add(go.GetInstanceID());
                }
                so.ApplyModifiedProperties();
                report.Info("顔メッシュをTrace and Optimizeの自動統合から除外しました(分離を確実に維持)。");
            }
            catch (Exception ex)
            {
                report.Warn($"顔メッシュの自動統合除外に失敗しました(顔は分離維持されますが念のため確認してください): {ex.Message}");
            }
        }

        // ================================================================
        // [A] 統合先のメッシュ衛生設定(Root Bone / Anchor Override / localBounds)
        // ================================================================

        /// <summary>
        /// 統合先(空の)SkinnedMeshRenderer に Root Bone と Anchor Override(probeAnchor)を設定する。
        /// これらは AAO MergeSkinnedMesh が統合先へ自動設定しないため、未設定だとビルド時に警告が出る。
        ///  ・rootBone   := ソース群の非null rootBone の最頻値。無ければヒューマノイドの Hips。どちらも無ければ未設定。
        ///  ・probeAnchor := ソース群の非null probeAnchor の最頻値。無ければ上で選んだ rootBone。
        ///  ・localBounds := rootBone を設定する場合は既定(ゼロ)にしておき、AAO がビルド時にソースから境界を再計算できるようにする
        ///    (MergeSkinnedMeshProcessor.MergeBounds は target.Bounds==default かつ rootBone!=null のときのみ再計算する)。
        /// 例外は投げない(統合自体を止めない)。
        /// </summary>
        private static void ConfigureMergeTargetHygiene(GameObject cloneRoot, SkinnedMeshRenderer targetSmr, List<SkinnedMeshRenderer> sources, ConversionReport report)
        {
            if (targetSmr == null) return;
            try
            {
                Transform rootBone = MostCommonNonNull(sources, s => s != null ? s.rootBone : null);
                if (rootBone == null) rootBone = GetHumanoidHips(cloneRoot);

                Transform probeAnchor = MostCommonNonNull(sources, s => s != null ? s.probeAnchor : null);
                if (probeAnchor == null) probeAnchor = rootBone;

                if (rootBone != null)
                {
                    targetSmr.rootBone = rootBone;
                    // AAO がビルド時に境界を再計算できるよう既定(ゼロ)にしておく(独自にセットすると再計算されず見切れる恐れ)。
                    targetSmr.localBounds = new Bounds(Vector3.zero, Vector3.zero);
                }
                if (probeAnchor != null)
                {
                    targetSmr.probeAnchor = probeAnchor;
                }

                if (rootBone != null || probeAnchor != null)
                {
                    string x = rootBone != null ? rootBone.name : "未設定";
                    string y = probeAnchor != null ? probeAnchor.name : "未設定";
                    report.Info($"統合先にRoot Bone({x})とAnchor Override({y})を設定しました。");
                }
                else
                {
                    report.Warn("統合先のRoot Bone/Anchor Overrideを決定できませんでした(ソースに設定が無く、ヒューマノイドのHipsも取得できません)。ビルド時に未設定の警告が出る場合があります。");
                }
            }
            catch (Exception ex)
            {
                report.Warn($"統合先のRoot Bone/Anchor Override設定に失敗しました: {ex.Message}");
            }
        }

        /// <summary>selector が返す非null Transform のうち最頻値(同数なら先に最大へ到達したもの)を返す。無ければ null。</summary>
        private static Transform MostCommonNonNull(List<SkinnedMeshRenderer> sources, Func<SkinnedMeshRenderer, Transform> selector)
        {
            if (sources == null) return null;
            var counts = new Dictionary<Transform, int>();
            Transform best = null;
            int bestCount = 0;
            foreach (SkinnedMeshRenderer s in sources)
            {
                Transform t = selector(s);
                if (t == null) continue;
                counts.TryGetValue(t, out int c);
                c++;
                counts[t] = c;
                if (c > bestCount) { bestCount = c; best = t; }
            }
            return best;
        }

        /// <summary>アバターがヒューマノイドなら Hips ボーンの Transform を返す(でなければ null)。</summary>
        private static Transform GetHumanoidHips(GameObject cloneRoot)
        {
            if (cloneRoot == null) return null;
            Animator animator = cloneRoot.GetComponent<Animator>();
            if (animator == null) animator = cloneRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman) return null;
            return animator.GetBoneTransform(HumanBodyBones.Hips);
        }

        // ================================================================
        // [B] 統合対象間の同名ブレンドシェイプ値の衝突を自動固定(FreezeBlendShape)で解消
        // ================================================================

        /// <summary>
        /// sources の間で、同名ブレンドシェイプが2つ以上のソースに存在し、かつ現在値(SkinnedMeshRenderer.GetBlendShapeWeight)が
        /// 食い違うもの(AAO の MergeSkinnedMeshProcessor が「BlendShapeの値が揃っていません」と警告する条件と同一)を検出する。
        /// 各衝突シェイプについて、(a) アニメーション未使用 かつ (b) ビセーム/まばたき/MMD標準モーフでない ものは、
        /// それを持つ全ソースに AAO FreezeBlendShape を付けて現在値で固定する(ビルド時に各メッシュへ焼き込まれ、シェイプが消える
        /// → 見た目は同一・警告は解消)。アニメ使用やビセーム等はレポート警告のみ(自動固定しない)。例外は投げない。
        /// </summary>
        private static void ResolveSameNameBlendShapeConflicts(
            GameObject cloneRoot,
            List<SkinnedMeshRenderer> sources,
            HashSet<string> animatedKeys,
            HashSet<string> protectedShapeNames,
            ConversionReport report)
        {
            if (cloneRoot == null || sources == null || sources.Count < 2) return;

            try
            {
                // name -> 各ソースでの (smr, path, weight)。同名が2ソース以上・値が異なるかを判定する。
                var byName = new Dictionary<string, List<(SkinnedMeshRenderer smr, string path, float weight)>>(StringComparer.Ordinal);
                foreach (SkinnedMeshRenderer smr in sources)
                {
                    if (smr == null) continue;
                    Mesh mesh = smr.sharedMesh;
                    if (mesh == null) continue;
                    string path = QuestCompat.GetRelativePath(cloneRoot.transform, smr.transform) ?? smr.gameObject.name;
                    int count = mesh.blendShapeCount;
                    for (int i = 0; i < count; i++)
                    {
                        string name = mesh.GetBlendShapeName(i);
                        if (string.IsNullOrEmpty(name)) continue;
                        float weight = smr.GetBlendShapeWeight(i);
                        if (!byName.TryGetValue(name, out var list))
                        {
                            list = new List<(SkinnedMeshRenderer smr, string path, float weight)>();
                            byName[name] = list;
                        }
                        list.Add((smr, path, weight));
                    }
                }

                Type freezeType = QuestCompat.FindType(FreezeBlendShapeTypeName);

                // 固定はソース1件につき1つの FreezeBlendShape へまとめて設定する。
                var freezePlan = new Dictionary<SkinnedMeshRenderer, List<string>>();
                var frozenShapeNames = new List<string>();

                foreach (var kv in byName)
                {
                    string name = kv.Key;
                    var list = kv.Value;
                    if (list.Count < 2) continue; // 2ソース以上に無ければ衝突しない

                    // AAO と同じ判定: 最初に見た値と異なる値があれば衝突(浮動小数の厳密比較)。
                    float first = list[0].weight;
                    bool conflict = false;
                    for (int i = 1; i < list.Count; i++)
                    {
                        // ReSharper disable once CompareOfFloatsByEqualityOperator
                        if (list[i].weight != first) { conflict = true; break; }
                    }
                    if (!conflict) continue;

                    bool isProtected = (protectedShapeNames != null && protectedShapeNames.Contains(name)) ||
                                       AAOMeshRemovalHelper.IsMmdStandardMorph(name);

                    // animatedKeys==null は走査失敗 → 安全側で全て「アニメ使用扱い」にして自動固定しない。
                    bool isAnimated = animatedKeys == null;
                    if (!isAnimated)
                    {
                        foreach (var e in list)
                        {
                            if (animatedKeys.Contains(e.path + "\n" + name)) { isAnimated = true; break; }
                        }
                    }

                    if (isAnimated || isProtected)
                    {
                        string reason = isAnimated
                            ? "アニメーション使用のため自動固定しません"
                            : "ビセーム/まばたき/MMD標準モーフのため自動固定しません";
                        report.Warn($"同名ブレンドシェイプ '{name}' の値が統合対象間で異なります({reason})。見た目が意図と違う場合は該当メッシュを統合から除外してください。");
                        continue;
                    }

                    if (freezeType == null)
                    {
                        // AAO は解決済み(MergeSkinnedMesh が見つかっている)ため通常ここには来ない。保険。
                        report.Warn($"同名ブレンドシェイプ '{name}' の値が統合対象間で異なりますが、AAO Freeze BlendShapeが見つからないため自動固定できませんでした。手動で値を揃えるか固定してください。");
                        continue;
                    }

                    // その名前を持つ全ソースを、各自の現在値で固定する(それぞれの見た目は変わらない)。
                    foreach (var e in list)
                    {
                        if (!freezePlan.TryGetValue(e.smr, out var names))
                        {
                            names = new List<string>();
                            freezePlan[e.smr] = names;
                        }
                        if (!names.Contains(name)) names.Add(name);
                    }
                    frozenShapeNames.Add(name);
                }

                if (freezePlan.Count == 0) return;

                foreach (var kv in freezePlan)
                {
                    SkinnedMeshRenderer smr = kv.Key;
                    if (smr == null) continue;
                    string path = QuestCompat.GetRelativePath(cloneRoot.transform, smr.transform) ?? smr.gameObject.name;
                    try
                    {
                        // FreezeBlendShape は DisallowMultipleComponent。既存があれば再利用し、無ければ追加する。
                        Component freeze = smr.GetComponent(freezeType);
                        if (freeze == null) freeze = Undo.AddComponent(smr.gameObject, freezeType);
                        if (freeze == null)
                        {
                            report.Warn($"AAO Freeze BlendShapeの追加に失敗したため同名ブレンドシェイプの自動固定をスキップしました: {path}");
                            continue;
                        }
                        AddFreezeShapeKeys(freeze, kv.Value, report, path);
                    }
                    catch (Exception ex)
                    {
                        report.Warn($"同名ブレンドシェイプの自動固定に失敗しました({path}): {ex.Message}");
                    }
                }

                if (frozenShapeNames.Count > 0)
                {
                    report.Info($"同名ブレンドシェイプの値の衝突を解消するため、{string.Join(", ", frozenShapeNames)} を現在の値で固定しました(AAOがビルド時に各メッシュへ焼き込むため見た目は変わりません)。");
                }
            }
            catch (Exception ex)
            {
                report.Warn($"同名ブレンドシェイプ衝突の解消に失敗しました: {ex.Message}。統合は続行します。");
            }
        }

        /// <summary>
        /// FreezeBlendShape のシェイプ名集合(PrefabSafeSet&lt;string&gt; の shapeKeysSet)へ names を追加する。
        /// FreezeBlendShape は internal 型で公開スクリプティングAPIが無いため、SerializedObject で shapeKeysSet.mainSet へ
        /// 直接追加する(RemoveMeshByBlendShape のルート2と同じ作法。ネスト0の複製が前提)。追加できたら true。
        /// </summary>
        private static bool AddFreezeShapeKeys(Component freeze, List<string> names, ConversionReport report, string rendererPath)
        {
            try
            {
                var so = new SerializedObject(freeze);
                SerializedProperty mainSet = so.FindProperty("shapeKeysSet.mainSet");
                if (mainSet == null || !mainSet.isArray)
                {
                    report.Warn($"AAO Freeze BlendShapeのシェイプ集合(shapeKeysSet.mainSet)が取得できず自動固定を設定できませんでした({rendererPath})。");
                    return false;
                }

                var existing = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < mainSet.arraySize; i++)
                {
                    existing.Add(mainSet.GetArrayElementAtIndex(i).stringValue);
                }
                foreach (string name in names)
                {
                    if (string.IsNullOrEmpty(name) || existing.Contains(name)) continue;
                    int idx = mainSet.arraySize;
                    mainSet.arraySize = idx + 1;
                    mainSet.GetArrayElementAtIndex(idx).stringValue = name;
                    existing.Add(name);
                }
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(freeze);
                return true;
            }
            catch (Exception ex)
            {
                report.Warn($"同名ブレンドシェイプの自動固定(シェイプ名設定)に失敗しました({rendererPath}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// cloneRoot から到達可能な全アニメーションクリップを走査し、アニメーションされている blendShape を
        /// キー "path\nname"(path はアバタールート相対レンダラーパス)の集合として返す。収集範囲・作法は
        /// ComponentRemover.CollectPhysBoneTogglePaths と同じ(ルートのコントローラー + 子コンポーネント参照を前置)。
        /// 走査に失敗した場合は null を返す(呼び出し側は安全側で一切自動固定しない)。
        /// </summary>
        private static HashSet<string> CollectAnimatedBlendShapeKeys(GameObject root, ConversionReport report)
        {
            if (root == null) return new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);

                var seenClips = new HashSet<AnimationClip>();
                foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(root))
                {
                    if (controller == null) continue;
                    foreach (AnimationClip clip in controller.animationClips)
                    {
                        if (clip == null || !seenClips.Add(clip)) continue;
                        AddBlendShapeKeysFromClip(clip, string.Empty, keys);
                    }
                }

                // 子オブジェクトの Animator / MA Merge Animator 等が参照するコントローラーは、
                // バインディングパスがそのコンポーネント基準の可能性があるため、位置を前置したパスも追加する
                // (判定が広がる=固定を控える方向にしか働かないため安全側)。
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
                            AddBlendShapeKeysFromClip(clip, prefix, keys);
                        }
                    }
                }
                return keys;
            }
            catch (Exception ex)
            {
                report.Warn($"アニメーション使用ブレンドシェイプの走査に失敗したため、同名シェイプの自動固定は行いません(安全側): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// クリップ内の blendShape.&lt;name&gt; バインディングを、prefix(コントローラー所有オブジェクトのルート相対パス。
        /// ルート相対なら空文字)を前置したレンダラーパスと組み合わせ、"path\nname" として keys へ追加する。
        /// </summary>
        private static void AddBlendShapeKeysFromClip(AnimationClip clip, string prefix, HashSet<string> keys)
        {
            const string BlendShapePrefix = "blendShape.";
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName == null ||
                    !binding.propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal)) continue;
                string shape = binding.propertyName.Substring(BlendShapePrefix.Length);
                if (shape.Length == 0) continue;

                string path = binding.path ?? string.Empty;
                string full;
                if (prefix.Length == 0) full = path;
                else full = path.Length == 0 ? prefix : prefix + "/" + path;

                keys.Add(full + "\n" + shape);
            }
        }

        /// <summary>
        /// アバターの VRCAvatarDescriptor が参照するビセーム(VisemeBlendShapes)・まばたき(eyelidsBlendshapes)の
        /// シェイプ名集合を返す。これらは自動固定([B])の対象外とし、誤って固定・除去しないためのガードに使う。
        /// Descriptor が無い/未設定なら空集合。例外時も空集合(MMDガードは別途効くため致命的ではない)。
        /// </summary>
        private static HashSet<string> CollectProtectedFaceShapeNames(GameObject cloneRoot)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (cloneRoot == null) return set;
            try
            {
                VRCAvatarDescriptor descriptor = cloneRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (descriptor == null) return set;

                // ビセーム(リップシンクがブレンドシェイプ方式のとき)。
                if (descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape &&
                    descriptor.VisemeBlendShapes != null)
                {
                    foreach (string v in descriptor.VisemeBlendShapes)
                    {
                        if (!string.IsNullOrEmpty(v)) set.Add(v);
                    }
                }

                // まばたき/視線(まぶたがブレンドシェイプ方式のとき、eyelidsBlendshapes のインデックスをシェイプ名へ解決)。
                if (descriptor.enableEyeLook)
                {
                    VRCAvatarDescriptor.CustomEyeLookSettings eye = descriptor.customEyeLookSettings;
                    if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes &&
                        eye.eyelidsSkinnedMesh != null && eye.eyelidsSkinnedMesh.sharedMesh != null &&
                        eye.eyelidsBlendshapes != null)
                    {
                        Mesh mesh = eye.eyelidsSkinnedMesh.sharedMesh;
                        foreach (int idx in eye.eyelidsBlendshapes)
                        {
                            if (idx >= 0 && idx < mesh.blendShapeCount) set.Add(mesh.GetBlendShapeName(idx));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // ベストエフォート(ガードが空でも MMD ガード・アニメガードは効く)。
            }
            return set;
        }

        /// <summary>
        /// AAO コンポーネントの Initialize(int) を(存在すれば)呼んで既定挙動バージョンを固定する。
        /// 見つからない・失敗しても致命的ではないため握りつぶす(コンポーネント自体は機能する)。
        /// </summary>
        private static void TryInitialize(Component component, Type type, int version)
        {
            try
            {
                MethodInfo initialize = type.GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int) },
                    null);
                if (initialize != null) initialize.Invoke(component, new object[] { version });
            }
            catch (Exception)
            {
                // ベストエフォート(Initialize が無い/失敗してもコンポーネントは動作する)
            }
        }
    }
}
#endif
