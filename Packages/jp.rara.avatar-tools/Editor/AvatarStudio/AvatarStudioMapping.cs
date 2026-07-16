// RARA AvatarStudio - 設定マッピング(統合設定 → 旧2ツールの設定型)
// AvatarStudioSettings を、パイプラインが要求する RARA.PCOptimizer.PCOptimizeSettings /
// RARA.QuestConverter.QuestConvertSettings へ変換する。
//
// 【方針(検証済みハザード対応)】
//  ・共有ブロックのList(toggleChoices / physBoneKeep・Remove / skinnedMeshMergeOptOut …)は
//    「同一インスタンス参照」で PC/Quest 双方の設定へ渡す(コピーしない)。パイプラインは設定リストを
//    読み取り専用として扱う(Optimize/Convert 内でリストへ Add/Remove/Clear しない)ことを確認済み。
//  ・scalar(enum/bool/int)は値コピーする。
//  ・H3: Quest設定は必ず conversionMode = QuestConvert に固定する(PC最適化のみモードは使わない)。
//  ・H2: Quest設定は deactivateOriginal = false に固定する(元アバターを非アクティブ化しない。
//        繰り返し生成でも元が生きたまま残るよう、実行側でも毎回リアクティベートする)。
//  ・PhysBone は KeepAll 運用(physBoneSelectionMode = KeepAll)。studioのPhysBoneパネルは
//    physBoneRemovePaths のみを書くremoveモデルなので、PC/Questとも「削除」指定分だけ除く。
//  ・AvatarStudioSettings で明示していない Quest スカラー(maxTextureSize / androidFormat /
//    aggressiveTextureReduction / bakeShadowIntoMainTex / generateShadowRamp / bakeEmission /
//    mapRimLighting / convertAnimations / removeUnsupportedComponents / convertUnityConstraints /
//    hideExpressionOverlays / outputFolder 等)は QuestConvertSettings の既定値をそのまま用いる
//    (既定は Quest 推奨値のため、統合UIで露出しない項目は既定に委ねる)。
#if UNITY_EDITOR
using RARA.PCOptimizer;
using RARA.QuestConverter;

namespace RARA.AvatarStudio
{
    /// <summary>
    /// AvatarStudioSettings のインスタンス版マッピング。呼び出し側の書き味に合わせて
    /// <c>settings.BuildPCOptimizeSettings()</c> / <c>settings.BuildQuestConvertSettings()</c> でも
    /// 静的版と同じ結果を得られるようにする(実体は <see cref="AvatarStudioMapping"/> へ委譲)。
    /// </summary>
    public partial class AvatarStudioSettings
    {
        /// <summary>この設定から PC軽量化用の PCOptimizeSettings を生成する(共有Listは参照共有)。</summary>
        public PCOptimizeSettings BuildPCOptimizeSettings()
        {
            return AvatarStudioMapping.BuildPCOptimizeSettings(this);
        }

        /// <summary>この設定から Quest変換用の QuestConvertSettings を生成する(共有Listは参照共有、H2/H3固定)。</summary>
        public QuestConvertSettings BuildQuestConvertSettings()
        {
            return AvatarStudioMapping.BuildQuestConvertSettings(this);
        }
    }

    /// <summary>
    /// 統合設定 <see cref="AvatarStudioSettings"/> を旧2ツールの設定型へ写像する静的ヘルパー。
    /// </summary>
    public static class AvatarStudioMapping
    {
        /// <summary>
        /// PC軽量化用の <see cref="PCOptimizeSettings"/> を組み立てる。
        /// 共有ブロックのListは studio の同一インスタンスをそのまま代入する(参照共有)。
        /// </summary>
        public static PCOptimizeSettings BuildPCOptimizeSettings(AvatarStudioSettings s)
        {
            var pc = new PCOptimizeSettings();
            if (s == null) return pc;

            // ---- 共有ブロック(同一List参照 / scalarは値コピー) ----
            pc.toggleChoices = s.toggleChoices;                               // 参照共有
            pc.mergeSkinnedMeshesMode = s.mergeSkinnedMeshesMode;
            pc.skinnedMeshMergeOptOutPaths = s.skinnedMeshMergeOptOutPaths;   // 参照共有
            pc.mergePhysBones = s.mergePhysBones;
            pc.physBoneLooseMerge = s.physBoneLooseMerge;
            pc.physBoneKeepPaths = s.physBoneKeepPaths;                       // 参照共有(通常は空。空ならremove基準)
            pc.physBoneRemovePaths = s.physBoneRemovePaths;                   // 参照共有
            pc.ensureTraceAndOptimize = s.ensureTraceAndOptimize;

            // ---- PC固有 ----
            pc.targetRank = s.pcTargetRank;
            pc.savePrefab = s.savePrefab;
            pc.enableAtlas = s.pcEnableAtlas;
            pc.atlasMaxSize = s.pcAtlasMaxSize;
            pc.atlasColorOnlyMaterials = s.pcAtlasColorOnlyMaterials;
            pc.atlasBakeEmissionMask = s.pcAtlasBakeEmissionMask;
            pc.atlasIgnoreCull = s.pcAtlasIgnoreCull;
            pc.atlasOutlineUnifyMode = s.pcAtlasOutlineUnifyMode;
            pc.atlasExcludeMaterialGuids = s.pcAtlasExcludeGuids;             // 参照共有
            pc.texturePlan = s.pcTexturePlan;                                 // 参照共有

            return pc;
        }

        /// <summary>
        /// Quest変換用の <see cref="QuestConvertSettings"/> を組み立てる。
        /// 共有ブロックのListは studio の同一インスタンスをそのまま代入し(参照共有)、
        /// H3(conversionMode=QuestConvert)・H2(deactivateOriginal=false)を固定する。
        /// </summary>
        public static QuestConvertSettings BuildQuestConvertSettings(AvatarStudioSettings s)
        {
            var quest = new QuestConvertSettings();
            if (s == null) return quest;

            // ---- 共有ブロック(同一List参照 / scalarは値コピー) ----
            quest.toggleChoices = s.toggleChoices;                               // 参照共有
            quest.mergeSkinnedMeshesMode = s.mergeSkinnedMeshesMode;
            quest.skinnedMeshMergeOptOutPaths = s.skinnedMeshMergeOptOutPaths;   // 参照共有
            quest.mergePhysBones = s.mergePhysBones;
            quest.physBoneLooseMerge = s.physBoneLooseMerge;
            quest.physBoneKeepPaths = s.physBoneKeepPaths;                       // 参照共有(通常は空。KeepAllではremove基準)
            quest.physBoneRemovePaths = s.physBoneRemovePaths;                   // 参照共有
            quest.ensureTraceAndOptimize = s.ensureTraceAndOptimize;

            // PhysBone は KeepAll 運用(既定は全て残し、UIで「削除」指定したものだけ除く)。
            // studio のPhysBoneパネルは physBoneRemovePaths のみを書き込む remove モデルのため、
            // Quest でも PC と同じく physBoneRemovePaths を基準にする(OptInにすると keepPaths が
            // 常に空 → 全PhysBoneが削除されてしまうため使わない)。
            quest.physBoneSelectionMode = PhysBoneSelectionMode.KeepAll;
            quest.physBoneNoMergePaths = s.questPhysBoneNoMergePaths;            // 参照共有(Quest固有)

            // ---- Quest固有 ----
            quest.shaderTarget = s.shaderTarget;
            quest.transparentHandling = s.transparentHandling;
            quest.materialOverrides = s.materialOverrides;                       // 参照共有
            quest.enableAtlas = s.questEnableAtlas;
            quest.atlasMaxSize = s.questAtlasMaxSize;
            quest.atlasUnifyRamps = s.questAtlasUnifyRamps;
            quest.textureSizePlan = s.questTextureSizePlan;                      // 参照共有
            quest.questExcludePaths = s.questExcludePaths;                       // 参照共有
            quest.removeHiddenMeshByBlendShape = s.questRemoveHiddenMeshByBlendShape;
            quest.hiddenMeshRendererPaths = s.questHiddenMeshRendererPaths;      // 参照共有
            quest.enableDecimation = s.questEnableDecimation;
            quest.decimationTargetTriangles = s.questDecimationTargetTriangles;
            quest.decimationPlan = s.questDecimationPlan;                        // 参照共有
            quest.trimPhysBonesToPoorLimit = s.questTrimPhysBonesToPoorLimit;

            // ---- Quest変換パラメータ(統合UIで露出。QuestConvertSettings同名フィールドへ1:1写像) ----
            quest.maxTextureSize = s.questMaxTextureSize;
            quest.androidFormat = s.questAndroidFormat;
            quest.aggressiveTextureReduction = s.questAggressiveTextureReduction;
            quest.generateShadowRamp = s.questGenerateShadowRamp;
            quest.bakeEmission = s.questBakeEmission;
            quest.bakeShadowIntoMainTex = s.questBakeShadowIntoMainTex;
            quest.mapRimLighting = s.questMapRimLighting;
            quest.convertAnimations = s.questConvertAnimations;
            quest.removeUnsupportedComponents = s.questRemoveUnsupportedComponents;
            quest.convertUnityConstraints = s.questConvertUnityConstraints;
            // outputFolder は空だと生成先が壊れるため、空なら既定(構築済みquest.outputFolder)を保持する。
            quest.outputFolder = string.IsNullOrEmpty(s.questOutputFolder) ? quest.outputFolder : s.questOutputFolder;

            // ---- 固定(検証済みハザード対応。ここは studio では変更させない) ----
            quest.conversionMode = ConversionMode.QuestConvert; // H3: 必ずQuest変換モード
            quest.deactivateOriginal = false;                   // H2: 元アバターを非アクティブ化しない

            // hideExpressionOverlays / hideTransparentMaterials(旧移行用) は統合UIで露出しないため
            // QuestConvertSettings の既定値をそのまま使う。
            return quest;
        }
    }
}
#endif
