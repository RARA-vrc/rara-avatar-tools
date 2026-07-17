// RARA Avatar Tools - Booth 導入ガイド ウィンドウ
// このスクリプトは「案内のみ」です。ツール本体は含まれていません。
// 本体は VCC / ALCOM から導入してください。
//
// 依存ゼロ・Unity 2019.4 以降のどのプロジェクトでもコンパイルできます。
// (UnityEditor 以外の外部アセンブリを参照しません)

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace RARA.BoothGuide
{
    /// <summary>
    /// 初回インポート時に一度だけ自動でガイドを表示するためのフック。
    /// SessionState で「同一セッション中の再表示」を防ぎ、
    /// EditorPrefs で「非表示にする」を選んだら以降は自動表示しない。
    /// </summary>
    [InitializeOnLoad]
    internal static class RARABoothGuideAutoOpen
    {
        // プロジェクトごとに独立したキーにする(dataPath ベース)。
        // string.GetHashCode はランタイム実装によって値が変わり得るため、
        // 「今後表示しない」の保存キーが将来もぶれないよう、決定的な独自ハッシュで算出する。
        private static string ProjectKey
        {
            get { return "RARA.BoothGuide.Dismissed." + StableHash(Application.dataPath); }
        }

        // ランタイム非依存で決定的な 32bit ハッシュ(FNV-1a)。
        private static string StableHash(string s)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= 16777619u;
            }
            return hash.ToString("x8");
        }

        private const string SessionShownKey = "RARA.BoothGuide.ShownThisSession";

        static RARABoothGuideAutoOpen()
        {
            // 静的コンストラクタから直接ウィンドウを開くのは避け、delayCall で遅延実行する。
            EditorApplication.delayCall += TryAutoOpen;
        }

        private static void TryAutoOpen()
        {
            // ユーザーが「今後表示しない」を選んでいたら何もしない。
            if (EditorPrefs.GetBool(ProjectKey, false))
                return;

            // 同一 Unity セッション中はコンパイルの度に開かないようにする。
            if (SessionState.GetBool(SessionShownKey, false))
                return;

            SessionState.SetBool(SessionShownKey, true);
            RARABoothGuideWindow.ShowWindow();
        }

        internal static string DismissedPrefKey { get { return ProjectKey; } }
    }

    /// <summary>
    /// VCC / ALCOM への導入手順を案内するエディタウィンドウ。
    /// </summary>
    public class RARABoothGuideWindow : EditorWindow
    {
        // ── 案内文言・URL(仕様で指定された固定値)────────────────────────
        private const string ToolName = "RARA Avatar Tools";

        private const string AddRepoDeepLink =
            "vcc://vpm/addRepo?url=https%3A%2F%2Frara-vrc.github.io%2Frara-avatar-tools%2Findex.json";
        private const string InstallPageUrl =
            "https://rara-vrc.github.io/rara-avatar-tools/";
        private const string ManualRepoUrl =
            "https://rara-vrc.github.io/rara-avatar-tools/index.json";

        // Open β / 不具合報告先
        private const string BugReportX = "https://x.com/RR_vrchat";
        private const string BugReportMailAddress = "raravrchat@gmail.com";
        private const string BugReportMailUrl =
            "mailto:raravrchat@gmail.com?subject=RARA%20Avatar%20Tools%20%E4%B8%8D%E5%85%B7%E5%90%88%E5%A0%B1%E5%91%8A";

        // ガイドフォルダ(削除ボタン用)
        private const string GuideFolderPath = "Assets/RARA_導入ガイド";

        private Vector2 _scroll;

        [MenuItem("RARA/導入ガイド(VCC・ALCOM)")]
        public static void ShowWindow()
        {
            var win = GetWindow<RARABoothGuideWindow>(true, "RARA 導入ガイド", true);
            win.minSize = new Vector2(460f, 560f);
            win.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space();

            // ── タイトル ───────────────────────────────────────────────
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                wordWrap = true
            };
            EditorGUILayout.LabelField(ToolName, titleStyle);
            GUILayout.Space(2f);

            var noteStyle = new GUIStyle(EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(
                "本体は VCC / ALCOM から導入します。\nこの unitypackage は案内のみで、ツール本体は含まれていません。",
                noteStyle);

            DrawSeparator();

            // ── メインアクション:リポジトリ追加(大きいボタン)───────────
            var bigButton = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                fixedHeight = 46f
            };
            if (GUILayout.Button("VCC / ALCOM にリポジトリを追加", bigButton))
            {
                Application.OpenURL(AddRepoDeepLink);
            }
            EditorGUILayout.LabelField(
                "※ ボタンが反応しない場合は VCC / ALCOM が未インストールか、\n" +
                "  下の URL を手動で追加してください。",
                EditorStyles.miniLabel, GUILayout.Height(28));

            GUILayout.Space(4f);

            if (GUILayout.Button("導入ページを開く", GUILayout.Height(28)))
            {
                Application.OpenURL(InstallPageUrl);
            }

            GUILayout.Space(6f);

            // ── 手動追加用 URL(選択・コピー可能)───────────────────────
            EditorGUILayout.LabelField("手動で追加する場合のリポジトリ URL:", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                ManualRepoUrl,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight + 2));

            DrawSeparator();

            // ── 導入手順(3行)─────────────────────────────────────────
            EditorGUILayout.LabelField("導入手順", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("1. 上のボタンで VCC / ALCOM にリポジトリを追加", noteStyle);
            EditorGUILayout.LabelField("2. プロジェクトに「RARA Avatar Tools」を追加", noteStyle);
            EditorGUILayout.LabelField("3. Unity を開くとメニューバーに「RARA」が表示されます", noteStyle);

            DrawSeparator();

            // ── Open β / 不具合報告先 ─────────────────────────────────
            EditorGUILayout.LabelField("Open β(オープンベータ)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "本ツールは Open β 公開中です。不具合・ご要望は下記までお願いします。",
                noteStyle);

            GUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("X(Twitter)DM で報告", GUILayout.Height(24)))
                {
                    Application.OpenURL(BugReportX);
                }
                if (GUILayout.Button("メールで報告", GUILayout.Height(24)))
                {
                    Application.OpenURL(BugReportMailUrl);
                }
            }
            EditorGUILayout.SelectableLabel(
                "X: " + BugReportX + "   /   Mail: " + BugReportMailAddress,
                EditorStyles.miniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            DrawSeparator();

            // ── 表示制御・削除 ─────────────────────────────────────────
            bool dismissed = EditorPrefs.GetBool(RARABoothGuideAutoOpen.DismissedPrefKey, false);
            bool newDismissed = EditorGUILayout.ToggleLeft(
                "次回から起動時に自動表示しない", dismissed);
            if (newDismissed != dismissed)
            {
                EditorPrefs.SetBool(RARABoothGuideAutoOpen.DismissedPrefKey, newDismissed);
            }
            EditorGUILayout.LabelField(
                "※ いつでもメニュー「RARA > 導入ガイド(VCC・ALCOM)」から再表示できます。",
                EditorStyles.miniLabel, GUILayout.Height(16));

            GUILayout.Space(4f);

            var oldColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.9f, 0.5f, 0.5f);
            if (GUILayout.Button("このガイドをプロジェクトから削除", GUILayout.Height(26)))
            {
                DeleteGuide();
            }
            GUI.backgroundColor = oldColor;

            EditorGUILayout.Space();
            EditorGUILayout.EndScrollView();
        }

        private void DeleteGuide()
        {
            bool ok = EditorUtility.DisplayDialog(
                "ガイドを削除",
                "このガイド(" + GuideFolderPath + ")をプロジェクトから削除します。\n" +
                "ツール本体には影響しません。よろしいですか?",
                "削除する", "キャンセル");
            if (!ok)
                return;

            // 削除後にウィンドウを閉じる(削除中の再描画エラーを避ける)。
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.DeleteAsset(GuideFolderPath))
                {
                    AssetDatabase.Refresh();
                    Debug.Log("[RARA] 導入ガイドを削除しました: " + GuideFolderPath);
                }
                else
                {
                    Debug.LogWarning("[RARA] 導入ガイドの削除に失敗しました: " + GuideFolderPath);
                }
            };
            Close();
        }

        private static void DrawSeparator()
        {
            GUILayout.Space(6f);
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.4f));
            GUILayout.Space(6f);
        }
    }
}
#endif
