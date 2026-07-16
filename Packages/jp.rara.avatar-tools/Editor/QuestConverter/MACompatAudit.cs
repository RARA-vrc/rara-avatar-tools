// RARA Quest Converter - Modular Avatar 互換ガード・注記・監査(R3/R4/R5/R6/R7)
// Modular Avatar(MA)は NDMF の Transforming フェーズでアーマチュア統合・リアクティブ
// コンポーネント・ShapeChanger(ポリゴン削除)・ReplaceObject のパス乗っ取り・BoneProxy 再親付け等を
// ビルド時に行う。本ツールの変換(クローン編集)はビルド前に走るため、MA が参照する対象を除外・非表示・
// 統合すると「ビルド時に動かない」「未変換マテリアルが表示側へ移動する」等の齟齬が起こりうる。
//
// このクラスはそれらを検出して警告/注記する集約点であり、AvatarQuestConverter(M1所有)・PCOptimizer・
// ToggleConsolidator・ComponentRemover・AAOMeshRemovalHelper・AvatarStudioExecution から呼ばれる
// 公開静的エントリを提供する。
//
// 【重要】MA への参照はすべてリフレクション/SerializedObject 経由で行い、コンパイル時に MA 型へ依存しない
// (MA 未導入時は型解決に失敗して全ガードが黙って no-op になる)。名前空間プレフィックスで MA コンポーネントを
// 判別し、AvatarObjectReference(referencePath + targetObject)の解決規則は MA の Get と同じ優先順位に合わせる。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RARA.QuestConverter
{
    /// <summary>
    /// Modular Avatar(MA)互換のためのガード・注記・監査を集約したリフレクション専用ヘルパー。
    /// すべての公開メソッドは MA 未導入・引数 null に対して安全(no-op / 空集合)。
    /// </summary>
    public static class MACompatAudit
    {
        /// <summary>MA コンポーネントの名前空間プレフィックス(core / 各モジュール)。この配下の型を MA とみなす。</summary>
        private const string MaNamespacePrefix = "nadena.dev.modular_avatar";

        /// <summary>MA の AvatarObjectReference がアバタールートを指すときの referencePath 番兵値。</summary>
        private const string MaAvatarRootSentinel = "$$$AVATAR_ROOT$$$";

        // R4 で PCOptimizer / AvatarStudioExecution が共有する注記文言。
        /// <summary>MA Merge Armature 使用時の計測注記(表示値は統合前である旨)。</summary>
        public const string MergeArmatureNote =
            "MA Merge Armature使用: ボーン数・PhysBoneマージ機会はビルド時の統合後に改善されます(表示値は統合前)。";

        /// <summary>
        /// R7 の監査で「本ツールが明示的に理解している」MA コンポーネント短縮型名の集合。
        /// これ以外の MA コンポーネントが載っていると監査が Warn で列挙する(誤変換の可能性を可視化)。
        /// マテリアル/メッシュ/ボーンへ影響しうる主要型 + ビルド時処理で変換に干渉しない周辺型を含む。
        /// </summary>
        private static readonly HashSet<string> UnderstoodMaTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            // 変換パイプラインが明示的に考慮している型
            "ModularAvatarMaterialSetter",   // R2: アトラス除外・スロット固定で考慮
            "ModularAvatarMaterialSwap",     // R2: アトラス除外・スロット固定で考慮
            "ModularAvatarMergeAnimator",    // R1/R6: クリップ走査・共有クリップ注記で考慮
            "ModularAvatarMergeBlendTree",   // アニメーター統合と同様にビルド時統合
            "ModularAvatarObjectToggle",     // トグル整理(ToggleConsolidator)で考慮
            "ModularAvatarMergeArmature",    // R3(H4)/R4: 除外参照ガード・計測注記で考慮
            "ModularAvatarShapeChanger",     // R5: メッシュ削除の二重指定回避で考慮
            "ModularAvatarBoneProxy",        // R3: 除外参照ガードで考慮
            "ModularAvatarReplaceObject",    // R3(H4): 除外参照ガードで考慮
            // ビルド時処理・変換に干渉しない周辺型(把握済みとして扱い、監査で騒がない)
            "ModularAvatarMenuInstaller",
            "ModularAvatarMenuItem",
            "ModularAvatarMenuGroup",
            "ModularAvatarMenuInstallTarget",
            "ModularAvatarParameters",
            "ModularAvatarBlendshapeSync",
            "ModularAvatarVisibleHeadAccessory",
            "ModularAvatarWorldFixedObject",
            "ModularAvatarPBBlocker",
            "ModularAvatarScaleAdjuster",
            "MAMoveIndependently",
            "ModularAvatarRenameVRChatCollisionTags",
            "ModularAvatarMeshSettings",
            "ModularAvatarConvertConstraints",
            "ModularAvatarMMDLayerControl",
        };

        // ================================================================
        // R3 + H4: 除外・非表示サブツリーに対する MA 参照ガード
        // ================================================================

        /// <summary>
        /// R3/H4: クローン上の MA コンポーネントを走査し、除外(EditorOnly化)・非表示固定される
        /// サブツリー(excludedRoot 配下)に関わるものを警告する。呼び出し側の除外自体は妨げない(警告のみ)。
        ///
        /// (A) 除外サブツリーの「外」にある MA コンポーネントが、除外サブツリー「内」を参照している場合:
        ///     ビルド時に対象が消えるため MA が動作しない/エラーになる可能性がある旨を警告。
        /// (H4) 除外サブツリーの「内」に MA ReplaceObject / MergeArmature が存在する場合:
        ///     ビルド時にその(未変換の)メッシュ・マテリアルが表示側アバターへ移動・統合され、
        ///     除外が効かない/PC用マテリアルが混入する可能性がある旨を警告。
        ///
        /// AvatarQuestConverter.ApplyQuestExclusions(M1所有)と ToggleConsolidator.ApplyConsolidation
        /// (LockHidden)の両方から、除外/非表示対象1件ごとに呼ばれることを想定する。MA 未導入なら no-op。
        /// </summary>
        /// <param name="clone">編集中のクローン(アバタールート)。</param>
        /// <param name="excludedRoot">EditorOnly化/非表示固定されるサブツリーのルート Transform。</param>
        /// <param name="path">対象のアバタールート相対パス(警告メッセージ用)。</param>
        /// <param name="report">警告の出力先。</param>
        public static void WarnMaReferencesIntoExcludedSubtree(GameObject clone, Transform excludedRoot, string path, ConversionReport report)
        {
            if (clone == null || excludedRoot == null || report == null) return;

            Transform avatarRoot = clone.transform;
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                Type t = c.GetType();
                if (t.Namespace == null || !t.Namespace.StartsWith(MaNamespacePrefix, StringComparison.Ordinal)) continue;

                bool compInside = c.transform == excludedRoot || c.transform.IsChildOf(excludedRoot);

                if (compInside)
                {
                    // H4: 除外サブツリー内の ReplaceObject / MergeArmature は、ビルド時にソースを表示側へ持ち込む。
                    if (t.Name == "ModularAvatarReplaceObject" || t.Name == "ModularAvatarMergeArmature")
                    {
                        report.Warn($"MAコンポーネント {t.Name} が除外対象 {path} 内にあります。ビルド時にこのオブジェクトの(未変換の)メッシュ・マテリアルが表示側アバターへ移動・統合され、除外が効かない/PC用マテリアルが混入する可能性があります。");
                    }
                    // 内側のコンポーネント自体はビルドで除去されるため、参照ガード(A)の対象外。
                    continue;
                }

                // (A) 外側の MA コンポーネントが除外サブツリー内を参照していないか。
                foreach (Transform referenced in ResolveMaReferencedTransforms(c, avatarRoot))
                {
                    if (referenced == excludedRoot || referenced.IsChildOf(excludedRoot))
                    {
                        report.Warn($"MAコンポーネント {t.Name} が除外対象 {path} を参照しています。ビルドで動作しない/エラーになる可能性があります。");
                        break; // このコンポーネントについては1件警告すれば十分。
                    }
                }
            }
        }

        // ================================================================
        // R4: MA Merge Armature の有無(計測の正直さ)
        // ================================================================

        /// <summary>
        /// R4: root 配下(非アクティブ含む)に MA MergeArmature が1つでもあれば true。
        /// これがある場合、ボーン数・PhysBoneマージ機会はビルド時の統合後にしか確定しないため、
        /// 計測レポートの表示値は「統合前」の暫定値であることを注記/エラー緩和に使う。MA 未導入なら false。
        /// </summary>
        public static bool HasMergeArmature(GameObject root)
        {
            if (root == null) return false;
            Type maType = QuestCompat.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            if (maType == null) return false;
            Component[] found = root.GetComponentsInChildren(maType, true);
            return found != null && found.Length > 0;
        }

        // ================================================================
        // R5: MA ShapeChanger が対象にしているブレンドシェイプ
        // ================================================================

        /// <summary>
        /// R5: avatarRoot 配下の MA ShapeChanger を走査し、対象 SkinnedMeshRenderer ごとに
        /// ShapeChanger が扱うブレンドシェイプ名集合を返す。AAO の自動メッシュ削除検出(DetectShrinkShapes)が
        /// これらのシェイプを二重に削除指定しないために使う(ShapeChanger(Delete)の削減はビルド時に適用される)。
        /// MA 未導入なら空辞書。
        /// </summary>
        public static Dictionary<SkinnedMeshRenderer, HashSet<string>> CollectShapeChangerShapes(GameObject avatarRoot)
        {
            var map = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();
            if (avatarRoot == null) return map;

            Type scType = QuestCompat.FindType("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger");
            if (scType == null) return map;

            foreach (Component c in avatarRoot.GetComponentsInChildren(scType, true))
            {
                if (c == null) continue;
                CollectShapeChangerShapesFrom(c, avatarRoot.transform, map);
            }
            return map;
        }

        /// <summary>
        /// ShapeChanger の各行(ChangedShape)を「その行が対象にするレンダラー → その行の ShapeName」として map に加える。
        /// 行ごとに Object(AvatarObjectReference)を解決するため、複数レンダラーを対象にする ShapeChanger でも
        /// 名前が誤って他レンダラーへ帰属しない(R5 の名前衝突誤スキップを防ぐ)。型バージョン差に頑健。
        /// </summary>
        private static void CollectShapeChangerShapesFrom(Component shapeChanger, Transform avatarRoot,
            Dictionary<SkinnedMeshRenderer, HashSet<string>> map)
        {
            SerializedObject so;
            try { so = new SerializedObject(shapeChanger); }
            catch { return; }

            SkinnedMeshRenderer sameGo = shapeChanger.GetComponent<SkinnedMeshRenderer>(); // フォールバック: 同一GO

            SerializedProperty it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.Generic) continue;
                SerializedProperty shapeName = it.FindPropertyRelative("ShapeName") ?? it.FindPropertyRelative("shapeName");
                if (shapeName == null || shapeName.propertyType != SerializedPropertyType.String ||
                    string.IsNullOrEmpty(shapeName.stringValue)) continue;

                // ここまで来た it は1行(ChangedShape)。行の Object を対象レンダラーへ解決する。
                SkinnedMeshRenderer smr = ResolveShapeChangerRowRenderer(it, avatarRoot) ?? sameGo;
                if (smr == null) continue;

                HashSet<string> set;
                if (!map.TryGetValue(smr, out set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    map[smr] = set;
                }
                set.Add(shapeName.stringValue);
            }
        }

        /// <summary>ChangedShape 1行の Object(AvatarObjectReference)を対象 SkinnedMeshRenderer へ解決する。未解決なら null。</summary>
        private static SkinnedMeshRenderer ResolveShapeChangerRowRenderer(SerializedProperty row, Transform avatarRoot)
        {
            SerializedProperty objRef = row.FindPropertyRelative("Object") ?? row.FindPropertyRelative("m_object");
            if (objRef == null) return null;
            SerializedProperty refPath = objRef.FindPropertyRelative("referencePath");
            SerializedProperty targetObj = objRef.FindPropertyRelative("targetObject");
            if (refPath == null || targetObj == null) return null;
            Transform resolved = ResolveAvatarObjectReference(refPath, targetObj, avatarRoot);
            return resolved != null ? resolved.GetComponent<SkinnedMeshRenderer>() : null;
        }

        // ================================================================
        // R6: MA Merge Animator 経由のクリップ集合
        // ================================================================

        /// <summary>
        /// R6: root 配下の MA MergeAnimator が参照するコントローラーに含まれる全アニメーションクリップを返す。
        /// 共有クリップ(元アバターと共有していて書き換えできないクリップ)が Merge Animator 経由で統合される
        /// ものかを判定し、警告メッセージへ「Merge Animator 経由」である旨を追記するために使う。MA 未導入なら空集合。
        /// </summary>
        public static HashSet<AnimationClip> CollectMergeAnimatorClips(GameObject root)
        {
            var clips = new HashSet<AnimationClip>();
            if (root == null) return clips;

            Type maType = QuestCompat.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
            if (maType == null) return clips;

            foreach (Component c in root.GetComponentsInChildren(maType, true))
            {
                if (c == null) continue;
                SerializedObject so;
                try { so = new SerializedObject(c); }
                catch { continue; }

                SerializedProperty it = so.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var controller = it.objectReferenceValue as RuntimeAnimatorController;
                    if (controller == null) continue;
                    foreach (AnimationClip clip in controller.animationClips)
                    {
                        if (clip != null) clips.Add(clip);
                    }
                }
            }
            return clips;
        }

        // ================================================================
        // R7: MA カバレッジ監査(実行終盤に1行の Info/Warn を出す)
        // ================================================================

        /// <summary>
        /// R7: クローンに載っている MA コンポーネント型を列挙し、本ツールが明示的に理解している型と突き合わせ、
        /// 理解外の型があれば1件 Warn で列挙する(無ければ1件 Info で「把握済み」を要約)。
        /// AvatarQuestConverter.Convert / PCOptimizer.Optimize の終盤から呼ぶ。MA 未導入・MA コンポーネント無しなら no-op。
        /// </summary>
        /// <param name="clone">監査対象のクローン(アバタールート)。</param>
        /// <param name="context">レポート接頭辞に付ける文脈("Quest変換" / "PC軽量化" 等。空でも可)。</param>
        /// <param name="report">出力先。</param>
        public static void AuditCoverage(GameObject clone, string context, ConversionReport report)
        {
            if (clone == null || report == null) return;

            // 短縮型名ごとの個数(名前順で安定表示)。
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                Type t = c.GetType();
                if (t.Namespace == null || !t.Namespace.StartsWith(MaNamespacePrefix, StringComparison.Ordinal)) continue;
                int prev;
                counts.TryGetValue(t.Name, out prev);
                counts[t.Name] = prev + 1;
            }
            if (counts.Count == 0) return; // MA 未使用 → 何も言わない。

            var understood = new List<string>();
            var unknown = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts)
            {
                string label = ShortLabel(kv.Key) + "×" + kv.Value;
                if (UnderstoodMaTypes.Contains(kv.Key)) understood.Add(label);
                else unknown.Add(kv.Key + "×" + kv.Value); // 未知はフル型名で出す(調査しやすさ優先)
            }

            string prefix = string.IsNullOrEmpty(context) ? "MA互換監査" : "MA互換監査(" + context + ")";
            if (unknown.Count > 0)
            {
                report.Warn($"{prefix}: 本ツールが明示的に考慮していないModular Avatarコンポーネントを検出しました: {string.Join("、", unknown)}。マテリアル/メッシュ/ボーンに影響する場合は変換結果を手動で確認してください" +
                    (understood.Count > 0 ? $"(把握済み: {string.Join("、", understood)})。" : "。"));
            }
            else
            {
                report.Info($"{prefix}: Modular Avatarコンポーネントはすべて本ツールが把握済みです({string.Join("、", understood)})。実際の統合・削除・削減はビルド時(NDMF)に適用されます。");
            }
        }

        /// <summary>型短縮名の "ModularAvatar" 接頭辞を落として読みやすくする。</summary>
        private static string ShortLabel(string typeName)
        {
            const string p = "ModularAvatar";
            return typeName != null && typeName.StartsWith(p, StringComparison.Ordinal) && typeName.Length > p.Length
                ? typeName.Substring(p.Length)
                : typeName;
        }

        // ================================================================
        // 共通: MA コンポーネントが参照する Transform の解決(リフレクション/SerializedObject)
        // ================================================================

        /// <summary>
        /// MA コンポーネントのシリアライズを走査し、参照している avatarRoot 配下の Transform を列挙する。
        /// (1) AvatarObjectReference(referencePath + targetObject を子に持つ Generic ノード)を MA と同じ規則で解決、
        /// (2) 直接の Transform/GameObject 参照(BoneProxy.target 等)。いずれも avatarRoot 配下のもののみ返す。
        /// 型名のハードコードを避け MA のバージョン差に頑健にするため、フィールド名ではなく構造で判定する。
        /// </summary>
        private static IEnumerable<Transform> ResolveMaReferencedTransforms(Component maComp, Transform avatarRoot)
        {
            var results = new List<Transform>();
            if (maComp == null || avatarRoot == null) return results;

            SerializedObject so;
            try { so = new SerializedObject(maComp); }
            catch { return results; }

            // BoneProxy はシリアライズされた参照(AvatarObjectReference / 直接 Transform)を持たず、
            // boneReference + subPath から対象を動的に解決する。構造走査では拾えないため専用に解決する。
            if (maComp.GetType().Name == "ModularAvatarBoneProxy")
            {
                Transform bpTarget = ResolveBoneProxyTarget(so, avatarRoot);
                if (bpTarget != null && (bpTarget == avatarRoot || bpTarget.IsChildOf(avatarRoot))) results.Add(bpTarget);
                return results;
            }

            SerializedProperty it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType == SerializedPropertyType.Generic)
                {
                    SerializedProperty refPath = it.FindPropertyRelative("referencePath");
                    SerializedProperty targetObj = it.FindPropertyRelative("targetObject");
                    if (refPath != null && targetObj != null)
                    {
                        Transform resolved = ResolveAvatarObjectReference(refPath, targetObj, avatarRoot);
                        if (resolved != null) results.Add(resolved);
                    }
                }
                else if (it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    Transform tr = AsTransform(it.objectReferenceValue);
                    if (tr != null && (tr == avatarRoot || tr.IsChildOf(avatarRoot))) results.Add(tr);
                }
            }
            return results;
        }

        /// <summary>
        /// MA の AvatarObjectReference(referencePath + targetObject)を avatarRoot 配下の Transform へ解決する。
        /// MA の Get と同じ優先順位: targetObject がアバター配下ならそれ / 番兵ならルート / それ以外は referencePath を Find。
        /// </summary>
        private static Transform ResolveAvatarObjectReference(SerializedProperty referencePathProp, SerializedProperty targetObjectProp, Transform avatarRoot)
        {
            var targetGo = targetObjectProp != null ? targetObjectProp.objectReferenceValue as GameObject : null;
            if (targetGo != null && (targetGo.transform == avatarRoot || targetGo.transform.IsChildOf(avatarRoot)))
            {
                return targetGo.transform;
            }

            string refPath = referencePathProp != null ? referencePathProp.stringValue : null;
            if (string.IsNullOrEmpty(refPath)) return null;
            if (refPath == MaAvatarRootSentinel) return avatarRoot;
            return avatarRoot.Find(refPath);
        }

        /// <summary>
        /// ModularAvatarBoneProxy の再親付け先 Transform を boneReference + subPath から解決する
        /// (MA の ModularAvatarBoneProxy.UpdateDynamicMapping と同じ規則)。解決できなければ null。
        /// avatarRoot は BoneProxy の属するアバタールート(= MA の FindAvatarTransformInParents 相当)を渡す。
        /// </summary>
        private static Transform ResolveBoneProxyTarget(SerializedObject so, Transform avatarRoot)
        {
            SerializedProperty boneRefProp = so.FindProperty("boneReference");
            SerializedProperty subPathProp = so.FindProperty("subPath");
            HumanBodyBones boneReference = boneRefProp != null ? (HumanBodyBones)boneRefProp.intValue : HumanBodyBones.LastBone;
            string subPath = subPathProp != null ? subPathProp.stringValue : null;

            if (boneReference == HumanBodyBones.LastBone && string.IsNullOrWhiteSpace(subPath)) return null;
            if (subPath == "$$AVATAR") return avatarRoot;
            if (boneReference == HumanBodyBones.LastBone)
                return string.IsNullOrEmpty(subPath) ? null : avatarRoot.Find(subPath);

            var animator = avatarRoot.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return null;
            var bone = animator.GetBoneTransform(boneReference);
            if (bone == null) return null;
            if (string.IsNullOrWhiteSpace(subPath)) return bone;
            return bone.Find(subPath);
        }

        /// <summary>ObjectReference を Transform へ(GameObject→transform / Transform→自身 / それ以外→null)。</summary>
        private static Transform AsTransform(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            var go = obj as GameObject;
            if (go != null) return go.transform;
            return obj as Transform;
        }
    }
}
#endif
