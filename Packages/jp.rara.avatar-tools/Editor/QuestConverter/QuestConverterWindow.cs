// RARA Quest Converter - メインウィンドウ(IMGUI)
// PCアバターを複製してQuest(Android)対応へ変換するツールの入口。
// 初めてツールを触る人向けに、画面を「導入 → 基本(背骨) → 詳細・上級 → 実行 → ヘルプ」で構成する:
//   ・導入ヘッダー(DrawIntroHeader): ツールの目的と「Questでできること/できないこと/諦めること」を提示。
//   ・案内バー(DrawModeBar): PC最適化のみは姉妹ツール「RARA PC軽量化ツール」へ移行した旨を先頭で案内する。
//   ・基本(常時表示の背骨): 1.アバター選択 → 2.診断結果 →(詳細・上級)→ 11.実行。
//   ・詳細・上級(既定は畳むfoldout。EditorPrefsで開閉を記憶):
//       3.マテリアル設定 / 4.PhysBone設定 / 5.衣装・トグル整理 / 6.アトラス統合 /
//       7.メッシュ削減(AAO連携) / 8.Quest除外 / 9.変換設定 / 10.ポリゴン削減 / 12.ヘルプ。
//   ・各セクションの見出しは DrawSectionHeader / DrawSectionFoldout に統一し、長い説明は
//     各セクション内の「説明」foldout(DrawExplainFoldout)へ畳んで初見でも短い1画面に収める。
// 診断は QuestDiagnostics.Analyze、変換は AvatarQuestConverter.Convert に委譲する。
// セクション3〜11の描画は QuestConverterWindowSections.cs(partialクラス)に分割している。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>PCアバターをQuest(Android)対応へ変換するメインウィンドウ。</summary>
    public partial class QuestConverterWindow : EditorWindow
    {
        /// <summary>変換設定の永続化に使うEditorPrefsキー。</summary>
        private const string SettingsPrefsKey = "RARA.QuestConverter.Settings";

        // ---- 状態 ----
        [SerializeField]
        private VRCAvatarDescriptor _avatar;                       // 対象アバター(シーン上のみ。スクリプトリロード後も選択を保持)
        private QuestConvertSettings _settings = new QuestConvertSettings();
        private DiagnosticsResult _diagnostics;                    // 直近の診断結果(未診断ならnull)
        private bool _diagnosisStale;                              // 診断後に設定が変更された(再診断が必要)
        private List<MaterialPreviewRow> _materialPreview;         // マテリアル変換プレビュー(診断時に更新)
        private List<DecalOverlayRow> _expressionDecals;           // 表情デカール(チーク/涙/アイハイライト)の自動非表示化プレビュー(診断時に更新。null=未診断)
        private ConversionReport _lastReport;                      // 直近の変換レポート(未実行ならnull)
        private GameObject _convertedAvatar;                       // 直近に生成されたQuest対応アバター
        private bool _autoDiagnoseQueued;                          // アバター設定時の自動診断の再入ガード
        private bool _isDiagnosing;                                // RunDiagnosticsの再入ガード

        // 古い複製(古いレイアウトのアトラス生成物)のプリフライト検出。重い走査を避けるため結果をキャッシュしスロットルする。
        private bool _staleCloneDetected;
        private int _staleCloneAvatarId;
        private double _staleCloneCheckTime;

        private bool _materialPreviewRefreshQueued;                // マテリアルプレビュー再取得の再入ガード
        private PhysBonePreview _physBonePreview;                  // PhysBoneマージ・削除のドライランプレビュー(セクション4。未計算ならnull)
        private bool _physBonePreviewRefreshQueued;                // PhysBoneプレビュー再計算の再入ガード
        private bool _physBonePreviewFailed;                       // PhysBoneプレビュー計算の失敗ラッチ(毎OnGUIの再試行を防ぐ)
        private List<PhysBoneRowDisplay> _physBoneRowDisplays;     // セクション4の行表示キャッシュ(プレビュー構築時に作成。毎フレームの文字列生成を避ける)
        private List<ToggleGroup> _toggleGroups;                   // セクション5「衣装・トグル整理」: 検出されたトグルグループ(未検出ならnull)
        private bool _toggleGroupsRefreshQueued;                   // トグル検出の再入ガード
        private bool _toggleGroupsFailed;                          // トグル検出の失敗ラッチ(毎OnGUIの再試行を防ぐ)
        private List<ShrinkShapeCandidate> _hiddenMeshCandidates;  // セクション7「メッシュ削減(AAO連携)」: 検出されたshrinkブレンドシェイプ候補(未検出ならnull)
        private bool _hiddenMeshRefreshQueued;                     // shrinkブレンドシェイプ検出の再入ガード
        private bool _hiddenMeshFailed;                            // shrinkブレンドシェイプ検出の失敗ラッチ(毎OnGUIの再試行を防ぐ)

        // PhysBoneグループ行のメンバー一覧の開閉状態(キー: グループ先頭メンバーの識別パス)
        private readonly HashSet<string> _physBoneExpandedGroups = new HashSet<string>();

        // セクション7: 隠れメッシュ候補のブレンドシェイプ名一覧の開閉状態(キー: rendererPath)
        private readonly HashSet<string> _hiddenMeshExpanded = new HashSet<string>();

        // マテリアル→GUIDのキャッシュ(毎フレームのAssetDatabase問い合わせを避ける。プレビュー更新時にクリア)
        private readonly Dictionary<Material, string> _materialGuidCache = new Dictionary<Material, string>();

        // ---- UI状態 ----
        private Vector2 _mainScroll;
        private Vector2 _reportScroll;
        private Vector2 _physBoneListScroll;                   // セクション4のPhysBone一覧のスクロール位置(100本超でも軽快に保つ)
        private bool _foldNonMobileMaterials = true;
        private bool _foldUnsupportedComponents = true;
        private bool _foldTextureWarnings = true;
        private bool _foldSizeTextures;                        // サイズ診断: テクスチャ上位(件数が多いため既定で閉じる)
        private bool _foldSizeSuggestions = true;              // サイズ診断: 削減提案
        private bool _foldAdvancedSettings;                    // 変換設定: 詳細設定(既定で閉じる)
        private bool _foldGlossary;                            // ヘルプ: 用語ミニ解説(既定で閉じる)
        private bool _foldResultCheck;                         // 実行: 変換結果チェック(既定で閉じる。開くまでサムネイルは計算しない)
        private List<ConvertedPairRow> _resultPairs;           // 変換結果チェックの行キャッシュ(変換のたびに作り直す)
        private bool _reportWarnOnly;                          // レポート表示フィルター(true=警告以上のみ)
        private bool _showReDiagnoseNote;                      // 削減提案の適用後に再診断を促すフラグ
        private string _excludeAddError;                       // Quest除外: 追加時のエラーメッセージ(成功で消える)
        private GUIStyle _wrapLabel;                           // 折り返しラベル(OnGUI内で遅延生成)
        private GUIStyle _miniWrapLabel;                       // 折り返しミニラベル
        private GUIStyle _badgeLabel;                          // マテリアル状態バッジ用ラベル
        private GUIStyle _verdictLabel;                        // アップロード可否の強調ラベル
        private GUIStyle _titleLabel;                          // 導入ヘッダーのタイトル(大きめボールド)
        private GUIStyle _purposeLabel;                        // セクション見出し直下の目的説明(折り返しミニ)
        private GUIStyle _foldoutHeader;                       // セクション/導入foldoutの見出し(ボールドfoldout)

        // 詳細・上級セクションのfoldout開閉状態はEditorPrefsに保存する(ウィンドウを閉じても記憶される)。
        // 毎OnGUIのEditorPrefsアクセスを避けるため、値はこの辞書にキャッシュする。
        private readonly Dictionary<string, bool> _foldPrefCache = new Dictionary<string, bool>();

        // ---- 導入・セクションfoldoutのEditorPrefsキー ----
        private const string IntroSeenPrefKey = "RARA.QuestConverter.IntroSeen";               // 初回起動判定(未見なら導入を開いて表示)
        private const string IntroFoldPrefKey = "RARA.QuestConverter.Fold.Intro";              // 「はじめての方へ」foldout(導入+目標ランクガイド)の開閉
        private const string FoldKeyMaterial = "RARA.QuestConverter.Fold.Section.Material";    // 3. マテリアル設定
        private const string FoldKeyPhysBone = "RARA.QuestConverter.Fold.Section.PhysBone";    // 4. PhysBone設定
        private const string FoldKeyOutfit = "RARA.QuestConverter.Fold.Section.Outfit";        // 5. 衣装・トグル整理
        private const string FoldKeyAtlas = "RARA.QuestConverter.Fold.Section.Atlas";          // 6. アトラス統合
        private const string FoldKeyHiddenMesh = "RARA.QuestConverter.Fold.Section.HiddenMesh";// 7. メッシュ削減(AAO連携)
        private const string FoldKeyExclude = "RARA.QuestConverter.Fold.Section.Exclude";      // 8. Quest除外
        private const string FoldKeySettings = "RARA.QuestConverter.Fold.Section.Settings";    // 9. 変換設定
        private const string FoldKeyPolygon = "RARA.QuestConverter.Fold.Section.Polygon";      // 10. ポリゴン削減
        private const string FoldKeyHelp = "RARA.QuestConverter.Fold.Section.Help";            // 12. ヘルプ

        /// <summary>サイズ診断のテクスチャ上位リストの表示上限。</summary>
        private const int SizeTextureRowMax = 15;

        // 上限超過・不可表示の強調色
        private static readonly Color OverLimitColor = new Color(1f, 0.42f, 0.42f);

        // アップロード可の強調色
        private static readonly Color UploadOkColor = new Color(0.35f, 0.85f, 0.45f);

        // 注意書き(黄色note)の表示色
        private static readonly Color NoteYellowColor = new Color(1f, 0.85f, 0.35f);

        // 変換先シェーダーの日本語ラベル(QuestShaderTargetの並び順と一致させること)
        private static readonly GUIContent[] ShaderTargetLabels =
        {
            new GUIContent("Toon Standard(推奨)", "VRChat/Mobile/Toon Standard。影ランプ・ノーマルマップ・エミッション・リムに対応"),
            new GUIContent("Toon Lit(最軽量)", "VRChat/Mobile/Toon Lit。陰影・エミッションはメインテクスチャへベイク"),
        };

        private static readonly GUIContent[] TextureSizeLabels =
        {
            new GUIContent("512"),
            new GUIContent("1024(推奨)"),
            new GUIContent("2048"),
        };
        private static readonly int[] TextureSizeValues = { 512, 1024, 2048 };

        // Android圧縮形式はASTCのみに限定
        private static readonly GUIContent[] AndroidFormatLabels =
        {
            new GUIContent("ASTC 4x4(高品質・大サイズ)"),
            new GUIContent("ASTC 5x5"),
            new GUIContent("ASTC 6x6(推奨)"),
            new GUIContent("ASTC 8x8(低品質・小サイズ)"),
        };
        private static readonly int[] AndroidFormatValues =
        {
            (int)TextureImporterFormat.ASTC_4x4,
            (int)TextureImporterFormat.ASTC_5x5,
            (int)TextureImporterFormat.ASTC_6x6,
            (int)TextureImporterFormat.ASTC_8x8,
        };

        // Phase 3: RARA メニューは統合ウィンドウの2項目のみへ整理。旧UIはメニューから外したが、
        // 互換のため Open() API は残す(統合ウィンドウ側や他コードから直接呼べる)。
        public static void Open()
        {
            var window = GetWindow<QuestConverterWindow>();
            window.titleContent = new GUIContent("RARA Quest変換");
            window.minSize = new Vector2(430f, 560f);
            window.Show();
        }

        /// <summary>
        /// アバターを指定してウィンドウを開く(PC軽量化ツール等の姉妹ツールから、生成したクローンを
        /// そのままQuest変換フローへ引き渡すための入口)。ウィンドウを前面に出し、指定アバターを
        /// 選択して通常の自動診断パスを起動する。avatar が null なら通常起動と同じ挙動になる。
        /// シーン上のVRCAvatarDescriptorを持たない/プレハブアセットのオブジェクトは選択せず、警告を出す。
        /// </summary>
        public static void Open(GameObject avatar)
        {
            Open();                                        // 既存のメニュー起動と同じ初期化(生成・タイトル・最小サイズ・表示)
            var window = GetWindow<QuestConverterWindow>();
            window.Focus();
            if (avatar == null) return;

            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                Debug.LogWarning("[RARA QuestConverter] 指定オブジェクトにVRCAvatarDescriptorが見つからないため、アバターは自動選択されませんでした: " + avatar.name);
                return;
            }
            if (EditorUtility.IsPersistent(descriptor))
            {
                // シーン上のインスタンスのみ受け付ける(プレハブアセットは不可)。
                Debug.LogWarning("[RARA QuestConverter] シーン上のアバターを指定してください(プレハブアセットは自動選択できません): " + avatar.name);
                return;
            }
            window.SetAvatar(descriptor);                  // 別アバターなら診断結果をクリアして自動診断を予約する
            window.Repaint();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("RARA Quest変換");
            LoadSettings();
            // スクリプトリロード後: _avatar はシリアライズで復元されるが、診断結果・プレビューは
            // 復元されないため自動で再診断する(アバターを選び直す手間をなくす)
            if (_avatar != null && _diagnostics == null) QueueAutoDiagnosis();
        }

        private void OnDisable()
        {
            // ウィンドウを閉じるときの保存では診断を古い扱いにしない(設定は変わっていない)
            SaveSettings(false);
        }

        private void OnGUI()
        {
            EnsureStyles();

            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            // 導入: ツールの目的 +「できること/できないこと/諦めること」
            DrawIntroHeader();

            // 案内バー: PC最適化のみは姉妹ツール「RARA PC軽量化ツール」へ移行した旨を先頭で案内する。
            DrawModeBar();

            // ---- 基本(常時表示の背骨): アバター選択 → 診断 ----
            DrawAvatarSection();       // 1. アバター選択
            DrawDiagnosticsSection();  // 2. 診断結果

            // ---- 詳細・上級(既定は畳むfoldout。必要な人だけ開く) ----
            DrawGroupDivider("詳細・上級設定(必要に応じて開いてください)");
            DrawMaterialSection();     // 3. マテリアル設定(Sections側)
            DrawPhysBoneSection();     // 4. PhysBone設定(Sections側)
            DrawOutfitToggleSection(); // 5. 衣装・トグル整理(Sections側)
            DrawAtlasSection();        // 6. アトラス統合(Sections側)
            DrawHiddenMeshRemovalSection(); // 7. メッシュ削減(AAO連携)(Sections側)
            DrawExcludeSection();      // 8. Quest除外(Sections側)
            DrawSettingsSection();     // 9. 変換設定(Sections側)
            DrawPolygonReductionSection(); // 10. ポリゴン削減(Sections側)

            // ---- 基本(常時表示の背骨): 実行 ----
            DrawGroupDivider("設定はここまで。最後にQuest対応版を生成します");
            DrawConvertSection();      // 11. 実行(Sections側)

            DrawHelpSection();         // 12. ヘルプ(Sections側)
            EditorGUILayout.EndScrollView();
        }

        // ================================================================
        // 導入ヘッダー / 共通見出し・foldoutヘルパー
        // ================================================================

        /// <summary>
        /// 導入ヘッダー。ウィンドウ最上部に「アバター / 総合ランク / 推定サイズ」の1行サマリー(常時表示)と、
        /// 「はじめての方へ」foldout(できること・できないこと・諦めること + 目標ランク達成ガイドを集約。既定で畳む)
        /// を描画する。foldoutは初回起動時(IntroSeenPrefKeyが未設定)だけ開いて見せ、以後は既定で畳む
        /// (開閉状態はEditorPrefsに記憶)。
        /// </summary>
        private void DrawIntroHeader()
        {
            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // 常時表示の1行サマリー(対象アバター / 総合ランク / 推定ダウンロードサイズ)
                DrawTopSummaryLine();

                // 「はじめての方へ」= 導入トライパネル + 目標ランク達成ガイドを1つに集約(既定で畳む)
                bool introSeen = EditorPrefs.GetBool(IntroSeenPrefKey, false);
                bool expanded = GetFoldPref(IntroFoldPrefKey, !introSeen); // 初回起動時だけ開いて表示する
                bool newExpanded = EditorGUILayout.Foldout(
                    expanded, "はじめての方へ(できること・できないこと・目標ランクの目安)", true, _foldoutHeader);
                if (newExpanded != expanded) SetFoldPref(IntroFoldPrefKey, newExpanded);
                if (!introSeen) EditorPrefs.SetBool(IntroSeenPrefKey, true); // 一度表示したので次回からは既定で畳む

                if (newExpanded)
                {
                    EditorGUILayout.LabelField("RARA Quest対応コンバーター", _titleLabel);
                    EditorGUILayout.LabelField(
                        "PCアバターの複製を作り、Quest(Android)向けにシェーダー・テクスチャ・メッシュ・揺れものを" +
                        "変換・削減してアップロードできる状態にするツールです。元のアバターは変更しません。" +
                        "上から順に進めれば完了します。" +
                        "PC版のランク改善(テクスチャ・スロット削減など)は姉妹ツール「RARA PC軽量化ツール」で。",
                        _wrapLabel);
                    DrawIntroTriPanel();
                    DrawGoalGuidePanel(); // 目標ランク達成ガイド(診断済みのときだけ中身が出る。実装はSections側)
                }
            }
        }

        /// <summary>ウィンドウ最上部に常時表示する1行サマリー(対象アバター / 総合ランク / 推定ダウンロードサイズ)。</summary>
        private void DrawTopSummaryLine()
        {
            var defaultColor = GUI.color;
            using (new EditorGUILayout.HorizontalScope())
            {
                string avatarName = _avatar != null ? _avatar.gameObject.name : "未選択";
                EditorGUILayout.LabelField(new GUIContent("アバター: " + avatarName, "現在の対象アバター"), EditorStyles.boldLabel);

                if (_diagnostics == null)
                {
                    EditorGUILayout.LabelField("ランク: 未診断", EditorStyles.miniLabel, GUILayout.Width(150f));
                }
                else
                {
                    string rank = string.IsNullOrEmpty(_diagnostics.overallRating) ? "不明" : _diagnostics.overallRating;
                    GUI.color = _diagnostics.canUploadToAndroid ? UploadOkColor : OverLimitColor;
                    EditorGUILayout.LabelField("ランク: " + rank, EditorStyles.miniBoldLabel, GUILayout.Width(150f));
                    GUI.color = defaultColor;
                }

                SizeEstimateResult est = _diagnostics != null ? _diagnostics.sizeEstimate : null;
                if (est == null)
                {
                    EditorGUILayout.LabelField("推定: -", EditorStyles.miniLabel, GUILayout.Width(135f));
                }
                else
                {
                    GUI.color = est.overCap ? OverLimitColor : defaultColor;
                    EditorGUILayout.LabelField(
                        "推定: " + est.estimatedDownloadMB.ToString("F1") + " / " + QuestLimits.HardDownloadSizeCapMB + " MB",
                        EditorStyles.miniLabel, GUILayout.Width(135f));
                    GUI.color = defaultColor;
                }
            }
        }

        /// <summary>「Questでできること/できないこと/諦めること」の3ブロックを縦に描画する(初見の期待値合わせ)。</summary>
        private void DrawIntroTriPanel()
        {
            EditorGUILayout.LabelField("Questでできること", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "・シェーダー質感のベイク変換(見た目を近づける)\n" +
                "・パーティクルによる透過の近似(加算/乗算)\n" +
                "・アトラス統合でマテリアルスロットを削減\n" +
                "・PhysBoneのマージ・選別で揺れものを上限内に\n" +
                "・衣装トグルの固定でメッシュ・スロットを削減\n" +
                "・テクスチャの極限縮小で容量を削減\n" +
                "・隠面メッシュ削除(AAO連携。服の下の見えない部分)",
                MessageType.Info);

            EditorGUILayout.LabelField("Questでできないこと", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "・メッシュの半透明(QuestはParticles加算/乗算のみ。カットアウト=アルファ抜きもありません)\n" +
                "・アウトライン・マットキャップなど高度な質感の再現",
                MessageType.Warning);

            EditorGUILayout.LabelField("諦める(割り切る)こと", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "・衣装のオンオフを減らすほどメッシュが減ります(全部残したいならPC版で残せばOK)\n" +
                "・透過は乗算/加算/非表示での近似になります\n" +
                "・テクスチャ解像度は落とす前提です",
                MessageType.None);
        }

        /// <summary>常時表示セクションの統一見出し(番号+タイトルのボールド行 + 目的の折り返し説明)。</summary>
        private void DrawSectionHeader(int number, string title, string purpose)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(number + ". " + title, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(purpose))
            {
                EditorGUILayout.LabelField(purpose, _purposeLabel);
            }
        }

        /// <summary>
        /// 詳細・上級セクションの統一foldout見出し(番号+タイトル)。開閉状態はEditorPrefsに保存する
        /// (既定は畳む)。目的の説明は畳んでいても常に見せて初見の道しるべにする。
        /// 展開されていれば true を返す(呼び出し側は true のときだけセクション本体を描画する)。
        /// </summary>
        private bool DrawSectionFoldout(int number, string title, string purpose, string prefKey)
        {
            EditorGUILayout.Space(6f);
            bool expanded = GetFoldPref(prefKey, false); // 詳細・上級は既定で畳む(初見で短い1画面にする)
            bool newExpanded = EditorGUILayout.Foldout(expanded, number + ". " + title, true, _foldoutHeader);
            if (newExpanded != expanded) SetFoldPref(prefKey, newExpanded);
            if (!string.IsNullOrEmpty(purpose))
            {
                EditorGUILayout.LabelField(purpose, _purposeLabel);
            }
            return newExpanded;
        }

        /// <summary>
        /// セクション内の長い説明HelpBoxを畳むための「説明」foldout(開閉状態はEditorPrefsに保存)。
        /// true を返したときだけ、呼び出し側が説明本体(HelpBox等)を描画する。
        /// </summary>
        private bool DrawExplainFoldout(string prefKey)
        {
            bool expanded = GetFoldPref(prefKey, false);
            bool newExpanded = EditorGUILayout.Foldout(expanded, "説明", true);
            if (newExpanded != expanded) SetFoldPref(prefKey, newExpanded);
            return newExpanded;
        }

        /// <summary>基本/詳細などのグループ境界を示す控えめな見出し行。</summary>
        private void DrawGroupDivider(string label)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
        }

        /// <summary>foldout開閉状態をEditorPrefsから読む(値はキャッシュして毎OnGUIのアクセスを避ける)。</summary>
        private bool GetFoldPref(string key, bool defaultValue)
        {
            if (_foldPrefCache.TryGetValue(key, out bool cached)) return cached;
            bool value = EditorPrefs.GetBool(key, defaultValue);
            _foldPrefCache[key] = value;
            return value;
        }

        /// <summary>foldout開閉状態をEditorPrefsへ保存する(キャッシュも更新)。</summary>
        private void SetFoldPref(string key, bool value)
        {
            _foldPrefCache[key] = value;
            EditorPrefs.SetBool(key, value);
        }

        // ================================================================
        // 案内バー(PC最適化のみは姉妹ツールへ移行)
        // ================================================================

        /// <summary>
        /// 先頭の案内バー。旧「変換モード(PC最適化のみ)」の選択UIは廃止し、PCランク改善(テクスチャ・
        /// スロット削減など)は姉妹ツール「RARA PC軽量化ツール」へ移行した旨をHelpBoxで案内する。
        /// このコンバーターはQuest(Android)対応の変換に専念する(ConversionMode列挙・変換コードパスは
        /// 後方互換のため残すが、UIからは選択できない。旧設定のConsolidateOnlyはLoadSettingsで戻す)。
        /// </summary>
        private void DrawModeBar()
        {
            if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "PC最適化のみモードは『RARA PC軽量化ツール』へ移行しました(メニュー RARA > PC軽量化ツール)。" +
                "このコンバーターはQuest(Android)対応の変換に専念します。",
                MessageType.Info);
        }

        // ================================================================
        // セクション1: アバター選択
        // ================================================================
        private void DrawAvatarSection()
        {
            UpdateStaleCloneFlag();
            DrawSectionHeader(1, "アバター選択",
                "変換するアバター(シーン上のVRCAvatarDescriptor)を指定します。指定すると自動で診断します。");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = EditorGUILayout.ObjectField(
                        new GUIContent("アバター", "変換元のVRCAvatarDescriptor(シーン上のオブジェクトのみ)"),
                        _avatar, typeof(VRCAvatarDescriptor), true) as VRCAvatarDescriptor;
                    if (picked != _avatar)
                    {
                        if (picked != null && EditorUtility.IsPersistent(picked))
                        {
                            // プレハブアセット等は不可。シーン上のインスタンスのみ受け付ける。
                            EditorUtility.DisplayDialog("アバター選択",
                                "シーン上のアバターを指定してください(プレハブアセットは指定できません)。", "OK");
                        }
                        else
                        {
                            SetAvatar(picked);
                        }
                    }

                    if (GUILayout.Button(new GUIContent("シーンから検出", "開いているシーンからVRCAvatarDescriptorを検索します"), GUILayout.Width(110f)))
                    {
                        DetectAvatarsInScene();
                    }
                }

                if (_avatar == null)
                {
                    EditorGUILayout.HelpBox("変換するアバター(VRCAvatarDescriptor)をシーンから指定してください。指定すると自動で診断が実行されます。", MessageType.Info);
                }
                else if (_staleCloneDetected)
                {
                    EditorGUILayout.HelpBox("シーン内の複製は古い生成物です。削除して再生成してください(自動でも削除されます)", MessageType.Warning);
                }
            }
        }

        /// <summary>
        /// シーンに、現世代の生成メッシュと異なるレイアウトハッシュのアトラスメッシュを参照する
        /// 古い複製が残っていないかを安価に判定してキャッシュする(2秒スロットル)。決して例外を投げない。
        /// </summary>
        private void UpdateStaleCloneFlag()
        {
            if (_avatar == null || _settings == null) { _staleCloneDetected = false; return; }
            int id = _avatar.GetInstanceID();
            double now = EditorApplication.timeSinceStartup;
            if (id == _staleCloneAvatarId && now - _staleCloneCheckTime < 2.0) return;
            _staleCloneAvatarId = id;
            _staleCloneCheckTime = now;
            try
            {
                string root = string.IsNullOrEmpty(_settings.outputFolder)
                    ? "Assets/RARA/QuestConverter/Generated"
                    : _settings.outputFolder.Replace('\\', '/').TrimEnd('/');
                string safe = QuestConverterUtility.SanitizeAssetName(_avatar.gameObject.name);
                string meshesFolder = root + "/" + safe + "/Meshes";
                _staleCloneDetected = MaterialAtlasser.SceneHasStaleAtlasClone(
                    _avatar.gameObject, _avatar.gameObject.name + "_Quest", meshesFolder);
            }
            catch
            {
                _staleCloneDetected = false;
            }
        }

        /// <summary>
        /// アバターを設定する。別のアバターに変わったら古い診断結果をクリアし、
        /// 新しいアバターの診断を自動で予約する(初めての人が診断ボタンを探さなくて済むように)。
        /// </summary>
        private void SetAvatar(VRCAvatarDescriptor avatar)
        {
            if (_avatar == avatar) return;
            _avatar = avatar;
            _diagnostics = null;       // 別アバターの診断結果を表示し続けないようクリア
            _materialPreview = null;   // マテリアルプレビューも同様にクリア
            _expressionDecals = null;  // 表情デカール検出結果もクリア(診断時に再検出される)
            _physBonePreview = null;   // PhysBoneプレビューも同様にクリア(診断時に再計算される)
            _physBoneRowDisplays = null; // PhysBone行表示キャッシュも破棄(プレビュー再計算時に作り直す)
            _physBonePreviewFailed = false;
            _physBoneExpandedGroups.Clear();
            _toggleGroups = null;      // トグルグループ検出結果もクリア(診断時に再検出される)
            _toggleGroupsFailed = false;
            _hiddenMeshCandidates = null; // shrinkブレンドシェイプ候補もクリア(診断時に再検出される)
            _hiddenMeshFailed = false;
            _hiddenMeshExpanded.Clear();
            _excludeAddError = null;   // 前のアバター宛ての除外追加エラーも消す
            _diagnosisStale = false;
            QueueAutoDiagnosis();
        }

        /// <summary>
        /// アバター設定直後の自動診断を1回だけ予約する(OnGUI中の重い処理を避けてdelayCallで実行)。
        /// _autoDiagnoseQueued で再入をガードする。
        /// </summary>
        private void QueueAutoDiagnosis()
        {
            if (_avatar == null || _autoDiagnoseQueued) return;
            _autoDiagnoseQueued = true;
            EditorApplication.delayCall += () =>
            {
                _autoDiagnoseQueued = false;
                if (this == null) return;   // ウィンドウが閉じられた(Unityのnull比較)
                if (_avatar == null) return;
                RunDiagnostics();
                Repaint();
            };
        }

        /// <summary>
        /// マテリアルプレビューのみの再取得を1回だけ予約する(診断全体は再実行しない)。
        /// OnGUIの描画ループが _materialPreview を列挙している最中にリストを差し替えないよう、
        /// delayCall で実行する(_materialPreviewRefreshQueued で再入をガード)。
        /// </summary>
        private void QueueMaterialPreviewRefresh()
        {
            if (_avatar == null || _materialPreviewRefreshQueued) return;
            _materialPreviewRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _materialPreviewRefreshQueued = false;
                if (this == null) return;   // ウィンドウが閉じられた(Unityのnull比較)
                if (_avatar == null) return;
                RefreshMaterialPreview();
                Repaint();
            };
        }

        /// <summary>
        /// PhysBoneプレビュー(セクション4)の再計算を1回だけ予約する(ドライランのため軽量だが、
        /// 毎OnGUIの再計算は避ける)。OnGUIの描画ループが行リストを列挙している最中に
        /// リストを差し替えないよう、delayCallで実行する(_physBonePreviewRefreshQueuedで再入をガード)。
        /// </summary>
        private void QueuePhysBonePreviewRefresh()
        {
            if (_avatar == null || _physBonePreviewRefreshQueued) return;
            _physBonePreviewRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _physBonePreviewRefreshQueued = false;
                if (this == null) return;   // ウィンドウが閉じられた(Unityのnull比較)
                if (_avatar == null) return;
                RefreshPhysBonePreview();
                Repaint();
            };
        }

        /// <summary>シーン内のアバターを検出して選択させる(1体なら即設定、複数ならメニュー表示)。</summary>
        private void DetectAvatarsInScene()
        {
            // 非アクティブも含めて全ロード済みシーンから検索(FindObjectsOfType(true) 相当)
            var avatars = FindObjectsByType<VRCAvatarDescriptor>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
            if (avatars == null || avatars.Length == 0)
            {
                EditorUtility.DisplayDialog("シーンから検出",
                    "シーン内にVRCAvatarDescriptorを持つオブジェクトが見つかりませんでした。", "OK");
                return;
            }

            if (avatars.Length == 1)
            {
                SetAvatar(avatars[0]);
                return;
            }

            // 複数見つかった場合はポップアップメニューで選択
            var menu = new GenericMenu();
            foreach (var candidate in avatars)
            {
                if (candidate == null) continue;
                var local = candidate; // クロージャ用
                menu.AddItem(new GUIContent(GetMenuLabel(local)), _avatar == local, () =>
                {
                    SetAvatar(local);
                    Repaint();
                });
            }
            menu.ShowAsContext();
        }

        /// <summary>メニュー表示用ラベル。"/"はGenericMenuのサブメニュー区切りになるため置換する。</summary>
        private static string GetMenuLabel(VRCAvatarDescriptor avatar)
        {
            var t = avatar.transform;
            var sb = new System.Text.StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + " > ");
            }
            if (!avatar.gameObject.activeInHierarchy) sb.Append(" (非アクティブ)");
            return sb.ToString().Replace("/", "_");
        }

        // ================================================================
        // セクション2: 診断結果
        // ================================================================
        private void DrawDiagnosticsSection()
        {
            DrawSectionHeader(2, "診断結果",
                "現在の構成がQuestの基準を満たすか診断し、サイズ推定や削減提案を表示します。");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると自動で診断されます。", EditorStyles.miniLabel);
                    return;
                }

                if (_diagnostics == null)
                {
                    if (_autoDiagnoseQueued || _isDiagnosing)
                    {
                        EditorGUILayout.LabelField("診断を準備中...", EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("まだ診断していません。", EditorStyles.miniLabel);
                        if (GUILayout.Button(new GUIContent("診断を実行",
                            "現在のアバターをAndroid(Quest)のパフォーマンス基準で診断します"), GUILayout.Height(24f)))
                        {
                            RunDiagnostics();
                        }
                    }
                    return;
                }

                DrawUploadVerdictLine();

                // 診断後に設定が変わった場合は「古い診断」であることを明示する
                if (_diagnosisStale)
                {
                    EditorGUILayout.HelpBox(
                        "設定が変更されたため、この診断結果は最新でない可能性があります。" +
                        "「再診断」を押すか、そのまま生成すると自動で再診断されます。",
                        MessageType.Warning);
                }

                DrawOverallRating();
                DrawPerfTable();
                DrawDiagnosticsLists();
                DrawSizeEstimateSection();
                // 目標ランク達成ガイド(DrawGoalGuidePanel)は「はじめての方へ」foldout(DrawIntroHeader)へ集約した
            }
        }

        /// <summary>Androidアップロード可否を色付きの大きめラベルで表示し、再診断ボタンを並べる。</summary>
        private void DrawUploadVerdictLine()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool ok = _diagnostics.canUploadToAndroid;
                var prev = GUI.color;
                GUI.color = ok ? UploadOkColor : OverLimitColor;
                EditorGUILayout.LabelField(
                    ok ? "Android アップロード: 可" : "Android アップロード: 不可(Very Poor)",
                    _verdictLabel, GUILayout.Height(20f));
                GUI.color = prev;
                if (GUILayout.Button(new GUIContent("再診断", "現在の設定で診断をやり直します"), GUILayout.Width(70f)))
                {
                    RunDiagnostics();
                }
            }
        }

        /// <summary>
        /// 診断を実行する(_isDiagnosingで再入ガード)。
        /// 成功時はマテリアルプレビューも更新し、stale(古い診断)フラグを解除する。
        /// </summary>
        private void RunDiagnostics()
        {
            if (_avatar == null) return;
            if (_isDiagnosing) return; // 再入ガード(診断中の診断ボタン連打・自動診断の重複)
            if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)

            _isDiagnosing = true;
            _showReDiagnoseNote = false; // 再診断したので案内を消す
            try
            {
                // 設定(除外パス・Android圧縮形式)を反映した診断+サイズ推定
                _diagnostics = QuestDiagnostics.Analyze(_avatar, _settings);
                RefreshMaterialPreview(); // マテリアル設定テーブルも同時に更新
                RefreshExpressionDecals(); // 表情デカール(チーク/涙/アイハイライト)の検出も同時に更新
                RefreshPhysBonePreview(); // PhysBone設定のプレビューも同時に更新
                RefreshToggleGroups();    // 衣装・トグル整理の検出結果も同時に更新
                RefreshHiddenMeshCandidates(); // メッシュ削減(AAO連携)のshrinkブレンドシェイプ検出も同時に更新
                _diagnosisStale = false;
            }
            catch (Exception ex)
            {
                _diagnostics = null;
                _materialPreview = null;
                _expressionDecals = null;
                _physBonePreview = null;
                _toggleGroups = null;
                _hiddenMeshCandidates = null;
                Debug.LogError("[RARA QuestConverter] 診断中に例外が発生しました: " + ex);
                EditorUtility.DisplayDialog("Quest適合診断", "診断中に例外が発生しました:\n" + ex.Message, "OK");
            }
            finally
            {
                _isDiagnosing = false;
            }
        }

        /// <summary>総合ランクを色付きHelpBoxで表示する。</summary>
        private void DrawOverallRating()
        {
            string rating = string.IsNullOrEmpty(_diagnostics.overallRating) ? "不明" : _diagnostics.overallRating;
            string key = NormalizeRating(rating);

            string message = "総合パフォーマンスランク: " + rating;
            MessageType type;
            if (key == "verypoor" || !_diagnostics.canUploadToAndroid)
            {
                type = MessageType.Error;
                message += "\nAndroidへはアップロードできません。変換・削減が必要です。";
            }
            else if (key == "poor")
            {
                type = MessageType.Warning;
                message += "\nアップロードは可能ですが、Quest既定の表示ランク(Medium)を超えています。";
            }
            else
            {
                type = MessageType.Info; // Excellent / Good / Medium
            }
            EditorGUILayout.HelpBox(message, type);
        }

        /// <summary>パフォーマンス統計のテーブル表示。上限超過行は赤、ランク列はランク色で着色する。</summary>
        private void DrawPerfTable()
        {
            var rows = _diagnostics.perfRows;
            if (rows == null || rows.Count == 0) return;

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                // ヘッダー行
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(130f));
                    EditorGUILayout.LabelField("値", EditorStyles.miniBoldLabel, GUILayout.Width(110f));
                    EditorGUILayout.LabelField("ランク", EditorStyles.miniBoldLabel, GUILayout.Width(80f));
                }

                var defaultColor = GUI.color;
                foreach (var row in rows)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // 上限超過行は全体を赤系に着色
                        GUI.color = row.overLimit ? OverLimitColor : defaultColor;
                        EditorGUILayout.LabelField(row.category ?? "", GUILayout.MinWidth(130f));
                        EditorGUILayout.LabelField(row.value ?? "", GUILayout.Width(110f));

                        // ランクセルはランク別の色で着色
                        GUI.color = row.overLimit ? OverLimitColor : RatingColor(row.rating);
                        EditorGUILayout.LabelField(row.rating ?? "", EditorStyles.boldLabel, GUILayout.Width(80f));
                        GUI.color = defaultColor;
                    }
                }
                GUI.color = defaultColor;
            }
        }

        /// <summary>非Mobileマテリアル・非対応コンポーネント・テクスチャ警告のフォルドアウト表示。</summary>
        private void DrawDiagnosticsLists()
        {
            // 非Mobileシェーダーのマテリアル一覧
            var mats = _diagnostics.nonMobileMaterials;
            int matCount = mats != null ? mats.Count : 0;
            _foldNonMobileMaterials = EditorGUILayout.Foldout(_foldNonMobileMaterials,
                "非Mobileシェーダーのマテリアル一覧 (" + matCount + ")", true);
            if (_foldNonMobileMaterials)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    if (matCount == 0)
                    {
                        EditorGUILayout.LabelField("なし(すべてMobile対応シェーダーです)", EditorStyles.miniLabel);
                    }
                    else
                    {
                        using (new EditorGUI.DisabledScope(true)) // 読み取り専用表示
                        {
                            foreach (var mat in mats)
                            {
                                EditorGUILayout.ObjectField(mat, typeof(Material), false);
                            }
                        }
                    }
                }
            }

            // 非対応コンポーネント一覧
            var comps = _diagnostics.unsupportedComponents;
            int compCount = comps != null ? comps.Count : 0;
            _foldUnsupportedComponents = EditorGUILayout.Foldout(_foldUnsupportedComponents,
                "非対応コンポーネント一覧 (" + compCount + ")", true);
            if (_foldUnsupportedComponents)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    if (compCount == 0)
                    {
                        EditorGUILayout.LabelField("なし", EditorStyles.miniLabel);
                    }
                    else
                    {
                        using (new EditorGUI.DisabledScope(true)) // 読み取り専用表示
                        {
                            foreach (var comp in comps)
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    EditorGUILayout.LabelField(comp != null ? comp.GetType().Name : "(欠落)", GUILayout.Width(170f));
                                    EditorGUILayout.ObjectField(comp, typeof(Component), true);
                                }
                            }
                        }
                    }
                }
            }

            // テクスチャ警告一覧
            var texWarnings = _diagnostics.textureWarnings;
            int texCount = texWarnings != null ? texWarnings.Count : 0;
            _foldTextureWarnings = EditorGUILayout.Foldout(_foldTextureWarnings,
                "テクスチャ警告一覧 (" + texCount + ")", true);
            if (_foldTextureWarnings)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    if (texCount == 0)
                    {
                        EditorGUILayout.LabelField("なし", EditorStyles.miniLabel);
                    }
                    else
                    {
                        foreach (var warning in texWarnings)
                        {
                            EditorGUILayout.LabelField("・" + warning, _wrapLabel);
                        }
                    }
                }
            }
        }

        // ================================================================
        // セクション2b: サイズ診断(推定)
        // ================================================================

        /// <summary>推定ダウンロードサイズ・内訳・削減提案の表示(診断結果にsizeEstimateがある場合のみ)。</summary>
        private void DrawSizeEstimateSection()
        {
            SizeEstimateResult est = _diagnostics != null ? _diagnostics.sizeEstimate : null;
            if (est == null) return;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("サイズ診断(推定)", EditorStyles.boldLabel);
            // PC最適化のみモードではテクスチャ変換・縮小を行わないため、下記の縮小提案は適用対象外になる旨を注記する
            DrawConsolidateOnlySkipNote("Quest向けのテクスチャ変換・縮小(下の縮小提案の適用)");

            // 上限に対する推定ダウンロードサイズ(ビルド前の目安であり実測値ではない)
            float cap = QuestLimits.HardDownloadSizeCapMB;
            string message = "推定ダウンロードサイズ " + est.estimatedDownloadMB.ToString("F1") +
                             " MB / 上限 " + QuestLimits.HardDownloadSizeCapMB + " MB(ビルド前の目安)";
            MessageType type;
            if (est.overCap)
            {
                type = MessageType.Error;
                message += "\n10MBを超える見込みです。以下の提案で削減してください";
            }
            else if (est.estimatedDownloadMB >= cap * 0.8f)
            {
                type = MessageType.Warning; // 上限の20%以内まで迫っている
            }
            else
            {
                type = MessageType.Info;
            }
            EditorGUILayout.HelpBox(message, type);

            // 内訳(すべて目安)
            EditorGUILayout.LabelField(
                "内訳(目安): テクスチャ " + est.textureDownloadMB.ToString("F1") +
                " MB / メッシュ " + est.meshDownloadMB.ToString("F1") +
                " MB / アニメ " + est.animationDownloadMB.ToString("F1") +
                " MB / テクスチャメモリ(VRAM) " + est.textureMemoryMB.ToString("F1") + " MB",
                _wrapLabel);

            DrawSizeTextureList(est);
            DrawSizeSuggestions(est);
        }

        /// <summary>ダウンロードサイズの大きいテクスチャ上位のフォルドアウト表示。</summary>
        private void DrawSizeTextureList(SizeEstimateResult est)
        {
            var textures = est.textures;
            int total = textures != null ? textures.Count : 0;
            int shown = Mathf.Min(total, SizeTextureRowMax);
            _foldSizeTextures = EditorGUILayout.Foldout(_foldSizeTextures,
                "テクスチャ上位 (" + shown + "/" + total + ")", true);
            if (!_foldSizeTextures) return;

            using (new EditorGUI.IndentLevelScope())
            {
                if (total == 0)
                {
                    EditorGUILayout.LabelField("なし", EditorStyles.miniLabel);
                    return;
                }

                // downloadMB降順の上位のみ表示(元リストは変更しない)
                var sorted = new List<TextureSizeInfo>(textures);
                sorted.Sort((a, b) => b.downloadMB.CompareTo(a.downloadMB));

                var defaultColor = GUI.color;
                for (int i = 0; i < shown; i++)
                {
                    TextureSizeInfo info = sorted[i];
                    if (info == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true)) // 読み取り専用表示
                        {
                            EditorGUILayout.ObjectField(info.texture, typeof(Texture), false, GUILayout.Width(150f));
                        }
                        EditorGUILayout.LabelField(
                            info.downloadMB.ToString("F2") + " MB / " + info.formatLabel + " / 最大" + info.currentAndroidMaxSize + "px",
                            GUILayout.MinWidth(140f));
                        if (!info.hasAndroidOverride)
                        {
                            GUI.color = NoteYellowColor;
                            EditorGUILayout.LabelField("Android上書きなし", EditorStyles.miniLabel, GUILayout.Width(105f));
                            GUI.color = defaultColor;
                        }
                        else
                        {
                            // 現行版はインポート設定を変更しないため、この上書きは元アセット側の設定
                            // (旧バージョンの本ツールが適用したもの、または手動設定)が残っている状態。
                            // 見積もりは実際の上書き値で計算済みだが、元アセットに設定が残っている事実を明示する。
                            EditorGUILayout.LabelField(new GUIContent("Android上書きあり",
                                "このテクスチャの元アセットにAndroid向けインポート上書きが設定されています。" +
                                "現在の本ツールは元テクスチャのインポート設定を変更しません(縮小計画は縮小コピーを生成するだけです)。" +
                                "旧バージョンの本ツールや手動設定で付与された上書きは元アセットに残ったままのため、" +
                                "不要な場合はテクスチャのインポート設定(Androidタブ)から解除してください。" +
                                "サイズ見積もりはこの上書き値を反映済みです。"),
                                EditorStyles.miniLabel, GUILayout.Width(105f));
                        }
                    }
                }
                GUI.color = defaultColor;
            }
        }

        /// <summary>削減提案のフォルドアウト表示。テクスチャ縮小提案には「適用」ボタンを出す。</summary>
        private void DrawSizeSuggestions(SizeEstimateResult est)
        {
            var suggestions = est.suggestions;
            int count = suggestions != null ? suggestions.Count : 0;
            _foldSizeSuggestions = EditorGUILayout.Foldout(_foldSizeSuggestions, "削減提案 (" + count + ")", true);
            if (_foldSizeSuggestions)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawSizeBulkActionRow(est);
                    if (count == 0)
                    {
                        EditorGUILayout.LabelField("なし", EditorStyles.miniLabel);
                    }
                    else
                    {
                        foreach (var suggestion in suggestions)
                        {
                            if (suggestion == null) continue;
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                EditorGUILayout.LabelField(
                                    "-" + suggestion.savingMB.ToString("F1") + "MB " + (suggestion.description ?? ""),
                                    _wrapLabel);
                                if (suggestion.texture != null && suggestion.recommendedMaxSize > 0)
                                {
                                    if (GUILayout.Button(new GUIContent("適用",
                                        "このテクスチャを縮小計画に登録します。元のテクスチャは変更せず、変換時に縮小コピーを生成して変換後のマテリアルから参照します"),
                                        GUILayout.Width(44f)))
                                    {
                                        ApplySizeSuggestion(suggestion);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (_showReDiagnoseNote)
            {
                EditorGUILayout.HelpBox("縮小計画を更新しました。最新の推定サイズを確認するには「再診断」を押してください。", MessageType.Info);
            }
        }

        /// <summary>
        /// 削減提案(テクスチャ縮小)を縮小計画(settings.textureSizePlan)へ登録する。
        /// 元テクスチャのインポート設定は変更しない(変換時に縮小コピーが生成され、変換後のマテリアルがそれを参照する)。
        /// </summary>
        private void ApplySizeSuggestion(SizeSuggestion suggestion)
        {
            if (suggestion == null || suggestion.texture == null || suggestion.recommendedMaxSize <= 0) return;
            if (_settings == null) _settings = new QuestConvertSettings();

            bool ok = EditorUtility.DisplayDialog("削減提案の適用",
                "テクスチャ「" + suggestion.texture.name + "」を縮小計画に登録します。\n" +
                "元のテクスチャは変更しません。変換時に縮小コピーを生成し、変換後のマテリアルがそれを参照します。\n" +
                "「縮小計画をクリア」でいつでも取り消せます。\n\n" +
                "・縮小後の最大サイズ: " + suggestion.recommendedMaxSize + "px\n" +
                "・圧縮形式(生成コピー側): " + _settings.androidFormat + "\n\n登録しますか?",
                "登録する", "キャンセル");
            if (!ok) return;

            if (QuestSizeEstimator.UpsertTextureSizePlan(_settings, suggestion.texture, suggestion.recommendedMaxSize))
            {
                _showReDiagnoseNote = true;  // 推定サイズが古くなったため再診断を促す
                SaveSettings();              // 計画の変更を保存(診断は古い扱いになる)
                Debug.Log("[RARA QuestConverter] テクスチャ「" + suggestion.texture.name + "」を縮小計画に登録しました(最大" +
                          suggestion.recommendedMaxSize + "px。変換時に縮小コピーを生成)。");
            }
            else
            {
                EditorUtility.DisplayDialog("削減提案の適用",
                    "テクスチャ「" + suggestion.texture.name + "」は縮小計画に登録されませんでした。\n" +
                    "(プロジェクト内のテクスチャアセットのみ登録できます。既に同じか、より小さい計画がある場合も変更されません)", "OK");
            }
        }

        // ================================================================
        // セクション2b-2: 削減提案の一括アクション
        // ================================================================

        /// <summary>
        /// 削減提案リストの上に出す一括アクション行(提案をすべて適用 / 10MB以下まで自動調整 / 縮小計画をクリア)。
        /// いずれも元テクスチャのインポート設定は変更せず、縮小計画(settings.textureSizePlan)を更新するだけ。
        /// 自動調整ボタンは推定サイズが上限超過の見込みのときだけ表示する。
        /// </summary>
        private void DrawSizeBulkActionRow(SizeEstimateResult est)
        {
            if (est == null) return;
            int applicableCount = CountApplicableSuggestions(est);
            bool showBudgetFit = est.overCap || est.estimatedDownloadMB > QuestLimits.HardDownloadSizeCapMB;
            int planCount = _settings != null && _settings.textureSizePlan != null ? _settings.textureSizePlan.Count : 0;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(applicableCount == 0))
                {
                    if (GUILayout.Button(new GUIContent(
                        "提案をすべて適用 (" + applicableCount + "件)",
                        "テクスチャ縮小の提案をまとめて縮小計画に登録します。元のテクスチャは変更せず、変換時に縮小コピーを生成します"),
                        GUILayout.Height(22f)))
                    {
                        ApplyAllSizeSuggestions(est, applicableCount);
                    }
                }
                if (showBudgetFit)
                {
                    using (new EditorGUI.DisabledScope(_avatar == null))
                    {
                        if (GUILayout.Button(new GUIContent(
                            "10MB以下まで自動調整",
                            "推定ダウンロードサイズが上限(" + QuestLimits.HardDownloadSizeCapMB +
                            "MB)以下に収まるよう、大きいテクスチャから順に縮小計画を作って登録します(元のテクスチャは変更しません)"),
                            GUILayout.Height(22f)))
                        {
                            RunBudgetFit();
                        }
                    }
                }
            }
            // 非破壊であることの注記と、縮小計画のクリアボタン
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "※適用しても元のテクスチャは変更しません。変換時に縮小コピーを生成し、変換後のマテリアルがそれを参照します" +
                    "(現在の縮小計画: " + planCount + " 件)。",
                    _miniWrapLabel);
                using (new EditorGUI.DisabledScope(planCount == 0))
                {
                    if (GUILayout.Button(new GUIContent("縮小計画をクリア",
                        "登録済みのテクスチャ縮小計画(" + planCount + "件)をすべて削除します(元のテクスチャは最初から変更されていません)"),
                        EditorStyles.miniButton, GUILayout.Width(110f)))
                    {
                        ClearTextureSizePlan(planCount);
                    }
                }
            }
        }

        /// <summary>縮小計画(settings.textureSizePlan)を確認ダイアログ付きで全消去する。</summary>
        private void ClearTextureSizePlan(int planCount)
        {
            if (_settings == null || _settings.textureSizePlan == null || _settings.textureSizePlan.Count == 0) return;

            bool ok = EditorUtility.DisplayDialog("縮小計画をクリア",
                "登録済みのテクスチャ縮小計画 " + planCount + " 件をすべて削除します。\n" +
                "元のテクスチャは変更されていないため、次回の変換で縮小コピーが生成されなくなるだけです。\n\nクリアしますか?",
                "クリアする", "キャンセル");
            if (!ok) return;

            _settings.textureSizePlan.Clear();
            _showReDiagnoseNote = true;  // 推定サイズが古くなったため再診断を促す
            SaveSettings();              // 計画の変更を保存(診断は古い扱いになる)
            Debug.Log("[RARA QuestConverter] テクスチャ縮小計画をクリアしました(" + planCount + "件)。");
        }

        /// <summary>一括適用の対象になる提案(テクスチャ縮小提案)の件数を数える。汎用アドバイス行は対象外。</summary>
        private static int CountApplicableSuggestions(SizeEstimateResult est)
        {
            if (est == null || est.suggestions == null) return 0;
            int count = 0;
            foreach (SizeSuggestion suggestion in est.suggestions)
            {
                if (suggestion != null && suggestion.texture != null && suggestion.recommendedMaxSize > 0) count++;
            }
            return count;
        }

        /// <summary>
        /// テクスチャ縮小の提案をまとめて縮小計画に登録する(確認ダイアログ → 即時反映)。
        /// 元テクスチャのインポート設定は変更しないため一瞬で終わる(進捗バーは不要)。
        /// </summary>
        private void ApplyAllSizeSuggestions(SizeEstimateResult est, int applicableCount)
        {
            if (est == null || applicableCount <= 0) return;
            if (_settings == null) _settings = new QuestConvertSettings();

            bool ok = EditorUtility.DisplayDialog("提案をすべて適用",
                "テクスチャ縮小の提案 " + applicableCount + " 件をまとめて縮小計画に登録します。\n" +
                "元のテクスチャは変更しません。変換時に縮小コピーを生成し、変換後のマテリアルがそれを参照します。\n" +
                "「縮小計画をクリア」でいつでも取り消せます。\n\n" +
                "・縮小後の最大サイズ: 各提案の推奨値\n" +
                "・圧縮形式(生成コピー側): " + _settings.androidFormat + "\n\n登録しますか?",
                "登録する", "キャンセル");
            if (!ok) return;

            int applied;
            try
            {
                applied = QuestSizeEstimator.ApplyAllTextureSuggestions(est, _settings);
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA QuestConverter] 削減提案の一括登録に失敗しました: " + ex);
                EditorUtility.DisplayDialog("提案をすべて適用", "縮小計画の登録中にエラーが発生しました:\n" + ex.Message, "OK");
                return;
            }

            if (applied > 0)
            {
                _showReDiagnoseNote = true;  // 推定サイズが古くなったため再診断を促す
                SaveSettings();              // 計画の変更を保存(診断は古い扱いになる)
            }
            Debug.Log("[RARA QuestConverter] 削減提案の一括適用: " + applied + " 件を縮小計画に登録しました。");
            EditorUtility.DisplayDialog("提案をすべて適用",
                applied + " 件のテクスチャを縮小計画に登録しました。\n" +
                "元のテクスチャは変更されません(変換時に縮小コピーを生成します)。\n" +
                "「再診断」を押すと計画反映後の推定サイズを確認できます。", "OK");
        }

        /// <summary>
        /// 推定ダウンロードサイズが10MB上限に収まるようテクスチャの縮小計画を作って登録する
        /// (縮小プラン作成 → 内容の確認ダイアログ → 縮小計画へ登録)。
        /// 元のテクスチャは変更しない。登録は一瞬で終わるため進捗バーはプラン計算中のみ表示する。
        /// 上限ちょうどを狙うと見積もり誤差で超えやすいため、5%のマージンを取った目標で計画する。
        /// </summary>
        private void RunBudgetFit()
        {
            if (_avatar == null) return;
            if (_settings == null) _settings = new QuestConvertSettings();

            float targetMB = QuestLimits.HardDownloadSizeCapMB * 0.95f;
            List<BudgetFitStep> plan;
            try
            {
                EditorUtility.DisplayProgressBar("10MB以下まで自動調整", "縮小プランを計算しています...", 0.2f);
                plan = QuestSizeEstimator.PlanBudgetFit(_avatar.gameObject, _settings, targetMB);
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA QuestConverter] 自動調整プランの作成に失敗しました: " + ex);
                EditorUtility.DisplayDialog("10MB以下まで自動調整", "縮小プランの作成中にエラーが発生しました:\n" + ex.Message, "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (plan == null || plan.Count == 0)
            {
                EditorUtility.DisplayDialog("10MB以下まで自動調整",
                    "これ以上自動で下げられるテクスチャがありません。\n" +
                    "「削減提案」のQuest除外や、AAOによるブレンドシェイプ・メッシュの削減を検討してください。", "OK");
                return;
            }

            // 確認ダイアログ: 縮小ステップは最大12件まで列挙し、超過分は件数のみ表示する
            const int dialogStepMax = 12;
            float totalSavingMB = 0f;
            var steps = new System.Text.StringBuilder();
            for (int i = 0; i < plan.Count; i++)
            {
                BudgetFitStep step = plan[i];
                if (step == null) continue;
                totalSavingMB += step.savingMB;
                if (i < dialogStepMax)
                {
                    steps.AppendLine("・" + (step.texture != null ? step.texture.name : "(不明)") + " " +
                                     step.fromSize + "px→" + step.toSize + "px (-" + step.savingMB.ToString("F1") + "MB)");
                }
            }
            if (plan.Count > dialogStepMax)
            {
                steps.AppendLine("・他 " + (plan.Count - dialogStepMax) + " 件");
            }

            bool ok = EditorUtility.DisplayDialog("10MB以下まで自動調整",
                "推定ダウンロードサイズが上限(" + QuestLimits.HardDownloadSizeCapMB + "MB)以下になるよう、" +
                "次の " + plan.Count + " 件のテクスチャを縮小計画に登録します:\n\n" +
                steps.ToString() +
                "\n合計削減見込み: 約" + totalSavingMB.ToString("F1") + "MB\n\n" +
                "元のテクスチャは変更しません。変換時に縮小コピーを生成し、変換後のマテリアルがそれを参照します。" +
                "「縮小計画をクリア」でいつでも取り消せます。\n\n登録しますか?",
                "登録する", "キャンセル");
            if (!ok) return;

            int applied;
            try
            {
                applied = QuestSizeEstimator.ApplyBudgetFit(plan, _settings);
            }
            catch (Exception ex)
            {
                Debug.LogError("[RARA QuestConverter] 自動調整の登録に失敗しました: " + ex);
                EditorUtility.DisplayDialog("10MB以下まで自動調整", "縮小計画の登録中にエラーが発生しました:\n" + ex.Message, "OK");
                return;
            }

            if (applied > 0)
            {
                _showReDiagnoseNote = true;  // 推定サイズが古くなったため再診断を促す
                SaveSettings();              // 計画の変更を保存(診断は古い扱いになる)
            }
            Debug.Log("[RARA QuestConverter] 10MB以下まで自動調整: " + applied + " 件を縮小計画に登録しました(削減見込み 約" +
                      totalSavingMB.ToString("F1") + "MB)。");
            EditorUtility.DisplayDialog("10MB以下まで自動調整",
                applied + " 件のテクスチャを縮小計画に登録しました(削減見込み 約" + totalSavingMB.ToString("F1") + "MB)。\n" +
                "元のテクスチャは変更されません(変換時に縮小コピーを生成します)。\n" +
                "「再診断」を押すと計画反映後の推定サイズを確認できます。", "OK");
        }

        private static string NormalizeRating(string rating)
        {
            return (rating ?? "").Replace(" ", "").ToLowerInvariant();
        }

        /// <summary>ランク文字列に対応する表示色(不明な文字列は無着色)。</summary>
        private static Color RatingColor(string rating)
        {
            switch (NormalizeRating(rating))
            {
                case "excellent": return new Color(0.35f, 0.9f, 0.5f);
                case "good": return new Color(0.6f, 0.9f, 0.35f);
                case "medium": return new Color(1f, 0.85f, 0.35f);
                case "poor": return new Color(1f, 0.6f, 0.25f);
                case "verypoor": return new Color(1f, 0.4f, 0.4f);
                default: return Color.white;
            }
        }

        // ================================================================
        // 設定の永続化 / スタイル
        // ================================================================
        private void LoadSettings()
        {
            _settings = new QuestConvertSettings();
            var json = EditorPrefs.GetString(SettingsPrefsKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                // 既定値の上に上書き(欠けているフィールドは既定値のまま。
                // materialOverrides / enableAtlas / atlasMaxSize 等の新フィールドも自動で復元される)
                JsonUtility.FromJsonOverwrite(json, _settings);

                // 移行対策: OptIn機能追加より前に保存された設定にはphysBoneSelectionModeキーが無く、
                // FromJsonOverwriteでは既定値(OptIn)のまま残る。この状態で変換するとkeepPathsが空のため
                // 全PhysBoneが削除され、旧来(残す指定だけ削除=KeepAll)を期待していた既存ユーザーの
                // 揺れが不意に全停止してしまう。キーが存在しない旧設定は従来動作(KeepAll)を維持する。
                if (json.IndexOf("physBoneSelectionMode", StringComparison.Ordinal) < 0)
                {
                    _settings.physBoneSelectionMode = PhysBoneSelectionMode.KeepAll;
                }

                // 移行対策: transparentHandling(透過マテリアルの既定処理)追加より前に保存された設定には
                // このキーが無く、FromJsonOverwriteでは既定値(Emulate)のまま残る。旧来は
                // hideTransparentMaterials(既定オン=非表示)で透過を扱っていたため、キーが無い旧設定は
                // 旧hideTransparentMaterials==false → 不透明変換(従来のスキップ相当)/ それ以外 → 再現
                // へ移行して既存ユーザーの体験を大きく変えないようにする。
                if (json.IndexOf("transparentHandling", StringComparison.Ordinal) < 0)
                {
                    _settings.transparentHandling = _settings.hideTransparentMaterials
                        ? TransparentHandling.Emulate
                        : TransparentHandling.Opaque;
                }

                // 移行対策: SkinnedMesh統合(mergeSkinnedMeshesMode)追加より前に保存された設定にはこのキーが
                // 無く、FromJsonOverwriteでは新規既定(MergeExceptFace)のまま残る。既存ユーザーの見た目・SMR
                // レイアウトを不意に変えないよう、キーが無い旧設定は None(統合しない)へ戻す。
                if (json.IndexOf("mergeSkinnedMeshesMode", StringComparison.Ordinal) < 0)
                {
                    _settings.mergeSkinnedMeshesMode = SkinnedMeshMergeMode.None;
                }

                // 移行対策: 「PC最適化のみ」モードは姉妹ツール『RARA PC軽量化ツール』へ移行した。
                // ConversionMode列挙・変換コードパスは後方互換のため残すが、UIからは選択できないため、
                // 旧設定でConsolidateOnlyが保存されていてもQuest変換へ戻す(コンバーターはQuest対応に専念)。
                if (_settings.conversionMode == ConversionMode.ConsolidateOnly)
                {
                    _settings.conversionMode = ConversionMode.QuestConvert;
                    Debug.Log("[RARA QuestConverter] PC最適化のみモードは『RARA PC軽量化ツール』へ移行しました(メニュー RARA > PC軽量化ツール)");
                }
            }
            catch (Exception)
            {
                // 壊れた設定は破棄して既定値に戻す
                _settings = new QuestConvertSettings();
            }
        }

        /// <summary>設定を保存する(ユーザー操作による変更として、既存の診断結果を「古い」扱いにする)。</summary>
        private void SaveSettings()
        {
            SaveSettings(true);
        }

        /// <summary>
        /// 設定を保存する。markDiagnosisStale=true かつ診断済みの場合、
        /// 診断結果を「古い」扱いにする(セクション2に注意表示+生成時に自動再診断)。
        /// </summary>
        private void SaveSettings(bool markDiagnosisStale)
        {
            if (_settings == null) return;
            if (markDiagnosisStale && _diagnostics != null)
            {
                _diagnosisStale = true;
            }
            try
            {
                EditorPrefs.SetString(SettingsPrefsKey, JsonUtility.ToJson(_settings));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA QuestConverter] 設定の保存に失敗しました: " + ex.Message);
            }
        }

        private void EnsureStyles()
        {
            if (_wrapLabel == null)
            {
                _wrapLabel = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
            }
            if (_miniWrapLabel == null)
            {
                _miniWrapLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = false };
            }
            if (_badgeLabel == null)
            {
                _badgeLabel = new GUIStyle(EditorStyles.miniBoldLabel) { richText = false };
            }
            if (_verdictLabel == null)
            {
                _verdictLabel = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, richText = false };
            }
            if (_titleLabel == null)
            {
                _titleLabel = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, richText = false };
            }
            if (_purposeLabel == null)
            {
                // セクション見出し直下の目的説明。折り返しつつ、本文より一段控えめに見せる。
                _purposeLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = false };
            }
            if (_foldoutHeader == null)
            {
                // セクション/導入foldoutの見出し。通常のfoldoutをボールドにして見出しらしくする。
                _foldoutHeader = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, richText = false };
            }
        }
    }
}
#endif
