// RARA PC軽量化ツール - PC向けマテリアルアトラス統合モジュール
// ---------------------------------------------------------------------------
// 目的: PCアバターの「マテリアルスロット数(materialCount)」を減らすため、互換性のある
//       複数マテリアルを1枚のアトラス(1マテリアル)へ統合する。QuestConverter のアトラサーが
//       Quest変換済み(Toon Standard / Toon Lit)専用なのに対し、こちらは PC 側で「元シェーダーを
//       維持したまま」任意のシェーダー(lilToon / Standard / Toon Standard …)をシェーダー名ごとに
//       グループ化して統合する。
//
// 【役割分担 / QuestConverter の再利用】
//   1. グループ化・テクスチャ合成・アトラスマテリアル生成(＝AtlasBuildResult の構築)…… 本モジュール
//   2. メッシュのUV0をアトラスセルへ再配置＋サブメッシュ結合＋アニメスロット保護 ……
//      RARA.QuestConverter.MaterialAtlasser.RemapMeshesAndMergeSlots(public)を再利用。
//   3. レンダラーの sharedMaterials を「元マテリアル → アトラスマテリアル」へ差し替え ……
//      QuestConverter の該当処理(ApplyMaterialMap)は private のため、本モジュールで最小実装する。
//   合成の色空間・向きは QuestConverter と同一の「ベイクシェーダー(Hidden/RARA/QuestBake)の
//   Blit → ReadPixels」パターンに合わせる(実績のある正しい向き・色再現)。
//
// 【lilToon 等の注意(ユーザー承認済みの近似)】
//   lilToon のシェーディングをベイクしない。割り当て済みの「生テクスチャ」をそのままアトラスへ
//   詰め、アトラスマテリアルは代表メンバーのコピー(＝元シェーダーを保持)にテクスチャだけ差し替える。
//
// 【色をアトラスへ焼き込む(ユーザーの明示要望)】
//   ・_MainTex を持つメンバー: セル = テクスチャ × _Color(TintCopyパスで乗算)。アトラス側 _Color=白。
//   ・_MainTex 無しのメンバー(色のみ材質): 16pxの単色セルへ _Color を塗る。アトラス側 _Color=白。
//   ・エミッション: メンバーの発光色(×強度)を、_EmissionMap があれば「マップ×色」、無ければ単色セルへ
//     焼き込む。アトラス側 _EmissionColor=白＋発光有効(グループ内に発光メンバーが1つでもある場合のみ)。
//     非発光メンバーのエミッションセルは必ず黒のまま残す(白焼き込みバグ回避)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RARA.QuestConverter;

namespace RARA.PCOptimizer
{
    /// <summary>
    /// PC向けマテリアルアトラス統合。BuildAndApply が「アトラス構築 → メッシュUV再配置(再利用) →
    /// マテリアル差し替え」の一連を実行する。元アバターは触らず、渡された clone(＝_Opt複製)のみを編集する。
    /// </summary>
    public static class PCMaterialAtlasser
    {
        private const string ProgressTitle = "RARA PC軽量化";
        private const string UndoLabel = "PC軽量化(アトラス)";

        /// <summary>セル周囲のガター幅(px)。ミップマップ縮小時の隣セル滲みを防ぐ。QuestConverter と同値。</summary>
        private const int GutterPixels = 8;

        /// <summary>オーバーフロー時にセルを縮小できる最小サイズ(px)。</summary>
        private const int MinCellSize = 32;

        /// <summary>色のみ材質(テクスチャ無し)の単色セルの一辺(px)。</summary>
        private const int ColorCellSize = 16;

        /// <summary>UV0が0..1に収まっているとみなす許容誤差。</summary>
        private const float UvTolerance = 0.001f;

        /// <summary>メインテクスチャ / メインカラーのプロパティ候補(先頭から存在するものを採用)。</summary>
        private static readonly string[] MainTexProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_MainTexture" };
        private static readonly string[] MainColorProps = { "_Color", "_BaseColor", "_MainColor", "_Tint" };

        // ================================================================
        // シェーダー系統別のチャンネルプロパティ(NonToon 対応)
        // ================================================================
        // NonToon(jp.lilxyzw.nontoon)はメイン=_BaseTexture・ノーマル=_NormalMap で、メインカラー(色乗算)と
        // エミッションを持たない。既定表(_MainTex/_BumpMap/_Color 系)では解決できないため、系統別に振り分ける。
        // 未知の系統は必ず既定表へフォールバックし、現行動作を一切変えない。

        /// <summary>NonToon 系(NonToon / NonToonFur)のメインテクスチャ候補。</summary>
        private static readonly string[] NonToonMainTexProps = { "_BaseTexture" };

        /// <summary>NonToon 系のメインカラー候補(NonToon に色乗算プロパティは無いため空 = 色は白のまま)。</summary>
        private static readonly string[] NonToonMainColorProps = new string[0];

        /// <summary>NonToon(jp.lilxyzw.nontoon)系シェーダーか。名前は "NonToon" / "NonToonFur"(前方一致で判定)。</summary>
        private static bool IsNonToonShader(Shader shader)
        {
            return shader != null && shader.name.StartsWith("NonToon", StringComparison.Ordinal);
        }

        /// <summary>メインテクスチャ候補をシェーダー系統別に返す(未知系統は既定表=現行動作)。</summary>
        private static string[] MainTexPropsFor(Material mat)
        {
            return mat != null && IsNonToonShader(mat.shader) ? NonToonMainTexProps : MainTexProps;
        }

        /// <summary>メインカラー候補をシェーダー系統別に返す(NonToon は色乗算無し = 空)。</summary>
        private static string[] MainColorPropsFor(Material mat)
        {
            return mat != null && IsNonToonShader(mat.shader) ? NonToonMainColorProps : MainColorProps;
        }

        /// <summary>ノーマルマップのプロパティ名をシェーダー系統別に返す(既定 _BumpMap / NonToon _NormalMap)。</summary>
        private static string NormalTexPropFor(Material mat)
        {
            return mat != null && IsNonToonShader(mat.shader) ? "_NormalMap" : "_BumpMap";
        }

        /// <summary>系統別のノーマルマップテクスチャを返す(無ければ null)。</summary>
        private static Texture ResolveNormalTexture(Material mat)
        {
            if (mat == null) return null;
            string prop = NormalTexPropFor(mat);
            return mat.HasProperty(prop) ? mat.GetTexture(prop) : null;
        }

        /// <summary>
        /// 先頭メンバーからコピーされる質感パラメータのうち、差異があれば警告する既知floatプロパティ。
        /// 統合マテリアルは代表メンバーの非テクスチャパラメータを全継承するため、見た目に効く項目を広めに比較する。
        /// (テクスチャ・色はセルへ焼き込むので比較しない)
        /// </summary>
        private static readonly string[] ComparedFloatProps =
        {
            // lilToon
            "_ShadowBorder", "_ShadowBlur", "_RimBorder", "_RimBlur", "_RimFresnelPower",
            // アウトライン(アウトライン統一時は代表メンバーへ寄るため、太さの差異を検出して警告する)
            "_OutlineWidth",
            // 汎用 / Standard
            "_Metallic", "_Glossiness", "_Smoothness", "_BumpScale", "_OcclusionStrength", "_Cutoff",
            // Toon Standard
            "_ShadowBoost", "_ShadowAlbedo", "_Reflectance", "_GlossStrength", "_MetallicStrength",
            "_RimIntensity", "_MinBrightness", "_MatcapStrength",
        };

        /// <summary>
        /// アトラス化されない副次テクスチャ。統合マテリアルは代表(先頭)メンバーのこれらを全継承するため、
        /// メンバー間で異なると質感(メタリック/スムースネス/マットキャップ/ディテール/AO/2nd・3rdレイヤー等)が
        /// 黙って先頭に統一される。差異があれば警告する(メイン/ノーマル/エミッションのみ焼き込むため)。
        /// </summary>
        private static readonly string[] SecondaryTexProps =
        {
            "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
            "_DetailMap", "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask",
            "_MatcapTex", "_Matcap", "_MatCap",
            "_2ndTex", "_2ndMainTex", "_3rdTex",
            // アウトライン(統一時は代表メンバーへ寄るため、太さマスク/テクスチャの差異も警告する)
            "_OutlineTex", "_OutlineWidthMask",
            // NonToon: 影ランプの元(_SharedGradients)・共有マスク(_SharedMask)。異なると影色・マスクが黙って先頭に統一される。
            "_SharedGradients", "_SharedMask",
        };

        // ================================================================
        // 内部データ構造
        // ================================================================

        /// <summary>アトラス内の1セル(＝元マテリアル1個ぶんの領域)。</summary>
        private sealed class Cell
        {
            public Material src;          // 元(PC)マテリアル ＝ テクスチャ・色の供給元
            public bool colorOnly;        // _MainTex 無し → 単色セル
            public Color mainTint;        // メインセルへ焼き込む色(× テクスチャ)
            public bool emits;            // 発光メンバーか
            public Color emissionTint;    // 発光色 × 強度(LDRクランプ済み)
            public bool emissionClamped;  // HDR発光をLDRへクランプしたか(レポート用)
            public bool hasBump;          // ノーマルマップ(既定 _BumpMap / NonToon _NormalMap)を持つか
            public int width;             // セル幅(px)
            public int height;            // セル高さ(px)
            public RectInt rect;          // アトラス内ピクセル矩形(原点=左下)
        }

        /// <summary>統合互換グループ(同一シェーダー系統・カリング設定に依存)。</summary>
        private sealed class Group
        {
            public Shader shader;
            public readonly List<Cell> cells = new List<Cell>();
            public bool needNormal;
            public bool needEmission;
            public bool madeTwoSided; // グループ内でカリング値が混在し、Cull Off(両面)へ統一したか
            public bool repNeedsPlainShaderSwap; // モード1で代表がアウトライン版しか無く、アトラスコピーをプレーンlilToonへ差し替えるか
        }

        /// <summary>マテリアルのメッシュ上での使用箇所(UV範囲チェック用)。</summary>
        private sealed class MeshSlotUse
        {
            public Mesh mesh;
            public int submesh;
        }

        // ================================================================
        // 公開API: 構築 → 再配置 → 差し替え
        // ================================================================

        /// <summary>
        /// clone(＝_Opt複製)のマテリアルをアトラス統合してスロット数を削減する。
        /// 元アバターには触れない。生成アセットは outputDir 配下(Textures / Materials)へ
        /// GUID安定パスで保存する。settings.enableAtlas が false のときは何もしない。
        /// </summary>
        public static void BuildAndApply(GameObject clone, PCOptimizeSettings settings, string outputDir, ConversionReport report, ConversionAssetContext assets, Dictionary<Material, Material> originalToCloneMap = null)
        {
            if (report == null) report = new ConversionReport();
            if (clone == null || settings == null) return;
            if (!settings.enableAtlas)
            {
                report.Info("マテリアルアトラス統合: 設定で無効のためスキップしました。");
                return;
            }
            if (assets == null) assets = new ConversionAssetContext(); // 単体呼び出し用(通常はオーケストレーターから渡される)
            outputDir = NormalizeFolder(outputDir);

            AtlasBuildResult atlas = BuildAtlases(clone, settings, outputDir, report, assets, originalToCloneMap);

            // 除外理由をレポートへ(情報として一覧化)
            if (atlas != null && atlas.excluded != null)
            {
                foreach (string reason in atlas.excluded)
                {
                    report.Info("アトラス対象外 " + reason);
                }
            }

            if (atlas == null || atlas.atlasMap == null || atlas.atlasMap.Count == 0)
            {
                report.Info("マテリアルアトラス統合: 統合可能なマテリアルグループがありませんでした。");
                return;
            }

            // ---- メッシュ側(UV再配置・サブメッシュ結合・アニメスロット保護)は QuestConverter を再利用 ----
            // 【重要】この時点でレンダラーのスロットはまだ元(PC)マテリアルを指している必要がある。
            MaterialAtlasser.RemapMeshesAndMergeSlots(clone, atlas, outputDir, report, assets);

            // ---- 最終差し替え: 元(PC)マテリアル → アトラスマテリアル ----
            int swapped = ApplyMaterialMap(clone, atlas.atlasMap, report);

            var distinctAtlas = new HashSet<Material>();
            foreach (Material m in atlas.atlasMap.Values) if (m != null) distinctAtlas.Add(m);
            report.Info(string.Format("PCマテリアルアトラス: {0}グループを統合し、{1}スロットをアトラスマテリアルへ置換しました。",
                distinctAtlas.Count, swapped));
        }

        // ================================================================
        // アトラス構築(PC側グループ化・テクスチャ合成・マテリアル生成)
        // ================================================================

        private static AtlasBuildResult BuildAtlases(GameObject clone, PCOptimizeSettings settings, string outputDir, ConversionReport report, ConversionAssetContext assets, Dictionary<Material, Material> originalToCloneMap = null)
        {
            var result = new AtlasBuildResult
            {
                atlasMap = new Dictionary<Material, Material>(),
                cellRects = new Dictionary<Material, Rect>(),
                excluded = new List<string>(),
            };

            int atlasMaxSize = Mathf.Clamp(settings.atlasMaxSize <= 0 ? 2048 : settings.atlasMaxSize, 256, 8192);
            HashSet<Material> animationExcluded = CollectAnimationExcludedMaterials(clone);
            // MA Material Setter / Material Swap が参照するマテリアルは詰め込みから除外する(MA未導入時は空集合)。
            HashSet<Material> maReferenced = MACompatUtility.CollectReferencedMaterials(clone);
            // 除外指定は「元アバターのマテリアルGUID」で記録されている。実行はGUIDが異なる複製マテリアル上で
            // 走るため、元GUIDが除外対象なら対応する複製マテリアルのGUIDも除外集合へ追加する
            // (元GUIDも残す: 複製されなかったマテリアルはクローンでも元を参照しているため)。
            HashSet<string> excludeGuids = TranslateExcludeGuids(settings.atlasExcludeMaterialGuids, originalToCloneMap);

            Material blitMat = null;
            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, "アトラス候補を収集中...", 0.02f);

                // ---- 1. 候補収集とグループ化 ----
                Dictionary<Material, List<MeshSlotUse>> meshUsage = CollectMeshUsage(clone);
                var uvCache = new Dictionary<Mesh, Vector2[]>();
                var groups = new Dictionary<string, Group>();

                foreach (KeyValuePair<Material, List<MeshSlotUse>> pair in meshUsage)
                {
                    Material src = pair.Key;
                    // 合否判定・グループキーはプレビューと共有する分類器へ委譲する(preview==execution)。
                    AtlasClass cls = Classify(src, pair.Value, settings, animationExcluded, maReferenced, excludeGuids, uvCache);
                    if (!cls.eligible)
                    {
                        if (src != null && cls.reason != null) result.excluded.Add(src.name + ": " + cls.reason);
                        continue;
                    }

                    // エミッション情報はセル合成に必要なため分類後に取得する(合否は分類器が確定済み)。
                    Color emissionTint = Color.black;
                    bool emits = false;
                    bool clamped = false;
                    if (settings.atlasBakeEmissionMask)
                    {
                        Texture emiMap;
                        emits = HasActiveEmission(src, out emissionTint, out clamped, out emiMap);
                        if (emits && emiMap != null && !IsTextureStIdentity(src, "_EmissionMap"))
                        {
                            report.Warn(string.Format("'{0}': エミッションマップのタイリング/オフセット設定はアトラス統合では無視されます。", src.name));
                        }
                    }

                    var cell = new Cell
                    {
                        src = src,
                        colorOnly = cls.colorOnly,
                        mainTint = GetMainColor(src),
                        emits = emits,
                        emissionTint = emissionTint,
                        emissionClamped = clamped,
                        hasBump = ResolveNormalTexture(src) != null,
                    };
                    PlanCellSize(cell, settings, atlasMaxSize);

                    Group group;
                    if (!groups.TryGetValue(cls.key, out group))
                    {
                        group = new Group { shader = src.shader };
                        groups.Add(cls.key, group);
                    }
                    group.cells.Add(cell);
                }

                // ---- 2. 単独グループの除外(統合相手が居ない) ----
                var mergeGroups = new List<Group>();
                foreach (Group group in groups.Values)
                {
                    if (group.cells.Count < 2)
                    {
                        Cell only = group.cells[0];
                        string loneReason = ShouldEyeGuard(only.src, settings.atlasOutlineUnifyMode)
                            ? "瞳・顔系のためアウトライン付与を回避(プレーンのまま単独維持)"
                            : "統合できる相手がないため(単独グループ)";
                        result.excluded.Add(only.src.name + ": " + loneReason);
                        continue;
                    }
                    mergeGroups.Add(group);
                }
                if (mergeGroups.Count == 0) return result;

                // ---- 3. ベイクシェーダーのパス確認(無ければCPUフォールバック) ----
                Shader bakeShader = Shader.Find(QuestCompat.BakeShaderName);
                int passTint = -1;
                int passUnpack = -1;
                if (bakeShader != null)
                {
                    blitMat = new Material(bakeShader);
                    passTint = FindPassIndex(blitMat, "TintCopy");
                    passUnpack = FindPassIndex(blitMat, "UnpackNormal");
                }
                if (bakeShader == null || passTint < 0)
                {
                    report.Warn(string.Format("アトラス合成シェーダー '{0}'(TintCopyパス)が見つからないため、色合成はCPU近似で行います。色味が僅かに変わる場合があります。", QuestCompat.BakeShaderName));
                }

                // ---- 4. グループごとにパッキング → 合成 → マテリアル生成 ----
                for (int gi = 0; gi < mergeGroups.Count; gi++)
                {
                    Group group = mergeGroups[gi];
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
                        foreach (Cell cell in group.cells)
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
            // 結果確定後にレイアウトハッシュを算出する(remap 済みメッシュ名に使う。QuestConverter 側と共通実装)。
            result.EnsureLayoutHash();
            return result;
        }

        /// <summary>1グループぶんのパッキング・合成・マテリアル生成を行い、結果へ登録する。</summary>
        private static void BuildOneGroup(Group group, int atlasMaxSize, Material blitMat, int passTint, int passUnpack, PCOptimizeSettings settings, string outputDir, ConversionAssetContext assets, AtlasBuildResult result, ConversionReport report)
        {
            int atlasW, atlasH;
            if (!FitCells(group, atlasMaxSize, result.excluded, report, out atlasW, out atlasH))
            {
                return; // メンバーが2未満になった(残りは除外済み)
            }

            // ---- 内部整合性ガード(パッキング破綻の検出) ----
            // SkylinePack が確定した各セルの rect は以後不変で、ComposeChannel の描画先も
            // result.cellRects の登録値も RemapMeshesAndMergeSlots が参照するセルも、すべて
            // 同一 Cell.rect を単一の真実として使う(名前キーや並列配列での対応付けは無い)。
            // したがって「セル同士が重ならず、全セルがアトラス領域内に収まる」ことさえ保証すれば
            // 『テクスチャの描画先セル ↔ 登録セル矩形 ↔ サブメッシュの参照セル』は必ず一致する。
            // 万一パッキングが破綻して2セルが重なると、一方のセルへ他メンバーのテクスチャが描かれ、
            // そのセルを参照するメッシュが誤った材質内容(例: 別マテリアルの絵柄)を映してしまう。
            // 破綻を検出したらこのグループは統合せず据え置き、壊れたアトラスを出さない。
            if (!ValidateCellLayout(group, atlasW, atlasH, report))
            {
                // この時点では result.atlasMap/cellRects へ未登録(登録は後段の成功パスのみ、下の 410-421 行)。
                // よって除外リストへ積んで return するだけでよい(統合されなかったメンバーは元マテリアルのまま残る)。
                foreach (Cell brokenCell in group.cells)
                {
                    if (brokenCell.src != null)
                    {
                        result.excluded.Add(brokenCell.src.name + ": アトラス内部整合性エラーのため統合を中止(単独維持)");
                    }
                }
                return;
            }

            // アウトライン統合モードに応じて代表(先頭)を寄せ、見た目変化(付与/除去)を警告する。
            ResolveOutlineRepresentative(group, settings.atlasOutlineUnifyMode, report);

            // カリング差を無視して片面/両面が混在した場合は両面(Cull Off)へ統一し、対象メンバーを警告する。
            ResolveGroupCull(group, report);

            // チャンネルの要否
            group.needNormal = false;
            group.needEmission = false;
            foreach (Cell cell in group.cells)
            {
                if (cell.hasBump) group.needNormal = true;
                if (cell.emits) group.needEmission = true;
            }

            WarnMaterialParameterDifferences(group, report);

            // 名前は「メンバー構成由来の安定キー」。処理順に依存させないことで、同一構成のグループは
            // 実行間で常に同じパス(＝同じGUID)を得る(旧クローンのUV再配置と食い違わない)。
            // さらにパッキング結果(セル配置)由来のレイアウトハッシュを接尾辞に付ける。
            // メンバー構成が同じでも設定変更でUV配置が変わると別名=別GUIDのテクスチャ/マテリアルに
            // なり、旧レイアウトのメッシュを参照する古い複製を共有テクスチャのその場上書きで壊さない
            // (メッシュ命名と同じ保護をテクスチャ/マテリアルにも与える)。
            string baseName = "PCAtlas_" + BuildStableGroupName(group) + "_" + BuildGroupLayoutHash(group, atlasW, atlasH);

            // メイン
            Texture2D mainAtlas = ComposeChannel(group, atlasW, atlasH, AtlasChannel.Main, blitMat, passTint, passUnpack, report);
            Texture2D mainSaved = SaveAtlasTexture(mainAtlas, baseName, "_main", true, false, outputDir, assets);
            if (mainSaved == null)
            {
                report.Warn(string.Format("アトラス '{0}' のメインテクスチャ保存に失敗したため、このグループは統合せず据え置きます。", baseName));
                return;
            }

            // ノーマル
            Texture2D normalSaved = null;
            if (group.needNormal)
            {
                Texture2D normalAtlas = ComposeChannel(group, atlasW, atlasH, AtlasChannel.Normal, blitMat, passTint, passUnpack, report);
                normalSaved = SaveAtlasTexture(normalAtlas, baseName, "_normal", false, true, outputDir, assets);
                if (normalSaved == null)
                {
                    report.Warn(string.Format("アトラス '{0}' のノーマルマップ保存に失敗したため、ノーマルなしで統合します。", baseName));
                }
            }

            // エミッション
            Texture2D emissionSaved = null;
            if (group.needEmission)
            {
                Texture2D emissionAtlas = ComposeChannel(group, atlasW, atlasH, AtlasChannel.Emission, blitMat, passTint, passUnpack, report);
                emissionSaved = SaveAtlasTexture(emissionAtlas, baseName, "_emission", true, false, outputDir, assets);
                if (emissionSaved == null)
                {
                    report.Warn(string.Format("アトラス '{0}' のエミッション保存に失敗したため、エミッションなしで統合します。", baseName));
                }
            }

            Material atlasMaterial = CreateAtlasMaterial(group, mainSaved, normalSaved, emissionSaved, settings, baseName, outputDir, assets);

            // 結果登録(UV矩形は0.5テクセル内側へインセット。QuestConverter の RemapMeshesAndMergeSlots と同じ規約)
            var memberNames = new List<string>();
            int colorOnlyCount = 0;
            long packedArea = 0;
            foreach (Cell cell in group.cells)
            {
                result.atlasMap[cell.src] = atlasMaterial;
                result.cellRects[cell.src] = new Rect(
                    (cell.rect.x + 0.5f) / atlasW,
                    (cell.rect.y + 0.5f) / atlasH,
                    (cell.rect.width - 1f) / atlasW,
                    (cell.rect.height - 1f) / atlasH);
                memberNames.Add(cell.src.name);
                if (cell.colorOnly) colorOnlyCount++;
                packedArea += (long)cell.rect.width * cell.rect.height;
            }
            float usage = (atlasW > 0 && atlasH > 0) ? (float)packedArea / ((long)atlasW * atlasH) * 100f : 0f;
            string info = string.Format("統合: {0}材質 → 1 (サイズ{1}x{2}, 使用率{3:F1}%, メンバー{0}件: {4})",
                group.cells.Count, atlasW, atlasH, usage, string.Join(", ", memberNames));
            if (colorOnlyCount > 0) info += string.Format(" / 色のみ材質 {0}件を単色セルで統合", colorOnlyCount);
            report.Info(info);
        }

        /// <summary>
        /// パッキング後のセル配置が健全か(全セルがアトラス領域内に収まり、セル同士が重ならないか)を検証する。
        /// これは合成・登録・UV再配置がすべて共有する唯一の真実 = Cell.rect を対象にした不変条件チェックであり、
        /// 通れば「描画先セル ↔ 登録セル矩形 ↔ サブメッシュ参照セル」の一致が保証される。
        /// 破綻(領域外・重複)を検出したら 「アトラス内部整合性エラー」 を Error で報告し false を返す
        /// (呼び出し側はこのグループを統合せず据え置く)。O(n^2) だがグループ内セル数は少数のため十分軽い。
        /// </summary>
        private static bool ValidateCellLayout(Group group, int atlasW, int atlasH, ConversionReport report)
        {
            List<Cell> cells = group.cells;
            for (int i = 0; i < cells.Count; i++)
            {
                RectInt a = cells[i].rect;
                string an = cells[i].src != null ? cells[i].src.name : "?";
                if (a.width <= 0 || a.height <= 0 ||
                    a.x < 0 || a.y < 0 || a.x + a.width > atlasW || a.y + a.height > atlasH)
                {
                    report.Error(string.Format(
                        "アトラス内部整合性エラー: セル '{0}'({1},{2} {3}x{4}) がアトラス領域 {5}x{6} をはみ出しています。このグループは統合せず据え置きます。",
                        an, a.x, a.y, a.width, a.height, atlasW, atlasH));
                    return false;
                }
                for (int j = i + 1; j < cells.Count; j++)
                {
                    RectInt b = cells[j].rect;
                    if (a.xMin < b.xMax && b.xMin < a.xMax && a.yMin < b.yMax && b.yMin < a.yMax)
                    {
                        report.Error(string.Format(
                            "アトラス内部整合性エラー: セル '{0}' と '{1}' が重複しています(描画内容が入れ替わる恐れ)。このグループは統合せず据え置きます。",
                            an, cells[j].src != null ? cells[j].src.name : "?"));
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>アトラス名の安定キー(メンバー元マテリアル名をソート連結した FNV-1a 8桁hex)。処理順非依存。</summary>
        private static string BuildStableGroupName(Group group)
        {
            var names = new List<string>(group.cells.Count);
            foreach (Cell cell in group.cells) names.Add(cell.src != null ? cell.src.name : string.Empty);
            names.Sort(StringComparer.Ordinal);

            uint hash = 2166136261u; // FNV-1a 32bit
            foreach (string name in names)
            {
                foreach (char c in name) hash = (hash ^ c) * 16777619u;
                hash = (hash ^ '\n') * 16777619u;
            }
            return hash.ToString("x8");
        }

        /// <summary>
        /// グループのパッキング結果(セル配置)由来のレイアウトハッシュ8桁hexを返す。
        /// メンバーの元マテリアル名・各セルのピクセル矩形・アトラス寸法を(名前順に)畳み込むため、
        /// メンバー構成が同じでもパッキングが変わればハッシュが変わる。元マテリアル名で決定的に
        /// ソートするため、Dictionary列挙順=実行間で変わるインスタンスIDには依存しない。
        /// </summary>
        private static string BuildGroupLayoutHash(Group group, int atlasW, int atlasH)
        {
            var entries = new List<KeyValuePair<string, RectInt>>(group.cells.Count);
            foreach (Cell cell in group.cells)
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
        // シェーダー系統・カリング分類(プレビューと実行で共有)
        // ================================================================

        /// <summary>
        /// アウトライン統一で「同一シェーダー系統」とみなす {plain, outline} のペア表。
        /// アウトライン統一オンのときだけ、plain と outline を同一グループキーへ寄せる。
        /// ファー/透過/カットアウト等の他系統は決して跨がない(このペアのみ)。
        /// 名前ハックではなく、この表を増やすことで系統を一般化する。
        /// </summary>
        private static readonly string[][] OutlineFamilies =
        {
            new[] { "lilToon", "Hidden/lilToonOutline" },
        };

        /// <summary>プレビュー/実行で共有する1マテリアルの分類結果。</summary>
        private struct AtlasClass
        {
            public bool eligible;   // 統合候補になり得るか(単独判定は後段のグループ化で確定)
            public string reason;   // !eligible 時の除外文言(実行の excluded 用)
            public BlockKind kind;  // プレビューの理由分類
            public string key;      // eligible 時のグループキー
            public Shader shader;   // 実シェーダー(理由・系統判定用)
            public int? cull;       // カリング値(混在検出用。プロパティ無しは null)
            public bool isOutline;  // アウトライン系メンバーか(代表選定用)
            public bool colorOnly;  // テクスチャ無し(色のみ)材質か
            public bool eyeName;    // 名前が瞳・顔系トークンに一致するか(モード非依存)
            public bool eyeGuarded; // 現モードで「瞳・顔系のためアウトライン付与を回避」により統合系から外れたか
        }

        /// <summary>統合できない理由の種別(プレビューの文言・ヒント生成に使う)。</summary>
        private enum BlockKind
        {
            None = 0,
            ShaderInvalid,
            Transparent,
            Animation,
            MAReferenced,
            Excluded,
            ColorOnlyDisabled,
            NoMainTexInput,
            UvTiling,
            TextureTiling,
            EmissionSingle,
            PoiyomiLocked,
        }

        /// <summary>
        /// 1マテリアルがアトラス統合の候補になり得るか判定し、なり得るならグループキーを付与する。
        /// BuildAtlases とプレビュー(PreviewPlan)の両方から呼ぶ唯一の判定器(preview==execution)。
        /// </summary>
        private static AtlasClass Classify(Material src, List<MeshSlotUse> uses, PCOptimizeSettings settings,
            HashSet<Material> animationExcluded, HashSet<Material> maReferenced, HashSet<string> excludeGuids, Dictionary<Mesh, Vector2[]> uvCache)
        {
            var c = new AtlasClass { eligible = false, kind = BlockKind.None };
            if (src == null || src.shader == null)
            {
                c.reason = "シェーダーが無効なため"; c.kind = BlockKind.ShaderInvalid; return c;
            }
            c.shader = src.shader;

            // 透過/カットアウトは統合対象外(描画順・アルファの都合。QuestConverter の分類を再利用)
            if (QuestCompat.ClassifyTransparency(src) != QuestCompat.TransparencyClass.Opaque)
            {
                c.reason = "透過/カットアウト材質のため"; c.kind = BlockKind.Transparent; return c;
            }
            if (animationExcluded.Contains(src))
            {
                c.reason = "アニメーションでマテリアルが差し替わるため"; c.kind = BlockKind.Animation; return c;
            }
            // Modular Avatar の Material Setter / Material Swap が参照するマテリアルは、ビルド時に
            // スロットへ差し込まれる/同一性で差し替えられるため、アトラスへ詰め込むと差し替えが壊れる。
            if (maReferenced != null && maReferenced.Contains(src))
            {
                c.reason = "MAマテリアル設定が参照するため"; c.kind = BlockKind.MAReferenced; return c;
            }
            if (excludeGuids.Count > 0 && excludeGuids.Contains(GetAssetGuid(src)))
            {
                c.reason = "アトラス除外指定のため"; c.kind = BlockKind.Excluded; return c;
            }
            // ロック済みPoiyomiはシェーダーがマテリアル個別(名前が per-material:"Hidden/Locked/…/{guid}")で、
            // アトラスマテリアルは代表メンバーのコピー=そのロック済みシェーダーになる。代表のロック済み
            // シェーダーは自分のテクスチャ用に定数が焼き込まれているため、他メンバーのテクスチャを差し替えると
            // 見た目が壊れる。よってロック済みPoiyomiは統合対象外(アンロックすれば統合可能)。
            if (QuestCompat.IsPoiyomiLocked(src))
            {
                c.reason = "ロック済みPoiyomiのため統合不可(アンロックすると統合可能)"; c.kind = BlockKind.PoiyomiLocked; return c;
            }

            string[] mainTexProps = MainTexPropsFor(src);
            Texture mainTex = ResolveTexture(src, mainTexProps);
            bool colorOnly = mainTex == null;
            if (colorOnly)
            {
                if (!settings.atlasColorOnlyMaterials)
                {
                    c.reason = "テクスチャ無し材質のため(色セル統合が無効)"; c.kind = BlockKind.ColorOnlyDisabled; return c;
                }
                // メインテクスチャ入力の無いシェーダーは単色セルを表示できない(_Color を白へ落とすと色が失われる)。
                if (!HasAnyProperty(src, mainTexProps))
                {
                    c.reason = "メインテクスチャ入力の無いシェーダーのため(色のみ統合不可)"; c.kind = BlockKind.NoMainTexInput; return c;
                }
            }
            else
            {
                string uvReason;
                bool uvOk;
                try { uvOk = CheckUvRange(uses, uvCache, out uvReason); }
                catch (Exception ex) { uvOk = false; uvReason = "メッシュを読み取れないため(" + ex.Message + ")"; }
                if (!uvOk)
                {
                    c.reason = uvReason; c.kind = BlockKind.UvTiling; return c;
                }
                // NonToon のメイン(_BaseTexture)は _ST 非適用のため常に単位(GetTextureScale が既定 (1,1) を返す)。
                if (!IsTextureStIdentity(src, ResolveProperty(src, mainTexProps)))
                {
                    c.reason = "メインテクスチャにタイリング/オフセット設定があるため"; c.kind = BlockKind.TextureTiling; return c;
                }
            }

            if (!settings.atlasBakeEmissionMask)
            {
                // エミッションベイク無効時: 発光する材質は発光を維持するため統合せず単独据え置き
                Color t; bool cl; Texture m;
                if (HasActiveEmission(src, out t, out cl, out m))
                {
                    c.reason = "エミッション材質のため(エミッションベイク無効・単独維持)"; c.kind = BlockKind.EmissionSingle; return c;
                }
            }

            c.eligible = true;
            c.colorOnly = colorOnly;
            c.cull = ReadCullValue(src);
            bool isOutline;
            TryGetOutlineFamily(src.shader.name, out _, out isOutline);
            c.isOutline = isOutline;
            c.eyeName = IsEyeOrFaceSensitiveName(src.name);
            c.eyeGuarded = ShouldEyeGuard(src, settings.atlasOutlineUnifyMode);
            c.key = GroupKeyFor(src, settings);
            return c;
        }

        /// <summary>アウトライン統合の対象とみなす瞳・顔系の名前トークン。英字トークンは語頭境界一致、CJKトークンは部分一致で判定する。</summary>
        private static readonly string[] EyeFaceTokens =
        {
            "eye", "瞳", "hitomi", "pupil", "iris", "eyelash", "まつげ", "睫", "eyebrow", "眉", "face", "顔",
        };

        /// <summary>
        /// マテリアル名が瞳・顔系トークンに一致するか。
        /// 英字トークン("eye"/"face" 等)は surface/interface/fisheye のような英単語内部への誤一致を避けるため、
        /// 語頭境界(文字列先頭・非英字の直後・camelCase の大文字境界)で始まる出現のみを一致とみなす
        /// (Eyes/Faces の複数形や EyeBase・eye_L・cf_m_face 等は境界扱いで従来どおり一致する)。
        /// CJKトークン(瞳/顔/眉/睫/まつげ)は表語文字で誤一致の懸念が無いため部分一致のまま判定する。
        /// </summary>
        private static bool IsEyeOrFaceSensitiveName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string token in EyeFaceTokens)
            {
                bool hit = IsAsciiToken(token)
                    ? ContainsTokenAtWordStart(name, token)
                    : name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) return true;
            }
            return false;
        }

        /// <summary>トークンが全て ASCII(英字)か。CJKトークンと区別するために用いる。</summary>
        private static bool IsAsciiToken(string token)
        {
            foreach (char c in token)
            {
                if (c > 0x7F) return false;
            }
            return true;
        }

        /// <summary>英字トークンが語頭境界で始まる出現を name 内に持つか(大小無視)。</summary>
        private static bool ContainsTokenAtWordStart(string name, string token)
        {
            int start = 0;
            while (true)
            {
                int idx = name.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                bool leftBoundary = idx == 0
                    || !char.IsLetter(name[idx - 1])
                    || (char.IsLower(name[idx - 1]) && char.IsUpper(name[idx])); // camelCase の大文字境界
                if (leftBoundary) return true;
                start = idx + 1;
            }
        }

        /// <summary>
        /// モード2(アウトライン付きに統一)で、瞳・顔系のプレーンlilToonをアウトライン付与から回避するか。
        /// 対象はアウトライン系統のプレーンメンバー(＝アウトライン版ではない)かつ名前が瞳・顔系のもの。
        /// これらは黒縁化を避けるため統合系キーから外し、プレーン専用グループへ落とす(モード1は誰も付与されないため不要)。
        /// </summary>
        private static bool ShouldEyeGuard(Material src, OutlineUnifyMode mode)
        {
            if (mode != OutlineUnifyMode.アウトライン付きに統一 || src == null || src.shader == null) return false;
            bool isOutline;
            if (!TryGetOutlineFamily(src.shader.name, out _, out isOutline) || isOutline) return false; // 系統外・アウトライン版は対象外
            return IsEyeOrFaceSensitiveName(src.name);
        }

        /// <summary>グループキー(シェーダー系統[+カリング])。実行とプレビューで共有する。</summary>
        private static string GroupKeyFor(Material src, PCOptimizeSettings settings)
        {
            bool eyeGuard = ShouldEyeGuard(src, settings.atlasOutlineUnifyMode);
            string shaderKey = ShaderFamilyKey(src.shader, settings.atlasOutlineUnifyMode, eyeGuard);
            return settings.atlasIgnoreCull ? shaderKey : shaderKey + "|" + ReadCull(src);
        }

        /// <summary>
        /// アウトライン統合モードが「しない」以外で、{plain, outline} を同一系統キーへ寄せる。
        /// eyeGuardDivert(瞳・顔系のプレーンを回避)のときは系統キーへ寄せず、シェーダー名そのものを返す。
        /// </summary>
        private static string ShaderFamilyKey(Shader shader, OutlineUnifyMode mode, bool eyeGuardDivert)
        {
            string name = shader != null ? shader.name : "(null)";
            string familyId;
            if (mode != OutlineUnifyMode.しない && !eyeGuardDivert && TryGetOutlineFamily(name, out familyId, out _))
                return "outlinefam:" + familyId;
            return name;
        }

        /// <summary>シェーダー名がアウトライン系統ペアに属するか。属せば familyId=plain名, isOutline=アウトライン版か。</summary>
        private static bool TryGetOutlineFamily(string shaderName, out string familyId, out bool isOutline)
        {
            if (!string.IsNullOrEmpty(shaderName))
            {
                foreach (string[] family in OutlineFamilies)
                {
                    if (string.Equals(shaderName, family[0], StringComparison.Ordinal)) { familyId = family[0]; isOutline = false; return true; }
                    if (string.Equals(shaderName, family[1], StringComparison.Ordinal)) { familyId = family[0]; isOutline = true; return true; }
                }
            }
            familyId = null; isOutline = false; return false;
        }

        /// <summary>カリング値を返す(_Cull → _Culling の順)。プロパティが無ければ null。</summary>
        private static int? ReadCullValue(Material mat)
        {
            if (mat == null) return null;
            if (mat.HasProperty("_Cull")) return Mathf.RoundToInt(mat.GetFloat("_Cull"));
            if (mat.HasProperty("_Culling")) return Mathf.RoundToInt(mat.GetFloat("_Culling"));
            return null;
        }

        /// <summary>
        /// アウトライン統合モードに応じて代表(先頭)メンバーを寄せ、見た目変化を警告する。
        /// ・アウトライン付きに統一: 代表をアウトライン版へ寄せ、plain メンバーにアウトラインが付く旨を警告する。
        /// ・アウトラインを外して統合: 代表を plain メンバーへ寄せる(無ければアトラスコピーをプレーンlilToonへ差し替える)。
        ///   アウトライン版メンバーは輪郭線が外れる旨を警告する。
        /// </summary>
        private static void ResolveOutlineRepresentative(Group group, OutlineUnifyMode mode, ConversionReport report)
        {
            if (mode == OutlineUnifyMode.しない || group.cells.Count == 0) return;

            var outlineNames = new List<string>();
            var plainNames = new List<string>();
            foreach (Cell cell in group.cells)
            {
                if (cell == null || cell.src == null || cell.src.shader == null) continue;
                bool isOutline;
                if (!TryGetOutlineFamily(cell.src.shader.name, out _, out isOutline)) continue; // 系統外は無視
                if (isOutline) outlineNames.Add(cell.src.name); else plainNames.Add(cell.src.name);
            }

            if (mode == OutlineUnifyMode.アウトライン付きに統一)
            {
                PromoteToFront(group, IsOutlineCell);
                if (IsOutlineCell(group.cells[0]) && plainNames.Count > 0)
                {
                    report.Warn(string.Format("アウトラインが付与されます: {0}(アウトライン統一により、アウトラインの無かったマテリアルにアウトラインが付きます)", string.Join(", ", plainNames)));
                }
            }
            else // アウトラインを外して統合
            {
                bool promotedPlain = PromoteToFront(group, IsPlainFamilyCell);
                // plain メンバーが1つも無い(全てアウトライン版)場合は、代表のコピーをプレーンlilToonへ差し替える。
                group.repNeedsPlainShaderSwap = !promotedPlain && IsOutlineCell(group.cells[0]);
                if (outlineNames.Count > 0)
                {
                    report.Warn(string.Format("アトラス統合によりアウトラインが外れます: {0}", string.Join(", ", outlineNames)));
                }
            }
        }

        /// <summary>述語に一致する最初のメンバーを先頭へ寄せる(既に先頭が一致すれば true)。一致が無ければ false。</summary>
        private static bool PromoteToFront(Group group, Func<Cell, bool> predicate)
        {
            if (group.cells.Count == 0) return false;
            if (predicate(group.cells[0])) return true;
            for (int i = 1; i < group.cells.Count; i++)
            {
                if (predicate(group.cells[i]))
                {
                    Cell cell = group.cells[i];
                    group.cells.RemoveAt(i);
                    group.cells.Insert(0, cell);
                    return true;
                }
            }
            return false;
        }

        /// <summary>セルの元マテリアルがアウトライン系シェーダーか。</summary>
        private static bool IsOutlineCell(Cell cell)
        {
            if (cell == null || cell.src == null || cell.src.shader == null) return false;
            bool isOutline;
            return TryGetOutlineFamily(cell.src.shader.name, out _, out isOutline) && isOutline;
        }

        /// <summary>セルの元マテリアルがアウトライン系統の plain(アウトライン版でない)メンバーか。</summary>
        private static bool IsPlainFamilyCell(Cell cell)
        {
            if (cell == null || cell.src == null || cell.src.shader == null) return false;
            bool isOutline;
            return TryGetOutlineFamily(cell.src.shader.name, out _, out isOutline) && !isOutline;
        }

        /// <summary>アウトライン版マテリアルに対応するプレーン(輪郭無し)シェーダーを返す(見つからなければ null)。</summary>
        private static Shader GetPlainShaderForOutline(Material mat)
        {
            if (mat == null || mat.shader == null) return null;
            string familyId;
            bool isOutline;
            if (TryGetOutlineFamily(mat.shader.name, out familyId, out isOutline) && isOutline)
            {
                return Shader.Find(familyId);
            }
            return null;
        }

        /// <summary>グループ内のカリング値が混在していれば両面(Cull Off)へ統一するフラグを立て、対象を警告する。</summary>
        private static void ResolveGroupCull(Group group, ConversionReport report)
        {
            var values = new HashSet<int>();
            foreach (Cell cell in group.cells)
            {
                int? cull = ReadCullValue(cell.src);
                if (cull.HasValue) values.Add(cull.Value);
            }
            group.madeTwoSided = values.Count >= 2;
            if (group.madeTwoSided)
            {
                var names = new List<string>();
                foreach (Cell cell in group.cells) if (cell.src != null) names.Add(cell.src.name);
                report.Warn(string.Format("両面描画(Cull Off)に統一します: {0}(カリング差を無視して統合したため。フィルレートが少し増えます)", string.Join(", ", names)));
            }
        }

        // ================================================================
        // 統合プレビュー(ベイクせず、実行と同じキーでグループ化して可視化する)
        // ================================================================

        /// <summary>統合プレビュー結果(統合予定グループ・統合不可の理由・統合後の概算数)。</summary>
        public sealed class AtlasPlan
        {
            /// <summary>統合予定グループ(メンバー2件以上)。</summary>
            public readonly List<AtlasPlanGroup> groups = new List<AtlasPlanGroup>();
            /// <summary>統合できない(単独含む)マテリアルと理由。</summary>
            public readonly List<AtlasPlanBlocked> blocked = new List<AtlasPlanBlocked>();
            /// <summary>統合対象として考慮した一意マテリアル数(概算の分母)。</summary>
            public int candidateMaterialCount;
            /// <summary>統合後の一意マテリアル数(概算)。</summary>
            public int projectedMaterialCount;
        }

        /// <summary>統合予定グループ1件の表示情報。</summary>
        public sealed class AtlasPlanGroup
        {
            public string shaderLabel;
            public readonly List<string> memberNames = new List<string>();
            public bool twoSided;                                            // カリング混在 → Cull Off(両面)
            public readonly List<string> outlineAdded = new List<string>();   // アウトラインが付与される plain メンバー(付きに統一)
            public readonly List<string> outlineRemoved = new List<string>(); // アウトラインが外れるアウトライン版メンバー(外して統合)
        }

        /// <summary>統合できないマテリアル1件の表示情報。</summary>
        public sealed class AtlasPlanBlocked
        {
            public string name;
            public string reason;
            public string hint;   // オプションで解除できる場合の一言(なければ null)
        }

        /// <summary>
        /// アバターのマテリアルを、実行(BuildAndApply)と同じキーロジックでグループ化した
        /// ベイク無しのプレビューを返す。設定・アバターが変わるたびに呼び直す想定。
        /// トグル固定・AAO結合の前段のため概算(最終的なスロット数はレンダラー結合にも依存する)。
        /// </summary>
        public static AtlasPlan PreviewPlan(GameObject avatarRoot, PCOptimizeSettings settings)
        {
            var plan = new AtlasPlan();
            if (avatarRoot == null || settings == null || !settings.enableAtlas) return plan;

            HashSet<Material> animationExcluded = CollectAnimationExcludedMaterials(avatarRoot);
            HashSet<Material> maReferenced = MACompatUtility.CollectReferencedMaterials(avatarRoot);
            HashSet<string> excludeGuids = BuildGuidSet(settings.atlasExcludeMaterialGuids);
            Dictionary<Material, List<MeshSlotUse>> meshUsage = CollectMeshUsage(avatarRoot);
            var uvCache = new Dictionary<Mesh, Vector2[]>();

            plan.candidateMaterialCount = meshUsage.Count;

            var eligible = new List<AtlasClass>();
            var eligibleSrc = new List<Material>();
            foreach (KeyValuePair<Material, List<MeshSlotUse>> pair in meshUsage)
            {
                AtlasClass cls = Classify(pair.Key, pair.Value, settings, animationExcluded, maReferenced, excludeGuids, uvCache);
                if (cls.eligible) { eligible.Add(cls); eligibleSrc.Add(pair.Key); }
                else plan.blocked.Add(MakeBlocked(pair.Key, cls));
            }

            // 実行と同じキーでグループ化する。
            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < eligible.Count; i++)
            {
                List<int> list;
                if (!groups.TryGetValue(eligible[i].key, out list)) { list = new List<int>(); groups.Add(eligible[i].key, list); }
                list.Add(i);
            }

            int saved = 0;
            foreach (KeyValuePair<string, List<int>> kv in groups)
            {
                List<int> members = kv.Value;
                if (members.Count >= 2)
                {
                    plan.groups.Add(BuildPlanGroup(members, eligible, eligibleSrc, settings));
                    saved += members.Count - 1;
                }
                else
                {
                    // 単独になった eligible マテリアル: オプションで統合可能になるかを診断する。
                    plan.blocked.Add(ExplainLone(eligible[members[0]], eligibleSrc[members[0]], eligible, settings));
                }
            }

            plan.projectedMaterialCount = Mathf.Max(0, plan.candidateMaterialCount - saved);
            return plan;
        }

        /// <summary>統合予定グループ(2件以上)の表示情報を作る。</summary>
        private static AtlasPlanGroup BuildPlanGroup(List<int> members, List<AtlasClass> eligible, List<Material> eligibleSrc, PCOptimizeSettings settings)
        {
            var g = new AtlasPlanGroup();
            var cullValues = new HashSet<int>();
            bool anyOutline = false;
            Shader outlineShader = null;
            Shader plainShader = null;
            foreach (int mi in members)
            {
                g.memberNames.Add(eligibleSrc[mi] != null ? eligibleSrc[mi].name : "(不明)");
                if (eligible[mi].cull.HasValue) cullValues.Add(eligible[mi].cull.Value);
                if (eligible[mi].isOutline) { anyOutline = true; if (outlineShader == null) outlineShader = eligible[mi].shader; }
                else if (plainShader == null && IsFamilyShader(eligible[mi].shader)) plainShader = eligible[mi].shader;
            }

            OutlineUnifyMode mode = settings.atlasOutlineUnifyMode;
            // 代表(＝アトラスマテリアルの見た目)は: 付きに統一ならアウトライン版、外して統合ならプレーン。
            Shader repShader = mode == OutlineUnifyMode.アウトライン付きに統一
                ? (outlineShader ?? plainShader)
                : (plainShader ?? GetPlainShaderForFamily(outlineShader) ?? outlineShader);
            if (repShader == null) repShader = eligible[members[0]].shader;
            g.shaderLabel = repShader != null ? repShader.name : "(不明)";
            g.twoSided = cullValues.Count >= 2;

            if (anyOutline)
            {
                if (mode == OutlineUnifyMode.アウトライン付きに統一)
                {
                    foreach (int mi in members)
                        if (!eligible[mi].isOutline) g.outlineAdded.Add(eligibleSrc[mi] != null ? eligibleSrc[mi].name : "(不明)");
                }
                else if (mode == OutlineUnifyMode.アウトラインを外して統合)
                {
                    foreach (int mi in members)
                        if (eligible[mi].isOutline) g.outlineRemoved.Add(eligibleSrc[mi] != null ? eligibleSrc[mi].name : "(不明)");
                }
            }
            return g;
        }

        /// <summary>シェーダーがアウトライン系統ペアに属するか。</summary>
        private static bool IsFamilyShader(Shader shader)
        {
            return shader != null && TryGetOutlineFamily(shader.name, out _, out _);
        }

        /// <summary>アウトライン版シェーダーに対応するプレーン(輪郭無し)シェーダーを返す(見つからなければ null)。</summary>
        private static Shader GetPlainShaderForFamily(Shader outlineShader)
        {
            if (outlineShader == null) return null;
            string familyId;
            bool isOutline;
            if (TryGetOutlineFamily(outlineShader.name, out familyId, out isOutline) && isOutline)
            {
                return Shader.Find(familyId);
            }
            return null;
        }

        /// <summary>単独になった eligible マテリアルの理由と(あれば)解除ヒントを作る。</summary>
        private static AtlasPlanBlocked ExplainLone(AtlasClass cls, Material src, List<AtlasClass> eligible, PCOptimizeSettings settings)
        {
            var b = new AtlasPlanBlocked { name = src != null ? src.name : "(不明)" };

            // モード2で瞳・顔系のためアウトライン付与を回避して単独になった場合は、その旨を理由にする(意図的)。
            if (cls.eyeGuarded)
            {
                b.reason = "瞳・顔系のためアウトライン付与を回避(プレーンのまま単独維持)";
                return b;
            }

            bool inFamily = TryGetOutlineFamily(cls.shader != null ? cls.shader.name : null, out _, out _);
            OutlineUnifyMode mode = settings.atlasOutlineUnifyMode;
            // 統合可否の判定には、瞳ガードの無い最も許容的な「アウトラインを外して統合」を代表に使う。
            const OutlineUnifyMode unify = OutlineUnifyMode.アウトラインを外して統合;
            bool alreadyUnified = mode == OutlineUnifyMode.アウトラインを外して統合;

            bool cullHelps = !settings.atlasIgnoreCull && CountUnder(eligible, cls, true, mode) >= 2;
            bool outlineHelps = !alreadyUnified && inFamily && CountUnder(eligible, cls, settings.atlasIgnoreCull, unify) >= 2;
            bool bothHelps = !settings.atlasIgnoreCull && !alreadyUnified && inFamily && CountUnder(eligible, cls, true, unify) >= 2;

            if (cullHelps && outlineHelps)
            {
                b.reason = "同系統だがカリングとアウトラインの違いで分かれています";
                b.hint = "「カリング差を無視」またはアウトライン統合(外す/付き)を設定すると統合できます";
            }
            else if (cullHelps)
            {
                b.reason = "カリング(片面/両面)が他メンバーと異なるため";
                b.hint = "「カリング差を無視」をオンにすると統合できます";
            }
            else if (outlineHelps)
            {
                b.reason = "アウトライン有無が他メンバーと異なるため";
                b.hint = "「アウトラインを外して統合」または「アウトライン付きに統一」にすると統合できます";
            }
            else if (bothHelps)
            {
                b.reason = "カリングとアウトラインの両方が他メンバーと異なるため";
                b.hint = "「カリング差を無視」とアウトライン統合(外す/付き)の両方を設定すると統合できます";
            }
            else
            {
                b.reason = "単独グループ(同じシェーダー系統『" + (cls.shader != null ? cls.shader.name : "?") + "』の統合相手がいません)";
            }
            return b;
        }

        /// <summary>指定のカリング/アウトライン設定で target と同じキーになる eligible メンバー数を数える(自身を含む)。</summary>
        private static int CountUnder(List<AtlasClass> eligible, AtlasClass target, bool ignoreCull, OutlineUnifyMode mode)
        {
            string tk = ComposeKey(target, ignoreCull, mode);
            int n = 0;
            foreach (AtlasClass c in eligible) if (ComposeKey(c, ignoreCull, mode) == tk) n++;
            return n;
        }

        /// <summary>分類済みマテリアルのグループキーを任意のカリング/アウトライン設定で再計算する(GroupKeyFor と同一規約)。</summary>
        private static string ComposeKey(AtlasClass c, bool ignoreCull, OutlineUnifyMode mode)
        {
            bool eyeGuard = mode == OutlineUnifyMode.アウトライン付きに統一 && c.eyeName && IsPlainFamily(c.shader);
            string shaderKey = ShaderFamilyKey(c.shader, mode, eyeGuard);
            return ignoreCull ? shaderKey : shaderKey + "|" + (c.cull.HasValue ? c.cull.Value.ToString() : "n");
        }

        /// <summary>シェーダーがアウトライン系統の plain(アウトライン版でない)メンバーか。</summary>
        private static bool IsPlainFamily(Shader shader)
        {
            if (shader == null) return false;
            bool isOutline;
            return TryGetOutlineFamily(shader.name, out _, out isOutline) && !isOutline;
        }

        /// <summary>統合対象外(intrinsic)マテリアルの理由と(あれば)解除ヒントを作る。</summary>
        private static AtlasPlanBlocked MakeBlocked(Material src, AtlasClass cls)
        {
            var b = new AtlasPlanBlocked { name = src != null ? src.name : "(不明)" };
            string shaderName = src != null && src.shader != null ? src.shader.name : "?";
            switch (cls.kind)
            {
                case BlockKind.ShaderInvalid:
                    b.reason = "シェーダーが無効です"; break;
                case BlockKind.Transparent:
                    b.reason = "透過/カットアウト材質(" + shaderName + ")のため統合できません"; break;
                case BlockKind.Animation:
                    b.reason = "アニメでマテリアルが差し替えられるため"; break;
                case BlockKind.MAReferenced:
                    b.reason = "MAマテリアル設定(Material Setter/Swap)が参照するため"; break;
                case BlockKind.Excluded:
                    b.reason = "アトラス除外に指定されています"; break;
                case BlockKind.ColorOnlyDisabled:
                    b.reason = "テクスチャ無し(色だけ)材質のため";
                    b.hint = "「テクスチャ無し(色だけ)の材質も統合」をオンにすると統合できます"; break;
                case BlockKind.NoMainTexInput:
                    b.reason = "メインテクスチャ入力の無いシェーダー(" + shaderName + ")のため"; break;
                case BlockKind.UvTiling:
                    b.reason = "UVが0..1範囲外(タイリング使用)のため"; break;
                case BlockKind.TextureTiling:
                    b.reason = "メインテクスチャにタイリング/オフセット設定があるため"; break;
                case BlockKind.EmissionSingle:
                    b.reason = "エミッション材質のため(単独維持)";
                    b.hint = "「エミッションをアトラスへ焼き込む」をオンにすると統合できます"; break;
                case BlockKind.PoiyomiLocked:
                    b.reason = "ロック済みPoiyomi(" + shaderName + ")のため";
                    b.hint = "マテリアルをアンロック(Unlock)すると統合できます"; break;
                default:
                    b.reason = cls.reason ?? "統合対象外"; break;
            }
            return b;
        }

        // ================================================================
        // マテリアル差し替え(QuestConverter.ApplyMaterialMap 相当の最小実装)
        // ================================================================

        /// <summary>
        /// clone 配下の各レンダラーの sharedMaterials を「元 → アトラス」へ差し替える。
        /// EditorOnly サブツリーは対象外。map に無いマテリアル(パーティクル・除外分)はそのまま。
        /// </summary>
        private static int ApplyMaterialMap(GameObject clone, Dictionary<Material, Material> map, ConversionReport report)
        {
            int count = 0;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, clone.transform)) continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material atlasMat;
                    if (mats[i] != null && map.TryGetValue(mats[i], out atlasMat) && atlasMat != null)
                    {
                        mats[i] = atlasMat;
                        changed = true;
                        count++;
                    }
                }
                if (changed)
                {
                    Undo.RecordObject(renderer, UndoLabel);
                    renderer.sharedMaterials = mats;
                }
            }
            return count;
        }

        // ================================================================
        // 候補収集・チェック
        // ================================================================

        /// <summary>非パーティクルのメッシュ系レンダラーによるマテリアル使用箇所を収集する。</summary>
        private static Dictionary<Material, List<MeshSlotUse>> CollectMeshUsage(GameObject root)
        {
            var usage = new Dictionary<Material, List<MeshSlotUse>>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                Mesh mesh = GetRendererMesh(renderer); // SMR / MR(+MF)以外はnull ＝ パーティクル系は対象外
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
                    list.Add(new MeshSlotUse { mesh = mesh, submesh = Mathf.Min(i, mesh.subMeshCount - 1) });
                }
            }
            return usage;
        }

        /// <summary>
        /// アニメーションでマテリアルが差し替わる材質を集める。
        /// ・m_Materials の ObjectReference カーブが持つ全差し替え先マテリアル。
        /// ・その差し替え対象レンダラー(パスが一致)の現在の全マテリアル。
        /// これらを統合対象から外し、実行時の差し替えとUV再配置の食い違いを防ぐ。
        /// </summary>
        private static HashSet<Material> CollectAnimationExcludedMaterials(GameObject root)
        {
            var excluded = new HashSet<Material>();
            var animatedPaths = new HashSet<string>();

            // (a) 到達可能な全コントローラー = アバタールート相対とみなす(FXレイヤー等の主経路)。
            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(root))
            {
                if (controller == null) continue;
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    AddClipMaterialSlotExclusions(clip, string.Empty, animatedPaths, excluded);
                }
            }

            // (b) ルート以外のコンポーネント(子オブジェクトのAnimator / MA Merge Animator等)が参照する
            //     コントローラーは、バインディングパスがそのコンポーネント基準の可能性があるため、
            //     コンポーネント位置を前置したパスでも解釈する(除外が広がる=安全側)。
            //     ToggleConsolidator / ComponentRemover と同じ二段構え。
            var seenPrefixed = new HashSet<string>();
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (component.transform == root.transform) continue;
                string prefix = QuestCompat.GetRelativePath(root.transform, component.transform);
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
                        AddClipMaterialSlotExclusions(clip, prefix, animatedPaths, excluded);
                    }
                }
            }

            if (animatedPaths.Count > 0)
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
                    if (!animatedPaths.Contains(path)) continue;
                    foreach (Material m in renderer.sharedMaterials) if (m != null) excluded.Add(m);
                }
            }
            return excluded;
        }

        /// <summary>
        /// クリップ内の m_Materials(マテリアルスロット差し替え)ObjectReferenceバインディングについて、
        /// prefix を前置した対象パスを animatedPaths へ、差し替え先マテリアルを excluded へ追加する。
        /// </summary>
        private static void AddClipMaterialSlotExclusions(AnimationClip clip, string prefix, HashSet<string> animatedPaths, HashSet<Material> excluded)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (binding.propertyName == null || !binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)) continue;
                string raw = binding.path ?? string.Empty;
                string full = prefix.Length == 0 ? raw : (raw.Length == 0 ? prefix : prefix + "/" + raw);
                animatedPaths.Add(full);
                ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null) continue;
                foreach (ObjectReferenceKeyframe key in keys)
                {
                    var m = key.value as Material;
                    if (m != null) excluded.Add(m);
                }
            }
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

        // ================================================================
        // パッキング(QuestConverter のスカイライン法を移植)
        // ================================================================

        /// <summary>
        /// グループのセルを最小の2の累乗正方形へタイトに詰め、実使用領域を囲む最小の非正方形2の累乗(W×H)を返す。
        /// 収まらなければ最大セルを半分へ縮小、それでも駄目なら最小セルを脱落させる。メンバーが2未満で false。
        /// </summary>
        private static bool FitCells(Group group, int atlasMaxSize, List<string> excluded, ConversionReport report, out int atlasW, out int atlasH)
        {
            atlasW = 0;
            atlasH = 0;
            while (true)
            {
                int atlasSize;
                if (TryPackSquare(group.cells, atlasMaxSize, out atlasSize))
                {
                    ComputeTrimmedAtlasSize(group.cells, atlasMaxSize, out atlasW, out atlasH);
                    return true;
                }

                Cell largest = null;
                foreach (Cell c in group.cells)
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

                Cell smallest = null;
                foreach (Cell c in group.cells)
                {
                    if (smallest == null || c.width * c.height < smallest.width * smallest.height) smallest = c;
                }
                if (smallest == null) return false;
                group.cells.Remove(smallest);
                report.Warn(string.Format("'{0}': アトラスに収まらないため統合から除外しました(単独マテリアルのまま)。", smallest.src.name));
                excluded.Add(smallest.src.name + ": アトラスに収まらないため(単独)");

                if (group.cells.Count < 2)
                {
                    if (group.cells.Count == 1) excluded.Add(group.cells[0].src.name + ": 統合できる相手がなくなったため(単独)");
                    return false;
                }
            }
        }

        /// <summary>正方形詰め後の実配置から、実使用領域を囲む最小の非正方形2の累乗(W×H)を求める。</summary>
        private static void ComputeTrimmedAtlasSize(List<Cell> cells, int atlasMaxSize, out int atlasW, out int atlasH)
        {
            int usedW = 0;
            int usedH = 0;
            foreach (Cell cell in cells)
            {
                usedW = Mathf.Max(usedW, cell.rect.x + cell.rect.width + GutterPixels);
                usedH = Mathf.Max(usedH, cell.rect.y + cell.rect.height + GutterPixels);
            }
            atlasW = Mathf.Clamp(Mathf.NextPowerOfTwo(usedW), 64, atlasMaxSize);
            atlasH = Mathf.Clamp(Mathf.NextPowerOfTwo(usedH), 64, atlasMaxSize);
        }

        /// <summary>可変サイズのセルを最小の2の累乗正方形へタイトに詰める(スカイライン ボトムレフト法)。</summary>
        private static bool TryPackSquare(List<Cell> cells, int atlasMaxSize, out int atlasSize)
        {
            atlasSize = 0;
            if (cells.Count == 0) return false;

            // 同寸のセルは元マテリアル名で決定的に順序付ける(List<T>.Sort は不安定で、入力順=
            // Dictionary 列挙順=実行間で変わるインスタンスID順に左右されると、レイアウトハッシュが
            // セッションをまたいで変わってしまうため)。
            var order = new List<Cell>(cells);
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
            foreach (Cell cell in order)
            {
                int fw = cell.width + GutterPixels * 2;
                int fh = cell.height + GutterPixels * 2;
                if (fw > atlasMaxSize || fh > atlasMaxSize) return false;
                totalArea += (long)fw * fh;
                maxFootprint = Mathf.Max(maxFootprint, Mathf.Max(fw, fh));
            }

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

        private static bool SkylinePack(List<Cell> order, int size)
        {
            var skyline = new List<SkylineNode> { new SkylineNode { x = 0, y = 0, width = size } };
            foreach (Cell cell in order)
            {
                int fw = cell.width + GutterPixels * 2;
                int fh = cell.height + GutterPixels * 2;
                int px, py, index;
                if (!SkylineFindPosition(skyline, size, fw, fh, out px, out py, out index)) return false;
                SkylineAddLevel(skyline, index, px, py + fh, fw);
                cell.rect = new RectInt(px + GutterPixels, py + GutterPixels, cell.width, cell.height);
            }
            return true;
        }

        private struct SkylineNode
        {
            public int x;
            public int y;
            public int width;
        }

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
                if (y < 0) continue;
                if (y + h > size) continue;
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
        /// メイン: セル = テクスチャ(色のみ材質は白テクスチャ) × _Color。エミッション: マップ×色 or 単色。
        /// ノーマル: UnpackNormalパスで展開。非発光メンバーのエミッションセルは黒のまま残す。
        /// </summary>
        private static Texture2D ComposeChannel(Group group, int width, int height, AtlasChannel channel, Material blitMat, int passTint, int passUnpack, ConversionReport report)
        {
            bool linear = channel == AtlasChannel.Normal;
            Color32 background = linear ? new Color32(128, 128, 255, 255) : new Color32(0, 0, 0, 255);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;

            foreach (Cell cell in group.cells)
            {
                Texture memberTex = null;
                Color tint = Color.white;
                bool useShader;
                int pass;

                switch (channel)
                {
                    case AtlasChannel.Main:
                        memberTex = cell.colorOnly ? Texture2D.whiteTexture : ResolveTexture(cell.src, MainTexPropsFor(cell.src));
                        if (memberTex == null) memberTex = Texture2D.whiteTexture;
                        tint = cell.mainTint;
                        useShader = blitMat != null && passTint >= 0;
                        pass = passTint;
                        break;

                    case AtlasChannel.Normal:
                        memberTex = ResolveNormalTexture(cell.src);
                        useShader = blitMat != null && passUnpack >= 0;
                        pass = passUnpack;
                        break;

                    default: // Emission
                        if (!cell.emits)
                        {
                            // 非発光メンバーは描画をスキップしてセルを黒のまま残す(白焼き込みバグ回避)
                            DilateCell(pixels, width, height, cell.rect, GutterPixels);
                            continue;
                        }
                        memberTex = ResolveTexture(cell.src, new[] { "_EmissionMap" });
                        if (memberTex == null) memberTex = Texture2D.whiteTexture; // マップ無し → 単色エミッション
                        tint = cell.emissionTint;
                        useShader = blitMat != null && passTint >= 0;
                        pass = passTint;
                        if (cell.emissionClamped)
                        {
                            report.Info(string.Format("'{0}': HDRエミッション(強度>1)をLDRへクランプしてベイクしました。発光がPC版より弱くなる可能性があります。", cell.src.name));
                        }
                        break;
                }

                if (channel == AtlasChannel.Normal && memberTex == null)
                {
                    // ノーマル未設定メンバーはフラットノーマル(背景)のまま
                    DilateCell(pixels, width, height, cell.rect, GutterPixels);
                    continue;
                }

                if (tint.maxColorComponent > 0.001f || channel == AtlasChannel.Normal)
                {
                    Texture2D cellTex = RenderCell(memberTex, cell.rect.width, cell.rect.height, useShader ? blitMat : null, pass, tint, linear);
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
        /// テクスチャを幅×高さで Blit → ReadPixels して読み取り可能な Texture2D を返す。
        /// blitMat != null: ベイクシェーダーの指定パス(TintCopy/UnpackNormal)を使用(QuestConverter と同一)。
        /// blitMat == null(フォールバック): 素の Blit コピー。メイン/エミッションは tint を CPU 側で近似乗算する。
        /// </summary>
        private static Texture2D RenderCell(Texture source, int width, int height, Material blitMat, int pass, Color tint, bool linearOutput)
        {
            var previous = RenderTexture.active;
            RenderTexture rt = linearOutput
                ? RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                : RenderTexture.GetTemporary(width, height); // 既定 ＝ リニアプロジェクトではsRGB
            Texture src = source != null ? source : Texture2D.whiteTexture;

            if (blitMat != null && pass >= 0)
            {
                blitMat.SetColor("_TintColor", tint);
                Graphics.Blit(src, rt, blitMat, pass);
            }
            else
            {
                Graphics.Blit(src, rt); // 素コピー(ノーマルは生値、メイン/エミッションは後段でCPU乗算)
            }

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, linearOutput);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            // シェーダー不在時のフォールバック: メイン/エミッションの tint を CPU で近似乗算する
            if ((blitMat == null || pass < 0) && !linearOutput &&
                (tint.r < 0.999f || tint.g < 0.999f || tint.b < 0.999f))
            {
                ApplyCpuTint(tex, tint);
            }
            return tex;
        }

        /// <summary>CPUフォールバック用の近似ティント乗算(sRGBバイト空間での単純乗算)。</summary>
        private static void ApplyCpuTint(Texture2D tex, Color tint)
        {
            Color32[] px = tex.GetPixels32();
            float tr = Mathf.Clamp01(tint.r);
            float tg = Mathf.Clamp01(tint.g);
            float tb = Mathf.Clamp01(tint.b);
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(
                    (byte)(px[i].r * tr),
                    (byte)(px[i].g * tg),
                    (byte)(px[i].b * tb),
                    px[i].a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
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

        /// <summary>セルの端の行・列を外側gutterピクセルぶん複製する(ミップマップ縮小時の滲み防止)。</summary>
        private static void DilateCell(Color32[] pixels, int atlasWidth, int atlasHeight, RectInt rect, int gutter)
        {
            int x0 = rect.x;
            int y0 = rect.y;
            int x1 = rect.x + rect.width - 1;
            int y1 = rect.y + rect.height - 1;
            if (rect.width <= 0 || rect.height <= 0) return;

            for (int g = 1; g <= gutter; g++)
            {
                int below = y0 - g;
                int above = y1 + g;
                if (below >= 0) Array.Copy(pixels, y0 * atlasWidth + x0, pixels, below * atlasWidth + x0, rect.width);
                if (above < atlasHeight) Array.Copy(pixels, y1 * atlasWidth + x0, pixels, above * atlasWidth + x0, rect.width);
            }
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
        /// アトラステクスチャを PNG アセットとして GUID安定パスへ保存する(既存があれば上書き)。
        /// PC向けのため Android フォーマットは Automatic(既定/Standalone のインポート設定はプロジェクト既定を使用)。
        /// </summary>
        private static Texture2D SaveAtlasTexture(Texture2D tex, string baseName, string suffix, bool sRGB, bool isNormalMap, string outputDir, ConversionAssetContext assets)
        {
            if (tex == null) return null;
            string folder = outputDir + "/Textures";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(folder + "/" + baseName + suffix + ".png");
            int importMaxSize = Mathf.Max(tex.width, tex.height);
            Texture2D saved = TextureBaker.SaveTextureAsset(tex, path, sRGB, isNormalMap, false, importMaxSize, TextureImporterFormat.Automatic);
            if (saved != null && !ReferenceEquals(saved, tex) && !AssetDatabase.Contains(tex))
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            return saved;
        }

        /// <summary>
        /// グループの統合マテリアルを生成・保存する。代表(先頭)メンバーのコピー(＝元シェーダー・質感を保持)に
        /// アトラステクスチャを差し替え、色はアトラスへ焼き込んだので _Color/_EmissionColor は白へ落とす。
        /// </summary>
        private static Material CreateAtlasMaterial(Group group, Texture2D mainAtlas, Texture2D normalAtlas, Texture2D emissionAtlas, PCOptimizeSettings settings, string baseName, string outputDir, ConversionAssetContext assets)
        {
            Material rep = group.cells[0].src;
            var mat = new Material(rep); // 質感は代表メンバーに統一(元シェーダーを保持)
            mat.name = baseName;

            // アウトラインを外して統合(モード1): 代表がアウトライン版しか無い場合、アトラスコピーの
            // シェーダーをプレーンlilToonへ差し替える(アウトライン専用プロパティは未使用になるだけで無害)。
            if (group.repNeedsPlainShaderSwap)
            {
                Shader plain = GetPlainShaderForOutline(rep);
                if (plain != null) mat.shader = plain;
            }

            // カリング差を無視して混在したグループは両面描画(Cull Off)へ統一する。
            if (group.madeTwoSided)
            {
                if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);          // lilToon / 汎用
                else if (mat.HasProperty("_Culling")) mat.SetFloat("_Culling", 0f);
            }

            // メイン(NonToon は _BaseTexture。系統別に解決して同じプロパティへ差し替える)
            string mainTexProp = ResolveProperty(rep, MainTexPropsFor(rep));
            if (mat.HasProperty(mainTexProp))
            {
                mat.SetTexture(mainTexProp, mainAtlas);
                mat.SetTextureScale(mainTexProp, Vector2.one);
                mat.SetTextureOffset(mainTexProp, Vector2.zero);
            }
            // 色はセルへ焼き込み済み → メインカラーは白(NonToon は色乗算プロパティが無く対象なし)
            foreach (string colorProp in MainColorPropsFor(rep))
            {
                if (mat.HasProperty(colorProp)) mat.SetColor(colorProp, Color.white);
            }

            // ノーマル(既定 _BumpMap / NonToon _NormalMap)
            string normalTexProp = NormalTexPropFor(rep);
            if (group.needNormal && mat.HasProperty(normalTexProp))
            {
                mat.SetTexture(normalTexProp, normalAtlas);
                mat.SetTextureScale(normalTexProp, Vector2.one);
                mat.SetTextureOffset(normalTexProp, Vector2.zero);
                bool normalOn = normalAtlas != null;
                // 代表シェーダーがノーマル用キーワードを使う場合に有効化(存在しなくても無害)
                if (normalOn)
                {
                    mat.EnableKeyword("_NORMALMAP");
                    mat.EnableKeyword("USE_NORMAL_MAPS");
                }
                else
                {
                    mat.DisableKeyword("_NORMALMAP");
                    mat.DisableKeyword("USE_NORMAL_MAPS");
                }
                // lilToon等はキーワードではなく float トグル(_UseBumpMap)でノーマルサンプリングを門番する。
                // これを立てないと、代表(先頭)メンバーがノーマル無し(トグル0)の場合に焼いたノーマルアトラスが
                // 無視され、他メンバーのノーマルが黙って失われる。ConfigureEmission の _UseEmission と対称。
                if (mat.HasProperty("_UseBumpMap")) mat.SetFloat("_UseBumpMap", normalOn ? 1f : 0f);     // lilToon
                if (mat.HasProperty("_UseNormalMap")) mat.SetFloat("_UseNormalMap", normalOn ? 1f : 0f); // 汎用トグル名
            }

            // エミッション(色はセルへ焼き込み済み → 白 or 黒)。設定オフ時は必ず無効化して白焼き込みを避ける。
            bool emissionOn = settings.atlasBakeEmissionMask && group.needEmission && emissionAtlas != null;
            ConfigureEmission(mat, emissionOn, emissionAtlas);

            mat.enableInstancing = true;

            string folder = outputDir + "/Materials";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(folder + "/" + baseName + ".mat");
            Material saved = QuestAssetPersistence.SaveOrOverwriteMaterial(mat, path);
            if (saved != null && !ReferenceEquals(saved, mat) && !AssetDatabase.Contains(mat))
            {
                UnityEngine.Object.DestroyImmediate(mat);
            }
            return saved != null ? saved : mat;
        }

        /// <summary>
        /// アトラスマテリアルのエミッションを設定する。enabled かつ emissionAtlas あり → マップ=アトラス/色=白/有効化、
        /// それ以外 → マップ=null/色=黒/無効化(非発光グループの白焼き込みを防ぐ)。主要シェーダーの有効化方式を網羅。
        /// </summary>
        private static void ConfigureEmission(Material mat, bool enabled, Texture2D emissionAtlas)
        {
            bool on = enabled && emissionAtlas != null;
            if (mat.HasProperty("_EmissionMap"))
            {
                mat.SetTexture("_EmissionMap", on ? emissionAtlas : null);
                mat.SetTextureScale("_EmissionMap", Vector2.one);
                mat.SetTextureOffset("_EmissionMap", Vector2.zero);
            }
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", on ? Color.white : Color.black);
            if (mat.HasProperty("_EmissionStrength")) mat.SetFloat("_EmissionStrength", on ? 1f : 0f);
            if (mat.HasProperty("_UseEmission")) mat.SetFloat("_UseEmission", on ? 1f : 0f); // lilToon
            if (mat.HasProperty("_EmissionUV")) mat.SetFloat("_EmissionUV", 0f);             // Toon Standard

            if (on) mat.EnableKeyword("_EMISSION");
            else mat.DisableKeyword("_EMISSION");
            mat.globalIlluminationFlags = on
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        /// <summary>先頭メンバーと質感パラメータが目立って異なるメンバーを警告する。</summary>
        private static void WarnMaterialParameterDifferences(Group group, ConversionReport report)
        {
            Material first = group.cells[0].src;
            for (int i = 1; i < group.cells.Count; i++)
            {
                Material other = group.cells[i].src;
                bool warned = false;
                foreach (string prop in ComparedFloatProps)
                {
                    if (!first.HasProperty(prop) || !other.HasProperty(prop)) continue;
                    if (Mathf.Abs(first.GetFloat(prop) - other.GetFloat(prop)) > 0.01f)
                    {
                        report.Warn(string.Format("'{0}': 質感パラメータ({1})が '{2}' と異なります。質感は先頭マテリアルに統一されます。", other.name, prop, first.name));
                        warned = true;
                        break;
                    }
                }
                if (warned) continue;

                // アトラス化されない副次テクスチャ(メタリック/マットキャップ/ディテール等)の差異も警告する。
                // これらは先頭メンバーの値を継承するため、メンバー固有のマップが黙って失われる。
                foreach (string prop in SecondaryTexProps)
                {
                    if (!first.HasProperty(prop) || !other.HasProperty(prop)) continue;
                    if (!ReferenceEquals(first.GetTexture(prop), other.GetTexture(prop)))
                    {
                        report.Warn(string.Format("'{0}': 副次テクスチャ({1})が '{2}' と異なります。アトラス化されないため質感は先頭マテリアルに統一されます。", other.name, prop, first.name));
                        break;
                    }
                }
            }
        }

        // ================================================================
        // 色・エミッション・テクスチャ ヘルパー
        // ================================================================

        /// <summary>メインカラー(_Color 等)を返す。存在しなければ白(NonToon は色乗算が無く常に白)。</summary>
        private static Color GetMainColor(Material mat)
        {
            foreach (string prop in MainColorPropsFor(mat))
            {
                if (mat.HasProperty(prop)) return mat.GetColor(prop);
            }
            return Color.white;
        }

        /// <summary>
        /// メンバーがアトラスベイク対象として「発光している」か。色×強度が非黒で、かつ発光が有効なとき true。
        /// 有効判定は主要シェーダーを網羅した保守的な規則(不明時は非発光扱い＝黒セルで安全側)。
        /// map の有無は問わない(PCは色のみ発光も単色セルへ焼く)。tint に 色×強度(LDR)、clamped に HDRクランプ有無を返す。
        /// </summary>
        private static bool HasActiveEmission(Material mat, out Color tint, out bool clamped, out Texture emissionMap)
        {
            tint = Color.black;
            clamped = false;
            emissionMap = ResolveTexture(mat, new[] { "_EmissionMap" });
            if (!mat.HasProperty("_EmissionColor")) return false;

            Color color = mat.GetColor("_EmissionColor");
            float strength = mat.HasProperty("_EmissionStrength") ? mat.GetFloat("_EmissionStrength") : 1f;
            Color combined = color * strength;
            if (combined.maxColorComponent > 1f) clamped = true;
            tint = new Color(Mathf.Clamp01(combined.r), Mathf.Clamp01(combined.g), Mathf.Clamp01(combined.b), 1f);
            if (tint.maxColorComponent <= 0.001f) return false; // 色×強度が黒 ＝ 発光なし(強度0を含む)

            // 発光の有効状態を判定
            if (mat.HasProperty("_UseEmission")) return mat.GetFloat("_UseEmission") > 0.5f;   // lilToon が最優先
            if (mat.IsKeywordEnabled("_EMISSION")) return true;                                // Standard / 汎用
            bool giBlack = (mat.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) != 0;
            if (!giBlack && emissionMap != null) return true;                                  // マップあり＆GI黒指定なし
            return false;                                                                      // 不明 → 非発光(安全側)
        }

        /// <summary>プロパティ候補の先頭から存在するテクスチャを返す(無ければ null)。</summary>
        private static Texture ResolveTexture(Material mat, string[] props)
        {
            foreach (string prop in props)
            {
                if (mat.HasProperty(prop))
                {
                    Texture t = mat.GetTexture(prop);
                    if (t != null) return t;
                }
            }
            return null;
        }

        /// <summary>プロパティ候補の先頭から存在するプロパティ名を返す(無ければ先頭)。</summary>
        private static string ResolveProperty(Material mat, string[] props)
        {
            foreach (string prop in props)
            {
                if (mat.HasProperty(prop)) return prop;
            }
            return props[0];
        }

        /// <summary>候補プロパティのいずれかがマテリアルに存在するか。</summary>
        private static bool HasAnyProperty(Material mat, string[] props)
        {
            foreach (string prop in props)
            {
                if (mat.HasProperty(prop)) return true;
            }
            return false;
        }

        /// <summary>テクスチャプロパティのST(タイリング・オフセット)が単位か(未所持は単位扱い)。</summary>
        private static bool IsTextureStIdentity(Material mat, string property)
        {
            if (!mat.HasProperty(property)) return true;
            Vector2 scale = mat.GetTextureScale(property);
            Vector2 offset = mat.GetTextureOffset(property);
            return Mathf.Abs(scale.x - 1f) < 0.0001f && Mathf.Abs(scale.y - 1f) < 0.0001f &&
                   Mathf.Abs(offset.x) < 0.0001f && Mathf.Abs(offset.y) < 0.0001f;
        }

        /// <summary>カリング値を文字列で返す(_Cull → _Culling の順。無ければ "n")。グループキーの一部。</summary>
        private static string ReadCull(Material mat)
        {
            if (mat.HasProperty("_Cull")) return Mathf.RoundToInt(mat.GetFloat("_Cull")).ToString();
            if (mat.HasProperty("_Culling")) return Mathf.RoundToInt(mat.GetFloat("_Culling")).ToString();
            return "n";
        }

        /// <summary>
        /// セル1個の初期サイズ(px)を決める。長辺の上限を min(元テクスチャ長辺, atlasMaxSize, 縮小計画) とし、
        /// アスペクト比を保つ。色のみ材質は固定の小さな単色セル(ColorCellSize)。
        /// </summary>
        private static void PlanCellSize(Cell cell, PCOptimizeSettings settings, int atlasMaxSize)
        {
            if (cell.colorOnly)
            {
                cell.width = ColorCellSize;
                cell.height = ColorCellSize;
                return;
            }
            Texture tex = ResolveTexture(cell.src, MainTexPropsFor(cell.src));
            int srcW = tex != null ? Mathf.Max(1, tex.width) : 64;
            int srcH = tex != null ? Mathf.Max(1, tex.height) : 64;
            int longEdge = Mathf.Max(srcW, srcH);
            int maxCell = atlasMaxSize;

            int target = Mathf.Min(longEdge, maxCell);
            int planned = GetPlannedSize(settings, tex);
            if (planned > 0) target = Mathf.Min(target, planned);
            target = Mathf.Clamp(target, MinCellSize, maxCell);

            float scale = (float)target / longEdge;
            cell.width = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), MinCellSize, maxCell);
            cell.height = Mathf.Clamp(Mathf.RoundToInt(srcH * scale), MinCellSize, maxCell);
        }

        /// <summary>テクスチャに対応する縮小計画(settings.texturePlan)の目標サイズを返す(計画なしは0、複数該当は最小)。</summary>
        private static int GetPlannedSize(PCOptimizeSettings settings, Texture tex)
        {
            if (tex == null || settings == null || settings.texturePlan == null || settings.texturePlan.Count == 0) return 0;
            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out guid, out localId) || string.IsNullOrEmpty(guid)) return 0;
            int result = 0;
            foreach (TextureSizePlanEntry entry in settings.texturePlan)
            {
                if (entry == null || entry.targetSize <= 0 || entry.textureGuid != guid) continue;
                if (result == 0 || entry.targetSize < result) result = entry.targetSize;
            }
            return result;
        }

        // ================================================================
        // 共通ヘルパー
        // ================================================================

        /// <summary>名前でシェーダーパスのインデックスを探す(大文字小文字無視。見つからなければ-1)。</summary>
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

        /// <summary>マテリアルのアセットGUIDを返す(取得不可は空文字)。</summary>
        private static string GetAssetGuid(Material mat)
        {
            string guid;
            long localId;
            if (mat != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out guid, out localId) && !string.IsNullOrEmpty(guid))
            {
                return guid;
            }
            return string.Empty;
        }

        /// <summary>GUID文字列リストを大小無視のHashSetへ(null安全)。</summary>
        private static HashSet<string> BuildGuidSet(List<string> guids)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (guids != null)
            {
                foreach (string g in guids) if (!string.IsNullOrEmpty(g)) set.Add(g);
            }
            return set;
        }

        /// <summary>
        /// 除外GUID集合を実行時のマテリアル同定へ合わせて翻訳する。除外指定は元アバターのマテリアルGUIDで
        /// 記録されているが、実行はGUIDの異なる複製マテリアル上で行われる。originalToCloneMap があれば、
        /// 除外対象の元マテリアルに対応する複製マテリアルのGUIDを追加する(元GUIDも保持: 複製されなかった
        /// マテリアルはクローンでも元を参照するため、両方が有効)。
        /// </summary>
        private static HashSet<string> TranslateExcludeGuids(List<string> guids, Dictionary<Material, Material> originalToCloneMap)
        {
            HashSet<string> set = BuildGuidSet(guids);
            if (originalToCloneMap == null || set.Count == 0) return set;
            var additions = new List<string>();
            foreach (KeyValuePair<Material, Material> kv in originalToCloneMap)
            {
                if (kv.Key == null || kv.Value == null) continue;
                if (set.Contains(GetAssetGuid(kv.Key)))
                {
                    string cloneGuid = GetAssetGuid(kv.Value);
                    if (!string.IsNullOrEmpty(cloneGuid)) additions.Add(cloneGuid);
                }
            }
            foreach (string g in additions) set.Add(g);
            return set;
        }

        /// <summary>フォルダパスを "Assets/x/y" 形式へ正規化する。</summary>
        private static string NormalizeFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "Assets";
            return folder.Replace('\\', '/').TrimEnd('/');
        }
    }
}
#endif
