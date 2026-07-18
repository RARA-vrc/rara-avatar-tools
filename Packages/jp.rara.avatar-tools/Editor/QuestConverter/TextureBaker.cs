// RARA Quest Converter - テクスチャベイクモジュール
// lilToon の色調補正・レイヤー合成・影・エミッションをテクスチャへ焼き込み、
// PNG アセットとして保存する。MaterialQuestConverter から呼び出される。
//
// RenderTexture / ReadPixels の色空間の扱いは lilToon 本体の
// lilToonInspector.RunBake (lilEditorTextureBaker.cs) と同じパターンを踏襲している:
//   ・出力 Texture2D は new Texture2D(w, h)(= RGBA32 / sRGB / ミップあり)
//   ・RenderTexture.GetTemporary(w, h)(= 既定フォーマット / ReadWrite.Default = リニア設定時は sRGB)
//   ・Graphics.Blit → ReadPixels → Apply
// これによりリニアカラースペースのプロジェクトでも lilToon 純正ベイクと同じ色再現になる。
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RARA.QuestConverter
{
    /// <summary>
    /// テクスチャのベイク・合成・アセット保存を行う静的クラス。
    /// 返す Texture2D は(既存アセットをそのまま返す場合を除き)未保存の一時テクスチャであり、
    /// 破棄の責任は呼び出し側にある。内部で生成した一時マテリアル・一時テクスチャは本クラスが破棄する。
    /// </summary>
    public static class TextureBaker
    {
        private const int SolidTextureSize = 64;

        // ================================================================
        // lilToon メインカラーのベイク
        // ================================================================

        /// <summary>
        /// lilToon マテリアルのメインカラー(_Color / HSVG補正 / グラデーション / 2nd・3rdレイヤー)を
        /// lilToon 純正ベイカーシェーダー(Hidden/ltsother_baker)で合成した Texture2D を返す。
        /// メインテクスチャが無い場合は _Color 単色(64x64)を返す。失敗時は null。
        /// </summary>
        public static Texture2D BakeLilToonMain(Material lilToonMat, int maxSize, ConversionReport report)
        {
            if (lilToonMat == null) return null;
            maxSize = Mathf.Max(16, maxSize);

            Color mainColor = lilToonMat.HasProperty("_Color") ? lilToonMat.GetColor("_Color") : Color.white;

            Texture mainTex = lilToonMat.HasProperty("_MainTex") ? lilToonMat.GetTexture("_MainTex") : null;
            if (mainTex == null)
            {
                // メインテクスチャ未設定 → _Color の単色テクスチャを生成
                report.Info(string.Format("'{0}': メインテクスチャ未設定のため、_Color の単色テクスチャ({1}x{1})を生成しました。", lilToonMat.name, SolidTextureSize));
                return CreateSolidTexture(mainColor, lilToonMat.name + "_main");
            }

            Shader baker = Shader.Find(QuestCompat.LilToonBakerShaderName);
            if (baker == null)
            {
                // lilToonが未インポート等でベイカーが無い場合は、失敗ではなく汎用ベイク
                // (メインテクスチャ × _Color)へフォールバックする(色調補正・レイヤー合成は失われる)
                report.Warn(string.Format("'{0}': lilToonベイク用シェーダー '{1}' が見つかりません。lilToonのインポート状態を確認してください。汎用ベイク(メインテクスチャ×_Color)へフォールバックします(色調補正・2nd/3rdレイヤーは反映されません)。", lilToonMat.name, QuestCompat.LilToonBakerShaderName));
                return BakeGeneric(lilToonMat, maxSize, false, report);
            }

            Material hsvgMaterial = null;
            Texture2D loadedMain = null;
            try
            {
                hsvgMaterial = new Material(baker);

                // ---- メインカラー系プロパティのコピー(lilEditorTextureBaker.TextureBake と同じ流儀) ----
                hsvgMaterial.SetColor("_Color", mainColor);
                if (lilToonMat.HasProperty("_MainTexHSVG"))
                {
                    hsvgMaterial.SetVector("_MainTexHSVG", lilToonMat.GetVector("_MainTexHSVG"));
                }
                if (lilToonMat.HasProperty("_MainGradationStrength"))
                {
                    hsvgMaterial.SetFloat("_MainGradationStrength", lilToonMat.GetFloat("_MainGradationStrength"));
                }
                if (lilToonMat.HasProperty("_MainGradationTex"))
                {
                    hsvgMaterial.SetTexture("_MainGradationTex", lilToonMat.GetTexture("_MainGradationTex"));
                }
                if (lilToonMat.HasProperty("_MainColorAdjustMask"))
                {
                    hsvgMaterial.SetTexture("_MainColorAdjustMask", lilToonMat.GetTexture("_MainColorAdjustMask"));
                }

                // ---- 2nd / 3rd レイヤー(有効時のみコピー) ----
                if (IsOn(lilToonMat, "_UseMain2ndTex"))
                {
                    CopyLayerProperties(lilToonMat, hsvgMaterial, "2nd");
                }
                if (IsOn(lilToonMat, "_UseMain3rdTex"))
                {
                    CopyLayerProperties(lilToonMat, hsvgMaterial, "3rd");
                }

                // ---- ソーステクスチャ(PNG/JPG はファイルから非圧縮・フル解像度で読み直す) ----
                Texture blitSource = LoadReadableSource(mainTex, out loadedMain);
                hsvgMaterial.SetTexture("_MainTex", blitSource);

                int width, height;
                ComputeBakeSize(blitSource, maxSize, out width, out height);

                Texture2D result = RunBlit(blitSource, hsvgMaterial, width, height);
                result.name = lilToonMat.name + "_main";
                return result;
            }
            finally
            {
                if (hsvgMaterial != null) UnityEngine.Object.DestroyImmediate(hsvgMaterial);
                if (loadedMain != null) UnityEngine.Object.DestroyImmediate(loadedMain);
            }
        }

        /// <summary>2nd / 3rd レイヤーのプロパティ一式をベイカーマテリアルへコピーする(layer は "2nd" または "3rd")。</summary>
        private static void CopyLayerProperties(Material src, Material baker, string layer)
        {
            string useProp = "_UseMain" + layer + "Tex";       // _UseMain2ndTex / _UseMain3rdTex
            string colorProp = "_Color" + layer;               // _Color2nd / _Color3rd
            string texProp = "_Main" + layer + "Tex";          // _Main2ndTex / _Main3rdTex
            string maskProp = "_Main" + layer + "BlendMask";   // _Main2ndBlendMask / _Main3rdBlendMask

            CopyFloat(src, baker, useProp);
            CopyColor(src, baker, colorProp);
            CopyFloat(src, baker, texProp + "Angle");
            CopyVector(src, baker, texProp + "DecalAnimation");
            CopyVector(src, baker, texProp + "DecalSubParam");
            CopyFloat(src, baker, texProp + "IsDecal");
            CopyFloat(src, baker, texProp + "IsLeftOnly");
            CopyFloat(src, baker, texProp + "IsRightOnly");
            CopyFloat(src, baker, texProp + "ShouldCopy");
            CopyFloat(src, baker, texProp + "ShouldFlipMirror");
            CopyFloat(src, baker, texProp + "ShouldFlipCopy");
            CopyFloat(src, baker, texProp + "IsMSDF");
            CopyFloat(src, baker, texProp + "BlendMode");
            CopyFloat(src, baker, texProp + "AlphaMode");

            // テクスチャ(null は lilToon 本体のベイク処理と同様に白テクスチャ扱い)
            if (src.HasProperty(texProp))
            {
                Texture layerTex = src.GetTexture(texProp);
                baker.SetTexture(texProp, layerTex != null ? layerTex : Texture2D.whiteTexture);
                baker.SetTextureScale(texProp, src.GetTextureScale(texProp));
                baker.SetTextureOffset(texProp, src.GetTextureOffset(texProp));
            }
            if (src.HasProperty(maskProp))
            {
                Texture maskTex = src.GetTexture(maskProp);
                baker.SetTexture(maskProp, maskTex != null ? maskTex : Texture2D.whiteTexture);
            }
        }

        // ================================================================
        // lilToon アルファマスクの反映(半透明再現用)
        // ================================================================

        /// <summary>
        /// lilToon のアルファマスク機能(_AlphaMaskMode / _AlphaMask / _AlphaMaskScale / _AlphaMaskValue)を
        /// baked(BakeLilToonMain の出力)のアルファチャンネルへ反映する。半透明再現(乗算/加算)専用。
        ///
        /// 【なぜ必要か】lilToon純正ベイカー(Hidden/ltsother_baker)のメインベイクは col *= _Color により
        /// アルファ = _MainTex.a × _Color.a は保持するが、アルファマスク(_AlphaMaskMode != 0)は反映しない。
        /// そのため、
        ///   ・メインテクスチャ無し + アルファマスクで一律半透明化する影板(例: Front_Shadow。
        ///     _MainTex 無し・mode1・マスク無し → 実機アルファは saturate(_Scale + _Value) の一律値)は
        ///     アルファ = _Color.a = 1 の不透明ソリッドとして焼かれ、
        ///   ・アルファマスク(別テクスチャ)で大半を透明化する顔デカール(例: *_Other + Alpha マスク。
        ///     マスクの大部分が低値 → 実機ではほぼ透明)は アルファ ≈ 1 のほぼ不透明として焼かれる。
        /// これらは乗算/加算再現に回しても板として残る。乗算再現はアルファ = 0 で恒等(背景そのまま)に
        /// なるため、この可視域 = アルファの再現が「透明部のゴミRGBを見せない」ための肝になる。
        ///
        /// 【手法】lilToon純正の lilEditorTextureBaker.AutoBakeAlphaMask と同じく、ltsother_baker へ
        /// _ALPHAMASK キーワードを立てて焼き直し(_ALPHAMASK ブランチが col.a に mode 適用後のアルファを出力)、
        /// そのアルファチャンネルだけを baked へ移植する。ランタイム式(lil_common_frag.hlsl)と一致:
        ///   alphaMask = saturate(_AlphaMask.r * _AlphaMaskScale + _AlphaMaskValue)
        ///   mode1: a = alphaMask / mode2: a = a * alphaMask / mode3: a = saturate(a + alphaMask) / mode4: a = saturate(a - alphaMask)
        /// _AlphaMask 未設定時は lilToon 既定どおり白(=1)扱い(mode1 なら一律 saturate(_Scale + _Value))。
        /// _AlphaMaskMode == 0(マスク無効)の場合は何もしない(baked のアルファ = _MainTex.a × _Color.a をそのまま使う)。
        /// なお _ALPHAMASK ブランチは baker が _Color を掛けないため、mode2〜4 の基準アルファは _MainTex.a のみ
        /// (実機の _MainTex.a × _Color.a に対し _Color.a を落とす。lilToon純正ベイカーと同じ挙動。
        /// 通常 _Color.a = 1 かつ影・デカールは mode1 のため実害はない)。
        /// baked が非可読・シェーダー欠落・ブレンド失敗時は何もしない(致命的ではない)。
        /// </summary>
        public static void ApplyLilToonAlphaMask(Texture2D baked, Material src, ConversionReport report)
        {
            if (baked == null || src == null) return;
            int mode = src.HasProperty("_AlphaMaskMode") ? Mathf.RoundToInt(src.GetFloat("_AlphaMaskMode")) : 0;
            if (mode == 0) return; // アルファマスク無効: メインベイクのアルファ(_MainTex.a × _Color.a)をそのまま使う

            Shader baker = Shader.Find(QuestCompat.LilToonBakerShaderName);
            if (baker == null) return; // lilToon未インポート等 → メインベイクのアルファのまま(致命的ではない)

            Material maskMat = null;
            Texture2D loadedMain = null;
            Texture2D loadedMask = null;
            Texture2D alphaTex = null;
            try
            {
                maskMat = new Material(baker);
                maskMat.EnableKeyword("_ALPHAMASK"); // ltsother_baker の _ALPHAMASK ブランチ(lilToon純正 AutoBakeAlphaMask と同じ)
                maskMat.SetFloat("_AlphaMaskMode", mode);
                maskMat.SetFloat("_AlphaMaskScale", src.HasProperty("_AlphaMaskScale") ? src.GetFloat("_AlphaMaskScale") : 1f);
                maskMat.SetFloat("_AlphaMaskValue", src.HasProperty("_AlphaMaskValue") ? src.GetFloat("_AlphaMaskValue") : 0f);

                // _MainTex(mode2〜4 は col.a = _MainTex.a を基準にする。未設定時は白 = 1)。
                // Graphics.Blit は source を _MainTex へ差し込むため、source としても同じテクスチャを渡す。
                Texture mainTex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
                Texture mainBlit = mainTex != null ? LoadReadableSource(mainTex, out loadedMain) : Texture2D.whiteTexture;

                // _AlphaMask(未設定時は lilToon 既定どおり白 = 1)
                Texture maskTex = src.HasProperty("_AlphaMask") ? src.GetTexture("_AlphaMask") : null;
                Texture maskBlit = maskTex != null ? LoadReadableSource(maskTex, out loadedMask) : Texture2D.whiteTexture;
                maskMat.SetTexture("_AlphaMask", maskBlit);

                // baked と同じ解像度で焼く(アルファチャンネルを1:1で移植するため)
                alphaTex = RunBlit(mainBlit, maskMat, baked.width, baked.height);

                Color32[] alphaPixels = alphaTex.GetPixels32();
                Color32[] mainPixels = baked.GetPixels32();
                if (alphaPixels.Length == mainPixels.Length)
                {
                    for (int i = 0; i < mainPixels.Length; i++)
                    {
                        mainPixels[i].a = alphaPixels[i].a;
                    }
                    baked.SetPixels32(mainPixels);
                    baked.Apply(false, false);
                    if (report != null)
                    {
                        report.Info(string.Format("'{0}': lilToonのアルファマスク(mode {1})をベイク結果のアルファへ反映しました(半透明再現の可視域に使用)。", src.name, mode));
                    }
                }
            }
            catch (Exception ex)
            {
                if (report != null)
                {
                    report.Warn(string.Format("'{0}': lilToonアルファマスクの反映に失敗したため、メインベイクのアルファのまま続行します: {1}", src.name, ex.Message));
                }
            }
            finally
            {
                if (maskMat != null) UnityEngine.Object.DestroyImmediate(maskMat);
                if (loadedMain != null) UnityEngine.Object.DestroyImmediate(loadedMain);
                if (loadedMask != null) UnityEngine.Object.DestroyImmediate(loadedMask);
                if (alphaTex != null) UnityEngine.Object.DestroyImmediate(alphaTex);
            }
        }

        // ================================================================
        // 影・エミッションの合成(Toon Lit 用)
        // ================================================================

        /// <summary>
        /// ベイク済みメインテクスチャへ、lilToon の影色の乗算とエミッションの加算を合成した
        /// 新しい Texture2D を返す(サイズは baseTex と同じ)。
        /// 合成する要素が無い場合やシェーダー欠落時は baseTex をそのまま返す。
        /// </summary>
        public static Texture2D Composite(Texture2D baseTex, Material srcLilToonMat, bool multiplyShadow, bool addEmission, ConversionReport report)
        {
            if (baseTex == null || srcLilToonMat == null) return baseTex;

            // ---- 影の乗算色: lerp(白, _ShadowColor.rgb, _ShadowStrength) ----
            Color multiplyColor = Color.white;
            bool shadowOn = multiplyShadow && IsOn(srcLilToonMat, "_UseShadow");
            if (shadowOn)
            {
                Color shadowColor = srcLilToonMat.HasProperty("_ShadowColor") ? srcLilToonMat.GetColor("_ShadowColor") : new Color(0.82f, 0.76f, 0.85f, 1f);
                float strength = srcLilToonMat.HasProperty("_ShadowStrength") ? Mathf.Clamp01(srcLilToonMat.GetFloat("_ShadowStrength")) : 1f;
                multiplyColor = Color.Lerp(Color.white, shadowColor, strength);
                multiplyColor.a = 1f; // アルファはメインテクスチャのまま(シェーダー側でも維持される)
            }

            // ---- エミッション加算: _EmissionColor.rgb * _EmissionBlend(テクスチャは _EmissionMap) ----
            bool emissionOn = addEmission && IsOn(srcLilToonMat, "_UseEmission") && srcLilToonMat.HasProperty("_EmissionColor");
            Color emissionColor = Color.black;
            Texture emissionTex = null;
            if (emissionOn)
            {
                emissionColor = srcLilToonMat.GetColor("_EmissionColor"); // HDR の可能性あり(最終クランプはシェーダー側)
                float blend = srcLilToonMat.HasProperty("_EmissionBlend") ? Mathf.Clamp01(srcLilToonMat.GetFloat("_EmissionBlend")) : 1f;
                emissionColor *= blend;
                emissionColor.a = 1f;
                emissionTex = srcLilToonMat.HasProperty("_EmissionMap") ? srcLilToonMat.GetTexture("_EmissionMap") : null;
                if (emissionColor.maxColorComponent <= 0f) emissionOn = false; // 黒エミッションは無視
            }
            if (addEmission && IsOn(srcLilToonMat, "_UseEmission2nd"))
            {
                report.Warn(string.Format("'{0}': 2ndエミッションは無視されます(メインテクスチャへの合成対象外)。", srcLilToonMat.name));
            }

            // 合成する要素が無ければ元テクスチャをそのまま返す
            if (!shadowOn && !emissionOn) return baseTex;

            Shader bakeShader = Shader.Find(QuestCompat.BakeShaderName);
            if (bakeShader == null)
            {
                report.Warn(string.Format("'{0}': 合成用シェーダー '{1}' が見つかりません。影・エミッションの合成をスキップします。", srcLilToonMat.name, QuestCompat.BakeShaderName));
                return baseTex;
            }

            Material bakeMat = null;
            try
            {
                bakeMat = new Material(bakeShader);
                bakeMat.SetColor("_MultiplyColor", shadowOn ? multiplyColor : Color.white);
                bakeMat.SetColor("_EmissionColor", emissionOn ? emissionColor : Color.black);
                bakeMat.SetFloat("_EmissionUseTex", (emissionOn && emissionTex != null) ? 1f : 0f);
                if (emissionOn && emissionTex != null)
                {
                    bakeMat.SetTexture("_EmissionTex", emissionTex);
                    // エミッションのタイリング・オフセットを引き継ぐ
                    bakeMat.SetTextureScale("_EmissionTex", srcLilToonMat.GetTextureScale("_EmissionMap"));
                    bakeMat.SetTextureOffset("_EmissionTex", srcLilToonMat.GetTextureOffset("_EmissionMap"));
                }

                Texture2D result = RunBlit(baseTex, bakeMat, baseTex.width, baseTex.height);
                result.name = baseTex.name + "_comp";
                return result;
            }
            finally
            {
                if (bakeMat != null) UnityEngine.Object.DestroyImmediate(bakeMat);
            }
        }

        // ================================================================
        // 非 lilToon 汎用ベイク
        // ================================================================

        /// <summary>
        /// 汎用マテリアルのメインテクスチャ×_Color(+任意でエミッション加算)をベイクした Texture2D を返す。
        /// メインテクスチャが無い場合は単色(64x64)を返す。失敗時は null。
        /// </summary>
        public static Texture2D BakeGeneric(Material srcMat, int maxSize, bool addEmission, ConversionReport report)
        {
            if (srcMat == null) return null;
            maxSize = Mathf.Max(16, maxSize);

            Color multiplyColor = srcMat.HasProperty("_Color") ? srcMat.GetColor("_Color") : Color.white;

            // エミッション(プロパティが存在し、_EMISSION キーワードを持つシェーダーでは有効時のみ)
            bool emissionOn = addEmission && srcMat.HasProperty("_EmissionColor");
            Color emissionColor = Color.black;
            Texture emissionTex = null;
            if (emissionOn)
            {
                var emissionKeyword = srcMat.shader.keywordSpace.FindKeyword("_EMISSION");
                bool keywordOk = !emissionKeyword.isValid || srcMat.IsKeywordEnabled("_EMISSION");
                emissionColor = srcMat.GetColor("_EmissionColor");
                emissionColor.a = 1f;
                emissionTex = srcMat.HasProperty("_EmissionMap") ? srcMat.GetTexture("_EmissionMap") : null;
                emissionOn = keywordOk && emissionColor.maxColorComponent > 0f;
            }

            Texture mainTex = srcMat.mainTexture;
            if (mainTex == null)
            {
                // メインテクスチャ無し → _Color(+エミッション色)の単色テクスチャ
                Color solid = multiplyColor;
                if (emissionOn)
                {
                    solid.r += emissionColor.r;
                    solid.g += emissionColor.g;
                    solid.b += emissionColor.b;
                }
                report.Warn(string.Format("'{0}': ベイク可能なメインテクスチャが無いため、_Color の単色テクスチャ({1}x{1})を生成しました。Sprites/UI系などレンダラー側からテクスチャを供給するマテリアルの場合、単色の板に見える可能性があります。", srcMat.name, SolidTextureSize));
                return CreateSolidTexture(solid, srcMat.name + "_main");
            }

            Shader bakeShader = Shader.Find(QuestCompat.BakeShaderName);
            if (bakeShader == null)
            {
                report.Warn(string.Format("'{0}': 合成用シェーダー '{1}' が見つかりません。ベイクをスキップします。", srcMat.name, QuestCompat.BakeShaderName));
                return null;
            }

            Material bakeMat = null;
            Texture2D loadedMain = null;
            try
            {
                bakeMat = new Material(bakeShader);
                Color mul = multiplyColor;
                mul.a = 1f; // アルファはメインテクスチャのまま
                bakeMat.SetColor("_MultiplyColor", mul);
                bakeMat.SetColor("_EmissionColor", emissionOn ? emissionColor : Color.black);
                bakeMat.SetFloat("_EmissionUseTex", (emissionOn && emissionTex != null) ? 1f : 0f);
                if (emissionOn && emissionTex != null)
                {
                    bakeMat.SetTexture("_EmissionTex", emissionTex);
                    if (srcMat.HasProperty("_EmissionMap"))
                    {
                        bakeMat.SetTextureScale("_EmissionTex", srcMat.GetTextureScale("_EmissionMap"));
                        bakeMat.SetTextureOffset("_EmissionTex", srcMat.GetTextureOffset("_EmissionMap"));
                    }
                }

                Texture blitSource = LoadReadableSource(mainTex, out loadedMain);
                int width, height;
                ComputeBakeSize(blitSource, maxSize, out width, out height);

                Texture2D result = RunBlit(blitSource, bakeMat, width, height);
                result.name = srcMat.name + "_main";
                return result;
            }
            finally
            {
                if (bakeMat != null) UnityEngine.Object.DestroyImmediate(bakeMat);
                if (loadedMain != null) UnityEngine.Object.DestroyImmediate(loadedMain);
            }
        }

        // ================================================================
        // Poiyomi メインカラーのベイク
        // ================================================================

        /// <summary>
        /// Poiyomi マテリアルのアルベド(メイン × _Color、必要なら Main Color Adjust の
        /// 色相/彩度/明度/ガンマ補正)を Hidden/RARA/QuestBake の "PoiyomiColorAdjust" パスで焼いた
        /// Texture2D を返す。アルファは維持する(半透明再現の可視域に使うため)。
        /// ・_MainColorAdjustToggle が有効(>0.5)のときのみ補正を適用し、無効/欠落なら中立値で
        ///   単純な tint ベイク(メイン × _Color)に退化する。
        /// ・メインテクスチャはロック時に剥がされていることがあるため QuestCompat.GetPoiyomiTexture で
        ///   タグ("_stripped_tex__MainTex")復元も試みる。復元もできなければ _Color 単色(補正適用)を返す。
        /// ・ベイクシェーダーやパスが見つからない場合は汎用ベイク(メイン × _Color)へフォールバックする。
        /// 失敗時は null。
        /// </summary>
        public static Texture2D BakePoiyomiMain(Material poiMat, int maxSize, ConversionReport report)
        {
            if (poiMat == null) return null;
            maxSize = Mathf.Max(16, maxSize);

            Color mainColor = poiMat.HasProperty(QuestCompat.PoiyomiColorProp) ? poiMat.GetColor(QuestCompat.PoiyomiColorProp) : Color.white;
            bool adjust = poiMat.HasProperty(QuestCompat.PoiyomiColorAdjustToggleProp) && poiMat.GetFloat(QuestCompat.PoiyomiColorAdjustToggleProp) > 0.5f;

            float saturation = adjust && poiMat.HasProperty(QuestCompat.PoiyomiSaturationProp) ? poiMat.GetFloat(QuestCompat.PoiyomiSaturationProp) : 0f;
            // Poiyomi の _MainBrightness は中立0(Range -1..2)で base*(_MainBrightness+1) として適用されるため、中立の既定は 0f
            float brightness = adjust && poiMat.HasProperty(QuestCompat.PoiyomiBrightnessProp) ? poiMat.GetFloat(QuestCompat.PoiyomiBrightnessProp) : 0f;
            float gamma = adjust && poiMat.HasProperty(QuestCompat.PoiyomiGammaProp) ? poiMat.GetFloat(QuestCompat.PoiyomiGammaProp) : 1f;
            float hueShift = adjust && poiMat.HasProperty(QuestCompat.PoiyomiHueShiftProp) ? poiMat.GetFloat(QuestCompat.PoiyomiHueShiftProp) : 0f;

            Texture mainTex = QuestCompat.GetPoiyomiTexture(poiMat, QuestCompat.PoiyomiMainTexProp);
            if (mainTex == null)
            {
                // メイン未設定(ロックで剥がされ復元もできない場合を含む)→ _Color 単色に補正を適用して返す
                Color solid = adjust ? ApplyPoiyomiAdjust(mainColor, saturation, brightness, gamma, hueShift) : mainColor;
                report.Info(string.Format("Poiyomi: 『{0}』はメインテクスチャが無い(またはロックで剥離済み)ため、_Color の単色テクスチャ({1}x{1})を生成しました。", poiMat.name, SolidTextureSize));
                return CreateSolidTexture(solid, poiMat.name + "_main");
            }

            Shader bakeShader = Shader.Find(QuestCompat.BakeShaderName);
            int pass = -1;
            if (bakeShader != null)
            {
                Material probe = new Material(bakeShader);
                pass = FindPassIndex(probe, "PoiyomiColorAdjust");
                UnityEngine.Object.DestroyImmediate(probe);
            }
            if (bakeShader == null || pass < 0)
            {
                report.Warn(string.Format("Poiyomi: 『{0}』の色調補正パス(PoiyomiColorAdjust)が見つからないため、汎用ベイク(メイン×_Color)へフォールバックします(色調補正は反映されません)。RARA_QuestBake.shader の更新・再インポートを確認してください。", poiMat.name));
                return BakeGeneric(poiMat, maxSize, false, report);
            }

            Material bakeMat = null;
            Texture2D loadedMain = null;
            try
            {
                bakeMat = new Material(bakeShader);
                bakeMat.SetColor("_MultiplyColor", mainColor); // アルファも維持(col.a = mainCol.a * _MultiplyColor.a)
                bakeMat.SetFloat("_Saturation", saturation);
                // Poiyomi の中立0 明度を、ベイクシェーダーの乗算係数(中立1)へ +1 して渡す(base*(_MainBrightness+1) と一致)
                bakeMat.SetFloat("_MainBrightness", brightness + 1f);
                bakeMat.SetFloat("_MainGamma", gamma);
                bakeMat.SetFloat("_MainHueShift", hueShift);

                Texture blitSource = LoadReadableSource(mainTex, out loadedMain);
                int width, height;
                ComputeBakeSize(blitSource, maxSize, out width, out height);

                Texture2D result = RunBlit(blitSource, bakeMat, pass, width, height);
                result.name = poiMat.name + "_main";
                return result;
            }
            finally
            {
                if (bakeMat != null) UnityEngine.Object.DestroyImmediate(bakeMat);
                if (loadedMain != null) UnityEngine.Object.DestroyImmediate(loadedMain);
            }
        }

        /// <summary>
        /// Poiyomi の Main Color Adjust を単色に対して CPU 側で適用する(メインテクスチャが無い場合用)。
        /// 順序・式は RARA_QuestBake.shader の "PoiyomiColorAdjust" パスへ渡す係数(brightness+1)と一致させる
        /// (色相シフト → 彩度 lerp → 明度((brightness+1)乗算・中立0)→ ガンマ)。アルファは維持する。
        /// </summary>
        private static Color ApplyPoiyomiAdjust(Color c, float saturation, float brightness, float gamma, float hueShift)
        {
            float h, s, v;
            Color.RGBToHSV(new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b)), out h, out s, out v);
            h = Mathf.Repeat(h + hueShift, 1f);
            Color rgb = Color.HSVToRGB(h, s, v);

            float luma = 0.299f * rgb.r + 0.587f * rgb.g + 0.114f * rgb.b;
            float factor = 1f + saturation;
            rgb.r = Mathf.Lerp(luma, rgb.r, factor);
            rgb.g = Mathf.Lerp(luma, rgb.g, factor);
            rgb.b = Mathf.Lerp(luma, rgb.b, factor);

            rgb.r = Mathf.Clamp01(Mathf.Pow(Mathf.Max(rgb.r * (brightness + 1f), 0f), gamma));
            rgb.g = Mathf.Clamp01(Mathf.Pow(Mathf.Max(rgb.g * (brightness + 1f), 0f), gamma));
            rgb.b = Mathf.Clamp01(Mathf.Pow(Mathf.Max(rgb.b * (brightness + 1f), 0f), gamma));
            rgb.a = c.a;
            return rgb;
        }

        // ================================================================
        // 影ランプ生成(Toon Standard 用)
        // ================================================================

        /// <summary>
        /// lilToon の影設定から Toon Standard の _Ramp 用横長ランプ(256x4)を生成する。
        /// _UseShadow が無効なら null。
        /// ランプの向きは ToonStandard の Helpers.cginc(SampleShadowRampTexture / ndl01 = NdotL*0.5+0.5)で
        /// 検証済み: uv.x = 0 が完全な影側、uv.x = 1 が完全なライト側。
        /// 帯の境界は lilToon の lilTooningNoSaturateScale と同じく
        /// border - blur/2 〜 border + blur/2 の線形グラデーションとする。
        /// </summary>
        public static Texture2D GenerateShadowRamp(Material lilToonMat)
        {
            if (lilToonMat == null) return null;
            if (!IsOn(lilToonMat, "_UseShadow")) return null;

            // ---- 1st 影(色は _ShadowStrength ぶんだけ白から影色へ寄せる) ----
            Color shadowColor = lilToonMat.HasProperty("_ShadowColor") ? lilToonMat.GetColor("_ShadowColor") : new Color(0.82f, 0.76f, 0.85f, 1f);
            float strength = lilToonMat.HasProperty("_ShadowStrength") ? Mathf.Clamp01(lilToonMat.GetFloat("_ShadowStrength")) : 1f;
            float border1 = lilToonMat.HasProperty("_ShadowBorder") ? Mathf.Clamp01(lilToonMat.GetFloat("_ShadowBorder")) : 0.5f;
            float blur1 = lilToonMat.HasProperty("_ShadowBlur") ? Mathf.Clamp01(lilToonMat.GetFloat("_ShadowBlur")) : 0.1f;
            Color shade1 = Color.Lerp(Color.white, new Color(shadowColor.r, shadowColor.g, shadowColor.b, 1f), strength);

            // ---- 2nd 影(_Shadow2ndColor.a > 0 のときのみ) ----
            Color shadow2Color = lilToonMat.HasProperty("_Shadow2ndColor") ? lilToonMat.GetColor("_Shadow2ndColor") : new Color(0f, 0f, 0f, 0f);
            bool useSecond = shadow2Color.a > 0f;
            float border2 = lilToonMat.HasProperty("_Shadow2ndBorder") ? Mathf.Clamp01(lilToonMat.GetFloat("_Shadow2ndBorder")) : 0.15f;
            float blur2 = lilToonMat.HasProperty("_Shadow2ndBlur") ? Mathf.Clamp01(lilToonMat.GetFloat("_Shadow2ndBlur")) : blur1;

            const int width = 256;
            const int height = 4;
            var ramp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            ramp.name = lilToonMat.name + "_ramp";
            ramp.wrapMode = TextureWrapMode.Clamp;
            ramp.filterMode = FilterMode.Bilinear;

            var pixels = new Color[width * height];
            for (int x = 0; x < width; x++)
            {
                float v = (x + 0.5f) / width; // ndl01 (0 = 影側, 1 = ライト側)

                // 1st 影: v が境界より上でライト(白)、下で影色
                float lit1 = BandFactor(v, border1, blur1);
                Color col = Color.Lerp(shade1, Color.white, lit1);

                // 2nd 影: より深い影側を 2nd 影色(アルファ = 適用率)へ寄せる
                if (useSecond)
                {
                    float lit2 = BandFactor(v, border2, blur2);
                    var shade2 = new Color(shadow2Color.r, shadow2Color.g, shadow2Color.b, 1f);
                    col = Color.Lerp(col, shade2, (1f - lit2) * shadow2Color.a * strength);
                }

                col.a = 1f;
                for (int y = 0; y < height; y++)
                {
                    pixels[y * width + x] = col;
                }
            }
            ramp.SetPixels(pixels);
            ramp.Apply(false, false); // 読み取り可能なまま保持(保存時に EncodeToPNG するため)
            return ramp;
        }

        /// <summary>lilTooningNoSaturateScale 相当: border ± blur/2 の範囲で 0(影)→1(ライト)へ線形補間。</summary>
        private static float BandFactor(float value, float border, float blur)
        {
            float borderMin = Mathf.Clamp01(border - blur * 0.5f);
            float borderMax = Mathf.Clamp01(border + blur * 0.5f);
            return Mathf.Clamp01((value - borderMin) / Mathf.Max(borderMax - borderMin, 0.0001f));
        }

        // ================================================================
        // NonToon 影ランプ生成(グラデーション配列スライス → Toon Standard 用)
        // ================================================================

        /// <summary>
        /// NonToon の Shade モジュールが参照するグラデーション配列
        /// (マテリアルの _SharedGradients の _jp_lilxyzw_nontoon_shade_ShadeGradientIndex スライス)から、
        /// Toon Standard の _Ramp 用横長ランプ(256x4)を生成する。_SharedGradients 未割り当て・
        /// インデックス無効・スライス読み出し失敗時は null(呼び出し側はSDK既定ランプへフォールバック)。
        ///
        /// 【向き】uv.x = 0 が影側、uv.x = 1 がライト側(Toon Standard の SampleShadowRampTexture は
        /// ndl01 = NdotL*0.5+0.5 でサンプリング、Helpers.cginc で検証済み)。
        ///
        /// 【マッピング】NonToon の Shade(Modules/Shade/phase_shade.hlsl で検証)は、リアルタイム影
        /// sd.shadow=1 の基本状態で shade = min(halfLambert, _ShadeGradientRange.y) を用いて
        /// SCSampleClamp(gradients, float2(shade,0.5), index) でサンプリングする。よってランプは
        ///   ramp(x) = gradient( min(x, _ShadeGradientRange.y) )   (既定 range=(0,1) では ramp(x)=gradient(x))
        /// とする。_ShadeGradientRange.x はリアルタイム影(sd.shadow)の遷移にのみ効き、単一ランプでは
        /// 分離表現できないため未使用(1枚のランプへ落とす近似)。
        ///
        /// 【色空間】lilToonランプ経路(GenerateShadowRamp)と同じく、グラデーションの見た目どおりの
        /// 色値(as-authored)をそのままランプ画素へ入れ、呼び出し側が sRGB=false で保存する。
        /// ReadGradientArraySlice はプレビュー用シェーダー(または CopyTexture)経由で元の格納色を取り出す。
        /// </summary>
        public static Texture2D BakeNonToonRamp(Material src, ConversionReport report)
        {
            if (src == null) return null;

            var arr = src.HasProperty(QuestCompat.NonToonSharedGradientsProp)
                ? src.GetTexture(QuestCompat.NonToonSharedGradientsProp) as Texture2DArray
                : null;
            if (arr == null) return null; // _SharedGradients 未割り当て(既定の白)→ ランプ生成不可

            int index = QuestCompat.GetIntegerSafe(src, QuestCompat.NonToonShadeGradientIndexProp, -1);
            if (index < 0 || index >= arr.depth) return null;

            // _ShadeGradientRange は SC_float4 だが Color 型として保存される(.mat の m_Colors。.y = 明側上限)
            Color rangeCol = src.HasProperty(QuestCompat.NonToonShadeGradientRangeProp)
                ? src.GetColor(QuestCompat.NonToonShadeGradientRangeProp)
                : new Color(0f, 1f, 0f, 0f);
            float rangeMax = rangeCol.g > 0.0001f ? Mathf.Clamp01(rangeCol.g) : 1f;

            Color[] sliceRow = ReadGradientArraySlice(arr, index);
            if (sliceRow == null || sliceRow.Length == 0)
            {
                if (report != null)
                {
                    report.Info(string.Format("NonToon: 『{0}』のグラデーション配列スライス(index {1})を読み取れなかったため、既定の影ランプを使用します。", src.name, index));
                }
                return null;
            }

            const int width = 256;
            const int height = 4;
            var ramp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            ramp.name = src.name + "_ramp";
            ramp.wrapMode = TextureWrapMode.Clamp;
            ramp.filterMode = FilterMode.Bilinear;

            var pixels = new Color[width * height];
            for (int x = 0; x < width; x++)
            {
                float rampX = (x + 0.5f) / width;              // half-lambert 値(0=影側, 1=ライト側)
                float t = Mathf.Clamp01(Mathf.Min(rampX, rangeMax)); // グラデーション参照位置
                Color col = SampleRow(sliceRow, t);
                col.a = 1f;
                for (int y = 0; y < height; y++) pixels[y * width + x] = col;
            }
            ramp.SetPixels(pixels);
            ramp.Apply(false, false); // 読み取り可能なまま保持(保存時に EncodeToPNG するため)
            return ramp;
        }

        /// <summary>
        /// Texture2DArray の指定スライスを1行の色配列(幅ぶん)として読み出す。失敗時は null。
        /// グラデーション配列はミップ付き・非CPU可読の可能性があるため、RenderTexture 経由で ReadPixels する。
        /// まず shadercore のプレビュー用シェーダー(Hidden/ShaderCore/PreviewTexture、スライス選択サンプリング)で
        /// Blit し、無ければ Graphics.CopyTexture でスライスをコピーするフォールバックを試みる。
        /// 元の格納色(as-authored)を得るため、リニアRT + リニアTexture2Dで生値を保持する
        /// (プレビュー用シェーダーはsRGB配列のサンプル時デコードと表示用ガンマ再エンコードが相殺し、格納色へ戻る)。
        /// </summary>
        private static Color[] ReadGradientArraySlice(Texture2DArray arr, int slice)
        {
            if (arr == null) return null;
            int w = Mathf.Max(1, arr.width);
            int h = Mathf.Max(1, arr.height);

            RenderTexture rt = null;
            RenderTexture prevActive = RenderTexture.active;
            Material blitMat = null;
            Texture2D readback = null;
            try
            {
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);

                bool blitted = false;

                // ---- 方法1: shadercore のプレビュー用シェーダーでスライスをサンプリング(参照実装 TextureUtils と同じ) ----
                Shader preview = Shader.Find("Hidden/ShaderCore/PreviewTexture");
                if (preview != null)
                {
                    blitMat = new Material(preview);
                    if (blitMat.HasProperty("_MainTexArray")) blitMat.SetTexture("_MainTexArray", arr);
                    if (blitMat.HasProperty("_Index")) blitMat.SetFloat("_Index", slice); // legacy Int プロパティ
                    if (blitMat.HasProperty("_Channel")) blitMat.SetFloat("_Channel", -1f); // -1 = RGB全チャンネル
                    Graphics.Blit(null, rt, blitMat);
                    blitted = true;
                }

                // ---- 方法2: CopyTexture でスライスを直接コピー(プレビュー用シェーダーが無い環境向け) ----
                if (!blitted && (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.TextureToRT) != 0)
                {
                    Graphics.CopyTexture(arr, slice, 0, rt, 0, 0);
                    blitted = true;
                }

                if (!blitted) return null;

                RenderTexture.active = rt;
                readback = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readback.Apply(false, false);

                Color[] all = readback.GetPixels(); // w*h。グラデーション配列は高さ1のため先頭行を採用
                var row = new Color[w];
                for (int x = 0; x < w; x++) row[x] = all[x];
                return row;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (blitMat != null) UnityEngine.Object.DestroyImmediate(blitMat);
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        /// <summary>1行の色配列を u∈[0,1] でバイリニアサンプリング(Clamp)する。</summary>
        private static Color SampleRow(Color[] row, float u)
        {
            int n = row.Length;
            if (n <= 0) return Color.white;
            if (n == 1) return row[0];
            float f = Mathf.Clamp01(u) * (n - 1);
            int i0 = Mathf.FloorToInt(f);
            int i1 = Mathf.Min(i0 + 1, n - 1);
            return Color.Lerp(row[i0], row[i1], f - i0);
        }

        // ================================================================
        // アセット保存
        // ================================================================

        /// <summary>
        /// テクスチャを PNG として assetPath へ保存し、インポート設定
        /// (sRGB / ノーマルマップ / ミップマップ / 最大サイズ / Android 圧縮形式)を適用して
        /// 読み込み済みアセットを返す。失敗時は null。
        /// 実行内のパス衝突回避は呼び出し側(QuestAssetPersistence.StablePathRegistry 等)が行う。
        ///
        /// 【上書きとGUID保持】assetPath に既存の PNG がある場合は、アセットを削除せず
        /// ファイルのバイト列だけを上書きして再インポートする(File.WriteAllBytes + ImportAsset)。
        /// GUID が変わらないため、シーンに残っている前回の _Quest アバターや、
        /// ユーザーが元アバターへ手動で割り当てた生成テクスチャの参照が
        /// 2回目以降の変換でも壊れない。インポート設定は毎回同じ内容を冪等に比較・適用し、
        /// 実際に変更があった場合のみ SaveAndReimport する(不要な再インポートを避ける)。
        ///
        /// 【ランプ用の特別処理】isRamp = true の場合は影ランプとみなし、
        /// ミップマップ無効・ストリーミング無効・wrapMode = Clamp を設定する
        /// (ランプはグラデーション参照用テクスチャのため、ミップやリピートがあると陰影が破綻する)。
        /// ファイル名による推定は行わない(マテリアル名に "_ramp" を含むだけでアルベドが
        /// ランプ扱いになる誤判定を防ぐため、呼び出し側が明示的に指定する)。
        /// </summary>
        public static Texture2D SaveTextureAsset(Texture2D tex, string assetPath, bool sRGB, bool isNormalMap, bool isRamp, int maxTextureSize, TextureImporterFormat androidFormat)
        {
            if (tex == null || string.IsNullOrEmpty(assetPath)) return null;

            // 親フォルダを保証(呼び出し側でも作成済みだが防御的に)
            string folder = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(folder))
            {
                QuestConverterUtility.EnsureFolder(folder.Replace('\\', '/'));
            }

            byte[] png = tex.EncodeToPNG();
            if (png == null || png.Length == 0)
            {
                Debug.LogWarning("[RARA QuestConverter] PNGエンコードに失敗しました: " + assetPath);
                return null;
            }
            // 既存ファイルがあってもバイト列の上書きのみ(削除→再作成しないためGUIDが保持される)
            File.WriteAllBytes(Path.GetFullPath(assetPath), png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                // 設定は差分がある項目だけ書き込み、変更があった場合のみ SaveAndReimport する
                // (同じ設定で上書き保存を繰り返しても再インポートが1回で済む)
                bool changed = false;

                TextureImporterType textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (importer.textureType != textureType) { importer.textureType = textureType; changed = true; }
                if (importer.sRGBTexture != sRGB) { importer.sRGBTexture = sRGB; changed = true; }
                bool alphaIsTransparency = sRGB && !isNormalMap && !isRamp;
                if (importer.alphaIsTransparency != alphaIsTransparency) { importer.alphaIsTransparency = alphaIsTransparency; changed = true; }
                if (importer.maxTextureSize != maxTextureSize) { importer.maxTextureSize = maxTextureSize; changed = true; }

                bool mipmapEnabled = !isRamp; // ランプはミップ無効(グラデーション参照用のため)
                if (importer.mipmapEnabled != mipmapEnabled) { importer.mipmapEnabled = mipmapEnabled; changed = true; }
                if (importer.streamingMipmaps != mipmapEnabled) { importer.streamingMipmaps = mipmapEnabled; changed = true; }
                if (isRamp && importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }

                TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
                if (android == null || !android.overridden ||
                    android.maxTextureSize != maxTextureSize || android.format != androidFormat)
                {
                    importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                    {
                        name = "Android",
                        overridden = true,
                        maxTextureSize = maxTextureSize,
                        format = androidFormat,
                    });
                    changed = true;
                }

                if (changed) importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // ================================================================
        // テクスチャ縮小コピー(textureSizePlan 用)
        // ================================================================

        /// <summary>
        /// 元テクスチャを一切変更せず、targetSize(長辺・アスペクト比維持)へ縮小したコピーを
        /// assetPath へ PNG アセットとして生成して返す(settings.textureSizePlan の適用用)。
        /// ・isNormalMap = true: インポート済みノーマルマップはプラットフォーム向けに
        ///   スウィズルされている(DXTnm等)ため、Hidden/RARA/QuestBake の "UnpackNormal" パスで
        ///   標準RGBへ展開してから縮小し、isNormalMap = true で保存してインポーターに
        ///   ノーマルマップとして再エンコードさせる。
        /// ・sRGB = true(色テクスチャ): 既定のRT(リニアプロジェクトではsRGB)で色再現を維持する。
        /// ・sRGB = false(データテクスチャ): リニアRT+リニアTexture2Dで生値を保持する。
        /// 保存は SaveTextureAsset に委譲するため、既存パスへはGUIDを保持したまま上書きされる
        /// (2回目変換で旧参照を壊さない)。
        /// source が null・assetPath が空・targetSize が0以下・シェーダー欠落・保存失敗時は
        /// 警告を出して null を返す(呼び出し側は元テクスチャへのフォールバックを検討すること)。
        /// </summary>
        public static Texture2D DownscaleTextureCopy(Texture source, int targetSize, bool isNormalMap, bool sRGB, string assetPath, TextureImporterFormat androidFormat)
        {
            if (source == null || string.IsNullOrEmpty(assetPath) || targetSize <= 0)
            {
                Debug.LogWarning(string.Format(
                    "[RARA QuestConverter] テクスチャ縮小コピーの引数が不正なためスキップしました(source: {0} / assetPath: '{1}' / targetSize: {2})。",
                    source != null ? source.name : "(null)", assetPath, targetSize));
                return null;
            }

            int width, height;
            ComputeBakeSize(source, targetSize, out width, out height);

            Material unpackMat = null;
            Texture2D readable = null;
            try
            {
                int unpackPass = -1;
                if (isNormalMap)
                {
                    Shader bakeShader = Shader.Find(QuestCompat.BakeShaderName);
                    if (bakeShader == null)
                    {
                        Debug.LogWarning(string.Format(
                            "[RARA QuestConverter] '{0}': 縮小用シェーダー '{1}' が見つからないため、ノーマルマップの縮小コピーをスキップしました。",
                            source.name, QuestCompat.BakeShaderName));
                        return null;
                    }
                    unpackMat = new Material(bakeShader);
                    unpackPass = FindPassIndex(unpackMat, "UnpackNormal");
                    if (unpackPass < 0)
                    {
                        Debug.LogWarning(string.Format(
                            "[RARA QuestConverter] '{0}': ベイクシェーダーに UnpackNormal パスが見つからないため、ノーマルマップの縮小コピーをスキップしました。RARA_QuestBake.shader の更新・再インポートを確認してください。",
                            source.name));
                        return null;
                    }
                }

                // ノーマル・非sRGBはリニアRT+リニアTexture2Dで生値を保持し、
                // sRGBカラーは既定RT(リニアプロジェクトではsRGB)で色再現を維持する
                // (MaterialAtlasser.BlitToReadable / RunBlit と同じ色空間パターン)
                bool linear = isNormalMap || !sRGB;
                var previousRT = RenderTexture.active;
                RenderTexture rt = linear
                    ? RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    : RenderTexture.GetTemporary(width, height);
                if (unpackMat != null)
                {
                    Graphics.Blit(source, rt, unpackMat, unpackPass);
                }
                else
                {
                    Graphics.Blit(source, rt);
                }
                RenderTexture.active = rt;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply(false, false);
                RenderTexture.active = previousRT;
                RenderTexture.ReleaseTemporary(rt);
                readable.name = source.name + "_q" + targetSize;

                // 縮小済みの実寸をそのまま保てるよう、インポーターの最大サイズは実寸以上の2の累乗にする
                int importMaxSize = Mathf.NextPowerOfTwo(Mathf.Max(width, height));
                Texture2D saved = SaveTextureAsset(readable, assetPath, sRGB, isNormalMap, false, importMaxSize, androidFormat);
                if (saved == null)
                {
                    Debug.LogWarning(string.Format(
                        "[RARA QuestConverter] テクスチャ '{0}' の縮小コピーの保存に失敗しました: {1}", source.name, assetPath));
                }
                return saved;
            }
            finally
            {
                if (unpackMat != null) UnityEngine.Object.DestroyImmediate(unpackMat);
                if (readable != null && !AssetDatabase.Contains(readable)) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        // ================================================================
        // 内部ヘルパー
        // ================================================================

        /// <summary>シェーダーパス名からパス番号を返す(見つからなければ -1。MaterialAtlasser と同じ流儀)。</summary>
        private static int FindPassIndex(Material mat, string passName)
        {
            for (int i = 0; i < mat.passCount; i++)
            {
                if (string.Equals(mat.GetPassName(i), passName, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>
        /// ベイク出力サイズを計算する。アスペクト比を保ったまま長辺のみを maxSize へ収める
        /// (幅・高さを独立にクランプすると非正方形テクスチャが引き伸ばされ、
        /// テクスチャメモリが最大4倍に膨らむため。lilToon 純正ベイクと同様にアスペクト比を維持する)。
        /// </summary>
        private static void ComputeBakeSize(Texture source, int maxSize, out int width, out int height)
        {
            int sw = Mathf.Max(1, source.width);
            int sh = Mathf.Max(1, source.height);
            float scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(sw, sh));
            width = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(sh * scale));
        }

        /// <summary>float トグルプロパティが有効(>0.5)かどうか。</summary>
        private static bool IsOn(Material m, string propertyName)
        {
            return m.HasProperty(propertyName) && m.GetFloat(propertyName) > 0.5f;
        }

        private static void CopyFloat(Material src, Material dst, string prop)
        {
            if (src.HasProperty(prop)) dst.SetFloat(prop, src.GetFloat(prop));
        }

        private static void CopyColor(Material src, Material dst, string prop)
        {
            if (src.HasProperty(prop)) dst.SetColor(prop, src.GetColor(prop));
        }

        private static void CopyVector(Material src, Material dst, string prop)
        {
            if (src.HasProperty(prop)) dst.SetVector(prop, src.GetVector(prop));
        }

        /// <summary>
        /// ベイクのソースとして使うテクスチャを返す。PNG/JPG アセットの場合は
        /// ファイルから非圧縮・フル解像度で読み直す(lilTextureUtils.LoadTexture と同じ流儀。
        /// 圧縮劣化やインポーター側の縮小の影響を受けないため)。
        /// 読み直した一時テクスチャは loadedTemp へ返すので、呼び出し側で破棄すること。
        /// 読めない形式(psd 等)や失敗時は元のテクスチャをそのまま返す(GPU 経由の Blit は可能)。
        /// </summary>
        private static Texture LoadReadableSource(Texture source, out Texture2D loadedTemp)
        {
            loadedTemp = null;
            if (source == null) return null;

            string path = AssetDatabase.GetAssetPath(source);
            if (!string.IsNullOrEmpty(path) &&
                (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var loaded = new Texture2D(2, 2);
                    if (loaded.LoadImage(File.ReadAllBytes(Path.GetFullPath(path))))
                    {
                        loaded.filterMode = FilterMode.Bilinear;
                        loadedTemp = loaded;
                        return loaded;
                    }
                    UnityEngine.Object.DestroyImmediate(loaded);
                }
                catch (Exception)
                {
                    // 読み込み失敗時は元テクスチャへフォールバック(Blit は GPU 経由で可能)
                }
            }
            return source;
        }

        /// <summary>
        /// lilToonInspector.RunBake(lilEditorTextureBaker.cs)と同じパターンで
        /// マテリアルを通した Blit → ReadPixels を行い、読み取り可能な Texture2D を返す。
        /// 出力は sRGB / RGBA32 / ミップあり。RenderTexture は既定の ReadWrite 設定
        /// (リニアカラースペースでは sRGB)を使うことで色再現を lilToon 純正ベイクと一致させる。
        /// </summary>
        private static Texture2D RunBlit(Texture source, Material material, int width, int height)
        {
            var outTexture = new Texture2D(width, height);

            var bufRT = RenderTexture.active;
            var dstTexture = RenderTexture.GetTemporary(width, height);
            Graphics.Blit(source, dstTexture, material);
            RenderTexture.active = dstTexture;
            outTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            outTexture.Apply();
            RenderTexture.active = bufRT;
            RenderTexture.ReleaseTemporary(dstTexture);
            return outTexture;
        }

        /// <summary>
        /// RunBlit の特定パス版。material の pass 番目のパスだけを通して Blit → ReadPixels する
        /// (複数パスのベイクシェーダーで PoiyomiColorAdjust など目的のパスのみを実行するため)。
        /// 出力は sRGB / RGBA32 / ミップあり(色ベイク前提。RunBlit と同じ色空間の扱い)。
        /// </summary>
        private static Texture2D RunBlit(Texture source, Material material, int pass, int width, int height)
        {
            var outTexture = new Texture2D(width, height);

            var bufRT = RenderTexture.active;
            var dstTexture = RenderTexture.GetTemporary(width, height);
            Graphics.Blit(source, dstTexture, material, pass);
            RenderTexture.active = dstTexture;
            outTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            outTexture.Apply();
            RenderTexture.active = bufRT;
            RenderTexture.ReleaseTemporary(dstTexture);
            return outTexture;
        }

        /// <summary>単色テクスチャ(64x64 / sRGB / 読み取り可)を生成する。</summary>
        private static Texture2D CreateSolidTexture(Color color, string name)
        {
            color.r = Mathf.Clamp01(color.r);
            color.g = Mathf.Clamp01(color.g);
            color.b = Mathf.Clamp01(color.b);
            color.a = Mathf.Clamp01(color.a);

            var tex = new Texture2D(SolidTextureSize, SolidTextureSize, TextureFormat.RGBA32, true);
            tex.name = name;
            var pixels = new Color32[SolidTextureSize * SolidTextureSize];
            Color32 c32 = color;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = c32;
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }
    }
}
#endif
