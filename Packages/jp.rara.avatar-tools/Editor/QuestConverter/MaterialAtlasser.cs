// RARA Quest Converter - マテリアルアトラス統合モジュール
// 変換済みQuestマテリアル(Toon Standard / Toon Lit)のベイク済みテクスチャを1枚のアトラスへ
// 詰め直し、互換グループごとに1マテリアルへ統合する。メッシュのUV0をアトラスのセル矩形へ
// 再配置し、同一アトラスマテリアルを指すサブメッシュを結合してマテリアルスロットを削減する。
// 役割分担: 本ツールが「マテリアルを1つにする」ことを担当し、ビルド時のAAO(Trace&Optimize)が
// 同一アクティビティのメッシュ統合と同一参照スロットの重複排除を行う。
//
// 【実装メモ】アトラスの組み立ては Graphics.DrawTexture + GL.LoadPixelMatrix ではなく、
// セルごとの Graphics.Blit → ReadPixels → CPU合成 で行う。DrawTexture のピクセル行列描画は
// プラットフォーム(D3D/GL)でY反転の扱いが異なり向きの検証が難しいのに対し、
// Blit → ReadPixels は本ツールの TextureBaker.RunBlit で実績のある(生成PNGが正しい向きで
// 保存される)パターンであり、原点(左下)の座標系がUV空間と一致するため。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RARA.QuestConverter
{
    /// <summary>アトラス構築の結果一式。</summary>
    public class AtlasBuildResult
    {
        /// <summary>
        /// 上描き(多重描画)スロットの処理スキームのバージョン。1.0.8(=スキーム未畳み込み)は
        /// 余剰スロットを実サブメッシュへ複製し最終サブメッシュのUVも再配置していたため、生成メッシュの
        /// 内容が 1.0.9(=スキーム2: 非表示スロットは削除・可視スロットはネイティブ多重描画のまま温存・
        /// 三角形の複製なし)と非互換になる。レイアウトハッシュへこの定数を畳み込むことで、同一アトラス配置でも
        /// 1.0.9 は別名のメッシュ/アトラスを出力し、旧複製が参照する同一GUIDメッシュを非互換内容で
        /// 上書きして見た目を壊すことを防ぐ(旧複製は旧メッシュ+旧アトラスのまま整合した孤立物として残る)。
        /// </summary>
        public const int kOverdrawScheme = 2;

        /// <summary>元(PC)マテリアル → 統合後アトラスマテリアル。</summary>
        public Dictionary<Material, Material> atlasMap;
        /// <summary>元(PC)マテリアル → アトラス内UV矩形(0..1、0.5テクセル内側インセット済み)。</summary>
        public Dictionary<Material, Rect> cellRects;
        /// <summary>アトラス統合から除外したマテリアルと理由(「名前: 理由」形式)。</summary>
        public List<string> excluded;

        /// <summary>
        /// 今回の remap で実際に使うアトラス割り当て(cellRects の各矩形 + アトラスマテリアル名)から
        /// 決定的に算出した8桁hexのレイアウトハッシュ。remap 済みメッシュ名
        /// ("{mesh}_QuestAtlas_{layoutHash}.asset")に用いる。設定変更(例: カリング無視の既定変更)で
        /// グループ構成やパッキングが変わるとハッシュも変わり、旧レイアウトのメッシュ資産と
        /// 新レイアウトのメッシュ資産が別アセットになる。これにより、シーンに残った古い複製は
        /// 「旧メッシュ(旧UV)+旧アトラスマテリアル(旧テクスチャ)」の整合が保たれ、見た目が壊れない。
        /// 同一レイアウトの再実行では同一ハッシュ=同一パスとなり、GUID安定の上書きが維持される。
        /// EnsureLayoutHash() で結果確定時に一度だけ計算・キャッシュする。
        /// </summary>
        public string layoutHash;

        /// <summary>
        /// layoutHash を(未計算なら)算出してキャッシュし返す。cellRects の各エントリ
        /// (元マテリアル名・矩形x/y/w/h)を元マテリアル名順に畳み込み、続けてアトラス
        /// マテリアル名(重複排除・昇順)を畳み込む(BuildStableGroupName と同じ FNV-1a 32bit)。
        /// 同一レイアウトなら実行間で常に同一の8桁hexになる(処理順・列挙順に依存しない)。
        /// </summary>
        public string EnsureLayoutHash()
        {
            if (!string.IsNullOrEmpty(layoutHash)) return layoutHash;

            var entries = new List<KeyValuePair<string, Rect>>();
            if (cellRects != null)
            {
                foreach (KeyValuePair<Material, Rect> kv in cellRects)
                {
                    entries.Add(new KeyValuePair<string, Rect>(kv.Key != null ? kv.Key.name : string.Empty, kv.Value));
                }
            }
            entries.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.Key, b.Key);
                if (byName != 0) return byName;
                // 同名マテリアルが複数ある場合でも決定的になるよう矩形で安定化する
                int byX = a.Value.x.CompareTo(b.Value.x);
                if (byX != 0) return byX;
                return a.Value.y.CompareTo(b.Value.y);
            });

            uint hash = 2166136261u; // FNV-1a 32bit
            foreach (KeyValuePair<string, Rect> e in entries)
            {
                hash = FoldString(hash, e.Key);
                hash = FoldFloat(hash, e.Value.x);
                hash = FoldFloat(hash, e.Value.y);
                hash = FoldFloat(hash, e.Value.width);
                hash = FoldFloat(hash, e.Value.height);
            }

            var matNames = new List<string>();
            if (atlasMap != null)
            {
                var seen = new HashSet<Material>();
                foreach (KeyValuePair<Material, Material> kv in atlasMap)
                {
                    if (kv.Value != null && seen.Add(kv.Value)) matNames.Add(kv.Value.name);
                }
            }
            matNames.Sort(StringComparer.Ordinal);
            foreach (string n in matNames) hash = FoldString(hash, n);

            // 上描き処理スキームのバージョンを畳み込む(1.0.9で別名メッシュを出力し旧複製の上書き破壊を防ぐ)。
            hash = FoldString(hash, "overdraw-scheme:" + kOverdrawScheme);

            layoutHash = hash.ToString("x8");
            return layoutHash;
        }

        private static uint FoldString(uint hash, string s)
        {
            if (s != null)
            {
                foreach (char c in s) hash = (hash ^ c) * 16777619u;
            }
            hash = (hash ^ '\n') * 16777619u; // 名前区切り(連結の曖昧さ回避)
            return hash;
        }

        private static uint FoldFloat(uint hash, float f)
        {
            // IEEE ビット列を1バイトずつ畳み込む(文字列化の丸め誤差を避け、決定的に折り込む)
            byte[] bytes = BitConverter.GetBytes(f);
            for (int i = 0; i < bytes.Length; i++) hash = (hash ^ bytes[i]) * 16777619u;
            return hash;
        }
    }

    /// <summary>
    /// 変換済みマテリアルのアトラス統合と、メッシュUV再配置・サブメッシュ結合を行う静的クラス。
    /// BuildAtlases → (オーケストレーター側でRemapMeshesAndMergeSlots) → ApplyMaterialMap の順で使う。
    /// RemapMeshesAndMergeSlots はレンダラーのスロットがまだ元(PC)マテリアルを指している段階、
    /// つまり ApplyMaterialMap の前に呼び出すこと。
    /// </summary>
    public static class MaterialAtlasser
    {
        private const string ProgressTitle = "RARA Quest変換";
        private const string UndoLabel = "Quest変換";

        /// <summary>
        /// remap 済みアトラスメッシュ名のサフィックス。完成名は
        /// "{元メッシュ名}_QuestAtlas_{レイアウトハッシュ8桁hex}"。命名と解析(SceneHasStaleAtlasClone /
        /// CollectLatestGenerationHashes / ExtractLayoutHash)で必ずこの定数を共有する。
        /// </summary>
        public const string AtlasMeshSuffix = "_QuestAtlas_";

        /// <summary>セル周囲のガター幅(px)。ミップマップ縮小時の隣セル滲みを防ぐ。</summary>
        private const int GutterPixels = 8;

        /// <summary>オーバーフロー時にセルを縮小できる最小サイズ(px)。</summary>
        private const int MinCellSize = 32;

        /// <summary>低ディテール検出でセルを縮小できる最小サイズ(px)。単色は極端に小さいセルにできる。</summary>
        private const int MinDetailCellSize = 8;

        /// <summary>UV0が0..1に収まっているとみなす許容誤差。</summary>
        private const float UvTolerance = 0.001f;

        /// <summary>
        /// 質感パラメータの差異警告に使う比較対象float(存在しないものはスキップ)。
        /// 統合マテリアルは先頭メンバーから全非テクスチャパラメータを引き継ぐため、
        /// 引き継ぎで見た目が変わり得るシェーディング系パラメータを広めに比較する。
        /// </summary>
        private static readonly string[] ComparedFloatProps =
        {
            "_RimIntensity", "_MinBrightness", "_MatcapStrength", "_BumpScale",
            "_ShadowBoost", "_ShadowAlbedo", "_Reflectance", "_GlossStrength",
            "_MetallicStrength", "_RimRange", "_RimSharpness", "_OcclusionStrength",
        };

        // ================================================================
        // 内部データ構造
        // ================================================================

        /// <summary>アトラス内の1セル(=元マテリアル1個ぶんのテクスチャ領域)。</summary>
        private class AtlasCell
        {
            public Material src;        // 元(PC)マテリアル
            public Material converted;  // 変換済みQuestマテリアル(テクスチャの供給元)
            public Texture ramp;        // Toon Standard の _Ramp(ランプ統一時の代表選定用。null可)
            public string rampKey;      // ランプの内容ハッシュキー(代表選定・差異判定用)
            public int width;           // セル幅(px、縮小されることがある)
            public int height;          // セル高さ(px)
            public RectInt rect;        // アトラス内ピクセル矩形(原点=左下)
            public bool detailShrunk;   // 単色・低ディテール検出で縮小されたセルか
            public int detailPx;        // 縮小後の長辺px(detailShrunk時のみ・レポート用)
        }

        /// <summary>
        /// 統合互換グループ(同一シェーダー・同一カリング)。
        /// ランプは通常キーの一部だが、settings.atlasUnifyRamps 時はキーから外し、
        /// グループ確定後に UnifyGroupRamp が代表ランプを選んで ramp へ設定する。
        /// </summary>
        private class AtlasGroup
        {
            public bool toonStandard;
            public Texture ramp;        // Toon Standard のみ。通常キーの一部 / ランプ統一時は代表ランプ
            public AtlasCell representative; // ランプ統一時、代表ランプを供給したメンバー(ランプ連動パラメータの取得元)
            public readonly List<AtlasCell> cells = new List<AtlasCell>();
        }

        /// <summary>マテリアルのメッシュ上での使用箇所(UV範囲チェック用)。</summary>
        private class MeshSlotUse
        {
            public Mesh mesh;
            public int submesh;
        }

        // ================================================================
        // 公開API 1: アトラス構築
        // ================================================================

        /// <summary>
        /// materialMap(元→変換済み)のうち統合可能なマテリアルをグループ化し、
        /// テクスチャアトラスと統合マテリアル(RARA_Atlas_{n})を生成する。
        /// アセットは {outputDir}/Textures / {outputDir}/Materials へ、実行間で安定したパスに
        /// GUID を保持したまま保存される(assets: 変換1回ぶんのパス管理コンテキスト。null なら内部生成)。
        /// 失敗したグループは据え置かれる(全体は中断しない)。
        /// </summary>
        public static AtlasBuildResult BuildAtlases(GameObject questRoot, Dictionary<Material, Material> materialMap, HashSet<Material> animationUsed, Dictionary<Material, MaterialOverrideEntry> overrides, QuestConvertSettings settings, string outputDir, ConversionReport report, ConversionAssetContext assets = null)
        {
            var result = new AtlasBuildResult
            {
                atlasMap = new Dictionary<Material, Material>(),
                cellRects = new Dictionary<Material, Rect>(),
                excluded = new List<string>(),
            };
            if (report == null) report = new ConversionReport();
            if (questRoot == null || materialMap == null || materialMap.Count == 0 || settings == null)
            {
                return result;
            }
            if (animationUsed == null) animationUsed = new HashSet<Material>();
            if (overrides == null) overrides = new Dictionary<Material, MaterialOverrideEntry>();
            // 内容由来の低ディテール解析キャッシュを実行間で作り直す(再インポート後の内容を反映するため)。
            QuestSizeEstimator.InvalidateDetailCache();
            if (assets == null) assets = new ConversionAssetContext(); // 単体呼び出し用(通常はオーケストレーターから渡される)
            outputDir = NormalizeFolder(outputDir);
            int atlasMaxSize = Mathf.Clamp(settings.atlasMaxSize, 256, 8192);

            Material blitMat = null;
            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, "アトラス候補を収集中...", 0.02f);

                // ---- 1. 候補収集とグループ化 ----
                Dictionary<Material, List<MeshSlotUse>> meshUsage = CollectMeshUsage(questRoot);
                var uvCache = new Dictionary<Mesh, Vector2[]>();
                var rampKeyCache = new Dictionary<Texture, string>();
                var groups = new Dictionary<string, AtlasGroup>();

                // Modular Avatar の Material Setter / Material Swap が参照するマテリアルは、
                // ビルド時にスロットへ差し込まれる/同一性で差し替えられるため、アトラスへ詰め込むと
                // (スロット番号のずれ・複数マテリアルの1本化で)差し替えが壊れる。詰め込み対象から除外する。
                // MA 未導入時は空集合(何も除外されない)。
                HashSet<Material> maReferenced = MACompatUtility.CollectReferencedMaterials(questRoot);

                foreach (KeyValuePair<Material, Material> pair in materialMap)
                {
                    Material src = pair.Key;
                    Material converted = pair.Value;
                    if (src == null || converted == null || converted.shader == null) continue;

                    string shaderName = converted.shader.name;
                    bool isToonStandard = shaderName == QuestCompat.ToonStandardShaderName;
                    bool isToonLit = shaderName == QuestCompat.ToonLitShaderName;
                    if (!isToonStandard && !isToonLit) continue; // パーティクル・非表示等は対象外(除外理由にも載せない)

                    List<MeshSlotUse> uses;
                    if (!meshUsage.TryGetValue(src, out uses) || uses.Count == 0)
                    {
                        result.excluded.Add(src.name + ": メッシュで使用されていないため");
                        continue;
                    }
                    if (animationUsed.Contains(src))
                    {
                        result.excluded.Add(src.name + ": アニメで差し替えられるため");
                        continue;
                    }
                    if (maReferenced.Contains(src))
                    {
                        result.excluded.Add(src.name + ": MAマテリアル設定が参照するため");
                        continue;
                    }
                    MaterialOverrideEntry overrideEntry;
                    if (overrides.TryGetValue(src, out overrideEntry) && overrideEntry != null && overrideEntry.excludeFromAtlas)
                    {
                        result.excluded.Add(src.name + ": アトラス除外指定のため");
                        continue;
                    }
                    string uvReason;
                    bool uvOk;
                    try
                    {
                        uvOk = CheckUvRange(uses, uvCache, out uvReason);
                    }
                    catch (Exception ex)
                    {
                        // メッシュ読み取り失敗で変換全体(BuildAtlasesの外側までの例外伝播)を
                        // 中断せず、このマテリアルのみアトラス対象から除外して続行する
                        uvOk = false;
                        uvReason = "メッシュを読み取れないため(" + ex.Message + ")";
                    }
                    if (!uvOk)
                    {
                        result.excluded.Add(src.name + ": " + uvReason);
                        continue;
                    }
                    if (!IsMainTexStIdentity(converted))
                    {
                        // メインテクスチャSTが単位でない場合はUVがタイリング相当のため統合不可
                        result.excluded.Add(src.name + ": メインテクスチャにタイリング/オフセット設定があるため");
                        continue;
                    }

                    // グループキー: シェーダー名 + カリング + (Toon Standardのみ)ランプ内容ハッシュ。
                    // 生成ランプはマテリアルごとに別アセットとして保存されるため、参照(インスタンスID)で
                    // グループ化すると影設定が同一でも全マテリアルが単独グループになり統合されない。
                    // 内容ハッシュなら同じ影設定から生成されたランプ同士が同一グループになる。
                    // 【settings.atlasUnifyRamps 時】ランプ内容ハッシュをキーから外し、シェーダー名+カリング
                    // だけでグループ化する。影ランプがマテリアルごとに個別生成されるアバターでは全ランプが
                    // 相異なり全マテリアルが単独グループになってしまうため、ランプ差を無視して統合し、
                    // グループ確定後に代表ランプ1枚へ統一する(UnifyGroupRamp)。カリングは常にキーに残す
                    // (片面 cull=2 と両面 cull=0 を混ぜると描画が壊れるため)。
                    int cull = -1;
                    if (converted.HasProperty("_Culling")) cull = Mathf.RoundToInt(converted.GetFloat("_Culling"));
                    else if (converted.HasProperty("_Cull")) cull = Mathf.RoundToInt(converted.GetFloat("_Cull"));
                    Texture ramp = (isToonStandard && converted.HasProperty("_Ramp")) ? converted.GetTexture("_Ramp") : null;
                    string rampKey = GetRampGroupKey(ramp, rampKeyCache);

                    // マットキャップ(USE_MATCAP)を持つ Toon Standard は、マットキャップの同一性を
                    // グループキーへ折り込む。マットキャップは法線ベースのUVで統合セルに依らず全面へ適用される
                    // ため、異なるマットキャップを1マテリアルへ統合すると別々の映り込みが誤って混ざる
                    // (統合マテリアルは先頭メンバーの _Matcap/_MatcapType/_MatcapStrength を引き継ぐ)。
                    // 一方 マットキャップマスク(_MatcapMask)はUV0サンプルのため、統合でUV0がセルへ再配置
                    // されるとマスクがずれる。マスク付きマットキャップは誤ったマスクを黙って生成しないよう
                    // 統合対象から除外する(圧縮より正しさを優先)。
                    string matcapKey = string.Empty;
                    if (isToonStandard && converted.IsKeywordEnabled("USE_MATCAP"))
                    {
                        if (GetTexture(converted, "_MatcapMask") != null)
                        {
                            result.excluded.Add(src.name + ": マットキャップマスクがあるため(アトラス統合対象外)");
                            continue;
                        }
                        matcapKey = "|mc:" + GetMatcapGroupKey(converted);
                    }

                    string key = settings.atlasUnifyRamps
                        ? shaderName + "|" + cull + matcapKey
                        : shaderName + "|" + cull + "|" + rampKey + matcapKey;

                    AtlasGroup group;
                    if (!groups.TryGetValue(key, out group))
                    {
                        group = new AtlasGroup { toonStandard = isToonStandard, ramp = ramp };
                        groups.Add(key, group);
                    }

                    // セルサイズ = min(元寸法, maxTextureSize, 縮小計画, 低ディテール解析)。
                    // 単色・低ディテールなメンバーは極端に小さいセルへ縮小される(GOAL 2)。
                    var cell = new AtlasCell { src = src, converted = converted, ramp = ramp, rampKey = rampKey };
                    PlanCellSize(cell, settings);
                    group.cells.Add(cell);
                }

                // ---- 2. 単独グループの除外 ----
                var mergeGroups = new List<AtlasGroup>();
                foreach (AtlasGroup group in groups.Values)
                {
                    if (group.cells.Count < 2)
                    {
                        result.excluded.Add(group.cells[0].src.name + ": 統合できる相手がないため(単独グループ)");
                        continue;
                    }
                    mergeGroups.Add(group);
                }
                if (mergeGroups.Count == 0)
                {
                    report.Info("アトラス統合: 統合可能なマテリアルグループがありませんでした。");
                    return result;
                }

                // ---- 3. ベイクシェーダーのパス確認 ----
                Shader bakeShader = Shader.Find(QuestCompat.BakeShaderName);
                if (bakeShader == null)
                {
                    report.Warn(string.Format("アトラス統合: 合成用シェーダー '{0}' が見つからないため中止しました。", QuestCompat.BakeShaderName));
                    return result;
                }
                blitMat = new Material(bakeShader);
                int passTint = FindPassIndex(blitMat, "TintCopy");
                int passUnpack = FindPassIndex(blitMat, "UnpackNormal");
                if (passTint < 0 || passUnpack < 0)
                {
                    report.Warn("アトラス統合: ベイクシェーダーに必要なパス(TintCopy / UnpackNormal)が見つかりません。RARA_QuestBake.shader の更新・再インポートを確認してください。中止します。");
                    return result;
                }

                // ---- 4. グループごとにパッキング → アトラス合成 → マテリアル生成 ----
                for (int gi = 0; gi < mergeGroups.Count; gi++)
                {
                    AtlasGroup group = mergeGroups[gi];
                    float progress = 0.1f + 0.85f * ((gi + 1f) / mergeGroups.Count);
                    EditorUtility.DisplayProgressBar(ProgressTitle, string.Format("アトラス合成中 ({0}/{1})...", gi + 1, mergeGroups.Count), progress);

                    try
                    {
                        BuildOneGroup(group, atlasMaxSize, blitMat, passTint, passUnpack, settings, outputDir, assets, result, report);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        report.Warn(string.Format("アトラス統合中に例外が発生したため、このグループは統合せず据え置きます: {0}", ex.Message));
                        // 途中まで登録された可能性のあるグループメンバーを結果から取り除く(整合性維持)
                        foreach (AtlasCell cell in group.cells)
                        {
                            result.atlasMap.Remove(cell.src);
                            result.cellRects.Remove(cell.src);
                        }
                    }
                }
            }
            finally
            {
                if (blitMat != null) UnityEngine.Object.DestroyImmediate(blitMat);
                EditorUtility.ClearProgressBar();
            }
            // 結果確定後にレイアウトハッシュを算出する(全グループ処理・据え置き除外が反映された最終状態)。
            result.EnsureLayoutHash();
            return result;
        }

        /// <summary>1グループぶんのパッキング・アトラス合成・マテリアル生成を行い、結果へ登録する。</summary>
        private static void BuildOneGroup(AtlasGroup group, int atlasMaxSize, Material blitMat, int passTint, int passUnpack, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, AtlasBuildResult result, ConversionReport report)
        {
            // ---- パッキング(タイトに詰めて最小の2の累乗へ。収まらなければ縮小 → それでも駄目なら小セルを脱落) ----
            // FitCells は正方形へ詰めた後、実使用領域を囲む最小の非正方形2の累乗(atlasW×atlasH)を返す。
            // 以降のチャンネル合成・UV矩形正規化・使用率レポートはすべてこの atlasW/atlasH を使う。
            int atlasW, atlasH;
            if (!FitCells(group, atlasMaxSize, result.excluded, report, out atlasW, out atlasH))
            {
                return; // メンバーが2未満になった(残りは除外済み)
            }

            // ---- ランプ統一: 代表ランプを選ぶ(Toon Standard かつ設定オン時のみ) ----
            // FitCells 後(=実際にアトラスへ入るメンバー)を対象に、最多数のランプを代表として
            // group.ramp へ設定する(CreateAtlasMaterial が _Ramp へ書き込む)。
            if (settings.atlasUnifyRamps && group.toonStandard)
            {
                UnifyGroupRamp(group, report);
            }

            // ---- チャンネルの要否(Toon Standardのみノーマル・エミッションを持つ) ----
            bool needNormal = false;
            bool needEmission = false;
            if (group.toonStandard)
            {
                foreach (AtlasCell cell in group.cells)
                {
                    if (GetTexture(cell.converted, "_BumpMap") != null) needNormal = true;
                    Color emissionTint;
                    bool clamped;
                    if (HasActiveEmission(cell.converted, out emissionTint, out clamped))
                    {
                        // 真に発光するメンバーが1つでも存在するときだけエミッションアトラスを生成する
                        needEmission = true;
                    }
                    else if (emissionTint.maxColorComponent > 0.001f)
                    {
                        // 色×強度は黒でないが _EmissionMap が未設定 → 発光なしとして統合する
                        // (残存カラーを全面発光としてベイクしない。HasActiveEmission のコメント参照)
                        report.Info(string.Format("'{0}': _EmissionMapが未設定のため、エミッションなしとして統合します(_EmissionColorの残存値はベイクしません)。", cell.src.name));
                    }
                }
            }

            // ---- 質感差異の警告(非テクスチャパラメータは先頭メンバーからコピーされる) ----
            WarnMaterialParameterDifferences(group, report);

            // ---- アトラス合成(メイン / ノーマル / エミッション) ----
            // 名前は「グループ処理順の連番」ではなく、メンバー構成由来の安定キーにする。
            // 連番だと Dictionary の列挙順やグループの成立状況(FitCells脱落・設定変更等)が
            // 変わった実行で、別グループの内容が同じパス(=同じGUID)へ上書きされ、
            // 旧 _Quest クローンのUV再配置と食い違う静かな見た目破壊が起き得る。
            // 内容由来の名前なら同じメンバー構成のグループは常に同じパスを得る
            // (万一のハッシュ衝突は StablePathRegistry.Claim の連番で決定的に解決される)。
            // さらにパッキング結果(セル配置)由来のレイアウトハッシュを接尾辞に付ける。
            // メンバー構成が同じでも設定変更(atlasMaxSize 等)でUV配置が変わると別名=別GUIDの
            // テクスチャ/マテリアルになり、旧レイアウトのメッシュを参照する古い複製(リネーム後の
            // アップロード直前アバター等、名前一致の除去を免れたもの)を、共有テクスチャの
            // その場上書きで壊さない(メッシュ命名 line 730 と同じ保護をテクスチャ/マテリアルにも与える)。
            string baseName = "RARA_Atlas_" + BuildStableGroupName(group) + "_" + BuildGroupLayoutHash(group, atlasW, atlasH);

            Texture2D mainAtlas = ComposeChannel(group, atlasW, atlasH, AtlasChannel.Main, blitMat, passTint, passUnpack, report);
            Texture2D mainSaved = SaveAtlasTexture(mainAtlas, baseName, "_main", true, false, settings, outputDir, assets);
            if (mainSaved == null)
            {
                report.Warn(string.Format("アトラス '{0}' のメインテクスチャ保存に失敗したため、このグループは統合せず据え置きます。", baseName));
                return;
            }

            Texture2D normalSaved = null;
            if (needNormal)
            {
                Texture2D normalAtlas = ComposeChannel(group, atlasW, atlasH, AtlasChannel.Normal, blitMat, passTint, passUnpack, report);
                normalSaved = SaveAtlasTexture(normalAtlas, baseName, "_normal", false, true, settings, outputDir, assets);
                if (normalSaved == null)
                {
                    report.Warn(string.Format("アトラス '{0}' のノーマルマップ保存に失敗したため、ノーマルなしで統合します。", baseName));
                }
            }

            Texture2D emissionSaved = null;
            if (needEmission)
            {
                Texture2D emissionAtlas = ComposeChannel(group, atlasW, atlasH, AtlasChannel.Emission, blitMat, passTint, passUnpack, report);
                emissionSaved = SaveAtlasTexture(emissionAtlas, baseName, "_emission", true, false, settings, outputDir, assets);
                if (emissionSaved == null)
                {
                    report.Warn(string.Format("アトラス '{0}' のエミッション保存に失敗したため、エミッションなしで統合します。", baseName));
                }
            }

            // ---- 統合マテリアル生成 ----
            Material atlasMaterial = CreateAtlasMaterial(group, mainSaved, normalSaved, emissionSaved, baseName, outputDir, assets);

            // ---- 結果登録(UV矩形は0.5テクセル内側へインセット) ----
            var memberNames = new List<string>();
            var shrunkNotes = new List<string>();
            long packedArea = 0;
            foreach (AtlasCell cell in group.cells)
            {
                result.atlasMap[cell.src] = atlasMaterial;
                result.cellRects[cell.src] = new Rect(
                    (cell.rect.x + 0.5f) / atlasW,
                    (cell.rect.y + 0.5f) / atlasH,
                    (cell.rect.width - 1f) / atlasW,
                    (cell.rect.height - 1f) / atlasH);
                memberNames.Add(cell.src.name);
                packedArea += (long)cell.rect.width * cell.rect.height; // ガター抜きの実セル面積
                if (cell.detailShrunk) shrunkNotes.Add(string.Format("{0}({1}px)", cell.src.name, cell.detailPx));
            }
            float usage = (atlasW > 0 && atlasH > 0) ? (float)packedArea / ((long)atlasW * atlasH) * 100f : 0f;
            string info = string.Format("統合: {0}材質 → 1 (サイズ{1}x{2}, 使用率{3:F1}%, メンバー{0}件: {4})",
                group.cells.Count, atlasW, atlasH, usage, string.Join(", ", memberNames));
            if (shrunkNotes.Count > 0)
            {
                info += string.Format(" / 低ディテール縮小: {0}", string.Join(", ", shrunkNotes));
            }
            report.Info(info);
        }

        /// <summary>
        /// アトラス名の安定キーを返す(メンバーの元マテリアル名をソートして連結した文字列の
        /// FNV-1aハッシュ8桁hex)。処理順に依存しないため、同じメンバー構成のグループは
        /// 実行間で常に同じアトラス名(=同じ安定パス・GUID)を得る。
        /// </summary>
        private static string BuildStableGroupName(AtlasGroup group)
        {
            var names = new List<string>(group.cells.Count);
            foreach (AtlasCell cell in group.cells)
            {
                names.Add(cell.src != null ? cell.src.name : string.Empty);
            }
            names.Sort(StringComparer.Ordinal);

            uint hash = 2166136261u; // FNV-1a 32bit
            foreach (string name in names)
            {
                foreach (char c in name)
                {
                    hash = (hash ^ c) * 16777619u;
                }
                hash = (hash ^ '\n') * 16777619u; // 名前区切り(連結の曖昧さ回避)
            }
            return hash.ToString("x8");
        }

        /// <summary>
        /// グループのパッキング結果(セル配置)由来のレイアウトハッシュ8桁hexを返す。
        /// メンバーの元マテリアル名・各セルのピクセル矩形・アトラス寸法を(名前順に)畳み込むため、
        /// メンバー構成が同じでもパッキングが変わればハッシュが変わる。処理順・列挙順には依存しない
        /// (元マテリアル名で決定的にソートするため、Dictionary列挙順=実行間で変わるインスタンスIDに
        /// 左右されない)。TryPackSquare の同寸タイブレークと併せ、同一設定なら実行間で常に同一hex。
        /// </summary>
        private static string BuildGroupLayoutHash(AtlasGroup group, int atlasW, int atlasH)
        {
            var entries = new List<KeyValuePair<string, RectInt>>(group.cells.Count);
            foreach (AtlasCell cell in group.cells)
            {
                entries.Add(new KeyValuePair<string, RectInt>(cell.src != null ? cell.src.name : string.Empty, cell.rect));
            }
            entries.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.Key, b.Key);
                if (byName != 0) return byName;
                int byX = a.Value.x.CompareTo(b.Value.x);
                if (byX != 0) return byX;
                return a.Value.y.CompareTo(b.Value.y);
            });

            uint hash = 2166136261u; // FNV-1a 32bit
            hash = FoldLayoutInt(hash, atlasW);
            hash = FoldLayoutInt(hash, atlasH);
            hash = FoldLayoutInt(hash, AtlasBuildResult.kOverdrawScheme); // 上描き処理スキーム(1.0.9で別名アトラスを出力)
            foreach (KeyValuePair<string, RectInt> e in entries)
            {
                foreach (char c in e.Key) hash = (hash ^ c) * 16777619u;
                hash = (hash ^ '\n') * 16777619u; // 名前区切り
                hash = FoldLayoutInt(hash, e.Value.x);
                hash = FoldLayoutInt(hash, e.Value.y);
                hash = FoldLayoutInt(hash, e.Value.width);
                hash = FoldLayoutInt(hash, e.Value.height);
            }
            return hash.ToString("x8");
        }

        /// <summary>int を4バイトに分けて FNV-1a で畳み込む(決定的)。</summary>
        private static uint FoldLayoutInt(uint hash, int v)
        {
            uint u = (uint)v;
            hash = (hash ^ (u & 0xFF)) * 16777619u;
            hash = (hash ^ ((u >> 8) & 0xFF)) * 16777619u;
            hash = (hash ^ ((u >> 16) & 0xFF)) * 16777619u;
            hash = (hash ^ ((u >> 24) & 0xFF)) * 16777619u;
            return hash;
        }

        // ================================================================
        // 公開API 2: メッシュUV再配置とサブメッシュ結合
        // ================================================================

        /// <summary>
        /// questRoot配下の各メッシュのUV0をアトラスのセル矩形へ再配置し、
        /// 同一アトラスマテリアルへ写像されるサブメッシュを結合してスロットを削減する。
        /// 【重要】レンダラーのスロットがまだ元(PC)マテリアルを指している段階
        /// (ApplyMaterialMap の前)に呼び出すこと。結合後のスロットには先頭メンバーの
        /// 元マテリアルを残す(後段の ApplyMaterialMap がアトラスマテリアルへ差し替える)。
        /// レンダラー単位で失敗しても据え置いて続行する(全体は中断しない)。
        /// </summary>
        /// <param name="hiddenMaterials">
        /// Quest変換で「非表示化(不可視)」へ変換された元(PC)マテリアルの集合(任意)。最終サブメッシュの
        /// 上描き(多重描画)スロットがこの集合に含まれる場合、そのスロットは何も描かないため複製も温存もせず
        /// 削除する(ポリゴン増ゼロ)。PC最適化経路は null を渡す(非表示化を行わないため)。
        /// </param>
        public static void RemapMeshesAndMergeSlots(GameObject questRoot, AtlasBuildResult atlas, string outputDir, ConversionReport report, ConversionAssetContext assets = null, ISet<Material> hiddenMaterials = null)
        {
            if (report == null) report = new ConversionReport();
            if (questRoot == null || atlas == null || atlas.atlasMap == null || atlas.atlasMap.Count == 0 || atlas.cellRects == null)
            {
                return;
            }
            if (assets == null) assets = new ConversionAssetContext(); // 単体呼び出し用(通常はオーケストレーターから渡される)
            outputDir = NormalizeFolder(outputDir);

            // remap 済みメッシュ名に付けるレイアウトハッシュを確定する(結果は使い回されるため一度だけ計算)。
            // BuildAtlases 側で計算済みでも、単体呼び出しに備えてここでも確実に用意する。
            atlas.EnsureLayoutHash();

            // マテリアル差し替えアニメーションはスロットをバインディングパス
            // 「m_Materials.Array.data[N]」のインデックスで参照するため、スロット統合で
            // 後続スロットの番号がずれるとアニメが範囲外/誤ったスロットを駆動して壊れる
            // (AnimationConverter はカーブの参照値のみ書き換え、バインディングのパス・番号は
            //  書き換えない)。該当レンダラーはスロット統合を行わず、UV再配置のみ実施して
            // スロット番号を維持する(同一アトラス参照の重複スロットはビルド時のAAOに委ねる)。
            HashSet<string> animatedSlotPaths = CollectMaterialSlotAnimationPaths(questRoot);

            // 対象レンダラー(SkinnedMeshRenderer / MeshRenderer+MeshFilter のみ、EditorOnly除外)
            var targets = new List<Renderer>();
            foreach (Renderer renderer in questRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, questRoot.transform)) continue;
                if (GetRendererMesh(renderer) == null) continue;
                targets.Add(renderer);
            }

            // ---- 事前パス: 可視の上描き(ネイティブ温存)を持つレンダラーの基底マテリアルを非アトラス化する ----
            // 可視の余剰スロットは最終サブメッシュの重ね描き。三角形を複製せずネイティブ多重描画のまま温存するには、
            // 上描きスロットと基底スロットが「同一の頂点UV」を共有する必要がある。よって基底サブメッシュのUV0を
            // アトラスセルへ再配置してはならない。基底マテリアル(と、アトラス化されている上描きマテリアル)を
            // アトラス割り当て(atlasMap)から外し、元UVのまま・非アトラスの変換済みマテリアルで描く。
            // cellRects は残す(後段の冗長スロット判定=同一セル比較で元のセルを参照するため)。
            // 全レンダラーを処理する前に一括で外すため、同じ基底マテリアルを使う他レンダラーとも整合する
            // (共有マテリアルは全レンダラーで一貫して非アトラス化=最適化が僅かに減るのみで見た目は不変)。
            HashSet<Material> unAtlas = CollectNativeKeepUnAtlasMaterials(targets, atlas, animatedSlotPaths, questRoot, hiddenMaterials);
            foreach (Material m in unAtlas)
            {
                atlas.atlasMap.Remove(m);
            }

            var meshCache = new List<MeshCacheEntry>();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    Renderer renderer = targets[i];
                    EditorUtility.DisplayProgressBar(ProgressTitle,
                        string.Format("メッシュUVをアトラスへ再配置中 ({0}/{1}): {2}", i + 1, targets.Count, renderer.name),
                        (i + 1f) / Mathf.Max(1, targets.Count));
                    try
                    {
                        bool allowSlotMerge = !animatedSlotPaths.Contains(
                            AnimationUtility.CalculateTransformPath(renderer.transform, questRoot.transform));
                        ProcessRenderer(renderer, atlas, outputDir, assets, meshCache, allowSlotMerge, hiddenMaterials, report);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        report.Warn(string.Format("'{0}': アトラス用メッシュ編集に失敗したため、このレンダラーは据え置きます: {1}", renderer.name, ex.Message));
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 可視の上描き(ネイティブ温存)を持つレンダラーについて、非アトラス化すべきマテリアルを集める。
        /// 対象は「基底(最終サブメッシュ担当)マテリアル」と「アトラス化されている可視の上描きマテリアル」。
        /// これらを atlasMap から外すことで、基底サブメッシュのUV0が再配置されず(元UVのまま)、上描きスロットと
        /// 同一UVを共有できるようになる(=三角形を複製せずネイティブ多重描画のまま温存できる)。
        /// 統合不可レンダラー(allowSlotMerge=false)は従来どおりネイティブ温存かつ基底も再配置するため対象外。
        /// hidden/冗長/null の余剰スロットは削除対象で非アトラス化は不要(温存しないため)。
        /// </summary>
        private static HashSet<Material> CollectNativeKeepUnAtlasMaterials(List<Renderer> targets, AtlasBuildResult atlas, HashSet<string> animatedSlotPaths, GameObject questRoot, ISet<Material> hiddenMaterials)
        {
            var result = new HashSet<Material>();
            foreach (Renderer renderer in targets)
            {
                bool allowSlotMerge = !animatedSlotPaths.Contains(
                    AnimationUtility.CalculateTransformPath(renderer.transform, questRoot.transform));
                if (!allowSlotMerge) continue; // 統合不可は従来どおり(基底も再配置=対象外)

                Mesh mesh = GetRendererMesh(renderer);
                if (mesh == null) continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null) continue;
                int subMeshCount = mesh.subMeshCount;
                int slotCount = mats.Length;
                if (subMeshCount == 0 || slotCount <= subMeshCount) continue;

                int overdrawnSubmesh = subMeshCount - 1;
                Material baseSlotMat = mats[overdrawnSubmesh];
                Rect baseCell = default(Rect);
                bool baseAtlased = baseSlotMat != null && atlas.cellRects.TryGetValue(baseSlotMat, out baseCell);

                for (int i = subMeshCount; i < slotCount; i++)
                {
                    Material om = mats[i];
                    if (om == null) continue;                              // 削除対象(温存しない)
                    if (IsHiddenMaterial(om, hiddenMaterials)) continue;   // 非表示 = 削除対象(温存しない)
                    bool redundant = ReferenceEquals(om, baseSlotMat);
                    if (!redundant && baseAtlased)
                    {
                        Rect oc;
                        if (atlas.cellRects.TryGetValue(om, out oc) && oc == baseCell) redundant = true;
                    }
                    if (redundant) continue;                               // 冗長 = 削除対象(温存しない)

                    // 可視の上描き = ネイティブ温存 → 基底とこの上描き(アトラス化されていれば)を非アトラス化する
                    if (baseSlotMat != null) result.Add(baseSlotMat);
                    if (atlas.atlasMap.ContainsKey(om)) result.Add(om);
                }
            }
            return result;
        }

        /// <summary>
        /// マテリアル(元マテリアル)がQuest変換で「非表示化(不可視)」へ変換されるものかを判定する。
        /// 主判定は変換パイプラインから渡される集合(hiddenMaterials)への所属。集合が渡されない経路や
        /// 取りこぼしに備え、名前接尾辞 "_QuestHidden"(FinalizeMaterialが不可視マテリアルへ付与)も安全網とする。
        /// </summary>
        private static bool IsHiddenMaterial(Material m, ISet<Material> hiddenMaterials)
        {
            if (m == null) return false;
            if (hiddenMaterials != null && hiddenMaterials.Contains(m)) return true;
            string n = m.name; // 安全網: スロットが既に変換済み不可視マテリアルを指す経路向け
            return n != null && n.EndsWith("_QuestHidden", StringComparison.Ordinal);
        }

        /// <summary>同一メッシュ+同一マテリアル配列のレンダラーで編集済みメッシュを再利用するためのキャッシュ。</summary>
        private class MeshCacheEntry
        {
            public Mesh sourceMesh;
            public Material[] sourceMats;
            public Mesh newMesh;
            public Material[] newMats;
            /// <summary>スロット統合を許可して編集したメッシュか(統合可否が異なるレンダラー間では共用しない)。</summary>
            public bool allowSlotMerge;
        }

        /// <summary>
        /// アバター配下の全コントローラーの全クリップを走査し、マテリアルスロット差し替え
        /// (m_Materials.Array.data[N])のObjectReferenceカーブがバインドするレンダラーの
        /// Transformパス(アバタールート相対)を収集する。
        /// アトラス化はアニメーション変換(AnimationConverter)より前に実行されるため、
        /// この時点のコントローラーは元(PC)のものだが、バインディングパスは複製後も同一。
        /// </summary>
        private static HashSet<string> CollectMaterialSlotAnimationPaths(GameObject questRoot)
        {
            var paths = new HashSet<string>();

            // (a) 到達可能な全コントローラー = アバタールート相対とみなす(FXレイヤー等の主経路)。
            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(questRoot))
            {
                if (controller == null) continue;
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    AddMaterialSlotPaths(clip, string.Empty, paths);
                }
            }

            // (b) ルート以外のコンポーネント(子オブジェクトのAnimator / MA Merge Animator等)が参照する
            //     コントローラーは、バインディングパスがそのコンポーネント基準の可能性があるため、
            //     コンポーネント位置を前置したパスでも解釈する。判定が広がる(=スロット統合を控える)方向に
            //     しか働かないため安全側。ToggleConsolidator / ComponentRemover と同じ二段構え。
            var seenPrefixed = new HashSet<string>();
            foreach (Component component in questRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (component.transform == questRoot.transform) continue;
                string prefix = QuestCompat.GetRelativePath(questRoot.transform, component.transform);
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
                        AddMaterialSlotPaths(clip, prefix, paths);
                    }
                }
            }

            // (c) Modular Avatar の Material Setter(対象レンダラー)/ Material Swap(ルート配下レンダラー)が
            //     指すレンダラーは、スロット番号(MaterialIndex)やマテリアル同一性に依存する。スロット統合で
            //     番号がずれる/マテリアルが1本化されると差し替えが壊れるため、該当レンダラーはスロット統合を
            //     控えて(UV再配置のみ実施して)スロット番号を維持する。MA 未導入時は空集合。
            foreach (string maPath in MACompatUtility.CollectTargetRendererPaths(questRoot))
            {
                paths.Add(maPath);
            }

            return paths;
        }

        /// <summary>
        /// クリップ内の m_Materials(マテリアルスロット差し替え)ObjectReferenceバインディングのパスを、
        /// prefix(コントローラー所有オブジェクトのルート相対パス。ルート相対なら空文字)を前置して paths へ追加する。
        /// </summary>
        private static void AddMaterialSlotPaths(AnimationClip clip, string prefix, HashSet<string> paths)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (binding.propertyName == null ||
                    !binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)) continue;
                string raw = binding.path ?? string.Empty;
                string full = prefix.Length == 0 ? raw : (raw.Length == 0 ? prefix : prefix + "/" + raw);
                paths.Add(full);
            }
        }

        /// <summary>
        /// レンダラー1つぶんのUV再配置・サブメッシュ結合・アセット保存・差し替えを行う。
        /// allowSlotMerge が false の場合、UV再配置のみ行いスロット統合(スロット番号の変化)は行わない。
        /// hiddenMaterials は非表示化(不可視)へ変換される元マテリアルの集合(任意)。最終サブメッシュの
        /// 上描きスロットがこれに含まれる場合は削除する(何も描かないため。ポリゴン増ゼロ)。
        /// </summary>
        private static void ProcessRenderer(Renderer renderer, AtlasBuildResult atlas, string outputDir, ConversionAssetContext assets, List<MeshCacheEntry> meshCache, bool allowSlotMerge, ISet<Material> hiddenMaterials, ConversionReport report)
        {
            Mesh mesh = GetRendererMesh(renderer);
            if (mesh == null) return;
            Material[] mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0 || mesh.subMeshCount == 0) return;

            // 対象スロットが1つも無ければスキップ
            bool anyAtlasSlot = false;
            foreach (Material m in mats)
            {
                if (m != null && atlas.atlasMap.ContainsKey(m) && atlas.cellRects.ContainsKey(m))
                {
                    anyAtlasSlot = true;
                    break;
                }
            }
            if (!anyAtlasSlot) return;

            if (!allowSlotMerge)
            {
                report.Info(string.Format("'{0}': マテリアル差し替えアニメーションの対象のため、スロット統合は行わずUV再配置のみ実施します(スロット番号を維持)。", renderer.name));
            }

            // キャッシュ: 同一メッシュ+同一マテリアル配列+同一統合可否なら編集済みメッシュを再利用
            foreach (MeshCacheEntry entry in meshCache)
            {
                if (entry.sourceMesh == mesh && entry.allowSlotMerge == allowSlotMerge && MaterialsEqual(entry.sourceMats, mats))
                {
                    AssignToRenderer(renderer, entry.newMesh, (Material[])entry.newMats.Clone());
                    report.Info(string.Format("'{0}': スロット {1}→{2}(編集済みメッシュを共用)", renderer.name, mats.Length, entry.newMats.Length));
                    return;
                }
            }

            int subMeshCount = mesh.subMeshCount;
            int slotCount = mats.Length;

            // ---- 処理スロット構築(実サブメッシュに対応するスロット) ----
            // procMats/procIndices/procTopology は「独自の出力サブメッシュを持ち、UVをアトラスセルへ
            // 再配置する」スロット群。実サブメッシュと1対1のスロット([0..realSlotCount))を積む
            // (余剰=多重描画スロットは複製せず、下の方針決定で削除またはネイティブ温存する)。
            int realSlotCount = Mathf.Min(slotCount, subMeshCount);
            var procMats = new List<Material>(slotCount);
            var procIndices = new List<int[]>(slotCount);       // GetIndicesは毎回新しい配列を返す(安全に書き換え可能)
            var procTopology = new List<MeshTopology>(slotCount);
            for (int i = 0; i < realSlotCount; i++)
            {
                procMats.Add(mats[i]);
                procIndices.Add(mesh.GetIndices(i));
                procTopology.Add(mesh.GetTopology(i));
            }

            // ---- 多重描画スロット(slotCount > subMeshCount)の方針決定 ----
            // 余剰スロットは「最終サブメッシュ(subMeshCount-1)の重ね描き」。三角形を絶対に複製しない方針:
            //   ・null / 非表示化(不可視)へ変換される余剰 = 何も描かない。削除する(温存も複製もしない)。
            //   ・基底(最終サブメッシュ担当スロット)と同一マテリアル/同一セルへ写像される冗長な余剰 = 削除する。
            //   ・それ以外の可視の重ね描き = 元アバターと同じネイティブ多重描画のまま温存する(複製しない)。
            //     事前パス(CollectNativeKeepUnAtlasMaterials)で基底マテリアルを非アトラス化済みのため、基底
            //     サブメッシュのUV0は再配置されず(元UVのまま)、温存した上描きスロットと同一UVを共有して正しく描ける。
            //     ポリゴン増ゼロ・スロット数は元アバターと同じ(実体化していた旧実装の三角形水増しを全廃)。
            // なお統合不可レンダラー(allowSlotMerge=false: マテリアル差し替えアニメのスロット番号維持が必要)は
            // スロット番号を変えられないため、従来どおり全余剰を削除も温存判定もせずネイティブ重ね描きとして温存する。
            var overdrawMats = new List<Material>();                 // 温存する余剰(ネイティブ重ね描き)
            if (slotCount > subMeshCount)
            {
                int overdrawnSubmesh = subMeshCount - 1;
                if (!allowSlotMerge)
                {
                    // スロット番号維持が必要 → 従来どおり複製せずネイティブ重ね描きとして温存(基底UVのみ再配置)。
                    for (int i = subMeshCount; i < slotCount; i++) overdrawMats.Add(mats[i]);
                }
                else
                {
                    Material baseSlotMat = mats[overdrawnSubmesh];
                    Rect baseCell = default(Rect);
                    bool baseAtlased = baseSlotMat != null && atlas.cellRects.TryGetValue(baseSlotMat, out baseCell);
                    int droppedCount = 0;
                    int keptCount = 0;
                    for (int i = subMeshCount; i < slotCount; i++)
                    {
                        Material om = mats[i];
                        if (om == null) { droppedCount++; continue; }                    // 何も描かない → 削除
                        if (IsHiddenMaterial(om, hiddenMaterials)) { droppedCount++; continue; } // 非表示 → 削除
                        // 冗長判定: 基底と同一マテリアル、または(両者アトラスで)同一セルへ写像される場合は削除可。
                        bool redundant = ReferenceEquals(om, baseSlotMat);
                        if (!redundant && baseAtlased)
                        {
                            Rect oc;
                            if (atlas.cellRects.TryGetValue(om, out oc) && oc == baseCell) redundant = true;
                        }
                        if (redundant) { droppedCount++; continue; }                    // 冗長 → 削除
                        // 可視の重ね描き = ネイティブ多重描画のまま温存(複製しない)。基底は事前に非アトラス化済み。
                        overdrawMats.Add(om);
                        keptCount++;
                    }
                    if (keptCount > 0)
                        report.Info(string.Format("'{0}': 可視の上描きスロット{1}件を元と同じ多重描画のまま保持しました(複製せずポリゴン増ゼロ。冗長・非表示{2}件は削除)。", renderer.name, keptCount, droppedCount));
                    else if (droppedCount > 0)
                        report.Info(string.Format("'{0}': 冗長・非表示の多重描画スロット{1}件を削除しました(ポリゴン数は増えません)。", renderer.name, droppedCount));
                }
            }

            int baseSlotCount = procMats.Count; // 独自出力サブメッシュを持つスロット数(実サブメッシュ + 実体化した余剰)

            // スロットが割り当たらない末尾サブメッシュ(スロット数 < サブメッシュ数)はそのまま保持
            var trailingIndices = new List<int[]>();
            var trailingTopology = new List<MeshTopology>();
            for (int j = realSlotCount; j < subMeshCount; j++)
            {
                trailingIndices.Add(mesh.GetIndices(j));
                trailingTopology.Add(mesh.GetTopology(j));
            }

            // ---- 頂点ごとの使用サブメッシュ数(AAOの共有頂点複製パターン) ----
            int vertexCount = mesh.vertexCount;
            var userCount = new int[vertexCount];
            var lastUser = new int[vertexCount]; // 0 = 未使用。サブメッシュIDは1始まり
            int submeshId = 1;
            for (int i = 0; i < baseSlotCount; i++, submeshId++)
            {
                CountVertexUsers(procIndices[i], userCount, lastUser, submeshId);
            }
            foreach (int[] indices in trailingIndices)
            {
                CountVertexUsers(indices, userCount, lastUser, submeshId);
                submeshId++;
            }

            // ---- メッシュ複製とUV0再配置 ----
            Mesh copy = UnityEngine.Object.Instantiate(mesh);
            // 名前にレイアウトハッシュを含める。設定変更でレイアウトが変わると別名=別アセットになり、
            // 旧レイアウトのメッシュ+旧アトラスを参照する古い複製は整合したまま残る(見た目が壊れない)。
            copy.name = mesh.name + AtlasMeshSuffix + atlas.EnsureLayoutHash();

            var uv0 = new List<Vector2>();
            copy.GetUVs(0, uv0);
            if (uv0.Count != vertexCount)
            {
                UnityEngine.Object.DestroyImmediate(copy);
                report.Warn(string.Format("'{0}': メッシュ '{1}' のUV0が頂点数と一致しないため、このレンダラーは据え置きます。", renderer.name, mesh.name));
                return;
            }

            var cloneSource = new List<int>();               // 追加頂点 → 元頂点インデックス
            var transformedInPlace = new bool[vertexCount];  // 二重変換防止
            for (int i = 0; i < baseSlotCount; i++)
            {
                Material slotMat = procMats[i];
                if (slotMat == null || !atlas.atlasMap.ContainsKey(slotMat)) continue;
                Rect cellRect;
                if (!atlas.cellRects.TryGetValue(slotMat, out cellRect)) continue;

                // 複数サブメッシュで共有される頂点は、このサブメッシュ専用のクローンへ差し替える
                // (サブメッシュ内の重複は1つのクローンへまとめる = AAOと同じパターン)
                var perSubmeshClone = new Dictionary<int, int>();
                int[] indices = procIndices[i];
                for (int k = 0; k < indices.Length; k++)
                {
                    int v = indices[k];
                    if (userCount[v] > 1)
                    {
                        int cloneIndex;
                        if (!perSubmeshClone.TryGetValue(v, out cloneIndex))
                        {
                            cloneIndex = vertexCount + cloneSource.Count;
                            cloneSource.Add(v);
                            // 共有頂点のuv0[v]は未変換のまま残っている(in-place変換はuserCount==1のみ)
                            uv0.Add(TransformUv(uv0[v], cellRect));
                            perSubmeshClone[v] = cloneIndex;
                        }
                        indices[k] = cloneIndex;
                    }
                    else if (!transformedInPlace[v])
                    {
                        uv0[v] = TransformUv(uv0[v], cellRect);
                        transformedInPlace[v] = true;
                    }
                }
            }

            // ---- クローン頂点ぶんの全チャンネル拡張 ----
            if (cloneSource.Count > 0)
            {
                AppendClonedVertices(copy, mesh, cloneSource, uv0);
            }
            else
            {
                copy.SetUVs(0, uv0);
            }

            // ---- サブメッシュ結合(同一アトラスマテリアルへ写像されるスロットを連結) ----
            var outIndices = new List<List<int[]>>();  // 出力サブメッシュごとの連結待ち配列
            var outTopology = new List<MeshTopology>();
            var outMats = new List<Material>();
            var outIndexByAtlasMat = new Dictionary<Material, int>();
            for (int i = 0; i < baseSlotCount; i++)
            {
                Material slotMat = procMats[i];
                Material atlasMat = null;
                if (slotMat != null) atlas.atlasMap.TryGetValue(slotMat, out atlasMat);

                // allowSlotMerge = false のレンダラーはスロットを結合しない
                // (アニメーションのバインディング番号 m_Materials.Array.data[N] を維持するため)
                if (allowSlotMerge && atlasMat != null)
                {
                    int existing;
                    if (outIndexByAtlasMat.TryGetValue(atlasMat, out existing) && outTopology[existing] == procTopology[i])
                    {
                        outIndices[existing].Add(procIndices[i]); // 結合(スロット消滅)
                        continue;
                    }
                    if (!outIndexByAtlasMat.ContainsKey(atlasMat))
                    {
                        outIndexByAtlasMat[atlasMat] = outMats.Count; // 先頭メンバーの出力位置を記録
                    }
                    // トポロジー不一致の場合は結合せず別サブメッシュのまま(参照は同一アトラスに
                    // なるため、スロットの重複排除はビルド時のAAOに委ねられる)
                }
                outIndices.Add(new List<int[]> { procIndices[i] });
                outTopology.Add(procTopology[i]);
                outMats.Add(slotMat); // 元(PC)マテリアルのまま = ApplyMaterialMapが後で差し替える
            }
            int slotEntryCount = outMats.Count;
            for (int j = 0; j < trailingIndices.Count; j++)
            {
                outIndices.Add(new List<int[]> { trailingIndices[j] });
                outTopology.Add(trailingTopology[j]);
            }

            // ---- インデックス書き込み ----
            int newVertexCount = vertexCount + cloneSource.Count;
            if (newVertexCount > 65535)
            {
                copy.indexFormat = IndexFormat.UInt32; // SetIndicesより前に必ず設定
            }
            copy.subMeshCount = outIndices.Count;      // 個別SetIndicesより前に必ず設定
            for (int i = 0; i < outIndices.Count; i++)
            {
                copy.SetIndices(ConcatIndexArrays(outIndices[i]), outTopology[i], i, false);
            }
            copy.bounds = mesh.bounds; // ジオメトリ不変のため元メッシュのバウンズをそのまま使用

            // ---- アセット保存と差し替え(実行間で安定したパスへ GUID を保持したまま上書き) ----
            string folder = outputDir + "/Meshes";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(copy.name) + ".asset");
            Mesh savedMesh = QuestAssetPersistence.SaveOrOverwriteMesh(copy, path);
            // 既存アセットへ上書きした場合、メモリ上の一時メッシュは不要
            // (アセット化されておらず、保存側で破棄されていないもののみ破棄する)
            if (savedMesh != null && !ReferenceEquals(savedMesh, copy) && copy != null && !AssetDatabase.Contains(copy))
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            if (savedMesh == null) savedMesh = copy;

            // 温存した余剰(多重描画)マテリアルを最終サブメッシュ担当スロットの後ろに付け足す。
            // Unity はマテリアル数 > サブメッシュ数のとき最終サブメッシュを追加マテリアルで重ね描きするため、
            // 三角形を複製せず元アバターと同じネイティブ多重描画構成を再現できる。overdrawMats に入るのは
            // 統合不可レンダラー(allowSlotMerge=false)の全余剰と、統合可能レンダラーで温存した可視の上描き
            // (事前パスで基底を非アトラス化し元UVを共有させたもの)。冗長・非表示の余剰は既に削除済み。
            var newMats = new Material[slotEntryCount + overdrawMats.Count];
            for (int i = 0; i < slotEntryCount; i++) newMats[i] = outMats[i];
            for (int i = 0; i < overdrawMats.Count; i++) newMats[slotEntryCount + i] = overdrawMats[i];
            AssignToRenderer(renderer, savedMesh, newMats);

            meshCache.Add(new MeshCacheEntry
            {
                sourceMesh = mesh,
                sourceMats = (Material[])mats.Clone(),
                newMesh = savedMesh,
                newMats = (Material[])newMats.Clone(),
                allowSlotMerge = allowSlotMerge,
            });
            report.Info(string.Format("'{0}': スロット {1}→{2}(メッシュ: {3})", renderer.name, mats.Length, newMats.Length, path));
        }

        /// <summary>インデックス配列が使用する頂点の「使用サブメッシュ数」を数える(同一サブメッシュ内の重複は1回)。</summary>
        private static void CountVertexUsers(int[] indices, int[] userCount, int[] lastUser, int submeshId)
        {
            foreach (int v in indices)
            {
                if (lastUser[v] != submeshId)
                {
                    lastUser[v] = submeshId;
                    userCount[v]++;
                }
            }
        }

        /// <summary>UV(0..1想定)をセル矩形へ写像する。許容誤差ぶんはクランプする。</summary>
        private static Vector2 TransformUv(Vector2 uv, Rect cellRect)
        {
            float u = Mathf.Clamp01(uv.x);
            float v = Mathf.Clamp01(uv.y);
            return new Vector2(cellRect.x + u * cellRect.width, cellRect.y + v * cellRect.height);
        }

        /// <summary>
        /// クローン頂点(cloneSource)ぶんだけ全頂点チャンネルを拡張して書き込む。
        /// uv0 は変換済みのリスト(クローンぶん追加済み)を受け取る。
        /// 【近似】ボーンウェイトは4本表現(Mesh.boneWeights)で複製する。5本以上の
        /// ウェイトを持つメッシュでは超過分が失われるが、Quest(モバイル)の描画は
        /// 4本までのため実用上の影響はない。
        /// </summary>
        private static void AppendClonedVertices(Mesh copy, Mesh source, List<int> cloneSource, List<Vector2> uv0)
        {
            int vertexCount = source.vertexCount;

            // ---- 先に旧頂点数のまま全チャンネルを読み出す ----
            var positions = new List<Vector3>();
            copy.GetVertices(positions);
            var normals = new List<Vector3>();
            copy.GetNormals(normals);
            var tangents = new List<Vector4>();
            copy.GetTangents(tangents);
            var colors = new List<Color32>();
            copy.GetColors(colors);
            BoneWeight[] boneWeights = copy.boneWeights;

            // UV1..7(次元を保って読む)
            var uvChannels = new object[8]; // List<Vector2/3/4> または null
            var uvDims = new int[8];
            for (int ch = 1; ch < 8; ch++)
            {
                int dim = copy.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + ch));
                uvDims[ch] = dim;
                if (dim == 2) { var l = new List<Vector2>(); copy.GetUVs(ch, l); uvChannels[ch] = l; }
                else if (dim == 3) { var l = new List<Vector3>(); copy.GetUVs(ch, l); uvChannels[ch] = l; }
                else if (dim == 4) { var l = new List<Vector4>(); copy.GetUVs(ch, l); uvChannels[ch] = l; }
            }

            // ブレンドシェイプ(旧頂点数のまま読み出し)
            List<BlendShapeData> shapes = ReadBlendShapes(copy);
            // 読み出したら、頂点数を変更する前に必ずクリアする(旧頂点数のブレンドシェイプが
            // 残ったまま SetVertices で頂点数を拡張しない。AAOと同じ「構築後に追加し直す」順序)
            if (shapes.Count > 0) copy.ClearBlendShapes();

            // ---- クローンぶん追加(存在判定は追加前のカウントで確定させる) ----
            bool hasNormals = normals.Count == vertexCount;
            bool hasTangents = tangents.Count == vertexCount;
            bool hasColors = colors.Count == vertexCount;
            foreach (int src in cloneSource)
            {
                positions.Add(positions[src]);
                if (hasNormals) normals.Add(normals[src]);
                if (hasTangents) tangents.Add(tangents[src]);
                if (hasColors) colors.Add(colors[src]);
            }

            // ---- 書き込み(頂点 → 各チャンネル → インデックスの順) ----
            copy.SetVertices(positions);
            if (normals.Count == positions.Count) copy.SetNormals(normals);
            if (tangents.Count == positions.Count) copy.SetTangents(tangents);
            if (colors.Count == positions.Count) copy.SetColors(colors);
            copy.SetUVs(0, uv0);
            for (int ch = 1; ch < 8; ch++)
            {
                if (uvDims[ch] == 2) AppendAndSetUvs(copy, ch, (List<Vector2>)uvChannels[ch], cloneSource, vertexCount);
                else if (uvDims[ch] == 3) AppendAndSetUvs(copy, ch, (List<Vector3>)uvChannels[ch], cloneSource, vertexCount);
                else if (uvDims[ch] == 4) AppendAndSetUvs(copy, ch, (List<Vector4>)uvChannels[ch], cloneSource, vertexCount);
            }
            if (boneWeights != null && boneWeights.Length == vertexCount && vertexCount > 0)
            {
                var newWeights = new BoneWeight[vertexCount + cloneSource.Count];
                Array.Copy(boneWeights, newWeights, vertexCount);
                for (int k = 0; k < cloneSource.Count; k++) newWeights[vertexCount + k] = boneWeights[cloneSource[k]];
                copy.boneWeights = newWeights;
            }
            if (shapes.Count > 0)
            {
                foreach (BlendShapeData shape in shapes)
                {
                    for (int f = 0; f < shape.weights.Count; f++)
                    {
                        copy.AddBlendShapeFrame(shape.name, shape.weights[f],
                            ExtendDeltas(shape.deltaVertices[f], cloneSource),
                            ExtendDeltas(shape.deltaNormals[f], cloneSource),
                            ExtendDeltas(shape.deltaTangents[f], cloneSource));
                    }
                }
            }
        }

        /// <summary>UVチャンネルへクローンぶんを追加して書き込む(存在チャンネルのみ)。</summary>
        private static void AppendAndSetUvs<T>(Mesh mesh, int channel, List<T> list, List<int> cloneSource, int vertexCount)
        {
            if (list == null || list.Count != vertexCount) return;
            foreach (int src in cloneSource) list.Add(list[src]);
            if (typeof(T) == typeof(Vector2)) mesh.SetUVs(channel, (List<Vector2>)(object)list);
            else if (typeof(T) == typeof(Vector3)) mesh.SetUVs(channel, (List<Vector3>)(object)list);
            else mesh.SetUVs(channel, (List<Vector4>)(object)list);
        }

        /// <summary>ブレンドシェイプ1個ぶんの全フレームデータ。</summary>
        private class BlendShapeData
        {
            public string name;
            public readonly List<float> weights = new List<float>();
            public readonly List<Vector3[]> deltaVertices = new List<Vector3[]>();
            public readonly List<Vector3[]> deltaNormals = new List<Vector3[]>();
            public readonly List<Vector3[]> deltaTangents = new List<Vector3[]>();
        }

        /// <summary>メッシュの全ブレンドシェイプを読み出す(頂点数は現在のメッシュに従う)。</summary>
        private static List<BlendShapeData> ReadBlendShapes(Mesh mesh)
        {
            var result = new List<BlendShapeData>();
            int vertexCount = mesh.vertexCount;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                var shape = new BlendShapeData { name = mesh.GetBlendShapeName(s) };
                int frames = mesh.GetBlendShapeFrameCount(s);
                for (int f = 0; f < frames; f++)
                {
                    var dv = new Vector3[vertexCount];
                    var dn = new Vector3[vertexCount];
                    var dt = new Vector3[vertexCount];
                    mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    shape.weights.Add(mesh.GetBlendShapeFrameWeight(s, f));
                    shape.deltaVertices.Add(dv);
                    shape.deltaNormals.Add(dn);
                    shape.deltaTangents.Add(dt);
                }
                result.Add(shape);
            }
            return result;
        }

        /// <summary>デルタ配列をクローンぶん拡張する(クローンは元頂点のデルタを複製)。</summary>
        private static Vector3[] ExtendDeltas(Vector3[] deltas, List<int> cloneSource)
        {
            var result = new Vector3[deltas.Length + cloneSource.Count];
            Array.Copy(deltas, result, deltas.Length);
            for (int k = 0; k < cloneSource.Count; k++) result[deltas.Length + k] = deltas[cloneSource[k]];
            return result;
        }

        /// <summary>複数のインデックス配列を1本へ連結する。</summary>
        private static int[] ConcatIndexArrays(List<int[]> arrays)
        {
            if (arrays.Count == 1) return arrays[0];
            int total = 0;
            foreach (int[] a in arrays) total += a.Length;
            var result = new int[total];
            int offset = 0;
            foreach (int[] a in arrays)
            {
                Array.Copy(a, 0, result, offset, a.Length);
                offset += a.Length;
            }
            return result;
        }

        /// <summary>メッシュとマテリアル配列をレンダラーへ割り当てる(SMRのlocalBoundsは既存値を維持)。</summary>
        private static void AssignToRenderer(Renderer renderer, Mesh newMesh, Material[] newMats)
        {
            Undo.RecordObject(renderer, UndoLabel);
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null)
            {
                smr.sharedMesh = newMesh; // localBoundsはジオメトリ不変のため触らない
            }
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    Undo.RecordObject(filter, UndoLabel);
                    filter.sharedMesh = newMesh;
                }
            }
            renderer.sharedMaterials = newMats;
        }

        private static bool MaterialsEqual(Material[] a, Material[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // ================================================================
        // 候補収集・チェック
        // ================================================================

        /// <summary>非パーティクルのメッシュ系レンダラーによるマテリアル使用箇所を収集する。</summary>
        private static Dictionary<Material, List<MeshSlotUse>> CollectMeshUsage(GameObject questRoot)
        {
            var usage = new Dictionary<Material, List<MeshSlotUse>>();
            foreach (Renderer renderer in questRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, questRoot.transform)) continue;
                Mesh mesh = GetRendererMesh(renderer); // SMR / MR(+MF)以外はnull = パーティクル系は対象外
                if (mesh == null || mesh.subMeshCount == 0) continue;
                Material[] mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    List<MeshSlotUse> list;
                    if (!usage.TryGetValue(mats[i], out list))
                    {
                        list = new List<MeshSlotUse>();
                        usage.Add(mats[i], list);
                    }
                    // 余剰スロット(サブメッシュ数超)は最終サブメッシュの重ね描きとして扱う
                    list.Add(new MeshSlotUse { mesh = mesh, submesh = Mathf.Min(i, mesh.subMeshCount - 1) });
                }
            }
            return usage;
        }

        /// <summary>
        /// 影ランプのグループキーを返す(ピクセル内容ベース。結果はキャッシュされる)。
        /// Blit → ReadPixels で読むため、Read/Write無効・圧縮済みのランプアセットでも取得できる。
        /// ランプはリニア保存のため、リニアRT+リニアTexture2Dで生値のまま読む。
        /// 読み取りに失敗した場合は従来どおり参照(インスタンスID)ベースのキーへフォールバックする
        /// (そのランプのマテリアルが統合されないだけで、安全側に倒れる)。
        /// </summary>
        private static string GetRampGroupKey(Texture ramp, Dictionary<Texture, string> cache)
        {
            if (ramp == null) return "0";
            string key;
            if (cache.TryGetValue(ramp, out key)) return key;
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = RenderTexture.GetTemporary(ramp.width, ramp.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(ramp, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(ramp.width, ramp.height, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, ramp.width, ramp.height), 0, 0);
                tex.Apply(false, false);
                byte[] raw = tex.GetRawTextureData();

                // FNV-1a 64bit
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < raw.Length; i++)
                {
                    hash ^= raw[i];
                    hash *= 1099511628211UL;
                }
                key = ramp.width + "x" + ramp.height + ":" + hash.ToString("x16");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(string.Format("[RARA QuestConverter] 影ランプ '{0}' の内容ハッシュ取得に失敗したため、参照ベースでグループ化します: {1}", ramp.name, ex.Message));
                key = "id:" + ramp.GetInstanceID();
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
            cache.Add(ramp, key);
            return key;
        }

        /// <summary>
        /// マットキャップ(USE_MATCAP)を持つ Toon Standard のグループキー用サブキーを返す。
        /// マットキャップテクスチャの同一性 + 合成タイプ(_MatcapType) + 量子化した強度(_MatcapStrength、0.05刻み)。
        /// 同一マットキャップのメンバーだけが同一グループになり、統合マテリアルが引き継ぐ先頭メンバーの
        /// マットキャップが全メンバーで妥当になる。
        /// </summary>
        private static string GetMatcapGroupKey(Material converted)
        {
            string texId = GetTextureIdentityKey(GetTexture(converted, "_Matcap"));
            int type = converted.HasProperty("_MatcapType") ? Mathf.RoundToInt(converted.GetFloat("_MatcapType")) : 0;
            float strength = converted.HasProperty("_MatcapStrength") ? converted.GetFloat("_MatcapStrength") : 1f;
            int strengthBucket = Mathf.RoundToInt(Mathf.Clamp01(strength) * 20f); // 0.05刻みで量子化
            return texId + ":" + type + ":" + strengthBucket;
        }

        /// <summary>テクスチャの安定した同一性キー(GUID:localId。取得不能なら instanceID)。null は "none"。</summary>
        private static string GetTextureIdentityKey(Texture tex)
        {
            if (tex == null) return "none";
            string guid;
            long localId;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out guid, out localId) && !string.IsNullOrEmpty(guid))
            {
                return guid + ":" + localId;
            }
            return "iid:" + tex.GetInstanceID();
        }

        /// <summary>マテリアルの全使用箇所でUV0が0..1(許容誤差付き)に収まっているか。</summary>
        private static bool CheckUvRange(List<MeshSlotUse> uses, Dictionary<Mesh, Vector2[]> uvCache, out string reason)
        {
            foreach (MeshSlotUse use in uses)
            {
                Vector2[] uv;
                if (!uvCache.TryGetValue(use.mesh, out uv))
                {
                    uv = use.mesh.uv;
                    uvCache.Add(use.mesh, uv);
                }
                if (uv == null || uv.Length == 0)
                {
                    reason = "UV0が存在しないため";
                    return false;
                }
                int[] indices = use.mesh.GetIndices(use.submesh);
                foreach (int idx in indices)
                {
                    if (idx >= uv.Length)
                    {
                        reason = "UV0が不完全なため";
                        return false;
                    }
                    Vector2 p = uv[idx];
                    if (p.x < -UvTolerance || p.x > 1f + UvTolerance ||
                        p.y < -UvTolerance || p.y > 1f + UvTolerance)
                    {
                        reason = "UVが0..1範囲外(タイリング使用)";
                        return false;
                    }
                }
            }
            reason = null;
            return true;
        }

        /// <summary>メインテクスチャのタイリング・オフセットが単位(1,1 / 0,0)か。</summary>
        private static bool IsMainTexStIdentity(Material mat)
        {
            if (!mat.HasProperty("_MainTex")) return true;
            Vector2 scale = mat.GetTextureScale("_MainTex");
            Vector2 offset = mat.GetTextureOffset("_MainTex");
            return Mathf.Abs(scale.x - 1f) < 0.0001f && Mathf.Abs(scale.y - 1f) < 0.0001f &&
                   Mathf.Abs(offset.x) < 0.0001f && Mathf.Abs(offset.y) < 0.0001f;
        }

        // ================================================================
        // パッキング
        // ================================================================

        /// <summary>
        /// グループのセルを最小の2の累乗正方形アトラスへタイトに詰めたうえで、実際に使われている
        /// 領域を囲む最小の非正方形2の累乗サイズ(atlasW×atlasH)を求めて返す。正方形詰めの
        /// 空き半分(例: 内容が下半分だけの 1024×512 相当)を切り落として無駄な黒領域を無くす。
        /// 収まらない場合は最大セルを半分に縮小し、縮小しきっても収まらなければ最小セルを
        /// 脱落させる(除外リストへ追加)。メンバーが2未満になったらfalse(グループ解散)を返す。
        /// </summary>
        private static bool FitCells(AtlasGroup group, int atlasMaxSize, List<string> excluded, ConversionReport report, out int atlasW, out int atlasH)
        {
            atlasW = 0;
            atlasH = 0;
            while (true)
            {
                int atlasSize;
                if (TryPackSquare(group.cells, atlasMaxSize, out atlasSize))
                {
                    // 正方形詰め後の実配置から、非正方形の最終寸法(atlasW/atlasH)を確定する。
                    // 以降の合成・矩形正規化・レポートはこの1箇所で確定した寸法を共有する。
                    ComputeTrimmedAtlasSize(group.cells, atlasMaxSize, out atlasW, out atlasH);
                    return true;
                }

                // 最大セル(面積)を半分へ縮小
                AtlasCell largest = null;
                foreach (AtlasCell c in group.cells)
                {
                    if (largest == null || c.width * c.height > largest.width * largest.height) largest = c;
                }
                if (largest != null && (largest.width > MinCellSize || largest.height > MinCellSize))
                {
                    largest.width = Mathf.Max(MinCellSize, largest.width / 2);
                    largest.height = Mathf.Max(MinCellSize, largest.height / 2);
                    report.Warn(string.Format("'{0}': アトラス({1}px)に収まらないため、セルを{2}x{3}へ縮小しました(解像度が低下します)。", largest.src.name, atlasMaxSize, largest.width, largest.height));
                    continue;
                }

                // これ以上縮小できない → 最小セル(優先度最下位)を脱落させて単独変換のまま残す
                AtlasCell smallest = null;
                foreach (AtlasCell c in group.cells)
                {
                    if (smallest == null || c.width * c.height < smallest.width * smallest.height) smallest = c;
                }
                if (smallest == null) return false;
                group.cells.Remove(smallest);
                report.Warn(string.Format("'{0}': アトラスに収まらないため統合から除外しました(単独マテリアルのまま変換されます)。", smallest.src.name));
                excluded.Add(smallest.src.name + ": アトラスに収まらないため(単独変換)");

                if (group.cells.Count < 2)
                {
                    if (group.cells.Count == 1)
                    {
                        excluded.Add(group.cells[0].src.name + ": 統合できる相手がなくなったため(単独変換)");
                    }
                    return false;
                }
            }
        }

        /// <summary>
        /// 正方形詰め後のセル配置(cell.rect は既に確定済み)から、実際に使われている領域を
        /// 囲む最小の非正方形2の累乗サイズを求める。各セルはガター込みのフットプリント右端
        /// (rect.x + rect.width + gutter)・上端(rect.y + rect.height + gutter)まで領域を占めるため、
        /// それらの最大値を2の累乗へ切り上げ、[64, atlasMaxSize] へクランプする。
        /// 正方形詰めで全セルが size×size に収まっている前提のため、結果は常に元の正方形サイズ以下。
        /// </summary>
        private static void ComputeTrimmedAtlasSize(List<AtlasCell> cells, int atlasMaxSize, out int atlasW, out int atlasH)
        {
            int usedW = 0;
            int usedH = 0;
            foreach (AtlasCell cell in cells)
            {
                usedW = Mathf.Max(usedW, cell.rect.x + cell.rect.width + GutterPixels);
                usedH = Mathf.Max(usedH, cell.rect.y + cell.rect.height + GutterPixels);
            }
            atlasW = Mathf.Clamp(Mathf.NextPowerOfTwo(usedW), 64, atlasMaxSize);
            atlasH = Mathf.Clamp(Mathf.NextPowerOfTwo(usedH), 64, atlasMaxSize);
        }

        /// <summary>
        /// 可変サイズのセルを最小の2の累乗正方形アトラスへタイトに詰める(スカイライン ボトムレフト法)。
        /// 高さ→幅の降順でソートし、面積下限・最大フットプリント下限から求めた開始サイズから
        /// 2の累乗で拡大しながら、全セルが収まる最小サイズを探す(上限 atlasMaxSize)。
        /// 各セルは周囲2×ガターぶんの余白(フットプリント)込みで配置し、cell.rect には
        /// ガター内側の実ピクセル矩形を設定する。収まらなければ false。
        /// </summary>
        private static bool TryPackSquare(List<AtlasCell> cells, int atlasMaxSize, out int atlasSize)
        {
            atlasSize = 0;
            if (cells.Count == 0) return false;

            // 高さ降順(同高なら幅降順)。背の高いセルを先に置くとスカイラインが安定して隙間が減る。
            // 同幅・同高のセルは元マテリアル名で決定的に順序付ける。List<T>.Sort は不安定で、
            // 同寸セルの最終配置が入力順(= materialMap の Dictionary 列挙順 = 実行間で変わる
            // インスタンスID順)に左右されるため、名前タイブレークが無いとレイアウトハッシュが
            // セッションをまたいで変わってしまう(孤立メッシュ蓄積・Finding#1 のその場上書き誘発)。
            var order = new List<AtlasCell>(cells);
            order.Sort((a, b) =>
            {
                int byHeight = b.height.CompareTo(a.height);
                if (byHeight != 0) return byHeight;
                int byWidth = b.width.CompareTo(a.width);
                if (byWidth != 0) return byWidth;
                string an = a.src != null ? a.src.name : string.Empty;
                string bn = b.src != null ? b.src.name : string.Empty;
                return string.CompareOrdinal(an, bn);
            });

            long totalArea = 0;
            int maxFootprint = 0;
            foreach (AtlasCell cell in order)
            {
                int fw = cell.width + GutterPixels * 2;
                int fh = cell.height + GutterPixels * 2;
                if (fw > atlasMaxSize || fh > atlasMaxSize) return false; // 単一セルが上限を超える
                totalArea += (long)fw * fh;
                maxFootprint = Mathf.Max(maxFootprint, Mathf.Max(fw, fh));
            }

            // 開始サイズ = 面積下限 sqrt(Σフットプリント) と 最大フットプリント長辺 の大きい方(2の累乗へ切り上げ)。
            int areaSide = Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1f, (float)totalArea)));
            int start = Mathf.NextPowerOfTwo(Mathf.Max(maxFootprint, areaSide));
            if (start < 1) start = 1;
            for (int size = start; size <= atlasMaxSize; size *= 2)
            {
                if (SkylinePack(order, size))
                {
                    atlasSize = size;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// スカイライン(ボトムレフト)法で order の全セルを size×size のビンへ配置する。
        /// 全セルを配置できたら true(各 cell.rect にガター内側のピクセル矩形を設定)、
        /// 1つでも収まらなければ false(部分的に設定済みの rect は次のサイズ試行で上書きされる)。
        /// </summary>
        private static bool SkylinePack(List<AtlasCell> order, int size)
        {
            var skyline = new List<SkylineNode> { new SkylineNode { x = 0, y = 0, width = size } };
            foreach (AtlasCell cell in order)
            {
                int fw = cell.width + GutterPixels * 2;
                int fh = cell.height + GutterPixels * 2;
                int px, py, index;
                if (!SkylineFindPosition(skyline, size, fw, fh, out px, out py, out index))
                {
                    return false;
                }
                SkylineAddLevel(skyline, index, px, py + fh, fw);
                cell.rect = new RectInt(px + GutterPixels, py + GutterPixels, cell.width, cell.height);
            }
            return true;
        }

        /// <summary>スカイラインの1区画。x から width ぶんの水平区間が高さ y まで埋まっていることを表す。</summary>
        private struct SkylineNode
        {
            public int x;
            public int y;
            public int width;
        }

        /// <summary>
        /// 幅 w・高さ h のフットプリントを置ける最下・最左の位置を探す(ボトムレフト)。
        /// 各区画の左端に左揃えで置いた場合の設置高さ(その区間の最大スカイライン高さ)を求め、
        /// y 最小・同値なら x 最小の位置を選ぶ。見つかれば true(px/py と配置起点の区画 index を設定)。
        /// </summary>
        private static bool SkylineFindPosition(List<SkylineNode> skyline, int size, int w, int h, out int px, out int py, out int index)
        {
            px = 0;
            py = 0;
            index = -1;
            int bestY = int.MaxValue;
            int bestX = int.MaxValue;
            for (int i = 0; i < skyline.Count; i++)
            {
                int y = SkylineSpanTop(skyline, i, w, size);
                if (y < 0) continue;        // この区画からは幅 w が収まらない
                if (y + h > size) continue; // 高さ方向にビンを超える
                int x = skyline[i].x;
                if (y < bestY || (y == bestY && x < bestX))
                {
                    bestY = y;
                    bestX = x;
                    px = x;
                    py = y;
                    index = i;
                }
            }
            return index >= 0;
        }

        /// <summary>区画 i の左端から幅 w ぶんを覆う区間の最大スカイライン高さを返す(ビン幅を超えるなら -1)。</summary>
        private static int SkylineSpanTop(List<SkylineNode> skyline, int i, int w, int size)
        {
            if (skyline[i].x + w > size) return -1;
            int widthLeft = w;
            int y = 0;
            for (int idx = i; idx < skyline.Count && widthLeft > 0; idx++)
            {
                if (skyline[idx].y > y) y = skyline[idx].y;
                widthLeft -= skyline[idx].width;
            }
            return widthLeft > 0 ? -1 : y;
        }

        /// <summary>
        /// フットプリント配置後にスカイラインを更新する。区画 index に新しい水平区画
        /// (x..x+w を高さ topY)を挿入し、覆われた後続区画を左から縮める・消去する。
        /// 最後に同高さの隣接区画を結合する(RectangleBinPack の SkylineBinPack と同じ流儀)。
        /// </summary>
        private static void SkylineAddLevel(List<SkylineNode> skyline, int index, int x, int topY, int w)
        {
            skyline.Insert(index, new SkylineNode { x = x, y = topY, width = w });
            for (int i = index + 1; i < skyline.Count; i++)
            {
                SkylineNode prev = skyline[i - 1];
                SkylineNode cur = skyline[i];
                if (cur.x < prev.x + prev.width)
                {
                    int shrink = prev.x + prev.width - cur.x;
                    cur.x += shrink;
                    cur.width -= shrink;
                    if (cur.width <= 0)
                    {
                        skyline.RemoveAt(i);
                        i--;
                    }
                    else
                    {
                        skyline[i] = cur;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            // 同高さの隣接区画を結合(スカイラインを最小構成に保つ)
            for (int i = 0; i < skyline.Count - 1; i++)
            {
                if (skyline[i].y == skyline[i + 1].y)
                {
                    SkylineNode merged = skyline[i];
                    merged.width += skyline[i + 1].width;
                    skyline[i] = merged;
                    skyline.RemoveAt(i + 1);
                    i--;
                }
            }
        }

        // ================================================================
        // アトラス合成
        // ================================================================

        private enum AtlasChannel { Main, Normal, Emission }

        /// <summary>
        /// 1チャンネルぶんのアトラスを合成した一時Texture2Dを返す(破棄は呼び出し側)。
        /// 各セルはベイクシェーダーのパス経由で Blit → ReadPixels し、CPU側で合成する。
        /// セル境界は外側8pxへ膨張(ディレーション)してミップマップの滲みを防ぐ。
        /// 【近似】エミッションは「セル = _EmissionMap × (_EmissionColor×_EmissionStrength)」を
        /// LDRへクランプしてベイクし、統合マテリアル側は白×強度1とする。強度>1のHDR発光は
        /// PC版より弱くなる。発光なし(_EmissionMap 未設定を含む)のメンバーのセルは
        /// 背景と同じ黒のまま残す(HasActiveEmission 参照)。
        /// </summary>
        private static Texture2D ComposeChannel(AtlasGroup group, int width, int height, AtlasChannel channel, Material blitMat, int passTint, int passUnpack, ConversionReport report)
        {
            bool linear = channel == AtlasChannel.Normal;
            Color32 background = linear ? new Color32(128, 128, 255, 255) : new Color32(0, 0, 0, 255);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;

            foreach (AtlasCell cell in group.cells)
            {
                Texture memberTex = null;
                Color tint = Color.white;
                int pass = passTint;

                switch (channel)
                {
                    case AtlasChannel.Main:
                        memberTex = GetTexture(cell.converted, "_MainTex");
                        if (memberTex == null) memberTex = Texture2D.whiteTexture; // 想定外だが白で埋める
                        break;

                    case AtlasChannel.Normal:
                        memberTex = GetTexture(cell.converted, "_BumpMap");
                        pass = passUnpack;
                        if (memberTex != null && !IsTextureStIdentity(cell.converted, "_BumpMap"))
                        {
                            report.Warn(string.Format("'{0}': ノーマルマップのタイリング/オフセット設定はアトラス統合では無視されます。", cell.src.name));
                        }
                        // null → フラットノーマル(背景色)のまま
                        break;

                    case AtlasChannel.Emission:
                        // エミッションアトラスは「発光箇所以外はすべて黒」が前提。
                        // 発光なしのメンバーは描画をスキップしてセルを背景(黒)のまま残す。
                        // ここで白テクスチャ等の代用で埋めてはいけない(非発光メンバーの
                        // セルが白く焼き込まれ、Questで純白に発光する不具合になる)。
                        // メンバーの発光判定は HasActiveEmission に集約(変換済みマテリアルの
                        // エミッション値を最終値として扱う。詳細はそちらのコメント参照)。
                        bool clamped;
                        if (!HasActiveEmission(cell.converted, out tint, out clamped))
                        {
                            tint = Color.black; // 発光なし → 黒(背景色)のまま
                            break;
                        }
                        memberTex = GetTexture(cell.converted, "_EmissionMap"); // HasActiveEmission が非nullを保証
                        if (!IsTextureStIdentity(cell.converted, "_EmissionMap"))
                        {
                            report.Warn(string.Format("'{0}': エミッションマップのタイリング/オフセット設定はアトラス統合では無視されます。", cell.src.name));
                        }
                        if (clamped)
                        {
                            report.Info(string.Format("'{0}': HDRエミッション(強度>1)をLDRへクランプしてベイクしました。発光がPC版より弱くなる可能性があります。", cell.src.name));
                        }
                        break;
                }

                if (memberTex != null && tint.maxColorComponent > 0.001f)
                {
                    Texture2D cellTex = BlitToReadable(memberTex, cell.rect.width, cell.rect.height, blitMat, pass, tint, linear);
                    CopyCellInto(pixels, width, cellTex, cell.rect.x, cell.rect.y);
                    UnityEngine.Object.DestroyImmediate(cellTex);
                }
                DilateCell(pixels, width, height, cell.rect, GutterPixels);
            }

            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            atlas.SetPixels32(pixels);
            atlas.Apply(false, false);
            return atlas;
        }

        /// <summary>
        /// テクスチャをベイクシェーダーの指定パスで Blit → ReadPixels して読み取り可能な
        /// Texture2D を返す(TextureBaker.RunBlit と同じ色空間パターン。ノーマル用は
        /// リニアRT+リニアTexture2Dで生値を保持する)。
        /// </summary>
        private static Texture2D BlitToReadable(Texture source, int width, int height, Material blitMat, int pass, Color tint, bool linearOutput)
        {
            blitMat.SetColor("_TintColor", tint);

            var previous = RenderTexture.active;
            RenderTexture rt = linearOutput
                ? RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                : RenderTexture.GetTemporary(width, height); // 既定 = リニアプロジェクトではsRGB
            Graphics.Blit(source, rt, blitMat, pass);
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, linearOutput);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        /// <summary>セルのピクセルをアトラス配列へコピーする(原点=左下、行単位コピー)。</summary>
        private static void CopyCellInto(Color32[] atlasPixels, int atlasWidth, Texture2D cellTex, int destX, int destY)
        {
            Color32[] src = cellTex.GetPixels32();
            int w = cellTex.width;
            int h = cellTex.height;
            for (int y = 0; y < h; y++)
            {
                Array.Copy(src, y * w, atlasPixels, (destY + y) * atlasWidth + destX, w);
            }
        }

        /// <summary>
        /// セルの端の行・列を外側gutterピクセルぶん複製する(ミップマップ縮小時の滲み防止)。
        /// セルはガター2倍ぶんの間隔で配置済みのため、隣のセル本体を上書きすることはない。
        /// </summary>
        private static void DilateCell(Color32[] pixels, int atlasWidth, int atlasHeight, RectInt rect, int gutter)
        {
            int x0 = rect.x;
            int y0 = rect.y;
            int x1 = rect.x + rect.width - 1;
            int y1 = rect.y + rect.height - 1;

            // 上下の行を外側へ複製
            for (int g = 1; g <= gutter; g++)
            {
                int below = y0 - g;
                int above = y1 + g;
                if (below >= 0) Array.Copy(pixels, y0 * atlasWidth + x0, pixels, below * atlasWidth + x0, rect.width);
                if (above < atlasHeight) Array.Copy(pixels, y1 * atlasWidth + x0, pixels, above * atlasWidth + x0, rect.width);
            }
            // 左右の列を外側へ複製(上下ガター帯も含めて角を埋める)
            int yStart = Mathf.Max(0, y0 - gutter);
            int yEnd = Mathf.Min(atlasHeight - 1, y1 + gutter);
            for (int y = yStart; y <= yEnd; y++)
            {
                Color32 left = pixels[y * atlasWidth + x0];
                Color32 right = pixels[y * atlasWidth + x1];
                for (int g = 1; g <= gutter; g++)
                {
                    if (x0 - g >= 0) pixels[y * atlasWidth + (x0 - g)] = left;
                    if (x1 + g < atlasWidth) pixels[y * atlasWidth + (x1 + g)] = right;
                }
            }
        }

        // ================================================================
        // アセット保存・マテリアル生成
        // ================================================================

        /// <summary>
        /// アトラステクスチャをPNGアセットとして保存する。インポーターの最大サイズには
        /// アトラス自身の寸法を渡し、意図しない縮小を防ぐ。一時テクスチャは保存後に破棄する。
        /// 保存先は実行間で安定したパス(ConversionAssetContext.Claim)で、既存の PNG があれば
        /// SaveTextureAsset が同じパスへ上書きして GUID を保持する。
        /// </summary>
        private static Texture2D SaveAtlasTexture(Texture2D tex, string baseName, string suffix, bool sRGB, bool isNormalMap, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets)
        {
            if (tex == null) return null;
            string folder = outputDir + "/Textures";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(folder + "/" + baseName + suffix + ".png");
            int importMaxSize = Mathf.Max(tex.width, tex.height);
            Texture2D saved = TextureBaker.SaveTextureAsset(tex, path, sRGB, isNormalMap, false, importMaxSize, settings.androidFormat);
            if (!ReferenceEquals(saved, tex) && !AssetDatabase.Contains(tex))
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            return saved;
        }

        /// <summary>
        /// グループの統合マテリアルを生成して保存する。非テクスチャの質感(リム等)は
        /// 先頭メンバーからコピーし、テクスチャ類をアトラスへ差し替える。
        /// 保存先は実行間で安定したパスで、既存アセットがあれば GUID を保持したまま内容だけ上書きする。
        /// </summary>
        private static Material CreateAtlasMaterial(AtlasGroup group, Texture2D mainAtlas, Texture2D normalAtlas, Texture2D emissionAtlas, string baseName, string outputDir, ConversionAssetContext assets)
        {
            Material first = group.cells[0].converted;
            var mat = new Material(first); // 質感は先頭マテリアルに統一
            mat.name = baseName;

            mat.SetTexture("_MainTex", mainAtlas);
            mat.SetTextureScale("_MainTex", Vector2.one);
            mat.SetTextureOffset("_MainTex", Vector2.zero);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);

            if (group.toonStandard)
            {
                if (mat.HasProperty("_Ramp")) mat.SetTexture("_Ramp", group.ramp);
                // _ShadowAlbedo はランプに連動する(生成ランプ→0 / 既定ランプ→0.5)。
                // 質感は先頭メンバーからコピーされるが、代表ランプは多数決で別メンバー由来のことがあるため、
                // ランプ統一時は _ShadowAlbedo を代表ランプと同じメンバーから取り直して組み合わせを揃える。
                if (group.representative != null && group.representative.converted != null &&
                    mat.HasProperty("_ShadowAlbedo") && group.representative.converted.HasProperty("_ShadowAlbedo"))
                {
                    mat.SetFloat("_ShadowAlbedo", group.representative.converted.GetFloat("_ShadowAlbedo"));
                }
                if (mat.HasProperty("_BumpMap"))
                {
                    mat.SetTexture("_BumpMap", normalAtlas);
                    mat.SetTextureScale("_BumpMap", Vector2.one);
                    mat.SetTextureOffset("_BumpMap", Vector2.zero);
                    if (normalAtlas != null) mat.EnableKeyword("USE_NORMAL_MAPS");
                    else mat.DisableKeyword("USE_NORMAL_MAPS");
                }
                // 【順序重要】new Material(first) は先頭メンバーのエミッション状態(残存する
                // 白色 _EmissionColor 等)をそのままコピーしているため、エミッション関連は
                // アセット保存(SaveOrOverwriteMaterial)の直前(最後のプロパティ書き込み)で必ず
                // アトラスの最終値へ上書きしてから保存する。これ以降にエミッションへ書き込む処理を
                // 追加しないこと(コピー値が残ったまま永続化される)。
                //   ・エミッションアトラスあり: map=アトラス / 色=白 / 強度=1
                //   ・なし:                     map=null    / 色=黒 / 強度=0
                if (mat.HasProperty("_EmissionMap"))
                {
                    mat.SetTexture("_EmissionMap", emissionAtlas); // null なら先頭メンバーのマップも明示的に外す
                    mat.SetTextureScale("_EmissionMap", Vector2.one);
                    mat.SetTextureOffset("_EmissionMap", Vector2.zero);
                }
                if (mat.HasProperty("_EmissionColor"))
                {
                    // 色×強度はセルへベイク済みのため、マテリアル側は白×1(発光なしなら黒)
                    mat.SetColor("_EmissionColor", emissionAtlas != null ? Color.white : Color.black);
                }
                if (mat.HasProperty("_EmissionStrength")) mat.SetFloat("_EmissionStrength", emissionAtlas != null ? 1f : 0f);
                if (mat.HasProperty("_EmissionUV")) mat.SetInt("_EmissionUV", 0);
            }
            mat.enableInstancing = true;

            string folder = outputDir + "/Materials";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(folder + "/" + baseName + ".mat");
            Material saved = QuestAssetPersistence.SaveOrOverwriteMaterial(mat, path);
            // 既存アセットへ上書きした場合、メモリ上の一時マテリアルは不要
            // (アセット化されておらず、保存側で破棄されていないもののみ破棄する)
            if (saved != null && !ReferenceEquals(saved, mat) && mat != null && !AssetDatabase.Contains(mat))
            {
                UnityEngine.Object.DestroyImmediate(mat);
            }
            return saved != null ? saved : mat;
        }

        /// <summary>
        /// ランプ統一グループの代表ランプを選んで group.ramp へ設定する。
        /// 代表 = メンバー中で最も多いランプ(内容ハッシュキーで数える)。
        /// 同数の場合は内容ハッシュキーが最小のものを決定的に採用する。
        /// メンバー間でランプが2種以上あった場合は一度だけ警告する
        /// (統合後は影のトーンがグループ内で共通になるため)。
        /// </summary>
        private static void UnifyGroupRamp(AtlasGroup group, ConversionReport report)
        {
            if (group.cells.Count == 0) return;

            // ランプ内容キーごとの出現数と代表テクスチャ・代表セルを集計
            var counts = new Dictionary<string, int>();
            var repByKey = new Dictionary<string, Texture>();
            var repCellByKey = new Dictionary<string, AtlasCell>();
            foreach (AtlasCell cell in group.cells)
            {
                string k = cell.rampKey ?? "0";
                int c;
                counts.TryGetValue(k, out c);
                counts[k] = c + 1;
                if (!repByKey.ContainsKey(k)) { repByKey[k] = cell.ramp; repCellByKey[k] = cell; }
            }

            // 最多数のキー(同数なら内容ハッシュキー最小)を代表に選ぶ
            string bestKey = null;
            int bestCount = -1;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (kv.Value > bestCount ||
                    (kv.Value == bestCount && string.CompareOrdinal(kv.Key, bestKey) < 0))
                {
                    bestKey = kv.Key;
                    bestCount = kv.Value;
                }
            }
            group.ramp = repByKey[bestKey];
            group.representative = repCellByKey[bestKey];

            if (counts.Count > 1)
            {
                report.Warn(string.Format("アトラス統合: 影ランプを統一しました(グループ内 {0}種→1種、{1}材質)。影のトーンが共通になります", counts.Count, group.cells.Count));
            }
        }

        /// <summary>先頭メンバーと質感パラメータが目立って異なるメンバーを警告する。</summary>
        private static void WarnMaterialParameterDifferences(AtlasGroup group, ConversionReport report)
        {
            Material first = group.cells[0].converted;
            for (int i = 1; i < group.cells.Count; i++)
            {
                Material other = group.cells[i].converted;
                foreach (string prop in ComparedFloatProps)
                {
                    if (!first.HasProperty(prop) || !other.HasProperty(prop)) continue;
                    if (Mathf.Abs(first.GetFloat(prop) - other.GetFloat(prop)) > 0.01f)
                    {
                        report.Warn(string.Format("'{0}': リム・明るさ等の質感が '{1}' と異なります。質感は先頭マテリアルに統一されます。", group.cells[i].src.name, first.name));
                        break;
                    }
                }
            }
        }

        // ================================================================
        // 共通ヘルパー
        // ================================================================

        /// <summary>
        /// メンバーのエミッションがアトラスベイク対象として有効か。
        /// 有効条件: _EmissionMap が設定済み、かつ 色×強度 が黒でない。
        /// 【前提】変換済みメンバーのエミッション(マップ・色・強度)は MaterialQuestConverter の
        /// 出力を最終値として扱い、ここで再解釈・再変換はしない。ただし _EmissionMap が
        /// 未設定のメンバーは「発光なし」とみなす — Toon Standard の _EmissionMap 既定値は
        /// 白のため、未設定メンバーを白テクスチャ代用でベイクすると、lilToon 由来の残存
        /// _EmissionColor(既定=白)を持つ非発光メンバーのセル全面が純白で焼き込まれ、
        /// Quest で「発光しないはずのオブジェクトが真っ白に光る」不具合になる。
        /// tint は返り値に関わらず LDR へクランプ済みの合成色(色×強度)を出力する
        /// (呼び出し側が「色はあるがマップ未設定」の報告に使う)。
        /// </summary>
        private static bool HasActiveEmission(Material mat, out Color tint, out bool clamped)
        {
            tint = Color.black;
            clamped = false;
            if (!mat.HasProperty("_EmissionColor")) return false;

            Color color = mat.GetColor("_EmissionColor");
            float strength = mat.HasProperty("_EmissionStrength") ? mat.GetFloat("_EmissionStrength") : 1f;
            Color combined = color * strength;
            if (combined.maxColorComponent > 1f) clamped = true;
            tint = new Color(Mathf.Clamp01(combined.r), Mathf.Clamp01(combined.g), Mathf.Clamp01(combined.b), 1f);
            return tint.maxColorComponent > 0.001f && GetTexture(mat, "_EmissionMap") != null;
        }

        /// <summary>プロパティが存在すればテクスチャを返す(無ければnull)。</summary>
        private static Texture GetTexture(Material mat, string property)
        {
            return mat.HasProperty(property) ? mat.GetTexture(property) : null;
        }

        /// <summary>
        /// セル1個の初期サイズ(px)を決める。長辺の上限を
        /// min(合成対象テクスチャの長辺, settings.maxTextureSize, 縮小計画, (積極縮小時)ディテール解析)とし、
        /// アスペクト比を保って width/height を求める。単色・低ディテールなテクスチャは
        /// QuestSizeEstimator.AnalyzeDetailSize により極端に小さいセル(例: 単色=16px)へ縮小される(GOAL 2)。
        /// 元テクスチャ・合成対象テクスチャは一切変更しない。
        /// </summary>
        private static void PlanCellSize(AtlasCell cell, QuestConvertSettings settings)
        {
            Texture memberTex = GetTexture(cell.converted, "_MainTex"); // アトラスへ合成される変換済みテクスチャ
            int srcW = memberTex != null ? Mathf.Max(1, memberTex.width) : 64;
            int srcH = memberTex != null ? Mathf.Max(1, memberTex.height) : 64;
            int longEdge = Mathf.Max(srcW, srcH);
            int maxCell = Mathf.Max(MinDetailCellSize, settings.maxTextureSize);

            // 上限: 元寸法 と maxTextureSize
            int target = Mathf.Min(longEdge, maxCell);

            // 上限: 縮小計画(元テクスチャに計画があればその長辺で頭打ち)
            int planned = GetPlannedSize(settings, GetTexture(cell.src, "_MainTex"));
            if (planned > 0) target = Mathf.Min(target, planned);

            // 上限: 単色・低ディテール検出(積極縮小オン時のみ。元/合成テクスチャは変更しない)
            cell.detailShrunk = false;
            cell.detailPx = 0;
            if (settings.aggressiveTextureReduction && memberTex != null)
            {
                int detail = QuestSizeEstimator.AnalyzeDetailSize(memberTex, target);
                if (detail > 0 && detail < target)
                {
                    target = detail;
                    cell.detailShrunk = true;
                }
            }

            target = Mathf.Clamp(target, MinDetailCellSize, maxCell);

            // アスペクト比を保って長辺を target へ縮小する(短辺は比率でスケール)
            float scale = (float)target / longEdge;
            cell.width = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), MinDetailCellSize, maxCell);
            cell.height = Mathf.Clamp(Mathf.RoundToInt(srcH * scale), MinDetailCellSize, maxCell);
            if (cell.detailShrunk) cell.detailPx = Mathf.Max(cell.width, cell.height);
        }

        /// <summary>
        /// テクスチャに対応する縮小計画(settings.textureSizePlan)の目標サイズを返す(計画なしは0)。
        /// GUIDで照合し、複数該当時は最小値を採用する(MaterialQuestConverter.TryGetPlannedSize と同じ規則)。
        /// </summary>
        private static int GetPlannedSize(QuestConvertSettings settings, Texture tex)
        {
            if (tex == null || settings == null || settings.textureSizePlan == null || settings.textureSizePlan.Count == 0)
            {
                return 0;
            }
            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out guid, out localId) || string.IsNullOrEmpty(guid))
            {
                return 0;
            }
            int result = 0;
            foreach (TextureSizePlanEntry entry in settings.textureSizePlan)
            {
                if (entry == null || entry.targetSize <= 0 || entry.textureGuid != guid) continue;
                if (result == 0 || entry.targetSize < result) result = entry.targetSize;
            }
            return result;
        }

        /// <summary>テクスチャプロパティのST(タイリング・オフセット)が単位か。</summary>
        private static bool IsTextureStIdentity(Material mat, string property)
        {
            if (!mat.HasProperty(property)) return true;
            Vector2 scale = mat.GetTextureScale(property);
            Vector2 offset = mat.GetTextureOffset(property);
            return Mathf.Abs(scale.x - 1f) < 0.0001f && Mathf.Abs(scale.y - 1f) < 0.0001f &&
                   Mathf.Abs(offset.x) < 0.0001f && Mathf.Abs(offset.y) < 0.0001f;
        }

        /// <summary>名前でシェーダーパスのインデックスを探す(大文字小文字を無視。見つからなければ-1)。</summary>
        private static int FindPassIndex(Material mat, string passName)
        {
            for (int i = 0; i < mat.passCount; i++)
            {
                if (string.Equals(mat.GetPassName(i), passName, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>SkinnedMeshRenderer / MeshRenderer(+MeshFilter)のメッシュを取得する(それ以外はnull)。</summary>
        private static Mesh GetRendererMesh(Renderer renderer)
        {
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            if (renderer is MeshRenderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }
            return null;
        }

        /// <summary>tがroot配下のEditorOnlyサブツリー(自身または祖先にEditorOnlyタグ)に含まれるか。</summary>
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

        /// <summary>フォルダパスを "Assets/x/y" 形式へ正規化する。</summary>
        private static string NormalizeFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "Assets";
            return folder.Replace('\\', '/').TrimEnd('/');
        }

        // ================================================================
        // 古い複製(古いレイアウトの生成物)の検出 — UIプリフライト警告用
        // ================================================================

        /// <summary>
        /// avatar のシーンに、現世代の生成メッシュとは異なるレイアウトハッシュのアトラスメッシュを
        /// 参照する「古い複製」が残っていないかを安価に判定する(ウィンドウのプリフライト警告用)。
        /// cloneName は複製の基準名("{avatar}_Opt" / "{avatar}_Quest")。generatedMeshesFolder は
        /// 現世代メッシュの保存先フォルダ("{outputDir}/Meshes")。
        /// 判定不能・例外時は false(誤警告を出さない安全側)。決して例外を投げない。
        /// </summary>
        public static bool SceneHasStaleAtlasClone(GameObject avatar, string cloneName, string generatedMeshesFolder)
        {
            try
            {
                if (avatar == null || string.IsNullOrEmpty(cloneName)) return false;
                UnityEngine.SceneManagement.Scene scene = avatar.scene;
                if (!scene.IsValid()) return false;

                HashSet<string> currentHashes = CollectLatestGenerationHashes(generatedMeshesFolder);
                if (currentHashes.Count == 0) return false; // 現世代の生成物が無ければ比較不能=警告しない

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        GameObject go = t.gameObject;
                        if (go == avatar) continue;
                        // 元アバターとその階層(祖先・子孫)は対象外(自分自身の複製検出で誤検知しない)
                        if (IsWithin(go.transform, avatar.transform) || IsWithin(avatar.transform, go.transform)) continue;
                        if (!IsGeneratedCloneName(go.name, cloneName)) continue;

                        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r == null) continue;
                            Mesh m = GetRendererMesh(r);
                            string h = ExtractLayoutHash(m != null ? m.name : null);
                            if (h != null && !currentHashes.Contains(h)) return true; // 旧世代のレイアウトを参照=古い複製
                        }
                    }
                }
            }
            catch
            {
                // 補助的なUI判定のため、失敗しても警告は出さない(never throwing)
            }
            return false;
        }

        /// <summary>
        /// 生成メッシュフォルダから「最新世代」のレイアウトハッシュ集合を返す。
        /// フォルダには過去世代の孤立メッシュも残るため、最終更新時刻が最も新しいものから
        /// 一定時間内(単一変換は数秒で全メッシュを書く)に書かれたアセットだけを最新世代とみなす。
        /// </summary>
        private static HashSet<string> CollectLatestGenerationHashes(string meshesFolder)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(meshesFolder) || !AssetDatabase.IsValidFolder(meshesFolder)) return result;

            var entries = new List<KeyValuePair<string, DateTime>>();
            DateTime newest = DateTime.MinValue;
            foreach (string guid in AssetDatabase.FindAssets("t:Mesh", new[] { meshesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                string h = ExtractLayoutHash(System.IO.Path.GetFileNameWithoutExtension(path));
                if (h == null) continue;
                DateTime wt;
                try { wt = System.IO.File.GetLastWriteTimeUtc(path); }
                catch { wt = DateTime.MinValue; }
                entries.Add(new KeyValuePair<string, DateTime>(h, wt));
                if (wt > newest) newest = wt;
            }
            if (entries.Count == 0) return result;

            DateTime threshold = newest - TimeSpan.FromSeconds(300); // 直近の1変換ぶんを最新世代とみなす
            foreach (KeyValuePair<string, DateTime> e in entries)
            {
                if (e.Value >= threshold) result.Add(e.Key);
            }
            return result;
        }

        /// <summary>メッシュ/ファイル名末尾の "_QuestAtlas_{8桁hex}" からレイアウトハッシュ(小文字hex)を取り出す。無ければnull。</summary>
        private static string ExtractLayoutHash(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int idx = name.LastIndexOf(AtlasMeshSuffix, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + AtlasMeshSuffix.Length;
            if (start + 8 > name.Length) return null;
            for (int i = 0; i < 8; i++)
            {
                char c = name[start + i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            return name.Substring(start, 8).ToLowerInvariant();
        }

        /// <summary>node が ancestor 自身、またはその子孫か。</summary>
        private static bool IsWithin(Transform node, Transform ancestor)
        {
            for (Transform p = node; p != null; p = p.parent)
            {
                if (p == ancestor) return true;
            }
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
    }
}
#endif
