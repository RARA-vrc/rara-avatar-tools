// RARA Quest Converter - Android/iOSビルド時の非対応コンポーネント自動削除フック
// FaceEmo等のNDMFツールはビルド時にコンポーネントをアバターへ注入するため、
// エディット時の変換では取り切れない。SDKビルダーの「Auto Fix」と同等の削除を
// ビルド直前に自動実行し、赤アラート(手動Auto Fix要求)を回避する。
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace RARA.QuestConverter
{
    /// <summary>
    /// VRChat SDKのアバタービルド前コールバック。Android/iOSビルド時のみ、
    /// モバイル非対応コンポーネント(AudioSource/Camera/Light/Cloth等)を自動削除する。
    /// 通常のBuild/Test/Uploadでは、コールバックへ渡される avatarGameObject はビルド用の複製
    /// ("(Clone)"サフィックス付き)であり、保存済みシーンは変更されない。
    /// (NDMFの「Apply on Play」では再生モード中のシーン上オブジェクトが渡されるが、
    /// 変更は再生モード終了時に破棄されるため永続的な影響はない)
    /// </summary>
    public class QuestBuildPreprocessor : IVRCSDKPreprocessAvatarCallback
    {
        /// <summary>自動削除の有効/無効を保存するEditorPrefsキー。</summary>
        public const string EditorPrefsKey = "RARA.QuestConverter.AutoStripOnAndroidBuild";

        /// <summary>Android/iOSビルド時の自動削除が有効か(既定: 有効)。ウィンドウのトグルから変更される。</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EditorPrefsKey, true);
            set => EditorPrefs.SetBool(EditorPrefsKey, value);
        }

        /// <summary>
        /// 実行順。次の既存フックより後に実行するため 1024 とする:
        /// ・NDMF/Modular Avatar/FaceEmo/AAO の変換パイプライン(-11000 で開始、-1025 で最適化)
        /// ・SDKの EditorOnly 除去(RemoveAvatarEditorOnly、-1024 付近)
        /// ・lilToon のマテリアル処理(100)
        /// これによりビルド時に注入されたコンポーネントも漏れなく検出できる。
        /// (NDMFの ForceReinitPhysBones は int.MaxValue のため、それよりは前に実行される)
        /// </summary>
        public int callbackOrder => 1024;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                StripUnsupportedForActiveTarget(avatarGameObject);
                return true;
            }
            catch (Exception ex)
            {
                // このフックが原因でビルドをブロックしない。削除できなかった非対応コンポーネントが
                // 残っていれば、従来どおりSDKビルダー側の検証(Auto Fix)で対処できる。
                Debug.LogError("[RARA QuestConverter] モバイルビルド時の自動削除中に例外が発生しました: " + ex);
                return true;
            }
        }

        /// <summary>
        /// 非対応コンポーネント自動削除の本体。OnPreprocessAvatar と「ビルド不要の即時実測」で共用する。
        /// 無効(Enabled=false)、またはモバイル(Android/iOS)ビルドでない場合は何もしない(OnPreprocessAvatar と同一判定)。
        /// 個別の削除失敗は握りつぶすが、致命的な例外は呼び出し側へ委ねる(OnPreprocessAvatar 側で try/catch する)。
        /// </summary>
        public static void StripUnsupportedForActiveTarget(GameObject avatarGameObject)
        {
            if (!Enabled || avatarGameObject == null) return;
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android && target != BuildTarget.iOS) return;

            // QuestCompat.FindUnsupportedComponents は依存関係
            // (Joint→Rigidbody、AudioListener/FlareLayer→Camera)を満たす削除順で返す
            var components = QuestCompat.FindUnsupportedComponents(avatarGameObject);
            if (components == null || components.Count == 0) return; // 非対応なし: 黙って通す

            int removed = 0;
            foreach (var component in components)
            {
                // 先行する削除の連鎖(依存コンポーネントの破棄等)で既に消えている場合はスキップ
                if (component == null) continue;

                // 削除後は参照できないため、ログ用情報を先に取得しておく
                string typeName = component.GetType().Name;
                string path = GetHierarchyPath(avatarGameObject.transform, component.transform);
                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                    if (component == null) // Unityのnull比較: 破棄済みならtrue
                    {
                        removed++;
                        Debug.Log("[RARA QuestConverter] モバイルビルド: 非対応コンポーネントを自動削除 " +
                                  typeName + " (" + path + ")");
                    }
                    else
                    {
                        // RequireComponentで他コンポーネントから要求されている等、削除できなかった場合。
                        // ビルドは止めずSDK側の検証に委ねる。
                        Debug.LogWarning("[RARA QuestConverter] モバイルビルド: " + typeName + " (" + path +
                                         ") を削除できませんでした(他のコンポーネントから必要とされている可能性があります)。");
                    }
                }
                catch (Exception ex)
                {
                    // 個別の削除失敗ではビルドを止めず、残りの削除を続行する
                    Debug.LogWarning("[RARA QuestConverter] モバイルビルド: " + typeName + " (" + path +
                                     ") の削除中に例外が発生しました: " + ex.Message);
                }
            }

            Debug.Log("[RARA QuestConverter] モバイルビルド: 非対応コンポーネントを " + removed +
                      " 件自動削除しました(SDKビルダーの「Auto Fix」と同等の処理。通常のBuild/Uploadではビルド用コピーに適用され、保存済みシーンは変更されません)。");
        }

        /// <summary>ログ表示用の階層パス(アバタールート名/相対パス)を返す。root配下でない場合は名前のみ。</summary>
        private static string GetHierarchyPath(Transform root, Transform target)
        {
            if (target == null) return "(不明)";
            string relative = QuestCompat.GetRelativePath(root, target);
            if (relative == null) return target.name; // root配下でない(通常は起こらない)
            return relative.Length == 0 ? root.name : root.name + "/" + relative;
        }
    }
}
#endif
