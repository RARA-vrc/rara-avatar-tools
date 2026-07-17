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
                    if (CreateMergeTarget(cloneRoot, msmType, sources, MergeTargetNameForGroup(group.groupIndex), report))
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
                if (!CreateMergeTarget(cloneRoot, msmType, sources, MergeTargetName, report)) return 0;
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
        private static bool CreateMergeTarget(GameObject cloneRoot, Type msmType, List<SkinnedMeshRenderer> sources, string targetName, ConversionReport report)
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
                Undo.AddComponent<SkinnedMeshRenderer>(targetGo);

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
