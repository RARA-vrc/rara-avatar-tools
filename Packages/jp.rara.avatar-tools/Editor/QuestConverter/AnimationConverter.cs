// RARA Quest Converter - アニメーション変換モジュール
// FX等のアニメーション(Material参照のObjectReferenceカーブ)がPC用マテリアルへ差し替えている場合、
// 変換後(Quest用)マテリアルを参照する複製アセットを生成して差し替える。
// VRChat Avatars SDK 3.10.4 / Unity 2022.3 向け。Built-in Render Pipeline / Editor専用。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>
    /// アバターのアニメーション(クリップ・コントローラー)内のマテリアル参照を
    /// 変換後マテリアルへ差し替えた複製に置き換えるモジュール。
    /// </summary>
    public static class AnimationConverter
    {
        /// <summary>1回の変換実行で共有するキャッシュとカウンタ。</summary>
        private class Context
        {
            public Dictionary<Material, Material> materialMap;
            /// <summary>パーティクル系レンダラー(ParticleSystemRenderer等)専用の変換結果。未指定なら空。</summary>
            public Dictionary<Material, Material> particleMaterialMap;
            public string outputDir;
            public ConversionReport report;
            /// <summary>生成アセットの安定パス払い出し・書き込み記録用コンテキスト(常に非null)。</summary>
            public ConversionAssetContext assets;

            /// <summary>元クリップ → 変換後クリップ(変換不要ならnull)。同じクリップを二重複製しない。</summary>
            public readonly Dictionary<AnimationClip, AnimationClip> clipCache =
                new Dictionary<AnimationClip, AnimationClip>();

            /// <summary>元コントローラー → 変換後コントローラー(変換不要なら元と同一)。同じコントローラーを二重複製しない。</summary>
            public readonly Dictionary<RuntimeAnimatorController, RuntimeAnimatorController> controllerCache =
                new Dictionary<RuntimeAnimatorController, RuntimeAnimatorController>();

            public int convertedControllerCount;
            public int convertedClipCount;
        }

        /// <summary>
        /// Questクローン上の全アニメーターコントローラー参照(VRCAvatarDescriptorのレイヤー、
        /// Animator、Modular AvatarのMergeAnimator等の任意コンポーネント)を走査し、
        /// マテリアル差し替えアニメーションを含むものを変換後マテリアル参照の複製へ差し替える。
        /// assets は生成アセットの安定パス払い出し・書き込み記録用コンテキスト
        /// (オーケストレーターが1変換につき1つ渡す。null なら単体呼び出し用に内部生成する)。
        /// </summary>
        public static void ConvertAvatarAnimations(GameObject questRoot, Dictionary<Material, Material> materialMap, string outputDir, ConversionReport report, Dictionary<Material, Material> particleMaterialMap = null, ConversionAssetContext assets = null)
        {
            if (report == null) report = new ConversionReport();
            if (questRoot == null)
            {
                report.Error("アニメーション変換: 対象のアバター(questRoot)がnullです。");
                return;
            }
            if (materialMap == null || materialMap.Count == 0)
            {
                report.Info("アニメーション変換: マテリアル対応表が空のためスキップしました。");
                return;
            }

            var ctx = new Context
            {
                materialMap = materialMap,
                particleMaterialMap = particleMaterialMap ?? new Dictionary<Material, Material>(),
                outputDir = outputDir,
                report = report,
                assets = assets ?? new ConversionAssetContext(),
            };

            // 1) VRCAvatarDescriptor のアニメーションレイヤー(Base / Special)
            var descriptor = questRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                bool descriptorChanged = false;

                // CustomAnimLayer は構造体のため、要素を書き換えてから配列を再代入する
                var baseLayers = descriptor.baseAnimationLayers;
                if (ConvertLayerArray(baseLayers, ctx))
                {
                    descriptor.baseAnimationLayers = baseLayers;
                    descriptorChanged = true;
                }

                var specialLayers = descriptor.specialAnimationLayers;
                if (ConvertLayerArray(specialLayers, ctx))
                {
                    descriptor.specialAnimationLayers = specialLayers;
                    descriptorChanged = true;
                }

                if (descriptorChanged)
                {
                    EditorUtility.SetDirty(descriptor);
                }
            }
            else
            {
                report.Warn("アニメーション変換: VRCAvatarDescriptor が見つかりませんでした。汎用走査のみ実行します。");
            }

            // 2) 汎用走査: 全コンポーネントの ObjectReference プロパティから
            //    RuntimeAnimatorController 参照を探して差し替える。
            //    (Animator や Modular Avatar の MergeAnimator 等をコンパイル時依存なしでカバーする)
            foreach (var component in questRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;                    // Missing Script 等
                if (component is Transform) continue;               // コントローラー参照を持たないため省略
                if (component is VRCAvatarDescriptor) continue;     // 手順1で処理済み(二重処理防止)
                SweepComponentControllerReferences(component, ctx);
            }

            if (ctx.convertedControllerCount > 0 || ctx.convertedClipCount > 0)
            {
                AssetDatabase.SaveAssets();
                report.Info(string.Format(
                    "アニメーション変換完了: 複製したコントローラー {0} 件 / クリップ {1} 件。",
                    ctx.convertedControllerCount, ctx.convertedClipCount));
            }
            else
            {
                report.Info("アニメーション変換: マテリアル差し替えアニメーションは見つかりませんでした(変換不要)。");
            }
        }

        /// <summary>
        /// クリップ内のObjectReferenceカーブが対応表のマテリアルを参照している場合、
        /// 変換後マテリアルを参照する複製を {outputDir}/Animations/ に保存して返す。
        /// 変換不要なら null を返す(複製しない)。
        /// </summary>
        public static AnimationClip ConvertClip(AnimationClip clip, Dictionary<Material, Material> materialMap, string outputDir, ConversionReport report)
        {
            if (report == null) report = new ConversionReport();
            var ctx = new Context
            {
                materialMap = materialMap,
                particleMaterialMap = new Dictionary<Material, Material>(),
                outputDir = outputDir,
                report = report,
                assets = new ConversionAssetContext(),
            };
            return ConvertClipCached(clip, ctx);
        }

        // ---------------------------------------------------------------
        // コントローラー収集(共有ヘルパー)
        // ---------------------------------------------------------------

        /// <summary>
        /// root配下から到達可能な全RuntimeAnimatorControllerを重複なく収集する。
        /// VRCAvatarDescriptorのアニメーションレイヤーに加え、全コンポーネントのシリアライズ済み
        /// ObjectReferenceプロパティを走査するため、AnimatorやModular AvatarのMergeAnimator等の
        /// 任意コンポーネントが参照するコントローラーも含む
        /// (ConvertAvatarAnimations と同じ走査範囲。マテリアル収集との不整合を防ぐための共通化)。
        /// </summary>
        public static List<RuntimeAnimatorController> CollectControllers(GameObject root)
        {
            var controllers = new List<RuntimeAnimatorController>();
            if (root == null) return controllers;

            var descriptor = root.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                AddLayerControllers(descriptor.baseAnimationLayers, controllers);
                AddLayerControllers(descriptor.specialAnimationLayers, controllers);
            }

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;                    // Missing Script 等
                if (component is Transform) continue;               // コントローラー参照を持たないため省略
                if (component is VRCAvatarDescriptor) continue;     // レイヤーで処理済み

                var serializedObject = new SerializedObject(component);
                var property = serializedObject.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var controller = property.objectReferenceValue as RuntimeAnimatorController;
                    if (controller != null && !controllers.Contains(controller))
                    {
                        controllers.Add(controller);
                    }
                }
            }
            return controllers;
        }

        private static void AddLayerControllers(VRCAvatarDescriptor.CustomAnimLayer[] layers, List<RuntimeAnimatorController> controllers)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer.animatorController != null && !controllers.Contains(layer.animatorController))
                {
                    controllers.Add(layer.animatorController);
                }
            }
        }

        // ---------------------------------------------------------------
        // レイヤー配列
        // ---------------------------------------------------------------

        /// <summary>CustomAnimLayer配列内のコントローラーを変換して差し替える。変更があればtrue。</summary>
        private static bool ConvertLayerArray(VRCAvatarDescriptor.CustomAnimLayer[] layers, Context ctx)
        {
            if (layers == null) return false;
            bool changed = false;
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.animatorController == null) continue;

                var converted = ConvertController(layer.animatorController, ctx);
                if (converted != null && !ReferenceEquals(converted, layer.animatorController))
                {
                    ctx.report.Info(string.Format(
                        "アニメーションレイヤー ({0}) のコントローラーを差し替えました: {1}",
                        layer.type, layer.animatorController.name));
                    layer.animatorController = converted;
                    layers[i] = layer; // 構造体のため書き戻し
                    changed = true;
                }
            }
            return changed;
        }

        // ---------------------------------------------------------------
        // 汎用コンポーネント走査
        // ---------------------------------------------------------------

        /// <summary>
        /// コンポーネントのシリアライズ済みプロパティを全深度で走査し、
        /// RuntimeAnimatorController 参照を変換後のものへ差し替える。
        /// </summary>
        private static void SweepComponentControllerReferences(Component component, Context ctx)
        {
            var serializedObject = new SerializedObject(component);
            var property = serializedObject.GetIterator();
            bool modified = false;

            // Next(true) で子プロパティを含む全プロパティを訪問する
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

                var controller = property.objectReferenceValue as RuntimeAnimatorController;
                if (controller == null) continue;

                var converted = ConvertController(controller, ctx);
                if (converted != null && !ReferenceEquals(converted, controller))
                {
                    property.objectReferenceValue = converted;
                    modified = true;
                    ctx.report.Info(string.Format(
                        "{0} ({1}) のコントローラー参照を差し替えました: {2}",
                        component.GetType().Name, component.gameObject.name, controller.name));
                }
            }

            if (modified)
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ---------------------------------------------------------------
        // コントローラー変換
        // ---------------------------------------------------------------

        /// <summary>
        /// コントローラーを必要に応じて複製・変換して返す。変換不要なら元をそのまま返す。
        /// 同一コントローラーは一度だけ変換される(キャッシュ)。
        /// </summary>
        private static RuntimeAnimatorController ConvertController(RuntimeAnimatorController controller, Context ctx)
        {
            if (controller == null) return null;

            RuntimeAnimatorController cached;
            if (ctx.controllerCache.TryGetValue(controller, out cached)) return cached;

            // 再帰ガード(処理中は元を返す)
            ctx.controllerCache[controller] = controller;

            RuntimeAnimatorController result = controller;
            var animatorController = controller as AnimatorController;
            if (animatorController != null)
            {
                result = ConvertAnimatorController(animatorController, ctx);
            }
            else
            {
                var overrideController = controller as AnimatorOverrideController;
                if (overrideController != null)
                {
                    result = ConvertOverrideController(overrideController, ctx);
                }
                // その他の RuntimeAnimatorController 派生は対象外(そのまま)
            }

            ctx.controllerCache[controller] = result;
            return result;
        }

        /// <summary>AnimatorController: 参照クリップに変換対象があればアセットごと複製し、複製内のクリップ参照を差し替える。</summary>
        private static RuntimeAnimatorController ConvertAnimatorController(AnimatorController controller, Context ctx)
        {
            // 到達可能な全クリップ(BlendTree内含む)に変換対象があるか判定
            bool needsConversion = false;
            foreach (var clip in controller.animationClips)
            {
                if (ClipNeedsConversion(clip, ctx.materialMap))
                {
                    needsConversion = true;
                    break;
                }
            }
            if (!needsConversion) return controller;

            var sourcePath = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(sourcePath))
            {
                // アセットとして保存されていないコントローラーは CopyAsset で複製できない。
                // (通常のアバターでは発生しない想定。発生した場合は元のまま残して警告する)
                ctx.report.Warn(string.Format(
                    "コントローラー「{0}」はアセットとして保存されていないため複製できませんでした。マテリアル差し替えアニメーションが変換されていない可能性があります。",
                    controller.name));
                return controller;
            }
            if (AssetDatabase.IsSubAsset(controller))
            {
                // サブアセットのコントローラーに CopyAsset を使うと親アセット全体が
                // .controller として複製され、壊れたアセットが残るだけで変換もされない。
                // 単体の複製手段が無いため、元のまま残して正確に警告する。
                ctx.report.Warn(string.Format(
                    "コントローラー「{0}」は他のアセット({1})のサブアセットのため複製できませんでした。マテリアル差し替えアニメーションが変換されていない可能性があります。コントローラーを単体の .controller アセットとして保存し直してから再変換してください。",
                    controller.name, sourcePath));
                return controller;
            }

            var folder = EnsureAnimationsFolder(ctx);
            var targetPath = ctx.assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(controller.name) + "_Quest.controller");

            // AnimatorController はステート・遷移等のサブアセット一式を持つため CopyAsset でしか複製できず、
            // CopyAsset は GUID を保持できない(既存パスへの CopyAsset は失敗する)。そこで安定パスに
            // 前回の複製が残っている場合は「そのファイルだけ」を削除してから複製し直す。
            // 【なぜ安全か】この複製コントローラーは今回生成するクローンの内部専用アセットで、
            // 変換フローの外から参照されることはない。唯一の例外はシーンに残った前回の _Quest クローンで、
            // この削除によりそのクローンのアニメーター参照のみ失われる(GUID を保持して上書きされる
            // マテリアル・テクスチャ等の参照は保たれる)。フォルダ全削除で全参照が壊れていた
            // 従来動作に対する、文書化されたトレードオフ。
            if (AssetDatabase.LoadMainAssetAtPath(targetPath) != null)
            {
                if (AssetDatabase.DeleteAsset(targetPath))
                {
                    ctx.report.Info(string.Format(
                        "前回生成のコントローラー複製を作り直します: {0}(シーンに前回の _Quest クローンが残っている場合、そのクローンのアニメーター参照のみ失われます。マテリアル等の参照は維持されます)",
                        targetPath));
                }
                else
                {
                    // 削除できない場合は連番付きパスへ退避して変換を継続する(書き込み記録のためClaimを通す)
                    targetPath = ctx.assets.Claim(QuestConverterUtility.UniqueAssetPath(targetPath));
                    ctx.report.Warn(string.Format(
                        "既存のコントローラー複製を削除できなかったため、連番付きパスへ複製します: {0}(不要になったら手動で削除してください)",
                        targetPath));
                }
            }

            if (!AssetDatabase.CopyAsset(sourcePath, targetPath))
            {
                ctx.report.Warn(string.Format("コントローラー「{0}」の複製に失敗しました: {1}", controller.name, sourcePath));
                return controller;
            }

            var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(targetPath);
            if (copy == null)
            {
                ctx.report.Warn(string.Format("複製したコントローラーの読み込みに失敗しました: {0}", targetPath));
                return controller;
            }

            // 複製内の全ステートマシンを再帰的に走査してクリップ参照を差し替える
            var visitedStateMachines = new HashSet<AnimatorStateMachine>();
            var visitedBlendTrees = new HashSet<BlendTree>();
            var externalTreeClones = new Dictionary<BlendTree, BlendTree>(); // 外部BlendTree → 複製内クローン
            foreach (var layer in copy.layers)
            {
                ConvertStateMachine(layer.stateMachine, targetPath, visitedStateMachines, visitedBlendTrees, externalTreeClones, ctx);
            }

            EditorUtility.SetDirty(copy);
            ctx.convertedControllerCount++;
            ctx.report.Info(string.Format("コントローラーを複製しました: {0} → {1}", controller.name, targetPath));
            return copy;
        }

        /// <summary>ステートマシンを再帰走査し、各ステートのMotion(クリップ/BlendTree)を変換する。</summary>
        private static void ConvertStateMachine(AnimatorStateMachine stateMachine, string controllerPath,
            HashSet<AnimatorStateMachine> visitedStateMachines, HashSet<BlendTree> visitedBlendTrees,
            Dictionary<BlendTree, BlendTree> externalTreeClones, Context ctx)
        {
            if (stateMachine == null || !visitedStateMachines.Add(stateMachine)) return;

            foreach (var childState in stateMachine.states)
            {
                var state = childState.state;
                if (state == null) continue;

                var clip = state.motion as AnimationClip;
                if (clip != null)
                {
                    var converted = ConvertClipCached(clip, ctx);
                    if (converted != null)
                    {
                        state.motion = converted;
                        EditorUtility.SetDirty(state);
                    }
                    continue;
                }

                var blendTree = state.motion as BlendTree;
                if (blendTree != null)
                {
                    // 外部(共有)BlendTreeアセットに変換対象クリップが含まれる場合は、
                    // 共有アセットを書き換えずに複製コントローラー内へディープクローンして差し替える
                    var replacement = CloneExternalBlendTreeIfNeeded(blendTree, controllerPath, externalTreeClones, ctx);
                    if (!ReferenceEquals(replacement, blendTree))
                    {
                        state.motion = replacement;
                        EditorUtility.SetDirty(state);
                        blendTree = replacement;
                    }
                    ConvertBlendTree(blendTree, controllerPath, visitedBlendTrees, externalTreeClones, ctx);
                }
            }

            foreach (var childStateMachine in stateMachine.stateMachines)
            {
                ConvertStateMachine(childStateMachine.stateMachine, controllerPath, visitedStateMachines, visitedBlendTrees, externalTreeClones, ctx);
            }
        }

        /// <summary>Motion(クリップまたはBlendTreeツリー)に変換対象マテリアルを参照するクリップが含まれるか。</summary>
        private static bool MotionNeedsConversion(Motion motion, HashSet<BlendTree> visited, Context ctx)
        {
            var clip = motion as AnimationClip;
            if (clip != null) return ClipNeedsConversion(clip, ctx.materialMap);

            var tree = motion as BlendTree;
            if (tree == null || !visited.Add(tree)) return false;
            foreach (var child in tree.children)
            {
                if (MotionNeedsConversion(child.motion, visited, ctx)) return true;
            }
            return false;
        }

        /// <summary>
        /// BlendTreeが外部(共有)アセットで、かつ変換対象クリップを含む場合、
        /// 複製コントローラーのサブアセットとしてディープクローンして返す。
        /// それ以外は元のBlendTreeをそのまま返す。同じ外部ツリーは一度だけクローンする。
        /// </summary>
        private static BlendTree CloneExternalBlendTreeIfNeeded(BlendTree blendTree, string controllerPath,
            Dictionary<BlendTree, BlendTree> externalTreeClones, Context ctx)
        {
            if (blendTree == null) return null;

            BlendTree cached;
            if (externalTreeClones.TryGetValue(blendTree, out cached)) return cached;

            var treePath = AssetDatabase.GetAssetPath(blendTree);
            bool isExternal = !string.IsNullOrEmpty(treePath) && treePath != controllerPath;
            if (!isExternal || !MotionNeedsConversion(blendTree, new HashSet<BlendTree>(), ctx))
            {
                return blendTree;
            }

            var clone = CloneBlendTreeDeep(blendTree, controllerPath, externalTreeClones);
            ctx.report.Info(string.Format(
                "外部BlendTree「{0}」({1})を複製コントローラー内へクローンしました(共有アセットは変更しません)。",
                blendTree.name, treePath));
            return clone;
        }

        /// <summary>
        /// BlendTreeを子ツリーごと再帰的に複製し、複製コントローラーのサブアセットとして追加する。
        /// クリップ参照はそのまま(後段の ConvertBlendTree が差し替える)。
        /// </summary>
        private static BlendTree CloneBlendTreeDeep(BlendTree source, string controllerPath,
            Dictionary<BlendTree, BlendTree> externalTreeClones)
        {
            BlendTree cached;
            if (externalTreeClones.TryGetValue(source, out cached)) return cached;

            var clone = Object.Instantiate(source);
            clone.name = source.name;
            clone.hideFlags = HideFlags.HideInHierarchy;
            externalTreeClones[source] = clone; // 再帰・共有参照対策のため先に登録
            AssetDatabase.AddObjectToAsset(clone, controllerPath);

            var children = clone.children;
            bool changed = false;
            for (int i = 0; i < children.Length; i++)
            {
                var nested = children[i].motion as BlendTree;
                if (nested != null)
                {
                    children[i].motion = CloneBlendTreeDeep(nested, controllerPath, externalTreeClones);
                    changed = true;
                }
            }
            if (changed)
            {
                clone.children = children;
            }
            EditorUtility.SetDirty(clone);
            return clone;
        }

        /// <summary>
        /// BlendTree内のクリップ参照を変換する。
        /// 複製コントローラーのサブアセットであるBlendTreeはその場で書き換える
        /// (ChildMotionは構造体のため配列を再代入する)。
        /// </summary>
        private static void ConvertBlendTree(BlendTree blendTree, string controllerPath,
            HashSet<BlendTree> visitedBlendTrees, Dictionary<BlendTree, BlendTree> externalTreeClones, Context ctx)
        {
            if (blendTree == null || !visitedBlendTrees.Add(blendTree)) return;

            // 複製コントローラーのサブアセット以外(外部共有BlendTreeアセット)を書き換えると
            // PC側のコントローラーも壊れるため、書き換えずに警告する。
            // (変換が必要な外部ツリーは呼び出し前に CloneExternalBlendTreeIfNeeded で
            //  クローン済みのため、通常この警告には到達しない。安全網として残す)
            var blendTreePath = AssetDatabase.GetAssetPath(blendTree);
            bool isOwnSubAsset = string.IsNullOrEmpty(blendTreePath) || blendTreePath == controllerPath;

            var children = blendTree.children;
            bool changed = false;
            bool warnedExternal = false;

            for (int i = 0; i < children.Length; i++)
            {
                var childMotion = children[i];

                var clip = childMotion.motion as AnimationClip;
                if (clip != null)
                {
                    var converted = ConvertClipCached(clip, ctx);
                    if (converted == null) continue;

                    if (!isOwnSubAsset)
                    {
                        if (!warnedExternal)
                        {
                            ctx.report.Warn(string.Format(
                                "BlendTree「{0}」は外部アセット({1})のため書き換えをスキップしました。マテリアル差し替えクリップ「{2}」が変換されていません。",
                                blendTree.name, blendTreePath, clip.name));
                            warnedExternal = true;
                        }
                        continue;
                    }

                    childMotion.motion = converted;
                    children[i] = childMotion; // 構造体のため書き戻し
                    changed = true;
                    continue;
                }

                var nestedTree = childMotion.motion as BlendTree;
                if (nestedTree != null)
                {
                    // 自身のサブアセット内から外部BlendTreeが参照されている場合もクローンして差し替える
                    if (isOwnSubAsset)
                    {
                        var replacement = CloneExternalBlendTreeIfNeeded(nestedTree, controllerPath, externalTreeClones, ctx);
                        if (!ReferenceEquals(replacement, nestedTree))
                        {
                            childMotion.motion = replacement;
                            children[i] = childMotion; // 構造体のため書き戻し
                            changed = true;
                            nestedTree = replacement;
                        }
                    }
                    ConvertBlendTree(nestedTree, controllerPath, visitedBlendTrees, externalTreeClones, ctx);
                }
            }

            if (changed)
            {
                blendTree.children = children;
                EditorUtility.SetDirty(blendTree);
            }
        }

        /// <summary>
        /// AnimatorOverrideController: ベースまたはオーバーライドクリップに変換対象があれば、
        /// 変換後ベースを参照する新しいオーバーライドコントローラーを生成して保存する。
        /// </summary>
        private static RuntimeAnimatorController ConvertOverrideController(AnimatorOverrideController overrideController, Context ctx)
        {
            var baseController = overrideController.runtimeAnimatorController;
            var convertedBase = baseController != null ? ConvertController(baseController, ctx) : null;
            bool baseChanged = convertedBase != null && !ReferenceEquals(convertedBase, baseController);

            // 旧オーバーライド一覧(元クリップ → 差し替えクリップ)
            var oldPairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            overrideController.GetOverrides(oldPairs);

            bool overridesNeedConversion = false;
            foreach (var pair in oldPairs)
            {
                if (pair.Value != null && ClipNeedsConversion(pair.Value, ctx.materialMap))
                {
                    overridesNeedConversion = true;
                    break;
                }
            }

            if (!baseChanged && !overridesNeedConversion) return overrideController;

            if (baseController == null)
            {
                // ベースなしのオーバーライドコントローラーは実質機能しないためそのまま残す
                ctx.report.Warn(string.Format(
                    "オーバーライドコントローラー「{0}」にベースコントローラーが設定されていないため変換をスキップしました。",
                    overrideController.name));
                return overrideController;
            }

            var newOverrideController = new AnimatorOverrideController(baseChanged ? convertedBase : baseController);

            // 旧オーバーライドを元クリップ単位で引けるようにする
            var oldMap = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var pair in oldPairs)
            {
                if (pair.Key != null && pair.Value != null && !oldMap.ContainsKey(pair.Key))
                {
                    oldMap.Add(pair.Key, pair.Value);
                }
            }

            // 変換後クリップ → 元クリップ の逆引き表
            // (ベースを複製した場合、新しいキーは変換後クリップになっているため)
            var reverseClipMap = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var kv in ctx.clipCache)
            {
                if (kv.Value != null && !reverseClipMap.ContainsKey(kv.Value))
                {
                    reverseClipMap.Add(kv.Value, kv.Key);
                }
            }

            var newPairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            newOverrideController.GetOverrides(newPairs);
            for (int i = 0; i < newPairs.Count; i++)
            {
                var key = newPairs[i].Key;
                if (key == null) continue;

                // 新ベースのキーが変換後クリップなら、元クリップに対応する旧オーバーライドを探す
                AnimationClip sourceKey;
                if (!reverseClipMap.TryGetValue(key, out sourceKey)) sourceKey = key;

                AnimationClip oldValue;
                if (!oldMap.TryGetValue(sourceKey, out oldValue)) continue;

                var convertedValue = ConvertClipCached(oldValue, ctx);
                newPairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(
                    key, convertedValue != null ? convertedValue : oldValue);
            }
            newOverrideController.ApplyOverrides(newPairs);

            var folder = EnsureAnimationsFolder(ctx);
            var targetPath = ctx.assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(overrideController.name) + "_Quest.overrideController");

            // オーバーライドコントローラーは単一オブジェクトのアセットのため、既存があれば
            // EditorUtility.CopySerialized で内容だけ上書きして GUID を保持する
            // (QuestAssetPersistence の Material/Mesh/Clip と同じ流儀。AnimatorController と違い
            //  サブアセットを持たないため CopyAsset に頼る必要がなく、前回複製の削除も不要)。
            var existingOverride = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(targetPath);
            AnimatorOverrideController savedOverride;
            if (existingOverride != null)
            {
                EditorUtility.CopySerialized(newOverrideController, existingOverride);
                EditorUtility.SetDirty(existingOverride);
                Object.DestroyImmediate(newOverrideController); // 一時インスタンスは破棄(既存側を使う)
                savedOverride = existingOverride;
            }
            else
            {
                AssetDatabase.CreateAsset(newOverrideController, targetPath);
                savedOverride = newOverrideController;
            }

            ctx.convertedControllerCount++;
            ctx.report.Info(string.Format("オーバーライドコントローラーを複製しました: {0} → {1}", overrideController.name, targetPath));
            return savedOverride;
        }

        // ---------------------------------------------------------------
        // クリップ変換
        // ---------------------------------------------------------------

        /// <summary>クリップのObjectReferenceカーブに対応表のマテリアル参照が含まれるか。</summary>
        private static bool ClipNeedsConversion(AnimationClip clip, Dictionary<Material, Material> materialMap)
        {
            if (clip == null || materialMap == null || materialMap.Count == 0) return false;

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes == null) continue;
                foreach (var keyframe in keyframes)
                {
                    var material = keyframe.value as Material;
                    if (material != null && materialMap.ContainsKey(material)) return true;
                }
            }
            return false;
        }

        /// <summary>キャッシュ付きクリップ変換。変換不要ならnull(キャッシュにもnullを記録)。</summary>
        private static AnimationClip ConvertClipCached(AnimationClip clip, Context ctx)
        {
            if (clip == null) return null;

            AnimationClip cached;
            if (ctx.clipCache.TryGetValue(clip, out cached)) return cached;

            AnimationClip result = null;
            if (ClipNeedsConversion(clip, ctx.materialMap))
            {
                result = DuplicateAndRewriteClip(clip, ctx);
            }
            ctx.clipCache[clip] = result;
            return result;
        }

        /// <summary>クリップを複製し、マテリアル参照を変換後マテリアルへ書き換えて保存する。</summary>
        private static AnimationClip DuplicateAndRewriteClip(AnimationClip clip, Context ctx)
        {
            // CopySerialized で設定(ループ等)を含めて完全複製する
            var copy = new AnimationClip();
            EditorUtility.CopySerialized(clip, copy);
            copy.hideFlags = HideFlags.None; // FBX等のサブアセット由来のhideFlagsを解除

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(copy))
            {
                var keyframes = AnimationUtility.GetObjectReferenceCurve(copy, binding);
                if (keyframes == null) continue;

                // パーティクル系レンダラーへの差し替えはパーティクル用変換結果を優先する
                // (メッシュと共用のマテリアルを不透明メッシュ用で差し替えると加算/乗算描画が壊れるため)
                bool preferParticle = IsParticleLikeBindingType(binding.type);

                bool curveChanged = false;
                for (int i = 0; i < keyframes.Length; i++)
                {
                    var material = keyframes[i].value as Material;
                    if (material == null) continue;

                    Material replacement = null;
                    bool found = preferParticle && ctx.particleMaterialMap != null &&
                                 ctx.particleMaterialMap.TryGetValue(material, out replacement) && replacement != null;
                    if (!found)
                    {
                        found = ctx.materialMap.TryGetValue(material, out replacement) && replacement != null;
                    }
                    if (found)
                    {
                        keyframes[i].value = replacement;
                        curveChanged = true;
                    }
                }
                if (curveChanged)
                {
                    AnimationUtility.SetObjectReferenceCurve(copy, binding, keyframes);
                }
            }

            var folder = EnsureAnimationsFolder(ctx);
            var targetPath = ctx.assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(clip.name) + "_Quest.anim");
            // 実行間で安定したパスへ、既存アセットがあれば GUID を保持したまま内容だけ上書きする
            // (前回の _Quest クローンのアニメーション参照が再変換で切れない)
            AnimationClip saved = QuestAssetPersistence.SaveOrOverwriteClip(copy, targetPath);
            // 既存アセットへ上書きした場合、メモリ上の一時クリップは不要
            // (アセット化されておらず、保存側で破棄されていないもののみ破棄する)
            if (saved != null && !ReferenceEquals(saved, copy) && copy != null && !AssetDatabase.Contains(copy))
            {
                Object.DestroyImmediate(copy);
            }

            ctx.convertedClipCount++;
            ctx.report.Info(string.Format("クリップを複製しました: {0} → {1}", clip.name, targetPath));
            return saved != null ? saved : copy;
        }

        // ---------------------------------------------------------------
        // 共通
        // ---------------------------------------------------------------

        /// <summary>
        /// ObjectReferenceカーブのバインディング対象コンポーネント型がパーティクル系レンダラー
        /// (ParticleSystemRenderer / TrailRenderer / LineRenderer)かどうか。
        /// AvatarQuestConverter.IsParticleLikeRenderer と同じ判定基準。
        /// </summary>
        private static bool IsParticleLikeBindingType(System.Type type)
        {
            if (type == null) return false;
            return typeof(ParticleSystemRenderer).IsAssignableFrom(type) ||
                   typeof(TrailRenderer).IsAssignableFrom(type) ||
                   typeof(LineRenderer).IsAssignableFrom(type);
        }

        /// <summary>{outputDir}/Animations フォルダを作成して返す。</summary>
        private static string EnsureAnimationsFolder(Context ctx)
        {
            var root = string.IsNullOrEmpty(ctx.outputDir)
                ? "Assets"
                : ctx.outputDir.Replace('\\', '/').TrimEnd('/');
            var folder = root + "/Animations";
            QuestConverterUtility.EnsureFolder(folder);
            return folder;
        }
    }
}
#endif
