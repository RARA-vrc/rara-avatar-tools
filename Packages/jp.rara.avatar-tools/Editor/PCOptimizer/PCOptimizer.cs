// RARA PC軽量化ツール - 最適化オーケストレーター
// PCアバターを非破壊で複製("_Opt")し、衣装・トグル整理 / マテリアル複製・テクスチャ縮小 /
// アトラス統合 / PhysBone整理 / AAO付与を行い、Windows基準のランクを改善する。
// 元アバターは一切変更しない。結果はシーン上の複製とプレファブ(_Opt.prefab)の両方で残す。
//
// RARA.QuestConverter と同一アセンブリ(Assembly-CSharp-Editor)のため、QuestConverter の
// 公開 API(ToggleConsolidator / ComponentRemover / AnimationConverter / TextureBaker /
// AAOMeshRemovalHelper / QuestAssetPersistence 等)をそのまま再利用する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using RARA.QuestConverter;

namespace RARA.PCOptimizer
{
    /// <summary>
    /// PC(Windows)ランク改善を統括する。元アバターは変更せず、複製("_Opt")に対して最適化を適用する。
    /// </summary>
    public static class PCOptimizer
    {
        private const string UndoLabel = "PC軽量化";
        private const string ProgressTitle = "RARA PC軽量化";
        private const string GeneratedRoot = "Assets/RARA/PCOptimizer/Generated";

        /// <summary>
        /// シーン上のPCアバターを複製して軽量化する。成功時は複製("_Opt")を返す。検証エラー時は null。
        /// savePrefab が有効なら Generated/{name}/{name}_Opt.prefab としても保存する(同一パス上書き)。
        /// </summary>
        public static GameObject Optimize(GameObject avatar, PCOptimizeSettings settings, ConversionReport report)
        {
            if (report == null) report = new ConversionReport(); // 呼び出し側の渡し忘れ対策(結果は破棄される)

            // --- 検証 ---
            if (avatar == null)
            {
                report.Error("軽量化対象のアバターが指定されていません。");
                return null;
            }
            if (!avatar.scene.IsValid())
            {
                report.Error("シーン上に配置されたアバターを指定してください(プレファブアセットは直接最適化できません)。");
                return null;
            }
            if (settings == null)
            {
                report.Error("設定(PCOptimizeSettings)がnullです。");
                return null;
            }

            // --- 使い方ガイダンス(製品意図) ---
            report.Info("PC軽量化: 元アバターは無改変のまま、複製 '" + avatar.name + "_Opt' を作成し、プレファブとしても保存します(非破壊で新たにプレファブが出現する形式)。");
            report.Info("【推奨手順】先に外部のポリゴン削減(デシメーション)ツールで三角形を70,000以下にしてから本ツールを使うと、PCランクを効率よく改善できます。本ツールはその後の「雑な軽量化」を担当します。");

            // --- 最適化前のパフォーマンス計測(元アバター基準) ---
            PerfSnapshot before = PerfEval.Compute(avatar);

            // --- 出力フォルダ ---
            string safeName = QuestConverterUtility.SanitizeAssetName(avatar.name);
            string outputDir = GeneratedRoot + "/" + safeName;
            QuestConverterUtility.EnsureFolder(GeneratedRoot);
            QuestConverterUtility.EnsureFolder(outputDir);
            QuestConverterUtility.EnsureFolder(outputDir + "/Materials");
            QuestConverterUtility.EnsureFolder(outputDir + "/Textures");
            QuestConverterUtility.EnsureFolder(outputDir + "/Meshes");
            var assets = new ConversionAssetContext();

            int undoGroup = Undo.GetCurrentGroup();
            GameObject clone = null;
            try
            {
                // --- 1. 前回の "_Opt" 複製をシーンから除去(元アバターは絶対に削除しない) ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "前回の複製を整理中...", 0.05f);
                string cloneName = avatar.name + "_Opt";
                RemovePriorOptClones(avatar, cloneName, report);

                // --- 2. アバター複製(プレファブ接続を保持) ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "アバターを複製中...", 0.1f);
                clone = DuplicateAvatar(avatar, report);
                clone.name = cloneName;
                clone.SetActive(true);
                Undo.RegisterCreatedObjectUndo(clone, UndoLabel);
                report.Info("アバターを複製しました: " + clone.name);

                // --- 3. 衣装・トグル整理(常時表示固定 / 非表示除去) ---
                if (HasConsolidationWork(settings))
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "衣装・トグルを整理中...", 0.2f);
                    ToggleConsolidator.ApplyConsolidation(clone, settings.toggleChoices, report, outputDir, assets);
                }

                // --- 4. マテリアルを複製して差し替え(以降の編集は複製のみ。元マテリアルは無改変) ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "マテリアルを複製中...", 0.35f);
                // 元→複製マテリアルの対応表を保持する。アトラス除外指定は「元アバターのマテリアルGUID」で
                // 記録されているため、複製後の新GUIDへ翻訳してアトラス統合へ渡す(除外がプレビューだけでなく
                // 実行でも効くようにする)。
                Dictionary<Material, Material> materialMap = DuplicateAndRemapMaterials(clone, outputDir, assets, report);

                // --- 5. テクスチャ縮小計画を適用(縮小コピーを生成し、複製マテリアルから参照させる) ---
                if (settings.texturePlan != null && settings.texturePlan.Count > 0)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "テクスチャ縮小コピーを生成中...", 0.45f);
                    ApplyTexturePlan(clone, settings, outputDir, assets, report);
                }

                // --- 6. アトラス統合(I2: PCMaterialAtlasser) ---
                if (settings.enableAtlas)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "マテリアルをアトラス化中...", 0.55f);
                    try
                    {
                        PCMaterialAtlasser.BuildAndApply(clone, settings, outputDir, report, assets, materialMap);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        report.Warn("アトラス化でエラーが発生したためスキップしました: " + ex.Message);
                    }
                }

                // --- 7. PhysBone整理(選択 → マージ。PCでは強制トリムしない) ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "PhysBoneを整理中...", 0.7f);
                ApplyPhysBones(clone, settings, report);

                // --- 7.5. SkinnedMesh統合(顔以外を1つへ) ---
                // 顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を AAO MergeSkinnedMesh で1つへ統合し、
                // SMR数・マテリアルスロット数を削減する(PC Good上限 SMR2対策)。ブレンドシェイプ改名・スロット
                // 再マップ・アニメ再パスはビルド時(NDMF)にAAOが行うため、顔以外のブレンドシェイプ・アニメも
                // 追従して動き続ける(バインディング書き換え済み)。QuestConverter と同じ実装を共有する。
                if (settings.mergeSkinnedMeshesMode != RARA.QuestConverter.SkinnedMeshMergeMode.None)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "SkinnedMeshを統合中...", 0.8f);
                    RARA.QuestConverter.SkinnedMeshMergePlan mergePlan = RARA.QuestConverter.SkinnedMeshMergePlanner.BuildPlan(
                        clone, settings.mergeSkinnedMeshesMode, settings.skinnedMeshMergeOptOutPaths, settings.smrMergeGroups);
                    RARA.QuestConverter.AAOMeshMergeHelper.ApplyMergeSkinnedMesh(clone, mergePlan, report);
                }

                // --- 8. AAO Trace and Optimize 付与(ビルド時のメッシュ/スロット統合を有効化) ---
                if (settings.ensureTraceAndOptimize)
                {
                    AAOMeshRemovalHelper.EnsureTraceAndOptimize(clone, report);
                }

                // --- 8.5. Network ID割り当て(PC/Quest間の揺れ物の掴み同期) ---
                // 両クローンで同じ論理PhysBoneが同じIDになるようにする。PhysBone整理(step 7)より後・プレファブ保存
                // (step 10)より前に置き、クローンの生存コンポーネントを対象にしつつ保存物へIDが含まれるようにする。
                if (settings.assignNetworkIds)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Network IDを割り当て中...", 0.84f);
                    NetworkIdAssigner.AssignNetworkIds(avatar, avatar.GetComponent<VRCAvatarDescriptor>(), clone, report);
                }

                // --- 9. Windows基準でパフォーマンスを再計測し、前後を報告(目標超過はエラー+助言) ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "パフォーマンスを再計測中...", 0.85f);
                PerfSnapshot after = PerfEval.Compute(clone);
                // R4: MA Merge Armature があると、ボーン数・PhysBoneマージ機会はビルド時の統合後にしか確定しない。
                // 計測レポートの値は「統合前」の暫定値であることを注記し、ボーン数超過はエラーではなく警告に緩める。
                bool hasMergeArmature = MACompatAudit.HasMergeArmature(clone);
                PerfEval.Report(before, after, settings.targetRank, hasMergeArmature, report);

                // --- 10. プレファブ保存(非破壊: 元アバターとは独立した新規プレファブ) ---
                if (settings.savePrefab)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "プレファブを保存中...", 0.95f);
                    string prefabPath = outputDir + "/" + QuestConverterUtility.SanitizeAssetName(cloneName) + ".prefab";
                    GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(clone, prefabPath);
                    if (savedPrefab != null)
                    {
                        report.Info("プレファブを保存しました: " + prefabPath);
                    }
                    else
                    {
                        report.Warn("プレファブの保存に失敗しました: " + prefabPath);
                    }
                }

                // R7: Modular Avatar コンポーネントのカバレッジ監査(本ツールが把握していない型があれば警告)。
                MACompatAudit.AuditCoverage(clone, "PC軽量化", report);

                // --- 仕上げ ---
                Selection.activeGameObject = clone;
                EditorGUIUtility.PingObject(clone);
                EditorSceneManager.MarkSceneDirty(clone.scene);
                AssetDatabase.SaveAssets();

                report.Info("PC軽量化が完了しました。確認・アップロードするのはこの複製 '" + cloneName + "' です(シーンでハイライト表示しました)。元アバターは無改変です。生成先: " + outputDir);
                report.Info("この複製の VRCAvatarDescriptor をそのまま Questコンバーターへかければ、続けてQuest対応も行えます。");
                return clone;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                report.Error("PC軽量化中に予期しない例外が発生しました: " + ex.Message);
                // 途中まで最適化された複製は削除せず残す(ユーザーが状態を確認できるように)
                return clone;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Undo.SetCurrentGroupName(UndoLabel);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        // ================================================================
        // 複製・前回クローン整理
        // ================================================================

        /// <summary>
        /// settings.toggleChoices に Keep 以外(LockVisible / LockHidden)の指定が1件でもあるか。
        /// </summary>
        private static bool HasConsolidationWork(PCOptimizeSettings settings)
        {
            if (settings == null || settings.toggleChoices == null) return false;
            foreach (ToggleGroupChoice choice in settings.toggleChoices)
            {
                if (choice != null && choice.choice != ToggleLockChoice.Keep) return true;
            }
            return false;
        }

        /// <summary>
        /// 前回生成の "{name}_Opt" 複製をシーンから除去する。同じ親の兄弟だけでなく、
        /// シーン内の全ルート配下(非アクティブ・移動された複製も含む)を走査し、
        /// 「名前が cloneName(Unity重複サフィックス " (1)" 等を許容)と一致し、VRCAvatarDescriptor を持つ」
        /// オブジェクトを対象とする。元アバター自身とその階層(祖先・子孫)は絶対に削除しない。
        /// 削除できなかった候補は名前を明示して警告する(黙って残さない)。
        /// </summary>
        private static void RemovePriorOptClones(GameObject avatar, string cloneName, ConversionReport report)
        {
            var toRemove = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            foreach (GameObject root in avatar.scene.GetRootGameObjects())
            {
                if (root == null) continue;
                // 非アクティブ含む全子孫の VRCAvatarDescriptor を走査する(別の親へ移動・非表示にされた旧複製も捕捉)
                foreach (VRCAvatarDescriptor desc in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                {
                    if (desc == null) continue;
                    GameObject go = desc.gameObject;
                    if (!seen.Add(go)) continue;
                    if (IsSameOrRelated(go, avatar)) continue; // 元アバターとその階層は絶対に触らない
                    if (!IsGeneratedCloneName(go.name, cloneName)) continue;
                    toRemove.Add(go);
                }
            }

            foreach (GameObject prior in toRemove)
            {
                report.Warn("以前の最適化結果 '" + prior.name + "' を削除して作り直します。");
                try
                {
                    Undo.DestroyObjectImmediate(prior);
                }
                catch (Exception ex)
                {
                    report.Warn("以前の複製 '" + prior.name + "' を削除できませんでした(手動で削除してください): " + ex.Message);
                }
            }

            // 削除しきれず残ったものが無いか最終確認する(黙って残さない)
            foreach (GameObject prior in toRemove)
            {
                if (prior != null)
                {
                    report.Warn("複製 '" + prior.name + "' が削除されずシーンに残っています。手動で削除してから再実行してください。");
                }
            }
        }

        /// <summary>candidate が avatar 自身、または avatar と祖先・子孫の関係にあるか。</summary>
        private static bool IsSameOrRelated(GameObject candidate, GameObject avatar)
        {
            if (candidate == null || avatar == null) return true;
            if (candidate == avatar) return true;
            for (Transform p = candidate.transform; p != null; p = p.parent) if (p == avatar.transform) return true;
            for (Transform p = avatar.transform; p != null; p = p.parent) if (p == candidate.transform) return true;
            return false;
        }

        /// <summary>name が cloneName そのものか、Unityの重複サフィックス付き("cloneName (1)" 等)か。</summary>
        private static bool IsGeneratedCloneName(string name, string cloneName)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cloneName)) return false;
            if (string.Equals(name, cloneName, StringComparison.Ordinal)) return true;
            string prefix = cloneName + " (";
            if (name.Length > prefix.Length + 1 &&
                name.StartsWith(prefix, StringComparison.Ordinal) &&
                name[name.Length - 1] == ')')
            {
                string inner = name.Substring(prefix.Length, name.Length - prefix.Length - 1);
                int suffix;
                return inner.Length > 0 && int.TryParse(inner, out suffix);
            }
            return false;
        }

        /// <summary>
        /// プレファブインスタンス状態を保持したままアバターを複製する。
        /// ペーストボード複製が使えない場合は Object.Instantiate にフォールバックする。
        /// </summary>
        private static GameObject DuplicateAvatar(GameObject source, ConversionReport report)
        {
            GameObject clone = null;
            try
            {
                Selection.activeGameObject = source;
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                GameObject duplicated = Selection.activeGameObject;
                if (duplicated != null && duplicated != source && duplicated.scene == source.scene)
                {
                    clone = duplicated;
                }
                else if (duplicated != null && duplicated != source)
                {
                    UnityEngine.Object.DestroyImmediate(duplicated);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            if (clone == null)
            {
                report.Warn("ペーストボード複製に失敗したため Object.Instantiate で複製します(プレファブ接続は保持されません)。");
                clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.transform.localPosition = source.transform.localPosition;
                clone.transform.localRotation = source.transform.localRotation;
                clone.transform.localScale = source.transform.localScale;
                clone.name = source.name; // "(Clone)" を除去(直後に "_Opt" 付与でリネームされる)
            }
            return clone;
        }

        // ================================================================
        // マテリアル複製・差し替え(PC: 元マテリアルはPCシェーダーのまま複製し、以降は複製のみ編集)
        // ================================================================

        /// <summary>
        /// 複製が参照する固有マテリアル(レンダラー + コンポーネント参照 + アニメーション参照)を
        /// Generated/Materials へGUID安定で複製し、複製アバターの参照を複製マテリアルへ差し替える。
        /// 差し替えた対応表(元→複製)を返す。以降のテクスチャ縮小・アトラスは複製マテリアルのみを編集する。
        /// </summary>
        private static Dictionary<Material, Material> DuplicateAndRemapMaterials(GameObject clone, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            List<Material> originals = CollectCloneMaterials(clone);
            var map = new Dictionary<Material, Material>();
            foreach (Material src in originals)
            {
                if (src == null || map.ContainsKey(src)) continue;
                var copy = new Material(src) { name = src.name };
                string path = assets.Claim(outputDir + "/Materials/" + QuestConverterUtility.SanitizeAssetName(src.name) + ".mat");
                Material persisted = QuestAssetPersistence.SaveOrOverwriteMaterial(copy, path);
                if (persisted != null) map[src] = persisted;
            }

            if (map.Count == 0)
            {
                report.Info("複製対象マテリアルが見つかりませんでした(スキップ)。");
                return map;
            }

            int rendererSwaps = ApplyMaterialMapToRenderers(clone, map);
            ApplyMaterialMapToComponents(clone, map);
            // アニメーションのマテリアル差し替えカーブも複製マテリアルを指すよう複製・差し替える
            // (共有クリップ/コントローラーは無改変。複製専用のオーバーライドが outputDir に生成される)。
            AnimationConverter.ConvertAvatarAnimations(clone, map, outputDir, report, null, assets);

            report.Info("マテリアルを複製して差し替えました: " + map.Count + " 種類 / レンダラー " + rendererSwaps + " スロット。元マテリアルは無改変です。");
            return map;
        }

        /// <summary>
        /// 複製配下(EditorOnly除く)のレンダラー・コンポーネント・アニメーションが参照する固有マテリアルを収集する。
        /// </summary>
        private static List<Material> CollectCloneMaterials(GameObject clone)
        {
            var result = new List<Material>();

            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (QuestCompat.IsEditorOnly(renderer.transform)) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && !result.Contains(material)) result.Add(material);
                }
            }

            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(clone))
            {
                if (controller == null) continue;
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (keys == null) continue;
                        foreach (ObjectReferenceKeyframe key in keys)
                        {
                            var material = key.value as Material;
                            if (material != null && !result.Contains(material)) result.Add(material);
                        }
                    }
                }
            }

            SweepComponentMaterialReferences(clone, (component, property, material) =>
            {
                if (!result.Contains(material)) result.Add(material);
                return false; // 収集のみ
            });

            return result;
        }

        /// <summary>map に従って全レンダラー(EditorOnly除く)の sharedMaterials を差し替え、差し替えスロット数を返す。</summary>
        private static int ApplyMaterialMapToRenderers(GameObject clone, Dictionary<Material, Material> map)
        {
            int swaps = 0;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (QuestCompat.IsEditorOnly(renderer.transform)) continue;
                Material[] shared = renderer.sharedMaterials;
                var replaced = new Material[shared.Length];
                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    Material current = shared[i];
                    Material copy;
                    if (current != null && map.TryGetValue(current, out copy))
                    {
                        replaced[i] = copy;
                        changed = true;
                        swaps++;
                    }
                    else
                    {
                        replaced[i] = current;
                    }
                }
                if (changed)
                {
                    Undo.RecordObject(renderer, UndoLabel);
                    renderer.sharedMaterials = replaced;
                }
            }
            return swaps;
        }

        /// <summary>map に従ってコンポーネント(Renderer除く)のシリアライズ済みMaterial参照を差し替える。</summary>
        private static void ApplyMaterialMapToComponents(GameObject clone, Dictionary<Material, Material> map)
        {
            SweepComponentMaterialReferences(clone, (component, property, material) =>
            {
                Material copy;
                if (map.TryGetValue(material, out copy))
                {
                    property.objectReferenceValue = copy;
                    return true;
                }
                return false;
            });
        }

        /// <summary>
        /// clone 配下の全コンポーネント(Transform/Renderer/EditorOnly除く)のシリアライズ済み
        /// ObjectReference プロパティを走査し、Material参照ごとに visitor を呼ぶ。
        /// visitor が true を返した(参照を書き換えた)コンポーネントには ApplyModifiedPropertiesWithoutUndo を適用する。
        /// </summary>
        private static void SweepComponentMaterialReferences(GameObject clone, Func<Component, SerializedProperty, Material, bool> visitor)
        {
            foreach (Component component in clone.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;       // Missing Script等
                if (component is Transform) continue;
                if (component is Renderer) continue;    // sharedMaterials は別途処理済み
                if (QuestCompat.IsEditorOnly(component.transform)) continue;

                try
                {
                    var serializedObject = new SerializedObject(component);
                    SerializedProperty property = serializedObject.GetIterator();
                    bool modified = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var material = property.objectReferenceValue as Material;
                        if (material == null) continue;
                        if (visitor(component, property, material)) modified = true;
                    }
                    if (modified) serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[RARA PCOptimizer] コンポーネントのマテリアル参照走査に失敗したためスキップしました: "
                        + component.GetType().Name + " (" + component.gameObject.name + "): " + ex.Message);
                }
            }
        }

        // ================================================================
        // テクスチャ縮小計画(QuestConverter の縮小コピーパイプラインを再利用)
        // ================================================================

        /// <summary>
        /// settings.texturePlan の各エントリについて、対象テクスチャの縮小コピーを Generated/Textures へ生成し、
        /// 複製マテリアルのテクスチャプロパティのうち元テクスチャを指すものを縮小コピーへ差し替える。
        /// 元テクスチャのインポート設定は一切変更しない。
        /// </summary>
        private static void ApplyTexturePlan(GameObject clone, PCOptimizeSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            // 複製マテリアル一覧(EditorOnly除くレンダラーから収集)。差し替え対象はこれらのみ。
            var cloneMaterials = new List<Material>();
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (QuestCompat.IsEditorOnly(renderer.transform)) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && !cloneMaterials.Contains(material)) cloneMaterials.Add(material);
                }
            }

            int planned = 0;
            foreach (TextureSizePlanEntry entry in settings.texturePlan)
            {
                if (entry == null || string.IsNullOrEmpty(entry.textureGuid) || entry.targetSize <= 0) continue;

                string srcPath = AssetDatabase.GUIDToAssetPath(entry.textureGuid);
                if (string.IsNullOrEmpty(srcPath)) continue;
                var source = AssetDatabase.LoadAssetAtPath<Texture>(srcPath);
                if (source == null) continue;

                // 既に目標以下なら縮小しない(拡大はしない)。
                int longEdge = Mathf.Max(source.width, source.height);
                if (longEdge <= entry.targetSize) continue;

                bool isNormalMap;
                bool sRGB;
                ReadTextureImportFlags(srcPath, out isNormalMap, out sRGB);

                string cacheKey = entry.textureGuid + "@" + entry.targetSize;
                Texture2D copy;
                if (!assets.TryGetDownscaledCopy(cacheKey, out copy) || copy == null)
                {
                    string dstPath = assets.Claim(outputDir + "/Textures/"
                        + QuestConverterUtility.SanitizeAssetName(source.name) + "_p" + entry.targetSize + ".png");
                    // PC向けの縮小コピー。androidFormat は PC ビルドには影響しない(標準/Standalone は既定サイズを使う)。
                    copy = TextureBaker.DownscaleTextureCopy(source, entry.targetSize, isNormalMap, sRGB, dstPath, TextureImporterFormat.ASTC_6x6);
                    if (copy != null) assets.RegisterDownscaledCopy(cacheKey, copy);
                }
                if (copy == null)
                {
                    report.Warn("テクスチャ '" + source.name + "' の縮小コピー生成に失敗したためスキップしました。");
                    continue;
                }

                int swaps = RemapTextureInMaterials(cloneMaterials, source, copy);
                if (swaps > 0) planned++;
                report.Info("テクスチャを縮小: '" + source.name + "' → 長辺 " + entry.targetSize + "px(" + swaps + " プロパティ差し替え)。");
            }

            if (planned > 0) report.Info("テクスチャ縮小計画を適用しました: " + planned + " 件。元テクスチャは無改変です。");
        }

        /// <summary>テクスチャのインポート設定から「ノーマルマップか」「sRGBか」を読む(失敗時は color/sRGB とみなす)。</summary>
        private static void ReadTextureImportFlags(string assetPath, out bool isNormalMap, out bool sRGB)
        {
            isNormalMap = false;
            sRGB = true;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            isNormalMap = importer.textureType == TextureImporterType.NormalMap;
            sRGB = importer.sRGBTexture && !isNormalMap;
        }

        /// <summary>
        /// materials の各テクスチャプロパティのうち source を指すものを replacement へ差し替え、差し替え数を返す。
        /// シェーダーのテクスチャプロパティを列挙して走査するため、任意のシェーダーに対応する。
        /// </summary>
        private static int RemapTextureInMaterials(List<Material> materials, Texture source, Texture replacement)
        {
            int swaps = 0;
            foreach (Material material in materials)
            {
                if (material == null || material.shader == null) continue;
                int count = ShaderUtil.GetPropertyCount(material.shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(material.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    string propName = ShaderUtil.GetPropertyName(material.shader, i);
                    if (!material.HasProperty(propName)) continue;
                    if (material.GetTexture(propName) == source)
                    {
                        Undo.RecordObject(material, UndoLabel);
                        material.SetTexture(propName, replacement);
                        EditorUtility.SetDirty(material);
                        swaps++;
                    }
                }
            }
            return swaps;
        }

        // ================================================================
        // PhysBone整理(選択 → マージ。PCでは強制トリムしない)
        // ================================================================

        private static void ApplyPhysBones(GameObject clone, PCOptimizeSettings settings, ConversionReport report)
        {
            // 選択(マージより先に適用): keepPaths があれば OptIn、無ければ removePaths を削除、どちらも無ければ全て残す。
            if (settings.physBoneKeepPaths != null && settings.physBoneKeepPaths.Count > 0)
            {
                ComponentRemover.RemoveAllExceptKept(clone, settings.physBoneKeepPaths, report);
            }
            else if (settings.physBoneRemovePaths != null && settings.physBoneRemovePaths.Count > 0)
            {
                ComponentRemover.RemoveSelectedPhysBones(clone, settings.physBoneRemovePaths, report);
            }

            if (settings.mergePhysBones)
            {
                HashSet<string> animatedTogglePaths = ComponentRemover.CollectPhysBoneTogglePaths(clone);
                ComponentRemover.MergePhysBones(clone, animatedTogglePaths, null, settings.physBoneLooseMerge, report);
            }

            // 目標ランクの PhysBone コンポーネント上限との比較(PCでは自動削除せず警告のみ)。
            int surviving = 0;
            foreach (VRCPhysBone pb in clone.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (pb != null && !QuestCompat.IsEditorOnly(pb.transform)) surviving++;
            }
            int limit = PCRankLimits.GetLimit(settings.targetRank, PCRankLimits.PCStat.PhysBoneComponents);
            if (surviving > limit)
            {
                report.Warn("PhysBoneコンポーネントが " + surviving + " 個で、目標ランク(" + settings.targetRank
                    + ")の上限 " + limit + " を超えています。PhysBoneの選択(残す/削除)やマージで減らしてください(PCでは自動削除しません)。");
            }
        }

        // ================================================================
        // Windows基準パフォーマンス計測・評価
        // ================================================================

        /// <summary>1体ぶんの Windows 基準パフォーマンススナップショット。</summary>
        private struct PerfSnapshot
        {
            public double[] values;              // PCStat 添字ごとの実測値
            public PerformanceRating overall;    // 総合ランク
        }

        /// <summary>Windows基準の計測と前後比較レポートをまとめたヘルパー。</summary>
        private static class PerfEval
        {
            private const int StatCount = 12;

            private static readonly string[] Labels =
            {
                "三角形数(ポリゴン)",
                "スキンメッシュ数",
                "メッシュレンダラー数",
                "マテリアルスロット数",
                "テクスチャメモリ(MB)",
                "ボーン数",
                "PhysBoneコンポーネント数",
                "PhysBoneトランスフォーム数",
                "PhysBoneコライダー数",
                "PhysBone衝突判定数",
                "コンタクト数(非ローカル)",
                "コンストレイント数",
            };

            private static readonly string[] Advice =
            {
                "外部のデシメーションツールで三角形を減らしてください(目標ランクの上限以下へ)。",
                "衣装・トグルを『常時表示』に固定し、AAOでスキンメッシュを統合してください。",
                "不要なメッシュレンダラーを減らすか、トグル固定で統合してください。",
                "アトラス統合を有効にし、マテリアルスロットを減らしてください。",
                "テクスチャ縮小計画やアトラスでテクスチャメモリを減らしてください。",
                "使っていないボーンを減らすか、AAOのボーン統合を検討してください。",
                "PhysBoneのマージ/削除でコンポーネント数を減らしてください。",
                "PhysBoneの対象トランスフォーム数を減らしてください。",
                "PhysBoneコライダーを減らしてください。",
                "PhysBoneコライダー/対象を減らし、衝突判定数を下げてください。",
                "コンタクト(レシーバー)をローカル化するか数を減らしてください。",
                "コンストレイント数を減らしてください。",
            };

            private static readonly string[] RankLetters = { "Excellent", "Good", "Medium", "Poor", "Very Poor" };

            public static PerfSnapshot Compute(GameObject go)
            {
                var snapshot = new PerfSnapshot { values = new double[StatCount], overall = PerformanceRating.None };
                if (go == null) return snapshot;

                // アップロード時に除去されるEditorOnlyを取り除き、コンストレイントのグループ計上を正した一時複製で計測する
                // (ウィンドウ診断表 ComputePCPerformance と同じ経路。元/複製アバターは変更しない)。
                GameObject temp = UnityEngine.Object.Instantiate(go);
                temp.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    QuestCompat.StripEditorOnlySubtrees(temp);
                    QuestCompat.RefreshConstraintGroups(temp);

                    var stats = new AvatarPerformanceStats(false); // false => Windows(PC)基準
                    AvatarPerformance.CalculatePerformanceStats(go.name, temp, stats, false);

                    var pb = stats.physBone;
                    double[] v = snapshot.values;
                    v[(int)PCRankLimits.PCStat.Triangles] = stats.polyCount ?? 0;
                    v[(int)PCRankLimits.PCStat.SkinnedMeshes] = stats.skinnedMeshCount ?? 0;
                    v[(int)PCRankLimits.PCStat.MeshRenderers] = stats.meshCount ?? 0;
                    v[(int)PCRankLimits.PCStat.MaterialSlots] = stats.materialCount ?? 0;
                    v[(int)PCRankLimits.PCStat.TextureMemoryMB] = stats.textureMegabytes ?? 0f;
                    v[(int)PCRankLimits.PCStat.Bones] = stats.boneCount ?? 0;
                    v[(int)PCRankLimits.PCStat.PhysBoneComponents] = pb != null ? pb.Value.componentCount : 0;
                    v[(int)PCRankLimits.PCStat.PhysBoneTransforms] = pb != null ? pb.Value.transformCount : 0;
                    v[(int)PCRankLimits.PCStat.PhysBoneColliders] = pb != null ? pb.Value.colliderCount : 0;
                    v[(int)PCRankLimits.PCStat.PhysBoneCollisionChecks] = pb != null ? pb.Value.collisionCheckCount : 0;
                    v[(int)PCRankLimits.PCStat.Contacts] = stats.contactCount ?? 0;
                    v[(int)PCRankLimits.PCStat.Constraints] = stats.constraintsCount ?? 0;

                    snapshot.overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[RARA PCOptimizer] パフォーマンス計測に失敗しました: " + ex.Message);
                }
                finally
                {
                    if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
                }
                return snapshot;
            }

            public static void Report(PerfSnapshot before, PerfSnapshot after, PCTargetRank target, bool hasMergeArmature, ConversionReport report)
            {
                report.Info("=== PCパフォーマンス(Windows基準) 目標ランク: " + target + " ===");
                report.Info("総合ランク: " + before.overall + " → " + after.overall);

                for (int s = 0; s < StatCount; s++)
                {
                    var stat = (PCRankLimits.PCStat)s;
                    double b = before.values != null ? before.values[s] : 0;
                    double a = after.values != null ? after.values[s] : 0;
                    int limit = PCRankLimits.GetLimit(target, stat);
                    int achievedIndex = RankIndex(stat, a);
                    bool overTarget = achievedIndex > (int)target;

                    string line = Labels[s] + ": " + FormatValue(stat, b) + " → " + FormatValue(stat, a)
                        + "(目標 " + target + " 上限 " + limit + ") 判定: " + RankLetters[achievedIndex];

                    if (overTarget)
                    {
                        // R4: MA Merge Armature がある場合、ボーン数の超過はビルド時の統合で解消しうるため
                        // エラーではなく警告に緩める(表示値は統合前のため)。
                        if (hasMergeArmature && stat == PCRankLimits.PCStat.Bones)
                        {
                            report.Warn(line + " ⚠ 目標未達(ただしMA Merge Armature使用のためビルド時の統合で改善見込み): " + Advice[s]);
                        }
                        else
                        {
                            report.Error(line + " ⚠ 目標未達: " + Advice[s]);
                        }
                    }
                    else
                    {
                        report.Info(line);
                    }
                }

                report.Info("注: AAO(Trace and Optimize)による最終的なメッシュ/スロット統合や、EditorOnly化した非表示メッシュの削減はビルド時に反映されます。上記『適用後』はシーン上の暫定値のため、実際のビルド後はさらに改善する場合があります。");
                if (hasMergeArmature)
                {
                    report.Info(MACompatAudit.MergeArmatureNote);
                }
            }

            /// <summary>value が到達するランク添字(0=Excellent … 4=Very Poor)を返す。</summary>
            private static int RankIndex(PCRankLimits.PCStat stat, double value)
            {
                for (int r = 0; r < 4; r++)
                {
                    if (value <= PCRankLimits.GetLimit((PCTargetRank)r, stat)) return r;
                }
                return 4; // Very Poor
            }

            private static string FormatValue(PCRankLimits.PCStat stat, double value)
            {
                return stat == PCRankLimits.PCStat.TextureMemoryMB
                    ? value.ToString("F1")
                    : Mathf.RoundToInt((float)value).ToString();
            }
        }
    }
}
#endif
