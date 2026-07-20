// RARA Quest Converter - コンタクト(VRCContactSender / VRCContactReceiver)の選択・削除(1.11.0)
// Avatar Dynamics の5制約のうち「コンタクト数(Poor上限16)」に対応する。
// 揺れものの自動選定と同じ考え方で、残す/削除の選択(チェック=残す)と、
// 頭・手系レシーバーを優先して16以内へ絞る自動選定、変換時の複製からの削除を提供する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;

namespace RARA.QuestConverter
{
    /// <summary>コンタクト一覧の1行(残す/削除の選択に使う)。</summary>
    public class ContactPreviewRow
    {
        /// <summary>コンタクトの識別パス(AvatarDynamicsCost.GetContactIdentityPath 形式)。選択の保存に使う。</summary>
        public string path;
        /// <summary>短い表示名(GameObject名)。</summary>
        public string label;
        /// <summary>Receiver なら true、Sender なら false。</summary>
        public bool isReceiver;
        /// <summary>公式カウント対象(ローカル専用でない)なら true。ローカル専用は無料のため常に残す扱い。</summary>
        public bool counted;
        /// <summary>自動選定の優先度(小さいほど高優先)。頭・手系レシーバー=0 / その他レシーバー=1 / センダー=2。</summary>
        public int priorityScore;
    }

    /// <summary>コンタクト一覧のプレビュー(アバターへの変更なし)。</summary>
    public class ContactPreview
    {
        /// <summary>一覧行(優先度順ではなく出現順)。</summary>
        public List<ContactPreviewRow> rows = new List<ContactPreviewRow>();
        /// <summary>公式カウント対象のコンタクト数(現在。ローカル専用は含まない)。</summary>
        public int currentCountedCount;
        /// <summary>EditorOnly / Quest除外配下のため一覧から隠した本数。</summary>
        public int hiddenExcludedCount;
    }

    /// <summary>コンタクトの選択・削除・自動選定の静的ヘルパー。</summary>
    public static class ContactSelection
    {
        /// <summary>頭・手系レシーバーを優先するためのキーワード(部分一致・大文字小文字無視)。頭なで文化への配慮。</summary>
        private static readonly string[] HeadHandKeywords =
        {
            "head", "頭", "あたま", "pat", "なで", "撫", "headpat", "ヘッドパット",
            "hand", "手", "palm", "hug", "抱", "cheek", "ほほ", "頬", "face", "顔",
        };

        // ================================================================
        // プレビュー(残す/削除の一覧)
        // ================================================================

        /// <summary>アバター配下のコンタクトを一覧化する(EditorOnly / excludedRoots 配下は隠す)。</summary>
        public static ContactPreview PreviewContacts(GameObject root, List<Transform> excludedRoots = null)
        {
            var preview = new ContactPreview();
            if (root == null) return preview;

            foreach (ContactBase contact in root.GetComponentsInChildren<ContactBase>(true))
            {
                if (contact == null) continue;
                if (IsInEditorOnlySubtree(contact.transform, root.transform)) { preview.hiddenExcludedCount++; continue; }
                if (IsUnderAny(contact.transform, excludedRoots)) { preview.hiddenExcludedCount++; continue; }

                bool isReceiver = contact is VRCContactReceiver;
                bool counted = !contact.IsLocalOnly;
                var row = new ContactPreviewRow
                {
                    path = AvatarDynamicsCost.GetContactIdentityPath(root.transform, contact),
                    label = contact.gameObject.name,
                    isReceiver = isReceiver,
                    counted = counted,
                    priorityScore = ComputePriority(isReceiver, contact.gameObject.name),
                };
                preview.rows.Add(row);
                if (counted) preview.currentCountedCount++;
            }
            return preview;
        }

        /// <summary>優先度スコア(頭・手系レシーバー=0 / その他レシーバー=1 / センダー=2)。</summary>
        private static int ComputePriority(bool isReceiver, string name)
        {
            if (isReceiver)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    foreach (string kw in HeadHandKeywords)
                    {
                        if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                    }
                }
                return 1;
            }
            return 2;
        }

        // ================================================================
        // 変換時の削除(複製のみ)
        // ================================================================

        /// <summary>
        /// removePaths(識別パス)に一致するコンタクトを複製アバターから削除し、削除数を返す。
        /// RemoveSelectedPhysBones と同様、破棄で同一GameObject上の序数がずれないよう先に識別パスを確定してから削除する。
        /// </summary>
        public static int RemoveSelectedContacts(GameObject root, List<string> removePaths, ConversionReport report)
        {
            if (root == null || removePaths == null || removePaths.Count == 0) return 0;

            var byIdentity = new Dictionary<string, ContactBase>(StringComparer.Ordinal);
            foreach (ContactBase c in root.GetComponentsInChildren<ContactBase>(true))
            {
                if (c == null) continue;
                string id = AvatarDynamicsCost.GetContactIdentityPath(root.transform, c);
                if (id != null && !byIdentity.ContainsKey(id)) byIdentity[id] = c;
            }

            int removed = 0;
            var processed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in removePaths)
            {
                if (string.IsNullOrEmpty(path) || !processed.Add(path)) continue;
                if (byIdentity.TryGetValue(path, out ContactBase c) && c != null)
                {
                    report.Warn($"コンタクト削除(選択): {path}");
                    UnityEngine.Object.DestroyImmediate(c);
                    removed++;
                }
                else
                {
                    report.Info($"削除指定のコンタクトが見つかりません(スキップ): {path}");
                }
            }
            if (removed > 0) report.Info($"コンタクトを {removed} 件削除しました(選択)。");
            return removed;
        }

        // ================================================================
        // 自動選定(頭・手系レシーバー優先で cap 以内へ)
        // ================================================================

        /// <summary>
        /// 公式カウント対象のコンタクトを優先度順に cap 個まで残し、残りを removePaths として返す
        /// (ローカル専用は無料のため常に残す=削除しない)。binding には制約になったか(超過があったか)を返す。
        /// </summary>
        public static List<string> AutoSelectContacts(ContactPreview preview, int cap, out int keptCounted, out bool wasBinding)
        {
            keptCounted = 0;
            wasBinding = false;
            var remove = new List<string>();
            if (preview == null || preview.rows == null) return remove;

            var sorted = new List<ContactPreviewRow>(preview.rows);
            sorted.Sort((a, b) => a.priorityScore.CompareTo(b.priorityScore));

            foreach (ContactPreviewRow row in sorted)
            {
                if (row == null || string.IsNullOrEmpty(row.path)) continue;
                if (!row.counted) continue; // ローカル専用は無料 → 常に残す
                if (keptCounted < cap)
                {
                    keptCounted++;
                }
                else
                {
                    wasBinding = true;
                    if (!remove.Contains(row.path)) remove.Add(row.path);
                }
            }
            return remove;
        }

        // ================================================================
        // 補助
        // ================================================================

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

        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            if (roots == null || t == null) return false;
            foreach (Transform r in roots)
            {
                if (r != null && (t == r || t.IsChildOf(r))) return true;
            }
            return false;
        }
    }
}
#endif
