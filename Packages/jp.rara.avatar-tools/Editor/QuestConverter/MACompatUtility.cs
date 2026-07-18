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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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

    /// <summary>
    /// Meshia Mesh Simplification(Ram.Type-0 氏 / MIT, https://github.com/RamType0/Meshia.MeshSimplification)の
    /// NDMFコンポーネントをリフレクションで検出する共有ヘルパー。コンパイル時のMeshia依存は持たない(型名解決のみ)。
    /// Meshia はビルド時(Play/Upload)にメッシュを簡略化するため、本ツールのポリゴン削減(変換時に複製へ適用)と
    /// 同一レンダラーへ重ねると二重削減になりうる。UIの併用注意表示のために「付いているか」だけを判定する。
    /// Meshia 未導入・型解決失敗時は常に false(=注意表示は出ない)。
    /// </summary>
    public static class MeshiaCompat
    {
        /// <summary>Meshia のレンダラー単位コンポーネント型フルネーム(Meshia 導入時は常に存在しうる)。</summary>
        public const string PerRendererTypeName = "Meshia.MeshSimplification.Ndmf.MeshiaMeshSimplifier";

        /// <summary>Meshia のアバター単位コンポーネント型フルネーム(Modular Avatar 導入時のみコンパイルされるため不在の可能性あり)。</summary>
        public const string CascadingTypeName = "Meshia.MeshSimplification.Ndmf.MeshiaCascadingAvatarMeshSimplifier";

        /// <summary>
        /// root 配下(非アクティブ含む)に Meshia のいずれかのコンポーネントが1つでも付いていれば true。
        /// Cascading 型は環境により存在しないことがあるため、両型を型名で個別に探す。Meshia 未導入時は false。
        /// </summary>
        public static bool IsPresent(GameObject root)
        {
            if (root == null) return false;
            return HasComponentOfType(root, PerRendererTypeName) || HasComponentOfType(root, CascadingTypeName);
        }

        /// <summary>root 配下(非アクティブ含む)に typeName 型のコンポーネントがあるか。型が解決できなければ(未導入)false。</summary>
        private static bool HasComponentOfType(GameObject root, string typeName)
        {
            Type type = QuestCompat.FindType(typeName);
            if (type == null) return false; // Meshia 未導入 or 当該型なし(no-op)
            Component[] found = root.GetComponentsInChildren(type, true);
            return found != null && found.Length > 0;
        }

        // ================================================================
        // アバター単位(Cascading)コンポーネントの付与・目標設定(リフレクション専用)
        //   ポリゴン削減を Meshia へ委譲するため、複製アバターへ Cascading コンポーネントを付与し、
        //   全体目標三角形数(TargetTriangleCount)と各エントリの按分目標を設定する。
        //   ビルド時(NDMF)の簡略化はエントリごとの TargetTriangleCount のみを読むため、
        //   本ツール側で目標比の按分を明示的に複製する(Meshia のインスペクタ内 AutoAdjust は
        //   ヘッドレスでは走らないため)。Meshia / Modular Avatar 未導入時は全メソッドが安全に no-op。
        // ================================================================

        /// <summary>Cascading コンポーネントを付与する子GameObjectの名前(付与時に生成)。</summary>
        public const string CascadingChildName = "Meshia Mesh Simplifier";

        /// <summary>
        /// Meshia のアバター単位(Cascading)コンポーネントが利用可能か(=Meshia + Modular Avatar 導入済み)。
        /// Cascading 型は ENABLE_MODULAR_AVATAR 下でのみコンパイルされるため、型解決可否で判定する。
        /// </summary>
        public static bool IsCascadingAvailable()
        {
            return QuestCompat.FindType(CascadingTypeName) != null;
        }

        /// <summary>root 配下(非アクティブ含む)の最初の Cascading コンポーネントを返す。無ければ null。</summary>
        public static Component FindCascading(GameObject root)
        {
            if (root == null) return null;
            Type type = QuestCompat.FindType(CascadingTypeName);
            if (type == null) return null;
            Component[] found = root.GetComponentsInChildren(type, true);
            return found != null && found.Length > 0 ? found[0] : null;
        }

        /// <summary>Cascading コンポーネントの全体目標三角形数(TargetTriangleCount)。読めなければ -1。</summary>
        public static int GetCascadingTarget(Component cascading)
        {
            if (cascading == null) return -1;
            FieldInfo f = cascading.GetType().GetField("TargetTriangleCount");
            if (f == null || f.FieldType != typeof(int)) return -1;
            return f.GetValue(cascading) is int i ? i : -1;
        }

        /// <summary>
        /// avatarRoot 直下に新しい子GameObjectを作り、Cascading コンポーネントを付与して目標を設定する。
        /// Cascading はルート自身に付けられない(scope 原点=parent が必要)ため、必ず子へ付ける。
        /// Meshia / Modular Avatar 未導入・付与失敗時は null。返り値のコンポーネントの gameObject が生成した子。
        /// </summary>
        public static Component AddCascading(GameObject avatarRoot, int target)
        {
            if (avatarRoot == null) return null;
            Type type = QuestCompat.FindType(CascadingTypeName);
            if (type == null) return null; // Meshia + MA 未導入

            var child = new GameObject(CascadingChildName);
            child.transform.SetParent(avatarRoot.transform, false);

            Component cascading = child.AddComponent(type);
            if (cascading == null)
            {
                UnityEngine.Object.DestroyImmediate(child);
                return null;
            }
            ConfigureCascading(cascading, target);
            return cascading;
        }

        /// <summary>
        /// 既存の Cascading コンポーネントの全体目標を更新し、各エントリの按分目標を再計算する(エディタ操作用)。
        /// Undo に登録し Dirty 化する。cascading が null なら false。
        /// </summary>
        public static bool UpdateCascadingTarget(Component cascading, int target)
        {
            if (cascading == null) return false;
            Undo.RegisterCompleteObjectUndo(cascading, "Meshia 目標を更新");
            bool ok = ConfigureCascading(cascading, target);
            EditorUtility.SetDirty(cascading);
            return ok;
        }

        /// <summary>
        /// Cascading コンポーネントのエントリを最新化(RefreshEntries)し、全体目標三角形数と AutoAdjust を設定した上で、
        /// 各エントリの目標を目標比で按分する。ビルド時のNDMFはエントリごとの目標のみを読むため按分は必須。
        /// </summary>
        public static bool ConfigureCascading(Component cascading, int target)
        {
            if (cascading == null) return false;
            InvokeRefreshEntries(cascading);
            SetTopLevel(cascading, target);
            ApplyDistribution(cascading, target);
            return true;
        }

        /// <summary>public な RefreshEntries()(引数なし)をリフレクションで呼ぶ(所有レンダラーからエントリを補充)。</summary>
        private static void InvokeRefreshEntries(Component cascading)
        {
            MethodInfo m = cascading.GetType().GetMethod("RefreshEntries", Type.EmptyTypes);
            if (m != null) m.Invoke(cascading, null);
        }

        /// <summary>全体目標三角形数(TargetTriangleCount)と AutoAdjustEnabled=true を設定する。</summary>
        private static void SetTopLevel(Component cascading, int target)
        {
            Type type = cascading.GetType();
            FieldInfo tgt = type.GetField("TargetTriangleCount");
            if (tgt != null && tgt.FieldType == typeof(int)) tgt.SetValue(cascading, Mathf.Max(1, target));
            FieldInfo auto = type.GetField("AutoAdjustEnabled");
            if (auto != null && auto.FieldType == typeof(bool)) auto.SetValue(cascading, true);
        }

        /// <summary>
        /// 各エントリの目標三角形数を、元メッシュの三角形数に対する目標比で按分して設定する。
        /// 元三角形数はエントリのレンダラー実測(解決失敗時はエントリ既定の TargetTriangleCount=付与直後の全数)を用いる。
        /// 目標が合計以上(削減不要)・保護(!Enabled / Fixed)のエントリは元三角形数のまま据え置く。
        /// </summary>
        private static void ApplyDistribution(Component cascading, int target)
        {
            IList entries = GetEntries(cascading, out Type entryType);
            if (entries == null || entryType == null) return;
            FieldInfo fTgt = entryType.GetField("TargetTriangleCount");
            if (fTgt == null) return;
            FieldInfo fEnabled = entryType.GetField("Enabled");
            FieldInfo fFixed = entryType.GetField("Fixed");
            FieldInfo fRef = entryType.GetField("RendererObjectReference");

            int n = entries.Count;
            var originals = new int[n];
            long grandTotal = 0;
            for (int i = 0; i < n; i++)
            {
                object e = entries[i];
                int fallback = e != null ? Mathf.Max(0, (int)fTgt.GetValue(e)) : 0;
                int tris = fallback;
                if (e != null && fRef != null)
                {
                    Mesh mesh = GetRendererMesh(ResolveRenderer(fRef.GetValue(e), cascading));
                    int mtc = GetMeshTriangleCount(mesh);
                    if (mtc > 0) tris = mtc;
                }
                originals[i] = tris;
                grandTotal += Mathf.Max(0, tris);
            }
            if (grandTotal <= 0) return;

            double ratio = (double)Mathf.Max(1, target) / grandTotal;
            for (int i = 0; i < n; i++)
            {
                object e = entries[i];
                if (e == null) continue;
                int orig = Mathf.Max(1, originals[i]);
                bool enabled = fEnabled == null || (bool)fEnabled.GetValue(e);
                bool isFixed = fFixed != null && (bool)fFixed.GetValue(e);
                int newTarget = (ratio >= 1.0 || !enabled || isFixed)
                    ? orig
                    : Mathf.Max(1, (int)Math.Round(orig * ratio));
                fTgt.SetValue(e, newTarget);
            }
        }

        /// <summary>Cascading の Entries(List)を IList として取り出し、要素型を out で返す。読めなければ null。</summary>
        private static IList GetEntries(Component cascading, out Type entryType)
        {
            entryType = null;
            FieldInfo f = cascading.GetType().GetField("Entries");
            if (f == null) return null;
            IList list = f.GetValue(cascading) as IList;
            if (list == null) return null;
            Type ft = f.FieldType;
            if (ft.IsGenericType)
            {
                Type[] args = ft.GetGenericArguments();
                if (args.Length == 1) entryType = args[0];
            }
            if (entryType == null && list.Count > 0 && list[0] != null) entryType = list[0].GetType();
            return list;
        }

        /// <summary>AvatarObjectReference.Get(Component) をリフレクションで呼び、指すレンダラーを解決する。失敗時 null。</summary>
        private static Renderer ResolveRenderer(object avatarObjectReference, Component container)
        {
            if (avatarObjectReference == null || container == null) return null;
            try
            {
                MethodInfo getMethod = avatarObjectReference.GetType().GetMethod("Get", new[] { typeof(Component) });
                if (getMethod == null) return null;
                var go = getMethod.Invoke(avatarObjectReference, new object[] { container }) as GameObject;
                return go != null ? go.GetComponent<Renderer>() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>レンダラーが参照する共有メッシュ(SkinnedMeshRenderer / MeshFilter)。</summary>
        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer == null) return null;
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>メッシュの総三角形数(全サブメッシュのインデックス数合計 ÷ 3)。null なら 0。</summary>
        private static int GetMeshTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) total += mesh.GetIndexCount(i);
            return (int)Math.Min(total / 3, int.MaxValue);
        }
    }
}
#endif
