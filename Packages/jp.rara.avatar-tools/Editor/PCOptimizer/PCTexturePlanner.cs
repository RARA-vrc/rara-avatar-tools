// RARA PC軽量化ツール - テクスチャVRAM見積もり / 縮小提案モジュール
// PCランクの「テクスチャメモリ(TextureMegabytes)」統計は“ダウンロードサイズ”ではなく
// “展開後VRAM(ミップ込み)”で評価される。本モジュールはその観点で見積もり・縮小提案を行う。
//
// ・EstimateTextureMemoryMB … VRChat SDKの AvatarPerformanceStats.textureMegabytes(Windows基準)を
//   そのまま返す。これが実ランク判定に使われる権威ある値なので、表示・ランク判定はこの値に合わせる。
//   SDK呼び出しに失敗した場合のみ、下記の自前VRAMモデルの合計へフォールバックする。
// ・BuildSuggestions … 目標ランクのTextureMemoryMB上限を下回るための縮小案を、VRAM削減量の大きい順で返す。
//   自前の「TextureFormatのブロックサイズ×実効解像度×ミップチェーン」VRAMモデルで各テクスチャを見積もり、
//   合計は上記SDK値へアンカーする(モデル合計とSDK値の比でスケール補正)。これで提案の削減量が
//   実ランク数値と整合する。顔・体・肌系テクスチャは QuestConverter と同じ発想で縮小を後回しにする。
//   縮小の適用(縮小コピー生成)は I1(PCOptimizer)のパイプラインが担当し、本モジュールは解析と提案のみ。
//   提案は ToPlanEntry / ApplySuggestionsToPlan で QuestConverter の縮小計画エントリ(TextureSizePlanEntry)へ変換する。
//
// 【注意/前提】自前VRAMモデルは読み込み済みテクスチャの format(TextureFormat) / 実サイズ(=アクティブビルドターゲットの
//   インポート結果)を使う。PC軽量化ツールの用途上、通常はビルドターゲットがWindows(Standalone)であることを想定する。
//   仮にAndroidターゲットでも、合計はSDK値(Windows強制)へアンカー補正されるため“テクスチャメモリ総量”は正しい。
//   RenderTexture・実行時生成(アセット未保存)テクスチャは縮小できないため提案対象外にし、VRAMだけ総量へ計上する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using RARA.QuestConverter;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace RARA.PCOptimizer
{
    /// <summary>
    /// PC(Windows)ランクの「テクスチャメモリ(VRAM)」を見積もり、目標ランクへ収めるための
    /// テクスチャ縮小案を作る静的クラス。縮小の実適用は行わない(解析・提案と、縮小計画エントリへの変換のみ)。
    /// 例外はUIへ投げず、失敗時は安全側(0MB・空リスト・スキップ)に倒す。
    /// </summary>
    public static class PCTexturePlanner
    {
        /// <summary>縮小の下限サイズ(px)。これ未満へは縮めない(顔・体などの視認性を確保)。</summary>
        private const int MinTextureSize = 256;
        /// <summary>縮小提案を出す最小削減量(MB)。微小な提案でリストを埋めない。</summary>
        private const float SuggestionMinSavingMB = 0.05f;
        /// <summary>目標到達判定の許容誤差(MB。浮動小数のループ停止条件用)。</summary>
        private const float BudgetEpsilonMB = 0.01f;
        /// <summary>顔・体・肌系(縮小を後回しにする)テクスチャ名の部分一致キーワード(QuestConverterと同じ発想)。</summary>
        private static readonly string[] PriorityKeywords = { "face", "body", "skin", "hair", "顔", "体", "肌", "髪" };

        /// <summary>UIへ返す縮小提案1件(契約: PCOptimizerCoreのピン留め型)。</summary>
        public class PCTextureSuggestion
        {
            /// <summary>対象テクスチャ(プロジェクト内アセットのTexture2Dのみ)。</summary>
            public Texture2D texture;
            /// <summary>現在の最大辺(px)。</summary>
            public int currentSize;
            /// <summary>推奨する最大辺(px。2の累乗で段階的に縮小、下限256)。</summary>
            public int suggestedSize;
            /// <summary>この縮小で削減できるVRAMの目安(MB)。</summary>
            public float saveMB;
            /// <summary>提案理由(日本語。例: "4096→2048で -21.3MB")。</summary>
            public string reason;
        }

        /// <summary>内部作業用: テクスチャ1枚の縮小候補。</summary>
        private class Candidate
        {
            public Texture2D texture;
            public string name;
            public int srcWidth;
            public int srcHeight;
            public TextureFormat format;
            public bool hasMips;
            /// <summary>元の最大辺(px)。</summary>
            public int originalMax;
            /// <summary>現在の縮小目標(px。半減のたびに更新)。</summary>
            public int currentMax;
            /// <summary>元サイズでのVRAM目安(MB)。</summary>
            public float originalVramMB;
            /// <summary>現在の縮小目標でのVRAM目安(MB)。</summary>
            public float currentVramMB;
            /// <summary>顔・体・肌系(縮小を後回し)ならtrue。</summary>
            public bool priority;

            /// <summary>これ以上縮小できるか(下限より大きい)。</summary>
            public bool CanShrink => currentMax > MinTextureSize && NextHalvedSize(currentMax) < currentMax;
        }

        // ================================================================
        // 1. テクスチャメモリ(VRAM)の総量見積もり
        // ================================================================

        /// <summary>
        /// アバターのテクスチャメモリ(VRAM, MB)を見積もる。
        /// まずVRChat SDKの AvatarPerformanceStats.textureMegabytes(Windows基準)を用いる
        /// (実ランク判定と同じ値のため)。SDK呼び出しに失敗した場合のみ自前VRAMモデルの合計へフォールバックする。
        /// null安全。例外は投げず、失敗時は0を返す。
        /// </summary>
        public static float EstimateTextureMemoryMB(GameObject avatar)
        {
            if (avatar == null) return 0f;

            float sdk = SdkTextureMegabytes(avatar);
            if (sdk > 0f) return sdk;

            // フォールバック: 自前VRAMモデルの合計
            try
            {
                List<Candidate> candidates = CollectCandidates(avatar, out float nonShrinkableMB);
                float total = nonShrinkableMB;
                foreach (Candidate c in candidates) total += c.originalVramMB;
                return total;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA PCOptimizer] テクスチャメモリの見積もりに失敗しました: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// VRChat SDKの権威あるテクスチャメモリ値(Windows基準・MB)を返す。取得できなければ -1 を返す。
        /// 第4引数 false(isMobile=false)でWindows/Standaloneのテクスチャ設定に基づき評価させる。
        /// </summary>
        private static float SdkTextureMegabytes(GameObject avatar)
        {
            if (avatar == null) return -1f;
            // ウィンドウ診断表の textureMemoryMB(ComputePCPerformance)と一致させるため、EditorOnlyを
            // 除去した一時複製で計測する。元アバターは変更しない。
            GameObject temp = null;
            try
            {
                temp = UnityEngine.Object.Instantiate(avatar);
                temp.hideFlags = HideFlags.HideAndDontSave;
                QuestCompat.StripEditorOnlySubtrees(temp);

                var stats = new AvatarPerformanceStats(false);
                AvatarPerformance.CalculatePerformanceStats(avatar.name, temp, stats, false);
                return stats.textureMegabytes ?? -1f; // Windows基準のVRAM(MB)
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA PCOptimizer] SDKのテクスチャメモリ計算に失敗したため自前見積もりへフォールバックします: " + ex.Message);
                return -1f;
            }
            finally
            {
                if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        // ================================================================
        // 2. 縮小提案(目標ランクのTextureMemoryMB上限へ収める)
        // ================================================================

        /// <summary>
        /// 目標ランク(targetRank)のテクスチャメモリ上限を下回るための縮小案を、VRAM削減量の大きい順で返す。
        /// ・各テクスチャのVRAMは TextureFormat のブロックサイズ×実効解像度×実ミップチェーンで積算する。
        /// ・合計はSDKの textureMegabytes(取得できれば)へアンカーし、モデル合計との比でスケール補正する
        ///   (提案の削減量が実ランク数値と整合するように)。
        /// ・毎回「現在VRAMが最大の候補」を2の累乗で半減する。ただし顔・体・肌系は同じサイズ帯に他候補が
        ///   残っている間は後回しにする。同一テクスチャの複数回の半減は1件へまとめる(currentSize→suggestedSize)。
        /// ・既に上限以下なら空リストを返す。どの候補もこれ以上縮められなくなったら(目標未達でも)そこで打ち切る。
        /// 共有テクスチャは1回だけ数える。RenderTexture・アセット未保存テクスチャは提案対象外(総量には計上、ログ注記)。
        /// null安全・例外を投げない。
        /// </summary>
        public static List<PCTextureSuggestion> BuildSuggestions(GameObject avatar, PCTargetRank targetRank)
        {
            var suggestions = new List<PCTextureSuggestion>();
            if (avatar == null) return suggestions;

            try
            {
                float limitMB = PCRankLimits.GetLimit(targetRank, PCRankLimits.PCStat.TextureMemoryMB);
                if (limitMB <= 0f) return suggestions;

                List<Candidate> candidates = CollectCandidates(avatar, out float nonShrinkableMB);

                float modelTotal = nonShrinkableMB;
                foreach (Candidate c in candidates) modelTotal += c.originalVramMB;

                // 合計はSDK値(実ランク数値)へアンカーする。取得できなければモデル合計をそのまま使う。
                float sdkTotal = SdkTextureMegabytes(avatar);
                float anchorTotal = sdkTotal > 0f ? sdkTotal : modelTotal;
                // モデル値→アンカー値のスケール(各テクスチャの見積もり値をアンカー基準へ換算する係数)
                float scale = (sdkTotal > 0f && modelTotal > 0.0001f) ? sdkTotal / modelTotal : 1f;

                if (anchorTotal <= limitMB + BudgetEpsilonMB) return suggestions; // 既に目標ランク以内
                if (candidates.Count == 0) return suggestions;                    // 縮小できるテクスチャが無い

                // ---- 貪欲法: アンカー基準の実効VRAMで目標まで段階的に半減する ----
                float currentTotal = anchorTotal;
                int safety = candidates.Count * 32 + 64; // ループ安全弁(各テクスチャの半減回数は高々 log2(8192/256) 程度)
                while (currentTotal > limitMB + BudgetEpsilonMB && safety-- > 0)
                {
                    Candidate picked = PickCandidate(candidates);
                    if (picked == null) break; // どの候補もこれ以上縮められない

                    int newSize = NextHalvedSize(picked.currentMax);
                    float newVramMB = CandidateVramMB(picked, newSize);
                    float effDelta = (picked.currentVramMB - newVramMB) * scale;

                    currentTotal -= effDelta;
                    picked.currentMax = newSize;
                    picked.currentVramMB = newVramMB;
                }

                // ---- 縮小された候補を提案へ ----
                foreach (Candidate c in candidates)
                {
                    if (c.currentMax >= c.originalMax) continue; // 縮小されなかった
                    float saveMB = (c.originalVramMB - c.currentVramMB) * scale;
                    if (saveMB < SuggestionMinSavingMB) continue;

                    string reason = string.Format("{0}→{1}で -{2:F1}MB", c.originalMax, c.currentMax, saveMB);
                    if (c.priority) reason += "(顔/肌/髪テクスチャ)";

                    suggestions.Add(new PCTextureSuggestion
                    {
                        texture = c.texture,
                        currentSize = c.originalMax,
                        suggestedSize = c.currentMax,
                        saveMB = saveMB,
                        reason = reason,
                    });
                }

                suggestions.Sort((a, b) => b.saveMB.CompareTo(a.saveMB));
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA PCOptimizer] テクスチャ縮小提案の作成に失敗しました: " + ex);
            }
            return suggestions;
        }

        /// <summary>
        /// 次に半減する候補を選ぶ。基本は「現在VRAMが最大の候補」。ただしそれが優先保護(顔・体・肌系)の場合、
        /// 同じ最大サイズ帯に非保護の候補が残っていればそちら(のうちVRAM最大)を先に縮める。
        /// どの候補も縮められなければnullを返す(QuestSizeEstimator.PickBudgetFitCandidate と同じ流儀)。
        /// </summary>
        private static Candidate PickCandidate(List<Candidate> candidates)
        {
            Candidate best = null;
            int largestTier = 0;
            foreach (Candidate c in candidates)
            {
                if (!c.CanShrink) continue;
                if (c.currentMax > largestTier) largestTier = c.currentMax;
                if (best == null || c.currentVramMB > best.currentVramMB) best = c;
            }
            if (best == null || !best.priority) return best;

            Candidate bestNonPriority = null;
            foreach (Candidate c in candidates)
            {
                if (c.priority || !c.CanShrink || c.currentMax != largestTier) continue;
                if (bestNonPriority == null || c.currentVramMB > bestNonPriority.currentVramMB) bestNonPriority = c;
            }
            return bestNonPriority ?? best;
        }

        // ================================================================
        // 3. 縮小計画エントリ(QuestConverter の TextureSizePlanEntry)への変換ヘルパー
        //    実適用(縮小コピー生成)は I1 のパイプラインが担当する。
        // ================================================================

        /// <summary>
        /// 縮小提案1件を QuestConverter の縮小計画エントリ(GUID+目標サイズ)へ変換する。
        /// アセット化されていないテクスチャ(GUID無し)はnullを返す。
        /// </summary>
        public static TextureSizePlanEntry ToPlanEntry(PCTextureSuggestion suggestion)
        {
            if (suggestion == null || suggestion.texture == null || suggestion.suggestedSize <= 0) return null;
            string path = AssetDatabase.GetAssetPath(suggestion.texture);
            string guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return null;
            return new TextureSizePlanEntry { textureGuid = guid, targetSize = suggestion.suggestedSize };
        }

        /// <summary>
        /// 縮小提案リストを縮小計画(TextureSizePlanEntry のリスト)へ一括登録する。
        /// 既存エントリはGUIDで一意化し、より小さい目標サイズを保持する(QuestConverter.UpsertTextureSizePlan と同じ規則)。
        /// 追加・更新できた件数を返す。plan は呼び出し側(I1の PCOptimizeSettings.texturePlan)が保持するリスト。
        /// null安全。
        /// </summary>
        public static int ApplySuggestionsToPlan(List<PCTextureSuggestion> suggestions, List<TextureSizePlanEntry> plan)
        {
            if (suggestions == null || plan == null) return 0;
            int applied = 0;
            foreach (PCTextureSuggestion s in suggestions)
            {
                TextureSizePlanEntry entry = ToPlanEntry(s);
                if (entry == null) continue;
                if (UpsertPlan(plan, entry.textureGuid, entry.targetSize)) applied++;
            }
            return applied;
        }

        /// <summary>plan へ guid の目標サイズを登録/縮小する(小さい方を保持)。追加・縮小できたらtrue。</summary>
        private static bool UpsertPlan(List<TextureSizePlanEntry> plan, string guid, int targetSize)
        {
            if (string.IsNullOrEmpty(guid) || targetSize <= 0) return false;
            foreach (TextureSizePlanEntry entry in plan)
            {
                if (entry == null || entry.textureGuid != guid) continue;
                if (entry.targetSize > 0 && entry.targetSize <= targetSize) return false; // 既により小さい計画がある
                entry.targetSize = targetSize;
                return true;
            }
            plan.Add(new TextureSizePlanEntry { textureGuid = guid, targetSize = targetSize });
            return true;
        }

        // ================================================================
        // テクスチャ収集
        // ================================================================

        /// <summary>
        /// アバター配下のマテリアルから到達可能な全テクスチャを重複なしで集め、縮小候補(プロジェクト内アセットのTexture2D)を作る。
        /// レンダラーの sharedMaterials に加え、全コンポーネントの SerializedObject 走査で Material/Texture 参照も拾う
        /// (パーティクル・独自コンポーネント等が持つテクスチャも計上するため)。
        /// EditorOnly サブツリーは VRChat のビルドと同様に除外する。
        /// RenderTexture・アセット未保存テクスチャは縮小できないため候補にはせず、そのVRAMだけ nonShrinkableMB へ計上してログ注記する。
        /// </summary>
        private static List<Candidate> CollectCandidates(GameObject avatar, out float nonShrinkableMB)
        {
            nonShrinkableMB = 0f;
            var candidates = new List<Candidate>();
            if (avatar == null) return candidates;

            var textures = new HashSet<Texture>();
            var materialCache = new Dictionary<Material, List<Texture>>();

            // (1) レンダラーの sharedMaterials
            foreach (Renderer renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (QuestCompat.IsEditorOnly(renderer.transform)) continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null) continue;
                foreach (Material mat in mats)
                {
                    if (mat == null) continue;
                    foreach (Texture tex in GetMaterialTextures(mat, materialCache)) textures.Add(tex);
                }
            }

            // (2) 全コンポーネントの SerializedObject 走査(Material / Texture 参照)
            foreach (Component comp in avatar.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || comp is Transform) continue;
                if (QuestCompat.IsEditorOnly(comp.transform)) continue;
                try
                {
                    var so = new SerializedObject(comp);
                    SerializedProperty prop = so.GetIterator();
                    while (prop.Next(true))
                    {
                        if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                        UnityEngine.Object obj = prop.objectReferenceValue;
                        if (obj == null) continue;
                        if (obj is Material m)
                        {
                            foreach (Texture tex in GetMaterialTextures(m, materialCache)) textures.Add(tex);
                        }
                        else if (obj is Texture t)
                        {
                            textures.Add(t);
                        }
                    }
                    so.Dispose();
                }
                catch (Exception)
                {
                    // 一部の壊れた/特殊なコンポーネントは走査に失敗し得るが、致命的ではないので黙って飛ばす
                }
            }

            // (3) テクスチャを候補 / 非縮小(VRAM計上のみ)へ振り分け
            foreach (Texture tex in textures)
            {
                if (tex == null) continue;

                var tex2D = tex as Texture2D;
                string path = AssetDatabase.GetAssetPath(tex);
                bool hasAsset = !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));

                if (tex2D == null || !hasAsset)
                {
                    // RenderTexture・Cubemap・実行時生成など: 縮小計画に載せられないため提案対象外。VRAMだけ総量へ。
                    nonShrinkableMB += ProfilerVramMB(tex);
                    Debug.Log(string.Format(
                        "[RARA PCOptimizer] テクスチャ「{0}」は{1}のため縮小提案の対象外です(VRAM総量には計上)。",
                        tex.name, tex2D == null ? "RenderTexture/実行時生成等" : "プロジェクト内アセット未保存"));
                    continue;
                }

                var cand = BuildCandidate(tex2D);
                if (cand == null)
                {
                    nonShrinkableMB += ProfilerVramMB(tex);
                    continue;
                }
                candidates.Add(cand);
            }

            return candidates;
        }

        /// <summary>マテリアルが参照する全テクスチャ(mainTexture + 全テクスチャプロパティ)を重複なしで返す(結果はキャッシュ)。</summary>
        private static List<Texture> GetMaterialTextures(Material material, Dictionary<Material, List<Texture>> cache)
        {
            if (cache.TryGetValue(material, out List<Texture> cached)) return cached;

            var textures = new List<Texture>();
            try
            {
                Texture main = material.mainTexture;
                if (main != null) textures.Add(main);

                string[] names = material.GetTexturePropertyNames();
                if (names != null)
                {
                    foreach (string name in names)
                    {
                        Texture tex = material.GetTexture(name);
                        if (tex != null && !textures.Contains(tex)) textures.Add(tex);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format("[RARA PCOptimizer] マテリアル「{0}」のテクスチャ列挙に失敗しました: {1}",
                    material != null ? material.name : "(不明)", ex.Message));
            }
            cache[material] = textures;
            return textures;
        }

        /// <summary>Texture2D から縮小候補を作る(元サイズ・形式・ミップ・VRAM・優先度)。失敗時はnull。</summary>
        private static Candidate BuildCandidate(Texture2D tex2D)
        {
            try
            {
                int w = Mathf.Max(1, tex2D.width);
                int h = Mathf.Max(1, tex2D.height);
                int maxDim = Mathf.Max(w, h);
                int mipCount = Mathf.Max(1, tex2D.mipmapCount);
                bool hasMips = mipCount > 1;
                TextureFormat fmt = tex2D.format;

                var cand = new Candidate
                {
                    texture = tex2D,
                    name = tex2D.name,
                    srcWidth = w,
                    srcHeight = h,
                    format = fmt,
                    hasMips = hasMips,
                    originalMax = maxDim,
                    currentMax = maxDim,
                    priority = IsPriorityName(tex2D.name),
                };
                cand.originalVramMB = ComputeVramBytes(fmt, w, h, mipCount) / (1024f * 1024f);
                cand.currentVramMB = cand.originalVramMB;
                return cand;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format("[RARA PCOptimizer] テクスチャ「{0}」のVRAM見積もりに失敗しました: {1}",
                    tex2D != null ? tex2D.name : "(不明)", ex.Message));
                return null;
            }
        }

        // ================================================================
        // VRAM計算
        // ================================================================

        /// <summary>
        /// 指定サイズ(最大辺 targetMax)へアスペクト比を保って縮小した場合のVRAM(MB)を返す。
        /// 元がミップ持ちなら縮小後もフルミップチェーンを想定して積算する。
        /// </summary>
        private static float CandidateVramMB(Candidate c, int targetMax)
        {
            ComputeEffectiveSize(c.srcWidth, c.srcHeight, targetMax, out int nw, out int nh);
            int mipCount = c.hasMips ? 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(nw, nh), 2f)) : 1;
            return ComputeVramBytes(c.format, nw, nh, mipCount) / (1024f * 1024f);
        }

        /// <summary>
        /// TextureFormat のブロック情報(ブロック幅・高さ・ブロックあたりバイト数)から、
        /// width×height・mipCount 段のテクスチャのVRAMバイト数を積算する。
        /// 非圧縮形式はブロック1×1でバイト/ピクセルとして扱える(GraphicsFormatUtility が両方に対応)。
        /// GraphicsFormatUtility が値を返せない場合は非圧縮32bpp(4バイト/ピクセル)としてフォールバックする。
        /// </summary>
        private static long ComputeVramBytes(TextureFormat format, int width, int height, int mipCount)
        {
            int blockW = 1, blockH = 1, blockBytes = 4; // フォールバック: 32bpp(4バイト/ピクセル)
            try
            {
                blockW = (int)GraphicsFormatUtility.GetBlockWidth(format);
                blockH = (int)GraphicsFormatUtility.GetBlockHeight(format);
                blockBytes = (int)GraphicsFormatUtility.GetBlockSize(format);
            }
            catch (Exception)
            {
                blockW = 1; blockH = 1; blockBytes = 4;
            }
            if (blockW <= 0) blockW = 1;
            if (blockH <= 0) blockH = 1;
            if (blockBytes <= 0) blockBytes = 4;

            long total = 0;
            int levels = Mathf.Max(1, mipCount);
            for (int mip = 0; mip < levels; mip++)
            {
                int lw = Mathf.Max(1, width >> mip);
                int lh = Mathf.Max(1, height >> mip);
                long xBlocks = (lw + blockW - 1) / blockW;
                long yBlocks = (lh + blockH - 1) / blockH;
                total += xBlocks * yBlocks * blockBytes;
            }
            return total;
        }

        /// <summary>Profiler計測によるテクスチャの現在VRAM(MB)。ブロック法で測れない非Texture2D等のフォールバック用。</summary>
        private static float ProfilerVramMB(Texture tex)
        {
            try
            {
                return Profiler.GetRuntimeMemorySizeLong(tex) / (1024f * 1024f);
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>maxSize 上限へアスペクト比を保って収めた実効サイズを計算する(長辺を maxSize へ。QuestSizeEstimatorと同じ流儀)。</summary>
        private static void ComputeEffectiveSize(int srcWidth, int srcHeight, int maxSize, out int width, out int height)
        {
            float scale = Mathf.Min(1f, (float)Mathf.Max(1, maxSize) / Mathf.Max(1, Mathf.Max(srcWidth, srcHeight)));
            width = Mathf.Max(1, Mathf.RoundToInt(srcWidth * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(srcHeight * scale));
        }

        /// <summary>
        /// currentSize の半分以下で最大の2の累乗サイズを返す(下限 MinTextureSize)。
        /// 例: 4096→2048、680→512、300→256。currentSize が MinTextureSize 超なら必ず currentSize 未満を返す。
        /// </summary>
        private static int NextHalvedSize(int currentSize)
        {
            int half = Mathf.Max(MinTextureSize, currentSize / 2);
            int pow2 = MinTextureSize;
            while (pow2 * 2 <= half) pow2 *= 2;
            return pow2;
        }

        /// <summary>顔・体・肌・髪系(縮小を後回しにする)テクスチャ名か(大文字小文字を区別しない部分一致)。</summary>
        private static bool IsPriorityName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string keyword in PriorityKeywords)
            {
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
#endif
