// RARA PC軽量化ツール - 共有コア(設定・ランク上限)
// VRChat Avatars SDK 3.10.4 / Unity 2022.3.22f1 向け。
// Assembly-CSharp-Editor でコンパイルされる(asmdefなし)。RARA.QuestConverter と同一アセンブリのため
// 同名前空間の public/internal をそのまま再利用できる。
//
// このファイルはエージェント I1 が所有する「ピン留め契約(公開型)」の実体を定義する:
//   PCTargetRank / PCOptimizeSettings / PCRankLimits
// PCOptimizer(パイプライン)は PCOptimizer.cs、PCMaterialAtlasser は I2、
// PCTexturePlanner は I3 のファイルで定義される(同一アセンブリなので相互参照可能)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace RARA.PCOptimizer
{
    /// <summary>
    /// PC(Windows)基準の目標パフォーマンスランク。既定は Good。
    /// このツールは (b) Poor以上を目指すPCユーザー / (c) Good を目指すPCユーザー を対象とする
    /// (雑なQuest変換は QuestConverter が担当する)。
    /// 値は PerformanceRating の並び(Excellent が最良)に対応する。
    /// </summary>
    public enum PCTargetRank
    {
        /// <summary>最良(三角形32000以下など、最も厳しい上限)。</summary>
        Excellent = 0,
        /// <summary>推奨の既定(三角形70000以下)。</summary>
        Good = 1,
        /// <summary>中間。</summary>
        Medium = 2,
        /// <summary>最低ライン(これを超えると Very Poor: PCではアップロード可だが表示設定で隠され得る)。</summary>
        Poor = 3,
    }

    /// <summary>
    /// アトラス統合時のアウトライン(輪郭線)の扱い。
    /// ・しない: アウトライン有無が異なるマテリアルは別グループのまま(統合しない)。
    /// ・アウトラインを外して統合(推奨): プレーンlilToonとアウトライン版を同一グループにし、
    ///   統合マテリアルをプレーンlilToonへ揃える(服の輪郭線が消えるが、瞳・顔に黒縁は付かない)。
    /// ・アウトライン付きに統一: アウトライン付き側へ揃える(旧動作。輪郭の無かった部分に付く。
    ///   瞳・顔系のプレーンlilToonは黒縁化を避けるため自動でこのグループから除外する)。
    /// </summary>
    public enum OutlineUnifyMode
    {
        /// <summary>統合しない(アウトライン有無で分かれる)。</summary>
        しない = 0,
        /// <summary>アウトラインを外して統合(プレーンlilToonへ揃える。推奨)。</summary>
        アウトラインを外して統合 = 1,
        /// <summary>アウトライン付きに統一(旧動作。瞳・顔は自動回避)。</summary>
        アウトライン付きに統一 = 2,
    }

    /// <summary>
    /// PC軽量化の設定。ウィンドウ(I4)から編集され、PCOptimizer.Optimize に渡される。
    /// JsonUtility でそのままシリアライズできるよう、参照は GUID / アセット参照型で保持する
    /// (List 要素の TextureSizePlanEntry / ToggleGroupChoice はいずれも [Serializable])。
    /// </summary>
    [Serializable]
    public class PCOptimizeSettings
    {
        [Tooltip("目標にするPC(Windows)パフォーマンスランク。既定は Good")]
        public PCTargetRank targetRank = PCTargetRank.Good;

        [Tooltip("最適化後の複製をプレファブ(_Opt.prefab)としても保存する(非破壊: 元アバターは無改変)")]
        public bool savePrefab = true;

        [Tooltip("互換マテリアルを1枚のアトラスへ統合し、マテリアルスロット数とテクスチャメモリを削減する")]
        public bool enableAtlas = true;

        [Tooltip("アトラステクスチャの最大サイズ(px)")]
        public int atlasMaxSize = 2048;

        [Tooltip("テクスチャ無し(色のみ)マテリアルの色をアトラスの単色セルとしてベイクし、統合後も色を保つ")]
        public bool atlasColorOnlyMaterials = true;

        [Tooltip("エミッション色/マップをエミッション用アトラスへベイクする")]
        public bool atlasBakeEmissionMask = true;

        [Tooltip("カリング(片面/両面)の違いを無視して統合する。統合後のマテリアルは両面描画(Cull Off)になる。見た目の破綻はまれだがフィルレートが少し増える")]
        public bool atlasIgnoreCull = true;

        [Tooltip("アトラス統合時のアウトライン(輪郭線)の扱い。しない / アウトラインを外して統合(推奨: 服の輪郭線が消える) / アウトライン付きに統一(輪郭の無かった部分に付く。瞳・顔は自動回避)")]
        public OutlineUnifyMode atlasOutlineUnifyMode = OutlineUnifyMode.しない;

        [Tooltip("アトラス統合から除外するマテリアルのアセットGUID")]
        public List<string> atlasExcludeMaterialGuids = new List<string>();

        [Tooltip("縮小コピーを生成するテクスチャの計画(元テクスチャは無改変。QuestConverterと同じ計画型を再利用)")]
        public List<RARA.QuestConverter.TextureSizePlanEntry> texturePlan = new List<RARA.QuestConverter.TextureSizePlanEntry>();

        [Tooltip("設定が一致する兄弟PhysBoneチェーンを1つへマージし、揺れを維持したままコンポーネント数を削減する")]
        public bool mergePhysBones = true;

        [Tooltip("設定が異なる兄弟チェーンも先頭の設定に統一してマージする(揺れ方が先頭チェーンに揃う)")]
        public bool physBoneLooseMerge = true;

        [Tooltip("OptInで残すPhysBoneの識別パス(空でなければ、ここに無いPhysBoneはマージ前に削除される)")]
        public List<string> physBoneKeepPaths = new List<string>();

        [Tooltip("削除指定するPhysBoneの識別パス(physBoneKeepPaths が空のときに適用)")]
        public List<string> physBoneRemovePaths = new List<string>();

        [Tooltip("衣装・トグルグループごとの固定方法(維持/常時表示/非表示除去)。QuestConverterと同じ選択保存型を再利用")]
        public List<RARA.QuestConverter.ToggleGroupChoice> toggleChoices = new List<RARA.QuestConverter.ToggleGroupChoice>();

        [Tooltip("AAOのTrace and Optimizeが無ければ複製へ追加し、ビルド時のメッシュ/スロット統合を有効にする")]
        public bool ensureTraceAndOptimize = true;

        // 【SkinnedMesh統合】顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を AAO MergeSkinnedMesh で
        //   1つへ統合し、SMR数・マテリアルスロット数を削減する(PC Good上限=SMR2対策)。統合はビルド時
        //   (NDMF)にAAOが行い、ブレンドシェイプ改名・スロット再マップ・アニメ再パスもAAOが行うため、
        //   顔以外のブレンドシェイプ・アニメも追従して動き続ける(バインディング書き換え済み)。
        //   QuestConverter と同じ SkinnedMeshMergeMode 型を再利用する。
        //   【移行】旧保存JSONにこのキーが無い場合は LoadSettings で None へ戻す(新規既定は MergeExceptFace)。
        [Tooltip("SkinnedMeshの統合方法。しない=従来どおり / 顔以外を統合=顔(ビセーム/まばたき)以外の全SkinnedMeshRendererを1つへ統合しSMR数・スロット数を削減(推奨。PC Good上限SMR2対策)")]
        public RARA.QuestConverter.SkinnedMeshMergeMode mergeSkinnedMeshesMode = RARA.QuestConverter.SkinnedMeshMergeMode.MergeExceptFace;

        [Tooltip("SkinnedMesh統合から個別に除外するレンダラーの相対パス(プレビューで「統合しない」を選んだもの)")]
        public List<string> skinnedMeshMergeOptOutPaths = new List<string>();

        [Tooltip("mergeSkinnedMeshesMode=グループ指定 のときの、レンダラー→グループ番号(1..8)の割り当て。統合UIから設定する")]
        public List<RARA.QuestConverter.SmrMergeGroupAssignment> smrMergeGroups = new List<RARA.QuestConverter.SmrMergeGroupAssignment>();
    }

    /// <summary>
    /// PC(Windows)パフォーマンスランクの上限値。
    /// 既定値は SDK 3.10.4 の StatsLevels/Windows/{Excellent,Good,Medium,Poor}_Windows.asset から
    /// 直接確認した検証済みの数値をハードコードする。初回参照時に SDK の Windows ランクレベル
    /// (AvatarPerformanceStats.GetStatLevelForRating(rating, mobile:false))からベストエフォートで
    /// 実値を読み直し、SDK 更新時にも追随する。読めない場合はハードコード値へ黙ってフォールバックする。
    /// </summary>
    public static class PCRankLimits
    {
        /// <summary>ランク判定に使う統計項目(PC軽量化ツールが目標比較する12項目)。</summary>
        public enum PCStat
        {
            Triangles = 0,
            SkinnedMeshes = 1,
            MeshRenderers = 2,
            MaterialSlots = 3,
            TextureMemoryMB = 4,
            Bones = 5,
            PhysBoneComponents = 6,
            PhysBoneTransforms = 7,
            PhysBoneColliders = 8,
            PhysBoneCollisionChecks = 9,
            Contacts = 10,
            Constraints = 11,
        }

        private const int RankCount = 4; // Excellent / Good / Medium / Poor
        private const int StatCount = 12;

        // [rank(Excellent..Poor), stat] の上限値(2026-07時点、Windows のランクレベルアセットから検証)。
        private static readonly int[,] Defaults =
        {
            //                     Excellent  Good  Medium   Poor
            /* Triangles */              { 32000, 70000, 70000, 70000 },
            /* SkinnedMeshes */          {     1,     2,     8,    16 },
            /* MeshRenderers */          {     4,     8,    16,    24 },
            /* MaterialSlots */          {     4,     8,    16,    32 },
            /* TextureMemoryMB */        {    40,    75,   110,   150 },
            /* Bones */                  {    75,   150,   256,   400 },
            /* PhysBoneComponents */     {     4,     8,    16,    32 },
            /* PhysBoneTransforms */     {    16,    64,   128,   256 },
            /* PhysBoneColliders */      {     4,     8,    16,    32 },
            /* PhysBoneCollisionChecks */{    32,   128,   256,   512 },
            /* Contacts */               {     8,    16,    24,    32 },
            /* Constraints */            {   100,   250,   300,   350 },
        };

        // Defaults は [stat, rank] の並びで書いたため、内部キャッシュは [rank, stat] に転置して持つ。
        private static int[,] _cache;
        private static bool _resolved;
        private static readonly object _lock = new object();

        /// <summary>
        /// 指定ランクにおける統計項目の Windows 上限値を返す(その値以下ならそのランク以内)。
        /// </summary>
        public static int GetLimit(PCTargetRank rank, PCStat stat)
        {
            EnsureResolved();
            int r = Mathf.Clamp((int)rank, 0, RankCount - 1);
            int s = (int)stat;
            if (s < 0 || s >= StatCount) return int.MaxValue;
            return _cache[r, s];
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            lock (_lock)
            {
                if (_resolved) return;

                // まずハードコード値([rank, stat] へ転置)で初期化する。
                var cache = new int[RankCount, StatCount];
                for (int s = 0; s < StatCount; s++)
                {
                    for (int r = 0; r < RankCount; r++)
                    {
                        cache[r, s] = Defaults[s, r];
                    }
                }

                // SDK の Windows ランクレベルからベストエフォートで実値へ差し替える。
                // 失敗しても cache はハードコード値のまま残るため安全。
                // レベルの型名には依存せず var で受ける(型は VRCSDKBase.dll 内のメソッド戻り値のみで到達可能)。
                try
                {
                    for (int r = 0; r < RankCount; r++)
                    {
                        // PerformanceRating: None=0, Excellent=1, Good=2, Medium=3, Poor=4
                        var rating = (PerformanceRating)(r + 1);
                        var level = AvatarPerformanceStats.GetStatLevelForRating(rating, false);
                        if (level == null) continue;

                        Assign(cache, r, PCStat.Triangles, level.polyCount);
                        Assign(cache, r, PCStat.SkinnedMeshes, level.skinnedMeshCount);
                        Assign(cache, r, PCStat.MeshRenderers, level.meshCount);
                        Assign(cache, r, PCStat.MaterialSlots, level.materialCount);
                        Assign(cache, r, PCStat.TextureMemoryMB, Mathf.RoundToInt(level.textureMegabytes));
                        Assign(cache, r, PCStat.Bones, level.boneCount);
                        Assign(cache, r, PCStat.Contacts, level.contactCount);
                        Assign(cache, r, PCStat.Constraints, level.constraintsCount);

                        // AvatarPerformanceStatsLevel.physBone は非nullの値型(構造体)。
                        var pb = level.physBone;
                        {
                            Assign(cache, r, PCStat.PhysBoneComponents, pb.componentCount);
                            Assign(cache, r, PCStat.PhysBoneTransforms, pb.transformCount);
                            Assign(cache, r, PCStat.PhysBoneColliders, pb.colliderCount);
                            Assign(cache, r, PCStat.PhysBoneCollisionChecks, pb.collisionCheckCount);
                        }
                    }
                }
                catch (Exception)
                {
                    // 黙ってハードコード値へフォールバックする(SDK API 差異・欠落に耐える)。
                }

                _cache = cache;
                _resolved = true;
            }
        }

        /// <summary>正の実値のみ上書きする(0/負値の異常は既定値を維持)。</summary>
        private static void Assign(int[,] cache, int rank, PCStat stat, int value)
        {
            if (value > 0) cache[rank, (int)stat] = value;
        }
    }
}
#endif
