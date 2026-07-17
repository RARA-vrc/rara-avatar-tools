// RARA Quest Converter - 共有コア(設定・レポート・Quest互換性定義)
// VRChat Avatars SDK 3.10.4 / Unity 2022.3 向け。Assembly-CSharp-Editor でコンパイルされる(asmdefなし)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RARA.QuestConverter
{
    /// <summary>変換先のQuest対応シェーダー。</summary>
    public enum QuestShaderTarget
    {
        /// <summary>VRChat/Mobile/Toon Standard(推奨: 影ランプ・ノーマル・エミッション・リム対応)</summary>
        ToonStandard = 0,
        /// <summary>VRChat/Mobile/Toon Lit(最軽量: 陰影・エミッションはメインテクスチャへベイク)</summary>
        ToonLit = 1,
    }

    /// <summary>
    /// マテリアル別の変換方法の上書き指定。Auto 以外を指定すると自動判定より優先される。
    /// </summary>
    public enum MaterialOverride
    {
        /// <summary>自動判定(従来どおりの変換)。</summary>
        Auto = 0,
        /// <summary>VRChat/Mobile/Toon Standard へ強制変換。</summary>
        ToonStandard = 1,
        /// <summary>VRChat/Mobile/Toon Lit へ強制変換。</summary>
        ToonLit = 2,
        /// <summary>VRChat/Mobile/Particles/Additive(加算)へ強制変換。透過っぽい光り物向け。</summary>
        ParticleAdditive = 3,
        /// <summary>VRChat/Mobile/Particles/Multiply(乗算)へ強制変換。頬染め・影系の透過表現向け。</summary>
        ParticleMultiply = 4,
        /// <summary>不可視マテリアルへ差し替えてQuest版では非表示にする。</summary>
        Hide = 5,
        /// <summary>変換せず元のマテリアルのまま残す(アップロード時にSDK警告が出る)。</summary>
        Keep = 6,
    }

    /// <summary>
    /// マージ後PhysBoneの残し方(どのPhysBoneを最終アバターに残すかの決め方)。
    /// </summary>
    public enum PhysBoneSelectionMode
    {
        /// <summary>
        /// 選択制(既定): すべて既定オフ(削除)で、ユーザーが残すものだけ選ぶ。
        /// Poor上限(コンポーネント8)を超えると全PhysBoneが実機で無効化されるため、
        /// 100本以上あるアバターでも安全側に倒せる。残す指定は physBoneKeepPaths に入る。
        /// </summary>
        OptIn = 0,

        /// <summary>従来方式: 外した(physBoneRemovePaths に入れた)ものだけ削除し、残りは全て残す。</summary>
        KeepAll = 1,
    }

    /// <summary>変換モード。Quest対応変換を行うか、PC向けの最適化(統合)のみを行うか。</summary>
    public enum ConversionMode
    {
        /// <summary>Quest変換(シェーダー/テクスチャのQuest対応変換に加え、衣装・トグル整理も行う)。</summary>
        QuestConvert = 0,

        /// <summary>PC最適化のみ(シェーダー/テクスチャ変換は行わず、衣装・トグル整理でメッシュ/スロットだけ削減)。</summary>
        ConsolidateOnly = 1,
    }

    /// <summary>
    /// 半透明(アルファブレンド)マテリアルのQuest版での扱い方。
    /// Quest(Android)のアバターシェーダーホワイトリストにはアルファブレンド透過が無いため、
    /// 半透明はそのままでは再現できない。Emulate は乗算/加算のパーティクルシェーダーで
    /// 半透明を自動的に近似再現する(ユーザー設定不要の既定)。
    /// </summary>
    public enum TransparentHandling
    {
        /// <summary>乗算(暗い半透明)/加算(明るい半透明)で半透明を自動再現する(推奨・既定)。</summary>
        Emulate = 0,
        /// <summary>非表示化する(最軽量。頬染め等が板として見える事故を確実に防ぐ)。</summary>
        Hide = 1,
        /// <summary>不透明に変換する(従来のスキップ相当。透過は失われ塗りつぶされる)。</summary>
        Opaque = 2,
    }

    /// <summary>
    /// 衣装・アクセサリのトグルグループ1件に対する固定方法の選択。
    /// LockVisible / LockHidden にすると独立トグルが解消され、AvatarOptimizer(AAO)が
    /// メッシュ・マテリアルスロットを統合できるようになる(PC・Quest双方でランク削減に効く)。
    /// </summary>
    public enum ToggleLockChoice
    {
        /// <summary>トグルを維持する(従来どおりON/OFF切り替え可能。メッシュ/スロットは削減されない)。</summary>
        Keep = 0,

        /// <summary>常時表示に固定する(常にONへ + m_IsActiveアニメを除去し、AAOで統合可能にする)。</summary>
        LockVisible = 1,

        /// <summary>非表示に固定してメッシュごと除去する(EditorOnly化。メッシュ・スロットを完全に削減)。</summary>
        LockHidden = 2,
    }

    /// <summary>
    /// マテリアル1件分の変換方法上書き設定。
    /// シーンをまたいで保存できるよう、マテリアルはアセットGUIDで参照する
    /// (JsonUtilityでそのままシリアライズ可能)。
    /// </summary>
    [Serializable]
    public class MaterialOverrideEntry
    {
        /// <summary>対象マテリアルのアセットGUID。</summary>
        public string materialGuid;

        /// <summary>変換方法(Auto=自動判定)。</summary>
        public MaterialOverride mode = MaterialOverride.Auto;

        /// <summary>このマテリアルをアトラス統合の対象から除外する。</summary>
        public bool excludeFromAtlas = false;
    }

    /// <summary>
    /// テクスチャ縮小計画の1件。変換時に対象テクスチャを targetSize(長辺)へ縮小した
    /// コピーを Generated 配下へ生成し、変換後マテリアルから参照させるための計画で、
    /// 元テクスチャのインポート設定は一切変更しない。
    /// シーンをまたいで保存できるよう、テクスチャはアセットGUIDで参照する
    /// (JsonUtilityでそのままシリアライズ可能)。
    /// </summary>
    [Serializable]
    public class TextureSizePlanEntry
    {
        /// <summary>対象テクスチャのアセットGUID。</summary>
        public string textureGuid;

        /// <summary>縮小コピーの長辺サイズ(px)。元テクスチャがこれ以下なら縮小しない。</summary>
        public int targetSize;
    }

    /// <summary>
    /// 衣装・トグルグループ1件ぶんの固定方法の指定。
    /// groupId は ToggleConsolidator.DetectToggleGroups が返す ToggleGroup.id
    /// (アバタールート相対のオブジェクトパス)。JsonUtilityでそのままシリアライズ可能。
    /// </summary>
    [Serializable]
    public class ToggleGroupChoice
    {
        /// <summary>対象トグルグループのID(アバタールート相対のオブジェクトパス)。</summary>
        public string groupId;

        /// <summary>固定方法(Keep=維持 / LockVisible=常時表示 / LockHidden=非表示除去)。</summary>
        public ToggleLockChoice choice = ToggleLockChoice.Keep;
    }

    /// <summary>
    /// ポリゴン削減(メッシュ簡略化)の配分計画1件ぶんの保存用データ。
    /// レンダラーはアバタールート相対のパスで参照する(QuestCompat.GetRelativePath 準拠)ため、
    /// シーンをまたいでも JsonUtility でそのままシリアライズ・復元できる。
    /// UI表示用の付加情報(カテゴリ・現在の三角形数・品質下限)は
    /// PolygonBudgetPlanner.PolygonPlanEntry 側が持ち、保存にはパスと目標数のみを残す。
    /// </summary>
    [Serializable]
    public class PolygonPlanEntryData
    {
        /// <summary>対象レンダラーのアバタールート相対パス(QuestCompat.GetRelativePath)。</summary>
        public string rendererPath;

        /// <summary>このレンダラーの目標三角形数。変換時にこの数以下へ簡略化する。</summary>
        public int targetTris;
    }

    /// <summary>変換設定。ウィンドウから編集され、各モジュールに渡される。</summary>
    [Serializable]
    public class QuestConvertSettings
    {
        [Tooltip("変換モード。Quest変換=シェーダー/テクスチャ変換+衣装・トグル整理 / PC最適化のみ=変換せず衣装・トグル整理でメッシュ・スロットだけ削減(PCのランク削減向け)")]
        public ConversionMode conversionMode = ConversionMode.QuestConvert;

        public QuestShaderTarget shaderTarget = QuestShaderTarget.ToonStandard;

        [Tooltip("ベイク生成テクスチャの最大サイズ(Quest推奨: 1024)")]
        public int maxTextureSize = 1024;

        [Tooltip("Androidプラットフォームのテクスチャ圧縮形式")]
        public TextureImporterFormat androidFormat = TextureImporterFormat.ASTC_6x6;

        [Tooltip("単色・低ディテールなテクスチャを検出して極端に縮小する(見た目の劣化が少ない範囲で)")]
        public bool aggressiveTextureReduction = true;

        [Tooltip("Toon Lit時: lilToonの影色をメインテクスチャに乗算ベイクする(フラットな擬似陰影)")]
        public bool bakeShadowIntoMainTex = true;

        [Tooltip("Toon Standard時: lilToonの影設定から影ランプテクスチャを生成する(オフ時はSDK既定ランプ)")]
        public bool generateShadowRamp = true;

        [Tooltip("エミッションを変換する(Toon Lit: メインへ加算ベイク / Toon Standard: Emissionへマップ)")]
        public bool bakeEmission = true;

        [Tooltip("lilToonのリムライトをToon Standardのリムへ近似変換する(挙動が異なるため、まぶた等に想定外のハイライトが出ることがある。既定はオフ)")]
        public bool mapRimLighting = false;

        [Tooltip("半透明(アルファブレンド)マテリアルのQuest版での扱い。Emulate=乗算/加算で半透明を自動再現(推奨・既定。ユーザー設定不要でチーク・涙・レンズ等を近似再現) / Hide=非表示(最軽量) / Opaque=不透明化(従来のスキップ相当)。個別のマテリアルはマテリアル設定で変換方法を上書きできる")]
        public TransparentHandling transparentHandling = TransparentHandling.Emulate;

        // 【旧設定・移行用】以前の透過処理はこの2つのboolで制御していた。
        // 現在は変換ロジックはすべて transparentHandling で分岐する。これらは保存済みJSONからの
        // 移行元としてのみ残す(ウィンドウの LoadSettings が transparentHandling 欠落時に参照する:
        // hideTransparentMaterials==false → Opaque / それ以外 → Emulate)。新規に読み書きしないこと。
        [Tooltip("【旧設定・移行用】透過マテリアルを非表示化する。現在は transparentHandling へ移行済み")]
        public bool hideTransparentMaterials = true;

        [Tooltip("【旧設定・移行用】表情デカール(顔の透過オーバーレイ)を自動検出して扱う。現在も表情デカールの個別処理の有効/無効に使用する")]
        public bool hideExpressionOverlays = true;

        [Tooltip("マテリアル差し替えアニメーション(FX等)も変換後マテリアルを参照するよう複製・差し替える")]
        public bool convertAnimations = true;

        [Tooltip("Android非対応コンポーネント(Cloth/Camera/Light/AudioSource等)を削除する")]
        public bool removeUnsupportedComponents = true;

        [Tooltip("Unityコンストレイントを見つけたらVRCConstraintへ変換する(SDKの変換APIを使用)")]
        public bool convertUnityConstraints = true;

        [Tooltip("設定が一致する兄弟PhysBoneチェーンを1つのコンポーネントへマージし、揺れを維持したままコンポーネント数を削減する(パラメータ使用・アニメ制御あり・カーブ設定ありなどは自動で対象外)")]
        public bool mergePhysBones = true;

        [Tooltip("設定が異なる兄弟チェーンも先頭の設定に統一してマージする(揺れ方が先頭チェーンに揃う)")]
        public bool physBoneLooseMerge = true;

        [Tooltip("PhysBoneのマージ後もAndroidのPoor上限(コンポーネント8/コライダー16)を超える場合、超過分を自動削除する")]
        public bool trimPhysBonesToPoorLimit = false;

        // 【PhysBone識別パスの規則】(physBoneRemovePaths / physBoneNoMergePaths 共通)
        //   QuestCompat.GetRelativePath(アバタールート, pb.transform) のスラッシュ区切り相対パス。
        //   同一GameObjectに複数のPhysBoneが付いている場合のみ、末尾に「#インデックス」
        //   (GetComponents順・0始まり)を付与する(例: "Armature/Hips/Skirt#1")。
        //   生成・解決はともに ComponentRemover.GetPhysBoneIdentityPath /
        //   ComponentRemover.RemoveSelectedPhysBones が行う(手書きしないこと)。
        [Tooltip("削除指定されたPhysBoneの識別パス(プレビューで「削除」を選んだもの)。変換時に複製アバターから削除される")]
        public List<string> physBoneRemovePaths = new List<string>();

        [Tooltip("マージから除外するPhysBoneの識別パス(プレビューで「マージしない」を選んだもの)")]
        public List<string> physBoneNoMergePaths = new List<string>();

        [Tooltip("PhysBoneの残し方。OptIn=既定オフで選んだPhysBoneだけ残す(推奨。Poor上限8を超えると全PhysBoneが無効化されるため) / KeepAll=従来: 外したものだけ削除")]
        public PhysBoneSelectionMode physBoneSelectionMode = PhysBoneSelectionMode.OptIn;

        [Tooltip("OptInモードで残すPhysBoneの識別パス(プレビューで「残す」を選んだもの)。マージ後(POST-MERGE)のレイアウト基準")]
        public List<string> physBoneKeepPaths = new List<string>();

        [Tooltip("変換後に元のアバターを非アクティブ化する")]
        public bool deactivateOriginal = true;

        [Tooltip("Quest版でEditorOnly化(ビルド除外)するオブジェクトの相対パス。PC版には影響しない")]
        public System.Collections.Generic.List<string> questExcludePaths = new System.Collections.Generic.List<string>();

        [Tooltip("マテリアル別の変換方法指定")]
        public List<MaterialOverrideEntry> materialOverrides = new List<MaterialOverrideEntry>();

        [Tooltip("衣装・トグルグループごとの固定方法(維持/常時表示/非表示除去)。常時表示・非表示にするとトグルが解消され、AvatarOptimizerがメッシュ・マテリアルスロットを統合できるようになる(Quest・PC双方に効く)")]
        public List<ToggleGroupChoice> toggleChoices = new List<ToggleGroupChoice>();

        [Tooltip("服の下に隠れる肌などをAAOのブレンドシェイプ削除で消す(shrinkブレンドシェイプ検出時。見えない部分のみ)")]
        public bool removeHiddenMeshByBlendShape = false;

        [Tooltip("ブレンドシェイプ削除を適用するレンダラーの相対パス(検出された隠し/縮小ブレンドシェイプから選択したもの)")]
        public List<string> hiddenMeshRendererPaths = new List<string>();

        [Tooltip("AAOのTrace and Optimizeが無ければ複製に追加してビルド時最適化を有効にする")]
        public bool ensureTraceAndOptimize = true;

        // 【SkinnedMesh統合】顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を AAO MergeSkinnedMesh で
        //   1つへ統合し、SMR数・マテリアルスロット数を確実に削減する(Quest Poor上限=SMR2/スロット4対策)。
        //   統合はビルド時(NDMF)にAAOが行う。表情(顔ブレンドシェイプ/ビセーム)は分離維持で保たれる。
        //   【移行】旧保存JSONにこのキーが無い場合は LoadSettings で None(統合しない)へ戻し、既存ユーザーの
        //   挙動を変えない(新規設定の既定は MergeExceptFace=推奨)。
        [Tooltip("SkinnedMeshの統合方法。しない=従来どおり / 顔以外を統合=顔(ビセーム/まばたき)以外の全SkinnedMeshRendererを1つへ統合しSMR数・スロット数を削減(推奨。Quest Poor上限SMR2/スロット4対策)")]
        public SkinnedMeshMergeMode mergeSkinnedMeshesMode = SkinnedMeshMergeMode.MergeExceptFace;

        [Tooltip("SkinnedMesh統合から個別に除外するレンダラーの相対パス(プレビューで「統合しない」を選んだもの)")]
        public List<string> skinnedMeshMergeOptOutPaths = new List<string>();

        [Tooltip("変換時にこのサイズへ縮小したコピーを生成するテクスチャの計画。元テクスチャは変更しない")]
        public List<TextureSizePlanEntry> textureSizePlan = new List<TextureSizePlanEntry>();

        [Tooltip("互換マテリアルを1枚のアトラスに統合しスロット数とテクスチャメモリを削減(実験的)")]
        public bool enableAtlas = false;

        [Tooltip("アトラステクスチャの最大サイズ")]
        public int atlasMaxSize = 2048;

        [Tooltip("アトラス統合時、影ランプの違いを無視してグループ化する(1グループ=1代表ランプに統一)。影ランプが個別生成されるアバターでもスロットを大きく削減できる。影のトーンはグループ内で共通化される")]
        public bool atlasUnifyRamps = true;

        // 【ポリゴン削減(メッシュ簡略化)】
        //   目標三角形数に収まるよう、配分計画(decimationPlan)に従って各メッシュを簡略化する。
        //   頂点は元メッシュの部分集合になるため UV・法線・ボーンウェイト・ブレンドシェイプ差分は
        //   そのまま有効(補間なし)= ビセーム・表情は保たれる。顔・髪は配分で強く保護される。
        //   計画はウィンドウの「自動で配分計画を作成」(PolygonBudgetPlanner.BuildPlan)で生成する。
        //   旧保存JSONにこれらのキーが無い場合は JsonUtility が既定値(無効・空計画)を維持するため、
        //   既存ユーザーの挙動は変わらない(移行ガード不要)。
        [Tooltip("ポリゴン削減(メッシュ簡略化)を有効にする。目標ポリゴン数に収まるよう配分計画に従って各メッシュを簡略化する(顔・髪は強く保護)。既定はオフ")]
        public bool enableDecimation = false;

        [Tooltip("ポリゴン削減の目標三角形数(Questランク目安: Excellent 7500 / Good 10000 / Medium 15000 / Poor 20000)")]
        public int decimationTargetTriangles = 20000;

        [Tooltip("ポリゴン削減の配分計画(レンダラーごとの目標三角形数)。ウィンドウの「自動で配分計画を作成」で生成し、行ごとに編集・除外できる")]
        public List<PolygonPlanEntryData> decimationPlan = new List<PolygonPlanEntryData>();

        [Tooltip("生成アセットの出力先ルートフォルダ")]
        public string outputFolder = "Assets/RARA/QuestConverter/Generated";
    }

    /// <summary>変換・診断の結果レポート。UIにそのまま表示できる形で蓄積する。</summary>
    public class ConversionReport
    {
        public enum Severity { Info, Warning, Error }

        public struct Entry
        {
            public Severity severity;
            public string message;
        }

        public readonly List<Entry> entries = new List<Entry>();

        public bool HasErrors { get { return entries.Any(e => e.severity == Severity.Error); } }
        public int WarningCount { get { return entries.Count(e => e.severity == Severity.Warning); } }

        public void Info(string message) { Add(Severity.Info, message); }
        public void Warn(string message) { Add(Severity.Warning, message); }
        public void Error(string message) { Add(Severity.Error, message); }

        private void Add(Severity s, string message)
        {
            entries.Add(new Entry { severity = s, message = message });
            switch (s)
            {
                case Severity.Warning: Debug.LogWarning("[RARA QuestConverter] " + message); break;
                case Severity.Error: Debug.LogError("[RARA QuestConverter] " + message); break;
                default: Debug.Log("[RARA QuestConverter] " + message); break;
            }
        }

        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in entries)
            {
                sb.Append(e.severity == Severity.Error ? "[エラー] " : e.severity == Severity.Warning ? "[警告] " : "[情報] ");
                sb.AppendLine(e.message);
            }
            return sb.ToString();
        }
    }

    /// <summary>Android(Quest)アバターのパフォーマンス上限値(2026-07時点の公式仕様)。</summary>
    public static class QuestLimits
    {
        // Poor上限(これを超えると Very Poor。アップロード可否はサイズのみで決まりランクでは決まらない)
        public const int PoorPolygons = 20000;
        public const int PoorSkinnedMeshes = 2;
        public const int PoorBasicMeshes = 2;
        public const int PoorMaterialSlots = 4;
        public const int PoorBones = 150;
        public const int PoorPhysBoneComponents = 8;
        public const int PoorPhysBoneTransforms = 64;
        public const int PoorPhysBoneColliders = 16;
        public const int PoorContacts = 16;
        public const int PoorConstraints = 150;
        public const int PoorTextureMemoryMB = 40;

        // Medium上限(Questの既定表示ランクは Medium。これ以下を推奨)
        public const int MediumPolygons = 15000;
        public const int MediumSkinnedMeshes = 2;
        public const int MediumBasicMeshes = 2;
        public const int MediumMaterialSlots = 2;
        public const int MediumBones = 150;
        public const int MediumPhysBoneComponents = 6;
        public const int MediumTextureMemoryMB = 25;

        /// <summary>Androidアバターのダウンロードサイズ上限(ビルド後圧縮、MB)。超過するとアップロード/表示不可。</summary>
        public const int HardDownloadSizeCapMB = 10;
    }

    /// <summary>Quest(Android)アバターの互換性定義と検出ユーティリティ。</summary>
    public static class QuestCompat
    {
        /// <summary>Androidアバターで許可されるシェーダー名(SDK 3.10.4 の ShaderWhiteList 相当)。</summary>
        public static readonly string[] AllowedShaders =
        {
            "VRChat/Mobile/Standard Lite",
            "VRChat/Mobile/Diffuse",
            "VRChat/Mobile/Bumped Diffuse",
            "VRChat/Mobile/Bumped Mapped Specular",
            "VRChat/Mobile/Toon Lit",
            "VRChat/Mobile/MatCap Lit",
            "VRChat/Mobile/Particles/Additive",
            "VRChat/Mobile/Particles/Multiply",
            "VRChat/Mobile/Toon Standard",
            "VRChat/Mobile/Toon Standard (Outline)", // モバイルでは自動的に非Outline版へフォールバック
        };

        public const string ToonStandardShaderName = "VRChat/Mobile/Toon Standard";
        public const string ToonLitShaderName = "VRChat/Mobile/Toon Lit";
        public const string LilToonBakerShaderName = "Hidden/ltsother_baker";
        public const string BakeShaderName = "Hidden/RARA/QuestBake";

        /// <summary>SDK同梱の既定影ランプ(Resources.Load用パス)。</summary>
        public const string DefaultShadowRampResource = "VRChat/ShadowRampToon2Band";

        /// <summary>SDK同梱のフラット影ランプ(影無効マテリアル用、Resources.Load用パス)。</summary>
        public const string FlatShadowRampResource = "VRChat/ShadowRampFlat";

        public static bool IsMobileShader(Shader shader)
        {
            return shader != null && Array.IndexOf(AllowedShaders, shader.name) >= 0;
        }

        public static bool IsLilToonShader(Shader shader)
        {
            return shader != null && shader.name.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================================================================
        // NonToon(jp.lilxyzw.nontoon / ShaderCraft ベース)
        // ================================================================

        /// <summary>NonToon シェーダー名(Shaders/NonToon.scshader の Shader "NonToon" で検証済み)。</summary>
        public const string NonToonShaderName = "NonToon";

        /// <summary>NonToonFur シェーダー名(ファー用。ベースは NonToon と共通、ファー分は再現不可)。</summary>
        public const string NonToonFurShaderName = "NonToonFur";

        // ---- NonToon プロパティ名(実マテリアル Body_skin_NT.mat / 各 properties.hlsl で検証済み) ----
        // ベースプロパティは非接頭辞。モジュールプロパティは _jp_lilxyzw_nontoon_<module>_ 接頭辞
        // (SCProperty がインポート時に付与するため、シェーダーソースの原名ではこの接頭辞名で参照する)。
        /// <summary>メインテクスチャ(= アルベド。[SCMainTexture] で material.mainTexture も解決する。メインティント色は存在しない)。</summary>
        public const string NonToonBaseTextureProp = "_BaseTexture";
        /// <summary>ノーマルマップ。</summary>
        public const string NonToonNormalMapProp = "_NormalMap";
        /// <summary>ノーマルマップ強度。</summary>
        public const string NonToonNormalScaleProp = "_NormalScale";
        /// <summary>描画モード(SC_uint → Integer 型。0=不透明 / 1=カットアウト / 2=半透明)。GetIntegerSafe で読む。</summary>
        public const string NonToonRenderingModeProp = "_RenderingMode";
        /// <summary>カリング(legacy Int → GetFloat。Off=0/Front=1/Back=2)。</summary>
        public const string NonToonCullProp = "_Cull";
        /// <summary>影ランプ用の共有グラデーション配列(Texture2DArray。マテリアル個別スロット)。</summary>
        public const string NonToonSharedGradientsProp = "_SharedGradients";
        /// <summary>影(Shade)モジュール: グラデーション配列スライス番号(SC_int → Integer。-1=影無効)。</summary>
        public const string NonToonShadeGradientIndexProp = "_jp_lilxyzw_nontoon_shade_ShadeGradientIndex";
        /// <summary>影(Shade)モジュール: half-lambert ウィンドウ(SC_float4 → Color型で保存。.y = 明側上限)。</summary>
        public const string NonToonShadeGradientRangeProp = "_jp_lilxyzw_nontoon_shade_ShadeGradientRange";
        /// <summary>マットキャップモジュール: 有効フラグ(SC_uint トグル → Integer。1=有効)。</summary>
        public const string NonToonMatCapEnableProp = "_jp_lilxyzw_nontoon_matcaps_Enable";
        /// <summary>マットキャップ(乗算)テクスチャ。</summary>
        public const string NonToonMatCapMultiplyProp = "_jp_lilxyzw_nontoon_matcaps_MatCapMultiply";
        /// <summary>マットキャップ(乗算)色。</summary>
        public const string NonToonMatCapMultiplyColorProp = "_jp_lilxyzw_nontoon_matcaps_MatCapMultiplyColor";
        /// <summary>マットキャップ(加算)テクスチャ。</summary>
        public const string NonToonMatCapAddProp = "_jp_lilxyzw_nontoon_matcaps_MatCapAdd";
        /// <summary>マットキャップ(加算)色。</summary>
        public const string NonToonMatCapAddColorProp = "_jp_lilxyzw_nontoon_matcaps_MatCapAddColor";
        /// <summary>リムライトモジュール: リム色(SC_color。黒=実質OFF。_Enable 相当は無い)。</summary>
        public const string NonToonRimLightColorProp = "_jp_lilxyzw_nontoon_rimlight_RimLightColor";
        /// <summary>リムライトモジュール: アルベド乗算率(0=純リム色 / 1=アルベド×リム)。</summary>
        public const string NonToonRimLightMultiplyAlbedoProp = "_jp_lilxyzw_nontoon_rimlight_RimLightMultiplyAlbedo";
        /// <summary>リムライトモジュール: フレネル範囲(SC_float4 → Color型で保存。.x=開始 / .y=終了)。</summary>
        public const string NonToonRimLightRangeProp = "_jp_lilxyzw_nontoon_rimlight_RimLightRange";

        /// <summary>NonToon または NonToonFur シェーダーか(名前完全一致)。</summary>
        public static bool IsNonToonShader(Shader shader)
        {
            if (shader == null) return false;
            return string.Equals(shader.name, NonToonShaderName, StringComparison.Ordinal) ||
                   string.Equals(shader.name, NonToonFurShaderName, StringComparison.Ordinal);
        }

        /// <summary>NonToonFur(ファー)シェーダーか(名前完全一致)。</summary>
        public static bool IsNonToonFurShader(Shader shader)
        {
            return shader != null && string.Equals(shader.name, NonToonFurShaderName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Integer 型(ShaderLab Integer)プロパティを安全に読む。SC_uint/SC_int 由来の
        /// _RenderingMode / _ShadeGradientIndex / 各 *MaskChannel / _SDFType / _Enable などが対象。
        /// プロパティが無ければ fallback、GetInteger が使えない型・環境なら GetFloat を丸めて返す。
        /// </summary>
        public static int GetIntegerSafe(Material mat, string name, int fallback)
        {
            if (mat == null || string.IsNullOrEmpty(name) || !mat.HasProperty(name)) return fallback;
            try
            {
                return mat.GetInteger(name);
            }
            catch (Exception)
            {
                try { return Mathf.RoundToInt(mat.GetFloat(name)); }
                catch (Exception) { return fallback; }
            }
        }

        // ================================================================
        // Poiyomi(.poiyomi/Poiyomi Toon 系。ロック済みも含む)
        // ================================================================
        // 【重要】このプロジェクトの Poiyomi パッケージはダウンローダースタブでシェーダーソースが
        // 入っていないため、以下のプロパティ名はすべてドキュメント由来の推定であり、参照は必ず
        // HasProperty でガードする。キープロパティが欠落する場合(ロックで定数が焼き込まれた等)は
        // 汎用パスへ縮退し、明示的に Warn/Info する。

        // ---- 検出 ----
        /// <summary>ロック済みシェーダー名の接頭辞(Poiyomi の Lock In 済み: "Hidden/Locked/{元名}/{guid}")。</summary>
        public const string LockedShaderPrefix = "Hidden/Locked/";

        /// <summary>ロック時に剥がされた未使用テクスチャのGUIDを保持するマテリアルタグの接頭辞(mat.GetTag)。</summary>
        public const string StrippedTexTagPrefix = "_stripped_tex_";

        // ---- Poiyomi プロパティ名(ドキュメント由来。全て HasProperty ガード必須) ----
        public const string PoiyomiMainTexProp = "_MainTex";
        public const string PoiyomiColorProp = "_Color";
        public const string PoiyomiColorAdjustToggleProp = "_MainColorAdjustToggle";
        public const string PoiyomiSaturationProp = "_Saturation";
        public const string PoiyomiBrightnessProp = "_MainBrightness";
        public const string PoiyomiGammaProp = "_MainGamma";
        public const string PoiyomiHueShiftProp = "_MainHueShift";
        public const string PoiyomiBumpMapProp = "_BumpMap";
        public const string PoiyomiBumpScaleProp = "_BumpScale";
        public const string PoiyomiCutoffProp = "_Cutoff";
        public const string PoiyomiAlphaPremultiplyProp = "_AlphaPremultiply";
        public const string PoiyomiCullProp = "_Cull";
        public const string PoiyomiSrcBlendProp = "_SrcBlend";
        public const string PoiyomiDstBlendProp = "_DstBlend";
        public const string PoiyomiZWriteProp = "_ZWrite";
        public const string PoiyomiAlphaToCoverageProp = "_AlphaToCoverage";
        public const string PoiyomiEmissionColorProp = "_EmissionColor";
        public const string PoiyomiEmissionMapProp = "_EmissionMap";
        public const string PoiyomiEmissionStrengthProp = "_EmissionStrength";
        public const string PoiyomiShaderOptimizerProp = "_ShaderOptimizerEnabled";

        /// <summary>
        /// Poiyomi シェーダーか(シェーダー名に "poiyomi" を含む・大文字小文字無視)。
        /// アンロック(".poiyomi/Poiyomi Toon" 等)もロック済み("Hidden/Locked/{元名}/{guid}" は
        /// 元名に "Poiyomi" を残す)も、この部分一致で拾える。
        /// </summary>
        public static bool IsPoiyomiShader(Shader shader)
        {
            return shader != null && shader.name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>シェーダー名がロック済み("Hidden/Locked/…")か。</summary>
        public static bool IsLockedShaderName(string shaderName)
        {
            return !string.IsNullOrEmpty(shaderName) &&
                   shaderName.StartsWith(LockedShaderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// ロック済み(Lock In / ShaderOptimizer 済み)の Poiyomi マテリアルか。
        /// 名前が "Hidden/Locked/" で始まるか、_ShaderOptimizerEnabled==1 のいずれか。
        /// </summary>
        public static bool IsPoiyomiLocked(Material mat)
        {
            if (mat == null || mat.shader == null || !IsPoiyomiShader(mat.shader)) return false;
            if (IsLockedShaderName(mat.shader.name)) return true;
            return mat.HasProperty(PoiyomiShaderOptimizerProp) && mat.GetFloat(PoiyomiShaderOptimizerProp) > 0.5f;
        }

        /// <summary>
        /// Poiyomi マテリアルからテクスチャを取得する。プロパティに割り当てがあればそれを返し、
        /// 無ければロック時に剥がされた未使用テクスチャのタグ("_stripped_tex_{prop}" にGUIDを保持)を
        /// 参照してアセットをロードして返す(ロック済みシェーダーの復元)。どちらも無ければ null。
        /// </summary>
        public static Texture GetPoiyomiTexture(Material mat, string propertyName)
        {
            if (mat == null || string.IsNullOrEmpty(propertyName)) return null;
            Texture assigned = mat.HasProperty(propertyName) ? mat.GetTexture(propertyName) : null;
            if (assigned != null) return assigned;

            // ロックで剥がされたテクスチャのタグ復元(値はGUID。稀に "guid,fileID,type" 形式のため先頭要素を採る)
            string tag = mat.GetTag(StrippedTexTagPrefix + propertyName, false, string.Empty);
            if (string.IsNullOrEmpty(tag)) return null;
            int comma = tag.IndexOf(',');
            string guid = comma >= 0 ? tag.Substring(0, comma) : tag;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<Texture>(path);
        }

        // ================================================================
        // 透過分類
        // ================================================================

        /// <summary>マテリアルの透過分類。Transparent(アルファブレンド)がQuest非表示化の対象。</summary>
        public enum TransparencyClass
        {
            /// <summary>不透明(従来どおり不透明として変換)。</summary>
            Opaque = 0,
            /// <summary>カットアウト(アルファテスト。描画コストは不透明相当のため非表示化しない)。</summary>
            Cutout = 1,
            /// <summary>アルファブレンド透過(Quest非表示化の対象)。</summary>
            Transparent = 2,
        }

        /// <summary>
        /// マテリアルの透過分類を判定する(マテリアル・シェーダーがnullなら Opaque)。
        /// ・lilToon(非Multi): 描画モードはシェーダー名の最後の '/' 以降に埋め込まれている
        ///   (Cutout / Transparent / Overlay の部分一致。lilToonのlilShaderUtilsと同じ流儀)。
        /// ・lilToonMulti: _TransparentMode で判定
        ///   (0=Opaque, 1=Cutout, 2=Transparent, 3=Refraction, 4=Fur, 5=FurCutout, 6=Gem)。
        /// ・Refraction / Gem / Fur は Opaque 扱い(非表示化せず、従来の変換警告に委ねる)。
        /// ・その他の汎用シェーダー: RenderTypeタグ(完全一致)と renderQueue >= 3000 で判定。
        ///   【重要】lilToonのアルファブレンド透過は RenderType "TransparentCutout" / queue 2460 のため、
        ///   汎用判定より先に必ずシェーダー名で判定すること(タグ・キュー判定では誤分類される)。
        /// </summary>
        public static TransparencyClass ClassifyTransparency(Material mat)
        {
            if (mat == null || mat.shader == null) return TransparencyClass.Opaque;

            string shaderName = mat.shader.name;
            if (IsLilToonShader(mat.shader))
            {
                int separator = shaderName.LastIndexOf('/');
                string tail = separator >= 0 ? shaderName.Substring(separator + 1) : shaderName;

                if (tail.IndexOf("Multi", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int mode = mat.HasProperty("_TransparentMode") ? Mathf.RoundToInt(mat.GetFloat("_TransparentMode")) : 0;
                    if (mode == 2) return TransparencyClass.Transparent;         // Transparent
                    if (mode == 1 || mode == 5) return TransparencyClass.Cutout; // Cutout / FurCutout
                    return TransparencyClass.Opaque;                             // Opaque / Refraction / Fur / Gem
                }
                // FurCutout等は Cutout を先に判定する(Cutoutはアルファテストのため非表示化しない)
                if (tail.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return TransparencyClass.Cutout;
                }
                if (tail.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tail.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return TransparencyClass.Transparent;
                }
                return TransparencyClass.Opaque;
            }

            // NonToon: 描画モードは _RenderingMode(Integer型)で決まる。
            // 【重要】NonToon の RenderType タグは両 SubShader で "Opaque" にハードコードされており、
            // 半透明(_RenderingMode=2)でもマテリアルキューは BIRP/VRChat では 2460(<3000)のため、
            // 下の汎用タグ/キュー判定では半透明を Opaque と誤分類する。必ず _RenderingMode で分類すること
            // (0=不透明 / 1=カットアウト / 2=半透明。lilToon をシェーダー名で先に判定するのと同じ理由)。
            if (IsNonToonShader(mat.shader))
            {
                int renderingMode = GetIntegerSafe(mat, NonToonRenderingModeProp, 0);
                if (renderingMode == 2) return TransparencyClass.Transparent;
                if (renderingMode == 1) return TransparencyClass.Cutout;
                return TransparencyClass.Opaque;
            }

            // Poiyomi: _Mode の序数は信頼できない(バージョン差)ため、実ブレンド設定で分類する
            // (ClassifyPoiyomiTransparency。詳細はそのメソッド参照)。汎用タグ/キュー判定より先に必ず判定すること。
            if (IsPoiyomiShader(mat.shader))
            {
                string decidedBy;
                return ClassifyPoiyomiTransparency(mat, out decidedBy);
            }

            // 汎用シェーダー: RenderTypeタグ(TransparentCutoutを先に判定)→ レンダーキュー
            string renderType = mat.GetTag("RenderType", false, string.Empty);
            if (string.Equals(renderType, "TransparentCutout", StringComparison.OrdinalIgnoreCase))
            {
                return TransparencyClass.Cutout;
            }
            if (string.Equals(renderType, "Transparent", StringComparison.OrdinalIgnoreCase) ||
                mat.renderQueue >= 3000)
            {
                return TransparencyClass.Transparent;
            }
            return TransparencyClass.Opaque;
        }

        /// <summary>
        /// Poiyomi マテリアルの透過分類を、_Mode の序数(バージョン差で信頼できない)に依存せず、
        /// 実際のブレンド設定・アルファテスト設定・タグ/キューの組み合わせで判定する。
        /// decidedBy にどのシグナルで決めたかを返す(レポート用)。判定順:
        ///   1. _SrcBlend/_DstBlend が古典アルファブレンド(SrcAlpha/OneMinusSrcAlpha)または
        ///      乗算済みアルファ(One/OneMinusSrcAlpha もしくは _AlphaPremultiply=1)→ Transparent
        ///   2. _DstBlend=One の加算ブレンド → Transparent(加算半透明として再現に回す)
        ///   3. 不透明ブレンド(One/Zero または未定義)かつ _AlphaToCoverage=1 か _Cutoff>0 → Cutout
        ///      (カットアウトはアルファテストで不透明相当のため、キューだけでは判定しない)
        ///   4. _ZWrite=0 → Transparent(透過寄り)
        ///   5. RenderType タグ(TransparentCutout→Cutout / Transparent→Transparent)
        ///   6. renderQueue>=3000 → Transparent(最後のフォールバック。カットアウト系はここまでで確定済み)
        ///   7. それ以外 → Opaque
        /// プロパティは全て HasProperty ガード。ロックで焼き込まれ欠落した場合は後段のフォールバックへ縮退する。
        /// </summary>
        public static TransparencyClass ClassifyPoiyomiTransparency(Material mat, out string decidedBy)
        {
            decidedBy = "不透明(透過シグナルなし)";
            if (mat == null || mat.shader == null) { decidedBy = "マテリアル無し"; return TransparencyClass.Opaque; }

            bool hasSrc = mat.HasProperty(PoiyomiSrcBlendProp);
            bool hasDst = mat.HasProperty(PoiyomiDstBlendProp);
            int src = hasSrc ? Mathf.RoundToInt(mat.GetFloat(PoiyomiSrcBlendProp)) : -1;
            int dst = hasDst ? Mathf.RoundToInt(mat.GetFloat(PoiyomiDstBlendProp)) : -1;
            bool premultiply = mat.HasProperty(PoiyomiAlphaPremultiplyProp) && mat.GetFloat(PoiyomiAlphaPremultiplyProp) > 0.5f;

            int srcAlpha = (int)UnityEngine.Rendering.BlendMode.SrcAlpha;
            int oneMinusSrcAlpha = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
            int one = (int)UnityEngine.Rendering.BlendMode.One;
            int zero = (int)UnityEngine.Rendering.BlendMode.Zero;

            if (hasSrc && hasDst)
            {
                bool classicBlend = src == srcAlpha && dst == oneMinusSrcAlpha;
                bool premulBlend = (src == one && dst == oneMinusSrcAlpha) || premultiply;
                if (classicBlend || premulBlend)
                {
                    decidedBy = premulBlend ? "_SrcBlend/_DstBlend(乗算済みアルファ)" : "_SrcBlend/_DstBlend(アルファブレンド)";
                    return TransparencyClass.Transparent;
                }
                bool additive = dst == one && src != zero;
                if (additive)
                {
                    decidedBy = "_SrcBlend/_DstBlend(加算)";
                    return TransparencyClass.Transparent;
                }
            }

            bool opaqueBlend = (!hasSrc || src == one) && (!hasDst || dst == zero);
            bool alphaToCoverage = mat.HasProperty(PoiyomiAlphaToCoverageProp) && mat.GetFloat(PoiyomiAlphaToCoverageProp) > 0.5f;
            float cutoff = mat.HasProperty(PoiyomiCutoffProp) ? mat.GetFloat(PoiyomiCutoffProp) : 0f;
            if (opaqueBlend && (alphaToCoverage || cutoff > 0f))
            {
                decidedBy = alphaToCoverage ? "_AlphaToCoverage(不透明ブレンド)" : "_Cutoff>0(不透明ブレンド)";
                return TransparencyClass.Cutout;
            }

            if (mat.HasProperty(PoiyomiZWriteProp) && Mathf.RoundToInt(mat.GetFloat(PoiyomiZWriteProp)) == 0)
            {
                decidedBy = "_ZWrite=0";
                return TransparencyClass.Transparent;
            }

            string renderType = mat.GetTag("RenderType", false, string.Empty);
            if (string.Equals(renderType, "TransparentCutout", StringComparison.OrdinalIgnoreCase))
            {
                decidedBy = "RenderTypeタグ(TransparentCutout)";
                return TransparencyClass.Cutout;
            }
            if (string.Equals(renderType, "Transparent", StringComparison.OrdinalIgnoreCase))
            {
                decidedBy = "RenderTypeタグ(Transparent)";
                return TransparencyClass.Transparent;
            }
            if (mat.renderQueue >= 3000)
            {
                decidedBy = "レンダーキュー>=3000";
                return TransparencyClass.Transparent;
            }
            return TransparencyClass.Opaque;
        }

        // ================================================================
        // 透過自動非表示(hideTransparentMaterials)の対象判定
        // ================================================================

        /// <summary>
        /// 透過自動非表示(hideTransparentMaterials)で非表示化してよいメッシュの三角形数の上限。
        /// これを超えるメッシュは頬染めクアッド等の小型オーバーレイではなく、
        /// 髪・衣装などの本体パーツとみなして非表示化せず不透明として変換する
        /// (アルファブレンドの髪がQuest版で丸ごと消える事故を防ぐ)。
        /// </summary>
        public const int AutoHideMaxTriangles = 600;

        /// <summary>IsHairLikeName が髪と判定するキーワード(部分一致・大文字小文字無視)。</summary>
        private static readonly string[] HairNameKeywords = { "hair", "髪", "ヘア", "前髪", "後髪" };

        /// <summary>
        /// IsHairLikeName が髪と判定する短いローマ字キーワード(単語一致・大文字小文字無視)。
        /// 部分一致だと "Bangle" や "Kamikaze" 等の無関係な名前に誤反応するため、
        /// 前後が英字でない位置での一致のみ髪とみなす("bang_hair" や "Kami01" は一致する)。
        /// </summary>
        private static readonly string[] HairNameWholeWordKeywords = { "bang", "bangs", "kami" };

        /// <summary>
        /// 名前が髪パーツを示すか(大文字小文字を区別しない)。
        /// GameObject名・メッシュ名・マテリアル名の判定に使い、一致した場合は
        /// 透過自動非表示(hideTransparentMaterials)の対象から外す(髪が消える事故を防ぐ)。
        /// 部分一致キーワード: hair / 髪 / ヘア / 前髪 / 後髪。
        /// 単語一致キーワード: bang / bangs / kami(前後が英字でない位置のみ)。null・空文字は false。
        /// </summary>
        public static bool IsHairLikeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string keyword in HairNameKeywords)
            {
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            foreach (string keyword in HairNameWholeWordKeywords)
            {
                if (ContainsWholeWord(name, keyword)) return true;
            }
            return false;
        }

        /// <summary>
        /// name の中に keyword が「単語」として含まれるか(前後が英字でない位置での一致のみ。大文字小文字無視)。
        /// 例: "front_bang" や "Bang" は "bang" に一致するが、"Bangle" は一致しない。
        /// </summary>
        private static bool ContainsWholeWord(string name, string keyword)
        {
            int start = 0;
            while (start <= name.Length - keyword.Length)
            {
                int index = name.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return false;
                bool headOk = index == 0 || !char.IsLetter(name[index - 1]);
                int end = index + keyword.Length;
                bool tailOk = end >= name.Length || !char.IsLetter(name[end]);
                if (headOk && tailOk) return true;
                start = index + 1;
            }
            return false;
        }

        // ================================================================
        // 表情デカール(透過オーバーレイ)の名前判定
        // ================================================================

        /// <summary>
        /// 表情のチーク・涙・アイハイライト等の透過デカール(顔の透過オーバーレイ)を
        /// 名前から判定するためのトークン(部分一致・大文字小文字を区別しない)。
        /// マテリアル名・メインテクスチャ名のいずれかにこれらが含まれれば
        /// デカールオーバーレイの候補とみなす(IsDecalOverlayName)。
        /// "alpha" は汎用トークンのため、髪のアルファテクスチャ等を誤検出しないよう
        /// 呼び出し側(AvatarQuestConverter.DetectExpressionDecals)は透過マテリアルかつ
        /// 髪マテリアルでない場合にのみ本判定を適用する。
        /// </summary>
        public static readonly string[] DecalOverlayNameTokens =
        {
            "alpha", "decal", "overlay", "_other", "face_other",
            "blush", "cheek", "頬", "チーク", "赤面", "照れ",
            "tear", "涙", "泣", "sweat", "汗",
            "catchlight", "catch_light", "kira", "キラ", "hi_light", "highlight",
            "front_shadow",
        };

        /// <summary>
        /// マテリアル名またはメインテクスチャ名が表情デカール(透過オーバーレイ)を示すか。
        /// DecalOverlayNameTokens のいずれかを部分一致(大文字小文字を区別しない)で含めば true。
        /// materialName・mainTexName はどちらも null/空を許容し、両方とも該当しなければ false。
        /// </summary>
        public static bool IsDecalOverlayName(string materialName, string mainTexName)
        {
            return ContainsAnyToken(materialName, DecalOverlayNameTokens) ||
                   ContainsAnyToken(mainTexName, DecalOverlayNameTokens);
        }

        /// <summary>name が tokens のいずれかを部分一致(大文字小文字を区別しない)で含むか。null/空は false。</summary>
        private static bool ContainsAnyToken(string name, string[] tokens)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string token in tokens)
            {
                if (!string.IsNullOrEmpty(token) && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ================================================================
        // 半透明再現(Emulate)のオーバーレイ種別トークン
        // ================================================================

        /// <summary>
        /// 半透明再現(Emulate)で乗算(Multiply)にすべき「暗くする/減光系」オーバーレイを示すトークン
        /// (部分一致・大文字小文字を区別しない)。影・デカール・頬染め・レース・ストッキング・ベール等。
        /// 乗算は背景へ色を掛け合わせる描画のため、影・網タイツ等の暗い半透明表現の近似に向く。
        /// </summary>
        public static readonly string[] OverlayMultiplyTokens =
        {
            "shadow", "影", "シャドウ", "kage",
            "decal", "デカール",
            "cheek", "チーク", "頬", "blush",
            "lace", "レース",
            "stocking", "ストッキング", "tights", "タイツ", "socks", "靴下",
            "veil", "ベール",
        };

        /// <summary>
        /// 半透明再現(Emulate)で加算(Additive)にすべき「明るくする/発光系」オーバーレイを示すトークン
        /// (部分一致・大文字小文字を区別しない)。涙・ハイライト・キャッチライト・ラメ・星等。
        /// 加算は背景へ光を足す描画のため、涙のテカリ・目のキャッチライト等の明るい半透明表現の近似に向く。
        /// </summary>
        public static readonly string[] OverlayAdditiveTokens =
        {
            "tear", "涙", "namida",
            "highlight", "ハイライト", "catchlight", "キャッチライト",
            "glow", "sparkle", "ラメ", "キラ", "star", "星",
        };

        /// <summary>半透明再現の乗算/加算の名前判定結果。</summary>
        public enum OverlayEmulationMode
        {
            /// <summary>名前からは判定できない(呼び出し側は輝度等でフォールバック判定する)。</summary>
            None = 0,
            /// <summary>乗算(暗い半透明: 影・レース・ストッキング等)。</summary>
            Multiply = 1,
            /// <summary>加算(明るい半透明: 涙・ハイライト等)。</summary>
            Additive = 2,
        }

        /// <summary>
        /// マテリアル名から半透明再現の乗算/加算を判定する(部分一致・大文字小文字を区別しない)。
        /// 乗算トークンを先に判定する(「shadow_highlight」のような複合名では暗い側=乗算を優先。
        /// 影を誤って加算にすると暗所で光って見える事故になるため、乗算へバイアスする)。
        /// どちらのトークンも含まなければ None。
        /// </summary>
        public static OverlayEmulationMode ClassifyOverlayEmulation(string name)
        {
            if (ContainsAnyToken(name, OverlayMultiplyTokens)) return OverlayEmulationMode.Multiply;
            if (ContainsAnyToken(name, OverlayAdditiveTokens)) return OverlayEmulationMode.Additive;
            return OverlayEmulationMode.None;
        }

        /// <summary>マテリアル名がオーバーレイ種別トークン(乗算/加算いずれか)を含むか。null/空は false。</summary>
        public static bool IsOverlayEmulationName(string name)
        {
            return ClassifyOverlayEmulation(name) != OverlayEmulationMode.None;
        }

        // ================================================================
        // 効果専用(ベース面なし)の補助 lilToon [Optional] シェーダーの判定
        // ================================================================

        /// <summary>
        /// 疑似影(FakeShadow / 前髪影)を示すマテリアル名フォールバックトークン(部分一致・大文字小文字無視)。
        /// シェーダー名で拾えない(名称違い・差し替え済み)場合の保険。トークンは疑似影に特化して具体的
        /// (fakeshadow / fake_shadow / 疑似影)にし、見えている通常マテリアルを誤って隠さないようにする。
        /// </summary>
        public static readonly string[] FakeShadowNameTokens = { "fakeshadow", "fake_shadow", "疑似影" };

        /// <summary>
        /// 効果専用の補助 lilToon [Optional] シェーダー(ベース面を持たない演出パス)の種別。
        /// Quest ではステンシル・カスタムシェーダーが使えず再現できない。さらに ClassifyTransparency は
        /// シェーダー名末尾のトークンだけを見るため、これらは透過タグを持たず Opaque と誤分類され、
        /// 近似ベイクすると _Color(多くは near-white)の不透明面としてベイクされてしまう(白い髪バグ)。
        /// そのため常に非表示化する。
        /// </summary>
        public enum OverlayOnlyShaderKind
        {
            /// <summary>効果専用ではない(通常変換する)。</summary>
            None = 0,
            /// <summary>疑似影(_lil/[Optional] lilToonFakeShadow)。ステンシル+乗算で投影する前髪影。</summary>
            FakeShadow = 1,
            /// <summary>アウトラインのみ(_lil/[Optional] lilToonOutlineOnly*)。反転ハルの輪郭専用パス。</summary>
            OutlineOnly = 2,
        }

        /// <summary>
        /// マテリアルが効果専用の補助 lilToon [Optional] シェーダー(FakeShadow / OutlineOnly 系)か判定して種別を返す。
        /// 判定はシェーダー名(FakeShadow / OutlineOnly を含む。OutlineOnlyCutout / OutlineOnlyTransparent も含む)を
        /// 主とし、シェーダー名で拾えない場合はマテリアル名フォールバック(FakeShadowNameTokens)で疑似影を拾う。
        /// shader・materialName いずれも null/空を許容する。
        /// </summary>
        public static OverlayOnlyShaderKind ClassifyOverlayOnlyShader(Shader shader, string materialName)
        {
            string shaderName = shader != null ? shader.name : string.Empty;
            if (!string.IsNullOrEmpty(shaderName))
            {
                // OutlineOnly を先に判定する(OutlineOnly* は Cutout/Transparent 変種も輪郭のみで、ベース面が無い)。
                if (shaderName.IndexOf("OutlineOnly", StringComparison.OrdinalIgnoreCase) >= 0)
                    return OverlayOnlyShaderKind.OutlineOnly;
                if (shaderName.IndexOf("FakeShadow", StringComparison.OrdinalIgnoreCase) >= 0)
                    return OverlayOnlyShaderKind.FakeShadow;
            }
            // シェーダー名で拾えない場合のマテリアル名フォールバック(疑似影のみ。トークンは具体的で誤検出しにくい)。
            if (ContainsAnyToken(materialName, FakeShadowNameTokens))
                return OverlayOnlyShaderKind.FakeShadow;
            return OverlayOnlyShaderKind.None;
        }

        /// <summary>
        /// マテリアルが効果専用の補助 lilToon [Optional] シェーダー(FakeShadow / OutlineOnly 系)か。
        /// ClassifyOverlayOnlyShader != None のショートカット。
        /// </summary>
        public static bool IsOverlayOnlyShader(Shader shader, string materialName)
        {
            return ClassifyOverlayOnlyShader(shader, materialName) != OverlayOnlyShaderKind.None;
        }

        // ================================================================
        // PhysBone 優先度(自動選択のスコアリング)
        // ================================================================

        /// <summary>
        /// PhysBone自動選択の優先度キーワード(部分一致・大文字小文字無視)。
        /// 配列の添字が優先度で、小さいほど高優先(=Poor上限8を埋めるとき先に残す揺れもの)。
        ///
        /// 【段階順(earlier = higher priority)】
        ///   1) 前髪・アホ毛(顔まわりで動きが最も目立つ) → 2) サイド → 3) 後ろ髪 →
        ///   4) 汎用の髪(具体的な部位が名前から判別できないもの) → 5) アクセサリ(胸・尻尾・耳・スカート)。
        /// 髪は必ずアクセサリより上位に置く。以前は髪が優先されず、Quest変換で髪PhysBoneが
        /// Poor上限超過分として削られて「カチカチ」に硬直する不具合があったための並び。
        ///
        /// GetPhysBonePriorityScore が PhysBoneの名前(GameObject名・チェーンルート名・
        /// 共通親名・メンバー名など)に対し先頭から走査し、最初に部分一致したキーワードの添字を返す。
        /// このため具体的な髪の段(前髪等)は必ず汎用の "hair"/"髪" より前に並べ、
        /// 具体一致が汎用一致より小さい添字(高優先)を勝ち取るようにしている。
        ///
        /// 実際のボーン名はセパレータが多様(Hair_Front / HairFront / Hair Front / 前髪 …)なため、
        /// 各段は "hair" と方向語を隣接させた3表記(空白 / アンダースコア / 連結)を英日両方で用意する。
        /// 素の "front"/"side"/"back" は使わない(Skirt_Front など衣装パーツが髪より上位に化けるのを防ぐ)。
        /// </summary>
        public static readonly string[] PhysBonePriorityKeywords =
        {
            // 1) 前髪(bangs)— 顔を縁取り最も動きが目立つ。最優先。
            "hair front", "hair_front", "hairfront", "front hair", "front_hair", "fronthair",
            "前髪", "bang", "bangs",
            // 2) アホ毛(ahoge / antenna)
            "ahoge", "アホ毛", "あほ毛", "あほげ", "antenna",
            // 3) サイド(横髪・もみあげ)
            "hair side", "hair_side", "hairside", "side hair", "side_hair", "sidehair",
            "サイド", "横髪", "もみあげ",
            // 4) 後ろ髪(ポニテ・ツインテ・おさげ等も含む)
            "hair back", "hair_back", "hairback", "back hair", "back_hair", "backhair",
            "後ろ髪", "後髪", "うしろ", "ponytail", "ポニテ", "twintail", "ツインテ", "おさげ", "braid",
            // 5) 汎用の髪(部位が判別できない髪。必ず具体的な髪の段より後ろ)
            "hair", "髪", "ヘア",
            // 6) アクセサリ(髪より下位。胸・尻尾・耳・スカート)
            "breast", "胸", "むね", "bust", "tail", "しっぽ", "尻尾", "テール",
            "ear", "耳", "みみ", "skirt", "スカート",
        };

        /// <summary>
        /// name に最初に一致する PhysBonePriorityKeywords の添字(=優先度。小さいほど高優先)を返す。
        /// どのキーワードにも一致しなければ int.MaxValue。判定は部分一致・大文字小文字無視。
        /// 配列は優先度順に並んでいるため、先頭から走査して最初の一致を返せば最良(最小)添字になる。
        /// </summary>
        public static int GetPhysBonePriorityScore(string name)
        {
            if (string.IsNullOrEmpty(name)) return int.MaxValue;
            for (int i = 0; i < PhysBonePriorityKeywords.Length; i++)
            {
                if (name.IndexOf(PhysBonePriorityKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }
            return int.MaxValue;
        }

        // ================================================================
        // マテリアル別の変換方法上書き
        // ================================================================

        /// <summary>
        /// settings.materialOverrides(GUID参照)を実際のMaterial参照へ解決した辞書を返す。
        /// GUIDが空・アセットが見つからない・マテリアル以外のアセットを指す等、
        /// 解決できないエントリは黙ってスキップする。
        /// 同一マテリアルへの重複指定はリスト後方の項目が優先される(後勝ち)。
        /// </summary>
        public static Dictionary<Material, MaterialOverrideEntry> ResolveOverrides(QuestConvertSettings settings)
        {
            var result = new Dictionary<Material, MaterialOverrideEntry>();
            if (settings == null || settings.materialOverrides == null) return result;

            foreach (MaterialOverrideEntry entry in settings.materialOverrides)
            {
                if (entry == null || string.IsNullOrEmpty(entry.materialGuid)) continue;

                string path = AssetDatabase.GUIDToAssetPath(entry.materialGuid);
                if (string.IsNullOrEmpty(path)) continue; // GUIDが解決できない(削除済み等)

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) continue; // マテリアル以外のアセット・ロード失敗

                result[material] = entry;
            }
            return result;
        }

        // ================================================================
        // Quest除外(EditorOnly)関連
        // ================================================================

        /// <summary>VRChatのビルドから除外されるGameObjectのタグ(Unity組み込みタグのため常に定義済み)。</summary>
        public const string EditorOnlyTag = "EditorOnly";

        /// <summary>
        /// 自身または祖先(親がnullになるまで遡る)にEditorOnlyタグが付いているか。
        /// EditorOnlyのサブツリーはVRChatビルドでアバターから除去される。
        /// </summary>
        public static bool IsEditorOnly(Transform t)
        {
            Transform current = t;
            while (current != null)
            {
                if (current.CompareTag(EditorOnlyTag)) return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// rootからtargetへのスラッシュ区切り相対パスを返す。
        /// target == root なら空文字、targetがroot配下でない(またはnull)場合はnullを返す。
        /// </summary>
        public static string GetRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null) return null;
            if (target == root) return string.Empty;

            var names = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            if (current != root) return null; // root配下ではない
            names.Reverse();
            return string.Join("/", names);
        }

        /// <summary>
        /// スラッシュ区切りの相対パスでroot配下のTransformを探す(非アクティブ含む)。
        /// 空文字・空白のみならroot自身、rootがnull・パスが見つからない場合はnullを返す。
        /// </summary>
        public static Transform FindByPath(Transform root, string relativePath)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(relativePath)) return root;

            string normalized = relativePath.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0) return root;
            return root.Find(normalized); // Transform.Findはスラッシュ区切りパスを解決できる(見つからなければnull)
        }

        /// <summary>
        /// Androidアバターで削除対象となるコンポーネント型名。
        /// 依存関係(Joint→Rigidbody、AudioListener/FlareLayer→Camera)を満たす削除順に並んでいる。
        /// この順で DestroyImmediate すること。
        /// </summary>
        public static readonly string[] UnsupportedComponentTypeNames =
        {
            // サードパーティ(入っていない場合は単に解決失敗して無視される)
            "DynamicBone",
            "DynamicBoneCollider",
            "DynamicBoneColliderBase",
            "ONSPAudioSource",
            // Unity物理(JointはRigidbodyに依存するため先に削除)
            "UnityEngine.Joint",
            "UnityEngine.Cloth",
            "UnityEngine.Collider",
            "UnityEngine.Rigidbody",
            // カメラ依存物を先に
            "UnityEngine.AudioListener",
            "UnityEngine.FlareLayer",
            "UnityEngine.Camera",
            "UnityEngine.Light",
            // VRCSpatialAudioSourceはRequireComponentでAudioSourceを要求するため先に削除
            "VRC.SDK3.Avatars.Components.VRCSpatialAudioSource",
            "UnityEngine.AudioSource",
            // Unityコンストレイント(VRCConstraintへの変換後に残ったもの)
            "UnityEngine.Animations.PositionConstraint",
            "UnityEngine.Animations.RotationConstraint",
            "UnityEngine.Animations.ScaleConstraint",
            "UnityEngine.Animations.ParentConstraint",
            "UnityEngine.Animations.AimConstraint",
            "UnityEngine.Animations.LookAtConstraint",
        };

        /// <summary>FinalIK系はこの名前空間プレフィックスで判定して削除する。</summary>
        public const string FinalIKNamespacePrefix = "RootMotion.FinalIK";

        private static Dictionary<string, Type> _typeCache;

        /// <summary>ロード済み全アセンブリから型名で型を解決する(見つからなければnull)。</summary>
        public static Type FindType(string fullName)
        {
            if (_typeCache == null) _typeCache = new Dictionary<string, Type>();
            Type cached;
            if (_typeCache.TryGetValue(fullName, out cached)) return cached;

            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = asm.GetType(fullName, false);
                if (found != null) break;
            }
            if (found == null && fullName.IndexOf('.') < 0)
            {
                // 単純名のみ(DynamicBone等)は全型走査で解決
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (System.Reflection.ReflectionTypeLoadException) { continue; }
                    found = types.FirstOrDefault(t => t.Name == fullName);
                    if (found != null) break;
                }
            }
            _typeCache[fullName] = found;
            return found;
        }

        /// <summary>root配下(非アクティブ含む)からAndroid非対応コンポーネントを削除順で列挙する。</summary>
        public static List<Component> FindUnsupportedComponents(GameObject root)
        {
            var result = new List<Component>();
            foreach (var typeName in UnsupportedComponentTypeNames)
            {
                var type = FindType(typeName);
                if (type == null) continue;
                foreach (var c in root.GetComponentsInChildren(type, true))
                {
                    if (c != null && !result.Contains(c)) result.Add(c);
                }
            }
            // FinalIK(名前空間判定)
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var ns = c.GetType().Namespace;
                if (!string.IsNullOrEmpty(ns) && ns.StartsWith(FinalIKNamespacePrefix, StringComparison.Ordinal) && !result.Contains(c))
                {
                    result.Add(c);
                }
            }
            return result;
        }

        /// <summary>
        /// root配下のEditorOnlyタグ付きサブツリーを破棄する(VRChatビルド時の除去に合わせる)。
        /// 必ず一時複製に対して呼ぶこと(元/出力アバターを渡すと本体が変更される)。
        /// </summary>
        public static void StripEditorOnlySubtrees(GameObject root)
        {
            if (root == null) return;
            var toDelete = new List<GameObject>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.CompareTag(EditorOnlyTag)) toDelete.Add(t.gameObject);
            }
            foreach (GameObject go in toDelete)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go); // 親を先に消した場合、子はUnityのnull比較でスキップされる
            }
        }

        /// <summary>
        /// VRC.Dynamics.VRCConstraintManager.Sdk_ManuallyRefreshGroups(VRCConstraintBase[]) をリフレクションで呼び、
        /// パフォーマンス計測前にコンストレイントのグループ計上を正す(型がinternalのため直接参照できない)。
        /// これを行わないと CalculatePerformanceStats のコンストレイント数がやや不正確になる。
        /// メソッドが見つからない場合は続行する。
        /// </summary>
        public static void RefreshConstraintGroups(GameObject root)
        {
            if (root == null) return;
            var constraints = root.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true);
            if (constraints == null || constraints.Length == 0) return;

            var managerType = FindType("VRC.Dynamics.VRCConstraintManager");
            var method = managerType != null
                ? managerType.GetMethod(
                    "Sdk_ManuallyRefreshGroups",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(VRC.Dynamics.VRCConstraintBase[]) },
                    null)
                : null;
            if (method != null)
            {
                try { method.Invoke(null, new object[] { constraints }); }
                catch (Exception ex) { Debug.LogWarning("[RARA] コンストレイントグループの更新に失敗しました: " + ex.Message); }
            }
        }
    }

    /// <summary>アセット出力の共通ユーティリティ。</summary>
    public static class QuestConverterUtility
    {
        /// <summary>"Assets/x/y/z" 形式のフォルダを(親から順に)作成して確定させる。</summary>
        public static void EnsureFolder(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath)) return;
            var parts = assetFolderPath.Replace('\\', '/').Split('/');
            var current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        /// <summary>アセット名に使えない文字を除去する。</summary>
        public static string SanitizeAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Asset";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        /// <summary>重複しないアセットパスを返す。</summary>
        public static string UniqueAssetPath(string path)
        {
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }
    }

    /// <summary>
    /// 生成アセットを「GUIDを保持したまま」保存するためのユーティリティ。
    ///
    /// 【背景】以前は変換のたびに出力フォルダを丸ごと削除して作り直していたため、
    /// 生成アセットのGUIDが毎回変わり、
    /// ・シーンに残っている前回の _Quest アバターのマテリアル参照
    /// ・ユーザーが元アバターへ手動で割り当てた生成マテリアル(Body_Quest 等)
    /// が2回目の変換で Missing / InternalErrorShader になって壊れていた。
    ///
    /// 本クラスの SaveOrOverwrite 系は、既存アセットがあれば EditorUtility.CopySerialized で
    /// 「中身だけ」を上書きしてGUIDを保持し、無ければ新規作成する。パスの安定性は
    /// StablePathRegistry(実行間で同じ入力なら同じパスを払い出す)とセットで担保する。
    /// </summary>
    public static class QuestAssetPersistence
    {
        /// <summary>
        /// マテリアルを assetPath へ保存する。既存アセットがあれば CopySerialized で内容だけを
        /// 上書きしてGUIDを保持し(2回目変換で旧参照を壊さない)、無ければ新規作成する。
        /// 返り値は保存済みインスタンス(既存があれば既存側。newContent が未アセットなら破棄される)。
        /// </summary>
        public static Material SaveOrOverwriteMaterial(Material newContent, string assetPath)
        {
            return SaveOrOverwrite(newContent, assetPath);
        }

        /// <summary>
        /// メッシュを assetPath へ保存する。既存アセットがあれば CopySerialized で内容だけを
        /// 上書きしてGUIDを保持し(2回目変換で旧参照を壊さない)、無ければ新規作成する。
        /// 返り値は保存済みインスタンス(既存があれば既存側。newContent が未アセットなら破棄される)。
        /// </summary>
        public static Mesh SaveOrOverwriteMesh(Mesh newContent, string assetPath)
        {
            return SaveOrOverwrite(newContent, assetPath);
        }

        /// <summary>
        /// アニメーションクリップを assetPath へ保存する。既存アセットがあれば CopySerialized で
        /// 内容だけを上書きしてGUIDを保持し(2回目変換で旧参照を壊さない)、無ければ新規作成する。
        /// 返り値は保存済みインスタンス(既存があれば既存側。newContent が未アセットなら破棄される)。
        /// </summary>
        public static AnimationClip SaveOrOverwriteClip(AnimationClip newContent, string assetPath)
        {
            return SaveOrOverwrite(newContent, assetPath);
        }

        /// <summary>
        /// SaveOrOverwrite 系の共通実装。
        /// ・既存アセットあり: EditorUtility.CopySerialized(newContent → 既存)で中身だけ差し替え、
        ///   GUID・ファイルを維持する。メモリ上の newContent は(アセットでなければ)破棄し、
        ///   既存側を SetDirty して返す。
        /// ・既存アセットなし: 親フォルダを作成して AssetDatabase.CreateAsset し、newContent を返す。
        /// newContent が null・assetPath が空の場合は何もせず newContent をそのまま返す。
        /// </summary>
        private static T SaveOrOverwrite<T>(T newContent, string assetPath) where T : UnityEngine.Object
        {
            if (newContent == null || string.IsNullOrEmpty(assetPath)) return newContent;

            string normalized = assetPath.Replace('\\', '/');
            T existing = AssetDatabase.LoadAssetAtPath<T>(normalized);
            if (existing != null)
            {
                EditorUtility.CopySerialized(newContent, existing);
                if (!AssetDatabase.Contains(newContent))
                {
                    UnityEngine.Object.DestroyImmediate(newContent);
                }
                EditorUtility.SetDirty(existing);
                return existing;
            }

            int slash = normalized.LastIndexOf('/');
            if (slash > 0)
            {
                QuestConverterUtility.EnsureFolder(normalized.Substring(0, slash));
            }
            AssetDatabase.CreateAsset(newContent, normalized);
            return newContent;
        }

        /// <summary>
        /// 実行間で安定したアセットパスを払い出すレジストリ(変換1回につき1インスタンス作る)。
        /// AssetDatabase.GenerateUniqueAssetPath は「既存アセットの有無」で連番を振るため
        /// 実行のたびにパスが変わってしまう。本クラスは既存アセットを一切参照せず、
        /// 「この実行内で払い出した名前」だけで衝突を判定するため、同じマテリアル構成で
        /// 変換し直せば毎回同じパスになる(→ SaveOrOverwrite 系がGUIDを保持して上書きできる)。
        /// 同名衝突(同名マテリアルが複数ある等)は Claim を呼んだ順に _2, _3... を
        /// 決定的に付与して解決する(収集順が同じなら実行間でも同じ連番になる)。
        /// </summary>
        public sealed class StablePathRegistry
        {
            /// <summary>この実行内で払い出し済みのパス(Windowsのファイル系に合わせ大文字小文字は区別しない)。</summary>
            private readonly HashSet<string> _claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// desiredPath を払い出す。初回はそのまま返し、実行内で重複した場合は
            /// 拡張子の前に _2, _3... を付けた最初の空き名を返す。
            /// GenerateUniqueAssetPath は使わない(実行間でパスが安定しなくなるため)。
            /// null・空文字はそのまま返す。
            /// </summary>
            public string Claim(string desiredPath)
            {
                if (string.IsNullOrEmpty(desiredPath)) return desiredPath;

                string normalized = desiredPath.Replace('\\', '/');
                if (_claimed.Add(normalized)) return normalized;

                int slash = normalized.LastIndexOf('/');
                int dot = normalized.LastIndexOf('.');
                bool hasExtension = dot > slash; // 先頭ドット等は拡張子とみなさない
                string stem = hasExtension ? normalized.Substring(0, dot) : normalized;
                string extension = hasExtension ? normalized.Substring(dot) : string.Empty;
                for (int i = 2; ; i++)
                {
                    string candidate = stem + "_" + i + extension;
                    if (_claimed.Add(candidate)) return candidate;
                }
            }
        }
    }
}
#endif
