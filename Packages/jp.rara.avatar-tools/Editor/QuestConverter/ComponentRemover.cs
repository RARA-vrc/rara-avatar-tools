// RARA Quest Converter - コンポーネント整理
// Unityコンストレイントの変換、Android非対応コンポーネントの削除、
// PhysBoneのマージ(プレビュー・手動選択対応)・Poor上限調整を行う。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace RARA.QuestConverter
{
    /// <summary>
    /// PhysBoneマージプレビューの1行分。
    /// ・isGroup = true: マージされるグループ(共通親 parentPath 配下の memberPaths が1本になる)
    /// ・isGroup = false: マージされない単独PhysBone(singlePath)と、その理由(skipReason)
    /// memberPaths / singlePath は ComponentRemover.GetPhysBoneIdentityPath 形式の識別パスで、
    /// そのまま QuestConvertSettings.physBoneRemovePaths / physBoneNoMergePaths に保存できる。
    /// </summary>
    public class PhysBonePreviewRow
    {
        /// <summary>マージされるグループ(2本以上→1本)ならtrue、単独ならfalse。</summary>
        public bool isGroup;

        /// <summary>グループ行: マージ先を置く共通親のルート相対パス(ルート直下ならアバタールート="")。</summary>
        public string parentPath;

        /// <summary>グループ行: メンバーPhysBoneの識別パス(選択の保存にはこちらを使う)。</summary>
        public List<string> memberPaths;

        /// <summary>グループ行: メンバーの短い表示名(PhysBoneが付いているGameObject名)。</summary>
        public List<string> memberLabels;

        /// <summary>単独行: PhysBoneの識別パス。</summary>
        public string singlePath;

        /// <summary>単独行: マージされない理由(設定不一致/アニメ制御あり/手動でマージ除外 など)。</summary>
        public string skipReason;

        /// <summary>
        /// 影響Transform数の概算(チェーンルート配下をignoreTransformsを除いて数えた値。
        /// グループ行は共通親1+全メンバーの合計)。VRChatの正式カウント
        /// (エンドポイントボーン等)とは一致しない目安値。
        /// </summary>
        public int transformCount;

        /// <summary>
        /// グループ行: ルーズマージ(設定が異なる兄弟チェーンを先頭メンバーの設定へ統一して
        /// マージした)で作られた行なら true。設定が完全一致で従来どおりマージされた行は false。
        /// </summary>
        public bool looseMerged;

        /// <summary>
        /// グループ行: ルーズマージで先頭メンバーと設定が異なっていたメンバーの短い説明
        /// (「メンバー名(差異: プロパティ名)」形式)。設定差が無い場合は空リスト。
        /// looseMerged が true のときにUIの注意書きへ出す。単独行では null。
        /// </summary>
        public List<string> settingsDiffMembers;

        /// <summary>
        /// 自動選択の優先度スコア(QuestCompat.GetPhysBonePriorityScore の最小値。小さいほど高優先、
        /// int.MaxValue はキーワード非該当)。グループ行は共通親末尾名+全メンバー名、
        /// 単独行はPhysBoneのGameObject名+チェーンルート名から算出する。
        /// </summary>
        public int priorityScore;
    }

    /// <summary>
    /// PhysBoneマージのドライランプレビュー結果。
    /// ComponentRemover.PreviewPhysBoneMerge が返す(アバターへの変更は一切行わない)。
    /// </summary>
    public class PhysBonePreview
    {
        /// <summary>プレビュー行(マージグループ→単独の順)。</summary>
        public List<PhysBonePreviewRow> rows;

        /// <summary>現在のPhysBoneコンポーネント数(EditorOnly配下を除く実数。削除指定分も含む)。</summary>
        public int currentComponentCount;

        /// <summary>削除・マージ適用後の予測コンポーネント数(グループ数+単独数)。</summary>
        public int projectedComponentCount;

        /// <summary>
        /// 削除指定のみ適用しマージしなかった場合の予測コンポーネント数(マージ候補数)。
        /// mergePhysBones がオフのとき、実際に残る数はこちら(マージ削減は起きない)。
        /// </summary>
        public int nonMergedComponentCount;

        /// <summary>
        /// [1.5.1] EditorOnly / Quest除外(ビルド除外)配下のため一覧・現在数・予測数から隠したPhysBone本数。
        /// 0 より大きいとき、セクションに「n 件を非表示」の注記を1行だけ表示する(仕様[C])。
        /// </summary>
        public int hiddenExcludedCount;
    }

    /// <summary>
    /// Quest(Android)アバターで使用できないコンポーネントの整理を担当する。
    /// AvatarQuestConverter から複製後のアバターに対して呼び出される。
    /// </summary>
    public static class ComponentRemover
    {
        /// <summary>変換対象となるUnityコンストレイント6種。</summary>
        private static readonly Type[] UnityConstraintTypes =
        {
            typeof(PositionConstraint),
            typeof(RotationConstraint),
            typeof(ScaleConstraint),
            typeof(ParentConstraint),
            typeof(AimConstraint),
            typeof(LookAtConstraint),
        };

        /// <summary>
        /// root配下にUnityコンストレイントがあれば、SDKの変換APIでVRCConstraintへ変換する。
        /// 失敗時は警告のみ残す(残ったUnityコンストレイントは RemoveUnsupported で削除される)。
        ///
        /// 【重要】SDKの ConvertUnityConstraintsAcrossGameObjects は、デスクリプター経由で参照される
        /// アニメーションクリップをその場で書き換える。複製アバターのクリップは元アバターと
        /// 同一アセットを共有しているため、それを使うと元アバター側のクリップまで破壊してしまう。
        /// そのためクリップ自動変換なしの下位API(DoConvertUnityConstraints)でコンポーネントのみ変換し、
        /// 本ツールが出力フォルダへ複製したクリップに限ってコンストレイント参照を書き換える。
        /// </summary>
        public static void ConvertUnityConstraints(GameObject root, string outputDir, ConversionReport report)
        {
            int beforeCount = CountUnityConstraints(root);
            if (beforeCount == 0)
            {
                report.Info("Unityコンストレイントは見つかりませんでした(変換不要)。");
                return;
            }

            report.Info($"Unityコンストレイント {beforeCount} 件をVRCConstraintへ変換します。");

            try
            {
                // VRChat SDK 3.10.4: クリップ自動変換なし(第3引数=false)でコンポーネントのみ変換する。
                // avatarDescriptor はクリップ変換にのみ使われるため null でよい。
                IConstraint[] unityConstraints = root.GetComponentsInChildren<IConstraint>(true);
                VRC.SDK3.Avatars.AvatarDynamicsSetup.DoConvertUnityConstraints(unityConstraints, null, false);

                // 本ツールが複製した(=元アバターと共有していない)クリップのみ書き換える
                RebindGeneratedConstraintClips(root, outputDir, report);
            }
            catch (Exception ex)
            {
                report.Warn($"コンストレイント変換中に例外が発生しました: {ex.Message}(残ったUnityコンストレイントは非対応コンポーネントとして削除されます)");
                return;
            }

            int afterCount = CountUnityConstraints(root);
            if (afterCount == 0)
            {
                report.Info($"Unityコンストレイント {beforeCount} 件をVRCConstraintへ変換しました。");
            }
            else
            {
                // SDK側での変換失敗
                report.Warn($"Unityコンストレイントが {afterCount} 件変換されずに残っています(変換失敗)。残りは非対応コンポーネントとして削除されます。");
            }
        }

        /// <summary>
        /// root配下から到達可能なクリップのうち、Unityコンストレイントをアニメーションしているものについて:
        /// ・出力フォルダ配下(本ツールが複製した固有アセット)→ VRCConstraint 参照へ書き換える
        /// ・それ以外(元アバターと共有しているアセット)→ 書き換えずに警告する
        /// </summary>
        private static void RebindGeneratedConstraintClips(GameObject root, string outputDir, ConversionReport report)
        {
            string prefix = string.IsNullOrEmpty(outputDir)
                ? null
                : outputDir.Replace('\\', '/').TrimEnd('/') + "/";

            // R6: MA Merge Animator 経由で統合されるコントローラーに含まれるクリップ集合(MA未導入なら空)。
            // 共有クリップの警告に「Merge Animator 経由」である旨を追記して、原因の特定を助ける。
            HashSet<AnimationClip> mergeAnimatorClips = MACompatAudit.CollectMergeAnimatorClips(root);

            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(root))
            {
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    if (!ClipAnimatesUnityConstraint(clip)) continue;

                    string path = AssetDatabase.GetAssetPath(clip);
                    bool isGenerated = prefix != null && !string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.Ordinal);
                    if (isGenerated)
                    {
                        // 引数なし(oldConstraint = null)でUnityコンストレイント対象の全カーブを書き換える
                        VRC.SDK3.Avatars.AvatarDynamicsSetup.RebindConstraintAnimationClip(clip);
                        report.Info($"複製クリップ「{clip.name}」のコンストレイントアニメーションをVRCConstraint向けへ書き換えました。");
                    }
                    else
                    {
                        string maNote = mergeAnimatorClips.Contains(clip)
                            ? "(このクリップは Modular Avatar の Merge Animator 経由で統合されるコントローラーに含まれます)"
                            : string.Empty;
                        report.Warn($"クリップ「{clip.name}」はUnityコンストレイントをアニメーションしていますが、元アバターと共有しているため書き換えません(元アバター保護){maNote}。変換後アバターではこのコンストレイントアニメーションが機能しない可能性があります。");
                    }
                }
            }
        }

        /// <summary>クリップにUnityコンストレイント(IConstraint実装型)を対象とするカーブが含まれるか。</summary>
        private static bool ClipAnimatesUnityConstraint(AnimationClip clip)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != null && typeof(IConstraint).IsAssignableFrom(binding.type)) return true;
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (binding.type != null && typeof(IConstraint).IsAssignableFrom(binding.type)) return true;
            }
            return false;
        }

        /// <summary>
        /// root配下のAndroid非対応コンポーネント(Cloth/Camera/Light/DynamicBone等)を削除する。
        /// QuestCompat.FindUnsupportedComponents が依存関係を満たす削除順で列挙する前提。
        /// </summary>
        public static void RemoveUnsupported(GameObject root, ConversionReport report)
        {
            var comps = QuestCompat.FindUnsupportedComponents(root);
            if (comps.Count == 0)
            {
                report.Info("Android非対応コンポーネントは見つかりませんでした。");
                return;
            }

            int removedCount = 0;
            foreach (var comp in comps)
            {
                // 先行する削除の連鎖(RequireComponent等)で既に破棄されている場合があるためnullガード
                if (comp == null) continue;

                string typeName = comp.GetType().Name;
                string path = GetHierarchyPath(comp.transform, root.transform);
                try
                {
                    UnityEngine.Object.DestroyImmediate(comp);
                    removedCount++;
                    report.Warn($"削除: {typeName} ({path})");
                }
                catch (Exception ex)
                {
                    report.Warn($"削除失敗: {typeName} ({path}) - {ex.Message}");
                }
            }
            report.Info($"Android非対応コンポーネントを {removedCount} 件削除しました。");
        }

        /// <summary>
        /// PhysBoneコンポーネント/コライダーがAndroidのPoor上限(8/16)を超える場合、
        /// 階層の浅い順に残して超過分を削除する。削除後、残ったPhysBoneのコライダーリストから
        /// 参照切れ(null/missing)エントリを除去する。
        /// </summary>
        public static void TrimPhysBones(GameObject root, ConversionReport report)
        {
            // EditorOnly(Quest除外)サブツリー配下はVRChatビルドで除去されるため、
            // 集計・削除の対象から除外する(QuestDiagnosticsの集計と整合させる)。
            // 階層の浅い順(ルートに近いものほど重要とみなして残す)。OrderByは安定ソート。
            List<VRCPhysBone> physBones = root.GetComponentsInChildren<VRCPhysBone>(true)
                .Where(pb => !IsInEditorOnlySubtree(pb.transform, root.transform))
                .OrderBy(pb => GetHierarchyDepth(pb.transform, root.transform))
                .ToList();
            List<VRCPhysBoneCollider> colliders = root.GetComponentsInChildren<VRCPhysBoneCollider>(true)
                .Where(c => !IsInEditorOnlySubtree(c.transform, root.transform))
                .OrderBy(c => GetHierarchyDepth(c.transform, root.transform))
                .ToList();

            int boneOver = physBones.Count - QuestLimits.PoorPhysBoneComponents;
            int colliderOver = colliders.Count - QuestLimits.PoorPhysBoneColliders;

            if (boneOver <= 0 && colliderOver <= 0)
            {
                report.Info($"PhysBoneはPoor上限内です(コンポーネント {physBones.Count}/{QuestLimits.PoorPhysBoneComponents}、コライダー {colliders.Count}/{QuestLimits.PoorPhysBoneColliders})。");
                return;
            }

            int removedBones = 0;
            int removedColliders = 0;

            // --- 超過PhysBoneコンポーネントの削除 ---
            for (int i = QuestLimits.PoorPhysBoneComponents; i < physBones.Count; i++)
            {
                VRCPhysBone pb = physBones[i];
                if (pb == null) continue;
                report.Warn($"PhysBone削除(Poor上限 {QuestLimits.PoorPhysBoneComponents} 超過): {GetHierarchyPath(pb.transform, root.transform)}");
                UnityEngine.Object.DestroyImmediate(pb);
                removedBones++;
            }

            // --- 超過PhysBoneコライダーの削除 ---
            for (int i = QuestLimits.PoorPhysBoneColliders; i < colliders.Count; i++)
            {
                VRCPhysBoneCollider collider = colliders[i];
                if (collider == null) continue;
                report.Warn($"PhysBoneコライダー削除(Poor上限 {QuestLimits.PoorPhysBoneColliders} 超過): {GetHierarchyPath(collider.transform, root.transform)}");
                UnityEngine.Object.DestroyImmediate(collider);
                removedColliders++;
            }

            // --- 残ったPhysBoneのコライダーリストから参照切れエントリを除去 ---
            int keepCount = Mathf.Min(physBones.Count, QuestLimits.PoorPhysBoneComponents);
            int cleanedEntries = RemoveMissingColliderEntries(physBones.Take(keepCount));

            report.Info($"PhysBoneをPoor上限内に調整しました(コンポーネント削除 {removedBones} 件 / コライダー削除 {removedColliders} 件 / 参照切れエントリ除去 {cleanedEntries} 件)。");
        }

        /// <summary>
        /// [1.11.0][B] どのPhysBone(残存・非EditorOnly)からも参照されていない VRCPhysBoneCollider を
        /// 複製アバターから削除する(実行時カウント対象外の「浮いた」コライダーの掃除)。
        /// PhysBoneの選択・マージ・削除がすべて終わった後に呼ぶこと(残るPhysBoneが確定してから参照を判定する)。
        /// 変換(複製)専用。元アバターは触らない。削除数を返す。
        /// </summary>
        public static int RemoveUnreferencedColliders(GameObject root, ConversionReport report)
        {
            if (root == null) return 0;

            // 残存(非EditorOnly)PhysBoneが参照するコライダー集合
            var referenced = new HashSet<VRCPhysBoneColliderBase>();
            foreach (VRCPhysBone pb in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (pb == null || IsInEditorOnlySubtree(pb.transform, root.transform)) continue;
                if (pb.colliders == null) continue;
                foreach (VRCPhysBoneColliderBase c in pb.colliders)
                {
                    if (c != null) referenced.Add(c);
                }
            }

            var doomed = new List<VRCPhysBoneCollider>();
            foreach (VRCPhysBoneCollider collider in root.GetComponentsInChildren<VRCPhysBoneCollider>(true))
            {
                if (collider == null) continue;
                if (IsInEditorOnlySubtree(collider.transform, root.transform)) continue; // ビルドで消えるため対象外
                if (!referenced.Contains(collider)) doomed.Add(collider);
            }

            if (doomed.Count == 0)
            {
                report.Info("未参照のPhysBoneコライダーはありませんでした(掃除不要)。");
                return 0;
            }

            int removed = 0;
            foreach (VRCPhysBoneCollider collider in doomed)
            {
                if (collider == null) continue;
                UnityEngine.Object.DestroyImmediate(collider);
                removed++;
            }
            report.Info($"どのPhysBoneからも参照されていないコライダーを {removed} 件削除しました(コライダー数の削減)。");
            return removed;
        }

        /// <summary>
        /// 各PhysBoneのコライダーリストから参照切れ(null / 破棄済みmissing)エントリを除去し、
        /// 除去したエントリ数を返す。TrimPhysBones と RemoveSelectedPhysBones の共通後始末。
        /// シリアライズ上のフィールド名は "colliders"(VRCPhysBoneBase.colliders / プレハブYAMLで確認済み)。
        /// </summary>
        private static int RemoveMissingColliderEntries(IEnumerable<VRCPhysBone> physBones)
        {
            int cleanedEntries = 0;
            foreach (VRCPhysBone pb in physBones)
            {
                if (pb == null) continue;

                var so = new SerializedObject(pb);
                SerializedProperty collidersProp = so.FindProperty("colliders");
                if (collidersProp == null || !collidersProp.isArray) continue;

                bool changed = false;
                for (int j = collidersProp.arraySize - 1; j >= 0; j--)
                {
                    SerializedProperty element = collidersProp.GetArrayElementAtIndex(j);
                    if (element.objectReferenceValue == null)
                    {
                        // 破棄済み(missing)参照は一度nullにしてから削除する(Unityバージョン差異への保険)
                        element.objectReferenceValue = null;
                        collidersProp.DeleteArrayElementAtIndex(j);
                        changed = true;
                        cleanedEntries++;
                    }
                }
                if (changed)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            return cleanedEntries;
        }

        // ================================================================
        // PhysBoneマージ(揺れを維持したままコンポーネント数を削減)
        // ================================================================

        /// <summary>
        /// マージの設定比較で無視するシリアライズプロパティ。
        /// Unity内部のコンポーネント共通ヘッダーと、マージ側で再構成するフィールドのみ。
        /// これ以外の全プロパティ(version・forces・limits・collision・stretch・grab/pose・options・各カーブ等)は
        /// 完全一致を要求する(Avatar OptimizerのMergePhysBoneバリデーターと同じ方針)。
        /// </summary>
        private static readonly HashSet<string> MergeIgnoredProperties = new HashSet<string>
        {
            // Unity内部(コンポーネント共通ヘッダー)
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier",
            // マージ側で再構成するフィールド
            "rootTransform",    // マージ後は共通親を指す
            "ignoreTransforms", // メンバーの和集合+非メンバー子で再構成する
            "colliders",        // 順不同の集合として別途比較する(等しいことを確認の上、先頭メンバーの値を使う)
        };

        /// <summary>
        /// root配下から到達可能な全アニメーションクリップを走査し、GameObjectのアクティブ状態
        /// (m_IsActive)またはPhysBoneのプロパティ(m_Enabled含む)をアニメーションしている
        /// 対象パスの集合を返す。MergePhysBones の除外判定(アニメ制御ありのPhysBoneは
        /// マージしない)に使う。収集範囲は AnimationConverter.CollectControllers と共通。
        /// </summary>
        public static HashSet<string> CollectPhysBoneTogglePaths(GameObject root)
        {
            var paths = new HashSet<string>();

            // 全コントローラーのバインディングパスをアバタールート相対とみなして収集(FXレイヤー等の主経路)
            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(root))
            {
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    AddTogglePathsFromClip(clip, string.Empty, paths);
                }
            }

            // ルート以外のコンポーネント(子オブジェクトのAnimatorやModular AvatarのMerge Animator等)が
            // 参照するコントローラーは、バインディングパスがそのコンポーネント基準の可能性があるため、
            // コンポーネント位置を前置したパスも追加する。判定が広がる方向(=マージ対象外が増える)に
            // しか働かないため安全側。
            var seenPrefixedClips = new HashSet<string>();
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
                        if (!seenPrefixedClips.Add(clip.GetInstanceID() + "|" + prefix)) continue;
                        AddTogglePathsFromClip(clip, prefix, paths);
                    }
                }
            }
            return paths;
        }

        /// <summary>
        /// クリップ内のトグル対象バインディング(GameObjectアクティブ/PhysBoneプロパティ)のパスを、
        /// prefix(コントローラー所有オブジェクトのルート相対パス。ルート相対なら空文字)を前置してpathsへ追加する。
        /// </summary>
        private static void AddTogglePathsFromClip(AnimationClip clip, string prefix, HashSet<string> paths)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == null) continue;
                bool isActiveToggle = binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive";
                bool isPhysBoneProperty = typeof(VRCPhysBoneBase).IsAssignableFrom(binding.type);
                if (!isActiveToggle && !isPhysBoneProperty) continue;

                string path = binding.path ?? string.Empty;
                if (prefix.Length == 0)
                {
                    paths.Add(path);
                }
                else
                {
                    // 空パスはコントローラー所有オブジェクト自身を指す
                    paths.Add(path.Length == 0 ? prefix : prefix + "/" + path);
                }
            }
        }

        /// <summary>
        /// 設定が一致する兄弟PhysBoneチェーンをマージする(従来互換の3引数版)。
        /// 手動マージ除外(noMergePaths)なしで4引数版へ委譲する。
        /// </summary>
        public static int MergePhysBones(GameObject root, HashSet<string> animatedTogglePaths, ConversionReport report)
        {
            return MergePhysBones(root, animatedTogglePaths, null, report);
        }

        /// <summary>
        /// 設定が一致する兄弟PhysBoneチェーン(実効ルート = rootTransform ?? transform の親が同じ)を
        /// 1つのVRCPhysBoneへマージし、揺れを維持したままコンポーネント数を削減する。
        /// 削減できたコンポーネント数を返す。TrimPhysBones(削除)より先に呼ぶこと。
        /// 候補収集・除外判定・グループ分けは PlanPhysBoneMerge に集約されており、
        /// PreviewPhysBoneMerge(ドライラン)が示す計画と同じ内容を実行する。
        ///
        /// マージ方式(Avatar Optimizer の自動マージと同じ原理。ただし階層は一切変更しない):
        /// ・共通親Pにマージ先VRCPhysBoneを追加し、rootTransform=P・multiChildType=Ignore にする。
        ///   Ignoreのルートは揺れの計算対象にならず、Pの子(=各メンバーのチェーンルート)が
        ///   それぞれ独立したチェーンとして従来どおり揺れる。
        /// ・Pの子のうちメンバーのチェーンルートでないものは ignoreTransforms へ追加して対象外にする
        ///   (ボーンの移動・付け替えは行わないため、アニメーションパスやスキンウェイトに影響しない)。
        /// ・マージ対象外(安全側): パラメータ使用 / multiChildTypeがIgnore以外 / カーブ使用
        ///   (ルートが1段深くなるとカーブの意味が変わる) / アニメーションによるON/OFF・プロパティ制御 /
        ///   非アクティブ / 設定不一致 / noMergePathsによる手動除外。対象外の理由はすべてレポートに残す。
        /// ・QuestConvertSettings.physBoneRemovePaths で削除指定されたPhysBoneは、
        ///   RemoveSelectedPhysBones が本メソッドより先に複製アバターから削除している前提
        ///   (未削除で残っていても通常の候補として扱われるだけで、例外にはならない)。
        /// </summary>
        /// <param name="noMergePaths">手動でマージから除外するPhysBoneの識別パス
        /// (GetPhysBoneIdentityPath形式)。nullまたは空なら除外なし。</param>
        public static int MergePhysBones(GameObject root, HashSet<string> animatedTogglePaths, List<string> noMergePaths, ConversionReport report)
        {
            return MergePhysBones(root, animatedTogglePaths, noMergePaths, false, report);
        }

        /// <summary>
        /// 上の4引数版に「ルーズマージ」を加えた版。looseMerge=true のとき、同じ共通親を持ち
        /// ハードゲート(パラメータ未使用・アニメ制御なし・カーブなし・multiChildType==Ignore・
        /// 同一チェーンルートを共有しない・非アクティブでない・EditorOnly/除外外)を通過した兄弟チェーンは、
        /// 設定が一致しなくても先頭メンバーの設定へ統一して1本にマージする(揺れ方が先頭チェーンに揃う。
        /// コライダー等も先頭メンバーのものが使われる)。設定が異なっていたメンバーはレポートに残す。
        /// looseMerge=false のときは従来どおり設定完全一致のチェーンのみマージする(挙動は完全に同一)。
        /// </summary>
        public static int MergePhysBones(GameObject root, HashSet<string> animatedTogglePaths, List<string> noMergePaths, bool looseMerge, ConversionReport report)
        {
            // 削除指定分は既にクローン上に存在しない前提のため、removePaths=nullで計画する。
            // クローンは Quest除外・透過レンダラー自動非表示が既にEditorOnly化済みのため excludedRoots は不要(null)。
            PhysBoneMergePlan plan = PlanPhysBoneMerge(root, animatedTogglePaths, null, noMergePaths, null, looseMerge);
            if (plan.candidates.Count == 0)
            {
                report.Info("PhysBoneが見つからないためマージをスキップしました。");
                return 0;
            }

            // --- 対象外の理由ごとの集計(従来どおり理由単位でまとめて報告する) ---
            var skippedByReason = new Dictionary<string, List<string>>();
            foreach (PhysBoneSkipEntry skip in plan.skipped)
            {
                List<string> list;
                if (!skippedByReason.TryGetValue(skip.reason, out list)) skippedByReason[skip.reason] = list = new List<string>();
                list.Add(GetHierarchyPath(skip.physBone.transform, root.transform));
            }

            // --- 計画の実行(バケットごとに不一致を報告してからマージ) ---
            int reducedCount = 0;
            bool anyGrabbableMerged = false;
            var noSiblingPaths = new List<string>();
            foreach (PhysBoneBucketPlan bucket in plan.buckets)
            {
                if (bucket.members.Count < 2)
                {
                    // 同じ親を持つマージ候補が他に無い(親が異なる)
                    noSiblingPaths.Add(GetHierarchyPath(bucket.members[0].transform, root.transform));
                    continue;
                }

                // 不一致の報告はマージ(=メンバー破棄)より先に行う(詳細は計画時に算出済み)
                if (bucket.mismatches.Count > 0)
                {
                    report.Info($"PhysBoneマージ対象外(設定不一致): {string.Join("、", bucket.mismatches.Select(m => m.detail))}");
                }

                for (int ci = 0; ci < bucket.mergeClasses.Count; ci++)
                {
                    List<VRCPhysBone> cls = bucket.mergeClasses[ci];
                    if (IsGrabbingAllowed(cls[0])) anyGrabbableMerged = true;

                    // ルーズマージで先頭メンバーと設定が異なるメンバーがあれば、統一した旨を残す
                    // (揺れ方が先頭チェーンに揃うため。件数はマージグループ数ぶんに限られるので氾濫しない)。
                    List<string> diffMembers = ci < bucket.mergeClassDiffMembers.Count ? bucket.mergeClassDiffMembers[ci] : null;
                    if (diffMembers != null && diffMembers.Count > 0)
                    {
                        report.Warn($"PhysBoneルーズマージ(設定を先頭チェーンに統一): {GetHierarchyPath(bucket.parent, root.transform)} 配下で設定の異なるチェーンを先頭メンバーの設定へ統一しました(揺れ方・コライダーが先頭チェーンに揃います): {string.Join("、", diffMembers)}");
                    }

                    MergePhysBoneGroup(bucket.parent, cls, root, report);
                    reducedCount += cls.Count - 1;
                }
            }

            // --- 対象外の理由をまとめてレポート ---
            foreach (var kv in skippedByReason)
            {
                report.Info($"PhysBoneマージ対象外({kv.Key}): {string.Join("、", kv.Value)}");
            }
            if (noSiblingPaths.Count > 0)
            {
                report.Info($"PhysBoneマージ対象外(親が異なる: 同じ親を持つマージ可能な兄弟なし): {string.Join("、", noSiblingPaths)}");
            }
            if (anyGrabbableMerged)
            {
                // Avatar Optimizerは掴み有効なPhysBoneのマージを一律禁止している(同期挙動が変わりうるため)。
                // 本ツールはマージするが、ユーザーが確認できるよう警告として残す。
                report.Warn("掴み(Grab)が有効なPhysBoneをマージしました。マージ後も各チェーンは個別に掴めますが、他ユーザーから見た掴みの同期がPC版と完全一致しない場合があります。問題があればマージ設定をオフにしてください。");
            }

            // --- 最終カウントと上限の報告 ---
            int remaining = root.GetComponentsInChildren<VRCPhysBone>(true)
                .Count(pb => !IsInEditorOnlySubtree(pb.transform, root.transform));
            if (reducedCount > 0)
            {
                report.Info($"PhysBoneマージ完了: {plan.candidates.Count}本 → {remaining}本(削減 {reducedCount} 本。揺れは維持されます)。");
            }
            else
            {
                report.Info("マージできるPhysBoneの組み合わせはありませんでした。");
            }
            string countMessage = $"PhysBoneコンポーネント数: {remaining}(Medium上限 {QuestLimits.MediumPhysBoneComponents} / Poor上限 {QuestLimits.PoorPhysBoneComponents})";
            if (remaining > QuestLimits.PoorPhysBoneComponents)
            {
                report.Warn(countMessage + "。Poor上限を超えています(超過分の削除設定が有効な場合は続けて削除されます。Quest除外の活用も検討してください)。");
            }
            else
            {
                report.Info(countMessage + "。");
            }
            return reducedCount;
        }

        // ================================================================
        // PhysBoneマージのプレビュー(ドライラン)と手動選択
        // ================================================================

        /// <summary>マージ計画: 対象外になった1本分(コンポーネントと理由)。</summary>
        private sealed class PhysBoneSkipEntry
        {
            /// <summary>対象のPhysBone。</summary>
            public VRCPhysBone physBone;

            /// <summary>レポート用の詳細理由(従来の文言)。</summary>
            public string reason;

            /// <summary>プレビュー表示用の短い理由。</summary>
            public string shortReason;
        }

        /// <summary>マージ計画: 設定不一致でマージできなかった1本分。</summary>
        private sealed class PhysBoneMismatchEntry
        {
            /// <summary>対象のPhysBone。</summary>
            public VRCPhysBone physBone;

            /// <summary>代表クラスと最初に差異が見つかったプロパティ名(取得できなければnull)。</summary>
            public string differingProperty;

            /// <summary>レポート用の詳細("パス(差異: X)" 形式)。</summary>
            public string detail;
        }

        /// <summary>マージ計画: 共通親1つ分のバケット。</summary>
        private sealed class PhysBoneBucketPlan
        {
            /// <summary>共通親(マージ先コンポーネントを置くTransform)。</summary>
            public Transform parent;

            /// <summary>この親を共通親とするマージ候補(候補判定を通過したもの)。</summary>
            public readonly List<VRCPhysBone> members = new List<VRCPhysBone>();

            /// <summary>マージ実行対象(設定互換で2本以上のクラス)。members.Count >= 2 のときのみ設定される。</summary>
            public readonly List<List<VRCPhysBone>> mergeClasses = new List<List<VRCPhysBone>>();

            /// <summary>
            /// mergeClasses と同じ添字。ルーズマージで先頭メンバーと設定が異なっていたメンバーの
            /// 短い説明(「メンバー名(差異: プロパティ名)」)。厳密マージ(looseMerge=false)や
            /// 設定が完全一致のクラスでは空リスト。プレビュー行の settingsDiffMembers と実行時レポートに使う。
            /// </summary>
            public readonly List<List<string>> mergeClassDiffMembers = new List<List<string>>();

            /// <summary>兄弟はいるが設定不一致でマージできないメンバー(厳密マージ時のみ発生。ルーズマージでは空)。</summary>
            public readonly List<PhysBoneMismatchEntry> mismatches = new List<PhysBoneMismatchEntry>();
        }

        /// <summary>
        /// PhysBoneマージの実行計画。PlanPhysBoneMerge が作成し、
        /// MergePhysBones(実行)と PreviewPhysBoneMerge(ドライラン)の双方が同じ計画を参照する。
        /// </summary>
        private sealed class PhysBoneMergePlan
        {
            /// <summary>マージ検討対象の全PhysBone(EditorOnly配下と削除指定分を除く)。</summary>
            public readonly List<VRCPhysBone> candidates = new List<VRCPhysBone>();

            /// <summary>removePaths(削除指定)により計画から除外した本数。</summary>
            public int removePlannedCount;

            /// <summary>EditorOnly / Quest除外(ビルド除外)配下のため計画から隠した本数(仕様[C]の非表示注記用)。</summary>
            public int hiddenExcludedCount;

            /// <summary>候補判定で対象外になったもの(判定順)。</summary>
            public readonly List<PhysBoneSkipEntry> skipped = new List<PhysBoneSkipEntry>();

            /// <summary>共通親ごとのバケット(出現順)。</summary>
            public readonly List<PhysBoneBucketPlan> buckets = new List<PhysBoneBucketPlan>();
        }

        /// <summary>
        /// マージの候補収集・除外判定・グループ分けを行い、実行計画を返す(アバターへの変更は一切しない)。
        /// MergePhysBones はこの計画をそのまま実行し、PreviewPhysBoneMerge はプレビュー行へ変換する。
        /// </summary>
        /// <param name="removePaths">削除指定のPhysBone識別パス。該当PhysBoneは計画から完全に除外される(nullで指定なし)。</param>
        /// <param name="noMergePaths">手動マージ除外のPhysBone識別パス(nullで指定なし)。</param>
        /// <param name="excludedRoots">
        /// 変換時にクローン上でEditorOnly化される除外サブツリーのルート(元アバター上で解決したもの)。
        /// 該当サブツリー配下のPhysBoneは、EditorOnly配下と同様に計画から完全に除外する。
        /// プレビューを元アバターに対して実行する際に、変換後(クローン)の
        /// questExcludePaths / 全スロット透過レンダラー自動非表示と結果を一致させるために使う(nullで指定なし)。
        /// </param>
        /// <param name="looseMerge">
        /// true のとき、同じ共通親を持ちハードゲートを通過した兄弟チェーンは設定が一致しなくても
        /// 先頭メンバーの設定へ統一して1本にまとめる(設定差のみ許容。ハードゲートは緩めない)。
        /// false のときは従来どおり全シリアライズ設定が完全一致するチェーンのみをまとめる。
        /// </param>
        private static PhysBoneMergePlan PlanPhysBoneMerge(GameObject root, HashSet<string> animatedTogglePaths, List<string> removePaths, List<string> noMergePaths, List<Transform> excludedRoots, bool looseMerge)
        {
            var plan = new PhysBoneMergePlan();
            HashSet<string> removeSet = ToPathSet(removePaths);
            HashSet<string> noMergeSet = ToPathSet(noMergePaths);

            foreach (VRCPhysBone pb in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (IsInEditorOnlySubtree(pb.transform, root.transform)) { plan.hiddenExcludedCount++; continue; }
                // 変換時にEditorOnly化される除外サブツリー配下は、実変換ではマージ判定に載らないため計画から除く
                if (IsUnderAny(pb.transform, excludedRoots)) { plan.hiddenExcludedCount++; continue; }
                if (removeSet != null && removeSet.Contains(GetPhysBoneIdentityPath(root.transform, pb)))
                {
                    // 削除指定分は「存在しないもの」として扱う(プレビューの行・予測数にも含めない)
                    plan.removePlannedCount++;
                    continue;
                }
                plan.candidates.Add(pb);
            }
            if (plan.candidates.Count == 0) return plan;

            // --- 実効ルートを共有するPhysBoneの検出 ---
            // 同じ実効ルートを持つ複数のPhysBoneは、ignoreTransformsで担当サブチェーンを
            // 分担している可能性がある。マージすると ignoreTransforms が和集合になり
            // 全サブチェーンが揺れなくなるため、ルートを共有するものは全て対象外にする
            // (Avatar Optimizer の GroupBy(GetTarget).Where(Count()==1) と同じ規則)。
            var effectiveRootCounts = new Dictionary<Transform, int>();
            foreach (VRCPhysBone pb in plan.candidates)
            {
                Transform er = GetEffectiveRoot(pb);
                int count;
                effectiveRootCounts.TryGetValue(er, out count);
                effectiveRootCounts[er] = count + 1;
            }

            // --- 候補判定(対象外は理由付きで記録) ---
            var bucketByParent = new Dictionary<Transform, PhysBoneBucketPlan>();
            foreach (VRCPhysBone pb in plan.candidates)
            {
                Transform parent = null;
                string reason;
                string shortReason;
                if (noMergeSet != null && noMergeSet.Contains(GetPhysBoneIdentityPath(root.transform, pb)))
                {
                    // ユーザーの明示指定は他の自動判定より優先して理由に表示する
                    reason = "手動でマージ除外";
                    shortReason = "手動でマージ除外";
                }
                else
                {
                    reason = GetMergeExclusionReason(pb, root.transform, animatedTogglePaths, out parent, out shortReason);
                    if (reason == null && effectiveRootCounts[GetEffectiveRoot(pb)] > 1)
                    {
                        parent = null;
                        reason = "同じチェーンルートを複数のPhysBoneが共有(ignoreTransformsによる役割分担の可能性があるため対象外)";
                        shortReason = "同一ルート複数";
                    }
                }
                if (reason != null)
                {
                    plan.skipped.Add(new PhysBoneSkipEntry { physBone = pb, reason = reason, shortReason = shortReason });
                    continue;
                }
                PhysBoneBucketPlan bucket;
                if (!bucketByParent.TryGetValue(parent, out bucket))
                {
                    bucket = new PhysBoneBucketPlan { parent = parent };
                    bucketByParent[parent] = bucket;
                    plan.buckets.Add(bucket);
                }
                bucket.members.Add(pb);
            }

            // --- 親ごとにマージクラスへ分割 ---
            foreach (PhysBoneBucketPlan bucket in plan.buckets)
            {
                if (bucket.members.Count < 2) continue; // 単独(同グループなし)は実行/プレビュー側で扱う

                if (looseMerge)
                {
                    // ルーズマージ: 候補判定(ハードゲート)を通過した兄弟は、設定が一致しなくても
                    // 先頭メンバーの設定へ統一して1本にまとめる。設定差のみ許容し、ハードゲート
                    // (パラメータ・アニメ制御・カーブ・multiChildType・同一ルート共有・非アクティブ・
                    // EditorOnly/除外)は緩めない。先頭メンバーと設定が異なるメンバーを記録する
                    // (揺れ方・コライダーが先頭チェーンに揃う旨をUI/レポートへ出すため)。
                    var mergeClass = new List<VRCPhysBone>(bucket.members);
                    var diffMembers = new List<string>();
                    VRCPhysBone head = bucket.members[0];
                    for (int i = 1; i < bucket.members.Count; i++)
                    {
                        string difference;
                        if (!ArePhysBoneSettingsCompatible(head, bucket.members[i], out difference))
                        {
                            diffMembers.Add(BuildLooseDiffLabel(bucket.members[i], difference));
                        }
                    }
                    bucket.mergeClasses.Add(mergeClass);
                    bucket.mergeClassDiffMembers.Add(diffMembers);
                    continue;
                }

                // --- 厳密マージ: 全シリアライズ設定が一致するクラスへ分割 ---
                var classes = new List<List<VRCPhysBone>>();
                foreach (VRCPhysBone pb in bucket.members)
                {
                    List<VRCPhysBone> home = null;
                    foreach (List<VRCPhysBone> cls in classes)
                    {
                        string differing;
                        if (ArePhysBoneSettingsCompatible(cls[0], pb, out differing))
                        {
                            home = cls;
                            break;
                        }
                    }
                    if (home != null) home.Add(pb);
                    else classes.Add(new List<VRCPhysBone> { pb });
                }

                foreach (List<VRCPhysBone> cls in classes)
                {
                    if (cls.Count >= 2)
                    {
                        bucket.mergeClasses.Add(cls);
                        bucket.mergeClassDiffMembers.Add(new List<string>()); // 厳密マージは設定完全一致のため差分なし
                        continue;
                    }
                    // 兄弟はいるが設定が一致しなかった(代表クラスとの差異を報告用に取得)
                    string differing;
                    ArePhysBoneSettingsCompatible(classes[0][0], cls[0], out differing);
                    bucket.mismatches.Add(new PhysBoneMismatchEntry
                    {
                        physBone = cls[0],
                        differingProperty = differing,
                        detail = GetHierarchyPath(cls[0].transform, root.transform) +
                            (differing != null ? "(差異: " + differing + ")" : string.Empty),
                    });
                }
            }
            return plan;
        }

        /// <summary>
        /// マージ後のPhysBone構成のドライランプレビューを返す。
        /// 【厳密なドライラン】元アバター・複製アバターのどちらに対して呼んでもよく、
        /// シーン・コンポーネント・アセットへの変更は一切行わない(SerializedObjectは読み取りのみ)。
        /// 判定ロジックは MergePhysBones と完全に共通(PlanPhysBoneMerge)のため、
        /// ここで表示されるグループがそのまま変換時にマージされる。
        /// removePaths で削除指定されたPhysBoneは行にも予測数(projectedComponentCount)にも
        /// 含めない(currentComponentCount はアバター上の実数のため含む)。
        /// </summary>
        /// <param name="animatedTogglePaths">CollectPhysBoneTogglePaths の結果(nullでアニメ制御判定なし)。</param>
        /// <param name="removePaths">削除指定のPhysBone識別パス(nullで指定なし)。</param>
        /// <param name="noMergePaths">手動マージ除外のPhysBone識別パス(nullで指定なし)。</param>
        /// <param name="excludedRoots">
        /// 変換時にEditorOnly化される除外サブツリーのルート(元アバター上で解決したもの)。
        /// 配下のPhysBoneは行にも予測数・現在数にも含めない(変換後の結果と一致させる。nullで指定なし)。
        /// </param>
        public static PhysBonePreview PreviewPhysBoneMerge(GameObject root, HashSet<string> animatedTogglePaths, List<string> removePaths, List<string> noMergePaths, List<Transform> excludedRoots = null)
        {
            return PreviewPhysBoneMerge(root, animatedTogglePaths, removePaths, noMergePaths, false, excludedRoots);
        }

        /// <summary>
        /// 上の PreviewPhysBoneMerge に「ルーズマージ」を加えた版(厳密なドライラン。アバター・アセットへの
        /// 変更は一切行わない)。looseMerge=true のとき、同じ共通親を持ちハードゲートを通過した兄弟チェーンは
        /// 設定が一致しなくても1つのグループ行にまとまる(その行の looseMerged=true、settingsDiffMembers に
        /// 先頭メンバーと設定が異なるメンバーの一覧が入る)。looseMerge=false のときは従来どおり設定完全一致の
        /// チェーンのみがまとまり、結果は旧版と同一になる。各行には自動選択用の priorityScore も設定される。
        /// </summary>
        public static PhysBonePreview PreviewPhysBoneMerge(GameObject root, HashSet<string> animatedTogglePaths, List<string> removePaths, List<string> noMergePaths, bool looseMerge, List<Transform> excludedRoots = null)
        {
            var preview = new PhysBonePreview { rows = new List<PhysBonePreviewRow>() };
            if (root == null) return preview;

            Transform rootTransform = root.transform;
            PhysBoneMergePlan plan = PlanPhysBoneMerge(root, animatedTogglePaths, removePaths, noMergePaths, excludedRoots, looseMerge);
            preview.currentComponentCount = plan.candidates.Count + plan.removePlannedCount;
            // 削除指定のみ適用しマージしなかった場合に残る数(マージ候補数)。
            preview.nonMergedComponentCount = plan.candidates.Count;
            // EditorOnly / Quest除外配下のため一覧・現在数から隠した本数(セクションの非表示注記用)。
            preview.hiddenExcludedCount = plan.hiddenExcludedCount;

            // --- マージされるグループ(2本以上 → 1本) ---
            foreach (PhysBoneBucketPlan bucket in plan.buckets)
            {
                for (int ci = 0; ci < bucket.mergeClasses.Count; ci++)
                {
                    List<VRCPhysBone> cls = bucket.mergeClasses[ci];
                    List<string> diffMembers = ci < bucket.mergeClassDiffMembers.Count
                        ? bucket.mergeClassDiffMembers[ci]
                        : new List<string>();
                    var row = new PhysBonePreviewRow
                    {
                        isGroup = true,
                        parentPath = QuestCompat.GetRelativePath(rootTransform, bucket.parent),
                        memberPaths = new List<string>(),
                        memberLabels = new List<string>(),
                        transformCount = 1, // マージ先を置く共通親の分
                        settingsDiffMembers = diffMembers,
                        looseMerged = diffMembers.Count > 0,
                    };
                    // 優先度スコア用の名前集合。表示用memberLabelsはメンバー数と一致させる必要があるため
                    // 汚さず、スコアには単独行と同様にチェーンルート名(rootTransform指定時に揺れボーン名が
                    // 乗る)も含めて、単独/グループで採点基準を揃える。
                    var scoreLabels = new List<string>();
                    foreach (VRCPhysBone member in cls)
                    {
                        row.memberPaths.Add(GetPhysBoneIdentityPath(rootTransform, member));
                        row.memberLabels.Add(member.transform.name);
                        row.transformCount += CountChainTransforms(GetEffectiveRoot(member), BuildIgnoreSet(member));
                        scoreLabels.Add(member.transform.name);
                        scoreLabels.Add(GetEffectiveRoot(member).name);
                    }
                    row.priorityScore = ComputeGroupPriorityScore(row.parentPath, scoreLabels);
                    preview.rows.Add(row);
                }
            }

            // --- マージされない単独(バケット内: 設定不一致・同グループなし) ---
            foreach (PhysBoneBucketPlan bucket in plan.buckets)
            {
                if (bucket.members.Count < 2)
                {
                    preview.rows.Add(MakeSinglePreviewRow(rootTransform, bucket.members[0], "単独(同グループなし)"));
                    continue;
                }
                foreach (PhysBoneMismatchEntry mismatch in bucket.mismatches)
                {
                    string reason = mismatch.differingProperty != null
                        ? "設定不一致(差異: " + mismatch.differingProperty + ")"
                        : "設定不一致";
                    preview.rows.Add(MakeSinglePreviewRow(rootTransform, mismatch.physBone, reason));
                }
            }

            // --- マージされない単独(候補判定で対象外) ---
            foreach (PhysBoneSkipEntry skip in plan.skipped)
            {
                preview.rows.Add(MakeSinglePreviewRow(rootTransform, skip.physBone, skip.shortReason));
            }

            // 予測数 = グループ行(各1本になる)+ 単独行
            preview.projectedComponentCount = preview.rows.Count;
            return preview;
        }

        /// <summary>プレビューの単独行(マージされないPhysBone)を作る。</summary>
        private static PhysBonePreviewRow MakeSinglePreviewRow(Transform rootTransform, VRCPhysBone pb, string skipReason)
        {
            // 自動選択の優先度は PhysBoneのGameObject名とチェーンルート名の高い方(小さい添字)を採る
            int score = Mathf.Min(
                QuestCompat.GetPhysBonePriorityScore(pb.transform.name),
                QuestCompat.GetPhysBonePriorityScore(GetEffectiveRoot(pb).name));
            return new PhysBonePreviewRow
            {
                isGroup = false,
                singlePath = GetPhysBoneIdentityPath(rootTransform, pb),
                skipReason = skipReason,
                transformCount = CountChainTransforms(GetEffectiveRoot(pb), BuildIgnoreSet(pb)),
                priorityScore = score,
            };
        }

        /// <summary>
        /// グループ行の自動選択優先度スコア(共通親の末尾セグメント名+全メンバー名のうち最良=最小の添字)。
        /// いずれもキーワード非該当なら int.MaxValue。
        /// </summary>
        private static int ComputeGroupPriorityScore(string parentPath, List<string> memberLabels)
        {
            int best = QuestCompat.GetPhysBonePriorityScore(LastPathSegment(parentPath));
            if (memberLabels != null)
            {
                foreach (string label in memberLabels)
                {
                    int s = QuestCompat.GetPhysBonePriorityScore(label);
                    if (s < best) best = s;
                }
            }
            return best;
        }

        /// <summary>スラッシュ区切りパスの末尾セグメント(区切りが無ければそのまま。null・空はそのまま)。</summary>
        private static string LastPathSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        /// <summary>
        /// ルーズマージで先頭メンバーと設定が異なるメンバーの短い表示("メンバー名(差異: プロパティ名)"、
        /// プロパティが取得できなければ "メンバー名(設定差異あり)")を作る。
        /// </summary>
        private static string BuildLooseDiffLabel(VRCPhysBone member, string differingProperty)
        {
            string name = member.transform.name;
            return differingProperty != null
                ? name + "(差異: " + differingProperty + ")"
                : name + "(設定差異あり)";
        }

        /// <summary>
        /// 識別パスで指定されたPhysBoneを複製アバターから削除し、削除できた本数を返す。
        /// MergePhysBones より先に呼ぶこと(削除済みのPhysBoneをマージ判定に混ぜないため)。
        /// 解決できなかったパス(既に削除済み・改名など)は情報として報告してスキップする。
        /// 削除後、残ったPhysBoneのコライダーリストから参照切れエントリを除去する(TrimPhysBonesと同じ後始末)。
        /// </summary>
        public static int RemoveSelectedPhysBones(GameObject root, List<string> removePaths, ConversionReport report)
        {
            if (root == null || removePaths == null || removePaths.Count == 0) return 0;

            // 破棄で同一GameObject上のコンポーネント順(#インデックス)が変わらないよう、
            // 先に全PhysBoneの識別パスを確定させてから削除する。
            var byIdentity = new Dictionary<string, VRCPhysBone>(StringComparer.Ordinal);
            foreach (VRCPhysBone pb in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                string identity = GetPhysBoneIdentityPath(root.transform, pb);
                // 同名の兄弟GameObject等でパスが衝突した場合は先勝ち(後続は解決不能として報告される)
                if (identity != null && !byIdentity.ContainsKey(identity)) byIdentity[identity] = pb;
            }

            int removedCount = 0;
            var processed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in removePaths)
            {
                if (string.IsNullOrEmpty(path) || !processed.Add(path)) continue;

                VRCPhysBone pb;
                if (byIdentity.TryGetValue(path, out pb) && pb != null)
                {
                    report.Warn($"削除(手動選択): {path}");
                    UnityEngine.Object.DestroyImmediate(pb);
                    removedCount++;
                }
                else
                {
                    report.Info($"削除指定のPhysBoneが見つかりません(スキップ): {path}");
                }
            }

            if (removedCount > 0)
            {
                // 残ったPhysBoneのコライダーリストの参照切れ(null/missing)エントリを除去
                int cleanedEntries = RemoveMissingColliderEntries(root.GetComponentsInChildren<VRCPhysBone>(true));
                report.Info($"手動選択によりPhysBoneを {removedCount} 本削除しました" +
                    (cleanedEntries > 0 ? $"(参照切れコライダーエントリ除去 {cleanedEntries} 件)。" : "。"));
            }
            return removedCount;
        }

        /// <summary>
        /// 選択制(OptInモード)の実行: keepPaths(識別パス)に無いPhysBoneをすべて複製アバターから
        /// 削除し、削除できた本数を返す。既定オフ(残す指定だけ残す)の考え方で、100本以上ある
        /// アバターでもPoor上限内に収めやすくするためのもの。MergePhysBones より前に呼ぶこと。
        /// keepPathsはマージ前の各メンバーの識別パスであり、マージで生成される新規コンポーネントは
        /// 共通親に置かれるため識別パスが異なりkeepPathsに一致しない。もしマージ後に呼ぶと、
        /// 残すはずのグループの統合コンポーネントがkeep判定に外れて全削除されてしまう。
        ///
        /// ・EditorOnly(Quest除外)配下はビルドで除去されるため集計・削除の対象外(触らない)。
        /// ・RemoveSelectedPhysBones と同様、破棄で同一GameObject上の #序数 がずれないよう
        ///   先に全PhysBoneの識別パスを確定してから参照で削除する。
        /// ・レポートは削除1本ごとに最大15行まで出し、残りはまとめ件数のみ
        ///   (100本超のアバターでレポートが埋め尽くされるのを防ぐ)。
        /// ・keepPaths のうち現在のアバター上で解決できなかったものは情報として報告する。
        /// ・削除後、残ったPhysBoneのコライダーリストから参照切れエントリを除去する(TrimPhysBonesと同じ後始末)。
        /// </summary>
        public static int RemoveAllExceptKept(GameObject root, List<string> keepPaths, ConversionReport report)
        {
            if (root == null) return 0;
            if (report == null) report = new ConversionReport(); // 呼び出し側の渡し忘れ対策(結果は破棄される)

            // 残す指定の照合セット。空リストなら「1本も残さない」= 全削除。
            // 注意: アバタールート直下のPhysBoneの識別パスは空文字("")なので、ToPathSet(空文字を捨てる)
            // を使わず自前で構築する。空文字を捨てるとUIで「稼働」に指定したルートPhysBoneが
            // keepSetから漏れて意図に反して削除されてしまう(nullのみ除外し、空文字は有効な識別として残す)。
            var keepSet = new HashSet<string>(StringComparer.Ordinal);
            if (keepPaths != null)
            {
                foreach (string keepPath in keepPaths)
                {
                    if (keepPath != null) keepSet.Add(keepPath);
                }
            }

            // 破棄で #序数 が変わらないよう、先に全(非EditorOnly)PhysBoneの識別パスを確定させる。
            var snapshot = new List<KeyValuePair<string, VRCPhysBone>>();
            var resolvedKeep = new HashSet<string>(StringComparer.Ordinal);
            foreach (VRCPhysBone pb in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (IsInEditorOnlySubtree(pb.transform, root.transform)) continue;
                string identity = GetPhysBoneIdentityPath(root.transform, pb);
                snapshot.Add(new KeyValuePair<string, VRCPhysBone>(identity, pb));
                if (identity != null && keepSet.Contains(identity)) resolvedKeep.Add(identity);
            }

            int removedCount = 0;
            int reportedLines = 0;
            const int maxReportLines = 15;
            foreach (KeyValuePair<string, VRCPhysBone> pair in snapshot)
            {
                VRCPhysBone pb = pair.Value;
                if (pb == null) continue;                                     // 連鎖破棄で既に消えている場合の保険
                if (pair.Key != null && keepSet.Contains(pair.Key)) continue; // 残す指定はスキップ

                if (reportedLines < maxReportLines)
                {
                    report.Info($"削除(選択制: 未選択のため): {pair.Key ?? GetHierarchyPath(pb.transform, root.transform)}");
                    reportedLines++;
                }
                UnityEngine.Object.DestroyImmediate(pb);
                removedCount++;
            }
            if (removedCount > reportedLines)
            {
                report.Info($"…ほか {removedCount - reportedLines} 本のPhysBoneを削除しました(選択制のため未選択分をまとめて削除)。");
            }

            // 解決できなかった残す指定(改名・削除済み等)は情報として報告する
            foreach (string path in keepSet)
            {
                if (!resolvedKeep.Contains(path))
                {
                    report.Info($"残す指定のPhysBoneが見つかりません(スキップ): {path}");
                }
            }

            if (removedCount > 0)
            {
                // 残ったPhysBoneのコライダーリストの参照切れ(null/missing)エントリを除去
                int cleanedEntries = RemoveMissingColliderEntries(root.GetComponentsInChildren<VRCPhysBone>(true));
                report.Info($"選択制により未選択のPhysBoneを {removedCount} 本削除しました" +
                    (cleanedEntries > 0 ? $"(参照切れコライダーエントリ除去 {cleanedEntries} 件)。" : "。"));
            }
            else
            {
                report.Info("選択制: 削除対象のPhysBoneはありませんでした(すべて残す指定、またはPhysBoneなし)。");
            }
            return removedCount;
        }

        /// <summary>
        /// PhysBoneの識別パスを返す(プレビュー・設定保存・削除解決の共通規則)。
        /// 規則: QuestCompat.GetRelativePath(avatarRoot, pb.transform) の相対パス。
        /// 同一GameObjectに同系統のコンポーネントが複数ある場合のみ、
        /// 末尾に「#インデックス」(GetComponents順・0始まり)を付与する(例: "Armature/Hips/Skirt#1")。
        /// アバター外のコンポーネント・null は null を返す。
        /// 注意: 同名の兄弟GameObjectがあるとパスが一意にならない(その場合の解決は先勝ち)。
        /// </summary>
        public static string GetPhysBoneIdentityPath(Transform avatarRoot, Component pb)
        {
            if (avatarRoot == null || pb == null) return null;
            string basePath = QuestCompat.GetRelativePath(avatarRoot, pb.transform);
            if (basePath == null) return null; // アバター外

            // PhysBone系はVRCPhysBoneBase単位で数える(派生型が混在してもインデックスが安定するように)
            Type indexType = pb is VRCPhysBoneBase ? typeof(VRCPhysBoneBase) : pb.GetType();
            Component[] siblings = pb.gameObject.GetComponents(indexType);
            if (siblings.Length <= 1) return basePath;
            return basePath + "#" + Array.IndexOf(siblings, pb);
        }

        /// <summary>識別パスのリストを照合用セットへ変換する(null・空文字は無視。実質空ならnull)。</summary>
        private static HashSet<string> ToPathSet(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (!string.IsNullOrEmpty(path)) set.Add(path);
            }
            return set.Count > 0 ? set : null;
        }

        /// <summary>PhysBoneのignoreTransforms(null除去済み)をセットにして返す。</summary>
        private static HashSet<Transform> BuildIgnoreSet(VRCPhysBone pb)
        {
            var set = new HashSet<Transform>();
            if (pb.ignoreTransforms != null)
            {
                foreach (Transform t in pb.ignoreTransforms)
                {
                    if (t != null) set.Add(t);
                }
            }
            return set;
        }

        /// <summary>
        /// チェーンルート配下のTransform数を数える(自身を含む。ignoredのサブツリーは丸ごと除外)。
        /// プレビューの影響Transform数の概算用で、VRChatの正式カウント
        /// (エンドポイントボーンの仮想Transform等)は再現しない安価な目安値。
        /// </summary>
        private static int CountChainTransforms(Transform chainRoot, HashSet<Transform> ignored)
        {
            if (chainRoot == null || ignored.Contains(chainRoot)) return 0;
            int count = 1;
            foreach (Transform child in chainRoot)
            {
                count += CountChainTransforms(child, ignored);
            }
            return count;
        }

        /// <summary>PhysBoneの実効ルート(rootTransformが未設定ならコンポーネント自身のTransform)。</summary>
        private static Transform GetEffectiveRoot(VRCPhysBone pb)
        {
            return pb.rootTransform != null ? pb.rootTransform : pb.transform;
        }

        /// <summary>
        /// このPhysBoneをマージ候補から外すべき理由を返す(候補ならnullを返し、parentに実効ルートの親を設定)。
        /// shortReason にはプレビュー表示用の短い理由を返す(候補ならnull)。
        /// </summary>
        private static string GetMergeExclusionReason(VRCPhysBone pb, Transform root, HashSet<string> animatedTogglePaths, out Transform parent, out string shortReason)
        {
            parent = null;
            shortReason = null;

            Transform effectiveRoot = GetEffectiveRoot(pb);
            if (effectiveRoot == root || !effectiveRoot.IsChildOf(root))
            {
                shortReason = "チェーンルート対象外";
                return "チェーンルートがアバタールート自身またはアバター外";
            }
            if (IsInEditorOnlySubtree(effectiveRoot, root))
            {
                shortReason = "Quest除外(EditorOnly)配下";
                return "チェーンがEditorOnly(Quest除外)配下";
            }
            Transform p = effectiveRoot.parent;
            if (p == null)
            {
                shortReason = "チェーンルート対象外";
                return "チェーンルートに親がない"; // IsChildOf(root)が真なら通常起こらない(保険)
            }
            if (!pb.isActiveAndEnabled || !effectiveRoot.gameObject.activeInHierarchy)
            {
                shortReason = "非アクティブ";
                return "非アクティブ(挙動を変えないため対象外)";
            }
            if (!string.IsNullOrEmpty(pb.parameter))
            {
                shortReason = "パラメータ使用";
                return "パラメータ使用(メニュー/OSC制御の可能性)";
            }
            if (pb.multiChildType != VRCPhysBoneBase.MultiChildType.Ignore)
            {
                shortReason = "Multi Child Type";
                return "Multi Child TypeがIgnore以外(ルートの揺れ方が変わるため対象外)";
            }
            if (HasNonEmptyCurve(pb))
            {
                shortReason = "カーブ設定あり";
                return "カーブ設定あり(マージでルートが1段深くなりカーブの意味が変わるため対象外)";
            }
            if (IsToggleAnimated(pb, effectiveRoot, p, root, animatedTogglePaths))
            {
                shortReason = "アニメ制御あり";
                return "アニメ制御あり(ON/OFFやプロパティがアニメーション対象)";
            }

            parent = p;
            return null;
        }

        /// <summary>
        /// このPhysBoneのON/OFFがアニメーションで個別制御されているか。
        /// 共通親(=マージ先コンポーネントの置き場所)の祖先に対するトグルは、マージ後の
        /// コンポーネントにも等しく作用するため「個別制御」とはみなさない。
        /// </summary>
        private static bool IsToggleAnimated(VRCPhysBone pb, Transform effectiveRoot, Transform commonParent, Transform root, HashSet<string> animatedTogglePaths)
        {
            if (animatedTogglePaths == null || animatedTogglePaths.Count == 0) return false;

            // チェーンルート自身のアクティブ切り替えは常に個別制御(マージ先はチェーンルートより上に置かれる)
            if (effectiveRoot != pb.transform)
            {
                string rootPath = QuestCompat.GetRelativePath(root, effectiveRoot);
                if (rootPath != null && animatedTogglePaths.Contains(rootPath)) return true;
            }

            // コンポーネントのGameObjectとその祖先: 共通親の祖先(共有祖先)以外へのトグルは個別制御
            for (Transform t = pb.transform; t != null && t != root; t = t.parent)
            {
                string path = QuestCompat.GetRelativePath(root, t);
                if (path == null || !animatedTogglePaths.Contains(path)) continue;
                if (commonParent.IsChildOf(t)) continue; // 共通親自身または祖先 → マージ後も等しく作用する
                return true;
            }
            return false;
        }

        /// <summary>いずれかのカーブプロパティ(radiusCurve等)にキーが設定されているか。</summary>
        private static bool HasNonEmptyCurve(VRCPhysBone pb)
        {
            var so = new SerializedObject(pb);
            SerializedProperty prop = so.GetIterator();
            while (prop.Next(true))
            {
                if (prop.propertyType != SerializedPropertyType.AnimationCurve) continue;
                AnimationCurve curve = prop.animationCurveValue;
                if (curve != null && curve.length > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 掴み(Grab)が許可されているか(True、またはOtherでフィルターのいずれかが許可)。
        /// 型に依存しないようシリアライズ値で判定する(AdvancedBool: 0=False, 1=True, 2=Other)。
        /// </summary>
        private static bool IsGrabbingAllowed(VRCPhysBone pb)
        {
            var so = new SerializedObject(pb);
            SerializedProperty allowGrabbing = so.FindProperty("allowGrabbing");
            if (allowGrabbing == null) return true; // 想定外は「有効」側に倒す(情報表示にのみ使うため)
            if (allowGrabbing.enumValueIndex == 0) return false;
            if (allowGrabbing.enumValueIndex == 1) return true;
            SerializedProperty allowSelf = so.FindProperty("grabFilter.allowSelf");
            SerializedProperty allowOthers = so.FindProperty("grabFilter.allowOthers");
            return (allowSelf != null && allowSelf.boolValue) || (allowOthers != null && allowOthers.boolValue);
        }

        /// <summary>
        /// 2つのPhysBoneの設定が(rootTransform/ignoreTransforms/colliders/Unity内部を除き)完全一致するか。
        /// 一致しない場合、最初に差異が見つかったプロパティ名をdifferingPropertyへ返す。
        /// コライダーリストは順不同の集合として比較する(null・破棄済みエントリは無視)。
        /// </summary>
        private static bool ArePhysBoneSettingsCompatible(VRCPhysBone a, VRCPhysBone b, out string differingProperty)
        {
            differingProperty = null;
            if (a == b) return true;

            var soA = new SerializedObject(a);
            var soB = new SerializedObject(b);
            SerializedProperty prop = soA.GetIterator();
            bool enterChildren = true;
            while (prop.Next(enterChildren))
            {
                enterChildren = false; // トップレベルのみ列挙(DataEqualsが子を含めて比較する)
                if (MergeIgnoredProperties.Contains(prop.name)) continue;
                SerializedProperty other = soB.FindProperty(prop.propertyPath);
                if (other == null || !SerializedProperty.DataEquals(prop, other))
                {
                    differingProperty = prop.displayName;
                    return false;
                }
            }

            // コライダー: 順不同の集合として比較
            var setA = new HashSet<VRCPhysBoneColliderBase>();
            var setB = new HashSet<VRCPhysBoneColliderBase>();
            if (a.colliders != null) foreach (VRCPhysBoneColliderBase c in a.colliders) { if (c != null) setA.Add(c); }
            if (b.colliders != null) foreach (VRCPhysBoneColliderBase c in b.colliders) { if (c != null) setB.Add(c); }
            if (!setA.SetEquals(setB))
            {
                differingProperty = "Colliders";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 設定互換なメンバー群(実効ルートがすべてparentの直下)を1つのVRCPhysBoneへマージする。
        /// マージ先はparentのGameObjectに追加し、設定は先頭メンバーからコピーした上で
        /// rootTransform=parent・multiChildType=Ignore・ignoreTransformsを再構成する。
        /// </summary>
        private static void MergePhysBoneGroup(Transform parent, List<VRCPhysBone> members, GameObject root, ConversionReport report)
        {
            VRCPhysBone first = members[0];
            VRCPhysBone merged = parent.gameObject.AddComponent<VRCPhysBone>();
            EditorUtility.CopySerialized(first, merged); // version・forces・limits・colliders等を先頭メンバーから複製
            merged.rootTransform = parent;
            merged.multiChildType = VRCPhysBoneBase.MultiChildType.Ignore; // ルート(共通親)自体は揺らさず、各子が独立チェーンになる

            var memberRoots = new HashSet<Transform>();
            foreach (VRCPhysBone m in members) memberRoots.Add(GetEffectiveRoot(m));

            // 共通親の子のうちメンバーのチェーンルートでないもの + 各メンバーが自チェーン内で
            // 無視していたTransform(チェーン外への指定は元々無効なので引き継がない)
            var ignore = new List<Transform>();
            foreach (Transform child in parent)
            {
                if (!memberRoots.Contains(child)) ignore.Add(child);
            }
            foreach (VRCPhysBone m in members)
            {
                if (m.ignoreTransforms == null) continue;
                Transform memberRoot = GetEffectiveRoot(m);
                foreach (Transform t in m.ignoreTransforms)
                {
                    if (t != null && t.IsChildOf(memberRoot) && !ignore.Contains(t)) ignore.Add(t);
                }
            }
            merged.ignoreTransforms = ignore;

            int memberCount = members.Count;
            foreach (VRCPhysBone m in members)
            {
                UnityEngine.Object.DestroyImmediate(m);
            }

            report.Info($"PhysBoneマージ: {GetHierarchyPath(parent, root.transform)} 配下 {memberCount}本 → 1(揺れは維持されます)");
        }

        /// <summary>root配下のUnityコンストレイント(6種)の総数を数える。</summary>
        private static int CountUnityConstraints(GameObject root)
        {
            int count = 0;
            foreach (Type type in UnityConstraintTypes)
            {
                count += root.GetComponentsInChildren(type, true).Length;
            }
            return count;
        }

        /// <summary>t が roots のいずれかの配下(自身を含む)にあるか。roots が null なら false。</summary>
        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            if (roots == null || t == null) return false;
            foreach (Transform root in roots)
            {
                if (root != null && (t == root || t.IsChildOf(root))) return true;
            }
            return false;
        }

        /// <summary>tがroot配下のEditorOnlyサブツリー(自身または祖先にEditorOnlyタグ)に含まれるか。rootまでで判定を打ち切る。</summary>
        private static bool IsInEditorOnlySubtree(Transform t, Transform root)
        {
            Transform current = t;
            while (current != null)
            {
                if (current.CompareTag(QuestCompat.EditorOnlyTag)) return true;
                if (current == root) break;
                current = current.parent;
            }
            return false;
        }

        /// <summary>rootからの階層深さを返す(root自身は0)。root配下でない場合も親の数を返す。</summary>
        private static int GetHierarchyDepth(Transform t, Transform root)
        {
            int depth = 0;
            Transform current = t;
            while (current != null && current != root)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }

        /// <summary>レポート表示用: rootからの階層パス("Root/Armature/Hips" 形式)を返す。</summary>
        private static string GetHierarchyPath(Transform t, Transform root)
        {
            if (t == null) return "(不明)";
            var names = new List<string>();
            Transform current = t;
            while (current != null)
            {
                names.Add(current.name);
                if (current == root) break;
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
#endif
