// RARA アバター軽量化・Quest/iOS対応ツール - ビルド不要の即時実測(手動ベイク)
// 変換で生成した _Opt / _Quest 複製を「ビルド時の最終的な姿」で計測する一式。
//
// 仕組み(NDMF/SDK ソース調査に基づく・厳守):
//  ・NDMF の AvatarProcessor.ManualProcessAvatar(GameObject, INDMFPlatformProvider=null) をリフレクションで呼ぶ。
//    このAPIは内部で複製を Instantiate してから全パス(Transforming/Optimizing)を EDIT モードで実行し、
//    処理済みの複製を返す(元の複製は無改変)。Play も SDK ビルドも不要。
//    生成アセットは Assets/ZZZ_GeneratedAssets/{名前}/ へ永続保存されるが、手動経路は後始末しないため当方で消す。
//  ・手動ベイクは NDMF パスのみを走らせる。IVRCSDKPreprocessAvatarCallback(lilToon=100 等)は走らないため、
//    ビルド時パリティとして (1) EditorOnly サブツリー除去(SDKのRemoveAvatarEditorOnly相当)と
//    (2) 当パッケージ QuestBuildPreprocessor(callbackOrder 1024)相当の非対応コンポーネント除去を自前で施す。
//    lilToon 等の SDK ビルド固有処理は再現しない(レポートで正直に注記する)。
//  ・計測はビルドフックと同じ AvatarStudioMeasureUtil.MeasureActual(PC/Quest 両基準)を再利用する。
//  ・成功・失敗いずれでも、処理済み複製の DestroyImmediate・新規生成アセットの削除・進捗バーのクリア・
//    シーンの dirty 復元まで必ず後始末する。計測のみの実行でシーン/Assetsへは何も残さない。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using RARA.QuestConverter; // QuestCompat / QuestBuildPreprocessor

namespace RARA.AvatarStudio
{
    /// <summary>
    /// ビルド不要の即時実測。NDMF の ManualProcessAvatar で複製をベイクし、ビルド時パリティ処理を施したうえで
    /// ビルドフックと同じ計測メソッドで測る。NDMF は全てリフレクション(ハード参照しない=NDMF は任意依存)。
    /// </summary>
    internal static class InstantMeasure
    {
        // NDMF の ManualProcessAvatar が OverrideTemporaryDirectoryScope で固定する永続生成先。
        private const string GeneratedRoot = "Assets/ZZZ_GeneratedAssets";

        // AssemblyQualifiedName の簡易形(型名, アセンブリ名)。NDMF Editor アセンブリ名は "nadena.dev.ndmf"。
        private const string NdmfProcessorTypeName = "nadena.dev.ndmf.AvatarProcessor, nadena.dev.ndmf";

        private static MethodInfo _manualProcessCache;
        private static bool _manualProcessResolved;
        private static MethodInfo _clearDirtyCache;
        private static bool _clearDirtyResolved;

        /// <summary>NDMF(ManualProcessAvatar)が利用可能か。未導入なら false。</summary>
        public static bool IsNdmfAvailable => ResolveManualProcess() != null;

        // ------------------------------------------------------------
        // 入口
        // ------------------------------------------------------------

        /// <summary>
        /// 変換直後の複製に対する即時実測(自動実行)。QuestConverter / PCOptimizer の末尾から呼ぶ。
        /// 計測機能オフ・NDMF未導入・例外いずれでも変換結果へは一切影響しない(戻り値なし・例外を外へ出さない)。
        /// </summary>
        public static void MeasureAfterConvert(GameObject clone)
        {
            try
            {
                if (clone == null) return;
                if (!AvatarStudioMeasurePrefs.Enabled) return; // 計測機能自体がオフなら何もしない
                if (!IsNdmfAvailable)
                {
                    Debug.Log("[RARA AvatarStudio] NDMF未導入のため即時実測をスキップしました(ビルド/Play時に実測されます)。");
                    return;
                }

                MeasuredAvatar m = MeasureCore(clone);
                if (m != null)
                {
                    AvatarStudioMeasureStore.Record(m);
                    AvatarStudioMeasureReportWindow.ShowForInstantMeasure(false);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA AvatarStudio] 即時実測(ビルド不要)で例外が発生しました(変換結果には影響しません): " + ex.Message);
            }
        }

        /// <summary>
        /// 手動実測(レポートの「今すぐ実測(ビルド不要)」ボタン)。対象複製を即時実測して記録する。
        /// 成功なら true。失敗理由は message へ返す(NDMF未導入・例外など)。
        /// </summary>
        public static bool MeasureManual(GameObject clone, out string message)
        {
            message = string.Empty;
            if (clone == null) { message = "対象が指定されていません。"; return false; }
            if (!IsNdmfAvailable)
            {
                message = "NDMFが導入されていないため即時実測できません(ビルド/Play時に実測されます)。";
                return false;
            }
            try
            {
                MeasuredAvatar m = MeasureCore(clone);
                if (m == null) { message = "実測に失敗しました(詳細はConsoleを参照してください)。"; return false; }
                AvatarStudioMeasureStore.Record(m);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA AvatarStudio] 即時実測(手動)で例外が発生しました: " + ex.Message);
                message = "実測中に例外が発生しました: " + ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------
        // 本体
        // ------------------------------------------------------------

        /// <summary>
        /// 即時実測の本体。NDMF で複製を(内部クローンで)ベイクし、ビルド時パリティ処理を施して計測する。
        /// 生成アセット・処理済み複製・進捗バー・シーンの dirty 状態はすべて finally で後始末する。失敗時は null。
        /// </summary>
        private static MeasuredAvatar MeasureCore(GameObject clone)
        {
            // 計測名・種別は元の複製名から決める(ManualProcessAvatar 後は "(Clone)" が付くため元名を保持)。
            string name = AvatarStudioMeasureUtil.StripCloneSuffix(clone.name);
            string cloneType = AvatarStudioMeasureUtil.DetectCloneType(name);

            // 生成先の事前スナップショット(新規に増えたサブフォルダだけを後で消し、ユーザーの既存ベイクは触らない)。
            bool rootPreexisted = AssetDatabase.IsValidFolder(GeneratedRoot);
            HashSet<string> preSubfolders = SnapshotSubfolders(GeneratedRoot);

            // 計測のみの実行でシーンを汚さないため、開いている各シーンの dirty 状態を控える。
            Dictionary<Scene, bool> sceneDirtyBefore = SnapshotSceneDirtiness();

            // NDMF の ManualProcessAvatar は複製を Instantiate してシーンへ配置してから try/finally に入るため
            // (AvatarProcessor.cs: Instantiate→BuildContext生成→位置オフセット の後に try)、パスが途中で例外を投げると
            // 戻り値が返らず(processed=null のまま)その複製がシーンに取り残される(NDMF 側の finally は破棄しない)。
            // 実行前のルートを控え、finally で「この実行で新たに増えたルート」を回収して失敗時でも何も残さない。
            HashSet<int> rootsBefore = SnapshotRootObjectIds();

            GameObject processed = null;
            try
            {
                EditorUtility.DisplayProgressBar("RARA 実測(ビルド不要)", "実測中(ビルド不要)...", 0.3f);

                processed = InvokeManualProcess(clone);
                if (processed == null) return null;

                // ビルド時パリティ(1): EditorOnly サブツリーを除去(非アクティブ含む)。SDKのRemoveAvatarEditorOnly相当。
                QuestCompat.StripEditorOnlySubtrees(processed);

                // ビルド時パリティ(2): 当パッケージ QuestBuildPreprocessor(callbackOrder 1024)相当。
                //   activeBuildTarget が Android/iOS のときのみ非対応コンポーネントを除去する(フック本体と同一判定)。
                QuestBuildPreprocessor.StripUnsupportedForActiveTarget(processed);

                // ビルドフックと同じ計測メソッドで PC/Quest 両基準を実測する。
                MeasuredAvatar m = AvatarStudioMeasureUtil.MeasureActual(processed, name, cloneType);
                if (m != null) m.instantBake = true; // 「実測(手動ベイク・ビルド不要)」表示のため
                return m;
            }
            finally
            {
                if (processed != null) UnityEngine.Object.DestroyImmediate(processed);
                DestroyLeakedRootObjects(rootsBefore); // 失敗時に NDMF が取り残した複製をルート差分で回収する
                CleanupGeneratedAssets(preSubfolders, rootPreexisted);
                RestoreSceneDirtiness(sceneDirtyBefore);
                EditorUtility.ClearProgressBar();
            }
        }

        private static GameObject InvokeManualProcess(GameObject clone)
        {
            MethodInfo mi = ResolveManualProcess();
            if (mi == null) return null;
            object result = mi.Invoke(null, new object[] { clone, null });
            return result as GameObject;
        }

        // ------------------------------------------------------------
        // NDMF リフレクション解決
        // ------------------------------------------------------------

        private static MethodInfo ResolveManualProcess()
        {
            if (_manualProcessResolved) return _manualProcessCache;
            _manualProcessResolved = true;
            try
            {
                Type t = Type.GetType(NdmfProcessorTypeName);
                if (t == null) return null;
                // public static GameObject ManualProcessAvatar(GameObject obj, INDMFPlatformProvider platform = null)
                foreach (MethodInfo mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name != "ManualProcessAvatar") continue;
                    ParameterInfo[] ps = mi.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(GameObject))
                    {
                        _manualProcessCache = mi;
                        break;
                    }
                }
            }
            catch { _manualProcessCache = null; }
            return _manualProcessCache;
        }

        // ------------------------------------------------------------
        // 生成アセットの後始末
        // ------------------------------------------------------------

        private static HashSet<string> SnapshotSubfolders(string root)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(root)) return set;
            string[] subs = AssetDatabase.GetSubFolders(root);
            if (subs != null)
            {
                foreach (string sub in subs) set.Add(sub);
            }
            return set;
        }

        /// <summary>
        /// この実行で新しく増えた Assets/ZZZ_GeneratedAssets 直下サブフォルダだけを削除する。
        /// 事前に存在しなかったルートは、空になったなら削除する(ユーザーの既存ベイクは絶対に消さない)。
        /// </summary>
        private static void CleanupGeneratedAssets(HashSet<string> preSubfolders, bool rootPreexisted)
        {
            try
            {
                if (!AssetDatabase.IsValidFolder(GeneratedRoot)) return;

                string[] subs = AssetDatabase.GetSubFolders(GeneratedRoot);
                if (subs != null)
                {
                    foreach (string sub in subs)
                    {
                        if (!preSubfolders.Contains(sub)) AssetDatabase.DeleteAsset(sub);
                    }
                }

                if (!rootPreexisted && AssetDatabase.IsValidFolder(GeneratedRoot))
                {
                    string[] remaining = AssetDatabase.GetSubFolders(GeneratedRoot);
                    if (remaining == null || remaining.Length == 0) AssetDatabase.DeleteAsset(GeneratedRoot);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA AvatarStudio] 即時実測の生成アセット後始末で例外: " + ex.Message);
            }
        }

        // ------------------------------------------------------------
        // 取り残されたルートの回収(NDMF が失敗時に破棄しない複製の後始末)
        // ------------------------------------------------------------

        private static HashSet<int> SnapshotRootObjectIds()
        {
            var set = new HashSet<int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.IsValid() || !s.isLoaded) continue;
                foreach (GameObject go in s.GetRootGameObjects())
                {
                    if (go != null) set.Add(go.GetInstanceID());
                }
            }
            return set;
        }

        /// <summary>
        /// 実行前のルート集合に無い(=この実行で新たに増えた)ルート GameObject を破棄する。
        /// 成功時の processed は finally で先に破棄済みのため、ここで拾うのは主に失敗時に NDMF が
        /// 取り残した複製({名前}(Clone))。GetRootGameObjects はコピー配列を返すので破棄しながらの走査は安全。
        /// 破棄で例外が出ても後続の後始末を止めない(ベストエフォート)。
        /// </summary>
        private static void DestroyLeakedRootObjects(HashSet<int> before)
        {
            if (before == null) return;
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene s = SceneManager.GetSceneAt(i);
                    if (!s.IsValid() || !s.isLoaded) continue;
                    foreach (GameObject go in s.GetRootGameObjects())
                    {
                        if (go != null && !before.Contains(go.GetInstanceID()))
                            UnityEngine.Object.DestroyImmediate(go);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA AvatarStudio] 即時実測の取り残しルート回収で例外: " + ex.Message);
            }
        }

        // ------------------------------------------------------------
        // シーンの dirty 復元(計測のみの実行はシーンを汚さない)
        // ------------------------------------------------------------

        private static Dictionary<Scene, bool> SnapshotSceneDirtiness()
        {
            var map = new Dictionary<Scene, bool>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.IsValid()) map[s] = s.isDirty;
            }
            return map;
        }

        /// <summary>
        /// 実行前に clean だったシーンが実行で dirty になっていれば元へ戻す。元から dirty だったシーンは触らない
        /// (変換自体が汚したシーンはそのまま)。内部 API が無ければベストエフォートで諦める。
        /// </summary>
        private static void RestoreSceneDirtiness(Dictionary<Scene, bool> before)
        {
            if (before == null) return;
            MethodInfo clear = ResolveClearSceneDirtiness();
            if (clear == null) return;
            foreach (KeyValuePair<Scene, bool> kv in before)
            {
                Scene s = kv.Key;
                if (kv.Value) continue;                 // 元から dirty
                if (!s.IsValid() || !s.isLoaded) continue;
                if (!s.isDirty) continue;               // 既に clean
                try { clear.Invoke(null, new object[] { s }); }
                catch { /* ベストエフォート */ }
            }
        }

        private static MethodInfo ResolveClearSceneDirtiness()
        {
            if (_clearDirtyResolved) return _clearDirtyCache;
            _clearDirtyResolved = true;
            try
            {
                _clearDirtyCache = typeof(EditorSceneManager).GetMethod(
                    "ClearSceneDirtiness",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, new Type[] { typeof(Scene) }, null);
            }
            catch { _clearDirtyCache = null; }
            return _clearDirtyCache;
        }
    }
}
#endif
