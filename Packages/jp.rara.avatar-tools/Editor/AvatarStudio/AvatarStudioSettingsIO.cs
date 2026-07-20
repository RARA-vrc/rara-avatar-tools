// RARA AvatarStudio - 設定の永続化(アバター単位のJSON保存)
// AvatarStudioSettings を EditorPrefs にアバター単位で JSON 保存・復元する。
// キーは "RARA.AvatarStudio.Settings.<avatarKey>.<projectScope>"。avatarKey は可能ならプレファブアセットGUID、
// 取れなければアバターのGameObject名から生成する(同一アバターなら同じキーに落ち着く)。
// 末尾の projectScope は Application.dataPath の安定ハッシュ(<see cref="ProjectScope"/>)。EditorPrefs は
// マシン全体で共有される(別プロジェクトから同じキーが見える)ため、これを付けないと名前フォールバックの
// キー("name:Kei" 等)が別プロジェクトの同名アバターと衝突し、片方の設定がもう片方へ流れ込む。
//
// 【繰り返し生成の安全性】
//  ・保存/復元は元アバターや生成物を一切改変しない(EditorPrefs 文字列の読み書きのみ)。
//  ・初回(そのアバターに保存が無い)だけ AvatarStudioMigration で旧2ツール設定からシードする。
//    2回目以降は保存済みJSONを復元するため、シードは一度きり(旧設定を上書きし続けない)。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RARA.AvatarStudio
{
    /// <summary>AvatarStudioSettings をアバター単位で EditorPrefs に保存・復元する。</summary>
    public static class AvatarStudioSettingsIO
    {
        /// <summary>保存キーの接頭辞(全キーが "RARA.AvatarStudio.*" に収まる)。</summary>
        private const string KeyPrefix = "RARA.AvatarStudio.Settings.";

        /// <summary>
        /// アバターを一意に識別するキー断片を返す。
        /// 優先: プレファブアセットのGUID(シーンをまたいでも安定)。
        /// フォールバック: GameObject名(プレファブ非接続のシーン専用アバター向け)。
        /// avatar が null のときは "none"。
        /// </summary>
        public static string GetAvatarKey(GameObject avatar)
        {
            if (avatar == null) return "none";

            string assetPath = null;
            try
            {
                if (PrefabUtility.IsPartOfPrefabAsset(avatar))
                {
                    assetPath = AssetDatabase.GetAssetPath(avatar);
                }
                else if (PrefabUtility.IsPartOfPrefabInstance(avatar))
                {
                    assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar);
                }
            }
            catch (Exception)
            {
                assetPath = null; // プレファブ判定に失敗しても名前フォールバックへ
            }

            if (!string.IsNullOrEmpty(assetPath))
            {
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid)) return "guid:" + guid;
            }

            return "name:" + Sanitize(avatar.name);
        }

        /// <summary>そのアバターに保存済みのStudio設定があるか。</summary>
        public static bool HasSavedSettings(GameObject avatar)
        {
            return EditorPrefs.HasKey(FullKey(avatar)) &&
                   !string.IsNullOrEmpty(EditorPrefs.GetString(FullKey(avatar), ""));
        }

        /// <summary>
        /// そのアバターの保存済み設定を復元して返す。保存が無い/壊れている場合は null。
        /// (シードや既定へのフォールバックが不要で、純粋に保存内容だけ欲しい呼び出し向け。)
        /// プロジェクト識別子の導入前に保存された旧・非スコープキーがある場合は、現在のアバターに
        /// 帰属すると推定できるときだけ一度きりスコープキーへ移行する(下記参照)。
        /// </summary>
        public static AvatarStudioSettings LoadSaved(GameObject avatar)
        {
            // 1) プロジェクト別スコープキー(現行の保存先)。
            string scopedKey = FullKey(avatar);
            string json = EditorPrefs.GetString(scopedKey, "");
            if (!string.IsNullOrEmpty(json)) return Parse(json);

            // 2) 旧・非スコープキー(プロジェクト識別子の導入前に保存されたもの)からの一度きり移行。
            //    EditorPrefs はマシン全体で共有される(別プロジェクトから同じキーが見える)ため、非スコープキーには
            //    別プロジェクト/別アバターの設定が入っている可能性がある。現在のアバターに帰属すると推定できる
            //    (パスが解決する、またはパスを一切持たない)場合のみスコープキーへ移し、それ以外は破棄して新規から
            //    始める。非スコープキーへは二度と書き込まない(SaveSettings は常にスコープキーのみ)。
            string legacyJson = EditorPrefs.GetString(LegacyFullKey(avatar), "");
            if (!string.IsNullOrEmpty(legacyJson))
            {
                var legacy = Parse(legacyJson);
                if (legacy != null && PlausiblyBelongsTo(legacy, avatar))
                {
                    EditorPrefs.SetString(scopedKey, legacyJson); // 一度だけスコープキーへ移行
                    return legacy;
                }
            }
            return null;
        }

        /// <summary>JSON文字列を AvatarStudioSettings へ復元する。空/破損なら null。</summary>
        private static AvatarStudioSettings Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var settings = new AvatarStudioSettings();
                JsonUtility.FromJsonOverwrite(json, settings);
                return settings;
            }
            catch (Exception)
            {
                return null; // 壊れた設定は無視(呼び出し側で既定/シードへ)
            }
        }

        /// <summary>
        /// そのアバターの設定を返す(never null)。C(ウィンドウ)がアバター選択時に呼ぶ主入口。
        ///  1) 保存済みがあれば復元。
        ///  2) 無ければ、旧2ツールに保存があれば移行シード(<see cref="AvatarStudioMigration.SeedFromOldTools"/>)。
        ///  3) それも無ければ既定値。
        /// この呼び出し自体は保存しない(実際の保存はユーザー操作時に <see cref="SaveSettings"/> で行う)。
        /// </summary>
        public static AvatarStudioSettings LoadSettings(GameObject avatar)
        {
            var saved = LoadSaved(avatar);
            if (saved != null) return saved;

            if (AvatarStudioMigration.HasAnyOldSettings())
            {
                return AvatarStudioMigration.SeedFromOldTools();
            }
            return new AvatarStudioSettings();
        }

        /// <summary>そのアバターの設定を保存する。avatar/settings が null なら何もしない。</summary>
        public static void SaveSettings(GameObject avatar, AvatarStudioSettings settings)
        {
            if (avatar == null || settings == null) return;
            try
            {
                EditorPrefs.SetString(FullKey(avatar), JsonUtility.ToJson(settings));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA AvatarStudio] 設定の保存に失敗しました: " + ex.Message);
            }
        }

        /// <summary>そのアバターの保存済み設定を削除する(次回は移行シード/既定から開始)。</summary>
        public static void ClearSavedSettings(GameObject avatar)
        {
            string key = FullKey(avatar);
            if (EditorPrefs.HasKey(key)) EditorPrefs.DeleteKey(key);
            // 旧・非スコープキーが残っていると LoadSaved が再び移行してしまうため、併せて削除する。
            string legacyKey = LegacyFullKey(avatar);
            if (EditorPrefs.HasKey(legacyKey)) EditorPrefs.DeleteKey(legacyKey);
        }

        // ------------------------------------------------------------
        // プロジェクト識別子(EditorPrefs はマシン全体で共有されるため全キーへ付与する)
        // ------------------------------------------------------------

        // EditorPrefs(Windowsではレジストリ HKCU 配下)はマシン全体で共有され、別のUnityプロジェクトからも
        // 同じキーが見える。プロジェクト識別子を挟まないと、GUIDが取れずアバター名にフォールバックしたキー
        // ("name:Kei" 等)が別プロジェクトの同名アバターと衝突し、片方の設定がもう片方へ流れ込む
        // (実測: 別プロジェクトの別アバターMAYOの設定がKeiへ混入)。Application.dataPath はプロジェクトごとに
        // 一意なので、その安定ハッシュをプロジェクト識別子として全キーへ付与する(初回のみ計算してキャッシュ)。
        private static string _projectScope;
        private static string ProjectScope
        {
            get
            {
                if (_projectScope == null) _projectScope = "p" + StableHash(Application.dataPath);
                return _projectScope;
            }
        }

        /// <summary>
        /// ベースキーにこのプロジェクト固有の識別子を付与して返す(プロジェクト間の設定混入を防ぐ)。
        /// 旧2ツール(PCOptimizer/QuestConverter)のキーも、キー設計の判断を一箇所へ集約するため
        /// このメソッドを通してスコープ化する。
        /// </summary>
        public static string ProjectScopedKey(string baseKey)
        {
            return baseKey + "." + ProjectScope;
        }

        /// <summary>実行間で安定した非暗号ハッシュ(FNV-1a)。string.GetHashCode は実行間で不安定なため使わない。</summary>
        private static string StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                {
                    foreach (char c in s) { h ^= c; h *= 16777619u; }
                }
                return h.ToString("x8");
            }
        }

        /// <summary>
        /// 設定オブジェクトのアバター固有リスト(トグル選択・パス・GUID・削減計画など、すべて List フィールド)を
        /// 空にする。旧・非スコープキーからの移行時に、別プロジェクト/別アバターのパスを持ち込まないために使う
        /// (アバター非依存のスカラー設定のみを引き継ぐ)。PCOptimizeSettings / QuestConvertSettings 双方に使える。
        /// </summary>
        public static void StripAvatarSpecificPaths(object settings)
        {
            if (settings == null) return;
            foreach (FieldInfo f in settings.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Type t = f.FieldType;
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    f.SetValue(settings, Activator.CreateInstance(t)); // 空リストへ差し替え
                }
            }
        }

        // ------------------------------------------------------------

        private static string FullKey(GameObject avatar)
        {
            return ProjectScopedKey(KeyPrefix + GetAvatarKey(avatar));
        }

        /// <summary>プロジェクト識別子の導入前に使っていた非スコープキー(移行の読み取り元専用。書き込まない)。</summary>
        private static string LegacyFullKey(GameObject avatar)
        {
            return KeyPrefix + GetAvatarKey(avatar);
        }

        /// <summary>
        /// 保存設定が現在のアバターに帰属すると推定できるか。アバター固有の相対パスが1つでも解決すれば true。
        /// パスを一切持たない(スカラーのみ)なら無害とみなして true。パスはあるが1つも解決しないときだけ
        /// 「別アバター/別プロジェクトの設定」とみなして false(移行せず新規から始める)。
        /// </summary>
        private static bool PlausiblyBelongsTo(AvatarStudioSettings s, GameObject avatar)
        {
            if (s == null) return false;
            if (avatar == null) return true; // 検証できないなら移行を妨げない
            Transform root = avatar.transform;
            bool sawPath = false;
            foreach (string raw in EnumerateAvatarRelativePaths(s))
            {
                string path = raw;
                int hash = path.IndexOf('#'); // PhysBone識別パスの "#index" は除いて解決する
                if (hash >= 0) path = path.Substring(0, hash);
                if (string.IsNullOrEmpty(path)) continue;
                sawPath = true;
                if (RARA.QuestConverter.QuestCompat.FindByPath(root, path) != null) return true;
            }
            return !sawPath;
        }

        /// <summary>設定に含まれるアバタールート相対パス(トグル/レンダラー/PhysBone等)を列挙する。GUIDは対象外。</summary>
        private static IEnumerable<string> EnumerateAvatarRelativePaths(AvatarStudioSettings s)
        {
            if (s.toggleChoices != null)
                foreach (var c in s.toggleChoices)
                    if (c != null && !string.IsNullOrEmpty(c.groupId)) yield return c.groupId;
            foreach (var p in NonEmpty(s.skinnedMeshMergeOptOutPaths)) yield return p;
            foreach (var p in NonEmpty(s.skinnedMeshMergeOverdrawTrimPaths)) yield return p;
            foreach (var p in NonEmpty(s.skinnedMeshMergeMaterialAnimDisablePaths)) yield return p;
            if (s.smrMergeGroups != null)
                foreach (var g in s.smrMergeGroups)
                    if (g != null && !string.IsNullOrEmpty(g.rendererPath)) yield return g.rendererPath;
            foreach (var p in NonEmpty(s.physBoneKeepPaths)) yield return p;
            foreach (var p in NonEmpty(s.physBoneRemovePaths)) yield return p;
            foreach (var p in NonEmpty(s.questExcludePaths)) yield return p;
            foreach (var p in NonEmpty(s.questHiddenMeshRendererPaths)) yield return p;
            foreach (var p in NonEmpty(s.questPhysBoneNoMergePaths)) yield return p;
            foreach (var p in NonEmpty(s.contactRemovePaths)) yield return p; // [1.11.0] コンタクト削除指定もアバター相対パス

            if (s.questDecimationPlan != null)
                foreach (var d in s.questDecimationPlan)
                    if (d != null && !string.IsNullOrEmpty(d.rendererPath)) yield return d.rendererPath;
        }

        private static IEnumerable<string> NonEmpty(List<string> list)
        {
            if (list == null) yield break;
            foreach (var p in list)
                if (!string.IsNullOrEmpty(p)) yield return p;
        }

        /// <summary>名前フォールバックキー用に、扱いづらい文字を除いて長さを抑える。</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(char.IsControl(c) ? '_' : c);
            }
            string result = sb.ToString().Trim();
            if (result.Length == 0) return "unnamed";
            // EditorPrefs(Windowsではレジストリ)のキー長制限に配慮して過度に長い名前は切り詰める。
            const int maxLen = 180;
            return result.Length <= maxLen ? result : result.Substring(0, maxLen);
        }
    }
}
#endif
