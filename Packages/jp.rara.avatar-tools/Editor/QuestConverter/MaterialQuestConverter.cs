// RARA Quest Converter - マテリアル変換モジュール
// PC向けマテリアル(lilToon / Standard 等)を Quest(Android)対応シェーダーへ変換する。
// テクスチャのベイク処理は TextureBaker に委譲し、本クラスはシェーダー差し替えと
// プロパティのマッピングのみを担当する。
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace RARA.QuestConverter
{
    /// <summary>
    /// マテリアルの使われ方(どの種類のレンダラーが参照しているか)。
    /// パーティクル系レンダラー専用のマテリアルは不透明Toon Litではなく
    /// モバイルパーティクルシェーダーへ変換するために使う。
    /// </summary>
    public enum MaterialUsage
    {
        /// <summary>通常(メッシュ等)。従来どおりの変換。</summary>
        Default = 0,
        /// <summary>ParticleSystemRenderer / TrailRenderer / LineRenderer 専用。</summary>
        Particle = 1,
    }

    /// <summary>
    /// マテリアル1個を Quest 対応マテリアルへ変換する静的クラス。
    /// 元マテリアルは一切変更せず、常に新規アセットを生成して返す。
    /// 変換に失敗した場合は元マテリアルをそのまま返す(アバター全体の変換を中断しない)。
    /// </summary>
    public static class MaterialQuestConverter
    {
        // ---- Toon Standard のシェーダーキーワード(ToonStandard.shader / ToonStandardEditor.cs で検証済み) ----
        private const string KeywordNormalMaps = "USE_NORMAL_MAPS";
        private const string KeywordRimLight = "USE_RIMLIGHT";
        private const string KeywordMatcap = "USE_MATCAP"; // ToonStandardEditor.cs の LocalKeyword で検証済み

        // ---- モバイルパーティクルシェーダー名(Androidホワイトリスト対象のパーティクルは加算・乗算のみ) ----
        private const string ParticleAdditiveShaderName = "VRChat/Mobile/Particles/Additive";
        private const string ParticleMultiplyShaderName = "VRChat/Mobile/Particles/Multiply";

        // 半透明再現(Emulate)で加算/乗算を選ぶ輝度しきい値(名前判定で決まらなかった場合のフォールバック)。
        // ベイク済みメインテクスチャの可視テクセル(a>0.1)のアルファ加重平均輝度が
        // これ以上なら加算(明るい半透明:涙・ハイライト)、未満なら乗算(暗い半透明:頬染め・影・レンズ)。
        // しきい値は乗算寄り(高め)に設定する: 影を誤って加算にすると暗所で光る事故になるが、
        // ハイライトを誤って乗算にしても単に減光されるだけで事故が軽いため、乗算バイアスを掛ける。
        private const float EmulationAdditiveLuminanceThreshold = 0.75f;
        private const float EmulationVisibleAlphaThreshold = 0.1f;

        /// <summary>
        /// マテリアルを Quest 対応マテリアルへ変換して保存済みアセットを返す。
        /// ・null → null
        /// ・シェーダー欠落 → エラー報告して元マテリアルを返す(上書き指定でも修復不可)
        /// ・overrideMode(マテリアル別の変換方法指定)が Auto 以外なら自動判定より優先する
        /// ・既にモバイルシェーダー → 元マテリアルをそのまま返す(Hide指定のみ例外的に適用)
        /// ・lilToon系 → 設定(または上書き指定)に応じて Toon Standard / Toon Lit へ変換
        /// ・その他 → パーティクル近似 or メインテクスチャベイク+Toon Lit
        /// 透過(アルファブレンド)マテリアルの自動処理は settings.transparentHandling で決まる
        /// (Emulate=乗算/加算で再現・既定 / Hide=非表示化 / Opaque=不透明化)。
        /// suppressTransparentHide が true の場合、透過の再現・非表示化ブランチをスキップして全モードで
        /// 不透明として変換する(大型メッシュ・髪で使用される透過マテリアル用。透過→不透明化の警告は
        /// 従来どおり報告される)。手動指定の Hide は抑制されず常に非表示化する。
        /// assets は生成アセットの安定パス払い出し・書き込み記録用コンテキスト
        /// (オーケストレーターが1変換につき1つ渡す。null なら単体呼び出し用に内部生成する)。
        /// </summary>
        public static Material Convert(Material src, QuestConvertSettings settings, string outputDir, ConversionReport report, MaterialUsage usage = MaterialUsage.Default, MaterialOverride overrideMode = MaterialOverride.Auto, bool suppressTransparentHide = false, ConversionAssetContext assets = null)
        {
            // ---- (1) null ----
            if (src == null) return null;

            // ---- (2) シェーダー欠落・インポート失敗(Hidden/InternalErrorShader)----
            // 白ベイクで隠さず明示する。上書き指定でも壊れたシェーダーは修復できない。
            string srcShaderName = src.shader != null ? src.shader.name : string.Empty;
            if (src.shader == null || srcShaderName == "Hidden/InternalErrorShader")
            {
                report.Error(string.Format("マテリアル '{0}' のシェーダーが欠落しているか壊れています(Hidden/InternalErrorShader)。シェーダーのインポートを修正してから再変換してください。このマテリアルは変換せずそのまま残します。", src.name));
                return src;
            }

            // ---- (3) 上書き: 変換せずそのまま残す ----
            if (overrideMode == MaterialOverride.Keep)
            {
                report.Info(string.Format("『{0}』は設定により変換しません(非対応シェーダーのままだとアップロード時にSDK警告が出ます)", src.name));
                return src;
            }

            try
            {
                outputDir = NormalizeFolder(outputDir);
                if (assets == null) assets = new ConversionAssetContext(); // 単体呼び出し用(通常はオーケストレーターから渡される)

                // ---- (4) 上書き: 非表示化(既にモバイルシェーダーでも指定を尊重して非表示化する)----
                if (overrideMode == MaterialOverride.Hide)
                {
                    Material hidden = ConvertToHidden(src, outputDir, assets, report);
                    return hidden != null ? hidden : src;
                }

                // ---- (5) 既にQuest対応シェーダー → 変換不要 ----
                if (QuestCompat.IsMobileShader(src.shader))
                {
                    if (overrideMode != MaterialOverride.Auto)
                    {
                        report.Info(string.Format("『{0}』は既にQuest対応シェーダー({1})のため、変換方法の指定({2})は適用せずそのまま使用します。", src.name, src.shader.name, overrideMode));
                    }
                    else
                    {
                        report.Info(string.Format("マテリアル '{0}' は既にQuest対応シェーダー({1})です。変換をスキップします。", src.name, src.shader.name));
                    }
                    return src;
                }

                // ---- (6) TextMeshPro(SDFフォント)はベイク不可(アトラスを焼くと文字化けした板になる)。
                // 変換せずに残し、Quest除外を推奨する(クライアント側でフォールバック表示される)。
                if (srcShaderName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    srcShaderName.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    report.Warn(string.Format("マテリアル '{0}' ({1}) はTextMeshPro用のため変換できません(SDFアトラスをベイクすると文字が崩れます)。この文字オブジェクトはQuest除外(questExcludePaths)に追加することを推奨します。マテリアルは変換せずそのまま残します。", src.name, srcShaderName));
                    return src;
                }

                // ---- (6.5) 効果専用(ベース面なし)の補助lilToonシェーダーは再現不可のため常に非表示化 ----
                // 疑似影(FakeShadow / 前髪影)・アウトラインのみ(OutlineOnly)は、ベース面を持たない演出専用パス。
                // lilToon名を含むためシェーダー名末尾での透過判定では Opaque と誤分類され、そのまま近似ベイクすると
                // _Color(多くは near-white)の不透明面としてベイクされてしまう(白い髪バグ)。透過の扱い
                // (transparentHandling)や大型メッシュ・髪の透過保護(suppressTransparentHide)より優先し、
                // 常に不可視マテリアルへ差し替える(表情デカールの常時非表示と同じ扱い)。手動指定(Auto以外)は
                // ユーザーの明示選択のため尊重する(上の Keep/Hide/強制変換の各分岐で処理済み)。
                if (overrideMode == MaterialOverride.Auto)
                {
                    QuestCompat.OverlayOnlyShaderKind auxKind = QuestCompat.ClassifyOverlayOnlyShader(src.shader, src.name);
                    if (auxKind != QuestCompat.OverlayOnlyShaderKind.None)
                    {
                        Material hiddenAux = ConvertToHidden(src, outputDir, assets, report, OverlayOnlyHideReport(auxKind, src.name));
                        return hiddenAux != null ? hiddenAux : src;
                    }
                }

                Material result;
                if (overrideMode == MaterialOverride.ParticleAdditive || overrideMode == MaterialOverride.ParticleMultiply)
                {
                    // ---- (7) 上書き: モバイルパーティクルシェーダーへ強制変換 ----
                    if (overrideMode == MaterialOverride.ParticleMultiply &&
                        QuestCompat.ClassifyTransparency(src) == QuestCompat.TransparencyClass.Transparent)
                    {
                        report.Info(string.Format("『{0}』: 乗算(Multiply)は背景へ色を掛け合わせる描画のため、頬染め・影系など暗い色を重ねる透過表現の見た目に近い近似になります。", src.name));
                    }
                    string forcedShaderName = overrideMode == MaterialOverride.ParticleMultiply ? ParticleMultiplyShaderName : ParticleAdditiveShaderName;
                    result = ConvertParticle(src, settings, outputDir, assets, report, forcedShaderName);
                }
                else if (overrideMode == MaterialOverride.ToonStandard || overrideMode == MaterialOverride.ToonLit)
                {
                    // ---- (8) 上書き: Toon Standard / Toon Lit へ強制変換 ----
                    // 透過の扱い(transparentHandling)より優先される(透過でも不透明として変換する)。
                    // 透過→不透明化の警告は各変換内で従来どおり報告される。
                    QuestShaderTarget forcedTarget = overrideMode == MaterialOverride.ToonStandard ? QuestShaderTarget.ToonStandard : QuestShaderTarget.ToonLit;
                    if (QuestCompat.IsLilToonShader(src.shader))
                    {
                        result = ConvertLilToon(src, settings, outputDir, assets, report, forcedTarget);
                    }
                    else if (QuestCompat.IsNonToonShader(src.shader))
                    {
                        result = ConvertNonToon(src, settings, outputDir, assets, report, forcedTarget);
                    }
                    else if (QuestCompat.IsPoiyomiShader(src.shader))
                    {
                        result = ConvertPoiyomi(src, settings, outputDir, assets, report, forcedTarget);
                    }
                    else if (forcedTarget == QuestShaderTarget.ToonStandard)
                    {
                        // 非lilToon→Toon Standard は簡易パス(ベイク済みテクスチャ+フラットランプ)
                        result = ConvertGenericToToonStandard(src, settings, outputDir, assets, report);
                    }
                    else
                    {
                        // 強制Toon Lit: パーティクル系シェーダー名でも自動差し替えせずToon Litへベイクする
                        result = ConvertGeneric(src, settings, outputDir, assets, report, true);
                    }
                }
                // ---- (9) 自動判定(従来どおりの変換)----
                else if (usage == MaterialUsage.Particle)
                {
                    // パーティクル系レンダラー専用マテリアルは不透明ベイクせず、
                    // モバイルパーティクルシェーダーへ差し替える(加算/乗算のみホワイトリスト対象)
                    result = ConvertParticle(src, settings, outputDir, assets, report);
                }
                else
                {
                    // 透過(アルファブレンド)マテリアルの扱いは settings.transparentHandling で決まる。
                    //   Emulate: 乗算/加算のパーティクルシェーダーで半透明を再現(既定)
                    //   Hide:    不可視マテリアルへ差し替えて非表示化
                    //   Opaque:  不透明として変換(以降の通常パス。透過は失われる)
                    // 大型メッシュ・髪の保護(suppressTransparentHide)は全モードで不透明変換を強制する
                    // (乗算/加算は Unlit のため髪には不向き。非表示化も髪が丸ごと消えるため不可)。
                    bool isTransparent = QuestCompat.ClassifyTransparency(src) == QuestCompat.TransparencyClass.Transparent;
                    bool emulateOrHideTransparent = isTransparent && !suppressTransparentHide;

                    if (emulateOrHideTransparent && settings.transparentHandling == TransparentHandling.Hide)
                    {
                        // カットアウトは不透明相当の描画のため対象外 = 従来どおり不透明変換
                        result = ConvertToHidden(src, outputDir, assets, report);
                    }
                    else if (emulateOrHideTransparent && settings.transparentHandling == TransparentHandling.Emulate)
                    {
                        result = ConvertTransparentEmulated(src, settings, outputDir, assets, report);
                    }
                    else
                    {
                        // 不透明として変換する場合(不透明マテリアル / Opaqueモードの透過 / 保護された髪の透過)
                        if (isTransparent && suppressTransparentHide)
                        {
                            report.Info(string.Format("『{0}』は大型メッシュ/髪で使用されている透過マテリアルのため乗算/加算での再現や非表示化は行わず、不透明として変換します(透過保護)。", src.name));
                        }
                        if (QuestCompat.IsLilToonShader(src.shader))
                        {
                            result = ConvertLilToon(src, settings, outputDir, assets, report, settings.shaderTarget);
                        }
                        else if (QuestCompat.IsNonToonShader(src.shader))
                        {
                            result = ConvertNonToon(src, settings, outputDir, assets, report, settings.shaderTarget);
                        }
                        else if (QuestCompat.IsPoiyomiShader(src.shader))
                        {
                            result = ConvertPoiyomi(src, settings, outputDir, assets, report, settings.shaderTarget);
                        }
                        else
                        {
                            result = ConvertGeneric(src, settings, outputDir, assets, report);
                        }
                    }
                }
                // 内部でエラー報告済み(null)の場合は元マテリアルを維持
                return result != null ? result : src;
            }
            catch (Exception ex)
            {
                report.Error(string.Format("マテリアル '{0}' の変換中に例外が発生しました。元のマテリアルを維持します: {1}", src.name, ex.Message));
                return src;
            }
        }

        // ================================================================
        // lilToon 系
        // ================================================================

        /// <summary>
        /// lilToon マテリアルを target(Toon Standard / Toon Lit)へ変換する。
        /// 自動判定時は settings.shaderTarget、マテリアル別の上書き指定時は強制ターゲットが渡される。
        /// </summary>
        private static Material ConvertLilToon(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report, QuestShaderTarget target)
        {
            string shaderName = src.shader != null ? src.shader.name : string.Empty;

            // 透明・カットアウト判定(シェーダー名 or lilToonMulti の _TransparentMode)
            bool isTransparent =
                shaderName.IndexOf("trans", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (src.HasProperty("_TransparentMode") && Mathf.RoundToInt(src.GetFloat("_TransparentMode")) != 0);
            if (isTransparent)
            {
                report.Warn(string.Format("Quest不透明化: '{0}' は透明/カットアウト設定ですが、Toon Standard/Toon Litは透明非対応のため不透明として変換します。", src.name));
            }

            // アウトライン判定(lts_o系 → シェーダー名に Outline を含む)
            bool hasOutline = shaderName.IndexOf("outline", StringComparison.OrdinalIgnoreCase) >= 0;

            // メインカラー(色調補正・2nd/3rdレイヤー)を合成したアルベドをベイク
            // (元メインテクスチャに縮小計画があれば、ベイク解像度も計画値以下に抑える)
            Texture2D baked = TextureBaker.BakeLilToonMain(src, GetBakeMaxSize(src, settings, report), report);

            Material result;
            if (target == QuestShaderTarget.ToonStandard)
            {
                result = ConvertLilToonToToonStandard(src, baked, settings, outputDir, assets, report);
            }
            else
            {
                result = ConvertLilToonToToonLit(src, baked, settings, outputDir, assets, report);
            }

            if (result != null)
            {
                WarnUnsupportedLilToonFeatures(src, shaderName, hasOutline, report);
            }
            return result;
        }

        /// <summary>lilToon → VRChat/Mobile/Toon Standard 変換。</summary>
        private static Material ConvertLilToonToToonStandard(Material src, Texture2D baked, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            Shader shader = Shader.Find(QuestCompat.ToonStandardShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonStandardShaderName));
                return null;
            }

            var mat = new Material(shader);

            // ---- メインテクスチャ(ベイク結果を保存して割り当て。ST はベイクに含まれないため元からコピー) ----
            Texture2D mainTex = PersistBakedOrFallback(baked, src, "_main", true, settings, outputDir, assets, report);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, "_MainTex", mat, "_MainTex");
            mat.SetColor("_Color", Color.white); // 色調はベイク済みのため白

            // ---- カリング(lilToon _Cull と Toon Standard _Culling は同じ値定義: Off=0/Front=1/Back=2) ----
            if (src.HasProperty("_Cull"))
            {
                mat.SetInt("_Culling", Mathf.Clamp(Mathf.RoundToInt(src.GetFloat("_Cull")), 0, 2));
            }

            // ---- 影ランプ ----
            bool sourceHasShadow = IsFeatureOn(src, "_UseShadow");
            Texture2D rampAsset = null;
            if (settings.generateShadowRamp && sourceHasShadow)
            {
                Texture2D ramp = TextureBaker.GenerateShadowRamp(src);
                if (ramp != null)
                {
                    // ランプはリニア(sRGB=false)で保存する。Toon Standard はランプ texel を
                    // 輝度乗算値として直接サンプリングするため(SDK同梱ランプも sRGBTexture:0)。
                    // sRGB で保存するとリニアプロジェクトでデコードが掛かり、影が暗く・境界がずれる。
                    rampAsset = PersistTexture(ramp, src.name, "_ramp", false, settings, outputDir, assets, report);
                }
            }
            if (rampAsset != null)
            {
                mat.SetTexture("_Ramp", rampAsset);
                // 生成ランプは「影 = アルベド × 影色」を表す色付きランプのため _ShadowAlbedo は 0 にする。
                // Toon Standard の既定値 0.5 のままだと Lighting.cginc の
                // lerp(albedo * _ShadowAlbedo, 1, ramp) により影がアルベド寄りに明るく色付き、
                // ベイクした lilToon の _ShadowColor の色味が薄まってしまう。
                // (SDK同梱のグレースケールランプは _ShadowAlbedo = 0.5 前提のため、
                //  下のフォールバック側は既定値のまま変更しない)
                mat.SetFloat("_ShadowAlbedo", 0f);
            }
            else
            {
                // 影無効(_UseShadow オフ)のマテリアルにはフラットランプを割り当て、
                // PC版と同じ均一なライティングを維持する(2バンド既定ランプだと無かった陰影が付いてしまう)
                string rampResource = sourceHasShadow ? QuestCompat.DefaultShadowRampResource : QuestCompat.FlatShadowRampResource;
                Texture2D defaultRamp = Resources.Load<Texture2D>(rampResource);
                if (defaultRamp != null)
                {
                    mat.SetTexture("_Ramp", defaultRamp);
                    report.Info(string.Format("'{0}': 影ランプはSDK既定({1})を使用します。", src.name, rampResource));
                }
                else
                {
                    report.Warn(string.Format("'{0}': SDK既定の影ランプ({1})が読み込めませんでした。影は白ランプ(フラット)になります。", src.name, rampResource));
                }
            }

            // ---- ライト強度の下限・上限(lilToon: lightColor = clamp(lightColor, _LightMinLimit, _LightMaxLimit)) ----
            // 下限: Toon Standard の _MinBrightness(0〜0.1、既定0)へ対応付ける。
            // 未設定(0)のままだと暗いワールドで真っ黒になり、PC版(lilToon既定0.05の明るさ床)と
            // 違って「アバターが消えた」ように見えるため必ず書き込む。
            float lightMin = src.HasProperty("_LightMinLimit") ? src.GetFloat("_LightMinLimit") : 0.05f;
            mat.SetFloat("_MinBrightness", Mathf.Clamp(lightMin, 0f, 0.1f));
            if (lightMin > 0.1f)
            {
                report.Info(string.Format("'{0}': lilToonのライト下限({1:F2})がToon Standardの上限(0.1)を超えるためクランプしました。暗所でPC版より暗くなります。", src.name, lightMin));
            }

            // 上限: lilToon の _LightMaxLimit > 1(明るいワールドでの白飛び許容)は、
            // Toon Standard 既定の明るさ制限(_LimitBrightness = 1でライト強度を1へクランプ)を
            // 無効化して近似する(クランプしたままだとPC版より一様に暗く・平坦に見える)。
            float lightMax = src.HasProperty("_LightMaxLimit") ? src.GetFloat("_LightMaxLimit") : 1f;
            if (lightMax > 1.01f)
            {
                mat.SetFloat("_LimitBrightness", 0f);
                report.Info(string.Format("'{0}': lilToonのライト上限({1:F1})が1を超えるため、Toon Standardの明るさ制限(Limit Brightness)を無効化しました(過剰照明のワールドでPC版と同様に明るく描画されます)。", src.name, lightMax));
            }

            // ---- ノーマルマップ(再ベイクせず元アセットを流用。縮小計画があれば元を触らず縮小コピーを生成) ----
            if (IsFeatureOn(src, "_UseBumpMap") && src.HasProperty("_BumpMap"))
            {
                Texture bump = src.GetTexture("_BumpMap");
                if (bump != null)
                {
                    mat.SetTexture("_BumpMap", ResolveReusedTexture(bump, src.name, "ノーマルマップ", true, false, true, settings, outputDir, assets, report));
                    CopyTextureST(src, "_BumpMap", mat, "_BumpMap");
                    if (src.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", src.GetFloat("_BumpScale"));
                    mat.EnableKeyword(KeywordNormalMaps);
                }
            }

            // ---- エミッション ----
            // 注意: _EmissionMap が未設定の場合はエミッションなしとして扱う。
            // Toon Standard の _EmissionMap 既定値は「白」のため、マップなしで色だけ引き継ぐと
            // メッシュ全体が真っ白に発光する(lilToon側で実質見えていない設定でも顕在化する)。
            // アトラス統合側(MaterialAtlasser.HasActiveEmission)と同じ判定規則。
            Texture emissionMap = null;
            if (settings.bakeEmission && IsFeatureOn(src, "_UseEmission"))
            {
                emissionMap = src.HasProperty("_EmissionMap") ? src.GetTexture("_EmissionMap") : null;
            }
            if (emissionMap != null)
            {
                // 縮小計画があれば元テクスチャを触らず縮小コピーを生成して割り当てる
                mat.SetTexture("_EmissionMap", ResolveReusedTexture(emissionMap, src.name, "エミッションマップ", false, true, false, settings, outputDir, assets, report));
                CopyTextureST(src, "_EmissionMap", mat, "_EmissionMap");

                // 縮小計画が無いまま大きなエミッションマップ(顔用の4K等)がAndroid上書きで
                // そのまま引き継がれると、ダウンロードサイズを圧迫する。縮小計画への追加を促す
                // (自動縮小はしない。元テクスチャは変更せず、変換時に縮小コピーを生成する縮小計画を推奨するだけ)。
                // Android上書きが無いテクスチャは ResolveReusedTexture が同趣旨の警告を出すため、ここでは二重に出さない。
                string emissionPath = AssetDatabase.GetAssetPath(emissionMap);
                if (!string.IsNullOrEmpty(emissionPath) &&
                    !TryGetPlannedSize(settings, emissionMap, out _) &&
                    HasAndroidTextureOverride(emissionMap))
                {
                    int emissionEffectiveSize = GetEffectiveAndroidSize(emissionMap);
                    if (emissionEffectiveSize > Mathf.Max(1, settings.maxTextureSize))
                    {
                        report.Info(string.Format("'{0}': エミッションマップ '{1}' はAndroid実効サイズが約{2}pxと大きいまま引き継がれます(縮小計画なし)。ダウンロードサイズ削減にはサイズ見積もりの縮小計画への追加を推奨します(元テクスチャは変更せず、変換時に縮小コピーを生成します)。", src.name, emissionMap.name, emissionEffectiveSize));
                    }
                }

                // HDRエミッションの強度(>1 成分)は _EmissionStrength(0〜2)へ折り込み、色はLDRへ正規化する
                Color emissionColor = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white;
                float hdrIntensity = Mathf.Max(emissionColor.r, Mathf.Max(emissionColor.g, emissionColor.b));
                if (hdrIntensity > 1f)
                {
                    emissionColor.r /= hdrIntensity;
                    emissionColor.g /= hdrIntensity;
                    emissionColor.b /= hdrIntensity;
                }
                else
                {
                    hdrIntensity = 1f;
                }
                emissionColor.a = 1f;
                mat.SetColor("_EmissionColor", emissionColor);

                float blend = src.HasProperty("_EmissionBlend") ? src.GetFloat("_EmissionBlend") : 1f;
                float strength = blend * hdrIntensity;
                mat.SetFloat("_EmissionStrength", Mathf.Clamp(strength, 0f, 2f));
                if (strength > 2f)
                {
                    report.Info(string.Format("'{0}': HDRエミッション強度({1:F1})がToon Standardの上限(2)を超えるためクランプしました。発光がPC版より弱くなります。", src.name, strength));
                }

                mat.SetInt("_EmissionUV", 0);
                if (src.HasProperty("_EmissionMap_UVMode") && Mathf.RoundToInt(src.GetFloat("_EmissionMap_UVMode")) != 0)
                {
                    report.Warn(string.Format("'{0}': エミッションのUVモードがUV0以外です。UV0として変換したため見た目が変わる可能性があります。", src.name));
                }
            }
            else
            {
                mat.SetColor("_EmissionColor", Color.black);
                mat.SetFloat("_EmissionStrength", 0f);
                if (settings.bakeEmission && IsFeatureOn(src, "_UseEmission"))
                {
                    report.Info(string.Format("'{0}': lilToonのエミッションが有効ですが_EmissionMapが未設定のため、エミッションなしとして変換しました(単色発光を使いたい場合は生成マテリアルのEmissionを手動設定してください)。", src.name));
                }
            }

            // ---- リムライト ----
            ConvertRimLighting(src, mat, settings, report);

            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>
        /// lilToon のリムライトを Toon Standard のリムへ変換する(mat は Toon Standard マテリアル)。
        /// settings.mapRimLighting がオフ(既定)の場合はリムを明示的に無効化する。
        /// 【重要】Toon Standard の _RimIntensity の既定値は 0.5(ToonStandard.shader の Properties で
        /// 検証済み)のため、未設定のまま放置してはいけない。リムの USE_RIMLIGHT は
        /// dynamic_branch キーワードでキーワード無効なら描画されないが、アトラス統合
        /// (先頭メンバーからの Material コピー)や後からのマテリアルGUI操作でキーワードが
        /// 有効化された途端に「無かったはずのリム」が光り出すため、変換しない場合も常に 0 を書き込む。
        /// </summary>
        private static void ConvertRimLighting(Material src, Material mat, QuestConvertSettings settings, ConversionReport report)
        {
            bool sourceHasRim = IsFeatureOn(src, "_UseRim");

            if (!settings.mapRimLighting || !sourceHasRim)
            {
                mat.SetFloat("_RimIntensity", 0f);
                if (sourceHasRim)
                {
                    report.Info(string.Format("『{0}』: lilToonのリムライトは既定で無効化しました(必要なら設定「リムライトを近似変換」をオン、または生成マテリアルで手動調整)", src.name));
                }
                return;
            }

            // ---- 近似変換(mapRimLighting オン) ----
            // 強度・色: lilToon はリム色のアルファが実質的な強度として働く(加算ブレンドの係数)
            Color rim = src.HasProperty("_RimColor") ? src.GetColor("_RimColor") : Color.white;
            mat.SetColor("_RimColor", new Color(rim.r, rim.g, rim.b, 1f));
            mat.SetFloat("_RimIntensity", Mathf.Clamp01(rim.a));

            // 形状パラメータの対応(両シェーダーのソースを読んで検証済み):
            // ・lilToon (lil_common_frag.hlsl の lilGetRim + lil_common_functions.hlsl の lilTooningScale):
            //     fresnel = pow(saturate(1 - |N・V|), _RimFresnelPower)
            //     rim = saturate((fresnel - (_RimBorder - _RimBlur/2)) / _RimBlur)
            //   → _RimBorder が大きいほど「しきい値が高い = リムが細い」。_RimBlur は境界の全幅(大きいほど柔らかい)。
            // ・Toon Standard (Helpers.cginc の GetRimLight + VertexFragment.cginc):
            //     raw = saturate(1 - |V・N|)
            //     rim = smoothstep((1 - _RimRange) - _RimSharpness, (1 - _RimRange) + _RimSharpness, raw)
            //   → プロパティ取得時に rimRange = 1 - _RimRange と反転されるため、
            //     _RimRange が大きいほど「しきい値が低い = リムが太い」。
            //     _RimSharpness(表示名 "Softness")は smoothstep の半幅で、大きいほど柔らかい。
            // しきい値の対応: (1 - N・V) 空間での lilToon のしきい値は
            //   fresnel^power >= border ⇔ (1 - N・V) >= border^(1/power)
            // より border^(1/_RimFresnelPower)。よって
            //   _RimRange = 1 - border^(1/power)(power = 1 なら指示どおり 1 - _RimBorder に一致)。
            float border = src.HasProperty("_RimBorder") ? Mathf.Clamp01(src.GetFloat("_RimBorder")) : 0.5f;
            float fresnelPower = src.HasProperty("_RimFresnelPower") ? Mathf.Max(src.GetFloat("_RimFresnelPower"), 0.001f) : 3.5f;
            float threshold = Mathf.Pow(border, 1f / fresnelPower);
            mat.SetFloat("_RimRange", Mathf.Clamp01(1f - threshold));

            // 境界の柔らかさ: lilToon の _RimBlur(全幅)↔ Toon Standard の _RimSharpness(半幅)。
            // どちらも「大きいほど柔らかい」で方向は同じ(1 - _RimBlur と反転してはいけない)ため、
            // 全幅 = 2×半幅 から _RimSharpness = _RimBlur / 2。
            float blur = src.HasProperty("_RimBlur") ? Mathf.Clamp01(src.GetFloat("_RimBlur")) : 0.65f;
            mat.SetFloat("_RimSharpness", Mathf.Clamp01(blur * 0.5f));

            // リムはアルベドで色付くほうが肌などで自然(白飛びした「謎の光」に見えにくい)ため
            // アルベドティントを最大にし、環境光による強度スケールは無効にする
            mat.SetFloat("_RimAlbedoTint", 1f);
            mat.SetFloat("_RimEnvironmental", 0f);
            mat.EnableKeyword(KeywordRimLight);
            report.Info(string.Format("'{0}': リムライトを近似変換しました(シェーダーの挙動差により見た目は完全一致しません。気になる場合は設定「リムライトを近似変換」をオフにしてください)。", src.name));
        }

        /// <summary>lilToon → VRChat/Mobile/Toon Lit 変換(影・エミッションはメインテクスチャへ合成)。</summary>
        private static Material ConvertLilToonToToonLit(Material src, Texture2D baked, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            Shader shader = Shader.Find(QuestCompat.ToonLitShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonLitShaderName));
                return null;
            }

            // 影の乗算・エミッションの加算を合成
            Texture2D composited = null;
            if (baked != null)
            {
                composited = TextureBaker.Composite(baked, src, settings.bakeShadowIntoMainTex, settings.bakeEmission, report);
            }
            Texture2D finalTex = composited != null ? composited : baked;

            // 合成で新しいテクスチャが生成された場合、中間のベイク結果を破棄(アセット化されたものは除く)
            if (composited != null && !ReferenceEquals(composited, baked) && baked != null && !AssetDatabase.Contains(baked))
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }

            var mat = new Material(shader);

            // Toon Lit のプロパティは _MainTex のみ(VRChat-Mobile-ToonLit.shader で検証済み。_Color は存在しない)
            Texture2D mainTex = PersistBakedOrFallback(finalTex, src, "_main", true, settings, outputDir, assets, report);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, "_MainTex", mat, "_MainTex");

            // Toon Lit はカリング指定なし(Back固定)のため両面描画は失われる
            if (src.HasProperty("_Cull") && Mathf.RoundToInt(src.GetFloat("_Cull")) == 0)
            {
                report.Warn(string.Format("'{0}': 両面描画(Cull Off)はToon Litでは再現されません(背面カリング固定)。", src.name));
            }

            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>変換先で失われる lilToon 機能を警告として明示する。</summary>
        private static void WarnUnsupportedLilToonFeatures(Material src, string shaderName, bool hasOutline, ConversionReport report)
        {
            if (hasOutline)
            {
                report.Warn(string.Format("'{0}': アウトラインは失われます(Toon Standard (Outline)はPC専用)。", src.name));
            }
            if (IsFeatureOn(src, "_UseMatCap"))
            {
                report.Warn(string.Format("'{0}': マットキャップは変換されません。", src.name));
            }
            if (IsFeatureOn(src, "_UseGlitter"))
            {
                report.Warn(string.Format("'{0}': ラメ(グリッター)は変換されません。", src.name));
            }
            if (IsFeatureOn(src, "_UseEmission2nd"))
            {
                report.Warn(string.Format("'{0}': 2ndエミッションは変換されません。", src.name));
            }
            if (IsFeatureOn(src, "_UseAudioLink"))
            {
                report.Warn(string.Format("'{0}': AudioLink演出は変換されません。", src.name));
            }
            if (shaderName.IndexOf("fur", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                report.Warn(string.Format("'{0}': ファー表現は変換されません(ベースメッシュのみ描画されます)。", src.name));
            }
            if (shaderName.IndexOf("gem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shaderName.IndexOf("ref", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                report.Warn(string.Format("'{0}': 宝石・屈折表現は変換されません。", src.name));
            }
        }

        // ================================================================
        // NonToon(jp.lilxyzw.nontoon)系
        // ================================================================

        /// <summary>
        /// NonToon / NonToonFur マテリアルを target(Toon Standard / Toon Lit)へ変換する。
        /// NonToon はメインティント色を持たず _BaseTexture がそのままアルベドのため色調ベイクは行わない。
        /// 影は _SharedGradients のグラデーション配列スライスからランプを生成し、エミッションは存在しないため常にOFF。
        /// ファー・アウトライン・バックライト・Specular 等の追加表現は再現しない(警告で明示する)。
        /// 半透明(_RenderingMode=2)は Convert 側で Emulate/Hide/不透明化に振り分け済みのため通常ここには来ないが、
        /// 透過保護された髪・大型メッシュや強制指定でここに来た場合は不透明化警告を出す。
        /// </summary>
        private static Material ConvertNonToon(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report, QuestShaderTarget target)
        {
            if (QuestCompat.IsNonToonFurShader(src.shader))
            {
                report.Warn(string.Format("NonToon: 『{0}』のファーは再現できないためベース質感のみ変換します。", src.name));
            }

            // 描画モード(_RenderingMode は Integer 型 → GetIntegerSafe。0=不透明 / 1=カットアウト / 2=半透明)
            int renderingMode = QuestCompat.GetIntegerSafe(src, QuestCompat.NonToonRenderingModeProp, 0);
            if (renderingMode == 2)
            {
                report.Warn(string.Format("NonToon: 『{0}』は半透明設定ですが、Toon Standard/Toon Litは半透明非対応のため不透明として変換します。", src.name));
            }
            else if (renderingMode == 1)
            {
                report.Warn(string.Format("NonToon: 『{0}』はカットアウト設定ですが、Toon Standard/Toon Litは不透明のため塗りつぶして変換します(透過部が板状に見える可能性があります)。", src.name));
            }

            Material result = target == QuestShaderTarget.ToonStandard
                ? ConvertNonToonToToonStandard(src, settings, outputDir, assets, report)
                : ConvertNonToonToToonLit(src, settings, outputDir, assets, report);

            if (result != null)
            {
                WarnUnsupportedNonToonFeatures(src, report);
            }
            return result;
        }

        /// <summary>NonToon → VRChat/Mobile/Toon Standard 変換。</summary>
        private static Material ConvertNonToonToToonStandard(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            Shader shader = Shader.Find(QuestCompat.ToonStandardShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonStandardShaderName));
                return null;
            }

            var mat = new Material(shader);

            // ---- メインテクスチャ = _BaseTexture(色調ベイクなし。NonToonにメインティント色は無い) ----
            // NonToonはメインの _ST を適用しない([NoScaleOffset])ため、Toon Standard側にもST転写しない。
            Texture baseTex = src.HasProperty(QuestCompat.NonToonBaseTextureProp) ? src.GetTexture(QuestCompat.NonToonBaseTextureProp) : src.mainTexture;
            if (baseTex != null)
            {
                mat.SetTexture("_MainTex", ResolveReusedTexture(baseTex, src.name, "メインテクスチャ", false, true, true, settings, outputDir, assets, report));
            }
            else
            {
                report.Warn(string.Format("NonToon: 『{0}』はメインテクスチャ(_BaseTexture)が未設定のため白テクスチャで変換します。", src.name));
            }
            mat.SetColor("_Color", Color.white); // NonToonにメインカラーティントは無いため白

            // ---- カリング(_Cull は legacy Int → GetFloat。Off=0/Front=1/Back=2 で Toon Standard _Culling と同定義) ----
            if (src.HasProperty(QuestCompat.NonToonCullProp))
            {
                mat.SetInt("_Culling", Mathf.Clamp(Mathf.RoundToInt(src.GetFloat(QuestCompat.NonToonCullProp)), 0, 2));
            }

            // ---- ノーマルマップ(再ベイクせず元アセットを流用。縮小計画があれば縮小コピーを生成) ----
            if (src.HasProperty(QuestCompat.NonToonNormalMapProp))
            {
                Texture bump = src.GetTexture(QuestCompat.NonToonNormalMapProp);
                if (bump != null)
                {
                    mat.SetTexture("_BumpMap", ResolveReusedTexture(bump, src.name, "ノーマルマップ", true, false, true, settings, outputDir, assets, report));
                    if (src.HasProperty(QuestCompat.NonToonNormalScaleProp)) mat.SetFloat("_BumpScale", src.GetFloat(QuestCompat.NonToonNormalScaleProp));
                    mat.EnableKeyword(KeywordNormalMaps);
                }
            }

            // ---- 影ランプ(グラデーション配列スライスから生成) ----
            ConvertNonToonShadowRamp(src, mat, settings, outputDir, assets, report);

            // ---- ライト下限: NonToonに相当プロパティは無いが、暗所で真っ黒になり「消えた」ように
            //      見えるのを防ぐため、lilToon変換と同じくToon Standardの明るさ床(0.05)を書き込む ----
            mat.SetFloat("_MinBrightness", 0.05f);

            // ---- エミッション: NonToonにはエミッションが無いため常にOFF(_EmissionMap既定白による白発光バグ防止) ----
            mat.SetColor("_EmissionColor", Color.black);
            mat.SetFloat("_EmissionStrength", 0f);

            // ---- マットキャップ ----
            ConvertNonToonMatCap(src, mat, settings, outputDir, assets, report);

            // ---- リムライト ----
            ConvertNonToonRimLight(src, mat, settings, report);

            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>NonToon → VRChat/Mobile/Toon Lit 変換(メインテクスチャのみ。陰影・質感は失われる)。</summary>
        private static Material ConvertNonToonToToonLit(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            Shader shader = Shader.Find(QuestCompat.ToonLitShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonLitShaderName));
                return null;
            }

            var mat = new Material(shader);

            Texture baseTex = src.HasProperty(QuestCompat.NonToonBaseTextureProp) ? src.GetTexture(QuestCompat.NonToonBaseTextureProp) : src.mainTexture;
            if (baseTex != null)
            {
                mat.SetTexture("_MainTex", ResolveReusedTexture(baseTex, src.name, "メインテクスチャ", false, true, true, settings, outputDir, assets, report));
            }
            else
            {
                report.Warn(string.Format("NonToon: 『{0}』はメインテクスチャ(_BaseTexture)が未設定のため白テクスチャで変換します。", src.name));
            }

            // Toon Lit はカリング指定なし(Back固定)のため両面描画は失われる
            if (src.HasProperty(QuestCompat.NonToonCullProp) && Mathf.RoundToInt(src.GetFloat(QuestCompat.NonToonCullProp)) == 0)
            {
                report.Warn(string.Format("NonToon: 『{0}』の両面描画(Cull Off)はToon Litでは再現されません(背面カリング固定)。", src.name));
            }

            report.Info(string.Format("NonToon: 『{0}』をToon Lit(メインテクスチャのみ)へ変換しました。陰影・マットキャップ・リム等は反映されません。", src.name));
            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>
        /// NonToon の影(_SharedGradients のグラデーション配列スライス)を Toon Standard の _Ramp へ変換する。
        /// settings.generateShadowRamp がオンで _ShadeGradientIndex >= 0 のときのみグラデーションからランプを焼き、
        /// 解決失敗・影無効時はSDK既定ランプへフォールバックする(中断はしない)。
        /// </summary>
        private static void ConvertNonToonShadowRamp(Material src, Material mat, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            int gradientIndex = QuestCompat.GetIntegerSafe(src, QuestCompat.NonToonShadeGradientIndexProp, -1);
            Texture2D rampAsset = null;
            if (settings.generateShadowRamp && gradientIndex >= 0)
            {
                Texture2D ramp = TextureBaker.BakeNonToonRamp(src, report);
                if (ramp != null)
                {
                    // ランプはリニア(sRGB=false)で保存する(lilToonランプ経路と同じ規則。
                    // Toon Standard はランプ texel を輝度乗算値として直接サンプリングするため)。
                    rampAsset = PersistTexture(ramp, src.name, "_ramp", false, settings, outputDir, assets, report);
                }
            }

            if (rampAsset != null)
            {
                mat.SetTexture("_Ramp", rampAsset);
                // 生成ランプは「影 = アルベド × グラデーション色」を表す色付きランプのため _ShadowAlbedo は 0 にする
                // (既定0.5のままだと影がアルベド寄りに明るくなり、グラデーションの影色が薄まる)。
                mat.SetFloat("_ShadowAlbedo", 0f);
            }
            else
            {
                // グラデーション未割り当て/インデックス無効/解決失敗 → SDK既定ランプ。
                // _ShadeGradientIndex >= 0 だが解決できなかった場合は2バンド既定、影無効ならフラット。
                string rampResource = gradientIndex >= 0 ? QuestCompat.DefaultShadowRampResource : QuestCompat.FlatShadowRampResource;
                Texture2D defaultRamp = Resources.Load<Texture2D>(rampResource);
                if (defaultRamp != null)
                {
                    mat.SetTexture("_Ramp", defaultRamp);
                    report.Info(string.Format("NonToon: 『{0}』の影ランプはSDK既定({1})を使用します。", src.name, rampResource));
                }
                else
                {
                    report.Warn(string.Format("NonToon: 『{0}』のSDK既定影ランプ({1})が読み込めませんでした。影は白ランプ(フラット)になります。", src.name, rampResource));
                }
            }
        }

        /// <summary>
        /// NonToon のマットキャップ(乗算/加算の2枠)を Toon Standard の1枠マットキャップへ近似変換する。
        /// MatCaps モジュールが無効(_Enable=0)なら何もしない(Toon Standard の USE_MATCAP は既定オフ)。
        /// 乗算枠を優先して Multiplicative、無ければ加算枠を Additive にマップし、両方あれば加算枠は非対応として警告する。
        /// </summary>
        private static void ConvertNonToonMatCap(Material src, Material mat, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            if (QuestCompat.GetIntegerSafe(src, QuestCompat.NonToonMatCapEnableProp, 0) == 0) return;

            Texture multiplyTex = src.HasProperty(QuestCompat.NonToonMatCapMultiplyProp) ? src.GetTexture(QuestCompat.NonToonMatCapMultiplyProp) : null;
            Texture addTex = src.HasProperty(QuestCompat.NonToonMatCapAddProp) ? src.GetTexture(QuestCompat.NonToonMatCapAddProp) : null;

            Texture matcapTex;
            int matcapType; // Toon Standard _MatcapType: 0=Additive / 1=Multiplicative
            Color matcapColor;
            if (multiplyTex != null)
            {
                matcapTex = multiplyTex;
                matcapType = 1;
                matcapColor = src.HasProperty(QuestCompat.NonToonMatCapMultiplyColorProp) ? src.GetColor(QuestCompat.NonToonMatCapMultiplyColorProp) : Color.white;
                if (addTex != null)
                {
                    report.Warn(string.Format("NonToon: 『{0}』のマットキャップは乗算枠のみ変換します(Toon Standardは1枠のみのため加算枠=2枠目は非対応)。", src.name));
                }
            }
            else if (addTex != null)
            {
                matcapTex = addTex;
                matcapType = 0;
                matcapColor = src.HasProperty(QuestCompat.NonToonMatCapAddColorProp) ? src.GetColor(QuestCompat.NonToonMatCapAddColorProp) : Color.white;
            }
            else
            {
                return; // 有効だがテクスチャ未設定 → マットキャップ無し
            }

            mat.SetTexture("_Matcap", ResolveReusedTexture(matcapTex, src.name, "マットキャップ", false, true, true, settings, outputDir, assets, report));
            mat.SetInt("_MatcapType", matcapType);
            // NonToonのマットキャップには単一の強度スカラーが無いため、色の明度を強度(0..1)へ寄せる。
            float strength = Mathf.Clamp01(Mathf.Max(matcapColor.r, Mathf.Max(matcapColor.g, matcapColor.b)));
            mat.SetFloat("_MatcapStrength", strength);
            mat.EnableKeyword(KeywordMatcap);
            report.Info(string.Format("NonToon: 『{0}』のマットキャップ({1})を近似変換しました(マスク・ディテール法線・色味は完全再現されません)。", src.name, matcapType == 1 ? "乗算" : "加算"));
        }

        /// <summary>
        /// NonToon のリムライトを Toon Standard のリムへ近似変換する。
        /// settings.mapRimLighting がオフ(既定)、またはリム色が黒(=NonToonでは実質OFF)の場合は
        /// リムを明示的に無効化する(Toon Standard の _RimIntensity 既定値 0.5 を放置すると、
        /// アトラス統合や後からのキーワード有効化で「無かったはずのリム」が光り出すため必ず 0 を書き込む)。
        /// </summary>
        private static void ConvertNonToonRimLight(Material src, Material mat, QuestConvertSettings settings, ConversionReport report)
        {
            Color rim = src.HasProperty(QuestCompat.NonToonRimLightColorProp) ? src.GetColor(QuestCompat.NonToonRimLightColorProp) : Color.black;
            float rimMax = Mathf.Max(rim.r, Mathf.Max(rim.g, rim.b));
            bool sourceHasRim = rimMax > 0.0001f; // NonToonは_Enableが無く、リム色が黒=実質OFF

            if (!settings.mapRimLighting || !sourceHasRim)
            {
                mat.SetFloat("_RimIntensity", 0f);
                if (sourceHasRim)
                {
                    report.Info(string.Format("NonToon: 『{0}』のリムライトは既定で無効化しました(必要なら設定「リムライトを近似変換」をオン、または生成マテリアルで手動調整)。", src.name));
                }
                return;
            }

            // ---- 近似変換(mapRimLighting オン) ----
            mat.SetColor("_RimColor", new Color(rim.r, rim.g, rim.b, 1f));
            mat.SetFloat("_RimIntensity", Mathf.Clamp01(rimMax));

            // _RimLightRange(SC_float4 → Color型で保存): .x=開始(fresnelしきい値) / .y=終了。
            // Toon Standard は rimRange = 1 - _RimRange をしきい値、_RimSharpness を smoothstep 半幅として使う
            // (lilToonリム変換と同じ対応付け: 開始が大きいほどリムが細い / 幅が広いほど柔らかい)。
            Color rangeCol = src.HasProperty(QuestCompat.NonToonRimLightRangeProp) ? src.GetColor(QuestCompat.NonToonRimLightRangeProp) : new Color(0.6f, 0.9f, 0f, 0f);
            float start = Mathf.Clamp01(rangeCol.r);
            float end = Mathf.Clamp01(rangeCol.g);
            mat.SetFloat("_RimRange", Mathf.Clamp01(1f - start));
            mat.SetFloat("_RimSharpness", Mathf.Clamp01(Mathf.Abs(end - start) * 0.5f));

            // アルベド乗算率(0=純リム色 / 1=アルベド×リム)は両シェーダーで同義
            float multiplyAlbedo = src.HasProperty(QuestCompat.NonToonRimLightMultiplyAlbedoProp) ? Mathf.Clamp01(src.GetFloat(QuestCompat.NonToonRimLightMultiplyAlbedoProp)) : 0f;
            mat.SetFloat("_RimAlbedoTint", multiplyAlbedo);
            mat.SetFloat("_RimEnvironmental", 0f);
            mat.EnableKeyword(KeywordRimLight);
            report.Info(string.Format("NonToon: 『{0}』のリムライトを近似変換しました(シェーダー挙動差により見た目は完全一致しません)。", src.name));
        }

        /// <summary>変換先で失われる NonToon 機能を明示する。</summary>
        private static void WarnUnsupportedNonToonFeatures(Material src, ConversionReport report)
        {
            // アウトラインは常に失われる(Toon Standard (Outline) はモバイルクライアントのホワイトリスト外)
            report.Info(string.Format("NonToon: 『{0}』のアウトラインはQuestでは非対応のため外れます。", src.name));
            // バックライト(裏面光)・Specular・HairSpecular・RimShade・Details・DistanceFade 等の追加モジュールは非対応
            report.Info(string.Format("NonToon: 『{0}』のバックライト・Specular・Details等の追加表現は変換されません(ベース質感・影・リム・マットキャップのみ対応)。", src.name));
        }

        // ================================================================
        // Poiyomi(.poiyomi/Poiyomi Toon)系
        // ================================================================

        /// <summary>
        /// Poiyomi / Poiyomi(ロック済み)マテリアルを target(Toon Standard / Toon Lit)へ変換する。
        /// アルベドはメイン × _Color に Main Color Adjust(色相/彩度/明度/ガンマ)を焼き込む
        /// (TextureBaker.BakePoiyomiMain。ロック時のテクスチャ剥離はタグから復元)。
        /// このプロジェクトの Poiyomi はダウンローダースタブでシェーダーソースが無いため、影ランプ・
        /// マットキャップ・リム等は再現せず既定へ縮退する(質感の一部は既定値になる)。
        /// 半透明(_SrcBlend/_DstBlend 等で判定)は Convert 側で Emulate/Hide/不透明化へ振り分け済みのため
        /// 通常ここには来ないが、透過保護された髪・大型メッシュや強制指定で来た場合は不透明化警告を出す。
        /// </summary>
        private static Material ConvertPoiyomi(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report, QuestShaderTarget target)
        {
            bool locked = QuestCompat.IsPoiyomiLocked(src);

            // 透過/カットアウトを実ブレンド設定で分類し、どのシグナルで決めたかを報告する
            string decidedBy;
            QuestCompat.TransparencyClass tclass = QuestCompat.ClassifyPoiyomiTransparency(src, out decidedBy);
            if (tclass == QuestCompat.TransparencyClass.Transparent)
            {
                report.Warn(string.Format("Poiyomi: 『{0}』は半透明({1}判定)ですが、Toon Standard/Toon Litは半透明非対応のため不透明として変換します。", src.name, decidedBy));
            }
            else if (tclass == QuestCompat.TransparencyClass.Cutout)
            {
                report.Warn(string.Format("Poiyomi: 『{0}』はカットアウト({1}判定)ですが、Toon Standard/Toon Litは不透明のため塗りつぶして変換します(透過部が板状に見える可能性があります)。", src.name, decidedBy));
            }

            // アルベド(メイン×_Color+色調補正)をアルファ保持でベイク
            Texture2D baked = TextureBaker.BakePoiyomiMain(src, GetBakeMaxSize(src, settings, report), report);

            Material result = target == QuestShaderTarget.ToonStandard
                ? ConvertPoiyomiToToonStandard(src, baked, settings, outputDir, assets, report)
                : ConvertPoiyomiToToonLit(src, baked, settings, outputDir, assets, report);

            if (result != null)
            {
                WarnUnsupportedPoiyomiFeatures(src, report);
                if (locked)
                {
                    report.Info(string.Format("Poiyomi(ロック済み)から変換しました。プロパティが焼き込まれているため一部の質感は既定値になります: 『{0}』", src.name));
                }
            }
            return result;
        }

        /// <summary>Poiyomi → VRChat/Mobile/Toon Standard 変換。</summary>
        private static Material ConvertPoiyomiToToonStandard(Material src, Texture2D baked, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            Shader shader = Shader.Find(QuestCompat.ToonStandardShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonStandardShaderName));
                return null;
            }

            var mat = new Material(shader);

            // ---- メインテクスチャ(ベイク結果。色調はベイク済みのため _Color は白) ----
            Texture2D mainTex = PersistBakedOrFallback(baked, src, "_main", true, settings, outputDir, assets, report);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, QuestCompat.PoiyomiMainTexProp, mat, "_MainTex");
            mat.SetColor("_Color", Color.white);

            // ---- カリング(Poiyomi _Cull と Toon Standard _Culling は同じ Off=0/Front=1/Back=2) ----
            if (src.HasProperty(QuestCompat.PoiyomiCullProp))
            {
                mat.SetInt("_Culling", Mathf.Clamp(Mathf.RoundToInt(src.GetFloat(QuestCompat.PoiyomiCullProp)), 0, 2));
            }

            // ---- 影ランプ: Poiyomi の陰影設定はソース非公開のため再現できない。トゥーン調を保つため
            //      SDK既定の2バンドランプを割り当てる(_ShadowAlbedo は既定0.5=グレースケールランプ前提のまま) ----
            Texture2D defaultRamp = Resources.Load<Texture2D>(QuestCompat.DefaultShadowRampResource);
            if (defaultRamp != null)
            {
                mat.SetTexture("_Ramp", defaultRamp);
                report.Info(string.Format("Poiyomi: 『{0}』の影ランプはSDK既定({1})を使用します(Poiyomiの陰影設定は再現されません)。", src.name, QuestCompat.DefaultShadowRampResource));
            }
            else
            {
                report.Warn(string.Format("Poiyomi: 『{0}』のSDK既定影ランプ({1})が読み込めませんでした。影は白ランプ(フラット)になります。", src.name, QuestCompat.DefaultShadowRampResource));
            }

            // ---- ライト下限: 暗所で真っ黒(消えた見え)を防ぐため、lilToon/NonToon変換と同じ床(0.05)を書き込む ----
            mat.SetFloat("_MinBrightness", 0.05f);

            // ---- ノーマルマップ(ロック時はタグ復元。再ベイクせず元アセットを流用/縮小コピー) ----
            Texture bump = QuestCompat.GetPoiyomiTexture(src, QuestCompat.PoiyomiBumpMapProp);
            if (bump != null)
            {
                mat.SetTexture("_BumpMap", ResolveReusedTexture(bump, src.name, "ノーマルマップ", true, false, true, settings, outputDir, assets, report));
                CopyTextureST(src, QuestCompat.PoiyomiBumpMapProp, mat, "_BumpMap");
                if (src.HasProperty(QuestCompat.PoiyomiBumpScaleProp)) mat.SetFloat("_BumpScale", src.GetFloat(QuestCompat.PoiyomiBumpScaleProp));
                mat.EnableKeyword(KeywordNormalMaps);
            }

            // ---- エミッション ----
            // Toon Standard の _EmissionMap 既定値は白のため、マップが無いのに色だけ引き継ぐと全面発光する。
            // マップ(ロック時はタグ復元)がある場合のみ有効化し、色・強度・HDRを折り込む(lilToonと同じ規則)。
            ConvertPoiyomiEmission(src, mat, settings, outputDir, assets, report);

            // ---- リムライト: Poiyomi のリム設定はソース非公開で信頼できる対応付けができないため常に無効化する。
            //      Toon Standard の _RimIntensity 既定値0.5を放置すると、アトラス統合や後からのキーワード
            //      有効化で「無かったはずのリム」が光り出すため必ず 0 を書き込む ----
            mat.SetFloat("_RimIntensity", 0f);
            if (settings.mapRimLighting)
            {
                report.Info(string.Format("Poiyomi: 『{0}』のリムライトは変換対象外です(Poiyomiのシェーダーソースが無く正確な対応付けができないため。必要なら生成マテリアルで手動調整)。", src.name));
            }

            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>
        /// Poiyomi のエミッションを Toon Standard の Emission へ折り込む(mat は Toon Standard マテリアル)。
        /// settings.bakeEmission が有効で、エミッションマップがあり、_EnableEmission が無いか有効で、
        /// 色が非黒のときのみ有効化する。それ以外は Emission を OFF にする
        /// (マップ無し+色ありでの全面白発光バグを防ぐ)。HDR強度は _EmissionStrength(0〜2)へ折り込む。
        /// なお、割り当て済みマップが無く「ロックで剥がされた未使用マップ」(タグ復元)しか無い場合は、
        /// エミッションOFFで剥がされた可能性が高いため、_EnableEmission が明示的にONのときだけ採用する
        /// (_EnableEmission もロックで剥がされている=トグル値不明のケースで、消えていた発光を復活させない)。
        /// </summary>
        private static void ConvertPoiyomiEmission(Material src, Material mat, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            bool hasEnableProp = src.HasProperty("_EnableEmission");
            bool enableOk = !hasEnableProp || src.GetFloat("_EnableEmission") > 0.5f;
            Texture assignedMap = src.HasProperty(QuestCompat.PoiyomiEmissionMapProp) ? src.GetTexture(QuestCompat.PoiyomiEmissionMapProp) : null;
            Texture emissionMap = null;
            if (settings.bakeEmission && enableOk)
            {
                if (assignedMap != null)
                    emissionMap = assignedMap;                                   // 割り当て済み=使用中。従来どおり採用。
                else if (hasEnableProp)                                          // タグ復元(未使用剥離)は明示ONのときだけ採用。
                    emissionMap = QuestCompat.GetPoiyomiTexture(src, QuestCompat.PoiyomiEmissionMapProp);
            }
            Color emissionColor = src.HasProperty(QuestCompat.PoiyomiEmissionColorProp) ? src.GetColor(QuestCompat.PoiyomiEmissionColorProp) : Color.white;

            if (emissionMap != null && emissionColor.maxColorComponent > 0f)
            {
                mat.SetTexture("_EmissionMap", ResolveReusedTexture(emissionMap, src.name, "エミッションマップ", false, true, false, settings, outputDir, assets, report));
                CopyTextureST(src, QuestCompat.PoiyomiEmissionMapProp, mat, "_EmissionMap");

                float hdrIntensity = Mathf.Max(emissionColor.r, Mathf.Max(emissionColor.g, emissionColor.b));
                if (hdrIntensity > 1f)
                {
                    emissionColor.r /= hdrIntensity;
                    emissionColor.g /= hdrIntensity;
                    emissionColor.b /= hdrIntensity;
                }
                else
                {
                    hdrIntensity = 1f;
                }
                emissionColor.a = 1f;
                mat.SetColor("_EmissionColor", emissionColor);

                float strengthProp = src.HasProperty(QuestCompat.PoiyomiEmissionStrengthProp) ? src.GetFloat(QuestCompat.PoiyomiEmissionStrengthProp) : 1f;
                float strength = strengthProp * hdrIntensity;
                mat.SetFloat("_EmissionStrength", Mathf.Clamp(strength, 0f, 2f));
                if (strength > 2f)
                {
                    report.Info(string.Format("Poiyomi: 『{0}』のエミッション強度({1:F1})がToon Standardの上限(2)を超えるためクランプしました。発光がPC版より弱くなります。", src.name, strength));
                }
                mat.SetInt("_EmissionUV", 0);
            }
            else
            {
                mat.SetColor("_EmissionColor", Color.black);
                mat.SetFloat("_EmissionStrength", 0f);
                if (settings.bakeEmission && enableOk && emissionColor.maxColorComponent > 0f)
                {
                    report.Info(string.Format("Poiyomi: 『{0}』はエミッション色が設定されていますがマップが無い(またはロックで剥離)ため、エミッションなしとして変換しました(単色発光は生成マテリアルで手動設定してください)。", src.name));
                }
            }
        }

        /// <summary>Poiyomi → VRChat/Mobile/Toon Lit 変換(アルベドのみ。陰影・エミッション・リム等は失われる)。</summary>
        private static Material ConvertPoiyomiToToonLit(Material src, Texture2D baked, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            Shader shader = Shader.Find(QuestCompat.ToonLitShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonLitShaderName));
                return null;
            }

            var mat = new Material(shader);

            Texture2D mainTex = PersistBakedOrFallback(baked, src, "_main", true, settings, outputDir, assets, report);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, QuestCompat.PoiyomiMainTexProp, mat, "_MainTex");

            // Toon Lit はカリング指定なし(Back固定)のため両面描画は失われる
            if (src.HasProperty(QuestCompat.PoiyomiCullProp) && Mathf.RoundToInt(src.GetFloat(QuestCompat.PoiyomiCullProp)) == 0)
            {
                report.Warn(string.Format("Poiyomi: 『{0}』の両面描画(Cull Off)はToon Litでは再現されません(背面カリング固定)。", src.name));
            }

            report.Info(string.Format("Poiyomi: 『{0}』をToon Lit(アルベドのみ)へ変換しました。陰影・エミッション・マットキャップ・リム等は反映されません。", src.name));
            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>変換先で失われる Poiyomi 機能を明示する(いずれもソース非公開のため再現不可・情報として提示)。</summary>
        private static void WarnUnsupportedPoiyomiFeatures(Material src, ConversionReport report)
        {
            report.Info(string.Format("Poiyomi: 『{0}』のアウトライン・マットキャップ・リム・シャドウ調整・ラメ等の追加表現はQuestでは非対応のため反映されません(アルベド・ノーマル・エミッション・カリングのみ対応)。", src.name));
        }

        // ================================================================
        // パーティクル系レンダラー用(ParticleSystemRenderer / TrailRenderer / LineRenderer)
        // ================================================================

        /// <summary>
        /// マテリアルをモバイルパーティクルシェーダーへ変換する。
        /// forcedTargetShaderName が指定されていればそのシェーダーへ強制差し替え
        /// (マテリアル別の変換方法指定用)、null なら自動判定:
        /// 乗算ブレンド(シェーダー名 or _DstBlend=SrcColor)なら Particles/Multiply、
        /// それ以外(加算・アルファブレンド含む)は Particles/Additive へ差し替える
        /// (Androidアバターのホワイトリストに加算・乗算以外のパーティクルシェーダーが無いため)。
        /// どちらのシェーダーも _MainTex のみ対応(ティント色プロパティなし。シェーダーソースで検証済み)。
        /// </summary>
        private static Material ConvertParticle(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report, string forcedTargetShaderName = null)
        {
            string shaderName = src.shader != null ? src.shader.name : string.Empty;

            string targetName;
            if (forcedTargetShaderName != null)
            {
                targetName = forcedTargetShaderName;
            }
            else
            {
                // 乗算判定: シェーダー名 or ブレンド設定(Blend Zero SrcColor → _DstBlend = 3)
                bool isMultiply =
                    shaderName.IndexOf("multiply", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (src.HasProperty("_DstBlend") && Mathf.RoundToInt(src.GetFloat("_DstBlend")) == (int)UnityEngine.Rendering.BlendMode.SrcColor);
                targetName = isMultiply ? ParticleMultiplyShaderName : ParticleAdditiveShaderName;
            }

            Shader particleShader = Shader.Find(targetName);
            if (particleShader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", targetName));
                return null;
            }

            var particleMat = new Material(particleShader);
            Texture mainTex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : src.mainTexture;
            if (mainTex != null)
            {
                // メインテクスチャはベイクせず引き継ぐ。縮小計画(textureSizePlan)があれば
                // 元テクスチャを触らず縮小コピーを生成して割り当てる(無ければ縮小計画の追加を促す警告)
                particleMat.SetTexture("_MainTex", ResolveReusedTexture(mainTex, src.name, "パーティクル用メインテクスチャ", false, true, false, settings, outputDir, assets, report));
                if (src.HasProperty("_MainTex"))
                {
                    CopyTextureST(src, "_MainTex", particleMat, "_MainTex");
                }
            }

            string reason = forcedTargetShaderName != null ? "マテリアル別の変換方法指定のため" : "パーティクル系レンダラーで使用されているため";
            report.Warn(string.Format("パーティクル近似変換: '{0}' ({1}) を '{2}' へ差し替えました({3})。ティント色・ソフトパーティクル等は再現されません。", src.name, shaderName, targetName, reason));
            return FinalizeMaterial(particleMat, src, outputDir, assets, report);
        }

        // ================================================================
        // 透過マテリアルの非表示化(transparentHandling == Hide / 手動指定 Hide)
        // ================================================================

        /// <summary>
        /// 透過(アルファブレンド)マテリアルを「何も描画しない」不可視マテリアルへ変換する。
        /// VRChat/Mobile/Particles/Multiply は Blend Zero SrcColor(出力 = 背景 × ソース色)の
        /// 乗算シェーダーで、_MainTex の既定値が白("white")のため、テクスチャ未割り当てなら
        /// ソース色 = 白 → 背景がそのまま残り、見た目上何も描画されない(ZWrite Offのため遮蔽もしない)。
        /// ただし BindChannels でメッシュの頂点カラーがソース色へ乗算されるため、
        /// 白以外の頂点カラーを持つメッシュでは背景が暗く/色付いて乗算され、
        /// 特に黒に近い頂点カラー(AO・ティントのベイク等)では濃いシルエットとして
        /// はっきり見えてしまう(警告で明示する)。
        /// </summary>
        /// <summary>
        /// 効果専用シェーダー(疑似影 / アウトラインのみ)の自動非表示化のレポート文言を返す
        /// (ConvertToHidden の reportOverride へ渡す)。種別ごとに文言を分けて原因を明示する。
        /// </summary>
        private static string OverlayOnlyHideReport(QuestCompat.OverlayOnlyShaderKind kind, string matName)
        {
            return kind == QuestCompat.OverlayOnlyShaderKind.OutlineOnly
                ? string.Format("アウトライン専用シェーダーはQuestで再現できないため非表示化しました: {0}", matName)
                : string.Format("疑似影(FakeShadow)はQuestで再現できないため非表示化しました: {0}", matName);
        }

        /// <param name="reportOverride">
        /// 非表示化の理由を差し替えるレポート文言(効果専用シェーダーの自動非表示化など)。
        /// null のときは従来の透過マテリアル向け警告(頂点カラー乗算の注意付き)を出す。
        /// </param>
        private static Material ConvertToHidden(Material src, string outputDir, ConversionAssetContext assets, ConversionReport report, string reportOverride = null)
        {
            Shader shader = Shader.Find(ParticleMultiplyShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", ParticleMultiplyShaderName));
                return null;
            }

            // _MainTex は割り当てない(シェーダー既定の白テクスチャが乗算の単位元 = 不可視)
            var mat = new Material(shader);
            // 不可視化は乗算シェーダーで行うため、スロット単位で非表示化するメッシュが白以外の頂点カラーを
            // 持つ場合は濃いシルエットとして見えることがある。理由(reportOverride)に関わらず共通で注意する。
            const string vertexColorCaveat = "メッシュが白以外の頂点カラーを持つ場合は背景が暗く・色付いて見え、特に暗い頂点カラー(AOベイク等)では濃いシルエットとして視認されることがあります。";
            if (reportOverride != null)
            {
                report.Warn(reportOverride + " " + vertexColorCaveat);
            }
            else
            {
                report.Warn(string.Format("透過マテリアル『{0}』はQuest版では非表示化しました(透過の扱い=非表示)。", src.name) + vertexColorCaveat);
            }
            return FinalizeMaterial(mat, src, outputDir, assets, report, "_QuestHidden");
        }

        // ================================================================
        // 透過マテリアルの半透明再現(transparentHandling == Emulate)
        // ================================================================

        /// <summary>
        /// 透過(アルファブレンド)マテリアルを、Androidホワイトリスト対象のパーティクルシェーダー
        /// (VRChat/Mobile/Particles/Multiply または Additive)で半透明として再現する。
        /// どちらのシェーダーもアルファを尊重するため、テクスチャのアルファ0のテクセルは描画されない
        /// (頬染め・レンズ等が不透明な板として見える事故が起きない)。両シェーダーとも _MainTex のみで
        /// ティント色プロパティを持たないため、色調(_Color / HSVG / 2nd・3rdレイヤー)はテクスチャへ焼き込む。
        /// lilToonの色調補正ベイク(BakeLilToonMain)・汎用ベイク(BakeGeneric)はいずれもメインテクスチャの
        /// アルファを維持する(ltsother_baker は col *= _Color でアルファも保持、QuestBakeパス0は col.a = mainCol.a)。
        /// さらに lilToon はアルファマスク機能(_AlphaMaskMode)で透過を作ることがあり、メインベイクだけでは
        /// これを取りこぼす(影板 Front_Shadow や顔デカール等が不透明のまま焼かれる)ため、lilToon源は
        /// TextureBaker.ApplyLilToonAlphaMask でアルファマスクをベイク結果のアルファへ反映する。
        /// 乗算(暗い半透明)/加算(明るい半透明)の選択は、まずマテリアル名のオーバーレイトークン
        /// (影・レース等→乗算 / 涙・ハイライト等→加算)で判定し、決まらなければベイク結果の可視テクセルの
        /// アルファ加重平均輝度でフォールバック判定する(乗算バイアス)。
        /// </summary>
        private static Material ConvertTransparentEmulated(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            // ---- (1) ティントをアルファ保持でベイク ----
            int bakeSize = GetBakeMaxSize(src, settings, report);
            bool isLilToon = QuestCompat.IsLilToonShader(src.shader);
            bool isPoiyomi = QuestCompat.IsPoiyomiShader(src.shader);
            // Poiyomi は色調補正(Main Color Adjust)とロック時のテクスチャ剥離復元を含めてベイクする。
            // 乗算済みアルファ(Premultiply)は乗算/加算パーティクルでは正確に再現できないため近似として警告する。
            if (isPoiyomi && src.HasProperty(QuestCompat.PoiyomiAlphaPremultiplyProp) && src.GetFloat(QuestCompat.PoiyomiAlphaPremultiplyProp) > 0.5f)
            {
                report.Warn(string.Format("Poiyomi: 『{0}』は乗算済みアルファ(Premultiply)の透過ですが、乗算/加算パーティクルでは正確に再現できないため近似になります。", src.name));
            }
            Texture2D baked = isLilToon
                ? TextureBaker.BakeLilToonMain(src, bakeSize, report)
                : isPoiyomi
                    ? TextureBaker.BakePoiyomiMain(src, bakeSize, report)
                    : TextureBaker.BakeGeneric(src, bakeSize, false, report);

            // 非lilToonの汎用ベイク(BakeGeneric)はメインテクスチャのアルファのみを反映し、_Color.a を落とす
            // (BakeGeneric は mul.a=1、QuestBakeパス0は col.a = mainCol.a)。色アルファで透過を表現する
            // マテリアル(不透明アルファのメインテクスチャ + _Color.a<1 のティントガラス等)はこのままだと
            // tex.a=1 で全強度描画されてしまうため、メインテクスチャがある場合のみ _Color.a をベイク結果の
            // アルファへ折り込む。メインテクスチャが無い場合は単色テクスチャが既に _Color.a を保持しているので
            // 対象外(二重適用を避ける)。lilToonベイク(ltsother_baker は col*=_Color でアルファも保持)も対象外。
            // Poiyomi ベイク(PoiyomiColorAdjust パス)は col.a = mainTex.a × _Color.a を既に保持するため対象外
            // (lilToon ベイクと同じく二重適用を避ける)。
            if (baked != null && !isLilToon && !isPoiyomi && src.mainTexture != null)
            {
                float colorAlpha = src.HasProperty("_Color") ? src.GetColor("_Color").a : 1f;
                if (colorAlpha < 1f) ScaleTextureAlpha(baked, colorAlpha);
            }

            // lilToon のアルファマスク(_AlphaMaskMode/_AlphaMask/_AlphaMaskScale/_AlphaMaskValue)を反映する。
            // メインベイク(ltsother_baker)は _MainTex.a × _Color.a しか保持しないため、アルファマスクで
            // 透過を作るマテリアル(メインテクスチャ無しの影板 Front_Shadow、Alphaマスクで大半を透明化する
            // 顔デカール等)はアルファ≈1 のまま焼かれ、乗算再現でも板として残ってしまう。乗算再現はアルファ=0で
            // 恒等(背景そのまま)になるため、可視域=アルファの再現が透明部のゴミRGBを隠す肝になる。
            if (baked != null && isLilToon)
            {
                TextureBaker.ApplyLilToonAlphaMask(baked, src, report);
            }

            // ---- (2) 乗算 vs 加算を判定 ----
            // ベイク結果は破棄前(PersistBakedOrFallbackより先)に判定する(輝度判定にはCPU読み取り可能な一時テクスチャが要る)。
            //  (1) 名前判定: マテリアル名のオーバーレイトークン(影・ストッキング等→乗算 / 涙・ハイライト等→加算)。
            //  (2) 明度判定(名前で決まらない場合): ベイク結果の可視テクセルのアルファ加重平均輝度で判定(乗算バイアス)。
            bool useAdditive;
            string decidedBy;
            QuestCompat.OverlayEmulationMode nameMode = QuestCompat.ClassifyOverlayEmulation(src.name);
            if (nameMode == QuestCompat.OverlayEmulationMode.Multiply)
            {
                useAdditive = false;
                decidedBy = "名前判定";
            }
            else if (nameMode == QuestCompat.OverlayEmulationMode.Additive)
            {
                useAdditive = true;
                decidedBy = "名前判定";
            }
            else
            {
                useAdditive = ClassifyEmulationAdditive(baked, src);
                decidedBy = "明度判定";
            }
            string targetShaderName = useAdditive ? ParticleAdditiveShaderName : ParticleMultiplyShaderName;

            Shader shader = Shader.Find(targetShaderName);
            if (shader == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", targetShaderName));
                return null;
            }

            var mat = new Material(shader);
            Texture2D mainTex = PersistBakedOrFallback(baked, src, "_main", true, settings, outputDir, assets, report);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, "_MainTex", mat, "_MainTex");

            string modeLabel = useAdditive ? "加算" : "乗算";
            report.Info(string.Format("半透明を{0}で再現しました({2}): {1}", modeLabel, src.name, decidedBy));
            // メッシュの頂点カラーが白以外だと両シェーダーとも結果が変調される(乗算では暗く、加算では色付く)。
            // マテリアル単体からはメッシュの頂点カラーを判定できないため、1マテリアルにつき1回注意喚起する。
            report.Warn(string.Format("『{0}』: 半透明を{1}で再現しました。メッシュが白以外の頂点カラーを持つ場合、背景が変調されて濃く/色付いて見えることがあります。", src.name, modeLabel));
            if (useAdditive)
            {
                report.Warn(string.Format("『{0}』: 加算で再現したため、暗い場所では光って見えます。", src.name));
            }
            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>
        /// 半透明再現で加算(true)か乗算(false)かを判定する。
        /// ベイク済みメインテクスチャの可視テクセル(アルファ &gt; EmulationVisibleAlphaThreshold)の
        /// アルファ加重平均輝度(Rec.601)が EmulationAdditiveLuminanceThreshold 以上なら加算、未満なら乗算。
        /// テクスチャを読み取れない(ベイク失敗・可視テクセル皆無)場合はマテリアル色の輝度で判定する。
        /// 判定は決定的。
        /// </summary>
        private static bool ClassifyEmulationAdditive(Texture2D baked, Material src)
        {
            if (baked != null)
            {
                try
                {
                    Color32[] pixels = baked.GetPixels32();
                    double weightedLuminance = 0.0;
                    double weight = 0.0;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        float a = pixels[i].a / 255f;
                        if (a <= EmulationVisibleAlphaThreshold) continue;
                        float luminance = (0.299f * pixels[i].r + 0.587f * pixels[i].g + 0.114f * pixels[i].b) / 255f;
                        weightedLuminance += luminance * a;
                        weight += a;
                    }
                    if (weight > 0.0)
                    {
                        return (weightedLuminance / weight) >= EmulationAdditiveLuminanceThreshold;
                    }
                }
                catch (Exception)
                {
                    // GetPixels32 が読み取り不可等で失敗 → マテリアル色へフォールバック
                }
            }

            Color color = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
            float colorLuminance = 0.299f * color.r + 0.587f * color.g + 0.114f * color.b;
            return colorLuminance >= EmulationAdditiveLuminanceThreshold;
        }

        /// <summary>
        /// ベイク済みテクスチャのアルファチャンネルへ一律スケール(0..1)を掛ける。
        /// 半透明再現で、汎用ベイクが落とす _Color.a を可視強度へ反映するために使う。
        /// テクスチャが非可読でGetPixels32に失敗した場合はスケールをスキップする(致命的ではない)。
        /// </summary>
        private static void ScaleTextureAlpha(Texture2D tex, float alphaScale)
        {
            if (tex == null) return;
            alphaScale = Mathf.Clamp01(alphaScale);
            try
            {
                Color32[] pixels = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i].a = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[i].a * alphaScale), 0, 255);
                }
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
            }
            catch (Exception)
            {
                // 非可読テクスチャ等でGetPixels32が失敗 → スケール無しで続行
            }
        }

        // ================================================================
        // 非 lilToon(Standard / Sprites / Particles / UI 等)
        // ================================================================

        /// <summary>
        /// 非lilToonマテリアルの汎用変換(メインテクスチャベイク → Toon Lit)。
        /// disableParticleRedirect が true の場合(Toon Lit 強制指定時)、
        /// パーティクル系シェーダー名によるモバイルパーティクルへの自動差し替えを行わない。
        /// </summary>
        private static Material ConvertGeneric(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report, bool disableParticleRedirect = false)
        {
            string shaderName = src.shader != null ? src.shader.name : string.Empty;

            // 透明系シェーダーの警告: Toon Lit は不透明のため、透けていた部分が塗りつぶされて見える
            WarnIfGenericLooksTransparent(src, shaderName, "Toon Lit", report);

            // パーティクル系はモバイルパーティクルシェーダーへ直接差し替え(強制Toon Lit指定時は除く)
            if (!disableParticleRedirect && shaderName.IndexOf("particle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string targetName = null;
                if (shaderName.IndexOf("add", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetName = ParticleAdditiveShaderName;
                }
                else if (shaderName.IndexOf("multiply", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    targetName = ParticleMultiplyShaderName;
                }

                if (targetName != null)
                {
                    Shader particleShader = Shader.Find(targetName);
                    if (particleShader != null)
                    {
                        var particleMat = new Material(particleShader);
                        if (src.HasProperty("_MainTex"))
                        {
                            Texture particleMain = src.GetTexture("_MainTex");
                            // 縮小計画があれば元テクスチャを触らず縮小コピーを生成して割り当てる
                            particleMat.SetTexture("_MainTex", particleMain != null
                                ? ResolveReusedTexture(particleMain, src.name, "パーティクル用メインテクスチャ", false, true, false, settings, outputDir, assets, report)
                                : null);
                            CopyTextureST(src, "_MainTex", particleMat, "_MainTex");
                        }
                        report.Warn(string.Format("近似変換: '{0}' ({1}) を '{2}' へ差し替えました。ティント色や各種パラメータは再現されません。", src.name, shaderName, targetName));
                        return FinalizeMaterial(particleMat, src, outputDir, assets, report);
                    }
                }
            }

            // 汎用: メインテクスチャからのベイク → Toon Lit
            Shader toonLit = Shader.Find(QuestCompat.ToonLitShaderName);
            if (toonLit == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonLitShaderName));
                return null;
            }

            Texture2D baked = TextureBaker.BakeGeneric(src, GetBakeMaxSize(src, settings, report), settings.bakeEmission, report);
            Texture2D mainTex = PersistBakedOrFallback(baked, src, "_main", true, settings, outputDir, assets, report);

            var mat = new Material(toonLit);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, "_MainTex", mat, "_MainTex");

            report.Warn(string.Format("'{0}' ({1}) はメインテクスチャのみからの近似変換です。質感・透明度・その他パラメータは再現されません。", src.name, shaderName));
            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>
        /// 非lilToonマテリアル → VRChat/Mobile/Toon Standard 強制変換
        /// (マテリアル別の変換方法指定で ToonStandard が選ばれた場合の簡易パス)。
        /// 影・リム等の生成元情報が無いため、ベイク済みメインテクスチャ+フラット影ランプの
        /// 構成にする(見た目は Toon Lit 変換と同等)。アトラス統合などで変換先シェーダーを
        /// Toon Standard に揃えたい場合に使う。
        /// </summary>
        private static Material ConvertGenericToToonStandard(Material src, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            string shaderName = src.shader != null ? src.shader.name : string.Empty;

            // 透明系シェーダーの警告: Toon Standard は不透明のため、透けていた部分が塗りつぶされて見える
            WarnIfGenericLooksTransparent(src, shaderName, "Toon Standard", report);

            Shader toonStandard = Shader.Find(QuestCompat.ToonStandardShaderName);
            if (toonStandard == null)
            {
                report.Error(string.Format("シェーダー '{0}' が見つかりません。VRChat SDKのインポート状態を確認してください。", QuestCompat.ToonStandardShaderName));
                return null;
            }

            // メインテクスチャ × _Color(+設定によりエミッション加算)をベイク
            Texture2D baked = TextureBaker.BakeGeneric(src, GetBakeMaxSize(src, settings, report), settings.bakeEmission, report);
            Texture2D mainTex = PersistBakedOrFallback(baked, src, "_main", true, settings, outputDir, assets, report);

            var mat = new Material(toonStandard);
            if (mainTex != null) mat.SetTexture("_MainTex", mainTex);
            CopyTextureST(src, "_MainTex", mat, "_MainTex");
            mat.SetColor("_Color", Color.white); // 色はベイク済みのため白

            // 影の生成元情報が無いためフラットランプで均一ライティングにする
            // (2バンド既定ランプだと元に無かった陰影が付いてしまう)
            Texture2D flatRamp = Resources.Load<Texture2D>(QuestCompat.FlatShadowRampResource);
            if (flatRamp != null)
            {
                mat.SetTexture("_Ramp", flatRamp);
            }
            else
            {
                report.Warn(string.Format("'{0}': SDK既定のフラット影ランプ({1})が読み込めませんでした。影は白ランプ(フラット)になります。", src.name, QuestCompat.FlatShadowRampResource));
            }

            // エミッションは(bakeEmission設定時)ベイクでメインテクスチャへ加算済みのため、二重適用を避けて無効化する
            mat.SetColor("_EmissionColor", Color.black);

            // リムの生成元情報も無いため明示的に無効化する。Toon Standard の _RimIntensity の
            // 既定値は 0.5 のため、放置するとアトラス統合(先頭メンバーからのコピー)や
            // 後からのキーワード有効化で「無かったはずのリム」が光ることがある
            mat.SetFloat("_RimIntensity", 0f);

            report.Warn(string.Format("'{0}' ({1}) はメインテクスチャのみからの近似変換です(指定によりToon Standardへ変換。影はフラット)。質感・透明度・その他パラメータは再現されません。", src.name, shaderName));
            return FinalizeMaterial(mat, src, outputDir, assets, report);
        }

        /// <summary>
        /// 透明描画に見える非lilToonマテリアル(RenderTypeタグ / レンダーキュー / Sprites・UI系)を
        /// 不透明変換する際の警告。targetLabel には変換先シェーダーの表示名を渡す。
        /// </summary>
        private static void WarnIfGenericLooksTransparent(Material src, string shaderName, string targetLabel, ConversionReport report)
        {
            string renderType = src.GetTag("RenderType", false, string.Empty);
            bool looksTransparent =
                renderType.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                src.renderQueue >= 3000 ||
                shaderName.StartsWith("Sprites/", StringComparison.OrdinalIgnoreCase) ||
                shaderName.StartsWith("UI/", StringComparison.OrdinalIgnoreCase);
            if (looksTransparent)
            {
                report.Warn(string.Format("'{0}' ({1}) は透明描画のマテリアルですが、{2}は透明非対応のため不透明として変換されます(透けていた部分が板状に見える可能性があります)。見た目が崩れる場合は対象オブジェクトのQuest除外(questExcludePaths)を推奨します。", src.name, shaderName, targetLabel));
            }
        }

        // ================================================================
        // 共通ヘルパー
        // ================================================================

        /// <summary>
        /// ベイク結果をPNGアセットとして保存して返す。ベイク失敗・保存失敗時は
        /// 元マテリアルのメインテクスチャへフォールバックする。
        /// </summary>
        private static Texture2D PersistBakedOrFallback(Texture2D baked, Material src, string suffix, bool sRGB, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            if (baked == null)
            {
                Texture2D original = GetMainTexture(src);
                if (original != null)
                {
                    report.Warn(string.Format("'{0}': テクスチャのベイクに失敗したため、元のメインテクスチャをそのまま使用します。", src.name));
                }
                return original;
            }

            // ベイカーが既存アセット(元テクスチャ等)をそのまま返した場合は再保存しない
            if (AssetDatabase.Contains(baked))
            {
                return baked;
            }

            Texture2D saved = PersistTexture(baked, src.name, suffix, sRGB, settings, outputDir, assets, report);
            if (saved == null)
            {
                report.Warn(string.Format("'{0}': ベイクテクスチャの保存に失敗したため、元のメインテクスチャをそのまま使用します。", src.name));
                return GetMainTexture(src);
            }
            return saved;
        }

        /// <summary>
        /// 一時テクスチャを {outputDir}/Textures/{名前}{suffix}.png として保存し、保存後に一時テクスチャを破棄する。
        /// 保存先は実行間で安定したパス(ConversionAssetContext.Claim)で、既存の PNG があれば
        /// SaveTextureAsset が同じパスへ上書きして GUID を保持する(再変換で参照が切れない)。
        /// </summary>
        private static Texture2D PersistTexture(Texture2D tex, string matName, string suffix, bool sRGB, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            if (tex == null) return null;

            string folder = outputDir + "/Textures";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(matName) + suffix + ".png");

            // ランプ判定はファイル名の部分一致ではなくサフィックス指定で明示する
            // (マテリアル名に "_ramp" を含むだけでアルベドがランプ扱いになる誤判定を防ぐ)
            bool isRamp = string.Equals(suffix, "_ramp", StringComparison.Ordinal);
            Texture2D saved = TextureBaker.SaveTextureAsset(tex, path, sRGB, false, isRamp, settings.maxTextureSize, settings.androidFormat);

            // 保存後、メモリ上の一時テクスチャは不要(アセット化されていないもののみ破棄)
            if (!ReferenceEquals(saved, tex) && tex != null && !AssetDatabase.Contains(tex))
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            return saved;
        }

        /// <summary>
        /// マテリアル名の設定・インスタンシング有効化・アセット保存を行い、保存済みインスタンスを返す。
        /// 保存先は実行間で安定したパス(ConversionAssetContext.Claim)で、既存アセットがあれば
        /// GUID を保持したまま内容だけ上書きする(QuestAssetPersistence.SaveOrOverwriteMaterial)。
        /// これにより前回の _Quest クローンや、ユーザーが元アバターへ手動割り当てした
        /// 生成マテリアルの参照が再変換で切れない。
        /// </summary>
        private static Material FinalizeMaterial(Material mat, Material src, string outputDir, ConversionAssetContext assets, ConversionReport report, string suffix = "_Quest")
        {
            mat.name = src.name + suffix;
            mat.enableInstancing = true;

            string folder = outputDir + "/Materials";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(src.name) + suffix + ".mat");
            Material saved = QuestAssetPersistence.SaveOrOverwriteMaterial(mat, path);

            // 既存アセットへ上書きした場合、メモリ上の一時マテリアルは不要
            // (アセット化されておらず、保存側で破棄されていないもののみ破棄する)
            if (saved != null && !ReferenceEquals(saved, mat) && mat != null && !AssetDatabase.Contains(mat))
            {
                UnityEngine.Object.DestroyImmediate(mat);
            }

            Material result = saved != null ? saved : mat;
            report.Info(string.Format("マテリアル '{0}' を '{1}' へ変換しました: {2}", src.name, result.shader.name, path));
            return result;
        }

        // ================================================================
        // テクスチャ縮小計画(settings.textureSizePlan)
        // ================================================================

        /// <summary>
        /// テクスチャに対応する縮小計画エントリの目標サイズを返す。
        /// 計画はアセットGUIDで対応付ける(同一GUIDへの重複指定は最小の目標サイズを採用 =
        /// 見積もり側 QuestSizeEstimator.BuildPlanByGuid と同じ「小さい方勝ち」の規則。
        /// 見積もりと変換で採用値が食い違うと推定サイズと実サイズがずれるため揃える)。
        /// 計画が無い・GUIDが取得できない場合は false。
        /// </summary>
        private static bool TryGetPlannedSize(QuestConvertSettings settings, Texture tex, out int targetSize)
        {
            targetSize = 0;
            if (tex == null || settings == null || settings.textureSizePlan == null || settings.textureSizePlan.Count == 0)
            {
                return false;
            }
            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out guid, out localId) || string.IsNullOrEmpty(guid))
            {
                return false;
            }
            bool found = false;
            foreach (TextureSizePlanEntry entry in settings.textureSizePlan)
            {
                if (entry == null || entry.targetSize <= 0) continue;
                if (entry.textureGuid == guid && (!found || entry.targetSize < targetSize))
                {
                    targetSize = entry.targetSize; // 最小値勝ち(BuildPlanByGuidと同じ規則)
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// ベイク出力の最大サイズ = min(settings.maxTextureSize, 元メインテクスチャの縮小計画値)。
        /// 計画により設定値より小さくなる場合は情報として報告する。
        /// </summary>
        private static int GetBakeMaxSize(Material src, QuestConvertSettings settings, ConversionReport report)
        {
            int maxSize = settings.maxTextureSize;
            Texture mainTex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : src.mainTexture;
            int plannedSize;
            if (TryGetPlannedSize(settings, mainTex, out plannedSize) && plannedSize < maxSize)
            {
                report.Info(string.Format("'{0}': メインテクスチャ '{1}' の縮小計画に従い、ベイク解像度を {2}px 以下に制限します(元テクスチャは変更していません)。", src.name, mainTex.name, plannedSize));
                return plannedSize;
            }
            return maxSize;
        }

        /// <summary>
        /// 変換先マテリアルへ引き継ぐ(再利用する)元テクスチャを解決する。
        /// 縮小計画(settings.textureSizePlan)に目標サイズ &lt; 現在サイズのエントリがある場合、
        /// 元テクスチャのインポート設定は変更せず、縮小したコピーを {outputDir}/Textures へ生成して返す。
        /// 計画が無い場合は元テクスチャをそのまま返し、Android上書きも無ければ
        /// 縮小計画の追加を促す警告を出す(従来の「Android上書きを追加してください」警告の置き換え)。
        /// reportInheritInfo が true の場合、元テクスチャを警告なしで引き継いだときに情報として報告する。
        /// </summary>
        private static Texture ResolveReusedTexture(Texture tex, string matName, string label, bool isNormalMap, bool sRGB, bool reportInheritInfo, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            if (tex == null) return null;

            // 【重要】ロード済みの tex.width/height はアクティブビルドターゲット(通常PC)の
            // インポート結果のため、PC側のmaxTextureSizeがAndroid上書きより小さい場合に
            // 「もう小さい」と誤判定して縮小コピーの生成をスキップしてしまう
            // (Androidビルドでは上書きサイズの大きなテクスチャがそのまま残り、
            //  サイズ見積もりが約束した削減が実現されない)。
            // サイズ見積もり(QuestSizeEstimator.EstimateTexture)と同じ
            // 「ソース画像の元サイズをAndroidの実効maxTextureSizeで頭打ちにした値」で判定する。
            int currentSize = GetEffectiveAndroidSize(tex);
            int plannedSize;
            bool hasPlan = TryGetPlannedSize(settings, tex, out plannedSize);
            if (hasPlan && plannedSize < currentSize)
            {
                Texture2D copy = GetOrCreateDownscaledCopy(tex, plannedSize, isNormalMap, sRGB, settings, outputDir, assets);
                if (copy != null)
                {
                    report.Info(string.Format("'{0}': {1} '{2}' はテクスチャ縮小計画に従い {3}px の縮小コピーを生成して割り当てました(元テクスチャは変更していません): {4}", matName, label, tex.name, plannedSize, AssetDatabase.GetAssetPath(copy)));
                    return copy;
                }
                report.Warn(string.Format("'{0}': {1} '{2}' の縮小コピー生成に失敗したため、元のテクスチャをそのまま引き継ぎます。", matName, label, tex.name));
                return tex;
            }

            if (!hasPlan && !HasAndroidTextureOverride(tex))
            {
                report.Warn(string.Format("'{0}': {1} '{2}' はベイクせずそのまま引き継がれます。PC向けの大きなテクスチャのままだとテクスチャメモリが膨らみパフォーマンスランクが悪化する可能性があるため、サイズ見積もりの削減提案(テクスチャ縮小計画)の適用を推奨します(縮小計画は元テクスチャのインポート設定を変更せず、変換時に縮小コピーを生成します)。", matName, label, tex.name));
            }
            else if (reportInheritInfo)
            {
                report.Info(string.Format("'{0}': {1} '{2}' を引き継ぎました。", matName, label, tex.name));
            }
            return tex;
        }

        /// <summary>
        /// 元テクスチャの縮小コピーを {outputDir}/Textures/{名前}_q{targetSize}{種別}.png へ生成して返す
        /// (実行間で安定したパスへ上書き保存。元テクスチャのアセット・インポート設定は変更しない)。
        /// ファイル名の種別サフィックス(ノーマル: "_n" / リニア: "_lin" / sRGB: なし)は
        /// キャッシュキーと同じ役割区分をパスにも刻むためのもの。同じ元テクスチャが
        /// 実行によって異なる役割で先に払い出されても、役割ごとに別のパス(=別GUID)へ
        /// 保存されるため、既存コピーのインポート種別(NormalMap⇔Default)が実行間で
        /// 反転して旧クローンの参照先が壊れることがない。
        /// 同じ元テクスチャ×目標サイズ×種別のコピーは1回の変換内でキャッシュ共有し、二重生成しない。
        /// 失敗時は null。
        /// </summary>
        private static Texture2D GetOrCreateDownscaledCopy(Texture tex, int targetSize, bool isNormalMap, bool sRGB, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets)
        {
            string guid;
            long localId;
            string texKey = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out guid, out localId)
                ? guid + ":" + localId
                : "iid:" + tex.GetInstanceID();
            string cacheKey = texKey + "@" + targetSize + (isNormalMap ? ":normal" : sRGB ? ":srgb" : ":linear");

            Texture2D cached;
            if (assets.TryGetDownscaledCopy(cacheKey, out cached)) return cached;

            string roleSuffix = isNormalMap ? "_n" : sRGB ? string.Empty : "_lin";
            string folder = outputDir + "/Textures";
            QuestConverterUtility.EnsureFolder(folder);
            string path = assets.Claim(
                folder + "/" + QuestConverterUtility.SanitizeAssetName(tex.name) + "_q" + targetSize + roleSuffix + ".png");
            Texture2D copy = TextureBaker.DownscaleTextureCopy(tex, targetSize, isNormalMap, sRGB, path, GetDownscaleCopyFormat(tex, settings.androidFormat));
            if (copy != null)
            {
                assets.RegisterDownscaledCopy(cacheKey, copy);
            }
            return copy;
        }

        /// <summary>
        /// 縮小コピーへ適用するAndroid圧縮形式を返す。元テクスチャのAndroid上書き形式が
        /// 設定の形式(settings.androidFormat)以下のビット/ピクセル(=同等以上に高効率)なら
        /// その形式を維持する。サイズ見積もり・予算内調整(BuildBudgetFitCandidates の
        /// keepCurrentFormat)は既存の高効率な形式の維持を前提に削減量を計算するため、
        /// コピー側で設定形式へ「格上げ」すると実サイズが見積もりを上回り、
        /// 10MB上限内に収めたはずの計画が超過し得るのを防ぐ(見積もりと同じ規則で揃える)。
        /// </summary>
        private static TextureImporterFormat GetDownscaleCopyFormat(Texture tex, TextureImporterFormat configuredFormat)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return configuredFormat;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return configuredFormat;
            TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
            if (android == null || !android.overridden) return configuredFormat;

            string label;
            bool highQuality;
            float currentBpp = QuestSizeEstimator.FormatToBitsPerPixel(android.format, out label, out highQuality);
            float configuredBpp = QuestSizeEstimator.FormatToBitsPerPixel(configuredFormat, out label, out highQuality);
            return currentBpp <= configuredBpp ? android.format : configuredFormat;
        }

        /// <summary>float トグルプロパティが有効(>0.5)かどうか。</summary>
        private static bool IsFeatureOn(Material m, string propertyName)
        {
            return m.HasProperty(propertyName) && m.GetFloat(propertyName) > 0.5f;
        }

        /// <summary>
        /// 縮小判定に使う「Androidビルドでの実効サイズ(長辺px)」を返す。
        /// サイズ見積もり(QuestSizeEstimator)と同じく、ソース画像の元サイズを
        /// Androidの実効maxTextureSize(Android上書きがあれば上書き値、なければ既定の
        /// maxTextureSize)で頭打ちにした値。アセットでない・インポーターを取得できない
        /// 場合はロード済みテクスチャの寸法へフォールバックする。
        /// </summary>
        private static int GetEffectiveAndroidSize(Texture tex)
        {
            int loadedSize = Mathf.Max(tex.width, tex.height);
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return loadedSize;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return loadedSize;

            int srcWidth, srcHeight;
            QuestSizeEstimator.GetSourceTextureSize(importer, tex, out srcWidth, out srcHeight);
            TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
            int maxSize = android != null && android.overridden ? android.maxTextureSize : importer.maxTextureSize;
            return Mathf.Min(Mathf.Max(srcWidth, srcHeight), Mathf.Max(1, maxSize));
        }

        /// <summary>
        /// テクスチャアセットにAndroidプラットフォームのインポート上書きが設定済みか。
        /// アセットでない・インポーターを取得できない場合は true(=警告しない)を返す。
        /// </summary>
        private static bool HasAndroidTextureOverride(Texture tex)
        {
            if (tex == null) return true;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return true;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return true; // FBX埋め込み等、判定不能な場合は警告しない
            TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
            return android != null && android.overridden;
        }

        /// <summary>テクスチャのタイリング・オフセットをコピーする(両者にプロパティがある場合のみ)。</summary>
        private static void CopyTextureST(Material src, string srcProperty, Material dst, string dstProperty)
        {
            if (!src.HasProperty(srcProperty) || !dst.HasProperty(dstProperty)) return;
            dst.SetTextureScale(dstProperty, src.GetTextureScale(srcProperty));
            dst.SetTextureOffset(dstProperty, src.GetTextureOffset(srcProperty));
        }

        /// <summary>元マテリアルのメインテクスチャを取得する(存在しない場合はnull)。</summary>
        private static Texture2D GetMainTexture(Material src)
        {
            return src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") as Texture2D : null;
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
