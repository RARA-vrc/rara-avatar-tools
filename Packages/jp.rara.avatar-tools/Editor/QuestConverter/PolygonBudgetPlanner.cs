// RARA Quest Converter - ポリゴン削減の配分計画(バジェットプランナー)
// アバターの現在の三角形数を「診断と同じ数え方」で計測し、目標三角形数(Questランク)へ
// 収まるよう、レンダラーごとの削減目標を決める。顔・髪はカテゴリ品質下限で強く保護する。
// Unity 2022.3 / VRChat Avatars SDK 3.10.4。Assembly-CSharp-Editor でコンパイルされる(asmdefなし)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter.Decimation
{
    /// <summary>
    /// ポリゴン削減の配分計画1件(レンダラー1つぶん)。UI表示と変換の両方で使う。
    /// currentTris は診断と同じ除外条件(EditorOnly / Quest除外)で数えた現在の三角形数、
    /// targetTris は配分後の目標三角形数、category は保護カテゴリ(顔/髪/素体/衣装/その他)、
    /// qualityFloor はそのカテゴリの品質下限(=targetTris/currentTris がこれを下回らないよう保護)。
    /// </summary>
    public sealed class PolygonPlanEntry
    {
        /// <summary>対象レンダラーのアバタールート相対パス(QuestCompat.GetRelativePath)。</summary>
        public string rendererPath;

        /// <summary>診断と同じ除外条件で数えた現在の三角形数。</summary>
        public int currentTris;

        /// <summary>配分後の目標三角形数(この数以下へ簡略化する)。</summary>
        public int targetTris;

        /// <summary>保護カテゴリ(顔/髪/素体/衣装/その他)。UIのバッジ表示に使う。</summary>
        public string category;

        /// <summary>カテゴリの品質下限(0..1)。削減はこの品質(残す割合)を下回らないよう配分する。</summary>
        public float qualityFloor;
    }

    /// <summary>
    /// アバターの現在ポリゴン数を診断と同じ数え方で計測し、目標三角形数へ収まるよう
    /// レンダラーごとの削減目標を配分するプランナー。
    ///
    /// 【数え方】診断(QuestDiagnostics)・サイズ推定(QuestSizeEstimator)と同じく、
    ///   EditorOnly タグ付きサブツリーと settings.questExcludePaths 配下のレンダラーは
    ///   「ビルドで除去される」ものとして数えない(→ UIの現在ポリゴン数と一致する)。
    ///   透過自動非表示(Hideモード)は診断ヘッドラインでも適用されないため、ここでも適用しない
    ///   (対象は小型オーバーレイのみで寄与は僅少・変換側 step 3.8 が EditorOnly を再度ガードする)。
    ///
    /// 【カテゴリと品質下限】顔=0.85 / 髪=0.65 / 素体=0.40 / 衣装=0.45 / その他=0.30。
    ///   品質=残す三角形の割合。顔は VRCAvatarDescriptor.VisemeSkinnedMesh(+まぶたメッシュ)や
    ///   名前トークン(face/顔/head)で判定し最も強く保護する。髪は QuestCompat の髪名判定。
    ///
    /// 【配分】現在合計 <= 目標なら削減不要(空の計画を返す)。超過時は優先度の低い順
    ///   (その他→衣装→素体→髪→顔)に、各カテゴリを品質下限まで、目標に届くぶんだけ削る。
    ///   全カテゴリを下限まで削っても超過する場合は、顔以外の下限を比例で下回らせて詰める
    ///   (顔の下限は不可侵)。それでも顔の下限だけで目標を超える場合は顔以外を最小(サブメッシュ数)まで
    ///   削り、UI側で「目標未達(手動作業が必要)」を赤字警告する。
    /// </summary>
    public static class PolygonBudgetPlanner
    {
        // ---- 保護カテゴリ名(UIのバッジ表示とも共有する短い日本語) ----
        public const string CategoryFace = "顔";
        public const string CategoryHair = "髪";
        public const string CategoryBody = "素体";
        public const string CategoryClothes = "衣装";
        public const string CategoryOther = "その他";

        // ---- カテゴリ別の品質下限(残す三角形の割合の最小値) ----
        public const float FaceQualityFloor = 0.85f;
        public const float HairQualityFloor = 0.65f;
        public const float BodyQualityFloor = 0.40f;
        public const float ClothesQualityFloor = 0.45f;
        public const float OtherQualityFloor = 0.30f;

        // 顔(名前フォールバック)/素体/衣装 の名前トークン(部分一致・大文字小文字無視)。
        // 顔レンダラーはまずデスクリプター参照(VisemeSkinnedMesh/まぶた)で判定し、無ければ名前で判定する。
        private static readonly string[] FaceNameTokens = { "face", "顔", "head", "ヘッド", "頭" };
        private static readonly string[] BodyNameTokens = { "body", "ボディ", "素体", "素肌", "肌", "skin", "torso", "胴" };
        private static readonly string[] ClothesNameTokens =
        {
            "cloth", "clothes", "衣装", "服", "outfit", "costume", "dress", "ワンピ",
            "skirt", "スカート", "shirt", "シャツ", "tops", "トップス", "bottoms", "ボトムス",
            "pants", "パンツ", "ズボン", "shorts", "shoes", "靴", "boots", "ブーツ",
            "sock", "socks", "靴下", "ニーソ", "coat", "コート", "jacket", "ジャケット",
            "sweater", "セーター", "hoodie", "パーカー", "underwear", "下着", "bra", "水着", "swimsuit",
        };

        /// <summary>
        /// アバターの現在の三角形数を計測し、totalBudget へ収まるよう削減計画を組み立てて返す。
        /// 現在合計が totalBudget 以下なら空リスト(削減不要)を返す。返す計画には実際に削減される
        /// レンダラー(targetTris &lt; currentTris)のみを含める。avatarRoot が null なら空リスト。
        /// </summary>
        public static List<PolygonPlanEntry> BuildPlan(GameObject avatarRoot, int totalBudget, QuestConvertSettings settings)
        {
            var plan = new List<PolygonPlanEntry>();
            if (avatarRoot == null) return plan;
            if (settings == null) settings = new QuestConvertSettings();
            if (totalBudget < 1) totalBudget = 1;

            // ---- 顔レンダラーの特定(デスクリプター参照。名前フォールバックは Classify 内で判定) ----
            HashSet<Renderer> faceRenderers = CollectDescriptorFaceRenderers(avatarRoot);

            // ---- Quest除外サブツリー(EditorOnly は Transform 直判定するためここでは questExcludePaths のみ) ----
            List<Transform> excludedRoots = ResolveExcludedRoots(avatarRoot.transform, settings);

            // ---- 集計対象レンダラー(診断・サイズ推定と同じ除外条件) ----
            var entries = new List<WorkEntry>();
            long total = 0;
            foreach (Renderer renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsInEditorOnlySubtree(renderer.transform, avatarRoot.transform)) continue;
                if (IsUnderAny(renderer.transform, excludedRoots)) continue;

                Mesh mesh = GetRendererMesh(renderer);
                int tris = GetMeshTriangleCount(mesh);
                if (tris <= 0) continue;

                int subMeshCount = mesh != null ? Math.Max(1, mesh.subMeshCount) : 1;
                string category = Classify(renderer, mesh, faceRenderers);
                float floor = QualityFloorFor(category);
                // 品質下限まで削ったときに残る三角形数(サブメッシュあたり最低1枚は残す)。
                int minTris = Math.Max(subMeshCount, Mathf.CeilToInt(tris * floor));
                if (minTris > tris) minTris = tris; // 下限が現在数を超えることはないが安全側にクランプ

                string path = QuestCompat.GetRelativePath(avatarRoot.transform, renderer.transform);
                if (path == null) continue; // ルート配下でない(通常起きない)

                entries.Add(new WorkEntry
                {
                    rendererPath = path,
                    currentTris = tris,
                    targetTris = tris,
                    category = category,
                    qualityFloor = floor,
                    minTris = minTris,
                    subMeshCount = subMeshCount,
                });
                total += tris;
            }

            // 現在合計が目標以下なら削減不要。
            if (total <= totalBudget) return plan;

            // ---- 配分: 優先度の低い順に、各カテゴリを下限まで、目標に届くぶんだけ削る ----
            string[] reduceOrder = { CategoryOther, CategoryClothes, CategoryBody, CategoryHair, CategoryFace };
            foreach (string cat in reduceOrder)
            {
                long need = total - totalBudget;
                if (need <= 0) break;

                var group = entries.Where(e => e.category == cat && e.targetTris > e.minTris).ToList();
                long reducible = group.Sum(e => (long)(e.targetTris - e.minTris));
                if (reducible <= 0) continue;

                long cut = Math.Min(need, reducible);
                ReduceGroupProportional(group, cut);
                total = entries.Sum(e => (long)e.targetTris);
            }

            // ---- 全カテゴリ下限でも超過する場合: 顔以外の下限を比例で下回らせて詰める(顔は不可侵) ----
            if (total > totalBudget)
            {
                long faceFloorTotal = entries.Where(e => e.category == CategoryFace).Sum(e => (long)e.targetTris);
                var nonFace = entries.Where(e => e.category != CategoryFace).ToList();
                long nonFaceFloorTotal = nonFace.Sum(e => (long)e.targetTris);
                long nonFaceHardMin = nonFace.Sum(e => (long)e.subMeshCount);
                long remainingForNonFace = totalBudget - faceFloorTotal;

                if (remainingForNonFace <= nonFaceHardMin || nonFaceFloorTotal <= 0)
                {
                    // 顔の下限だけで(ほぼ)目標を食い尽くす → 顔以外を最小(サブメッシュ数)まで削る。
                    // これでも超過するなら目標未達(UI側が赤字警告)。顔はこれ以上削らない。
                    foreach (var e in nonFace) e.targetTris = e.subMeshCount;
                }
                else
                {
                    // 顔以外の下限三角形数を、残り予算に収まるよう比例縮小する(サブメッシュ数は下回らない)。
                    double scale = (double)remainingForNonFace / nonFaceFloorTotal;
                    foreach (var e in nonFace)
                    {
                        int scaled = (int)Math.Floor(e.targetTris * scale);
                        e.targetTris = Math.Max(e.subMeshCount, scaled);
                    }
                }
            }

            // ---- 実際に削減されるレンダラーのみを計画へ(targetTris < currentTris) ----
            foreach (var e in entries)
            {
                if (e.targetTris >= e.currentTris) continue;
                plan.Add(new PolygonPlanEntry
                {
                    rendererPath = e.rendererPath,
                    currentTris = e.currentTris,
                    targetTris = e.targetTris,
                    category = e.category,
                    qualityFloor = e.qualityFloor,
                });
            }
            return plan;
        }

        /// <summary>
        /// 現在の削減対象レンダラーの三角形数合計を診断と同じ除外条件で数えて返す(UIの現在ポリゴン数用)。
        /// avatarRoot が null / 設定 null 安全。
        /// </summary>
        public static int CountCurrentTriangles(GameObject avatarRoot, QuestConvertSettings settings)
        {
            if (avatarRoot == null) return 0;
            if (settings == null) settings = new QuestConvertSettings();
            List<Transform> excludedRoots = ResolveExcludedRoots(avatarRoot.transform, settings);
            long total = 0;
            foreach (Renderer renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsInEditorOnlySubtree(renderer.transform, avatarRoot.transform)) continue;
                if (IsUnderAny(renderer.transform, excludedRoots)) continue;
                total += GetMeshTriangleCount(GetRendererMesh(renderer));
            }
            return (int)Math.Min(total, int.MaxValue);
        }

        // ================================================================
        // 配分ヘルパー
        // ================================================================

        /// <summary>
        /// group の各エントリから合計 cut 三角形を削る(残す割合の余裕 targetTris-minTris に比例配分)。
        /// 整数配分の端数は最大剰余法で決定的に割り当て、どのエントリも minTris を下回らせない。
        /// cut が全余裕以上なら全エントリを minTris へ。cut<=0 / 空 group は何もしない。
        /// </summary>
        private static void ReduceGroupProportional(List<WorkEntry> group, long cut)
        {
            if (cut <= 0 || group.Count == 0) return;

            long totalHeadroom = group.Sum(e => (long)(e.targetTris - e.minTris));
            if (totalHeadroom <= 0) return;
            if (cut >= totalHeadroom)
            {
                foreach (var e in group) e.targetTris = e.minTris;
                return;
            }

            // 決定的な処理順(余裕の大きい順 → パス昇順)。
            var ordered = group
                .OrderByDescending(e => e.targetTris - e.minTris)
                .ThenBy(e => e.rendererPath, StringComparer.Ordinal)
                .ToList();

            var reduce = new Dictionary<WorkEntry, int>();
            var fractions = new List<KeyValuePair<WorkEntry, double>>();
            long allocated = 0;
            foreach (var e in ordered)
            {
                int headroom = e.targetTris - e.minTris;
                double exact = (double)cut * headroom / totalHeadroom;
                int floor = (int)Math.Floor(exact);
                if (floor > headroom) floor = headroom;
                reduce[e] = floor;
                allocated += floor;
                fractions.Add(new KeyValuePair<WorkEntry, double>(e, exact - floor));
            }

            long leftover = cut - allocated;
            // 端数の大きい順(→パス昇順)に +1 を配る。cut<totalHeadroom のため必ず配りきれる。
            fractions.Sort((a, b) =>
            {
                int byFrac = b.Value.CompareTo(a.Value);
                if (byFrac != 0) return byFrac;
                return string.CompareOrdinal(a.Key.rendererPath, b.Key.rendererPath);
            });
            while (leftover > 0)
            {
                bool placedAny = false;
                foreach (var kv in fractions)
                {
                    if (leftover == 0) break;
                    WorkEntry e = kv.Key;
                    if (reduce[e] < e.targetTris - e.minTris)
                    {
                        reduce[e]++;
                        leftover--;
                        placedAny = true;
                    }
                }
                if (!placedAny) break; // 保険(理論上到達しない)
            }

            foreach (var e in ordered) e.targetTris -= reduce[e];
        }

        // ================================================================
        // カテゴリ分類
        // ================================================================

        /// <summary>
        /// レンダラーを保護カテゴリへ分類する。優先度: 顔(デスクリプター参照/名前) → 髪 → 素体 → 衣装 → その他。
        /// 顔・髪は最優先で保護判定し、素体・衣装は名前トークンで判定、いずれにも当たらなければ その他。
        /// </summary>
        private static string Classify(Renderer renderer, Mesh mesh, HashSet<Renderer> faceRenderers)
        {
            if (faceRenderers.Contains(renderer)) return CategoryFace;

            string goName = renderer.gameObject.name;
            string meshName = mesh != null ? mesh.name : null;

            if (ContainsAnyToken(goName, FaceNameTokens) || ContainsAnyToken(meshName, FaceNameTokens))
                return CategoryFace;

            if (IsHairRenderer(renderer, mesh)) return CategoryHair;

            if (ContainsAnyToken(goName, BodyNameTokens) || ContainsAnyToken(meshName, BodyNameTokens))
                return CategoryBody;

            if (MaterialsMatch(renderer, BodyNameTokens)) return CategoryBody;

            if (ContainsAnyToken(goName, ClothesNameTokens) || ContainsAnyToken(meshName, ClothesNameTokens))
                return CategoryClothes;

            if (MaterialsMatch(renderer, ClothesNameTokens)) return CategoryClothes;

            return CategoryOther;
        }

        /// <summary>髪判定(QuestCompat.IsHairLikeName を GameObject名・メッシュ名・マテリアル名に適用)。</summary>
        private static bool IsHairRenderer(Renderer renderer, Mesh mesh)
        {
            if (QuestCompat.IsHairLikeName(renderer.gameObject.name)) return true;
            if (mesh != null && QuestCompat.IsHairLikeName(mesh.name)) return true;
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null && QuestCompat.IsHairLikeName(material.name)) return true;
            }
            return false;
        }

        private static bool MaterialsMatch(Renderer renderer, string[] tokens)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null && ContainsAnyToken(material.name, tokens)) return true;
            }
            return false;
        }

        private static bool ContainsAnyToken(string name, string[] tokens)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string token in tokens)
            {
                if (!string.IsNullOrEmpty(token) && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static float QualityFloorFor(string category)
        {
            switch (category)
            {
                case CategoryFace: return FaceQualityFloor;
                case CategoryHair: return HairQualityFloor;
                case CategoryBody: return BodyQualityFloor;
                case CategoryClothes: return ClothesQualityFloor;
                default: return OtherQualityFloor;
            }
        }

        // ================================================================
        // 顔レンダラーの特定(VRCAvatarDescriptor 参照)
        // ================================================================

        /// <summary>
        /// アバタールート配下の VRCAvatarDescriptor から顔レンダラー(表情・ビセーム・まぶた保持メッシュ)を集める。
        /// ・VisemeSkinnedMesh: リップシンク(ビセーム/あごブレンドシェイプ)の口メッシュ
        /// ・customEyeLookSettings.eyelidsSkinnedMesh: まぶた(まばたき)ブレンドシェイプメッシュ
        /// いずれも表情の要となるため最も強い保護カテゴリ(顔)へ入れる。デスクリプター無しなら空集合。
        /// </summary>
        private static HashSet<Renderer> CollectDescriptorFaceRenderers(GameObject avatarRoot)
        {
            var set = new HashSet<Renderer>();
            var descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor == null) return set;

            if (descriptor.VisemeSkinnedMesh != null) set.Add(descriptor.VisemeSkinnedMesh);

            var eye = descriptor.customEyeLookSettings;
            if (eye.eyelidsSkinnedMesh != null) set.Add(eye.eyelidsSkinnedMesh);

            return set;
        }

        // ================================================================
        // 除外・メッシュ計測ヘルパー(AvatarQuestConverter/QuestSizeEstimator と同じ規則を複製)
        // ================================================================

        /// <summary>questExcludePaths を root 上で解決する(ルート自身は除外対象にしない)。</summary>
        private static List<Transform> ResolveExcludedRoots(Transform root, QuestConvertSettings settings)
        {
            var excluded = new List<Transform>();
            if (settings.questExcludePaths == null) return excluded;
            foreach (string path in settings.questExcludePaths)
            {
                if (path == null) continue;
                Transform target = QuestCompat.FindByPath(root, path);
                if (target != null && target != root) excluded.Add(target);
            }
            return excluded;
        }

        /// <summary>t が roots のいずれかのサブツリー(自身または子孫)に含まれるか。</summary>
        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            if (roots == null) return false;
            foreach (Transform root in roots)
            {
                if (root != null && (t == root || t.IsChildOf(root))) return true;
            }
            return false;
        }

        /// <summary>t が root 配下の EditorOnly サブツリー(自身または祖先に EditorOnly タグ)に含まれるか。</summary>
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

        /// <summary>レンダラーが参照する共有メッシュを返す(SkinnedMeshRenderer / MeshFilter)。</summary>
        private static Mesh GetRendererMesh(Renderer renderer)
        {
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>メッシュの総三角形数(全サブメッシュのインデックス数合計 ÷ 3)。Read/Write設定に依存しない。null なら 0。</summary>
        private static int GetMeshTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                total += mesh.GetIndexCount(i);
            }
            return (int)Math.Min(total / 3, int.MaxValue);
        }

        // ================================================================
        // 内部作業用エントリ(配分計算中の可変状態を持つ)
        // ================================================================
        private sealed class WorkEntry
        {
            public string rendererPath;
            public int currentTris;
            public int targetTris;
            public string category;
            public float qualityFloor;
            public int minTris;      // 品質下限まで削ったときに残る三角形数
            public int subMeshCount; // サブメッシュ数(絶対最小=各サブメッシュ1枚)
        }
    }
}
#endif
