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

        /// <summary>[1.10.0] material.* アニメーションのプロパティ名接頭辞(AAOの material-animation-differently 警告と同じ)。</summary>
        private const string MaterialPropertyPrefix = "material.";

        // ================================================================
        // バインディング除去の共有機構(m_IsActive固定 / material.*無効化で共用)
        // ================================================================

        /// <summary>
        /// [1.10.0] クリップからバインディングを除去する作法(何を消すか・生成物の命名)をまとめた文脈。
        /// m_IsActive除去(トグル固定)と material.*除去(マテリアルアニメ無効化して統合)が、
        /// 同じ「クローンのコントローラー/クリップだけを複製して差し替える」機構(<see cref="WrapController"/> /
        /// <see cref="DuplicateAndStripClip"/>)を共有するためのパラメータ束。元アバターと共有するアセットは無改変。
        /// </summary>
        private sealed class BindingStripContext
        {
            /// <summary>除去対象のバインディング判定。(binding, そのサイトで有効な対象パス集合)→ 除去するなら true。</summary>
            public Func<EditorCurveBinding, HashSet<string>, bool> Match;

            /// <summary>生成する override / クリップに付ける名前サフィックス(例 "_Consolidated" / "_MatAnimDisabled")。</summary>
            public string NameSuffix;

            /// <summary>レポートの接頭ラベル(例 "衣装・トグル整理" / "マテリアルアニメ無効化")。</summary>
            public string Label;
        }

        /// <summary>m_IsActive(GameObjectのアクティブ)除去の文脈(衣装・トグル固定)。従来の "_Consolidated" 命名を維持する。</summary>
        private static readonly BindingStripContext ToggleStripContext = new BindingStripContext
        {
            Match = (binding, paths) => binding.type == typeof(GameObject) && binding.propertyName == ActiveProperty &&
                                        paths.Contains(binding.path ?? string.Empty),
            NameSuffix = "_Consolidated",
            Label = "衣装・トグル整理",
        };

        /// <summary>[1.10.0] material.*(Rendererのマテリアルプロパティ)除去の文脈(マテリアルアニメ無効化して統合)。</summary>
        private static readonly BindingStripContext MaterialAnimStripContext = new BindingStripContext
        {
            Match = MatchMaterialAnimBinding,
            NameSuffix = "_MatAnimDisabled",
            Label = "マテリアルアニメ無効化",
        };

        /// <summary>[1.10.0] binding が Renderer/SkinnedMeshRenderer 向けの material.* フロートで、対象パスに向いているか。</summary>
        private static bool MatchMaterialAnimBinding(EditorCurveBinding binding, HashSet<string> paths)
        {
            if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type)) return false;
            if (binding.propertyName == null ||
                !binding.propertyName.StartsWith(MaterialPropertyPrefix, StringComparison.Ordinal)) return false;
            return paths.Contains(binding.path ?? string.Empty);
        }

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
            var notFoundPaths = new List<string>(); // このアバターに存在しない指定(1件ずつ警告せず後で集約する)
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
                    // 1件ずつ警告するとログが溢れるため、集約して後で1つにまとめる(下の集約警告)。
                    notFound++;
                    notFoundPaths.Add(choice.groupId);
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

            // 未検出パスは1件ずつではなく1つに集約して警告する(別アバターの設定が保存済みJSONへ混入していた
            // 場合に、大量の「見つからない」警告でログが溢れるのを防ぐ。エントリ自体は設定に残す=保持)。
            if (notFoundPaths.Count > 0)
            {
                const int nameCap = 5; // 先頭数件だけ名前を出し、残りは件数に畳む
                int shownNames = Mathf.Min(nameCap, notFoundPaths.Count);
                string names = string.Join(", ", notFoundPaths.GetRange(0, shownNames));
                if (notFoundPaths.Count > nameCap) names += string.Format(" ...他 {0} 件", notFoundPaths.Count - nameCap);
                report.Warn(string.Format(
                    "衣装・トグル整理: 保存済み設定のうち {0} 件のパスがこのアバターに存在しないためスキップしました" +
                    "(別アバターの設定が混入していた場合は自動で無視されます): {1}",
                    notFoundPaths.Count, names));
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
                strippedClipCount = StripBindings(cloneRoot, stripPaths, ToggleStripContext, animRoot, assetContext, report);
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

        // ================================================================
        // [1.10.0] マテリアルアニメーションの無効化(統合できるようにする / クローンのみ編集)
        // ================================================================

        /// <summary>
        /// rendererPaths(ユーザーが「マテリアルアニメーションを無効化して統合」を選んだレンダラーのアバタールート相対パス)
        /// について、クローン cloneRoot 側のアニメーションクリップから、そのレンダラーに向いた material.* フロートカーブを
        /// すべて除去する(=切り替え演出は動かなくなるが、SkinnedMesh統合の波及ガードに引っかからず統合できるようになる)。
        ///
        /// 【安全性】ToggleConsolidator の m_IsActive 除去と同一の機構(<see cref="StripBindings"/>:対象クリップを複製し
        /// クローンのコントローラーを AnimatorOverrideController で差し替える)を共有する。元アバターと共有するクリップ・
        /// コントローラーは一切書き換えない。走査範囲はデスクリプターのアニメーションレイヤー + 子コンポーネント(子Animator /
        /// MA Merge Animator 等)のコントローラー参照で、SkinnedMeshMergePlanner の波及ガードが見る範囲と同じ。
        ///
        /// 【順序】SkinnedMeshMergePlanner.BuildPlan(波及ガードのアニメ走査)より前に呼ぶこと。除去後は該当レンダラーの
        /// material.* アニメ集合が空になるため、ガードが再評価して統合を許可する(BuildPlan に materialAnimDisablePaths を
        /// 渡さなくても、実際に空になったクリップを見て統合される)。
        ///
        /// optOutPaths(統合から個別除外したパス。null可)に含まれるレンダラーは、分離維持したい意図のため無効化しない。
        /// 戻り値: material.* を除去したレンダラー数(0=無し)。例外は投げない(失敗は Warn で報告し統合処理を止めない)。
        /// </summary>
        public static int NeutralizeMaterialAnimations(GameObject cloneRoot, List<string> rendererPaths, List<string> optOutPaths,
            string outputDir = null, ConversionAssetContext assets = null, ConversionReport report = null)
        {
            if (report == null) report = new ConversionReport(); // 呼び出し側の渡し忘れ対策(結果は破棄される)
            if (cloneRoot == null) return 0;
            if (rendererPaths == null || rendererPaths.Count == 0) return 0;

            // 対象パス集合(空/opt-out は除く)。opt-out は「分離維持したい」意図のため無効化しない。
            var optOut = optOutPaths != null ? new HashSet<string>(optOutPaths, StringComparer.Ordinal) : null;
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (string p in rendererPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (optOut != null && optOut.Contains(p)) continue;
                targets.Add(p);
            }
            if (targets.Count == 0) return 0;

            // レポート用: 各対象パスの material.* プロパティ名・クリップ名を読み取り専用で採取する(除去前に採取)。
            Dictionary<string, MaterialAnimInfo> infoByPath = CollectMaterialAnimInfo(cloneRoot, targets);

            string animRoot = ResolveOutputDir(outputDir, cloneRoot);
            var assetContext = assets ?? new ConversionAssetContext();

            int strippedClipCount = StripBindings(cloneRoot, targets, MaterialAnimStripContext, animRoot, assetContext, report);
            if (strippedClipCount > 0) AssetDatabase.SaveAssets();

            // [C] レンダラーごとに Warn で報告する(material.* アニメが実際に見つかったものだけ)。
            int neutralized = 0;
            foreach (string path in targets)
            {
                if (!infoByPath.TryGetValue(path, out MaterialAnimInfo info) || info == null || info.Props.Count == 0) continue;
                neutralized++;
                string rname = ResolveRendererName(cloneRoot, path);
                report.Warn(string.Format(
                    "'{0}': マテリアルアニメーションを無効化して統合しました(無効化: {1} / クリップ: {2})。該当の切り替え演出は動かなくなります。",
                    rname, FormatPropSummary(info.Props), FormatClipSummary(info.Clips)));
            }
            if (neutralized == 0 && strippedClipCount == 0)
            {
                // 対象はあるが material.* アニメが見つからなかった(既に無いか、走査対象外)。統合には影響しない。
                report.Info("マテリアルアニメ無効化: 対象レンダラーに無効化すべき material.* アニメーションは見つかりませんでした。");
            }
            else if (neutralized == 0 && strippedClipCount > 0)
            {
                // 情報収集(CollectMaterialAnimInfo)が例外で空振りしたが、除去自体は行われた防御的経路。
                // 無効化が無音になるのを防ぐため、詳細なしでも必ず報告する。
                report.Warn(string.Format(
                    "マテリアルアニメ無効化: {0} 個のクリップから material.* アニメーションを除去しました(詳細の収集に失敗したためプロパティ一覧は表示できません)。該当の切り替え演出は動かなくなります。",
                    strippedClipCount));
            }
            return neutralized;
        }

        /// <summary>[1.10.0] 1レンダラーの material.* アニメーション情報(レポート用)。</summary>
        private sealed class MaterialAnimInfo
        {
            /// <summary>material. を除いた(チャンネル接尾を畳んだ)プロパティ名(重複なし・安定順)。</summary>
            public readonly SortedSet<string> Props = new SortedSet<string>(StringComparer.Ordinal);

            /// <summary>そのプロパティを動かしているクリップ名(重複なし・安定順)。</summary>
            public readonly SortedSet<string> Clips = new SortedSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// cloneRoot から到達可能な全クリップを走査し、targets のレンダラーに向いた material.* フロートについて
        /// プロパティ名・クリップ名を収集する(読み取り専用)。走査範囲は SkinnedMeshMergePlanner の波及ガードと同じ
        /// (ルート相対のコントローラー + 子コンポーネント参照のパス前置)。走査失敗時は空辞書(レポートが省略されるだけ)。
        /// </summary>
        private static Dictionary<string, MaterialAnimInfo> CollectMaterialAnimInfo(GameObject cloneRoot, HashSet<string> targets)
        {
            var byPath = new Dictionary<string, MaterialAnimInfo>(StringComparer.Ordinal);
            if (cloneRoot == null || targets == null || targets.Count == 0) return byPath;
            try
            {
                var seenClips = new HashSet<AnimationClip>();
                foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(cloneRoot))
                {
                    if (controller == null) continue;
                    foreach (AnimationClip clip in controller.animationClips)
                    {
                        if (clip == null || !seenClips.Add(clip)) continue;
                        AddMaterialAnimInfoFromClip(clip, string.Empty, targets, byPath);
                    }
                }

                var seenPrefixed = new HashSet<string>();
                foreach (Component component in cloneRoot.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    if (component.transform == cloneRoot.transform) continue;
                    string prefix = QuestCompat.GetRelativePath(cloneRoot.transform, component.transform);
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
                            AddMaterialAnimInfoFromClip(clip, prefix, targets, byPath);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // レポート用の付随情報のため、失敗しても致命的ではない(統合は StripBindings 側で行われる)。
            }
            return byPath;
        }

        /// <summary>クリップ内の Renderer 向け material.* フロートのうち、targets に一致するパスの情報(プロパティ名・クリップ名)を集約する。</summary>
        private static void AddMaterialAnimInfoFromClip(AnimationClip clip, string prefix, HashSet<string> targets, Dictionary<string, MaterialAnimInfo> byPath)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type)) continue;
                if (binding.propertyName == null ||
                    !binding.propertyName.StartsWith(MaterialPropertyPrefix, StringComparison.Ordinal)) continue;
                string prop = binding.propertyName.Substring(MaterialPropertyPrefix.Length);
                if (prop.Length == 0) continue;

                string path = binding.path ?? string.Empty;
                string full;
                if (prefix.Length == 0) full = path;
                else full = path.Length == 0 ? prefix : prefix + "/" + path;
                if (!targets.Contains(full)) continue;

                if (!byPath.TryGetValue(full, out MaterialAnimInfo info))
                {
                    info = new MaterialAnimInfo();
                    byPath[full] = info;
                }
                info.Props.Add(NormalizeMaterialPropName(prop));
                if (clip != null && !string.IsNullOrEmpty(clip.name)) info.Clips.Add(clip.name);
            }
        }

        /// <summary>material.* プロパティ名のチャンネル接尾(".r"/".g"/".b"/".a"/".x"/".y"/".z"/".w")を落として集約表示しやすくする。</summary>
        private static string NormalizeMaterialPropName(string prop)
        {
            if (string.IsNullOrEmpty(prop) || prop.Length < 2) return prop;
            if (prop[prop.Length - 2] == '.')
            {
                char c = char.ToLowerInvariant(prop[prop.Length - 1]);
                if (c == 'r' || c == 'g' || c == 'b' || c == 'a' || c == 'x' || c == 'y' || c == 'z' || c == 'w')
                    return prop.Substring(0, prop.Length - 2);
            }
            return prop;
        }

        /// <summary>"_UseEmission ほか 2件" のように、先頭1件 + 残数で要約する。</summary>
        private static string FormatPropSummary(SortedSet<string> props)
        {
            if (props == null || props.Count == 0) return "(なし)";
            string first = null;
            foreach (string p in props) { first = p; break; } // 安定順の先頭
            return props.Count == 1 ? first : string.Format("{0} ほか {1}件", first, props.Count - 1);
        }

        /// <summary>クリップ名を最大3件まで列挙し、超過分は件数に畳む。</summary>
        private static string FormatClipSummary(SortedSet<string> clips)
        {
            if (clips == null || clips.Count == 0) return "(なし)";
            const int cap = 3;
            var shown = new List<string>();
            foreach (string c in clips)
            {
                shown.Add(c);
                if (shown.Count >= cap) break;
            }
            string joined = string.Join(", ", shown);
            if (clips.Count > cap) joined += string.Format(" ...他 {0}件", clips.Count - cap);
            return joined;
        }

        /// <summary>path のレンダラー名(GameObject名)を返す。解決できなければパスの末尾要素、それも無ければパスそのもの。</summary>
        private static string ResolveRendererName(GameObject cloneRoot, string path)
        {
            Transform t = cloneRoot != null ? QuestCompat.FindByPath(cloneRoot.transform, path) : null;
            if (t != null) return t.gameObject.name;
            int slash = path != null ? path.LastIndexOf('/') : -1;
            if (slash >= 0 && slash + 1 < path.Length) return path.Substring(slash + 1);
            return path ?? "?";
        }

        // ----------------------------------------------------------------
        // バインディング除去の共有機構(m_IsActive固定 / material.*無効化。共有アセットを壊さない override 方式)
        // ----------------------------------------------------------------

        /// <summary>
        /// クローンから到達可能なコントローラーのうち、ctx.Match が示すバインディング(stripPaths を対象とする)を
        /// 含むものを、クローン専用の AnimatorOverrideController(複製・除去済みクリップで差し替え)へ置き換える。
        /// ベースコントローラー・元クリップ(元PCアバターと共有)は一切変更しない。除去したクリップ数を返す。
        /// [1.10.0] ctx により m_IsActive除去(トグル固定)と material.*除去(マテリアルアニメ無効化)で共用する
        /// (旧名 StripActiveBindings を一般化。走査範囲=デスクリプターのレイヤー + 子コンポーネント参照は共通)。
        /// </summary>
        private static int StripBindings(GameObject cloneRoot, HashSet<string> stripPaths, BindingStripContext ctx,
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
                if (WrapLayerArray(baseLayers, stripPaths, ctx, wrapCache, animRoot, assets, report, ref strippedClipCount))
                {
                    descriptor.baseAnimationLayers = baseLayers;
                    changed = true;
                }
                var specialLayers = descriptor.specialAnimationLayers;
                if (WrapLayerArray(specialLayers, stripPaths, ctx, wrapCache, animRoot, assets, report, ref strippedClipCount))
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

                    var wrapped = WrapController(controller, siteStripPaths, ctx, wrapCache, animRoot, assets, report, ref strippedClipCount);
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
        private static bool WrapLayerArray(VRCAvatarDescriptor.CustomAnimLayer[] layers, HashSet<string> stripPaths, BindingStripContext ctx,
            Dictionary<string, RuntimeAnimatorController> wrapCache,
            string animRoot, ConversionAssetContext assets, ConversionReport report, ref int strippedClipCount)
        {
            if (layers == null) return false;
            bool changed = false;
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.animatorController == null) continue;

                var wrapped = WrapController(layer.animatorController, stripPaths, ctx, wrapCache, animRoot, assets, report, ref strippedClipCount);
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
        private static RuntimeAnimatorController WrapController(RuntimeAnimatorController controller, HashSet<string> stripPaths, BindingStripContext ctx,
            Dictionary<string, RuntimeAnimatorController> wrapCache,
            string animRoot, ConversionAssetContext assets, ConversionReport report, ref int strippedClipCount)
        {
            if (controller == null) return null;

            string cacheKey = WrapCacheKey(controller, stripPaths, ctx);
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
                if (ClipHasStripBinding(clip, stripPaths, ctx)) { hasStrip = true; break; }
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
                if (key == null || !ClipHasStripBinding(key, stripPaths, ctx)) continue;

                AnimationClip stripped;
                if (!clipCache.TryGetValue(key, out stripped))
                {
                    stripped = DuplicateAndStripClip(key, stripPaths, ctx, animRoot, assets, report);
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
            overrideController.name = QuestConverterUtility.SanitizeAssetName(controller.name) + ctx.NameSuffix;

            string path = assets.Claim(animRoot + "/Animations/" + overrideController.name + ".overrideController");
            AnimatorOverrideController saved = SaveOverrideController(overrideController, path);
            wrapCache[cacheKey] = saved;
            report.Info(string.Format(
                "{0}: コントローラー「{1}」のクリップを複製・差し替えました → {2}", ctx.Label, controller.name, path));
            return saved;
        }

        /// <summary>
        /// wrapCache の複合キー。コントローラーのインスタンスIDと、そのサイトで有効な stripPaths を
        /// 正規化(順序非依存)した文字列を連結する。同一コントローラーでも参照サイト(コンポーネント位置)で
        /// strip 対象が異なる場合に別エントリとして扱い、別サイトのラップ結果を誤って再利用しないようにする。
        /// </summary>
        private static string WrapCacheKey(RuntimeAnimatorController controller, HashSet<string> stripPaths, BindingStripContext ctx)
        {
            var sorted = new List<string>(stripPaths);
            sorted.Sort(StringComparer.Ordinal);
            // ctx.NameSuffix を混ぜ、同一コントローラーを m_IsActive除去 と material.*除去 で別エントリにする(命名も異なる)。
            return controller.GetInstanceID().ToString() + "\n" + ctx.NameSuffix + "\n" + string.Join("\n", sorted);
        }

        /// <summary>クリップに ctx.Match が示す(stripPaths を対象とする)バインディングが含まれるか。</summary>
        private static bool ClipHasStripBinding(AnimationClip clip, HashSet<string> stripPaths, BindingStripContext ctx)
        {
            if (clip == null) return false;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (ctx.Match(binding, stripPaths)) return true;
            }
            return false;
        }

        /// <summary>
        /// クリップを複製し、ctx.Match に一致する(stripPaths を対象とする)バインディングだけを除去して保存する。
        /// 一致が無ければ複製を破棄して null を返す。元クリップ(共有)は変更しない。
        /// 除去は一致したバインディング本数分で、他の対象(別オブジェクト・別プロパティ等)は残す。
        /// m_IsActive除去では対象パスの子孫は除去しない(ロックしたのはこのパスのみ)。material.*除去では対象パスに向いた
        /// material.* フロートのみを除去する(他レンダラーのアニメは残す)。
        /// </summary>
        private static AnimationClip DuplicateAndStripClip(AnimationClip source, HashSet<string> stripPaths, BindingStripContext ctx,
            string animRoot, ConversionAssetContext assets, ConversionReport report)
        {
            var copy = new AnimationClip();
            EditorUtility.CopySerialized(source, copy);
            copy.hideFlags = HideFlags.None; // FBX等のサブアセット由来のhideFlagsを解除

            int removed = 0;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(copy))
            {
                if (!ctx.Match(binding, stripPaths)) continue;
                AnimationUtility.SetEditorCurve(copy, binding, null); // 一致したバインディングだけ除去
                removed++;
            }

            if (removed == 0)
            {
                UnityEngine.Object.DestroyImmediate(copy);
                return null;
            }

            string path = assets.Claim(
                animRoot + "/Animations/" + QuestConverterUtility.SanitizeAssetName(source.name) + ctx.NameSuffix + ".anim");
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
