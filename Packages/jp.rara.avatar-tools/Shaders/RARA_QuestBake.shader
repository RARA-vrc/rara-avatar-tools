// RARA Quest Converter - テクスチャ合成用ベイクシェーダー
// Graphics.Blit 用のパス集。
//   パス0(無名): メインテクスチャに影色の乗算とエミッションの加算を行う(TextureBaker.Composite / BakeGeneric 用)
//   パス1 "UnpackNormal": インポート済みノーマルマップを展開して標準RGBで出力(MaterialAtlasser 用)
//   パス2 "TintCopy": メインテクスチャ×ティントの単純コピー(MaterialAtlasser 用)
//   パス3 "PoiyomiColorAdjust": メイン×_Color に Poiyomi の色調補正(色相/彩度/明度/ガンマ)を適用(TextureBaker.BakePoiyomiMain 用)
// シェーダー名は QuestConverterCore.cs の QuestCompat.BakeShaderName と一致させること。
Shader "Hidden/RARA/QuestBake"
{
    Properties
    {
        _MainTex ("メインテクスチャ", 2D) = "white" {}
        _MultiplyColor ("乗算カラー(影色など)", Color) = (1,1,1,1)
        _EmissionTex ("エミッションテクスチャ", 2D) = "black" {}
        [HDR] _EmissionColor ("エミッションカラー", Color) = (0,0,0,0)
        // 1: _EmissionTex をサンプリング / 0: 白(単色エミッション)として扱う
        _EmissionUseTex ("エミッションテクスチャを使用", Float) = 0
        // "TintCopy" パス専用の乗算ティント(エミッション色×強度のベイク等)
        _TintColor ("ティントカラー(TintCopyパス用)", Color) = (1,1,1,1)
        // "PoiyomiColorAdjust" パス専用の色調補正パラメータ(Poiyomi の Main Color Adjust 相当)
        _Saturation ("彩度(Poiyomi補正)", Float) = 0
        _MainBrightness ("明度(Poiyomi補正)", Float) = 1
        _MainGamma ("ガンマ(Poiyomi補正)", Float) = 1
        _MainHueShift ("色相シフト(Poiyomi補正・0..1=一周)", Float) = 0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _MultiplyColor;
            sampler2D _EmissionTex;
            float4 _EmissionTex_ST;
            half4 _EmissionColor;
            float _EmissionUseTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uvEmi : TEXCOORD1;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                // エミッションのみタイリング・オフセットを適用(C#側で元マテリアルのSTをコピーする)
                o.uvEmi = TRANSFORM_TEX(v.texcoord, _EmissionTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 mainCol = tex2D(_MainTex, i.uv);
                fixed4 col = mainCol * _MultiplyColor;
                // エミッション: テクスチャ未使用時は白として色のみ加算
                half3 emi = _EmissionUseTex > 0.5 ? tex2D(_EmissionTex, i.uvEmi).rgb : half3(1, 1, 1);
                col.rgb += emi * _EmissionColor.rgb;
                // HDRエミッション等での1超えを最終クランプ
                col.rgb = saturate(col.rgb);
                // アルファはメインテクスチャのものを維持する
                col.a = mainCol.a;
                return col;
            }
            ENDCG
        }

        // ---- パス1 "UnpackNormal"(MaterialAtlasser のノーマルアトラス合成用) ----
        // インポート済みノーマルマップはプラットフォーム向けにスウィズルされている
        // (デスクトップ: DXT5nm系 = xがアルファへ / BC5 等)ため、そのままコピーすると壊れる。
        // UnpackNormal() で展開し、標準RGB(n * 0.5 + 0.5)として出力する。
        // 出力先はリニアRT(RenderTextureReadWrite.Linear)を想定(ノーマルは非sRGB)。
        Pass
        {
            Name "UnpackNormal"
            CGPROGRAM
            #pragma vertex vertUnpack
            #pragma fragment fragUnpack
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct v2fUnpack
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2fUnpack vertUnpack(appdata_img v)
            {
                v2fUnpack o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 fragUnpack(v2fUnpack i) : SV_Target
            {
                float3 n = UnpackNormal(tex2D(_MainTex, i.uv));
                return fixed4(saturate(n * 0.5 + 0.5), 1.0);
            }
            ENDCG
        }

        // ---- パス2 "TintCopy"(MaterialAtlasser のアトラス合成用) ----
        // メインテクスチャ×_TintColor の単純コピー(ブレンドなしの上書き)。
        // ・メインアルベドのセルコピー: _TintColor = 白
        // ・エミッションのセルコピー: _TintColor = エミッション色×強度(LDRへクランプ済み)
        Pass
        {
            Name "TintCopy"
            CGPROGRAM
            #pragma vertex vertTint
            #pragma fragment fragTint
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _TintColor;

            struct v2fTint
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2fTint vertTint(appdata_img v)
            {
                v2fTint o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 fragTint(v2fTint i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                col.rgb = saturate(col.rgb * _TintColor.rgb);
                // アルファはメインテクスチャのまま維持(不透明アトラスでは未使用)
                return col;
            }
            ENDCG
        }

        // ---- パス3 "PoiyomiColorAdjust"(TextureBaker.BakePoiyomiMain 用) ----
        // Poiyomi の Main Color Adjust(色相シフト/彩度/明度/ガンマ)をメイン×_Color のアルベドへ焼き込む。
        // Poiyomi のシェーダーソースはこのプロジェクトでは入手できない(ダウンローダースタブ)ため、
        // 各補正の式は Poiyomi のUI仕様からの推定で、以下を前提とする(順序も含め見た目の近似):
        //   1. tint:      col = mainTex * _MultiplyColor(= 元マテリアルの _Color。アルファも乗算)
        //   2. hue:       HSV へ変換し H を _MainHueShift(0..1 = 0..360°)ぶん回す(彩度・明度は保持)
        //   3. saturation: 輝度(Rec.601)へ向けて lerp。factor = 1 + _Saturation
        //                  (_Saturation は Poiyomi 同様 0 で無変化、負で脱色、正で強調を想定)
        //   4. brightness: col *= _MainBrightness(1 で無変化の乗算)
        //   5. gamma:      col = pow(col, _MainGamma)(1 で無変化)
        // 補正トグルOFF時は C# 側が中立値(hue0/sat0/bright1/gamma1)を渡すため、単純な tint ベイクに退化する。
        // アルファは常にメインテクスチャ×ティントのアルファを維持する(半透明再現の可視域に使うため)。
        Pass
        {
            Name "PoiyomiColorAdjust"
            CGPROGRAM
            #pragma vertex vertPoi
            #pragma fragment fragPoi
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _MultiplyColor;
            float _Saturation;
            float _MainBrightness;
            float _MainGamma;
            float _MainHueShift;

            struct v2fPoi
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2fPoi vertPoi(appdata_img v)
            {
                v2fPoi o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // RGB <-> HSV(標準的な変換。H は 0..1)
            float3 RGBtoHSV(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HSVtoRGB(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            fixed4 fragPoi(v2fPoi i) : SV_Target
            {
                fixed4 mainCol = tex2D(_MainTex, i.uv);
                fixed4 col = mainCol * _MultiplyColor;

                // 色相シフト(HSV で H のみ回す)
                float3 hsv = RGBtoHSV(saturate(col.rgb));
                hsv.x = frac(hsv.x + _MainHueShift);
                col.rgb = HSVtoRGB(hsv);

                // 彩度(輝度へ向けた lerp)
                float luma = dot(col.rgb, float3(0.299, 0.587, 0.114));
                col.rgb = lerp(luma.xxx, col.rgb, 1.0 + _Saturation);

                // 明度(乗算)→ ガンマ
                col.rgb = max(col.rgb * _MainBrightness, 0.0);
                col.rgb = pow(col.rgb, _MainGamma);

                col.rgb = saturate(col.rgb);
                col.a = mainCol.a * _MultiplyColor.a; // アルファは維持(半透明再現の可視域用)
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
