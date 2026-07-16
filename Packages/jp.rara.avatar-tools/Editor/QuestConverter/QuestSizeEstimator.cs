// RARA Quest Converter - ダウンロードサイズ見積もりモジュール
// Android(Quest)ビルドのダウンロードサイズ(圧縮後)を概算し、削減案を提示する。
// あわせて、目標ダウンロードサイズへ収める自動縮小計画(PlanBudgetFit / ApplyBudgetFit)と
// テクスチャ縮小提案の一括適用(ApplyAllTextureSuggestions)もここで行う。
// 適用系はいずれも元テクスチャのインポート設定を変更せず、縮小計画(settings.textureSizePlan)を
// 更新するだけ。縮小は変換時に生成される縮小コピーへ適用され、変換後のマテリアルがそれを参照する。
// あくまで「目安」であり、実際のビルドサイズとは誤差がある(UI表示でもその旨を明示すること)。
// EditorOnlyタグの付いたサブツリーと settings.questExcludePaths で除外されたサブツリーは、
// VRChatのビルドと同様に「削除済み」として集計から除外する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>ダウンロードサイズ見積もりの結果一式(すべて概算の目安値)。</summary>
    public class SizeEstimateResult
    {
        /// <summary>ダウンロードサイズ合計の目安(MB)。テクスチャ+メッシュ+アニメーション。</summary>
        public float estimatedDownloadMB;
        /// <summary>テクスチャ分のダウンロードサイズ目安(MB)。</summary>
        public float textureDownloadMB;
        /// <summary>メッシュ分のダウンロードサイズ目安(MB)。</summary>
        public float meshDownloadMB;
        /// <summary>アニメーション分のダウンロードサイズ目安(MB)。</summary>
        public float animationDownloadMB;
        /// <summary>テクスチャの展開後メモリ目安(MB)。SDKのTextureMegabytes統計に相当する概算。</summary>
        public float textureMemoryMB;
        /// <summary>テクスチャ別の内訳(ダウンロードサイズ降順)。</summary>
        public System.Collections.Generic.List<TextureSizeInfo> textures;
        /// <summary>削減提案(削減量降順。最後の1件は常に汎用アドバイス)。</summary>
        public System.Collections.Generic.List<SizeSuggestion> suggestions;
        /// <summary>ダウンロードサイズ上限(QuestLimits.HardDownloadSizeCapMB)を超過している見込みならtrue。</summary>
        public bool overCap;
    }

    /// <summary>テクスチャ1枚分のサイズ見積もり情報。</summary>
    public class TextureSizeInfo
    {
        /// <summary>対象テクスチャ。</summary>
        public Texture texture;
        /// <summary>アセットパス(実行時生成テクスチャ等は空文字)。</summary>
        public string assetPath;
        /// <summary>Androidビルドで適用される最大テクスチャサイズ(上書きなしは既定のmaxTextureSize。縮小計画は含まない)。</summary>
        public int currentAndroidMaxSize;
        /// <summary>インポート設定にAndroid用の上書きがあるならtrue。</summary>
        public bool hasAndroidOverride;
        /// <summary>圧縮形式の表示用ラベル(例: "ASTC 6x6 (Android上書きなし・既定想定)")。縮小計画があるテクスチャは「縮小予定: {target}px」が付く。</summary>
        public string formatLabel;
        /// <summary>ダウンロードサイズ目安(MB)。</summary>
        public float downloadMB;
        /// <summary>展開後メモリ目安(MB、ミップマップ込み)。</summary>
        public float memoryMB;
    }

    /// <summary>サイズ削減の提案1件。</summary>
    public class SizeSuggestion
    {
        /// <summary>提案内容(日本語)。</summary>
        public string description;
        /// <summary>削減できる見込み量の目安(MB)。</summary>
        public float savingMB;
        /// <summary>対象テクスチャ(テクスチャ以外の提案ではnull)。</summary>
        public Texture texture;
        /// <summary>推奨する最大テクスチャサイズ(サイズ制限の提案でなければ0)。</summary>
        public int recommendedMaxSize;
    }

    /// <summary>
    /// 予算内サイズ調整(PlanBudgetFit)の計画1手順。
    /// テクスチャ1枚の実効サイズ(既存の縮小計画があればそれを適用した値)を
    /// fromSize → toSize へ縮小する(同一テクスチャの複数回の半減は1手順へまとめられる)。
    /// 適用(ApplyBudgetFit)は縮小計画(settings.textureSizePlan)への登録のみで、
    /// 元テクスチャのインポート設定は変更しない。
    /// </summary>
    public class BudgetFitStep
    {
        /// <summary>対象テクスチャ。</summary>
        public Texture texture;
        /// <summary>対象テクスチャのアセットパス。</summary>
        public string assetPath;
        /// <summary>縮小前の実効Android最大サイズ(px。既存の縮小計画適用後)。</summary>
        public int fromSize;
        /// <summary>縮小後の目標サイズ(px)。</summary>
        public int toSize;
        /// <summary>この縮小で削減できる見込み量の目安(MB)。</summary>
        public float savingMB;
        /// <summary>
        /// 縮小後サイズの見積もりに使う圧縮形式。基本は settings.androidFormat だが、既存のAndroid上書き形式の方が
        /// 高効率(bppが小さい)な場合はその形式を維持して見積もる。縮小計画には保存されないが、
        /// 変換時の縮小コピーも同じ規則(MaterialQuestConverter.GetDownscaleCopyFormat)で
        /// 形式を決めるため、見積もりと実際のコピーの形式は一致する。
        /// </summary>
        public TextureImporterFormat format = TextureImporterFormat.Automatic;
    }

    /// <summary>
    /// アバターのAndroid(Quest)ダウンロードサイズを概算する静的クラス。
    /// テクスチャは「実効サイズ × 圧縮形式のbpp × ミップ係数 × バンドル圧縮係数」、
    /// メッシュは「頂点構成からのバイト数 × 圧縮係数」、
    /// アニメーションは「Profiler計測値 × 換算係数」で見積もる。
    /// 例外はUIへ投げず、失敗した項目はラベル・注記として結果に残す。
    /// </summary>
    public static class QuestSizeEstimator
    {
        /// <summary>ミップマップ込みのメモリ係数(フルミップチェーンで約1/3増)。</summary>
        private const float MipmapFactor = 1.33f;
        /// <summary>アセットバンドル(LZ4)圧縮によるテクスチャの縮小係数の目安。</summary>
        private const float BundleCompressionFactor = 0.85f;
        /// <summary>メッシュデータの圧縮係数の目安(メッシュはよく縮む)。</summary>
        private const float MeshCompressionFactor = 0.6f;
        /// <summary>アニメーションのProfiler計測値→ダウンロードサイズ換算係数の目安。</summary>
        private const float AnimationDownloadFactor = 0.3f;
        /// <summary>ブレンドシェイプ1個・1頂点あたりの概算バイト数(デルタ圧縮込みの目安)。</summary>
        private const int BlendShapeBytesPerVertex = 10;
        /// <summary>Quest除外(EditorOnly)提案を出す最小削減量(MB)。</summary>
        private const float SubtreeSuggestionMinMB = 0.3f;
        /// <summary>テクスチャ縮小提案を出す最小削減量(MB)。微小な提案でリストを埋めない。</summary>
        private const float SuggestionMinSavingMB = 0.05f;
        /// <summary>ASTC 6x6のビット/ピクセル(圧縮形式見直し提案の再計算に使用)。</summary>
        private const float Astc6x6BitsPerPixel = 3.56f;
        /// <summary>予算内サイズ調整(PlanBudgetFit)で縮小する下限サイズ(これ以下へは縮めない)。</summary>
        private const int BudgetFitMinSize = 256;
        /// <summary>予算内サイズ調整で縮小を後回しにする優先保護テクスチャ名(顔・体・肌系)の部分一致キーワード。</summary>
        private static readonly string[] BudgetFitPriorityKeywords = { "face", "body", "skin", "顔", "体", "肌" };

        // ▼ Quest除外(EditorOnly)提案の保護判定。素体・顔メッシュを「除外候補」にすると
        //   アバターの本体が消えるため、これらのトークンに一致するレンダラーを含むサブツリーは
        //   actionable な除外提案にしない(PolygonBudgetPlanner の顔・素体分類と同じトークン)。
        /// <summary>顔メッシュとみなす名前トークン(GameObject名・メッシュ名の部分一致・大文字小文字無視)。</summary>
        private static readonly string[] FaceNameTokens = { "face", "顔", "head", "ヘッド", "頭" };
        /// <summary>素体メッシュとみなす名前トークン(GameObject名・メッシュ名・マテリアル名の部分一致・大文字小文字無視)。</summary>
        private static readonly string[] BodyNameTokens = { "body", "ボディ", "素体", "素肌", "肌", "skin", "torso", "胴" };
        /// <summary>単独で除外不可とみなす支配的メッシュの三角形シェアしきい値(アバター総三角形数に対する割合)。</summary>
        private const float DominantTriangleShare = 0.30f;

        /// <summary>低ディテール検出(AnalyzeDetailSize)のサンプル解像度。ここへ縮小してから色数・輝度分散を測る。</summary>
        private const int DetailSampleSize = 64;
        /// <summary>低ディテール検出で縮小する下限サイズ(px。これ未満へは縮めない)。</summary>
        private const int DetailMinSize = 16;
        // ▼ 低ディテール判定の輝度分散(0..1輝度)しきい値。いずれも「明らかに低ディテールなときだけ縮小する」
        //   保守的な値にしてある(通常のディテールあるテクスチャは誤って縮小しない)。分散は 1/12≒0.083 が
        //   輝度0..1一様分布の理論最大で、実写・イラスト系のアルベドは概ね 0.01〜0.05 程度になる。
        /// <summary>単色・極フラット判定の輝度分散しきい値(これ未満なら16pxまで縮小可)。</summary>
        private const float DetailSolidVariance = 0.00005f;
        /// <summary>準単色判定の輝度分散しきい値(量子化色数<=8 かつ これ未満で32pxまで縮小可)。</summary>
        private const float DetailNearSolidVariance = 0.0006f;
        /// <summary>低ディテール判定の輝度分散しきい値(これ未満で min(128, currentSize) まで縮小可)。</summary>
        private const float DetailLowVariance = 0.003f;
        /// <summary>
        /// 低ディテール(128px)判定で許容する量子化色数の上限。輝度分散は輝度(0.299R+0.587G+0.114B)のみで
        /// 測るため、輝度がほぼ一様でも色相・彩度に細部を持つテクスチャ(等輝度の細かな色パターン等)は分散が
        /// 低く出る。そこで16px/32px判定と同様に色数ガードを併用し、色数が多い=彩度ディテールがある場合は
        /// 128pxへ縮小しない(縮小しない側に倒す保守的な追加条件で、既存の縮小を強めることはない)。
        /// </summary>
        private const int DetailLowMaxDistinctColors = 128;

        /// <summary>AnalyzeDetailSize の結果キャッシュ(instanceID → ディテール由来の推奨上限px。int.MaxValue=縮小不要)。</summary>
        private static Dictionary<int, int> _detailCapCache;

        /// <summary>内部作業用: テクスチャ1枚の見積もりと、提案の再計算に必要な中間値。</summary>
        private class TextureWork
        {
            public TextureSizeInfo info;
            /// <summary>TextureImporterから見積もれたならtrue(falseはProfiler概算)。</summary>
            public bool hasImporter;
            /// <summary>想定圧縮形式のビット/ピクセル。</summary>
            public float bitsPerPixel;
            /// <summary>Androidビルドでの実効幅(maxTextureSize適用後)。</summary>
            public int effectiveWidth;
            /// <summary>Androidビルドでの実効高さ(maxTextureSize適用後)。</summary>
            public int effectiveHeight;
            /// <summary>ASTC 4x4 / 5x5(高品質・大容量)ならtrue。</summary>
            public bool isHighQualityAstc;
        }

        /// <summary>内部作業用: メッシュ1個の見積もり。</summary>
        private class MeshWork
        {
            public bool skinned;
            public float downloadMB;
            /// <summary>内訳: ブレンドシェイプ分のダウンロードサイズ目安(MB)。</summary>
            public float blendShapeMB;
        }

        /// <summary>
        /// アバターのダウンロードサイズを概算して返す。例外は投げず、失敗項目は結果内の注記として返す。
        /// EditorOnlyサブツリーと settings.questExcludePaths のサブツリー配下のレンダラーは集計から除外する。
        /// 縮小計画(settings.textureSizePlan)があるテクスチャは、その目標サイズで実効サイズを
        /// 頭打ちにして見積もる(変換時に縮小コピーが生成される前提。元インポート設定は見ない・変えない)。
        /// </summary>
        public static SizeEstimateResult Estimate(GameObject avatarRoot, QuestConvertSettings settings)
        {
            var result = new SizeEstimateResult
            {
                textures = new List<TextureSizeInfo>(),
                suggestions = new List<SizeSuggestion>(),
            };

            try
            {
                if (avatarRoot == null)
                {
                    result.suggestions.Add(Note("見積もり対象のアバターが指定されていません。"));
                    return result;
                }
                if (settings == null) settings = new QuestConvertSettings();

                // 内容由来の低ディテール解析キャッシュは実行間で作り直す(再インポート後の内容を反映するため)。
                InvalidateDetailCache();

                // 縮小計画をGUID→目標サイズの辞書へ(重複GUIDは最小の目標サイズを採用)
                Dictionary<string, int> planByGuid = BuildPlanByGuid(settings);

                // ---- 1. 収集対象レンダラー(EditorOnly / Quest除外パス配下は「削除済み」として除外) ----
                List<Transform> excludedRoots = ResolveExcludedRoots(avatarRoot.transform, settings);
                var keptRenderers = new List<Renderer>();
                foreach (Renderer renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null) continue;
                    if (QuestCompat.IsEditorOnly(renderer.transform)) continue;
                    if (IsUnderAny(renderer.transform, excludedRoots)) continue;
                    keptRenderers.Add(renderer);
                }

                // ---- 2. テクスチャ収集と見積もり ----
                var materialTextureCache = new Dictionary<Material, List<Texture>>();
                var rendererTextures = new Dictionary<Renderer, HashSet<Texture>>();
                var textureWorks = new Dictionary<Texture, TextureWork>();
                foreach (Renderer renderer in keptRenderers)
                {
                    var textureSet = new HashSet<Texture>();
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null) continue;
                        foreach (Texture tex in GetMaterialTextures(material, materialTextureCache))
                        {
                            textureSet.Add(tex);
                        }
                    }
                    rendererTextures[renderer] = textureSet;
                    foreach (Texture tex in textureSet)
                    {
                        if (!textureWorks.ContainsKey(tex))
                        {
                            textureWorks[tex] = EstimateTexture(tex, planByGuid);
                        }
                    }
                }

                // ---- 3. メッシュ収集と見積もり ----
                var rendererMeshes = new Dictionary<Renderer, Mesh>();
                var meshWorks = new Dictionary<Mesh, MeshWork>();
                foreach (Renderer renderer in keptRenderers)
                {
                    Mesh mesh = null;
                    bool skinned = false;
                    var skinnedRenderer = renderer as SkinnedMeshRenderer;
                    if (skinnedRenderer != null)
                    {
                        mesh = skinnedRenderer.sharedMesh;
                        skinned = true;
                    }
                    else
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null) mesh = filter.sharedMesh;
                    }
                    if (mesh == null) continue;
                    rendererMeshes[renderer] = mesh;

                    MeshWork existing;
                    if (!meshWorks.TryGetValue(mesh, out existing))
                    {
                        meshWorks[mesh] = EstimateMesh(mesh, skinned);
                    }
                    else if (skinned && !existing.skinned)
                    {
                        // 同一メッシュがスキンあり・なし両方で使われる場合は大きい方(スキンあり)で見積もる
                        meshWorks[mesh] = EstimateMesh(mesh, true);
                    }
                }

                // ---- 4. アニメーション見積もり ----
                result.animationDownloadMB = EstimateAnimationDownloadMB(avatarRoot, result);

                // ---- 5. 合計とキャップ判定 ----
                foreach (TextureWork work in textureWorks.Values)
                {
                    result.textures.Add(work.info);
                    result.textureDownloadMB += work.info.downloadMB;
                    result.textureMemoryMB += work.info.memoryMB;
                }
                result.textures.Sort((a, b) => b.downloadMB.CompareTo(a.downloadMB));

                float blendShapeTotalMB = 0f;
                foreach (MeshWork work in meshWorks.Values)
                {
                    result.meshDownloadMB += work.downloadMB;
                    blendShapeTotalMB += work.blendShapeMB;
                }

                result.estimatedDownloadMB = result.textureDownloadMB + result.meshDownloadMB + result.animationDownloadMB;
                result.overCap = result.estimatedDownloadMB > QuestLimits.HardDownloadSizeCapMB;

                // ---- 6. 削減提案 ----
                BuildSuggestions(avatarRoot, settings, result,
                    keptRenderers, rendererTextures, textureWorks, rendererMeshes, meshWorks, blendShapeTotalMB);
            }
            catch (Exception ex)
            {
                // UIへは例外を投げない。注記だけ残して返す。
                Debug.LogError("[RARA QuestConverter] サイズ見積もり中にエラーが発生しました: " + ex);
                result.suggestions.Add(Note("サイズ見積もり中にエラーが発生しました: " + ex.Message));
            }
            return result;
        }

        // ================================================================
        // 収集対象の決定
        // ================================================================

        /// <summary>settings.questExcludePaths をTransformへ解決する(解決できないパス・root自身を指すパスは無視)。</summary>
        private static List<Transform> ResolveExcludedRoots(Transform root, QuestConvertSettings settings)
        {
            var excluded = new List<Transform>();
            if (settings.questExcludePaths == null) return excluded;
            foreach (string path in settings.questExcludePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                Transform target = QuestCompat.FindByPath(root, path);
                // root自身は除外扱いにしない(全レンダラーが0MB集計になるのを防ぐ。
                // QuestDiagnostics.DestroyExcludedSubtrees / CollectExcludedRoots と同じガード)
                if (target != null && target != root && !excluded.Contains(target)) excluded.Add(target);
            }
            return excluded;
        }

        /// <summary>t が roots のいずれか自身またはその配下にあるか(Transform.IsChildOfは自分自身でもtrue)。</summary>
        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                if (roots[i] != null && t.IsChildOf(roots[i])) return true;
            }
            return false;
        }

        /// <summary>マテリアルが参照する全テクスチャ(シェーダーの全テクスチャプロパティ+mainTexture)を重複なしで返す。</summary>
        private static List<Texture> GetMaterialTextures(Material material, Dictionary<Material, List<Texture>> cache)
        {
            List<Texture> textures;
            if (cache.TryGetValue(material, out textures)) return textures;

            textures = new List<Texture>();
            try
            {
                Texture main = material.mainTexture;
                if (main != null) textures.Add(main);

                string[] propertyNames = material.GetTexturePropertyNames();
                if (propertyNames != null)
                {
                    foreach (string propertyName in propertyNames)
                    {
                        Texture tex = material.GetTexture(propertyName);
                        if (tex != null && !textures.Contains(tex)) textures.Add(tex);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format("[RARA QuestConverter] マテリアル '{0}' のテクスチャ列挙に失敗しました: {1}", material.name, ex.Message));
            }
            cache[material] = textures;
            return textures;
        }

        // ================================================================
        // テクスチャ見積もり
        // ================================================================

        /// <summary>
        /// テクスチャ1枚のAndroidビルドサイズを見積もる。
        /// アセットパスやTextureImporterが無い(実行時生成等)場合はProfiler計測値で概算し、その旨をラベルに残す。
        /// planByGuid(縮小計画)に該当エントリがあれば実効サイズをその目標値で頭打ちにし、
        /// ラベルへ「縮小予定: {target}px」を付ける(元インポート設定は変更しない)。
        /// 失敗しても例外は投げず、ラベルに失敗内容を残した0MBの行を返す。
        /// </summary>
        private static TextureWork EstimateTexture(Texture texture, Dictionary<string, int> planByGuid)
        {
            var work = new TextureWork
            {
                info = new TextureSizeInfo
                {
                    texture = texture,
                    assetPath = string.Empty,
                    formatLabel = string.Empty,
                },
            };
            TextureSizeInfo info = work.info;

            try
            {
                int texWidth = Mathf.Max(1, texture.width);
                int texHeight = Mathf.Max(1, texture.height);
                string path = AssetDatabase.GetAssetPath(texture);
                info.assetPath = path ?? string.Empty;
                var importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;

                if (importer == null)
                {
                    // 実行時生成テクスチャやTextureImporter以外のアセット(RenderTexture等)はProfiler計測値で概算
                    long runtimeBytes = Profiler.GetRuntimeMemorySizeLong(texture);
                    info.currentAndroidMaxSize = Mathf.Max(texWidth, texHeight);
                    info.hasAndroidOverride = false;
                    info.memoryMB = runtimeBytes / 1048576f;
                    info.downloadMB = info.memoryMB * BundleCompressionFactor;
                    info.formatLabel = "インポート設定なし(Profiler計測による概算)";
                    work.hasImporter = false;
                    work.bitsPerPixel = 32f;
                    work.effectiveWidth = texWidth;
                    work.effectiveHeight = texHeight;
                    return work;
                }

                // アクティブビルドターゲット(通常はPC)のインポート結果ではなくソース画像の元サイズで見積もる
                // (PC側のmaxTextureSizeがAndroid上書きより小さい場合に実効サイズを過小評価しないため)
                GetSourceTextureSize(importer, texture, out texWidth, out texHeight);

                TextureImporterPlatformSettings androidSettings = importer.GetPlatformTextureSettings("Android");
                bool hasOverride = androidSettings != null && androidSettings.overridden;
                info.hasAndroidOverride = hasOverride;
                int maxSize = hasOverride ? androidSettings.maxTextureSize : importer.maxTextureSize;
                info.currentAndroidMaxSize = maxSize;

                string label;
                bool isHighQualityAstc;
                float bitsPerPixel;
                if (hasOverride)
                {
                    bitsPerPixel = FormatToBitsPerPixel(androidSettings.format, out label, out isHighQualityAstc);
                }
                else
                {
                    // Android上書きなし: VRChatのAndroidビルドはASTC既定のため、実効的にASTC 6x6相当と仮定する
                    bitsPerPixel = FormatToBitsPerPixel(TextureImporterFormat.ASTC_6x6, out label, out isHighQualityAstc);
                    label += " (Android上書きなし・既定想定)";
                }
                info.formatLabel = label;
                work.hasImporter = true;
                work.bitsPerPixel = bitsPerPixel;
                work.isHighQualityAstc = isHighQualityAstc;

                // 縮小計画があれば実効サイズを目標値で頭打ちにする(変換時に縮小コピーが生成される前提)。
                // 計画値が現在の実効サイズ以上(縮小効果なし)の場合はラベルも付けない。
                int planTarget = GetPlanTarget(planByGuid, path);
                if (planTarget > 0 && planTarget < maxSize && planTarget < Mathf.Max(texWidth, texHeight))
                {
                    maxSize = planTarget;
                    info.formatLabel += " / 縮小予定: " + planTarget + "px";
                }

                int effectiveWidth, effectiveHeight;
                ComputeEffectiveSize(texWidth, texHeight, maxSize, out effectiveWidth, out effectiveHeight);
                work.effectiveWidth = effectiveWidth;
                work.effectiveHeight = effectiveHeight;

                info.memoryMB = effectiveWidth * effectiveHeight * (bitsPerPixel / 8f) * MipmapFactor / 1048576f;
                info.downloadMB = info.memoryMB * BundleCompressionFactor;
            }
            catch (Exception ex)
            {
                // 個別テクスチャの失敗は0MBの注記行として残す
                info.formatLabel = "見積もり失敗: " + ex.Message;
                info.downloadMB = 0f;
                info.memoryMB = 0f;
                work.hasImporter = false;
            }
            return work;
        }

        /// <summary>
        /// インポート形式ごとのビット/ピクセルと表示ラベルを返す。
        /// 未対応・非圧縮系は32bppとして扱い、ラベルにその旨を残す。
        /// (変換側の縮小コピー形式決定 MaterialQuestConverter.GetDownscaleCopyFormat からも使う)
        /// </summary>
        internal static float FormatToBitsPerPixel(TextureImporterFormat format, out string label, out bool isHighQualityAstc)
        {
            isHighQualityAstc = false;
            switch (format)
            {
                case TextureImporterFormat.ASTC_4x4: label = "ASTC 4x4"; isHighQualityAstc = true; return 8f;
                case TextureImporterFormat.ASTC_5x5: label = "ASTC 5x5"; isHighQualityAstc = true; return 5.12f;
                case TextureImporterFormat.ASTC_6x6: label = "ASTC 6x6"; return Astc6x6BitsPerPixel;
                case TextureImporterFormat.ASTC_8x8: label = "ASTC 8x8"; return 2f;
                case TextureImporterFormat.ASTC_10x10: label = "ASTC 10x10"; return 1.28f;
                case TextureImporterFormat.ASTC_12x12: label = "ASTC 12x12"; return 0.89f;
                case TextureImporterFormat.ETC2_RGBA8: label = "ETC2 RGBA8"; return 8f;
                case TextureImporterFormat.ETC2_RGBA8Crunched: label = "ETC2 RGBA8 (Crunch)"; return 8f;
                case TextureImporterFormat.ETC2_RGB4: label = "ETC2 RGB4"; return 4f;
                case TextureImporterFormat.ETC_RGB4: label = "ETC RGB4"; return 4f;
                case TextureImporterFormat.ETC_RGB4Crunched: label = "ETC RGB4 (Crunch)"; return 4f;
                case TextureImporterFormat.DXT1: label = "DXT1(Android非推奨)"; return 4f;
                case TextureImporterFormat.DXT5: label = "DXT5(Android非推奨)"; return 8f;
                case TextureImporterFormat.Alpha8: label = "Alpha8(非圧縮)"; return 8f;
                case TextureImporterFormat.RGBA16: label = "RGBA16(非圧縮)"; return 16f;
                case TextureImporterFormat.RGB24: label = "RGB24(非圧縮)"; return 24f;
                case TextureImporterFormat.RGBA32: label = "RGBA32(非圧縮)"; return 32f;
                case TextureImporterFormat.Automatic: label = "自動(ASTC 6x6相当と仮定)"; return Astc6x6BitsPerPixel;
                default: label = format + "(不明・32bpp想定)"; return 32f;
            }
        }

        /// <summary>
        /// maxTextureSize適用後の実効サイズを計算する(アスペクト比を保ったまま長辺をmaxSizeへ収める。
        /// TextureBaker.ComputeBakeSize と同じ流儀)。
        /// </summary>
        private static void ComputeEffectiveSize(int srcWidth, int srcHeight, int maxSize, out int width, out int height)
        {
            float scale = Mathf.Min(1f, (float)Mathf.Max(1, maxSize) / Mathf.Max(srcWidth, srcHeight));
            width = Mathf.Max(1, Mathf.RoundToInt(srcWidth * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(srcHeight * scale));
        }

        /// <summary>実効サイズを newMaxSize 上限で縮小し、圧縮形式を bitsPerPixel 相当へ変更した場合のダウンロードサイズを再計算する(提案の削減量算出用)。</summary>
        private static float RecalculateDownloadMB(TextureWork work, int newMaxSize, float bitsPerPixel)
        {
            return ComputeDownloadMB(work.effectiveWidth, work.effectiveHeight, newMaxSize, bitsPerPixel);
        }

        /// <summary>元サイズ srcWidth×srcHeight を maxSize 上限へ収めた場合のダウンロードサイズを見積もる(EstimateTextureと同じ式)。</summary>
        private static float ComputeDownloadMB(int srcWidth, int srcHeight, int maxSize, float bitsPerPixel)
        {
            int width, height;
            ComputeEffectiveSize(srcWidth, srcHeight, maxSize, out width, out height);
            float memoryMB = width * height * (bitsPerPixel / 8f) * MipmapFactor / 1048576f;
            return memoryMB * BundleCompressionFactor;
        }

        /// <summary>
        /// ソーステクスチャの元サイズを取得する(アクティブビルドターゲットのインポート結果に依存しない)。
        /// 対応する TextureImporter のAPIは internal のためリフレクションで呼び、
        /// 取得できない場合はロード済みテクスチャのサイズへフォールバックする(常に1以上を返す)。
        /// (変換側の縮小判定 MaterialQuestConverter.GetEffectiveAndroidSize からも使う)
        /// </summary>
        internal static void GetSourceTextureSize(TextureImporter importer, Texture texture, out int width, out int height)
        {
            width = Mathf.Max(1, texture != null ? texture.width : 1);
            height = Mathf.Max(1, texture != null ? texture.height : 1);
            if (importer == null) return;
            try
            {
                var method = typeof(TextureImporter).GetMethod("GetSourceTextureWidthAndHeight",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? typeof(TextureImporter).GetMethod("GetWidthAndHeight",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (method == null) return;
                object[] args = { 0, 0 };
                method.Invoke(importer, args);
                int w = (int)args[0];
                int h = (int)args[1];
                if (w > 0 && h > 0)
                {
                    width = w;
                    height = h;
                }
            }
            catch
            {
                // 取得失敗時はロード済みサイズのまま(従来の挙動)
            }
        }

        // ================================================================
        // 低ディテール検出(単色・低ディテールなテクスチャの極端な縮小)
        // ================================================================

        /// <summary>キャッシュ未生成なら生成する。</summary>
        private static void EnsureDetailCache()
        {
            if (_detailCapCache == null) _detailCapCache = new Dictionary<int, int>();
        }

        /// <summary>
        /// 低ディテール解析キャッシュ(_detailCapCache)を破棄する。キャッシュはinstanceID→内容由来の推奨上限で、
        /// テクスチャ内容の変化を検知しない。見積もり・アトラス生成の各実行(Estimate / BuildAtlases)の冒頭で
        /// 呼び、実行内では共有テクスチャの再解析を避けつつ、実行間では再インポート後の内容を反映し直す。
        /// </summary>
        public static void InvalidateDetailCache()
        {
            if (_detailCapCache != null) _detailCapCache.Clear();
        }

        /// <summary>
        /// テクスチャの内容を解析し、見た目の劣化がほぼ無い範囲で許容できる最大長辺(px)を返す
        /// (settings.aggressiveTextureReduction 用)。単色・低ディテールなテクスチャほど小さい値を返す。
        ///
        /// 【手法】元テクスチャは一切変更しない。一時RenderTexture(DetailSampleSize×DetailSampleSize)へ
        /// Blitした読み取り用コピーを ReadPixels → GetPixels32 して解析する(estimatorの既定RWパターン=
        /// リニアプロジェクトではsRGB。非可読テクスチャ・圧縮テクスチャでも安全に読める)。使い終えた
        /// 一時RT・一時Texture2Dは必ず解放・破棄する。
        /// ・distinct: 5bit/チャンネルへ量子化した色の種類数(DetailSampleSize角へ縮小後)。
        /// ・variance: 輝度(0..1)の分散。
        ///
        /// 【分類(保守的: 明らかに低ディテールなときだけ縮小する)】
        ///   distinct<=2 または variance<DetailSolidVariance         → 16px(単色・極フラット)
        ///   distinct<=8 かつ variance<DetailNearSolidVariance       → 32px(準単色)
        ///   variance<DetailLowVariance かつ distinct<=DetailLowMaxDistinctColors → min(128, currentSize)(低ディテール)
        ///   それ以外                                                → currentSize(縮小しない)
        /// 返り値は currentSize を超えず、DetailMinSize(16px)を下回らない。有効な縮小が無ければ
        /// currentSize をそのまま返す。しきい値はいずれも保守的で、通常のディテールを持つテクスチャは縮小しない。
        ///
        /// Texture2D 以外(RenderTexture/Cubemap 等)やGPU読み取り失敗時は currentSize を返す(スキップ)。
        /// 結果は tex.GetInstanceID() でキャッシュする(同一テクスチャの再解析を避ける)。
        /// null安全。例外は投げず、失敗時は currentSize を返す。
        /// </summary>
        public static int AnalyzeDetailSize(Texture tex, int currentSize)
        {
            if (tex == null) return currentSize;

            EnsureDetailCache();

            // Texture2D 以外は解析しない(RenderTextureは動的、Cubemapは6面のため2Dへ展開すると歪む)
            Texture2D tex2D = tex as Texture2D;
            if (tex2D == null) return currentSize;

            int detailCap;
            int id = tex.GetInstanceID();
            if (!_detailCapCache.TryGetValue(id, out detailCap))
            {
                detailCap = ComputeDetailCap(tex2D);
                _detailCapCache[id] = detailCap;
            }

            // ディテール由来の上限を currentSize で頭打ちにする。currentSize以上(=縮小効果なし)ならそのまま。
            if (detailCap >= currentSize) return currentSize;
            int result = Mathf.Max(DetailMinSize, detailCap);
            return result < currentSize ? result : currentSize; // currentSizeを超えない・下回らない(16px未満は返さない)
        }

        /// <summary>
        /// テクスチャ内容から「ディテール由来の推奨最大長辺(px)」を求める(currentSizeでの頭打ち前の生値)。
        /// 十分なディテールがある(=縮小不要な)場合、および読み取り失敗時は int.MaxValue を返す。
        /// 元テクスチャは変更しない(一時RT+一時Texture2Dのみ使用し、finallyで確実に解放・破棄する)。
        /// </summary>
        private static int ComputeDetailCap(Texture2D tex2D)
        {
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D readable = null;
            try
            {
                // 既定RW(リニアプロジェクトではsRGB)でBlit。読み取り用コピーを作るだけで元テクスチャは変更しない。
                rt = RenderTexture.GetTemporary(DetailSampleSize, DetailSampleSize);
                Graphics.Blit(tex2D, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(DetailSampleSize, DetailSampleSize, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, DetailSampleSize, DetailSampleSize), 0, 0);
                readable.Apply(false, false);

                Color32[] pixels = readable.GetPixels32();
                if (pixels == null || pixels.Length == 0) return int.MaxValue;

                var distinctColors = new HashSet<int>();
                double sum = 0.0;
                double sumSq = 0.0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    // 5bit/チャンネルへ量子化(各0..31)して色の種類を数える(微小ノイズを吸収する)
                    int key = (p.r >> 3) | ((p.g >> 3) << 5) | ((p.b >> 3) << 10);
                    distinctColors.Add(key);
                    double lum = (0.299 * p.r + 0.587 * p.g + 0.114 * p.b) / 255.0; // 輝度(0..1)
                    sum += lum;
                    sumSq += lum * lum;
                }
                int n = pixels.Length;
                double mean = sum / n;
                double variance = sumSq / n - mean * mean;
                if (variance < 0.0) variance = 0.0; // 丸め誤差対策
                int distinct = distinctColors.Count;

                if (distinct <= 2 || variance < DetailSolidVariance) return 16;         // 単色・極フラット
                if (distinct <= 8 && variance < DetailNearSolidVariance) return 32;     // 準単色
                if (variance < DetailLowVariance && distinct <= DetailLowMaxDistinctColors) return 128; // 低ディテール(彩度ディテールが多い場合は縮小しない)
                return int.MaxValue;                                                    // 十分ディテールあり → 縮小しない
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format(
                    "[RARA QuestConverter] テクスチャ '{0}' の低ディテール解析に失敗したため縮小提案をスキップします: {1}",
                    tex2D != null ? tex2D.name : "(不明)", ex.Message));
                return int.MaxValue;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        // ================================================================
        // メッシュ見積もり
        // ================================================================

        /// <summary>
        /// メッシュ1個のビルドサイズを見積もる。
        /// 頂点あたり 位置12+法線12+接線16+UV8(+頂点色4+スキンウェイト32)バイト、
        /// ブレンドシェイプは1個・1頂点あたり約10バイト(デルタ圧縮込み)、
        /// インデックスは三角形あたり6バイト(16bit)/12バイト(32bit)として概算する。
        /// </summary>
        private static MeshWork EstimateMesh(Mesh mesh, bool skinned)
        {
            var work = new MeshWork { skinned = skinned };
            try
            {
                int vertexCount = mesh.vertexCount;
                int stride = 12 + 12 + 16 + 8; // 位置+法線+接線+UV
                if (mesh.HasVertexAttribute(VertexAttribute.Color)) stride += 4;
                if (skinned) stride += 32; // ボーンウェイト+ボーンインデックス

                long indexCount = 0;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    indexCount += mesh.GetIndexCount(i);
                }
                long triangleCount = indexCount / 3;
                int bytesPerTriangle = mesh.indexFormat == IndexFormat.UInt32 ? 12 : 6;

                long blendShapeBytes = (long)mesh.blendShapeCount * vertexCount * BlendShapeBytesPerVertex;
                long totalBytes = (long)vertexCount * stride + blendShapeBytes + triangleCount * bytesPerTriangle;

                work.downloadMB = totalBytes * MeshCompressionFactor / 1048576f;
                work.blendShapeMB = blendShapeBytes * MeshCompressionFactor / 1048576f;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format("[RARA QuestConverter] メッシュ '{0}' の見積もりに失敗しました: {1}", mesh != null ? mesh.name : "(不明)", ex.Message));
                work.downloadMB = 0f;
                work.blendShapeMB = 0f;
            }
            return work;
        }

        // ================================================================
        // アニメーション見積もり
        // ================================================================

        /// <summary>
        /// VRCAvatarDescriptorのアニメーションレイヤー(Base/Special)の全クリップから
        /// ダウンロードサイズを概算する。Packages/配下(SDK同梱プロキシ等)のクリップは
        /// 共有・軽量のため集計から除外する。
        /// </summary>
        private static float EstimateAnimationDownloadMB(GameObject avatarRoot, SizeEstimateResult result)
        {
            float totalMB = 0f;
            try
            {
                VRCAvatarDescriptor descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
                if (descriptor == null) descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (descriptor == null) return 0f;

                var seenControllers = new HashSet<RuntimeAnimatorController>();
                var seenClips = new HashSet<AnimationClip>();
                CollectLayerClips(descriptor.baseAnimationLayers, seenControllers, seenClips);
                CollectLayerClips(descriptor.specialAnimationLayers, seenControllers, seenClips);

                foreach (AnimationClip clip in seenClips)
                {
                    try
                    {
                        string path = AssetDatabase.GetAssetPath(clip);
                        if (!string.IsNullOrEmpty(path) && path.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                        totalMB += Profiler.GetRuntimeMemorySizeLong(clip) * AnimationDownloadFactor / 1048576f;
                    }
                    catch (Exception)
                    {
                        // 個別クリップの失敗は無視(概算のため)
                    }
                }
            }
            catch (Exception ex)
            {
                result.suggestions.Add(Note("アニメーションの見積もりに失敗しました: " + ex.Message));
            }
            return totalMB;
        }

        /// <summary>CustomAnimLayer配列からコントローラーと全クリップを重複なしで集める。</summary>
        private static void CollectLayerClips(VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<RuntimeAnimatorController> seenControllers, HashSet<AnimationClip> seenClips)
        {
            if (layers == null) return;
            foreach (VRCAvatarDescriptor.CustomAnimLayer layer in layers)
            {
                RuntimeAnimatorController controller = layer.animatorController;
                if (controller == null || !seenControllers.Add(controller)) continue;

                AnimationClip[] clips = controller.animationClips;
                if (clips == null) continue;
                foreach (AnimationClip clip in clips)
                {
                    if (clip != null) seenClips.Add(clip);
                }
            }
        }

        // ================================================================
        // 削減提案
        // ================================================================

        /// <summary>
        /// 削減提案を構築して result.suggestions へ追加する(削減量降順。最後の1件は常に汎用アドバイス)。
        /// </summary>
        private static void BuildSuggestions(GameObject avatarRoot, QuestConvertSettings settings, SizeEstimateResult result,
            List<Renderer> keptRenderers,
            Dictionary<Renderer, HashSet<Texture>> rendererTextures,
            Dictionary<Texture, TextureWork> textureWorks,
            Dictionary<Renderer, Mesh> rendererMeshes,
            Dictionary<Mesh, MeshWork> meshWorks,
            float blendShapeTotalMB)
        {
            var suggestions = new List<SizeSuggestion>();

            // ---- (1) テクスチャの縮小提案(適用は縮小計画への登録) ----
            // 基準サイズ: 設定のmaxTextureSize(ただし1024以下に丸める)。超過分に縮小を提案する。
            // 実効サイズは縮小計画適用後の値のため、計画済みテクスチャへ同じ提案は再度出ない。
            // 「適用」で登録された計画は変換時の縮小コピー生成(原則 settings.androidFormat)に使われるため、
            // 削減量も同形式のbppで再計算する(表示した削減量と生成コピーの実サイズを一致させる)。
            // aggressiveTextureReduction が有効な場合は AnalyzeDetailSize で内容を解析し、単色・低ディテールな
            // テクスチャには汎用のサイズ超過提案より小さい「単色/低ディテール」提案を出す(小さくなる方を採用し、
            // 汎用提案との二重提案はしない)。適用経路は同じ縮小計画のため、変換時の縮小コピーで反映される。
            int baseLimit = settings.maxTextureSize > 0 ? Mathf.Min(settings.maxTextureSize, 1024) : 1024;
            string applyFormatLabel;
            bool applyFormatHighQuality;
            float applyBitsPerPixel = FormatToBitsPerPixel(settings.androidFormat, out applyFormatLabel, out applyFormatHighQuality);
            foreach (KeyValuePair<Texture, TextureWork> pair in textureWorks)
            {
                TextureWork work = pair.Value;
                if (!work.hasImporter) continue; // インポート設定が無いテクスチャにはサイズ制限を提案できない

                int currentDims = Mathf.Max(work.effectiveWidth, work.effectiveHeight);

                // 汎用のサイズ超過提案(従来どおり: baseLimit超過、または上限超過見込みで512超)
                int genericRec = 0;
                if (currentDims > baseLimit)
                {
                    genericRec = baseLimit;
                }
                else if (result.overCap && currentDims > 512)
                {
                    // 既に1024以下でも上限超過が見込まれる場合は512を提案
                    genericRec = 512;
                }

                // 単色・低ディテール提案(aggressiveTextureReduction時のみ内容を解析して極端な縮小を提案)
                int detailRec = 0;
                if (settings.aggressiveTextureReduction && pair.Key != null)
                {
                    int detail = AnalyzeDetailSize(pair.Key, currentDims);
                    if (detail > 0 && detail < currentDims) detailRec = detail;
                }

                // 実効的により小さくなる方を採用(低ディテール提案が小さければそちらを優先。汎用と二重提案しない)
                bool useDetail = detailRec > 0 && (genericRec <= 0 || detailRec < genericRec);
                int recommended = useDetail ? detailRec : genericRec;
                if (recommended <= 0 || recommended >= currentDims) continue;

                float saving = work.info.downloadMB - RecalculateDownloadMB(work, recommended, applyBitsPerPixel);
                if (saving < SuggestionMinSavingMB) continue;

                string texName = pair.Key != null ? pair.Key.name : "(不明)";
                string description = useDetail
                    ? string.Format("単色/低ディテール: 『{0}』を {1}px に縮小可能(変換時に縮小コピーを生成)", texName, recommended)
                    : string.Format("『{0}』を変換時に{1}pxへ縮小する(縮小コピーを生成)", texName, recommended);

                suggestions.Add(new SizeSuggestion
                {
                    description = description,
                    savingMB = saving,
                    texture = pair.Key,
                    recommendedMaxSize = recommended,
                });
            }

            // ---- (2) 圧縮形式の見直し(ASTC 4x4/5x5 → 6x6、上書きなし → 明示設定) ----
            float formatSaving = 0f;
            int highQualityCount = 0;
            int noOverrideCount = 0;
            foreach (TextureWork work in textureWorks.Values)
            {
                if (!work.hasImporter) continue;
                if (work.isHighQualityAstc && work.bitsPerPixel > 0f)
                {
                    highQualityCount++;
                    formatSaving += work.info.downloadMB * (1f - Astc6x6BitsPerPixel / work.bitsPerPixel);
                }
                else if (!work.info.hasAndroidOverride)
                {
                    noOverrideCount++;
                }
            }
            if (highQualityCount > 0 || noOverrideCount > 0)
            {
                suggestions.Add(new SizeSuggestion
                {
                    description = string.Format(
                        "テクスチャの圧縮形式をASTC 6x6/8x8へ見直す(ASTC 4x4/5x5: {0} 件 / Android上書きなし: {1} 件。上書きなしはASTC 6x6相当として集計済みのため、明示設定を推奨)",
                        highQualityCount, noOverrideCount),
                    savingMB = formatSaving,
                    texture = null,
                    recommendedMaxSize = 0,
                });
            }

            // ---- (3) サブツリーのQuest除外(EditorOnly)提案 ----
            // 候補: avatarRoot直下の子と、その直下(Armatureと並ぶメッシュグループ配下)まで。
            // 親を提案済みならその子は重複提案しない。候補処理順は親→子の順になっている。
            //
            // 【保護】素体・顔・髪・ビセーム・まぶた・支配的メッシュを含むサブツリーは、除外すると
            // アバターの見た目が壊れる(素体が消える等)ため、削減量で上位に並ぶ actionable な除外提案には
            // しない。代わりに「(参考) 大型メッシュ: 除外は非推奨」の情報行として末尾へ回す(適用ボタンなし・
            // savingMB=0 で削減合計にも算入しない。両ウィンドウとも texture!=null かつ recommendedMaxSize>0 の
            // 提案だけを適用対象にするため、この情報行が一括適用へ混入することはない)。
            HashSet<Renderer> protectedRenderers = CollectProtectedRenderers(avatarRoot, keptRenderers, rendererMeshes);
            var referenceSuggestions = new List<SizeSuggestion>();

            var candidates = new List<Transform>();
            foreach (Transform child in avatarRoot.transform)
            {
                candidates.Add(child);
                foreach (Transform grandChild in child)
                {
                    candidates.Add(grandChild);
                }
            }
            var suggestedRoots = new List<Transform>();
            foreach (Transform candidate in candidates)
            {
                if (IsUnderAny(candidate, suggestedRoots)) continue;

                var insideRenderers = new List<Renderer>();
                var outsideRenderers = new List<Renderer>();
                foreach (Renderer renderer in keptRenderers)
                {
                    if (renderer == null) continue;
                    if (renderer.transform.IsChildOf(candidate)) insideRenderers.Add(renderer);
                    else outsideRenderers.Add(renderer);
                }
                if (insideRenderers.Count == 0) continue;

                // サブツリー外の残存レンダラーが使うテクスチャ・メッシュは、除外しても削減にならない
                var outsideTextures = new HashSet<Texture>();
                var outsideMeshes = new HashSet<Mesh>();
                foreach (Renderer renderer in outsideRenderers)
                {
                    HashSet<Texture> textures;
                    if (rendererTextures.TryGetValue(renderer, out textures)) outsideTextures.UnionWith(textures);
                    Mesh mesh;
                    if (rendererMeshes.TryGetValue(renderer, out mesh)) outsideMeshes.Add(mesh);
                }

                float saving = 0f;
                var countedTextures = new HashSet<Texture>();
                var countedMeshes = new HashSet<Mesh>();
                foreach (Renderer renderer in insideRenderers)
                {
                    HashSet<Texture> textures;
                    if (rendererTextures.TryGetValue(renderer, out textures))
                    {
                        foreach (Texture tex in textures)
                        {
                            if (outsideTextures.Contains(tex) || !countedTextures.Add(tex)) continue;
                            TextureWork work;
                            if (textureWorks.TryGetValue(tex, out work)) saving += work.info.downloadMB;
                        }
                    }
                    Mesh mesh;
                    if (rendererMeshes.TryGetValue(renderer, out mesh))
                    {
                        if (!outsideMeshes.Contains(mesh) && countedMeshes.Add(mesh))
                        {
                            MeshWork meshWork;
                            if (meshWorks.TryGetValue(mesh, out meshWork)) saving += meshWork.downloadMB;
                        }
                    }
                }
                if (countedTextures.Count == 0) continue; // このサブツリー専用のテクスチャが無ければ提案しない
                if (saving < SubtreeSuggestionMinMB) continue;

                suggestedRoots.Add(candidate); // 保護・非保護を問わず、以降このサブツリー配下の子は重複処理しない

                // このサブツリーが素体・顔・髪・ビセーム・まぶた・支配的メッシュを含むなら除外は非推奨。
                // 削減量が大きくても actionable な提案にはせず、参考情報の行として末尾へ回す。
                bool guarded = false;
                foreach (Renderer renderer in insideRenderers)
                {
                    if (protectedRenderers.Contains(renderer)) { guarded = true; break; }
                }

                string relativePath = QuestCompat.GetRelativePath(avatarRoot.transform, candidate);
                if (guarded)
                {
                    referenceSuggestions.Add(new SizeSuggestion
                    {
                        description = string.Format(
                            "(参考) 大型メッシュ 『{0}』(約{1:F1}MB): 表示に必要な本体・顔・髪・表情メッシュを含むため除外は非推奨",
                            relativePath, saving),
                        savingMB = 0f,             // 削減合計へ算入しない(参考情報)。降順ソートでも最下位に来る
                        texture = null,            // 適用ボタンを出さない・一括適用の対象にもしない
                        recommendedMaxSize = 0,
                    });
                }
                else
                {
                    suggestions.Add(new SizeSuggestion
                    {
                        description = string.Format("『{0}』をQuest除外(EditorOnly)にする", relativePath),
                        savingMB = saving,
                        texture = null,
                        recommendedMaxSize = 0,
                    });
                }
            }

            // ---- (4) ブレンドシェイプが支配的な場合の汎用提案 ----
            if (blendShapeTotalMB >= 0.5f && result.meshDownloadMB > 0f && blendShapeTotalMB >= result.meshDownloadMB * 0.5f)
            {
                suggestions.Add(new SizeSuggestion
                {
                    description = string.Format(
                        "ブレンドシェイプがメッシュ容量の大部分(約{0:F1}MB)を占めています。AAO(Avatar Optimizer)のブレンドシェイプ固定・未使用削除が有効です",
                        blendShapeTotalMB),
                    savingMB = blendShapeTotalMB * 0.5f, // 半分程度削減できると仮定した目安
                    texture = null,
                    recommendedMaxSize = 0,
                });
            }

            // 削減量降順に並べ、参考情報(大型の保護メッシュ)→末尾に汎用アドバイスの順で固定追加する。
            // 参考情報は actionable な提案をすべて並べた後・汎用アドバイスの前に置く(常に最下位側)。
            suggestions.Sort((a, b) => b.savingMB.CompareTo(a.savingMB));
            suggestions.AddRange(referenceSuggestions);
            suggestions.Add(new SizeSuggestion
            {
                description = "AAOのTrace and Optimizeによるテクスチャ統合・メッシュ結合も削減に有効(ビルド時自動)",
                savingMB = 0f,
                texture = null,
                recommendedMaxSize = 0,
            });
            result.suggestions.AddRange(suggestions);
        }

        // ================================================================
        // Quest除外(EditorOnly)提案の保護判定ヘルパー
        // ================================================================

        /// <summary>
        /// Quest除外(EditorOnly)提案から保護すべき(=除外すると見た目が壊れる)レンダラーを集める。
        /// ・VRCAvatarDescriptor.VisemeSkinnedMesh(リップシンクの口メッシュ)
        /// ・customEyeLookSettings.eyelidsSkinnedMesh(まばたきのまぶたメッシュ)
        /// ・顔・素体の名前トークン(FaceNameTokens / BodyNameTokens)一致(GameObject名・メッシュ名・マテリアル名)
        /// ・髪(QuestCompat.IsHairLikeName)
        /// ・三角形シェアがアバター総三角形数の DominantTriangleShare(~30%)超の支配的メッシュ
        /// いずれかに該当するレンダラーを含むサブツリーは actionable な除外提案にしない。
        /// </summary>
        private static HashSet<Renderer> CollectProtectedRenderers(
            GameObject avatarRoot, List<Renderer> keptRenderers, Dictionary<Renderer, Mesh> rendererMeshes)
        {
            var protectedSet = new HashSet<Renderer>();
            if (keptRenderers == null) return protectedSet;

            // (a) デスクリプター参照(ビセーム口メッシュ・まぶたメッシュ)。表情・アイトラッキングの要。
            VRCAvatarDescriptor descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                if (descriptor.VisemeSkinnedMesh != null) protectedSet.Add(descriptor.VisemeSkinnedMesh);
                SkinnedMeshRenderer eyelids = descriptor.customEyeLookSettings.eyelidsSkinnedMesh;
                if (eyelids != null) protectedSet.Add(eyelids);
            }

            // (b) 支配的メッシュ判定用にアバター総三角形数を求める(kept レンダラーの共有メッシュ合計)。
            long totalTriangles = 0;
            foreach (Renderer renderer in keptRenderers)
            {
                if (renderer == null) continue;
                Mesh mesh;
                if (rendererMeshes != null && rendererMeshes.TryGetValue(renderer, out mesh))
                {
                    totalTriangles += GetMeshTriangleCount(mesh);
                }
            }

            // (c) 名前トークン(顔・素体・髪)一致、または (d) 支配的メッシュを保護対象に加える。
            foreach (Renderer renderer in keptRenderers)
            {
                if (renderer == null || protectedSet.Contains(renderer)) continue;
                Mesh mesh = null;
                if (rendererMeshes != null) rendererMeshes.TryGetValue(renderer, out mesh);

                if (IsProtectedByName(renderer, mesh))
                {
                    protectedSet.Add(renderer);
                    continue;
                }
                if (totalTriangles > 0 && mesh != null)
                {
                    long tris = GetMeshTriangleCount(mesh);
                    if ((float)tris / totalTriangles > DominantTriangleShare) protectedSet.Add(renderer);
                }
            }
            return protectedSet;
        }

        /// <summary>
        /// レンダラーが顔・素体・髪メッシュを示す名前を持つか(GameObject名・メッシュ名・マテリアル名)。
        /// 顔・素体は FaceNameTokens / BodyNameTokens の部分一致、髪は QuestCompat.IsHairLikeName で判定する。
        /// </summary>
        private static bool IsProtectedByName(Renderer renderer, Mesh mesh)
        {
            string goName = renderer.gameObject.name;
            string meshName = mesh != null ? mesh.name : null;

            if (ContainsAnyToken(goName, FaceNameTokens) || ContainsAnyToken(meshName, FaceNameTokens)) return true;
            if (ContainsAnyToken(goName, BodyNameTokens) || ContainsAnyToken(meshName, BodyNameTokens)) return true;
            if (QuestCompat.IsHairLikeName(goName)) return true;
            if (meshName != null && QuestCompat.IsHairLikeName(meshName)) return true;

            Material[] materials = renderer.sharedMaterials;
            if (materials != null)
            {
                foreach (Material material in materials)
                {
                    if (material == null) continue;
                    if (ContainsAnyToken(material.name, BodyNameTokens)) return true; // Body_skin_NT 等の素体マテリアル
                    if (QuestCompat.IsHairLikeName(material.name)) return true;
                }
            }
            return false;
        }

        /// <summary>name が tokens のいずれかを部分一致(大文字小文字を区別しない)で含むか。null・空は false。</summary>
        private static bool ContainsAnyToken(string name, string[] tokens)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string token in tokens)
            {
                if (!string.IsNullOrEmpty(token) && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>メッシュの総三角形数(全サブメッシュのインデックス数合計 ÷ 3)。null は 0。</summary>
        private static long GetMeshTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                total += mesh.GetIndexCount(i);
            }
            return total / 3;
        }

        // ================================================================
        // 削減提案の一括適用・予算内サイズ調整(Budget Fit)
        // ================================================================

        /// <summary>内部作業用: 予算内サイズ調整の縮小候補1件。</summary>
        private class BudgetFitCandidate
        {
            public Texture texture;
            public string assetPath;
            /// <summary>元テクスチャの幅(インポート後)。</summary>
            public int texWidth;
            /// <summary>元テクスチャの高さ(インポート後)。</summary>
            public int texHeight;
            /// <summary>計画前の実効Android最大サイズ(既存の縮小計画があればその値)。</summary>
            public int originalMaxSize;
            /// <summary>計画適用後の実効Android最大サイズ(半減のたびに更新)。</summary>
            public int currentMaxSize;
            /// <summary>計画前のダウンロードサイズ目安(MB)。</summary>
            public float originalDownloadMB;
            /// <summary>計画適用後のダウンロードサイズ目安(MB)。</summary>
            public float currentDownloadMB;
            /// <summary>下限(BudgetFitMinSize)まで縮小した場合のダウンロードサイズ目安(MB)。</summary>
            public float minPossibleDownloadMB;
            /// <summary>顔・体・肌系の優先保護テクスチャ(なるべく縮小しない)ならtrue。</summary>
            public bool isPriority;
            /// <summary>適用時の圧縮形式(既存の上書き形式の方が高効率ならそれを維持する)。</summary>
            public TextureImporterFormat applyFormat;
            /// <summary>applyFormat のビット/ピクセル(縮小後サイズの再計算に使用)。</summary>
            public float applyBitsPerPixel;

            /// <summary>まだ縮小の余地があるか(下限より大きく、縮めれば削減が見込める)。</summary>
            public bool CanShrink
            {
                get { return currentMaxSize > BudgetFitMinSize && minPossibleDownloadMB < currentDownloadMB; }
            }
        }

        /// <summary>
        /// 推定ダウンロードサイズが targetDownloadMB 以下へ収まるよう、テクスチャの
        /// 実効サイズを段階的に半減(例: 2048→1024→512→256)する計画を作る。
        /// ・現在の推定が既に目標以下なら空の計画を返す。
        /// ・候補は「アセットパス+TextureImporterがあり、現在の実効Android最大サイズ
        ///   (既存の縮小計画 settings.textureSizePlan があればその値)が256px超」のテクスチャ。
        ///   既存計画を起点にするため、繰り返し実行しても計画は収束する。
        /// ・毎回「計画適用後のダウンロードサイズが最大の候補」を半減する。ただし顔・体・肌系
        ///   (face/body/skin/顔/体/肌 の部分一致)は、同じ最大サイズ帯に他の候補が残っている間は後回しにする。
        /// ・同一テクスチャの複数回の半減は1手順へまとめる(fromSize=元、toSize=最終)。
        /// ・目標以下になるか、どの候補もこれ以上縮められなくなったら終了する。
        /// このメソッドは計画を作るだけで、インポート設定も縮小計画も一切変更しない(副作用なし・同じ入力に対して決定的)。
        /// 適用(縮小計画への登録)は ApplyBudgetFit で行う。例外は投げず、失敗時はそれまでに作れた計画を返す。
        /// </summary>
        public static List<BudgetFitStep> PlanBudgetFit(GameObject avatarRoot, QuestConvertSettings settings, float targetDownloadMB)
        {
            var plan = new List<BudgetFitStep>();
            try
            {
                if (avatarRoot == null) return plan;
                if (settings == null) settings = new QuestConvertSettings();

                SizeEstimateResult estimate = Estimate(avatarRoot, settings);
                if (estimate == null || estimate.textures == null) return plan;
                if (estimate.estimatedDownloadMB <= targetDownloadMB) return plan; // 既に予算内

                // 変換時の縮小コピーは原則 settings.androidFormat で生成されるため、
                // 縮小後のサイズは適用形式のbppで再計算する。ただし既存のAndroid上書き形式の方が
                // 高効率(bppが小さい)なテクスチャは形式維持を仮定して見積もる(候補ごとの
                // applyFormat / applyBitsPerPixel。形式の格上げで削減が0や負になるのを防ぐ)
                string applyFormatLabel;
                bool applyFormatHighQuality;
                float applyBitsPerPixel = FormatToBitsPerPixel(settings.androidFormat, out applyFormatLabel, out applyFormatHighQuality);

                List<BudgetFitCandidate> candidates = BuildBudgetFitCandidates(estimate, settings.androidFormat, applyBitsPerPixel, BuildPlanByGuid(settings));
                if (candidates.Count == 0) return plan;

                float totalMB = estimate.estimatedDownloadMB;
                var stepByTexture = new Dictionary<Texture, BudgetFitStep>();
                while (totalMB > targetDownloadMB)
                {
                    BudgetFitCandidate picked = PickBudgetFitCandidate(candidates);
                    if (picked == null) break; // どの候補もこれ以上縮められない(予算未達でも打ち切る)

                    int newSize = NextHalvedSize(picked.currentMaxSize);
                    float newDownloadMB = ComputeDownloadMB(picked.texWidth, picked.texHeight, newSize, picked.applyBitsPerPixel);
                    totalMB -= picked.currentDownloadMB - newDownloadMB;
                    picked.currentMaxSize = newSize;
                    picked.currentDownloadMB = newDownloadMB;

                    // 同一テクスチャの複数回の半減は1手順へまとめる(fromSize=元、toSize=最終)
                    BudgetFitStep step;
                    if (!stepByTexture.TryGetValue(picked.texture, out step))
                    {
                        step = new BudgetFitStep
                        {
                            texture = picked.texture,
                            assetPath = picked.assetPath,
                            fromSize = picked.originalMaxSize,
                            format = picked.applyFormat,
                        };
                        stepByTexture[picked.texture] = step;
                        plan.Add(step);
                    }
                    step.toSize = newSize;
                    step.savingMB = picked.originalDownloadMB - newDownloadMB;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA QuestConverter] 予算内サイズ調整の計画作成に失敗しました: " + ex);
            }
            return plan;
        }

        /// <summary>
        /// PlanBudgetFit の計画を縮小計画(settings.textureSizePlan)へ登録する。
        /// 元テクスチャのインポート設定は変更せず、縮小は変換時に生成される縮小コピーへ適用され、
        /// 変換後のマテリアルがそのコピーを参照する(PC版・元アバターには影響しない)。
        /// 同一テクスチャの既存エントリはGUIDで一意化し、小さい方の目標サイズを保持する。
        /// 計画を追加・更新できた件数を返す(アセット化されていない等の失敗はスキップして警告ログのみ。例外は投げない)。
        /// </summary>
        public static int ApplyBudgetFit(List<BudgetFitStep> plan, QuestConvertSettings settings)
        {
            int applied = 0;
            if (plan == null || settings == null) return applied;
            foreach (BudgetFitStep step in plan)
            {
                if (step == null || step.texture == null || step.toSize <= 0) continue;
                if (UpsertTextureSizePlan(settings, step.texture, step.toSize))
                {
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>
        /// サイズ削減提案のうちテクスチャ縮小提案(texture付き・recommendedMaxSize>0)をすべて
        /// 縮小計画(settings.textureSizePlan)へ一括登録する(ウィンドウの提案別「適用」ボタンと同じ内容)。
        /// 元テクスチャのインポート設定は変更せず、縮小は変換時に生成される縮小コピーへ適用される。
        /// 計画を追加・更新できた件数を返す(アセット化されていない等の失敗はスキップして警告ログのみ。例外は投げない)。
        /// </summary>
        public static int ApplyAllTextureSuggestions(SizeEstimateResult result, QuestConvertSettings settings)
        {
            int applied = 0;
            if (result == null || result.suggestions == null || settings == null) return applied;
            foreach (SizeSuggestion suggestion in result.suggestions)
            {
                if (suggestion == null || suggestion.texture == null || suggestion.recommendedMaxSize <= 0) continue;
                if (UpsertTextureSizePlan(settings, suggestion.texture, suggestion.recommendedMaxSize))
                {
                    applied++;
                }
            }
            return applied;
        }

        // ================================================================
        // 縮小計画(settings.textureSizePlan)のヘルパー
        // ================================================================

        /// <summary>
        /// テクスチャの縮小計画(settings.textureSizePlan)へ targetSize を登録する。
        /// 既存エントリがある場合は小さい方の目標サイズを保持する(GUIDで一意化)。
        /// 元テクスチャのインポート設定は一切変更しない。
        /// 計画が追加・縮小された場合のみtrueを返す(アセット化されていないテクスチャは警告ログを出してfalse)。
        /// </summary>
        public static bool UpsertTextureSizePlan(QuestConvertSettings settings, Texture texture, int targetSize)
        {
            if (settings == null || texture == null || targetSize <= 0) return false;

            string path = AssetDatabase.GetAssetPath(texture);
            string guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning(string.Format(
                    "[RARA QuestConverter] テクスチャ「{0}」はアセット化されていないため縮小計画に登録できません(プロジェクト内のテクスチャアセットのみ登録できます)。",
                    texture.name));
                return false;
            }
            if (settings.textureSizePlan == null) settings.textureSizePlan = new List<TextureSizePlanEntry>(); // 旧設定JSON読込対策

            foreach (TextureSizePlanEntry entry in settings.textureSizePlan)
            {
                if (entry == null || entry.textureGuid != guid) continue;
                if (entry.targetSize > 0 && entry.targetSize <= targetSize) return false; // 既により小さい(または同じ)計画がある
                entry.targetSize = targetSize;
                return true;
            }
            settings.textureSizePlan.Add(new TextureSizePlanEntry { textureGuid = guid, targetSize = targetSize });
            return true;
        }

        /// <summary>
        /// 縮小計画をGUID→目標サイズの辞書にして返す(不正エントリはスキップ、重複GUIDは最小の目標サイズを採用)。
        /// </summary>
        private static Dictionary<string, int> BuildPlanByGuid(QuestConvertSettings settings)
        {
            var plan = new Dictionary<string, int>();
            if (settings == null || settings.textureSizePlan == null) return plan;
            foreach (TextureSizePlanEntry entry in settings.textureSizePlan)
            {
                if (entry == null || string.IsNullOrEmpty(entry.textureGuid) || entry.targetSize <= 0) continue;
                int existing;
                if (!plan.TryGetValue(entry.textureGuid, out existing) || entry.targetSize < existing)
                {
                    plan[entry.textureGuid] = entry.targetSize;
                }
            }
            return plan;
        }

        /// <summary>assetPath のテクスチャに対する縮小計画の目標サイズを返す(計画なしは0)。</summary>
        private static int GetPlanTarget(Dictionary<string, int> planByGuid, string assetPath)
        {
            if (planByGuid == null || planByGuid.Count == 0 || string.IsNullOrEmpty(assetPath)) return 0;
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            int target;
            return !string.IsNullOrEmpty(guid) && planByGuid.TryGetValue(guid, out target) ? target : 0;
        }

        /// <summary>
        /// 見積もり結果から予算内サイズ調整の縮小候補を作る。
        /// 候補: アセットパスとTextureImporterがあり、見積もりに成功していて(downloadMB>0)、
        /// 現在の実効Android最大サイズ(Android上書きがあれば上書き値、なければ既定のmaxTextureSize。
        /// 実テクスチャサイズで頭打ち。既存の縮小計画 planByGuid があればさらにその値で頭打ち)が
        /// BudgetFitMinSize(256px)超のテクスチャ。
        /// 同点時も選択が揺れないようアセットパス順に並べて返す。
        /// 現状サイズには見積もり済みの info.downloadMB を再利用する
        /// (PlanBudgetFit の合計値 estimate.estimatedDownloadMB との整合を保つため。
        /// 見積もり側も同じ縮小計画で頭打ちにしているため整合する)。
        /// 個別テクスチャの失敗は警告ログを出してスキップする。
        /// </summary>
        private static List<BudgetFitCandidate> BuildBudgetFitCandidates(SizeEstimateResult estimate, TextureImporterFormat applyFormat, float applyBitsPerPixel, Dictionary<string, int> planByGuid)
        {
            var candidates = new List<BudgetFitCandidate>();
            foreach (TextureSizeInfo info in estimate.textures)
            {
                try
                {
                    if (info == null || info.texture == null || string.IsNullOrEmpty(info.assetPath)) continue;
                    // 見積もり失敗(0MB扱い)のテクスチャは合計値に含まれておらず、
                    // 候補にすると削減量の収支が合わなくなるため対象外にする
                    if (info.downloadMB <= 0f) continue;
                    var importer = AssetImporter.GetAtPath(info.assetPath) as TextureImporter;
                    if (importer == null) continue; // 実行時生成・TextureImporter以外のアセットは縮小できない

                    int texWidth, texHeight;
                    GetSourceTextureSize(importer, info.texture, out texWidth, out texHeight);
                    TextureImporterPlatformSettings androidSettings = importer.GetPlatformTextureSettings("Android");
                    bool hasOverride = androidSettings != null && androidSettings.overridden;
                    int maxSetting = hasOverride ? androidSettings.maxTextureSize : importer.maxTextureSize;
                    int effectiveMax = Mathf.Min(Mathf.Max(1, maxSetting), Mathf.Max(texWidth, texHeight));
                    // 既存の縮小計画を起点にする(繰り返し実行しても計画が収束するように)
                    int planTarget = GetPlanTarget(planByGuid, info.assetPath);
                    if (planTarget > 0 && planTarget < effectiveMax) effectiveMax = planTarget;
                    if (effectiveMax <= BudgetFitMinSize) continue; // 既に下限以下は縮めない

                    // 現在の圧縮形式のbpp(Android上書きなしはEstimateTextureと同じくASTC 6x6相当と仮定)
                    TextureImporterFormat currentFormat = hasOverride ? androidSettings.format : TextureImporterFormat.ASTC_6x6;
                    string currentLabel;
                    bool currentHighQuality;
                    float currentBitsPerPixel = FormatToBitsPerPixel(currentFormat, out currentLabel, out currentHighQuality);

                    // 既存形式の方が高効率(bppが小さい)なら形式は維持してサイズ縮小のみ行う
                    // (applyFormatへ格上げすると削減が0や負になり、適用でサイズが増え得るため)
                    bool keepCurrentFormat = currentBitsPerPixel <= applyBitsPerPixel;
                    TextureImporterFormat candidateFormat = keepCurrentFormat ? currentFormat : applyFormat;
                    float candidateBitsPerPixel = keepCurrentFormat ? currentBitsPerPixel : applyBitsPerPixel;

                    candidates.Add(new BudgetFitCandidate
                    {
                        texture = info.texture,
                        assetPath = info.assetPath,
                        texWidth = texWidth,
                        texHeight = texHeight,
                        originalMaxSize = effectiveMax,
                        currentMaxSize = effectiveMax,
                        originalDownloadMB = info.downloadMB,
                        currentDownloadMB = info.downloadMB,
                        minPossibleDownloadMB = ComputeDownloadMB(texWidth, texHeight, BudgetFitMinSize, candidateBitsPerPixel),
                        isPriority = IsBudgetFitPriorityName(info.texture.name),
                        applyFormat = candidateFormat,
                        applyBitsPerPixel = candidateBitsPerPixel,
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(string.Format("[RARA QuestConverter] テクスチャ '{0}' を縮小候補にできませんでした: {1}",
                        info != null && info.texture != null ? info.texture.name : "(不明)", ex.Message));
                }
            }
            candidates.Sort((a, b) => string.CompareOrdinal(a.assetPath, b.assetPath));
            return candidates;
        }

        /// <summary>
        /// 次に半減する候補を選ぶ。基本は「計画適用後のダウンロードサイズが最大の候補」。
        /// ただしそれが優先保護(顔・体・肌系)の場合、現在の最大サイズ帯に非保護の候補が
        /// 残っていればそちら(のうちダウンロードサイズ最大のもの)を先に縮める。
        /// どの候補も縮められなければnullを返す。
        /// </summary>
        private static BudgetFitCandidate PickBudgetFitCandidate(List<BudgetFitCandidate> candidates)
        {
            BudgetFitCandidate best = null;
            int largestTier = 0;
            foreach (BudgetFitCandidate candidate in candidates)
            {
                if (!candidate.CanShrink) continue;
                if (candidate.currentMaxSize > largestTier) largestTier = candidate.currentMaxSize;
                if (best == null || candidate.currentDownloadMB > best.currentDownloadMB) best = candidate;
            }
            if (best == null || !best.isPriority) return best;

            // 優先保護テクスチャが最大でも、同じ最大サイズ帯の非保護候補が残っていれば先に縮める
            BudgetFitCandidate bestNonPriority = null;
            foreach (BudgetFitCandidate candidate in candidates)
            {
                if (candidate.isPriority || !candidate.CanShrink || candidate.currentMaxSize != largestTier) continue;
                if (bestNonPriority == null || candidate.currentDownloadMB > bestNonPriority.currentDownloadMB) bestNonPriority = candidate;
            }
            return bestNonPriority != null ? bestNonPriority : best;
        }

        /// <summary>顔・体・肌系(縮小を後回しにする)テクスチャ名か(大文字小文字を区別しない部分一致)。</summary>
        private static bool IsBudgetFitPriorityName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string keyword in BudgetFitPriorityKeywords)
            {
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// currentSize の半分以下で最大の2の累乗サイズを返す(下限 BudgetFitMinSize)。
        /// 例: 2048→1024、680→512、300→256。currentSize が BudgetFitMinSize 超なら必ず currentSize 未満を返す。
        /// </summary>
        private static int NextHalvedSize(int currentSize)
        {
            int half = Mathf.Max(BudgetFitMinSize, currentSize / 2);
            int pow2 = BudgetFitMinSize;
            while (pow2 * 2 <= half) pow2 *= 2;
            return pow2;
        }

        /// <summary>削減量0の注記行(エラー・情報表示用)を作る。</summary>
        private static SizeSuggestion Note(string message)
        {
            return new SizeSuggestion
            {
                description = message,
                savingMB = 0f,
                texture = null,
                recommendedMaxSize = 0,
            };
        }
    }
}
#endif
