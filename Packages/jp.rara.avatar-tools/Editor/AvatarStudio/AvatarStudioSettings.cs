// RARA アバター軽量化・Quest/iOS対応ツール(AvatarStudio)- 統合設定モデル
// VRChat Avatars SDK 3.10.4 / Unity 2022.3.22f1 向け。Assembly-CSharp-Editor でコンパイルされる(asmdefなし)。
//
// このファイルは実装者A(設定/マッピング/移行)が所有する「ピン留め契約」の中核である。
// 実装者B(診断/プレビューパネル)・実装者C(ウィンドウ/実行)は本ファイルの型・フィールド名に
// そのままコードを書くため、公開名は不用意に変更しないこと。
//
// 【設計方針】
//  ・旧2ツール(RARA.PCOptimizer / RARA.QuestConverter)は同一アセンブリのため、そのフィールドの
//    CLR型(ToggleGroupChoice / TextureSizePlanEntry / MaterialOverrideEntry / PolygonPlanEntryData /
//    SkinnedMeshMergeMode / QuestShaderTarget / TransparentHandling / PCTargetRank …)を直接再利用する。
//  ・「共有ブロック」の各フィールドは AvatarStudioMapping で PC/Quest 双方の設定へ「同一List参照」で
//    配られる(1回の編集が両ターゲットへ同時に効く)。パイプライン(Optimize/Convert)は設定を読み取り
//    専用として扱う(リストへの追加・削除は行わない)ことを確認済みのため、参照共有は安全。
//  ・JsonUtility でそのまま往復できるよう、public フィールドのみ・列挙はint化・参照はGUID/相対パスで持つ。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;   // TextureImporterFormat(questAndroidFormat の型)
using UnityEngine;
using RARA.PCOptimizer;
using RARA.QuestConverter;

namespace RARA.AvatarStudio
{
    /// <summary>
    /// Quest(Android)側の目標パフォーマンスランクを表す「インデックスの意味」だけを与える補助列挙。
    /// 実フィールド <see cref="AvatarStudioSettings.questGoalRank"/> は旧 QuestConverter ウィンドウの
    /// 慣習(素の int: 0=Excellent..3=Poor、EditorPrefs "RARA.QuestConverter.TargetRank")に合わせて
    /// int 型で保持する(診断パネルが GUILayout.Toolbar のインデックスとして読み書きするため)。
    /// この列挙は既定値やコードの可読性のために <c>(int)QuestTargetRank.Medium</c> のように使う。
    /// PC側の <see cref="PCTargetRank"/> と並びを揃えてある(Excellent が最良)。
    /// </summary>
    public enum QuestTargetRank
    {
        /// <summary>最良(最も厳しい上限)。</summary>
        Excellent = 0,
        /// <summary>推奨上位。</summary>
        Good = 1,
        /// <summary>Questの既定表示ランク(既定)。</summary>
        Medium = 2,
        /// <summary>最低ライン(これを超えると Very Poor。既定で非表示になりやすく揺れ物も停止し得るが、アップロード可否はサイズのみで決まる)。</summary>
        Poor = 3,
    }

    /// <summary>
    /// 統合ウィンドウの全設定。1アバターにつき1件、EditorPrefs に JSON で永続化される
    /// (キー生成・保存は <see cref="AvatarStudioSettingsIO"/>)。
    ///
    /// フィールドは3ブロックに分かれる:
    ///  1) ターゲット選択(PC/Quest どちらを実行するか。既定は両方)
    ///  2) 共有ブロック(トグル整理・SkinnedMesh統合・PhysBone整理など、PC/Questで同じ設定を使う項目)
    ///  3) PCブロック / Questブロック(各ターゲット固有の項目。フィールド名は pc* / quest* で明示)
    ///
    /// マッピング(<see cref="AvatarStudioMapping"/>)は共有ブロックのList参照を PC/Quest 双方の設定へ
    /// そのまま渡す(コピーしない)。scalar はコピーする。
    /// partial なのは、同名の変換メソッド(BuildPCOptimizeSettings/BuildQuestConvertSettings の
    /// インスタンス版)を AvatarStudioMapping.cs 側に置くため。
    /// </summary>
    [Serializable]
    public partial class AvatarStudioSettings
    {
        // ============================================================
        // 1) ターゲット選択(既定=両方 PC+Quest)
        // ============================================================

        [Tooltip("PC(Windows)軽量化を実行対象に含める。既定オン(両対応)")]
        public bool targetPC = true;

        [Tooltip("Quest/iOS(Android)変換を実行対象に含める。既定オン(両対応)")]
        public bool targetQuest = true;

        // ============================================================
        // 2) 共有ブロック(PC/Quest 双方へ同一List参照で配られる)
        //    ここを編集すると PC・Quest の両パイプラインに同時に効く。
        // ============================================================

        [Tooltip("衣装・トグルグループごとの固定方法(維持/常時表示/非表示除去)。常時表示・非表示にするとトグルが解消され、AAOがメッシュ・マテリアルスロットを統合できるようになる(PC・Quest双方に効く)")]
        public List<ToggleGroupChoice> toggleChoices = new List<ToggleGroupChoice>();

        [Tooltip("SkinnedMeshの統合方法。しない=従来どおり / 顔以外を統合=顔(ビセーム/まばたき)以外の全SkinnedMeshRendererを1つへ統合しSMR数・スロット数を削減(推奨)。新規既定は MergeExceptFace")]
        public SkinnedMeshMergeMode mergeSkinnedMeshesMode = SkinnedMeshMergeMode.MergeExceptFace;

        [Tooltip("SkinnedMesh統合から個別に除外するレンダラーの相対パス(プレビューで「統合しない」を選んだもの)")]
        public List<string> skinnedMeshMergeOptOutPaths = new List<string>();

        [Tooltip("設定が一致する兄弟PhysBoneチェーンを1つへマージし、揺れを維持したままコンポーネント数を削減する")]
        public bool mergePhysBones = true;

        [Tooltip("設定が異なる兄弟チェーンも先頭の設定に統一してマージする(揺れ方が先頭チェーンに揃う)")]
        public bool physBoneLooseMerge = true;

        // 【PhysBone識別パスの規則】ComponentRemover.GetPhysBoneIdentityPath 準拠のスラッシュ区切り相対パス
        //  (同一GameObjectに複数PhysBoneがある場合のみ末尾 "#index")。手書きせず、プレビュー選択から生成する。
        [Tooltip("OptInで残すPhysBoneの識別パス(空でなければ、ここに無いPhysBoneはマージ前に削除される)。PC・Quest共通")]
        public List<string> physBoneKeepPaths = new List<string>();

        [Tooltip("削除指定するPhysBoneの識別パス(physBoneKeepPaths が空のときに適用)。PC・Quest共通")]
        public List<string> physBoneRemovePaths = new List<string>();

        [Tooltip("AAOのTrace and Optimizeが無ければ複製へ追加し、ビルド時のメッシュ/スロット統合・未使用ボーン削減を有効にする")]
        public bool ensureTraceAndOptimize = true;

        // ============================================================
        // 3a) PCブロック(PC軽量化固有)
        // ============================================================

        [Tooltip("目標にするPC(Windows)パフォーマンスランク。既定は Good")]
        public PCTargetRank pcTargetRank = PCTargetRank.Good;

        [Tooltip("最適化後の複製をプレファブ(_Opt.prefab)としても保存する(非破壊: 元アバターは無改変)")]
        public bool savePrefab = true;

        [Tooltip("PC: 互換マテリアルを1枚のアトラスへ統合し、マテリアルスロット数とテクスチャメモリを削減する")]
        public bool pcEnableAtlas = true;

        [Tooltip("PC: アトラステクスチャの最大サイズ(px)")]
        public int pcAtlasMaxSize = 2048;

        [Tooltip("PC: テクスチャ無し(色のみ)マテリアルの色をアトラスの単色セルとしてベイクし、統合後も色を保つ")]
        public bool pcAtlasColorOnlyMaterials = true;

        [Tooltip("PC: エミッション色/マップをエミッション用アトラスへベイクする")]
        public bool pcAtlasBakeEmissionMask = true;

        [Tooltip("PC: カリング(片面/両面)の違いを無視して統合する(統合後は両面描画になる)")]
        public bool pcAtlasIgnoreCull = true;

        [Tooltip("PC: アトラス統合時のアウトライン(輪郭線)の扱い。しない / アウトラインを外して統合(推奨) / アウトライン付きに統一(瞳・顔は自動回避)")]
        public OutlineUnifyMode pcAtlasOutlineUnifyMode = OutlineUnifyMode.しない;

        [Tooltip("PC: アトラス統合から除外するマテリアルのアセットGUID")]
        public List<string> pcAtlasExcludeGuids = new List<string>();

        [Tooltip("PC: 縮小コピーを生成するテクスチャの計画(元テクスチャは無改変)")]
        public List<TextureSizePlanEntry> pcTexturePlan = new List<TextureSizePlanEntry>();

        // ============================================================
        // 3b) Questブロック(Quest/iOS変換固有)
        // ============================================================

        // 診断パネル(実装者B)が GUILayout.Toolbar のインデックス(0=Excellent..3=Poor)として
        // 直接読み書きするため、型は int。意味は QuestTargetRank を参照。既定は 2(Medium)。
        [Tooltip("目標にするQuest(Android)パフォーマンスランクのインデックス(0=Excellent..3=Poor)。既定は 2=Medium")]
        public int questGoalRank = (int)QuestTargetRank.Medium;

        [Tooltip("変換先のQuest対応シェーダー。ToonStandard=影ランプ・ノーマル・エミッション対応(推奨) / ToonLit=最軽量")]
        public QuestShaderTarget shaderTarget = QuestShaderTarget.ToonStandard;

        [Tooltip("半透明(アルファブレンド)マテリアルのQuest版での扱い。Emulate=乗算/加算で半透明を自動再現(推奨) / Hide=非表示(最軽量) / Opaque=不透明化")]
        public TransparentHandling transparentHandling = TransparentHandling.Emulate;

        [Tooltip("マテリアル別の変換方法指定(Auto/ToonStandard/ToonLit/加算/乗算/非表示/そのまま + アトラス除外)")]
        public List<MaterialOverrideEntry> materialOverrides = new List<MaterialOverrideEntry>();

        [Tooltip("Quest: 互換マテリアルを1枚のアトラスに統合しスロット数とテクスチャメモリを削減(実験的)")]
        public bool questEnableAtlas = false;

        [Tooltip("Quest: アトラステクスチャの最大サイズ")]
        public int questAtlasMaxSize = 2048;

        [Tooltip("Quest: アトラス統合時、影ランプの違いを無視してグループ化する(スロットを大きく削減できる)")]
        public bool questAtlasUnifyRamps = true;

        [Tooltip("Quest: 変換時にこのサイズへ縮小したコピーを生成するテクスチャの計画。元テクスチャは変更しない")]
        public List<TextureSizePlanEntry> questTextureSizePlan = new List<TextureSizePlanEntry>();

        [Tooltip("Quest版でEditorOnly化(ビルド除外)するオブジェクトの相対パス。PC版には影響しない")]
        public List<string> questExcludePaths = new List<string>();

        [Tooltip("Quest: 服の下に隠れる肌などをAAOのブレンドシェイプ削除で消す(shrinkブレンドシェイプ検出時。見えない部分のみ)")]
        public bool questRemoveHiddenMeshByBlendShape = false;

        [Tooltip("Quest: ブレンドシェイプ削除を適用するレンダラーの相対パス(検出された隠し/縮小ブレンドシェイプから選択したもの)")]
        public List<string> questHiddenMeshRendererPaths = new List<string>();

        [Tooltip("Quest: ポリゴン削減(メッシュ簡略化)を有効にする。目標ポリゴン数に収まるよう配分計画に従って各メッシュを簡略化する(顔・髪は強く保護)。既定はオフ")]
        public bool questEnableDecimation = false;

        [Tooltip("Quest: ポリゴン削減の目標三角形数(目安: Excellent 7500 / Good 10000 / Medium 15000 / Poor 20000)")]
        public int questDecimationTargetTriangles = 20000;

        [Tooltip("Quest: ポリゴン削減の配分計画(レンダラーごとの目標三角形数)。「自動で配分計画を作成」で生成し、行ごとに編集・除外できる")]
        public List<PolygonPlanEntryData> questDecimationPlan = new List<PolygonPlanEntryData>();

        [Tooltip("Quest: マージから除外するPhysBoneの識別パス(プレビューで「マージしない」を選んだもの)")]
        public List<string> questPhysBoneNoMergePaths = new List<string>();

        [Tooltip("Quest: PhysBoneのマージ後もAndroidのPoor上限(コンポーネント8/コライダー16)を超える場合、超過分を自動削除する")]
        public bool questTrimPhysBonesToPoorLimit = false;

        // ------------------------------------------------------------
        // 3b-2) Quest変換パラメータ(旧QuestConverterで既定に隠れていた項目を統合UIへ露出)
        //   これらは AvatarStudioMapping.BuildQuestConvertSettings で QuestConvertSettings の
        //   同名フィールド(quest 接頭辞を外した名前)へ 1:1 で写像される。
        //   既定値は QuestConvertSettings のコンストラクタ既定に一致させる(JSON欠落時もこの既定へ落ちる)。
        // ------------------------------------------------------------

        [Tooltip("Quest: ベイク生成テクスチャの最大サイズ(px)。Quest推奨は1024")]
        public int questMaxTextureSize = 1024;

        [Tooltip("Quest: Androidプラットフォームのテクスチャ圧縮形式。既定はASTC 6x6")]
        public TextureImporterFormat questAndroidFormat = TextureImporterFormat.ASTC_6x6;

        [Tooltip("Quest: 単色・低ディテールなテクスチャを検出して極端に縮小する(見た目の劣化が少ない範囲で)")]
        public bool questAggressiveTextureReduction = true;

        [Tooltip("Quest: Toon Standard時、lilToonの影設定から影ランプテクスチャを生成する(オフ時はSDK既定ランプ)")]
        public bool questGenerateShadowRamp = true;

        [Tooltip("Quest: エミッションを変換する(Toon Lit=メインへ加算ベイク / Toon Standard=Emissionへマップ)")]
        public bool questBakeEmission = true;

        [Tooltip("Quest: Toon Lit時、lilToonの影色をメインテクスチャに乗算ベイクする(フラットな擬似陰影)")]
        public bool questBakeShadowIntoMainTex = true;

        [Tooltip("Quest: lilToonのリムライトをToon Standardのリムへ近似変換する(まぶた等に想定外のハイライトが出ることがある。既定はオフ)")]
        public bool questMapRimLighting = false;

        [Tooltip("Quest: マテリアル差し替えアニメーション(FX等)も変換後マテリアルを参照するよう複製・差し替える")]
        public bool questConvertAnimations = true;

        [Tooltip("Quest: Android非対応コンポーネント(Cloth/Camera/Light/AudioSource等)を削除する")]
        public bool questRemoveUnsupportedComponents = true;

        [Tooltip("Quest: Unityコンストレイントを見つけたらVRCConstraintへ変換する(SDKの変換APIを使用)")]
        public bool questConvertUnityConstraints = true;

        [Tooltip("Quest: 生成アセットの出力先ルートフォルダ")]
        public string questOutputFolder = "Assets/RARA/QuestConverter/Generated";
    }
}
#endif
