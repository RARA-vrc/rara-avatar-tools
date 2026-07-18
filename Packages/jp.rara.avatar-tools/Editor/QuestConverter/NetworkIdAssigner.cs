// RARA Quest Converter - Network ID割り当て(PC/Quest間の揺れ物の掴み同期の安定化)
// _Quest(Android)クローンと _Opt(PC)クローンで、同じ論理PhysBone(INetworkID)が同じ
// Network IDを持つように、元アバターを基準にした一貫したIDを両クローンへ書き込む。
//
// 【背景】PhysBoneの掴み/ポーズ/ストレッチ状態はNetwork IDをキーにネットワーク同期される。
// IDがクローンごとにアップロード時の自動採番に任されると、PC版とQuest版でPhysBoneの数・並びが
// 異なるため別々のIDが振られ、他ユーザーから見て「掴んでいる揺れ物」がズレる(クロスプラット同期不整合)。
// これを防ぐため、元アバター基準のIDテーブルを作り、両クローンへ同一IDを明示的に書き込む。
//
// 【SDK準拠】ID・型情報の取り扱いはSDK同梱の Network ID Utility
// (com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/VRCNetworkIDUtility.cs)に合わせている:
//  ・エントリは VRC.SDKBase.Network.NetworkIDPair(ID / gameObject / SerializedTypeNames)。
//  ・コレクションは INetworkIDContainer.NetworkIDCollection(List<NetworkIDPair>)。
//  ・型情報は NetworkIDAssignment.GetSerializedTypes(GameObject) で取得。
//  ・新規IDは NetworkIDAssignment.MinID(=10)以上・MaxID(=100000)以下(SDKの有効範囲)。
//  ・gameObject が null になったエントリは除去する(SDK Utility の RemoveAll と同じ後始末)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using VRC.SDKBase.Network;

namespace RARA.QuestConverter
{
    /// <summary>
    /// 元アバターを基準に、クローン(_Quest / _Opt)の VRCAvatarDescriptor へ一貫した
    /// Network ID(INetworkID = VRCPhysBone 等)を割り当てる共有ヘルパー。
    /// QuestConverter と PCOptimizer の両クローンパスから呼ばれる(両方に同じIDを振ることが目的)。
    /// アバターへの構造変更は行わず、クローンの NetworkIDCollection のみを書き換える。
    /// </summary>
    public static class NetworkIdAssigner
    {
        private const string UndoLabel = "Network ID割り当て";
        // パスセグメントの同名兄弟を区別するための内部区切り(実際のGameObject名・'/'とは衝突しない制御文字)。
        private const char SiblingIndexSeparator = '';

        /// <summary>
        /// 元アバター基準のIDテーブルを作り、クローンの生存 INetworkID へ同一IDを書き込む。
        /// 割り当てたエントリ数を返す(0のときはInfoを出さない=PhysBoneのないアバターでのログ氾濫防止)。
        /// </summary>
        /// <param name="originalRoot">元アバターのルートGameObject(変更しない)。</param>
        /// <param name="originalDescriptor">元アバターのVRCAvatarDescriptor(既存IDの継承元。nullでも可=継承なし)。</param>
        /// <param name="clone">書き込み対象のクローンGameObject(_Quest / _Opt)。</param>
        /// <param name="report">変換レポート(nullでも可)。</param>
        public static int AssignNetworkIds(GameObject originalRoot, VRCAvatarDescriptor originalDescriptor, GameObject clone, ConversionReport report)
        {
            if (clone == null) return 0;

            // クローンのコンテナ(VRCAvatarDescriptor は INetworkIDContainer 実装)を取得
            VRCAvatarDescriptor cloneDescriptor = clone.GetComponent<VRCAvatarDescriptor>();
            INetworkIDContainer cloneContainer = cloneDescriptor as INetworkIDContainer;
            if (cloneContainer == null) return 0; // デスクリプター無し(通常は起こらない)は何もしない

            // --- 1. 元アバター基準のIDテーブルを構築(元アバターは一切変更しない) ---
            var idByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var usedIds = new HashSet<int>();
            int cursor = NetworkIDAssignment.MinID; // 新規IDの探索カーソル(SDKの最小値から)

            // 1a. 元デスクリプターの既存 NetworkIDCollection を継承(ユーザーがSDK/VQTのUtilityで割り当て済みのIDを保持)。
            //     stale(gameObject==null)は捨て、アバター外・重複パスも無視する。
            int preservedCount = 0;
            INetworkIDContainer originalContainer = originalDescriptor as INetworkIDContainer;
            if (originalRoot != null && originalContainer != null && originalContainer.NetworkIDCollection != null)
            {
                foreach (NetworkIDPair pair in originalContainer.NetworkIDCollection)
                {
                    if (pair == null || pair.gameObject == null) continue; // stale除去
                    string path = IdentityPath(originalRoot.transform, pair.gameObject.transform);
                    if (path == null) continue;                 // アバター外
                    if (idByPath.ContainsKey(path)) continue;   // 同一オブジェクトの重複エントリは先勝ち
                    // 別パスに同一IDが振られた元コレクションの重複ID(SDK Utilityがconflictとして検出する状態)は
                    // 継承しない=先に使ったパスだけが保持し、後のパスは未登録のまま 1b/2 で一意な新規IDを受け取る
                    // (継承すると1つのクローンコレクション内でID重複=アップロード不正になるため)。
                    if (!usedIds.Add(pair.ID)) continue;
                    idByPath[path] = pair.ID;
                    preservedCount++;
                }
            }

            // 1b. 元アバターの全 INetworkID オブジェクト(EditorOnly配下も含めてIDを予約=両クローンで同一テーブルにするため)を
            //     決定的な順序(識別パスの序数ソート)で走査し、既存に無いものへ新規IDを割り当てる。
            //     元アバター全体を対象にテーブルを作ることで、PC/Quest どちらのクローンでも同じ論理オブジェクトが
            //     同じIDに解決される(生存有無に関わらずテーブルは同一)。
            bool overflow = false;
            if (originalRoot != null)
            {
                foreach (string path in SortedIdentityPaths(originalRoot))
                {
                    if (idByPath.ContainsKey(path)) continue;
                    idByPath[path] = NextFreshId(usedIds, ref cursor, ref overflow);
                }
            }

            // --- 2. クローンの生存 INetworkID オブジェクトへIDを書き込む ---
            // EditorOnly(ビルドで除去される)配下は対象外。マージで新設されたホスト等、テーブルに無いパスは
            // 新規IDを払い出す(ベストエフォート。マージはPC/Questで構成が異なり得るため完全な相互一致は保証できない)。
            var newCollection = new List<NetworkIDPair>();
            int freshOnCloneCount = 0;
            foreach (KeyValuePair<string, GameObject> entry in SortedNetworkObjects(clone))
            {
                string path = entry.Key;
                GameObject go = entry.Value;

                int id;
                if (!idByPath.TryGetValue(path, out id))
                {
                    // テーブルに無い(元アバターに対応が無い=マージホスト等) → 新規IDを払い出す
                    id = NextFreshId(usedIds, ref cursor, ref overflow);
                    idByPath[path] = id; // 同一実行内の一意性を保つ
                    freshOnCloneCount++;
                }

                newCollection.Add(new NetworkIDPair
                {
                    ID = id,
                    gameObject = go,
                    SerializedTypeNames = NetworkIDAssignment.GetSerializedTypes(go), // クローン現状の型集合を記録
                });
            }
            int assignedCount = newCollection.Count;

            // --- 3. クローンへ反映(既存コレクションは丸ごと置き換え=再変換でも冪等) ---
            // 元は空でも、前回変換で複製されたエントリが残る可能性があるため、生存分だけで作り直して stale を一掃する。
            if (cloneDescriptor != null) Undo.RecordObject(cloneDescriptor, UndoLabel);
            cloneContainer.NetworkIDCollection = newCollection;
            if (cloneDescriptor != null)
            {
                EditorUtility.SetDirty(cloneDescriptor);
                PrefabUtility.RecordPrefabInstancePropertyModifications(cloneDescriptor);
            }

            // --- 4. 報告(0件のときは出さない=PhysBoneのないアバターでのログ氾濫防止) ---
            if (report != null && assignedCount > 0)
            {
                // 継承IDのうち削除で消えたものはクローンに現れないため、表示は割り当て件数を上限に丸める
                int shownInherited = Math.Min(preservedCount, assignedCount);
                string inherited = shownInherited > 0 ? $"(うち元アバターの既存ID継承 {shownInherited} 件)" : string.Empty;
                report.Info($"Network IDを {assignedCount} 件割り当てました{inherited}。PC/Quest間で揺れ物を掴んだときの同期ズレを防ぎます。");
                if (freshOnCloneCount > 0)
                {
                    report.Info($"うち {freshOnCloneCount} 件は元アバターに対応が無いオブジェクト(PhysBoneのマージ先など)のため新規IDを付与しました。マージ構成はPC/Questで異なり得るため、これらのIDは完全な相互一致を保証しません(ベストエフォート)。");
                }
                if (overflow)
                {
                    report.Warn($"Network IDがSDKの有効範囲({NetworkIDAssignment.MinID}〜{NetworkIDAssignment.MaxID})を超えました。ネットワークオブジェクトが多すぎます。");
                }
            }
            return assignedCount;
        }

        /// <summary>
        /// 元アバター配下の INetworkID を持つGameObjectの識別パスを、序数ソートした列で返す(決定的順序)。
        /// EditorOnly配下も含める(両クローンで同一テーブルにするための予約)。
        /// </summary>
        private static IEnumerable<string> SortedIdentityPaths(GameObject root)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            Transform rootTransform = root.transform;
            foreach (INetworkID networkId in root.GetComponentsInChildren<INetworkID>(true))
            {
                var component = networkId as Component;
                if (component == null) continue;
                string path = IdentityPath(rootTransform, component.transform);
                if (path != null) paths.Add(path);
            }
            return paths;
        }

        /// <summary>
        /// クローン配下の INetworkID を持つGameObject(EditorOnly配下は除外)を、識別パスの序数ソート順で返す。
        /// 同一GameObjectに複数の INetworkID があっても1エントリにまとめる(NetworkIDPairはオブジェクト単位)。
        /// </summary>
        private static IEnumerable<KeyValuePair<string, GameObject>> SortedNetworkObjects(GameObject root)
        {
            var map = new SortedDictionary<string, GameObject>(StringComparer.Ordinal);
            Transform rootTransform = root.transform;
            foreach (INetworkID networkId in root.GetComponentsInChildren<INetworkID>(true))
            {
                var component = networkId as Component;
                if (component == null) continue;
                if (QuestCompat.IsEditorOnly(component.transform)) continue; // ビルドで除去されるため対象外
                string path = IdentityPath(rootTransform, component.transform);
                if (path == null) continue;
                if (!map.ContainsKey(path)) map[path] = component.gameObject; // オブジェクト単位で先勝ち
            }
            return map;
        }

        /// <summary>
        /// 次の未使用IDを払い出す(SDKの有効範囲 MinID〜MaxID を尊重し、既使用は飛ばす)。
        /// MaxID を超えた場合は overflow を立てつつ、そのまま採番を続ける(実運用ではまず到達しない)。
        /// </summary>
        private static int NextFreshId(HashSet<int> used, ref int cursor, ref bool overflow)
        {
            if (cursor < NetworkIDAssignment.MinID) cursor = NetworkIDAssignment.MinID;
            while (used.Contains(cursor)) cursor++;
            if (cursor > NetworkIDAssignment.MaxID) overflow = true;
            int id = cursor;
            used.Add(id);
            cursor++;
            return id;
        }

        /// <summary>
        /// root から target への識別パス(スラッシュ区切り)。同名の兄弟がいるセグメントは
        /// 末尾に区切り+兄弟インデックス(GetSiblingIndex)を付けて一意化する(元/クローンで同一構造→同一キー)。
        /// target==root は空文字、root配下でない・null は null を返す。
        /// QuestCompat.GetRelativePath(名前のみ)と異なり、同名兄弟のあるアバターでも衝突しない。
        /// </summary>
        private static string IdentityPath(Transform root, Transform target)
        {
            if (root == null || target == null) return null;
            if (target == root) return string.Empty;

            var segments = new List<string>();
            Transform current = target;
            while (current != null && current != root)
            {
                string segment = current.name;
                Transform parent = current.parent;
                if (parent != null && HasSameNamedSibling(parent, current))
                {
                    segment = current.name + SiblingIndexSeparator + current.GetSiblingIndex();
                }
                segments.Add(segment);
                current = parent;
            }
            if (current != root) return null; // root配下ではない

            segments.Reverse();
            return string.Join("/", segments);
        }

        /// <summary>parent の直下に child と同名の兄弟が他に存在するか。</summary>
        private static bool HasSameNamedSibling(Transform parent, Transform child)
        {
            int count = 0;
            foreach (Transform sibling in parent)
            {
                if (sibling.name == child.name)
                {
                    count++;
                    if (count > 1) return true;
                }
            }
            return false;
        }
    }
}
#endif
