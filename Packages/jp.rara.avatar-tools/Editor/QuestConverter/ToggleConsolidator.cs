// RARA Quest Converter - 衣装・トグル整理モジュール
// トグルで切り替える衣装・アクセサリ(FXの m_IsActive アニメ / Modular Avatar のオブジェクトトグル)を
// グループごとに「維持 / 常時表示に固定 / 非表示に固定(除去)」できるようにする。
// 「常時表示に固定」は常時ON化 + m_IsActive バインディング除去により、AvatarOptimizer(AAO)が
// 同一の activeness バケットへ入れてスキンメッシュ・マテリアルスロットを統合できるようにする
// (独立トグルのままだと AAO は結合を拒否する。研究の結論)。「非表示に固定」はメッシュごと EditorOnly 化する。
// VRChat Avatars SDK 3.10.4 / Unity 2022.3 向け。Editor専用。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>
    /// 検出したトグルグループ1件(=切り替え対象のGameObjectパス1つ)。
    /// DetectToggleGroups が返し、ウィンドウの一覧表示・ユーザー選択に使う(READ-ONLYな検出結果)。
    /// </summary>
    public class ToggleGroup
    {
        /// <summary>グループID(アバタールート相対のオブジェクトパス)。settings.toggleChoices の groupId と対応。</summary>
        public string id;

        /// <summary>表示用ラベル(対象GameObject名)。</summary>
        public string label;

        /// <summary>このグループが指すオブジェクトパス群(現状は1グループ1パス。ピン表示・拡張用にリスト)。</summary>
        public List<string> objectPaths;

        /// <summary>現在の表示状態(対象GameObjectの activeSelf)。</summary>
        public bool defaultActive;

        /// <summary>検出元("FX" = m_IsActiveアニメ / "MA" = Modular Avatarオブジェクトトグル)。</summary>
        public string source;

        /// <summary>このグループのサブツリーに含まれるRenderer数(統合で減るメッシュ量の目安)。</summary>
        public int rendererCount;
    }

    /// <summary>
    /// 衣装・トグルの検出(DetectToggleGroups)と、選択に応じた固定処理(ApplyConsolidation)を行う。
    /// 検出は完全にREAD-ONLY。固定処理はクローン(複製アバター)のみを編集し、
    /// 元PCアバターと共有するアセット(コントローラー・クリップ)は一切書き換えない。
    /// </summary>
    public static class ToggleConsolidator
    {
        /// <summary>Modular Avatar オブジェクトトグルの型名(未導入時はリフレクションで解決失敗して無視される)。</summary>
        private const string MaObjectToggleTypeName = "nadena.dev.modular_avatar.core.ModularAvatarObjectToggle";

        /// <summary>Modular Avatar の AvatarObjectReference がアバタールートを指すときの referencePath 番兵値。</summary>
        private const string MaAvatarRootSentinel = "$$$AVATAR_ROOT$$$";

        /// <summary>GameObjectのアクティブ状態を表すフロートカーブのプロパティ名(AAOも同名で参照)。</summary>
        private const string ActiveProperty = "m_IsActive";

        /// <summary>レポートの明細行の上限(トグルが多いアバターでログが溢れないようにする)。</summary>
        private const int ReportDetailCap = 30;

        /// <summary>outputDir未指定時に使う既定の生成ルート(QuestConvertSettings.outputFolder の既定値と一致)。</summary>
        private const string DefaultGeneratedRoot = "Assets/RARA/QuestConverter/Generated";

        // ================================================================
        // 検出(READ-ONLY)
        // ================================================================

        /// <summary>
        /// avatarRoot 配下のトグル(FXの m_IsActive アニメ / Modular Avatar オブジェクトトグル)を
        /// 走査し、切り替え対象のオブジェクトパスごとに1つの ToggleGroup を返す。
        /// 収集範囲は AnimationConverter.CollectControllers と共通(FXレイヤー・子Animator・MA Merge Animator等)。
        /// サブツリーにRendererを含むパスのみ採用し、同一パスは畳む。アバターは一切変更しない。
        /// </summary>
        public static List<ToggleGroup> DetectToggleGroups(GameObject avatarRoot)
        {
            var groups = new List<ToggleGroup>();
            if (avatarRoot == null) return groups;

            // パスで重複排除(FXとMAの両方で切り替わる場合も1グループに畳む。先勝ち)
            var byPath = new Dictionary<string, ToggleGroup>(StringComparer.Ordinal);

            // 1) FX等: GameObjectのアクティブ(m_IsActive)をアニメーションするクリップ。
            //    ComponentRemover.CollectPhysBoneTogglePaths と同じ二段構えで収集する。
            //    (a) 到達可能な全コントローラー = アバタールート相対とみなす(FXレイヤー等の主経路)。
            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(avatarRoot))
            {
                if (controller == null) continue;
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    AddFxGroupsFromClip(clip, string.Empty, avatarRoot, byPath, groups);
                }
            }

            //    (b) ルート以外のコンポーネント(子Animator / MA Merge Animator等)が参照する
            //        コントローラーは、バインディングパスがそのコンポーネント基準の可能性があるため、
            //        コンポーネント位置を前置したパスでも解釈する。いずれも実在パス(Renderer入り)のみ採用するため、
            //        誤解釈は自然に落ちる(TryAddGroup が検証する)。
            var seenPrefixed = new HashSet<string>();
            foreach (Component component in avatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (component.transform == avatarRoot.transform) continue;
                string prefix = QuestCompat.GetRelativePath(avatarRoot.transform, component.transform);
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
                        AddFxGroupsFromClip(clip, prefix, avatarRoot, byPath, groups);
                    }
                }
            }

            // 2) Modular Avatar のオブジェクトトグル(MA未導入時はリフレクションで解決失敗して無視される)
            AddMaObjectToggleGroups(avatarRoot, byPath, groups);

            return groups;
        }

        /// <summary>クリップ内の m_IsActive バインディングのパス(prefixを前置)をグループ候補として追加する。</summary>
        private static void AddFxGroupsFromClip(AnimationClip clip, string prefix, GameObject avatarRoot,
            Dictionary<string, ToggleGroup> byPath, List<ToggleGroup> groups)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(GameObject) || binding.propertyName != ActiveProperty) continue;
                string raw = binding.path ?? string.Empty;
                string full = prefix.Length == 0 ? raw : (raw.Length == 0 ? prefix : prefix + "/" + raw);
                TryAddGroup(full, "FX", avatarRoot, byPath, groups);
            }
        }

        /// <summary>
        /// path を avatarRoot 配下で解決し、Rendererを含む実在サブツリーであれば ToggleGroup を追加する。
        /// 空パス(=ルート自身)・解決不能・Renderer無しは対象外。パス重複は先勝ちで畳む。
        /// </summary>
        private static void TryAddGroup(string path, string source, GameObject avatarRoot,
            Dictionary<string, ToggleGroup> byPath, List<ToggleGroup> groups)
        {
            if (string.IsNullOrEmpty(path)) return;      // アバタールート自身はトグルグループにしない
            if (byPath.ContainsKey(path)) return;        // パスで重複排除(先勝ち)

            Transform target = QuestCompat.FindByPath(avatarRoot.transform, path);
            if (target == null || target == avatarRoot.transform) return;

            int rendererCount = CountRenderers(target);
            if (rendererCount == 0) return;              // Rendererを含まないパスは統合対象にならない

            var group = new ToggleGroup
            {
                id = path,
                label = target.gameObject.name,
                objectPaths = new List<string> { path },
                defaultActive = target.gameObject.activeSelf,
                source = source,
                rendererCount = rendererCount,
            };
            byPath[path] = group;
            groups.Add(group);
        }

        /// <summary>root(含む)配下のRenderer総数。</summary>
        private static int CountRenderers(Transform root)
        {
            if (root == null) return 0;
            return root.GetComponentsInChildren<Renderer>(true).Length;
        }

        /// <summary>Modular Avatar のオブジェクトトグルが切り替える各オブジェクトをグループ候補として追加する。</summary>
        private static void AddMaObjectToggleGroups(GameObject avatarRoot,
            Dictionary<string, ToggleGroup> byPath, List<ToggleGroup> groups)
        {
            Type maType = QuestCompat.FindType(MaObjectToggleTypeName);
            if (maType == null) return; // Modular Avatar 未導入
            foreach (Component comp in avatarRoot.GetComponentsInChildren(maType, true))
            {
                if (comp == null) continue;
                foreach (string path in ResolveMaTogglePaths(comp, avatarRoot))
                {
                    TryAddGroup(path, "MA", avatarRoot, byPath, groups);
                }
            }
        }

        /// <summary>
        /// ModularAvatarObjectToggle の m_objects(ToggledObjectの配列)を SerializedObject で読み、
        /// 各要素の対象オブジェクトを avatarRoot 相対パスへ解決して返す(コンパイル時MA依存を避けるため反射的に読む)。
        /// </summary>
        private static IEnumerable<string> ResolveMaTogglePaths(Component maToggle, GameObject avatarRoot)
        {
            var result = new List<string>();
            var serializedObject = new SerializedObject(maToggle);
            SerializedProperty objects = serializedObject.FindProperty("m_objects");
            if (objects == null || !objects.isArray) return result;

            for (int i = 0; i < objects.arraySize; i++)
            {
                SerializedProperty element = objects.GetArrayElementAtIndex(i);
                SerializedProperty objRef = element != null ? element.FindPropertyRelative("Object") : null;
                if (objRef == null) continue;

                Transform target = ResolveAvatarObjectReference(objRef, avatarRoot);
                if (target == null || target == avatarRoot.transform) continue;
                string path = QuestCompat.GetRelativePath(avatarRoot.transform, target);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
            return result;
        }

        /// <summary>
        /// Modular Avatar の AvatarObjectReference(referencePath + targetObject)を avatarRoot 配下の
        /// Transform へ解決する。MAの AvatarObjectReference.Get と同じ優先順位
        /// (targetObjectがアバター配下ならそれ / AVATAR_ROOT番兵ならルート / それ以外は referencePath を Find)。
        /// </summary>
        private static Transform ResolveAvatarObjectReference(SerializedProperty objRef, GameObject avatarRoot)
        {
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
            if (refPath == MaAvatarRootSentinel) return avatarRoot.transform;
            return avatarRoot.transform.Find(refPath);
        }

        // ================================================================
        // 固定処理(クローンのみ編集)
        // ================================================================

        /// <summary>
        /// choices に従ってクローン(cloneRoot)上のトグルを固定する。
        /// ・LockVisible: 対象を常時ONにし、m_IsActive バインディングを除去して AAO が統合できるようにする。
        /// ・LockHidden : 対象を EditorOnly 化 + 非アクティブ化してメッシュごとビルドから除去する。
        /// ・Keep       : 何もしない。
        /// 【安全性】m_IsActive の除去は、元PCアバターと共有するクリップ・コントローラーを絶対に書き換えない。
        /// 対象クリップを複製して当該バインディングだけ除去し、クローンのコントローラー参照を
        /// クローン専用の AnimatorOverrideController へ差し替える(ベースと元クリップは無改変)。
        /// あわせて Modular Avatar オブジェクトトグルの該当エントリも外す(ビルド時の再アニメ生成を防ぐ)。
        ///
        /// 【実行順序の推奨(オーケストレーター向け・本関数はクローン編集のみ / 呼び出し順は配線側が決める)】
        /// RARAのアニメーション変換(AnimationConverter)より前に呼ぶと、AAO/ビルドが見る有効クリップから
        /// m_IsActive が確実に外れ、後段のクリップ複製(AnimationConverter)がロック済みトグルの
        /// バインディングを持ち越さない。順序が前後しても本関数は共有アセットを壊さないため安全。
        ///
        /// outputDir / assets は任意(既定の3引数呼び出しでは outputDir をクローン名から導出し、
        /// assets は内部生成する)。オーケストレーターが自身の出力先・ConversionAssetContext を渡すと、
        /// 生成物が同一フォルダに収まり「前回の生成物(未使用)」報告とも整合する。
        /// </summary>
        public static void ApplyConsolidation(GameObject cloneRoot, List<ToggleGroupChoice> choices, ConversionReport report,
            string outputDir = null, ConversionAssetContext assets = null)
        {
            if (report == null) report = new ConversionReport(); // 呼び出し側の渡し忘れ対策(結果は破棄される)
            if (cloneRoot == null)
            {
                report.Error("衣装・トグル整理: 対象アバター(cloneRoot)がnullです。");
                return;
            }
            if (choices == null || choices.Count == 0)
            {
                report.Info("衣装・トグル整理: 指定がないためスキップしました。");
                return;
            }

            string animRoot = ResolveOutputDir(outputDir, cloneRoot);
            var assetContext = assets ?? new ConversionAssetContext();

            // LockVisible / LockHidden の対象パス(m_IsActive 除去・MAトグル解除の対象)
            var stripPaths = new HashSet<string>(StringComparer.Ordinal);
            var details = new List<string>();
            int lockVisible = 0, lockHidden = 0, kept = 0, notFound = 0;

            foreach (ToggleGroupChoice choice in choices)
            {
                if (choice == null || string.IsNullOrEmpty(choice.groupId)) continue;

                if (choice.choice == ToggleLockChoice.Keep)
                {
                    kept++;
                    details.Add("維持: " + choice.groupId);
                    continue;
                }

                Transform target = QuestCompat.FindByPath(cloneRoot.transform, choice.groupId);
                if (target == null || target == cloneRoot.transform)
                {
                    notFound++;
                    report.Warn($"衣装・トグル整理: パスが見つからないためスキップしました: {choice.groupId}");
                    continue;
                }

                GameObject go = target.gameObject;
                if (choice.choice == ToggleLockChoice.LockVisible)
                {
                    go.SetActive(true);
                    stripPaths.Add(choice.groupId);
                    lockVisible++;
                    details.Add("固定(表示): " + choice.groupId);
                }
                else // LockHidden
                {
                    go.tag = QuestCompat.EditorOnlyTag; // EditorOnlyサブツリーはVRChatビルドで除去される
                    go.SetActive(false);
                    // R3: この非表示固定サブツリーを参照/内包する Modular Avatar コンポーネントがあれば警告する
                    // (外側からの参照はビルドで動かない可能性、内側の ReplaceObject/MergeArmature は未変換素材が表示側へ移動する可能性)。
                    MACompatAudit.WarnMaReferencesIntoExcludedSubtree(cloneRoot, target, choice.groupId, report);
                    stripPaths.Add(choice.groupId);
                    lockHidden++;
                    details.Add("固定(非表示): " + choice.groupId);
                }
            }

            int maEntriesRemoved = 0;
            int strippedClipCount = 0;
            if (stripPaths.Count > 0)
            {
                // MAオブジェクトトグルの該当エントリを外す。残すとMAがビルド時に m_IsActive アニメを
                // 再生成し、AAOが「アニメ制御あり」と判定して統合しなくなるため。
                maEntriesRemoved = RemoveMaToggleEntries(cloneRoot, stripPaths, report);

                // FX等の m_IsActive バインディングを、共有アセットを壊さずに除去する
                // (対象クリップを複製し、クローンのコントローラーを override で差し替える)。
                strippedClipCount = StripActiveBindings(cloneRoot, stripPaths, animRoot, assetContext, report);
                if (strippedClipCount > 0) AssetDatabase.SaveAssets();
            }

            // 明細(上限で打ち切り)→ サマリー
            int shown = 0;
            foreach (string line in details)
            {
                if (shown >= ReportDetailCap)
                {
                    report.Info($"衣装・トグル整理: ...他 {details.Count - ReportDetailCap} 件(明細は省略)");
                    break;
                }
                report.Info("衣装・トグル整理 " + line);
                shown++;
            }
            report.Info(string.Format(
                "衣装・トグル整理: 固定(表示) {0} / 固定(非表示) {1} / 維持 {2}{3} / m_IsActive除去クリップ {4} 件{5}",
                lockVisible, lockHidden, kept,
                notFound > 0 ? " / 未検出 " + notFound : string.Empty,
                strippedClipCount,
                maEntriesRemoved > 0 ? " / MAトグル解除 " + maEntriesRemoved + " 件" : string.Empty));
        }

        /// <summary>
        /// クローン上の ModularAvatarObjectToggle から、stripPaths に該当する切り替えエントリを削除する。
        /// クローンのコンポーネント(=元アバターと非共有)のみを編集する。削除件数を返す。
        /// </summary>
        private static int RemoveMaToggleEntries(GameObject cloneRoot, HashSet<string> stripPaths, ConversionReport report)
        {
            Type maType = QuestCompat.FindType(MaObjectToggleTypeName);
            if (maType == null) return 0;

            int removed = 0;
            foreach (Component comp in cloneRoot.GetComponentsInChildren(maType, true))
            {
                if (comp == null) continue;
                var serializedObject = new SerializedObject(comp);
                SerializedProperty objects = serializedObject.FindProperty("m_objects");
                if (objects == null || !objects.isArray) continue;

                bool changed = false;
                for (int i = objects.arraySize - 1; i >= 0; i--)
                {
                    SerializedProperty element = objects.GetArrayElementAtIndex(i);
                    SerializedProperty objRef = element != null ? element.FindPropertyRelative("Object") : null;
                    if (objRef == null) continue;

                    Transform target = ResolveAvatarObjectReference(objRef, cloneRoot);
                    if (target == null) continue;
                    string path = QuestCompat.GetRelativePath(cloneRoot.transform, target);
                    if (string.IsNullOrEmpty(path) || !stripPaths.Contains(path)) continue;

                    objects.DeleteArrayElementAtIndex(i);
                    changed = true;
                    removed++;
                }
                if (changed) serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }
            if (removed > 0)
            {
                report.Info($"衣装・トグル整理: Modular Avatar のオブジェクトトグルから {removed} 件を固定用に解除しました。");
            }
            return removed;
        }

        // ----------------------------------------------------------------
        // m_IsActive バインディング除去(共有アセットを壊さない override 方式)
        // ----------------------------------------------------------------

        /// <summary>
        /// クローンから到達可能なコントローラーのうち、stripPaths を対象とする m_IsActive バインディングを
        /// 含むものを、クローン専用の AnimatorOverrideController(複製・除去済みクリップで差し替え)へ置き換える。
        /// ベースコントローラー・元クリップ(元PCアバターと共有)は一切変更しない。除去したクリップ数を返す。
        /// </summary>
        private static int StripActiveBindings(GameObject cloneRoot, HashSet<string> stripPaths,
            string animRoot, ConversionAssetContext assets, ConversionReport report)
        {
            // 元コントローラー → 差し替え後(override or 元のまま)。同じコントローラーを二重処理しない。
            // キーは (コントローラー, そのサイトで有効な stripPaths) の複合。同一コントローラーでも
            // 参照サイト(コンポーネント位置)により siteStripPaths が異なるため、コントローラー単独をキーにすると
            // 別サイトのラップ結果を誤って再利用し、ロックが取りこぼされる(strip漏れ)。
            var wrapCache = new Dictionary<string, RuntimeAnimatorController>();
            int strippedClipCount = 0;

            // 1) VRCAvatarDescriptor のアニメーションレイヤー(アバタールート相対 = prefix空)
            var descriptor = cloneRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                bool changed = false;

                var baseLayers = descriptor.baseAnimationLayers;
                if (WrapLayerArray(baseLayers, stripPaths, wrapCache, animRoot, assets, report, ref strippedClipCount))
                {
                    descriptor.baseAnimationLayers = baseLayers;
                    changed = true;
                }
                var specialLayers = descriptor.specialAnimationLayers;
                if (WrapLayerArray(specialLayers, stripPaths, wrapCache, animRoot, assets, report, ref strippedClipCount))
                {
                    descriptor.specialAnimationLayers = specialLayers;
                    changed = true;
                }
                if (changed) EditorUtility.SetDirty(descriptor);
            }

            // 2) 汎用走査: 任意コンポーネント(子Animator / MA Merge Animator等)のコントローラー参照
            foreach (Component component in cloneRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is VRCAvatarDescriptor) continue;

                string prefix = component.transform == cloneRoot.transform
                    ? string.Empty
                    : (QuestCompat.GetRelativePath(cloneRoot.transform, component.transform) ?? string.Empty);
                HashSet<string> siteStripPaths = SiteLocalStripPaths(stripPaths, prefix);

                var serializedObject = new SerializedObject(component);
                SerializedProperty property = serializedObject.GetIterator();
                bool modified = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var controller = property.objectReferenceValue as RuntimeAnimatorController;
                    if (controller == null) continue;

                    var wrapped = WrapController(controller, siteStripPaths, wrapCache, animRoot, assets, report, ref strippedClipCount);
                    if (wrapped != null && !ReferenceEquals(wrapped, controller))
                    {
                        property.objectReferenceValue = wrapped;
                        modified = true;
                    }
                }
                if (modified) serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }

            return strippedClipCount;
        }

        /// <summary>CustomAnimLayer配列内のコントローラーを差し替える。変更があれば true(構造体のため呼び出し側で書き戻す)。</summary>
        private static bool WrapLayerArray(VRCAvatarDescriptor.CustomAnimLayer[] layers, HashSet<string> stripPaths,
            Dictionary<string, RuntimeAnimatorController> wrapCache,
            string animRoot, ConversionAssetContext assets, ConversionReport report, ref int strippedClipCount)
        {
            if (layers == null) return false;
            bool changed = false;
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.animatorController == null) continue;

                var wrapped = WrapController(layer.animatorController, stripPaths, wrapCache, animRoot, assets, report, ref strippedClipCount);
                if (wrapped != null && !ReferenceEquals(wrapped, layer.animatorController))
                {
                    layer.animatorController = wrapped;
                    layers[i] = layer; // 構造体のため書き戻し
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// コントローラーに stripPaths 対象の m_IsActive クリップがあれば、対象クリップを複製・除去して
        /// override 指定した AnimatorOverrideController を生成し返す。対象が無ければ元コントローラーをそのまま返す。
        /// ベース(元コントローラー)・元クリップは無改変。同一コントローラーは一度だけ処理(キャッシュ)。
        /// </summary>
        private static RuntimeAnimatorController WrapController(RuntimeAnimatorController controller, HashSet<string> stripPaths,
            Dictionary<string, RuntimeAnimatorController> wrapCache,
            string animRoot, ConversionAssetContext assets, ConversionReport report, ref int strippedClipCount)
        {
            if (controller == null) return null;

            string cacheKey = WrapCacheKey(controller, stripPaths);
            RuntimeAnimatorController cachedWrap;
            if (wrapCache.TryGetValue(cacheKey, out cachedWrap)) return cachedWrap;

            // AnimatorController / AnimatorOverrideController のみ対象(override で有効クリップを差し替えられる)
            if (!(controller is AnimatorController) && !(controller is AnimatorOverrideController))
            {
                wrapCache[cacheKey] = controller;
                return controller;
            }

            // 有効クリップに除去対象があるか事前判定(無ければ何もしない)
            bool hasStrip = false;
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (ClipHasStripBinding(clip, stripPaths)) { hasStrip = true; break; }
            }
            if (!hasStrip)
            {
                wrapCache[cacheKey] = controller;
                return controller;
            }

            var overrideController = new AnimatorOverrideController(controller);
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrideController.GetOverrides(pairs); // Key=有効クリップ / Value=null(未上書き)

            // 同一元クリップは1回だけ複製する(このコントローラー内)
            var clipCache = new Dictionary<AnimationClip, AnimationClip>();
            bool any = false;
            for (int i = 0; i < pairs.Count; i++)
            {
                AnimationClip key = pairs[i].Key;
                if (key == null || !ClipHasStripBinding(key, stripPaths)) continue;

                AnimationClip stripped;
                if (!clipCache.TryGetValue(key, out stripped))
                {
                    stripped = DuplicateAndStripClip(key, stripPaths, animRoot, assets, report);
                    clipCache[key] = stripped;
                    if (stripped != null) strippedClipCount++;
                }
                if (stripped == null) continue;

                pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(key, stripped);
                any = true;
            }

            if (!any)
            {
                UnityEngine.Object.DestroyImmediate(overrideController);
                wrapCache[cacheKey] = controller;
                return controller;
            }

            overrideController.ApplyOverrides(pairs);
            overrideController.name = QuestConverterUtility.SanitizeAssetName(controller.name) + "_Consolidated";

            string path = assets.Claim(animRoot + "/Animations/" + overrideController.name + ".overrideController");
            AnimatorOverrideController saved = SaveOverrideController(overrideController, path);
            wrapCache[cacheKey] = saved;
            report.Info(string.Format(
                "衣装・トグル整理: コントローラー「{0}」のトグルを固定用に差し替えました → {1}", controller.name, path));
            return saved;
        }

        /// <summary>
        /// wrapCache の複合キー。コントローラーのインスタンスIDと、そのサイトで有効な stripPaths を
        /// 正規化(順序非依存)した文字列を連結する。同一コントローラーでも参照サイト(コンポーネント位置)で
        /// strip 対象が異なる場合に別エントリとして扱い、別サイトのラップ結果を誤って再利用しないようにする。
        /// </summary>
        private static string WrapCacheKey(RuntimeAnimatorController controller, HashSet<string> stripPaths)
        {
            var sorted = new List<string>(stripPaths);
            sorted.Sort(StringComparer.Ordinal);
            return controller.GetInstanceID().ToString() + "\n" + string.Join("\n", sorted);
        }

        /// <summary>クリップに stripPaths を対象とする m_IsActive バインディングが含まれるか。</summary>
        private static bool ClipHasStripBinding(AnimationClip clip, HashSet<string> stripPaths)
        {
            if (clip == null) return false;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == typeof(GameObject) && binding.propertyName == ActiveProperty &&
                    stripPaths.Contains(binding.path ?? string.Empty))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// クリップを複製し、stripPaths に一致する m_IsActive バインディングだけを除去して保存する。
        /// 一致が無ければ複製を破棄して null を返す。元クリップ(共有)は変更しない。
        /// 除去は「そのバインディング1本だけ」で、他の対象(別オブジェクトのトグル等)は残す。
        /// なお子孫パスのバインディングは除去しない(ロックしたのはこのパスのみで、子トグルは維持するため)。
        /// </summary>
        private static AnimationClip DuplicateAndStripClip(AnimationClip source, HashSet<string> stripPaths,
            string animRoot, ConversionAssetContext assets, ConversionReport report)
        {
            var copy = new AnimationClip();
            EditorUtility.CopySerialized(source, copy);
            copy.hideFlags = HideFlags.None; // FBX等のサブアセット由来のhideFlagsを解除

            int removed = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(copy))
            {
                if (binding.type != typeof(GameObject) || binding.propertyName != ActiveProperty) continue;
                if (!stripPaths.Contains(binding.path ?? string.Empty)) continue;
                AnimationUtility.SetEditorCurve(copy, binding, null); // その1バインディングだけ除去
                removed++;
            }

            if (removed == 0)
            {
                UnityEngine.Object.DestroyImmediate(copy);
                return null;
            }

            string path = assets.Claim(
                animRoot + "/Animations/" + QuestConverterUtility.SanitizeAssetName(source.name) + "_Consolidated.anim");
            // 実行間で安定したパスへ、既存があればGUIDを保持したまま内容だけ上書きする
            AnimationClip saved = QuestAssetPersistence.SaveOrOverwriteClip(copy, path);
            if (saved != null && !ReferenceEquals(saved, copy) && copy != null && !AssetDatabase.Contains(copy))
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            return saved != null ? saved : copy;
        }

        /// <summary>
        /// AnimatorOverrideController を assetPath へ保存する。既存があれば CopySerialized で内容だけ上書きして
        /// GUIDを保持し(再変換で前回クローンの参照を壊さない)、無ければ新規作成する。
        /// </summary>
        private static AnimatorOverrideController SaveOverrideController(AnimatorOverrideController newController, string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(newController, existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(newController); // 一時インスタンスは破棄(既存側を使う)
                return existing;
            }
            int slash = assetPath.LastIndexOf('/');
            if (slash > 0) QuestConverterUtility.EnsureFolder(assetPath.Substring(0, slash));
            AssetDatabase.CreateAsset(newController, assetPath);
            return newController;
        }

        /// <summary>
        /// アバタールート相対の stripPaths を、prefix(コントローラー所有オブジェクトのルート相対パス)基準の
        /// クリップローカルなパス集合へ変換する。prefix が空(ルート/デスクリプター)ならそのまま返す。
        /// prefix 配下のパスは相対化し、加えて絶対(アバタールート相対)解釈も許容する
        /// (MA Merge Animator の絶対パスモード等に対応。判定が広がる方向のみで、影響はクローン限定)。
        /// </summary>
        private static HashSet<string> SiteLocalStripPaths(HashSet<string> stripPaths, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return stripPaths;

            var result = new HashSet<string>(StringComparer.Ordinal);
            string prefixSlash = prefix + "/";
            foreach (string s in stripPaths)
            {
                if (s == prefix) result.Add(string.Empty);                    // コンポーネント自身のGO
                else if (s.StartsWith(prefixSlash, StringComparison.Ordinal)) result.Add(s.Substring(prefixSlash.Length));
                result.Add(s);                                                // 絶対解釈も許容
            }
            return result;
        }

        /// <summary>
        /// 生成物の出力先を決める。outputDir 指定があればそれを使い、無ければクローン名から
        /// "_Quest" / "_Opt" サフィックスを外して既定の生成ルート配下(オーケストレーターの出力先と同じ規則)を導出する。
        /// </summary>
        private static string ResolveOutputDir(string outputDir, GameObject cloneRoot)
        {
            if (!string.IsNullOrEmpty(outputDir)) return outputDir.Replace('\\', '/').TrimEnd('/');

            string name = cloneRoot != null ? cloneRoot.name : "Avatar";
            if (name.EndsWith("_Quest", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "_Quest".Length);
            else if (name.EndsWith("_Opt", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "_Opt".Length);
            return DefaultGeneratedRoot + "/" + QuestConverterUtility.SanitizeAssetName(name);
        }
    }
}
#endif
