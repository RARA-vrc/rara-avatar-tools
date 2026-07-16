// RARA AvatarStudio - 初回移行(旧2ツールの保存設定からのシード)
// 統合ウィンドウを初めて開いたアバターに対し、既存の『RARA PC軽量化ツール』/『RARA Quest対応コンバーター』
// が EditorPrefs に保存している設定を読み取り、AvatarStudioSettings の初期値として引き継ぐ。
//
// 【厳守事項】
//  ・読み取り専用。旧ツールの EditorPrefs キー(RARA.PCOptimizer.Settings / RARA.QuestConverter.Settings /
//    RARA.QuestConverter.TargetRank)へ書き込み・削除は一切行わない(旧ウィンドウは当面併存する)。
//  ・旧ツールが LoadSettings で行っている移行ガードをそのまま再現し、旧ユーザーの体験を変えない。
//  ・パースに失敗した設定は無視(null)し、既定値へフォールバックする。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RARA.PCOptimizer;
using RARA.QuestConverter;

namespace RARA.AvatarStudio
{
    /// <summary>
    /// 旧2ツールの保存済み設定から <see cref="AvatarStudioSettings"/> をシードする移行ヘルパー。
    /// <see cref="AvatarStudioSettingsIO"/> が、そのアバターに保存済みのStudio設定が無い初回のみ呼び出す。
    /// </summary>
    public static class AvatarStudioMigration
    {
        // 旧ツールのEditorPrefsキー(読み取り専用で参照する)。
        private const string PcSettingsKey = "RARA.PCOptimizer.Settings";
        private const string QuestSettingsKey = "RARA.QuestConverter.Settings";
        private const string QuestTargetRankKey = "RARA.QuestConverter.TargetRank";

        /// <summary>
        /// 旧2ツールのいずれかに保存済み設定があるか(=移行シードの価値があるか)。
        /// </summary>
        public static bool HasAnyOldSettings()
        {
            return !string.IsNullOrEmpty(EditorPrefs.GetString(PcSettingsKey, "")) ||
                   !string.IsNullOrEmpty(EditorPrefs.GetString(QuestSettingsKey, ""));
        }

        /// <summary>
        /// 旧2ツールの保存設定から新しい <see cref="AvatarStudioSettings"/> を組み立てて返す。
        /// 旧ツールの設定はグローバル(アバター非依存)のため、シードは全アバター共通の初期値になる。
        ///
        /// ・PC固有フィールドは PCOptimizeSettings から、Quest固有フィールドは QuestConvertSettings から取る。
        /// ・共有ブロック(トグル整理・SMR統合・PhysBone整理・ensureTraceAndOptimize)は、両方あれば
        ///   Quest側を優先し、Quest設定が無ければ PC側から取る(Quest対応コンバーターの方が共有項目の
        ///   ユースケースが広いため。どちらも無ければ AvatarStudioSettings の既定値のまま)。
        /// ・questGoalRank は Quest ウィンドウが別管理していた int 目標ランク pref から引き継ぐ。
        /// </summary>
        public static AvatarStudioSettings SeedFromOldTools()
        {
            var s = new AvatarStudioSettings();

            PCOptimizeSettings pc = TryLoadPcSettings();
            QuestConvertSettings quest = TryLoadQuestSettings();

            if (pc != null) ApplyPcSpecific(s, pc);
            if (quest != null) ApplyQuestSpecific(s, quest);

            // 共有ブロック: Quest優先、無ければPC。
            if (quest != null) ApplyShared(s,
                quest.toggleChoices, quest.mergeSkinnedMeshesMode, quest.skinnedMeshMergeOptOutPaths,
                quest.mergePhysBones, quest.physBoneLooseMerge, quest.physBoneKeepPaths,
                quest.physBoneRemovePaths, quest.ensureTraceAndOptimize);
            else if (pc != null) ApplyShared(s,
                pc.toggleChoices, pc.mergeSkinnedMeshesMode, pc.skinnedMeshMergeOptOutPaths,
                pc.mergePhysBones, pc.physBoneLooseMerge, pc.physBoneKeepPaths,
                pc.physBoneRemovePaths, pc.ensureTraceAndOptimize);

            // Quest目標ランク(旧Questウィンドウは素のint pref: 0=Excellent..3=Poor、既定2=Medium)。
            // questGoalRank は int 型のため、範囲だけ丸めてそのまま代入する。
            int rank = EditorPrefs.GetInt(QuestTargetRankKey, (int)QuestTargetRank.Medium);
            s.questGoalRank = Mathf.Clamp(rank, (int)QuestTargetRank.Excellent, (int)QuestTargetRank.Poor);

            return s;
        }

        // ============================================================
        // 旧設定のロード(旧ウィンドウの移行ガードを再現)
        // ============================================================

        /// <summary>
        /// 『RARA PC軽量化ツール』の保存設定を読み込む(未保存・破損なら null)。
        /// PCOptimizerWindow.LoadSettings の移行ガードを再現する:
        ///  ・mergeSkinnedMeshesMode キーが無い旧設定 → None(統合しない)。
        /// </summary>
        private static PCOptimizeSettings TryLoadPcSettings()
        {
            string json = EditorPrefs.GetString(PcSettingsKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var pc = new PCOptimizeSettings();
                JsonUtility.FromJsonOverwrite(json, pc);
                if (json.IndexOf("mergeSkinnedMeshesMode", StringComparison.Ordinal) < 0)
                {
                    pc.mergeSkinnedMeshesMode = SkinnedMeshMergeMode.None;
                }
                // 移行: bool atlasUnifyOutline → enum atlasOutlineUnifyMode(旧 PC設定の "true" はアウトライン付きに統一へ)。
                if (json.IndexOf("atlasOutlineUnifyMode", StringComparison.Ordinal) < 0
                    && json.IndexOf("\"atlasUnifyOutline\":true", StringComparison.Ordinal) >= 0)
                {
                    pc.atlasOutlineUnifyMode = OutlineUnifyMode.アウトライン付きに統一;
                }
                return pc;
            }
            catch (Exception)
            {
                return null; // 破損設定は無視(既定へフォールバック)
            }
        }

        /// <summary>
        /// 『RARA Quest対応コンバーター』の保存設定を読み込む(未保存・破損なら null)。
        /// QuestConverterWindow.LoadSettings の移行ガードを再現する:
        ///  ・physBoneSelectionMode キーが無い旧設定 → KeepAll(従来動作)。
        ///  ・transparentHandling キーが無い旧設定 → hideTransparentMaterials で Emulate / Opaque を決める。
        ///  ・mergeSkinnedMeshesMode キーが無い旧設定 → None(統合しない)。
        ///  ・conversionMode == ConsolidateOnly → QuestConvert(旧「PC最適化のみ」はPC軽量化ツールへ移行済み)。
        /// </summary>
        private static QuestConvertSettings TryLoadQuestSettings()
        {
            string json = EditorPrefs.GetString(QuestSettingsKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var quest = new QuestConvertSettings();
                JsonUtility.FromJsonOverwrite(json, quest);

                if (json.IndexOf("physBoneSelectionMode", StringComparison.Ordinal) < 0)
                {
                    quest.physBoneSelectionMode = PhysBoneSelectionMode.KeepAll;
                }
                if (json.IndexOf("transparentHandling", StringComparison.Ordinal) < 0)
                {
                    quest.transparentHandling = quest.hideTransparentMaterials
                        ? TransparentHandling.Emulate
                        : TransparentHandling.Opaque;
                }
                if (json.IndexOf("mergeSkinnedMeshesMode", StringComparison.Ordinal) < 0)
                {
                    quest.mergeSkinnedMeshesMode = SkinnedMeshMergeMode.None;
                }
                if (quest.conversionMode == ConversionMode.ConsolidateOnly)
                {
                    quest.conversionMode = ConversionMode.QuestConvert;
                }
                return quest;
            }
            catch (Exception)
            {
                return null; // 破損設定は無視(既定へフォールバック)
            }
        }

        // ============================================================
        // 各ブロックの適用(旧設定のList参照はそのまま譲り受ける:元オブジェクトは破棄されるため安全)
        // ============================================================

        private static void ApplyPcSpecific(AvatarStudioSettings s, PCOptimizeSettings pc)
        {
            s.pcTargetRank = pc.targetRank;
            s.savePrefab = pc.savePrefab;
            s.pcEnableAtlas = pc.enableAtlas;
            s.pcAtlasMaxSize = pc.atlasMaxSize;
            s.pcAtlasColorOnlyMaterials = pc.atlasColorOnlyMaterials;
            s.pcAtlasBakeEmissionMask = pc.atlasBakeEmissionMask;
            s.pcAtlasIgnoreCull = pc.atlasIgnoreCull;
            s.pcAtlasOutlineUnifyMode = pc.atlasOutlineUnifyMode;
            s.pcAtlasExcludeGuids = pc.atlasExcludeMaterialGuids ?? new List<string>();
            s.pcTexturePlan = pc.texturePlan ?? new List<TextureSizePlanEntry>();
        }

        private static void ApplyQuestSpecific(AvatarStudioSettings s, QuestConvertSettings quest)
        {
            s.shaderTarget = quest.shaderTarget;
            s.transparentHandling = quest.transparentHandling;
            s.materialOverrides = quest.materialOverrides ?? new List<MaterialOverrideEntry>();
            s.questEnableAtlas = quest.enableAtlas;
            s.questAtlasMaxSize = quest.atlasMaxSize;
            s.questAtlasUnifyRamps = quest.atlasUnifyRamps;
            s.questTextureSizePlan = quest.textureSizePlan ?? new List<TextureSizePlanEntry>();
            s.questExcludePaths = quest.questExcludePaths ?? new List<string>();
            s.questRemoveHiddenMeshByBlendShape = quest.removeHiddenMeshByBlendShape;
            s.questHiddenMeshRendererPaths = quest.hiddenMeshRendererPaths ?? new List<string>();
            s.questEnableDecimation = quest.enableDecimation;
            s.questDecimationTargetTriangles = quest.decimationTargetTriangles;
            s.questDecimationPlan = quest.decimationPlan ?? new List<PolygonPlanEntryData>();
            s.questPhysBoneNoMergePaths = quest.physBoneNoMergePaths ?? new List<string>();
            s.questTrimPhysBonesToPoorLimit = quest.trimPhysBonesToPoorLimit;

            // Quest変換パラメータ(旧QuestConverterで既定に隠れていた項目を統合UIへシード)。
            s.questMaxTextureSize = quest.maxTextureSize;
            s.questAndroidFormat = quest.androidFormat;
            s.questAggressiveTextureReduction = quest.aggressiveTextureReduction;
            s.questGenerateShadowRamp = quest.generateShadowRamp;
            s.questBakeEmission = quest.bakeEmission;
            s.questBakeShadowIntoMainTex = quest.bakeShadowIntoMainTex;
            s.questMapRimLighting = quest.mapRimLighting;
            s.questConvertAnimations = quest.convertAnimations;
            s.questRemoveUnsupportedComponents = quest.removeUnsupportedComponents;
            s.questConvertUnityConstraints = quest.convertUnityConstraints;
            // outputFolder は空/未設定なら AvatarStudioSettings の既定を保持(空を持ち込まない)。
            if (!string.IsNullOrEmpty(quest.outputFolder)) s.questOutputFolder = quest.outputFolder;
        }

        private static void ApplyShared(
            AvatarStudioSettings s,
            List<ToggleGroupChoice> toggleChoices,
            SkinnedMeshMergeMode mergeSkinnedMeshesMode,
            List<string> skinnedMeshMergeOptOutPaths,
            bool mergePhysBones,
            bool physBoneLooseMerge,
            List<string> physBoneKeepPaths,
            List<string> physBoneRemovePaths,
            bool ensureTraceAndOptimize)
        {
            s.toggleChoices = toggleChoices ?? new List<ToggleGroupChoice>();
            s.mergeSkinnedMeshesMode = mergeSkinnedMeshesMode;
            s.skinnedMeshMergeOptOutPaths = skinnedMeshMergeOptOutPaths ?? new List<string>();
            s.mergePhysBones = mergePhysBones;
            s.physBoneLooseMerge = physBoneLooseMerge;
            s.physBoneKeepPaths = physBoneKeepPaths ?? new List<string>();
            s.physBoneRemovePaths = physBoneRemovePaths ?? new List<string>();
            s.ensureTraceAndOptimize = ensureTraceAndOptimize;
        }
    }
}
#endif
