// RARA アバター軽量化・Quest/iOS対応ツール - スタジオ専用UIヘルパー(実装者C所有)
// 旧2ツール(PCOptimizerWindow / QuestConverterWindow)のスタイル・foldout・レポート配色の
// 「パターン」だけを写経したスタジオ内蔵ヘルパー。旧ウィンドウのinternalメンバーは一切参照しない
// (旧ウィンドウはバイト単位で不変のまま併存させるため)。
//
// 提供物:
//   ・見出し/本文/警告文の遅延生成GUIStyle(ドメインリロードでstaticがnullに戻るため都度再生成)
//   ・EditorPrefs "RARA.AvatarStudio.Fold.*" に開閉を保存するセクションfoldout
//   ・ターゲットチップ(PC/Quest)トグル
//   ・ConversionReport の重大度に応じたアイコン文字と配色
// すべて IMGUI(OnGUI)実行中の呼び出しを前提とする。
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using RARA.QuestConverter; // ConversionReport(重大度アイコン/配色に使用)

namespace RARA.AvatarStudio
{
    /// <summary>スタジオ・ウィンドウ共通のIMGUIスタイルと小さな描画ヘルパー。</summary>
    internal static class AvatarStudioUI
    {
        // ---- 配色(旧ツールと同系統の値を写経) ----
        internal static readonly Color OverLimitColor = new Color(1f, 0.42f, 0.42f);   // 目標超過(赤)
        internal static readonly Color UploadOkColor = new Color(0.35f, 0.85f, 0.45f); // 目標内(緑)
        internal static readonly Color NoteYellowColor = new Color(1f, 0.85f, 0.35f);  // 注意(黄)
        internal static readonly Color AccentColor = new Color(0.45f, 0.7f, 1f);       // 見出しアクセント(青)

        // 開閉状態をEditorPrefsから毎フレーム読むのを避けるためのキャッシュ。
        private static readonly System.Collections.Generic.Dictionary<string, bool> FoldCache =
            new System.Collections.Generic.Dictionary<string, bool>();

        private const string FoldPrefix = "RARA.AvatarStudio.Fold.";

        // ---- 遅延生成スタイル ----
        private static GUIStyle _title;
        private static GUIStyle _purpose;
        private static GUIStyle _wrap;
        private static GUIStyle _miniWrap;
        private static GUIStyle _stepHeader;
        private static GUIStyle _foldHeader;
        private static GUIStyle _chipOn;
        private static GUIStyle _chipOff;
        private static bool _stylesReady;

        /// <summary>OnGUI開始時に一度呼ぶ。ドメインリロードでstaticがnullに戻るため、null時のみ再構築する。</summary>
        internal static void EnsureStyles()
        {
            if (_stylesReady && _title != null) return;

            _title = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                wordWrap = true,
            };
            _purpose = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                fontSize = 11,
            };
            _wrap = new GUIStyle(EditorStyles.label) { wordWrap = true };
            _miniWrap = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            _stepHeader = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                wordWrap = true,
            };
            _foldHeader = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold,
            };
            _chipOn = new GUIStyle(EditorStyles.miniButton)
            {
                fontStyle = FontStyle.Bold,
                fixedHeight = 22f,
            };
            _chipOff = new GUIStyle(EditorStyles.miniButton)
            {
                fixedHeight = 22f,
            };
            _stylesReady = true;
        }

        internal static GUIStyle TitleLabel { get { EnsureStyles(); return _title; } }
        internal static GUIStyle PurposeLabel { get { EnsureStyles(); return _purpose; } }
        internal static GUIStyle WrapLabel { get { EnsureStyles(); return _wrap; } }
        internal static GUIStyle MiniWrapLabel { get { EnsureStyles(); return _miniWrap; } }
        internal static GUIStyle StepHeaderLabel { get { EnsureStyles(); return _stepHeader; } }

        // ---- セクションfoldout(EditorPrefs 永続 + キャッシュ) ----

        /// <summary>
        /// 太字の折りたたみ見出しを描画し、開いていれば true を返す。開閉状態は
        /// EditorPrefs "RARA.AvatarStudio.Fold.{key}" に保存する。
        /// </summary>
        internal static bool Fold(string key, string label, bool defaultOpen)
        {
            EnsureStyles();
            string prefKey = FoldPrefix + key;
            bool current;
            if (!FoldCache.TryGetValue(prefKey, out current))
            {
                current = EditorPrefs.GetBool(prefKey, defaultOpen);
                FoldCache[prefKey] = current;
            }

            bool next = EditorGUILayout.Foldout(current, label, true, _foldHeader);
            if (next != current)
            {
                FoldCache[prefKey] = next;
                EditorPrefs.SetBool(prefKey, next);
            }
            return next;
        }

        /// <summary>「▸ もっと詳しく」的な補足説明の折りたたみ。既定は閉じる。</summary>
        internal static bool ExplainFold(string key, string label)
        {
            return Fold("Explain." + key, label, false);
        }

        /// <summary>
        /// 指定キーの折りたたみ状態を強制的に設定する(目標達成ガイドの「→ ステップnで対応」から
        /// 対象ステップを開くために使う)。次の再描画で Fold が同じ値を読み、当該ステップが開く。
        /// </summary>
        internal static void SetFoldOpen(string key, bool open)
        {
            string prefKey = FoldPrefix + key;
            FoldCache[prefKey] = open;
            EditorPrefs.SetBool(prefKey, open);
        }

        // ---- 見出し ----

        /// <summary>丸数字付きのステップ見出しを描画する(例: 「① 診断」)。</summary>
        internal static void StepHeader(int number, string title)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(CircledNumber(number) + " " + title, _stepHeader);
        }

        /// <summary>丸数字の文字列を返す(ウィンドウのステップ見出し用)。</summary>
        internal static string Circled(int n)
        {
            return CircledNumber(n);
        }

        private static string CircledNumber(int n)
        {
            switch (n)
            {
                case 1: return "①";
                case 2: return "②";
                case 3: return "③";
                case 4: return "④";
                case 5: return "⑤";
                case 6: return "⑥";
                case 7: return "⑦";
                case 8: return "⑧";
                case 9: return "⑨";
                default: return n.ToString() + ".";
            }
        }

        // ---- ターゲットチップ ----

        /// <summary>PC/Quest等のターゲットチップ。押されると反転した値を返す(呼び出し側で保存)。</summary>
        internal static bool TargetChip(string label, bool value, float width)
        {
            EnsureStyles();
            Color prev = GUI.backgroundColor;
            if (value) GUI.backgroundColor = AccentColor;
            bool clicked = GUILayout.Button(
                new GUIContent((value ? "☑ " : "☐ ") + label),
                value ? _chipOn : _chipOff,
                GUILayout.Width(width));
            GUI.backgroundColor = prev;
            return clicked ? !value : value;
        }

        // ---- レポート重大度 ----

        internal static string SeverityGlyph(ConversionReport.Severity severity)
        {
            switch (severity)
            {
                case ConversionReport.Severity.Error: return "⛔";
                case ConversionReport.Severity.Warning: return "⚠";
                default: return "・";
            }
        }

        internal static Color SeverityColor(ConversionReport.Severity severity)
        {
            switch (severity)
            {
                case ConversionReport.Severity.Error: return OverLimitColor;
                case ConversionReport.Severity.Warning: return NoteYellowColor;
                default: return GUI.color;
            }
        }

        // ---- AAO(AvatarOptimizer)導入CTA ----

        /// <summary>VCC/ALCOM のリポジトリ追加ページ(AvatarOptimizer を含む anatawa12 VPM リポジトリ)。</summary>
        internal const string AAOAddRepoUrl = "https://vpm.anatawa12.com/add-repo";

        /// <summary>AAO(Avatar Optimizer)の公式ドキュメント(日本語)。</summary>
        internal const string AAODocsUrl = "https://vpm.anatawa12.com/avatar-optimizer/ja/";

        /// <summary>Meshia Mesh Simplification(Ram.Type-0 氏 / MIT)の公式ドキュメント。ポリゴン削減パネルの併用注意から開く。</summary>
        internal const string MeshiaDocsUrl = "https://ramtype0.github.io/Meshia.MeshSimplification/";

        /// <summary>不具合報告・要望の受付先: X(Twitter) の DM。</summary>
        internal const string BugReportXUrl = "https://x.com/RR_vrchat";

        /// <summary>不具合報告・要望の受付先: メール(mailto)。</summary>
        internal const string BugReportMailUrl = "mailto:raravrchat@gmail.com";

        /// <summary>
        /// AAO 未導入ガードの直後に置く導入CTA(ボタン + 1行の手順)。押すと VCC/ALCOM のリポジトリ追加ページを開く。
        /// </summary>
        internal static void DrawAAOInstallCTA()
        {
            if (GUILayout.Button(new GUIContent("AAOを導入する(VCCにリポジトリを追加)",
                "VCC/ALCOM のリポジトリ追加ページを開きます: " + AAOAddRepoUrl)))
            {
                Application.OpenURL(AAOAddRepoUrl);
            }
            EditorGUILayout.LabelField(
                "VCC/ALCOMが開いたら Add Repository → プロジェクトに AvatarOptimizer を追加 → Unityへ戻る",
                MiniWrapLabel);
        }
    }
}
#endif
