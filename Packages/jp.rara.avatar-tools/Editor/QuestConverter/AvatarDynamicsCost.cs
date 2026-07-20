// RARA Quest Converter - Avatar Dynamics(PhysBone/コライダー/コンタクト)の制約コスト計算(1.11.0)
// VRChat公式のパフォーマンス統計(モバイルPoor上限: コンポーネント8 / 影響Transform64 /
// コライダー16 / 衝突チェック64 / コンタクト16)は、どれか1分野でも超過すると実機で
// 揺れもの(PhysBone・コンタクト・コンストレイント)が全停止する。自動選定([A])で5制約を
// すべて満たすため、SDKと同じ式で各PhysBone/マージ後ユニットのコストを見積もる。
//
// 【式の出所】VRChat SDK の VRC.Dynamics(CalculatePerformanceStats)がグラウンドトゥルース。
// 本ファイルはその式を、コミュニティ実装(KRT VRCQuestTools AvatarDynamics.cs)で確認できた
// とおりに移植したもの:
//  ・影響Transform数(PB毎) = CountChildrenRecursive(rootTransform, ignoreTransforms) + 1
//      CountChildrenRecursive は ignoreTransforms に一致する子をサブツリーごとスキップする。
//  ・コライダー数(アバター全体・重複排除) = いずれかのPBの Colliders に参照されるコライダーの異なり数。
//  ・衝突チェック数(PB毎) = adjTransform × colliderCount。
//      adjTransform = 影響Transform数 - 1(ルート自身を除く)。
//      multiChildRoot(子が2以上かつ非ignore直下子が2以上)の子数を各々減算。
//      MultiChildType != Ignore なら multiChildRoot 数を加算。
//      EndpointPosition.magnitude > 0 なら、末端(childCount==0・非ignore・非EditorOnly)数を加算。
//      colliderCount = そのPBの Colliders のうち非null・アバターのコライダー集合に含まれる異なり数。
//  ・コンタクト数 = ローカル専用でない ContactSender/ContactReceiver の数(ローカル専用は無料)。
//
// マージ後(POST-MERGE)ユニットは、ComponentRemover.MergePhysBoneGroup と同じ形
// (rootTransform=共通親 / multiChildType=Ignore / ignoreTransforms=非メンバー子∪各メンバーの
//  チェーン内ignore / colliders・endpoint=先頭メンバー)を再現した仮想シェイプで見積もる。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace RARA.QuestConverter
{
    /// <summary>Avatar Dynamics の5制約の使用量(現在の選択で残る見込みの値)。</summary>
    public struct AvatarDynamicsUsage
    {
        /// <summary>PhysBoneコンポーネント数。</summary>
        public int physBoneComponents;
        /// <summary>PhysBoneの影響Transform数(全PBの合計)。</summary>
        public int physBoneTransforms;
        /// <summary>参照されるコライダーの異なり数(アバター全体・重複排除)。</summary>
        public int physBoneColliders;
        /// <summary>衝突チェック数(全PBの合計)。</summary>
        public int physBoneCollisionChecks;
        /// <summary>コンタクト数(ローカル専用でない Sender/Receiver)。</summary>
        public int contacts;
    }

    /// <summary>Avatar Dynamics の5制約の上限値(対象=Quest / PCランクで切り替わる)。</summary>
    public struct AvatarDynamicsLimits
    {
        public int physBoneComponents;
        public int physBoneTransforms;
        public int physBoneColliders;
        public int physBoneCollisionChecks;
        public int contacts;

        /// <summary>QuestのPoor上限(8 / 64 / 16 / 64 / 16)。どれか1つでも超過で実機の揺れもの全停止。</summary>
        public static AvatarDynamicsLimits Quest()
        {
            return new AvatarDynamicsLimits
            {
                physBoneComponents = QuestLimits.PoorPhysBoneComponents,
                physBoneTransforms = QuestLimits.PoorPhysBoneTransforms,
                physBoneColliders = QuestLimits.PoorPhysBoneColliders,
                physBoneCollisionChecks = 64, // QuestのPhysBone衝突チェックPoor上限(公式)
                contacts = QuestLimits.PoorContacts,
            };
        }

        /// <summary>PC(Windows)の指定ランクの上限(PCRankLimits の該当項目から取得)。</summary>
        public static AvatarDynamicsLimits Pc(RARA.PCOptimizer.PCTargetRank rank)
        {
            return new AvatarDynamicsLimits
            {
                physBoneComponents = RARA.PCOptimizer.PCRankLimits.GetLimit(rank, RARA.PCOptimizer.PCRankLimits.PCStat.PhysBoneComponents),
                physBoneTransforms = RARA.PCOptimizer.PCRankLimits.GetLimit(rank, RARA.PCOptimizer.PCRankLimits.PCStat.PhysBoneTransforms),
                physBoneColliders = RARA.PCOptimizer.PCRankLimits.GetLimit(rank, RARA.PCOptimizer.PCRankLimits.PCStat.PhysBoneColliders),
                physBoneCollisionChecks = RARA.PCOptimizer.PCRankLimits.GetLimit(rank, RARA.PCOptimizer.PCRankLimits.PCStat.PhysBoneCollisionChecks),
                contacts = RARA.PCOptimizer.PCRankLimits.GetLimit(rank, RARA.PCOptimizer.PCRankLimits.PCStat.Contacts),
            };
        }
    }

    /// <summary>1ユニット(単独PB、またはマージ後1本)のPhysBoneコスト。</summary>
    public struct PhysBoneUnitCost
    {
        /// <summary>影響Transform数。</summary>
        public int transforms;
        /// <summary>衝突チェック数。</summary>
        public int collisionChecks;
        /// <summary>このユニットが参照するコライダー(重複排除用)。</summary>
        public HashSet<VRCPhysBoneColliderBase> colliders;
    }

    /// <summary>
    /// SDKと同じ式で PhysBone/コライダー/コンタクトのコストを見積もる静的ヘルパー(自動選定[A]・メーター[D]共通)。
    /// アバター(または複製)への変更は一切行わない(読み取りのみ)。
    /// </summary>
    public static class AvatarDynamicsCost
    {
        // ================================================================
        // コライダー集合・PhysBone識別マップ
        // ================================================================

        /// <summary>アバター配下の非EditorOnlyな VRCPhysBoneCollider の集合(衝突チェックの「アバターのコライダー」判定用)。</summary>
        public static HashSet<VRCPhysBoneColliderBase> BuildAvatarColliderSet(GameObject root, List<Transform> excludedRoots = null)
        {
            var set = new HashSet<VRCPhysBoneColliderBase>();
            if (root == null) return set;
            foreach (VRCPhysBoneCollider c in root.GetComponentsInChildren<VRCPhysBoneCollider>(true))
            {
                if (c == null) continue;
                if (IsFinallyEditorOnly(root.transform, c.transform)) continue;
                if (IsUnderAny(c.transform, excludedRoots)) continue;
                set.Add(c);
            }
            return set;
        }

        /// <summary>識別パス(ComponentRemover.GetPhysBoneIdentityPath)→ VRCPhysBone の対応表を作る。</summary>
        public static Dictionary<string, VRCPhysBone> BuildPhysBoneMap(GameObject root)
        {
            var map = new Dictionary<string, VRCPhysBone>(System.StringComparer.Ordinal);
            if (root == null) return map;
            foreach (VRCPhysBone pb in root.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if (pb == null) continue;
                string id = ComponentRemover.GetPhysBoneIdentityPath(root.transform, pb);
                if (id != null && !map.ContainsKey(id)) map[id] = pb;
            }
            return map;
        }

        // ================================================================
        // 1ユニットのコスト(単独 or マージ後)
        // ================================================================

        /// <summary>
        /// members が1本なら単独PBのシェイプ、2本以上ならマージ後シェイプ(共通親・Ignore・ignore和集合・
        /// 先頭メンバーのコライダー/endpoint)で、影響Transform数・衝突チェック数・参照コライダー集合を返す。
        /// </summary>
        public static PhysBoneUnitCost ComputeUnit(List<VRCPhysBone> members, Transform avatarRoot, HashSet<VRCPhysBoneColliderBase> avatarColliders)
        {
            var cost = new PhysBoneUnitCost { colliders = new HashSet<VRCPhysBoneColliderBase>() };
            if (members == null || members.Count == 0) return cost;

            Transform rootTrans;
            List<Transform> ignore;
            VRCPhysBoneBase.MultiChildType mct;
            float endpointMag;
            IEnumerable<VRCPhysBoneColliderBase> colliders;

            if (members.Count == 1)
            {
                VRCPhysBone pb = members[0];
                rootTrans = EffectiveRoot(pb);
                ignore = NonNull(pb.ignoreTransforms);
                mct = pb.multiChildType;
                endpointMag = pb.endpointPosition.magnitude;
                colliders = pb.colliders;
            }
            else
            {
                // マージ後シェイプ(ComponentRemover.MergePhysBoneGroup と同じ再構成)
                VRCPhysBone head = members[0];
                Transform parent = EffectiveRoot(head).parent;
                rootTrans = parent;
                var memberRoots = new HashSet<Transform>();
                foreach (VRCPhysBone m in members) memberRoots.Add(EffectiveRoot(m));
                ignore = new List<Transform>();
                if (parent != null)
                {
                    foreach (Transform child in parent)
                    {
                        if (!memberRoots.Contains(child)) ignore.Add(child);
                    }
                }
                foreach (VRCPhysBone m in members)
                {
                    if (m.ignoreTransforms == null) continue;
                    Transform mRoot = EffectiveRoot(m);
                    foreach (Transform t in m.ignoreTransforms)
                    {
                        if (t != null && t.IsChildOf(mRoot) && !ignore.Contains(t)) ignore.Add(t);
                    }
                }
                mct = VRCPhysBoneBase.MultiChildType.Ignore;
                endpointMag = head.endpointPosition.magnitude;
                colliders = head.colliders;
            }

            if (rootTrans == null) return cost;

            // 参照コライダー集合(非null・アバターのコライダー集合に含まれるもの)
            if (colliders != null)
            {
                foreach (VRCPhysBoneColliderBase c in colliders)
                {
                    if (c != null && avatarColliders.Contains(c)) cost.colliders.Add(c);
                }
            }
            int colliderCount = cost.colliders.Count;

            // 影響Transform数 = CountChildrenRecursive + 1
            int fullTransformCount = CountChildrenRecursive(rootTrans, ignore) + 1;
            cost.transforms = fullTransformCount;

            // 衝突チェック数 = adjTransform × colliderCount
            int adj = fullTransformCount - 1; // ルート自身を除く
            Transform[] descendants = rootTrans.GetComponentsInChildren<Transform>(true);
            var multiChildRoots = new List<Transform>();
            foreach (Transform t in descendants)
            {
                if (IsFinallyEditorOnly(avatarRoot, t)) continue;
                if (IsMultiChildRoot(t, ignore)) multiChildRoots.Add(t);
            }
            foreach (Transform t in multiChildRoots) adj -= t.childCount;
            if (mct != VRCPhysBoneBase.MultiChildType.Ignore) adj += multiChildRoots.Count;
            if (endpointMag > 0f)
            {
                foreach (Transform t in descendants)
                {
                    if (t.childCount != 0) continue;
                    if (ignore.Contains(t)) continue;
                    if (IsFinallyEditorOnly(avatarRoot, t)) continue;
                    adj += 1;
                }
            }
            cost.collisionChecks = Mathf.Max(0, adj) * colliderCount;
            return cost;
        }

        /// <summary>PhysBoneの実効ルート(rootTransform未設定ならコンポーネント自身)。</summary>
        private static Transform EffectiveRoot(VRCPhysBone pb)
        {
            return pb.rootTransform != null ? pb.rootTransform : pb.transform;
        }

        private static List<Transform> NonNull(List<Transform> src)
        {
            var list = new List<Transform>();
            if (src != null)
            {
                foreach (Transform t in src) if (t != null) list.Add(t);
            }
            return list;
        }

        /// <summary>chainRoot 配下のTransform数(自身は含めない)。ignore に一致する子はサブツリーごとスキップ。</summary>
        private static int CountChildrenRecursive(Transform chainRoot, List<Transform> ignore)
        {
            int count = 0;
            foreach (Transform child in chainRoot)
            {
                if (ignore.Contains(child)) continue;
                count++;
                count += CountChildrenRecursive(child, ignore);
            }
            return count;
        }

        /// <summary>t が multiChildRoot(子が2以上、かつ非ignoreな直下子が2以上)か。</summary>
        private static bool IsMultiChildRoot(Transform t, List<Transform> ignore)
        {
            if (t.childCount <= 1) return false;
            int nonIgnored = 0;
            foreach (Transform child in t)
            {
                if (!ignore.Contains(child)) nonIgnored++;
            }
            return nonIgnored > 1;
        }

        /// <summary>自身または(rootまでの)祖先にEditorOnlyタグが付いているか。</summary>
        private static bool IsFinallyEditorOnly(Transform root, Transform t)
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

        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            if (roots == null || t == null) return false;
            foreach (Transform r in roots)
            {
                if (r != null && (t == r || t.IsChildOf(r))) return true;
            }
            return false;
        }

        // ================================================================
        // ユニット構築(プレビュー行 → マージ後ユニット)と使用量集計
        // ================================================================

        /// <summary>1つのプレビュー行を、残す前提でユニット群へ展開する(マージ有効ならグループは1ユニット)。</summary>
        private static List<List<VRCPhysBone>> BuildRowUnits(PhysBonePreviewRow row, Dictionary<string, VRCPhysBone> map, bool mergeOn)
        {
            var units = new List<List<VRCPhysBone>>();
            if (row == null) return units;
            if (row.isGroup)
            {
                var members = new List<VRCPhysBone>();
                if (row.memberPaths != null)
                {
                    foreach (string p in row.memberPaths)
                    {
                        if (p != null && map.TryGetValue(p, out VRCPhysBone pb) && pb != null) members.Add(pb);
                    }
                }
                if (members.Count == 0) return units;
                if (mergeOn) units.Add(members);
                else foreach (VRCPhysBone pb in members) units.Add(new List<VRCPhysBone> { pb });
            }
            else if (row.singlePath != null && map.TryGetValue(row.singlePath, out VRCPhysBone single) && single != null)
            {
                units.Add(new List<VRCPhysBone> { single });
            }
            return units;
        }

        /// <summary>キープ判定(識別パス→残すか)に従って、プレビュー行群をユニット群へ展開する(メーター用)。</summary>
        public static List<List<VRCPhysBone>> BuildKeptUnits(List<PhysBonePreviewRow> rows, Dictionary<string, VRCPhysBone> map, bool mergeOn, Func<string, bool> isKept)
        {
            var units = new List<List<VRCPhysBone>>();
            if (rows == null) return units;
            foreach (PhysBonePreviewRow row in rows)
            {
                if (row == null) continue;
                if (row.isGroup)
                {
                    var members = new List<VRCPhysBone>();
                    if (row.memberPaths != null)
                    {
                        foreach (string p in row.memberPaths)
                        {
                            if (p == null || !isKept(p)) continue;
                            if (map.TryGetValue(p, out VRCPhysBone pb) && pb != null) members.Add(pb);
                        }
                    }
                    if (members.Count == 0) continue;
                    if (mergeOn) units.Add(members);
                    else foreach (VRCPhysBone pb in members) units.Add(new List<VRCPhysBone> { pb });
                }
                else if (row.singlePath != null && isKept(row.singlePath) &&
                         map.TryGetValue(row.singlePath, out VRCPhysBone single) && single != null)
                {
                    units.Add(new List<VRCPhysBone> { single });
                }
            }
            return units;
        }

        /// <summary>ユニット群から PhysBone 4項目(コンポーネント/影響Transform/コライダー/衝突チェック)の使用量を集計する。</summary>
        public static AvatarDynamicsUsage ComputeUsageForUnits(List<List<VRCPhysBone>> units, Transform avatarRoot, HashSet<VRCPhysBoneColliderBase> avatarColliders)
        {
            var usage = new AvatarDynamicsUsage();
            var colliderUnion = new HashSet<VRCPhysBoneColliderBase>();
            if (units != null)
            {
                foreach (List<VRCPhysBone> unit in units)
                {
                    if (unit == null || unit.Count == 0) continue;
                    PhysBoneUnitCost cost = ComputeUnit(unit, avatarRoot, avatarColliders);
                    usage.physBoneComponents += 1;
                    usage.physBoneTransforms += cost.transforms;
                    usage.physBoneCollisionChecks += cost.collisionChecks;
                    if (cost.colliders != null) colliderUnion.UnionWith(cost.colliders);
                }
            }
            usage.physBoneColliders = colliderUnion.Count;
            return usage;
        }

        // ================================================================
        // 自動選定([A]): 優先度順の貪欲選択で PhysBone 4制約をすべて満たす
        // ================================================================

        /// <summary>
        /// sortedRows(優先度昇順)から、コンポーネント/影響Transform/コライダー/衝突チェックの4上限を
        /// すべて満たすよう貪欲に残す行を選ぶ。上限を超える行はスキップし(後続の小さい行に余地を残す)、
        /// 制約になった項目を bindingLabel に返す。keptUsage には選択後の4項目の見込み値を返す。
        /// excludedRoots はコライダー集合から外すQuest除外サブツリー(メーターと同じ集合。配下はビルド時に
        /// EditorOnlyになりカウント外になるため。未指定なら全コライダーを対象にする)。
        /// </summary>
        public static List<PhysBonePreviewRow> GreedySelectRows(
            GameObject root, List<PhysBonePreviewRow> sortedRows, bool mergeOn, AvatarDynamicsLimits limits,
            List<Transform> excludedRoots, out AvatarDynamicsUsage keptUsage, out string bindingLabel)
        {
            keptUsage = new AvatarDynamicsUsage();
            var selected = new List<PhysBonePreviewRow>();
            var colliderUnion = new HashSet<VRCPhysBoneColliderBase>();
            bool bComp = false, bTf = false, bCol = false, bChk = false;

            Dictionary<string, VRCPhysBone> map = BuildPhysBoneMap(root);
            HashSet<VRCPhysBoneColliderBase> avatarColliders = BuildAvatarColliderSet(root, excludedRoots);
            Transform avatarRoot = root != null ? root.transform : null;

            if (sortedRows != null)
            {
                foreach (PhysBonePreviewRow row in sortedRows)
                {
                    List<List<VRCPhysBone>> units = BuildRowUnits(row, map, mergeOn);
                    if (units.Count == 0) continue;

                    int addComp = units.Count;
                    int addTf = 0, addChk = 0;
                    var addColliders = new HashSet<VRCPhysBoneColliderBase>();
                    foreach (List<VRCPhysBone> unit in units)
                    {
                        PhysBoneUnitCost cost = ComputeUnit(unit, avatarRoot, avatarColliders);
                        addTf += cost.transforms;
                        addChk += cost.collisionChecks;
                        if (cost.colliders != null) addColliders.UnionWith(cost.colliders);
                    }
                    int projectedColliders = colliderUnion.Count;
                    foreach (VRCPhysBoneColliderBase c in addColliders) if (!colliderUnion.Contains(c)) projectedColliders++;

                    bool okComp = keptUsage.physBoneComponents + addComp <= limits.physBoneComponents;
                    bool okTf = keptUsage.physBoneTransforms + addTf <= limits.physBoneTransforms;
                    bool okCol = projectedColliders <= limits.physBoneColliders;
                    bool okChk = keptUsage.physBoneCollisionChecks + addChk <= limits.physBoneCollisionChecks;

                    if (okComp && okTf && okCol && okChk)
                    {
                        selected.Add(row);
                        keptUsage.physBoneComponents += addComp;
                        keptUsage.physBoneTransforms += addTf;
                        keptUsage.physBoneCollisionChecks += addChk;
                        colliderUnion.UnionWith(addColliders);
                    }
                    else
                    {
                        if (!okComp) bComp = true;
                        if (!okTf) bTf = true;
                        if (!okCol) bCol = true;
                        if (!okChk) bChk = true;
                    }
                }
            }
            keptUsage.physBoneColliders = colliderUnion.Count;

            var binds = new List<string>();
            if (bComp) binds.Add("コンポーネント");
            if (bTf) binds.Add("影響Transform");
            if (bCol) binds.Add("コライダー");
            if (bChk) binds.Add("チェック");
            bindingLabel = binds.Count > 0 ? string.Join("・", binds) : "なし(すべて収まりました)";
            return selected;
        }

        // ================================================================
        // コンタクト(VRCContactSender / VRCContactReceiver)
        // ================================================================

        /// <summary>コンタクトが公式カウントの対象(ローカル専用でない)か。ローカル専用は無料。</summary>
        public static bool IsCountedContact(ContactBase contact)
        {
            return contact != null && !contact.IsLocalOnly;
        }

        /// <summary>
        /// コンタクトの識別パス(PhysBoneと同じ規則: ルート相対パス + 同一GameObjectに複数ある場合のみ "#序数")。
        /// アバター外・null は null。序数は ContactBase 単位(Sender/Receiver混在でも安定)。
        /// </summary>
        public static string GetContactIdentityPath(Transform avatarRoot, Component contact)
        {
            if (avatarRoot == null || contact == null) return null;
            string basePath = QuestCompat.GetRelativePath(avatarRoot, contact.transform);
            if (basePath == null) return null;
            Component[] siblings = contact.gameObject.GetComponents<ContactBase>();
            if (siblings.Length <= 1) return basePath;
            return basePath + "#" + System.Array.IndexOf(siblings, contact);
        }
    }
}
#endif
