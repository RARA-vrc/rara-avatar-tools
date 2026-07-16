// RARA Quest Converter - Modular Avatar 互換ユーティリティ(共有・リフレクション専用)
// ---------------------------------------------------------------------------
// Modular Avatar(MA)の「マテリアルを差し替える」コンポーネント
//   ・MA Material Setter (ModularAvatarMaterialSetter) … 対象レンダラーの特定スロット(MaterialIndex)へ
//     Material を差し込む(スロット番号に依存)。
//   ・MA Material Swap  (ModularAvatarMaterialSwap)   … ルート配下レンダラーで From マテリアルを To へ
//     差し替える(マテリアルの「同一性」に依存)。
// を、コンパイル時のMA依存なしに(型名解決 + SerializedObject)読み取るための共有ヘルパー。
// MA 未導入・型解決失敗時は全メソッドが安全に空を返す(=呼び出し側のガードは自然に no-op になる)。
//
// アトラス統合はスロット番号の付け替え・マテリアルの1本化を行うため、上記MAコンポーネントが指す
// レンダラー/マテリアルと素朴に統合すると差し替えが壊れる。QuestConverter(MaterialAtlasser)と
// PCOptimizer(PCMaterialAtlasser)の両アトラッサーが本ヘルパーを使い、
//   (1) 参照マテリアルをアトラス詰め込みから除外(CollectReferencedMaterials)
//   (2) 対象レンダラーのスロット統合を抑止(CollectTargetRendererPaths)
// する。M2(除外・ガード側)からも再利用できるよう、個別読み取り(FindMAComponents /
// ResolveAvatarObjectReference / GetMaterialSetterEntries / GetMaterialSwapEntries)も公開する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RARA.QuestConverter
{
    /// <summary>
    /// Modular Avatar のマテリアル系コンポーネントをリフレクションで読む共有ユーティリティ。
    /// すべてのMAアクセスは型名解決 + SerializedObject 経由(コンパイル時MA依存なし)。
    /// MA 未導入時は各メソッドが空集合/空リストを返す。
    /// </summary>
    public static class MACompatUtility
    {
        /// <summary>MA Material Setter のコンポーネント型フルネーム。</summary>
        public const string MaterialSetterTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMaterialSetter";

        /// <summary>MA Material Swap のコンポーネント型フルネーム。</summary>
        public const string MaterialSwapTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMaterialSwap";

        /// <summary>AvatarObjectReference がアバタールート自身を指すときの番兵値(MAの定義と一致)。</summary>
        public const string AvatarRootSentinel = "$$$AVATAR_ROOT$$$";

        /// <summary>MA Material Setter の1エントリ(対象レンダラー + 差し込むスロット番号 + 差し込むマテリアル)。</summary>
        public struct MaterialSetterEntry
        {
            /// <summary>差し替え対象レンダラー(参照が解決できない場合 null)。</summary>
            public Renderer renderer;
            /// <summary>差し込むマテリアルスロット番号(MaterialIndex)。</summary>
            public int materialIndex;
            /// <summary>差し込むマテリアル(未設定なら null)。</summary>
            public Material material;
        }

        /// <summary>MA Material Swap の1エントリ(対象ルート配下の全レンダラー + From/To)。</summary>
        public struct MaterialSwapEntry
        {
            /// <summary>ルート(m_root)配下の全レンダラー(参照が解決できない場合は空配列)。同一コンポーネント内の全エントリで共通。</summary>
            public Renderer[] renderers;
            /// <summary>差し替え元マテリアル(同一性で照合される)。</summary>
            public Material from;
            /// <summary>差し替え先マテリアル。</summary>
            public Material to;
        }

        /// <summary>component が MA Material Setter / Material Swap のいずれかか(型フルネームで判定)。</summary>
        public static bool IsMaterialSetterOrSwap(Component component)
        {
            if (component == null) return false;
            string full = component.GetType().FullName;
            return full == MaterialSetterTypeName || full == MaterialSwapTypeName;
        }

        /// <summary>
        /// root 配下(非アクティブ含む)の typeName 型コンポーネントを列挙する。
        /// MA 未導入・型解決失敗時は空リストを返す。
        /// </summary>
        public static List<Component> FindMAComponents(GameObject root, string typeName)
        {
            var result = new List<Component>();
            if (root == null || string.IsNullOrEmpty(typeName)) return result;
            Type type = QuestCompat.FindType(typeName);
            if (type == null) return result; // MA 未導入
            foreach (Component c in root.GetComponentsInChildren(type, true))
            {
                if (c != null) result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// MA の AvatarObjectReference(referencePath + targetObject)を avatarRoot 配下の Transform へ解決する。
        /// MA の AvatarObjectReference.Get と同じ優先順位(targetObject がアバター配下ならそれ /
        /// AVATAR_ROOT 番兵ならルート / それ以外は referencePath を Find)。解決不能なら null。
        /// </summary>
        public static Transform ResolveAvatarObjectReference(SerializedProperty objRef, GameObject avatarRoot)
        {
            if (objRef == null || avatarRoot == null) return null;

            SerializedProperty pathProp = objRef.FindPropertyRelative("referencePath");
            SerializedProperty targetProp = objRef.FindPropertyRelative("targetObject");

            var targetGo = targetProp != null ? targetProp.objectReferenceValue as GameObject : null;
            if (targetGo != null &&
                (targetGo.transform == avatarRoot.transform || targetGo.transform.IsChildOf(avatarRoot.transform)))
            {
                return targetGo.transform;
            }

            string refPath = pathProp != null ? pathProp.stringValue : null;
            if (string.IsNullOrEmpty(refPath)) return null;
            if (refPath == AvatarRootSentinel) return avatarRoot.transform;
            return avatarRoot.transform.Find(refPath);
        }

        /// <summary>
        /// component の fieldName フィールド(AvatarObjectReference)を avatarRoot 配下の Transform へ解決する。
        /// フィールドが無い/解決不能なら null。
        /// </summary>
        public static Transform ResolveAvatarObjectReference(Component component, string fieldName, GameObject avatarRoot)
        {
            if (component == null || avatarRoot == null || string.IsNullOrEmpty(fieldName)) return null;
            var serializedObject = new SerializedObject(component);
            SerializedProperty objRef = serializedObject.FindProperty(fieldName);
            return ResolveAvatarObjectReference(objRef, avatarRoot);
        }

        /// <summary>
        /// MA Material Setter(setter)の m_objects を読み、各エントリ(対象レンダラー・スロット番号・マテリアル)を返す。
        /// setter が MA Material Setter でない/読めない場合は空リスト。
        /// </summary>
        public static List<MaterialSetterEntry> GetMaterialSetterEntries(Component setter, GameObject avatarRoot)
        {
            var result = new List<MaterialSetterEntry>();
            if (setter == null || avatarRoot == null) return result;

            var serializedObject = new SerializedObject(setter);
            SerializedProperty objects = serializedObject.FindProperty("m_objects");
            if (objects == null || !objects.isArray) return result;

            for (int i = 0; i < objects.arraySize; i++)
            {
                SerializedProperty element = objects.GetArrayElementAtIndex(i);
                if (element == null) continue;

                SerializedProperty objRef = element.FindPropertyRelative("Object");
                SerializedProperty matProp = element.FindPropertyRelative("Material");
                SerializedProperty idxProp = element.FindPropertyRelative("MaterialIndex");

                Transform target = objRef != null ? ResolveAvatarObjectReference(objRef, avatarRoot) : null;
                result.Add(new MaterialSetterEntry
                {
                    renderer = target != null ? target.GetComponent<Renderer>() : null,
                    materialIndex = idxProp != null ? idxProp.intValue : 0,
                    material = matProp != null ? matProp.objectReferenceValue as Material : null,
                });
            }
            return result;
        }

        /// <summary>
        /// MA Material Swap(swap)の m_root / m_swaps を読み、各エントリ(ルート配下レンダラー群・From/To)を返す。
        /// swap が MA Material Swap でない/読めない場合は空リスト。
        /// </summary>
        public static List<MaterialSwapEntry> GetMaterialSwapEntries(Component swap, GameObject avatarRoot)
        {
            var result = new List<MaterialSwapEntry>();
            if (swap == null || avatarRoot == null) return result;

            var serializedObject = new SerializedObject(swap);
            SerializedProperty rootRef = serializedObject.FindProperty("m_root");
            Transform rootTf = rootRef != null ? ResolveAvatarObjectReference(rootRef, avatarRoot) : null;
            Renderer[] renderers = rootTf != null
                ? rootTf.GetComponentsInChildren<Renderer>(true)
                : Array.Empty<Renderer>();

            SerializedProperty swaps = serializedObject.FindProperty("m_swaps");
            if (swaps == null || !swaps.isArray) return result;

            for (int i = 0; i < swaps.arraySize; i++)
            {
                SerializedProperty element = swaps.GetArrayElementAtIndex(i);
                if (element == null) continue;

                SerializedProperty fromProp = element.FindPropertyRelative("From");
                SerializedProperty toProp = element.FindPropertyRelative("To");
                result.Add(new MaterialSwapEntry
                {
                    renderers = renderers,
                    from = fromProp != null ? fromProp.objectReferenceValue as Material : null,
                    to = toProp != null ? toProp.objectReferenceValue as Material : null,
                });
            }
            return result;
        }

        /// <summary>
        /// root 配下の MA Material Setter / Material Swap が参照する全マテリアル(Setter: Material、
        /// Swap: From/To)を返す。アトラス詰め込みからの除外に使う。MA 未導入時は空集合。
        /// </summary>
        public static HashSet<Material> CollectReferencedMaterials(GameObject root)
        {
            var materials = new HashSet<Material>();
            if (root == null) return materials;

            foreach (Component setter in FindMAComponents(root, MaterialSetterTypeName))
            {
                foreach (MaterialSetterEntry entry in GetMaterialSetterEntries(setter, root))
                {
                    if (entry.material != null) materials.Add(entry.material);
                }
            }
            foreach (Component swap in FindMAComponents(root, MaterialSwapTypeName))
            {
                foreach (MaterialSwapEntry entry in GetMaterialSwapEntries(swap, root))
                {
                    if (entry.from != null) materials.Add(entry.from);
                    if (entry.to != null) materials.Add(entry.to);
                }
            }
            return materials;
        }

        /// <summary>
        /// MA Material Setter の対象レンダラー / Material Swap のルート配下レンダラーの、root 相対 Transform パス集合を返す。
        /// これらのレンダラーはスロット番号(MaterialIndex)やマテリアル同一性に依存するため、
        /// アトラスのスロット統合(RemapMeshesAndMergeSlots)を抑止する(=UV再配置のみに留める)ために使う。
        /// パスの形式は AnimationUtility.CalculateTransformPath と同一(root 直下は "child/grandchild")。MA 未導入時は空集合。
        /// </summary>
        public static HashSet<string> CollectTargetRendererPaths(GameObject root)
        {
            var paths = new HashSet<string>();
            if (root == null) return paths;

            foreach (Component setter in FindMAComponents(root, MaterialSetterTypeName))
            {
                foreach (MaterialSetterEntry entry in GetMaterialSetterEntries(setter, root))
                {
                    if (entry.renderer == null) continue;
                    string p = QuestCompat.GetRelativePath(root.transform, entry.renderer.transform);
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                }
            }
            foreach (Component swap in FindMAComponents(root, MaterialSwapTypeName))
            {
                foreach (MaterialSwapEntry entry in GetMaterialSwapEntries(swap, root))
                {
                    if (entry.renderers == null) continue;
                    foreach (Renderer r in entry.renderers)
                    {
                        if (r == null) continue;
                        string p = QuestCompat.GetRelativePath(root.transform, r.transform);
                        if (!string.IsNullOrEmpty(p)) paths.Add(p);
                    }
                }
            }
            return paths;
        }
    }
}
#endif
