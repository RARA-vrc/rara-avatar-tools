// RARA Quest Converter - Quest(Android)適合診断
// SDKのAvatarPerformance(モバイル基準)でパフォーマンス統計を算出し、
// 非モバイルシェーダーのマテリアル・非対応コンポーネント・テクスチャ設定の問題を列挙する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;

namespace RARA.QuestConverter
{
    /// <summary>診断結果テーブルの1行(カテゴリ名・値・ランク・上限超過)。</summary>
    public class DiagnosticsRow
    {
        /// <summary>カテゴリ名(日本語)。</summary>
        public string category;
        /// <summary>計測値(表示用文字列)。</summary>
        public string value;
        /// <summary>SDKのパフォーマンスランク(Excellent/Good/Medium/Poor/VeryPoor)。</summary>
        public string rating;
        /// <summary>VeryPoor(Androidの上限超過)ならtrue。</summary>
        public bool overLimit;
    }

    /// <summary>Quest(Android)適合診断の結果一式。</summary>
    public class DiagnosticsResult
    {
        /// <summary>カテゴリ別パフォーマンス統計。</summary>
        public System.Collections.Generic.List<DiagnosticsRow> perfRows;
        /// <summary>総合ランク。</summary>
        public string overallRating;
        /// <summary>ダウンロードサイズ上限内でSDKがアップロードをハードブロックしない見込みならtrue(可否はサイズのみで判定。ランクとは無関係)。</summary>
        public bool canUploadToAndroid;
        /// <summary>Android許可シェーダー以外を使用しているマテリアル。</summary>
        public System.Collections.Generic.List<Material> nonMobileMaterials;
        /// <summary>Android非対応コンポーネント。</summary>
        public System.Collections.Generic.List<Component> unsupportedComponents;
        /// <summary>テクスチャ設定に関する警告。</summary>
        public System.Collections.Generic.List<string> textureWarnings;
        /// <summary>ダウンロードサイズ推定の結果(推定失敗時はnull)。</summary>
        public SizeEstimateResult sizeEstimate;
    }

    /// <summary>アバターのQuest(Android)適合性を診断する静的クラス。</summary>
    public static class QuestDiagnostics
    {
        /// <summary>テクスチャ警告の表示上限(超過分は「他N件」に集約)。</summary>
        private const int TextureWarningDisplayMax = 30;

        /// <summary>
        /// アバターを診断して結果を返す(既定設定 = Quest除外パスなし)。
        /// </summary>
        public static DiagnosticsResult Analyze(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor avatar)
        {
            return Analyze(avatar, new QuestConvertSettings());
        }

        /// <summary>
        /// アバターを診断して結果を返す。EditorOnlyタグ付きサブツリーと settings.questExcludePaths で
        /// 指定されたサブツリーは、VRChatビルドと同様に「除去済み」として扱う
        /// (パフォーマンス統計・テクスチャ警告は除去後の一時複製に対して算出する)。
        /// 例外はUIへ投げず、textureWarningsにエラーメッセージを入れて返す。
        /// </summary>
        public static DiagnosticsResult Analyze(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor avatar, QuestConvertSettings settings)
        {
            var result = new DiagnosticsResult
            {
                perfRows = new List<DiagnosticsRow>(),
                overallRating = string.Empty,
                canUploadToAndroid = false,
                nonMobileMaterials = new List<Material>(),
                unsupportedComponents = new List<Component>(),
                textureWarnings = new List<string>(),
                sizeEstimate = null,
            };

            try
            {
                if (avatar == null)
                {
                    result.textureWarnings.Add("診断対象のアバターが指定されていません。");
                    return result;
                }
                if (settings == null) settings = new QuestConvertSettings();

                // ---- パフォーマンス統計: 除外サブツリーを除去した一時複製に対して算出する ----
                // (EditorOnlyのオブジェクトはVRChatビルドで除去されるため、アップロード結果に合わせる)
                GameObject temp = UnityEngine.Object.Instantiate(avatar.gameObject);
                temp.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    DestroyExcludedSubtrees(temp, settings);

                    // SDKビルダーと同じ手順: 先にコンストレイントのグループ情報を更新しないと
                    // 未登録のコンストレイントが正しく計上されない。
                    // VRCConstraintManager は internal クラス(SDKアセンブリのみ InternalsVisibleTo で参照可)のため、
                    // リフレクションで public static メソッド Sdk_ManuallyRefreshGroups(VRCConstraintBase[]) を呼び出す。
                    VRCConstraintBase[] vrcConstraints = temp.GetComponentsInChildren<VRCConstraintBase>(true);
                    RefreshConstraintGroups(vrcConstraints);

                    // モバイル(Android)基準でパフォーマンス統計を算出(表示名は元アバターの名前を使う)
                    var stats = new AvatarPerformanceStats(true /* isMobile */);
                    AvatarPerformance.CalculatePerformanceStats(avatar.gameObject.name, temp, stats, true /* isMobilePlatform */);

                    // カテゴリ別の行を構築(全統計フィールドはNullableのためガードする)
                    AddRow(result.perfRows, stats, "ポリゴン数", AvatarPerformanceCategory.PolyCount, FormatCount(stats.polyCount));
                    AddRow(result.perfRows, stats, "スキンメッシュ数", AvatarPerformanceCategory.SkinnedMeshCount, FormatCount(stats.skinnedMeshCount));
                    AddRow(result.perfRows, stats, "基本メッシュ数", AvatarPerformanceCategory.MeshCount, FormatCount(stats.meshCount));
                    AddRow(result.perfRows, stats, "マテリアルスロット数", AvatarPerformanceCategory.MaterialCount, FormatCount(stats.materialCount));
                    AddRow(result.perfRows, stats, "ボーン数", AvatarPerformanceCategory.BoneCount, FormatCount(stats.boneCount));
                    AddRow(result.perfRows, stats, "テクスチャメモリ(MB)", AvatarPerformanceCategory.TextureMegabytes, FormatMegabytes(stats.textureMegabytes));
                    AddRow(result.perfRows, stats, "PhysBoneコンポーネント数", AvatarPerformanceCategory.PhysBoneComponentCount, FormatCount(stats.physBone?.componentCount));
                    AddRow(result.perfRows, stats, "PhysBone対象Transform数", AvatarPerformanceCategory.PhysBoneTransformCount, FormatCount(stats.physBone?.transformCount));
                    AddRow(result.perfRows, stats, "PhysBoneコライダー数", AvatarPerformanceCategory.PhysBoneColliderCount, FormatCount(stats.physBone?.colliderCount));
                    AddRow(result.perfRows, stats, "PhysBone衝突チェック数", AvatarPerformanceCategory.PhysBoneCollisionCheckCount, FormatCount(stats.physBone?.collisionCheckCount));
                    AddRow(result.perfRows, stats, "コンタクト数", AvatarPerformanceCategory.ContactCount, FormatCount(stats.contactCount));
                    AddRow(result.perfRows, stats, "コンストレイント数", AvatarPerformanceCategory.ConstraintsCount, FormatCount(stats.constraintsCount));
                    AddRow(result.perfRows, stats, "アニメーター数", AvatarPerformanceCategory.AnimatorCount, FormatCount(stats.animatorCount));
                    AddRow(result.perfRows, stats, "パーティクルシステム数", AvatarPerformanceCategory.ParticleSystemCount, FormatCount(stats.particleSystemCount));

                    // 総合ランクとアップロード可否
                    PerformanceRating overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
                    result.overallRating = overall.ToString();
                    // アップロード可否はランクでは決まらない。SDKがハードブロックするのはダウンロードサイズ上限
                    // (QuestLimits.HardDownloadSizeCapMB)超過のみ。実際の可否は下のサイズ推定後に確定する。
                    result.canUploadToAndroid = true;

                    // テクスチャのインポート設定に関する警告も除去後の複製に対して収集する
                    // (依存関係ベースの収集のため、除外サブツリーのみが参照するテクスチャを自然に除外できる)
                    CollectTextureWarnings(temp, result.textureWarnings);
                }
                finally
                {
                    if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
                }

                // ---- 以降はシーン上の元アバターに対する列挙(UIからオブジェクトを参照できるよう
                //      複製ではなく実体を返す)。除外サブツリー配下の項目はスキップする ----
                HashSet<Transform> excludedRoots = CollectExcludedRoots(avatar.transform, settings);

                // 非モバイルシェーダーのマテリアル(非アクティブ子も含む全レンダラー、重複なし)
                CollectNonMobileMaterials(avatar.gameObject, excludedRoots, result.nonMobileMaterials);

                // Android非対応コンポーネント(除外サブツリー配下はビルドで消えるため対象外)
                foreach (Component comp in QuestCompat.FindUnsupportedComponents(avatar.gameObject))
                {
                    if (comp == null) continue;
                    if (IsExcluded(comp.transform, avatar.transform, excludedRoots)) continue;
                    result.unsupportedComponents.Add(comp);
                }

                // ---- ダウンロードサイズ推定(失敗しても診断全体は継続する)----
                try
                {
                    result.sizeEstimate = QuestSizeEstimator.Estimate(avatar.gameObject, settings);
                }
                catch (Exception sizeEx)
                {
                    Debug.LogWarning("[RARA QuestConverter] サイズ推定中にエラーが発生しました: " + sizeEx);
                    result.sizeEstimate = null;
                    result.textureWarnings.Add("サイズ推定に失敗しました: " + sizeEx.Message);
                }

                // アップロード可否はサイズのみで判定する(ランク非依存)。SDKは圧縮後ダウンロードサイズ上限
                // (QuestLimits.HardDownloadSizeCapMB=10MB)と展開後(非圧縮)サイズ上限
                // (QuestLimits.HardUncompressedSizeCapMB=40MB)のいずれかの超過でアップロードをブロックする。
                // どちらか一方でも超過見込みなら不可。推定失敗時はブロック扱いにしない。
                result.canUploadToAndroid = !(result.sizeEstimate != null &&
                    (result.sizeEstimate.overCap || result.sizeEstimate.overUncompressedCap));
            }
            catch (Exception ex)
            {
                // UIへは例外を投げない。エラーメッセージだけ返す。
                Debug.LogError("[RARA QuestConverter] 診断中にエラーが発生しました: " + ex);
                result.perfRows = new List<DiagnosticsRow>();
                result.overallRating = string.Empty;
                result.canUploadToAndroid = false;
                result.nonMobileMaterials = new List<Material>();
                result.unsupportedComponents = new List<Component>();
                result.sizeEstimate = null;
                result.textureWarnings = new List<string>
                {
                    "診断中にエラーが発生しました: " + ex.Message
                };
            }

            return result;
        }

        // ================================================================
        // Quest除外(EditorOnly / questExcludePaths)の扱い
        // ================================================================

        /// <summary>
        /// 診断用の一時複製から、EditorOnlyタグ付きサブツリーと settings.questExcludePaths で
        /// 指定されたサブツリーを削除する(VRChatビルドの除外を再現)。ルート自身は削除しない。
        /// </summary>
        private static void DestroyExcludedSubtrees(GameObject tempRoot, QuestConvertSettings settings)
        {
            var doomed = new List<GameObject>();

            // (a) EditorOnlyタグ付きサブツリー
            foreach (Transform t in tempRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == tempRoot.transform) continue;
                if (t.CompareTag(QuestCompat.EditorOnlyTag)) doomed.Add(t.gameObject);
            }

            // (b) settings.questExcludePaths(temp ルート基準で解決)
            if (settings != null && settings.questExcludePaths != null)
            {
                foreach (string path in settings.questExcludePaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    Transform found = QuestCompat.FindByPath(tempRoot.transform, path);
                    if (found != null && found != tempRoot.transform) doomed.Add(found.gameObject);
                }
            }

            foreach (GameObject go in doomed)
            {
                // 祖先側が先に削除されていると null になっているためガード
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>settings.questExcludePaths を root 基準で解決した除外サブツリーのルート集合を返す(ルート自身は含めない)。</summary>
        private static HashSet<Transform> CollectExcludedRoots(Transform root, QuestConvertSettings settings)
        {
            var excludedRoots = new HashSet<Transform>();
            if (root == null || settings == null || settings.questExcludePaths == null) return excludedRoots;

            foreach (string path in settings.questExcludePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                Transform found = QuestCompat.FindByPath(root, path);
                if (found != null && found != root) excludedRoots.Add(found);
            }
            return excludedRoots;
        }

        /// <summary>
        /// tがroot配下の除外サブツリー(EditorOnlyタグ付き、または除外パス指定)に含まれるか。
        /// rootまで遡って判定を打ち切る。
        /// </summary>
        private static bool IsExcluded(Transform t, Transform root, HashSet<Transform> excludedRoots)
        {
            Transform current = t;
            while (current != null)
            {
                if (current.CompareTag(QuestCompat.EditorOnlyTag)) return true;
                if (excludedRoots != null && excludedRoots.Contains(current)) return true;
                if (current == root) break;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// VRC.Dynamics.VRCConstraintManager.Sdk_ManuallyRefreshGroups(VRCConstraintBase[]) をリフレクションで呼び出す。
        /// 型は internal(InternalsVisibleTo で SDK アセンブリのみ公開)のため直接参照できない。
        /// SDKバージョン差異で見つからない場合は警告ログのみ残して続行する(統計のコンストレイント数が不正確になる可能性あり)。
        /// </summary>
        private static void RefreshConstraintGroups(VRCConstraintBase[] vrcConstraints)
        {
            var managerType = QuestCompat.FindType("VRC.Dynamics.VRCConstraintManager");
            var method = managerType != null
                ? managerType.GetMethod(
                    "Sdk_ManuallyRefreshGroups",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(VRCConstraintBase[]) },
                    null)
                : null;
            if (method != null)
            {
                method.Invoke(null, new object[] { vrcConstraints });
            }
            else
            {
                Debug.LogWarning("[RARA QuestConverter] VRCConstraintManager.Sdk_ManuallyRefreshGroups が見つかりませんでした。コンストレイント数の計上が不正確になる可能性があります。");
            }
        }

        /// <summary>カテゴリのランクを引いて1行追加する。</summary>
        private static void AddRow(List<DiagnosticsRow> rows, AvatarPerformanceStats stats, string label, AvatarPerformanceCategory category, string value)
        {
            PerformanceRating rating = stats.GetPerformanceRatingForCategory(category);
            rows.Add(new DiagnosticsRow
            {
                category = label,
                value = value,
                rating = rating.ToString(),
                overLimit = rating == PerformanceRating.VeryPoor,
            });
        }

        /// <summary>Nullableの個数値を表示用文字列にする(未計測は「-」)。</summary>
        private static string FormatCount(int? value)
        {
            return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "-";
        }

        /// <summary>NullableのMB値を表示用文字列にする(未計測は「-」)。</summary>
        private static string FormatMegabytes(float? value)
        {
            return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) + " MB" : "-";
        }

        /// <summary>非アクティブ含む全レンダラーから、Android許可シェーダー以外のマテリアルを重複なしで集める(除外サブツリー配下は対象外)。</summary>
        private static void CollectNonMobileMaterials(GameObject root, HashSet<Transform> excludedRoots, List<Material> output)
        {
            var seen = new HashSet<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (IsExcluded(renderer.transform, root.transform, excludedRoots)) continue; // ビルドで除去されるためスキップ
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue; // 空スロットはスキップ
                    if (!seen.Add(mat)) continue;
                    // シェーダーがnull(欠落)の場合も非モバイル扱い(IsMobileShaderはnullでfalseを返す)
                    if (!QuestCompat.IsMobileShader(mat.shader))
                    {
                        output.Add(mat);
                    }
                }
            }
        }

        /// <summary>依存テクスチャのインポート設定を調べて警告を集める(上限件数超過分は「他N件」に集約)。</summary>
        private static void CollectTextureWarnings(GameObject root, List<string> output)
        {
            var warnings = new List<string>();
            var seenPaths = new HashSet<string>();
            Object[] dependencies = EditorUtility.CollectDependencies(new Object[] { root });

            foreach (var dep in dependencies)
            {
                var tex = dep as Texture2D;
                if (tex == null) continue;

                string path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (!seenPaths.Add(path)) continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                // Android用の上書き設定がなく、最大サイズが1024を超えている場合は警告
                TextureImporterPlatformSettings androidSettings = importer.GetPlatformTextureSettings("Android");
                bool hasAndroidOverride = androidSettings != null && androidSettings.overridden;
                if (!hasAndroidOverride && importer.maxTextureSize > 1024)
                {
                    warnings.Add(string.Format("「{0}」: Android用の縮小設定なし(現在{1})", tex.name, importer.maxTextureSize));
                }

                // 圧縮形式に関する補足(任意チェック)
                if (importer.textureCompression == TextureImporterCompression.Uncompressed)
                {
                    warnings.Add(string.Format("「{0}」: 非圧縮形式です(容量削減のため圧縮を推奨)", tex.name));
                }
                else if (importer.crunchedCompression)
                {
                    warnings.Add(string.Format("「{0}」: Crunch圧縮が有効です(AndroidのASTC圧縮では効果がないため注意)", tex.name));
                }
            }

            // 表示上限で切り、残りは「他N件」に集約
            if (warnings.Count > TextureWarningDisplayMax)
            {
                int rest = warnings.Count - TextureWarningDisplayMax;
                warnings.RemoveRange(TextureWarningDisplayMax, rest);
                warnings.Add(string.Format("他{0}件", rest));
            }
            output.AddRange(warnings);
        }
    }
}
#endif
