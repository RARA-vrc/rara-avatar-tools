// RARA AvatarStudio - 設定の永続化(アバター単位のJSON保存)
// AvatarStudioSettings を EditorPrefs にアバター単位で JSON 保存・復元する。
// キーは "RARA.AvatarStudio.Settings.<avatarKey>"。avatarKey は可能ならプレファブアセットGUID、
// 取れなければアバターのGameObject名から生成する(同一アバターなら同じキーに落ち着く)。
//
// 【繰り返し生成の安全性】
//  ・保存/復元は元アバターや生成物を一切改変しない(EditorPrefs 文字列の読み書きのみ)。
//  ・初回(そのアバターに保存が無い)だけ AvatarStudioMigration で旧2ツール設定からシードする。
//    2回目以降は保存済みJSONを復元するため、シードは一度きり(旧設定を上書きし続けない)。
#if UNITY_EDITOR
using System;
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
        /// </summary>
        public static AvatarStudioSettings LoadSaved(GameObject avatar)
        {
            string json = EditorPrefs.GetString(FullKey(avatar), "");
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
        }

        // ------------------------------------------------------------

        private static string FullKey(GameObject avatar)
        {
            return KeyPrefix + GetAvatarKey(avatar);
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
