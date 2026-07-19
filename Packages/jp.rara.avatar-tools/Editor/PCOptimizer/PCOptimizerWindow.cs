// RARA PC軽量化ツール - メインウィンドウ(IMGUI)
// PCアバターを「非破壊」で複製し、Windows(PC)基準のパフォーマンスランクを引き上げるためのツールの入口。
//   ・対象ユーザー: (b) Poor以上を目指すPCユーザー / (c) Goodを目指すPCユーザー
//     ((a) 雑にQuest対応したい人は「RARA/Quest対応コンバーター」へ誘導する)。
//   ・このツールはポリゴンを削らない。ポリゴン削減は外部の減面ツールで70,000以下(Good閾値)まで
//     済ませてから使う想定(はじめての方へ で案内)。
//   ・出力は元アバターを変更しない「{名前}_Opt」シーン複製 + 保存プレファブ(非破壊)。
// 画面は「0.常時サマリー → 1.はじめての方へ → 2.アバター選択+PC基準診断 → 3.目標ランク
//   → 4.テクスチャ → 5.アトラス → 6.トグル整理 → 7.PhysBone → 8.実行 → 9.ヘルプ」で構成する。
// セクション4〜9の描画は PCOptimizerWindowSections.cs(partialクラス)に分割している。
// 診断は Windows(PC)基準の AvatarPerformance.CalculatePerformanceStats、
// 実行は RARA.PCOptimizer.PCOptimizer.Optimize に委譲する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using RARA.QuestConverter; // ConversionReport / ToggleConsolidator / ComponentRemover などを再利用

namespace RARA.PCOptimizer
{
    /// <summary>PCアバターを非破壊で複製し、PC(Windows)基準のランクを引き上げるメインウィンドウ。</summary>
    public partial class PCOptimizerWindow : EditorWindow
    {
        /// <summary>変換設定の永続化に使うEditorPrefsキー。</summary>
        private const string SettingsPrefsKey = "RARA.PCOptimizer.Settings";

        // ---- 状態 ----
        [SerializeField]
        private VRCAvatarDescriptor _avatar;                       // 対象アバター(シーン上のみ。スクリプトリロード後も選択を保持)
        private PCOptimizeSettings _settings = new PCOptimizeSettings();
        private PCDiagResult _diagnostics;                          // 直近のPC基準診断(未診断ならnull)
        private bool _diagnosisStale;                              // 診断後に設定が変更された(再診断が必要)
        private bool _autoDiagnoseQueued;                          // アバター設定時の自動診断の再入ガード
        private bool _isDiagnosing;                                // RunDiagnosticsの再入ガード

        // 古い複製(古いレイアウトのアトラス生成物)のプリフライト検出。重い走査を避けるため結果をキャッシュしスロットルする。
        private bool _staleCloneDetected;
        private int _staleCloneAvatarId;
        private double _staleCloneCheckTime;

        // セクション4: テクスチャ削減提案(診断時に更新。null=未計算)
        private List<PCTexturePlanner.PCTextureSuggestion> _textureSuggestions;
        private bool _textureSuggestionsQueued;
        private bool _textureSuggestionsFailed;

        // セクション5: アバターが使用するマテリアル一覧(アトラス除外指定用。診断時に更新)
        private List<Material> _avatarMaterials;
        private readonly Dictionary<Material, string> _materialGuidCache = new Dictionary<Material, string>();

        // セクション5: アトラス統合プレビュー(PreviewPlan。null=未計算。アバター/設定変更で無効化)
        private PCMaterialAtlasser.AtlasPlan _atlasPlan;
        private bool _atlasPlanQueued;
        private bool _atlasPlanFailed;

        // セクション6: 衣装・トグルグループ(ToggleConsolidator.DetectToggleGroups。未検出ならnull)
        private List<ToggleGroup> _toggleGroups;
        private bool _toggleGroupsQueued;
        private bool _toggleGroupsFailed;

        // [1.5.1] EditorOnly(ビルド除外)を一覧から隠すための共有判定キャッシュと非表示件数(PCはQuest除外なし=EditorOnlyのみ)。
        private readonly BuildExclusionCache _buildExclusion = new BuildExclusionCache();
        private int _toggleGroupsHiddenExcluded;                    // トグル整理: ビルド除外で非表示にしたトグルグループ数(検出時に算出)

        // セクション7: PhysBoneマージ・削除のドライランプレビュー(未計算ならnull)
        private PhysBonePreview _physBonePreview;
        private bool _physBonePreviewQueued;
        private bool _physBonePreviewFailed;
        private readonly HashSet<string> _physBoneExpandedGroups = new HashSet<string>();

        // セクション8: 実行結果
        private ConversionReport _lastReport;                     // 直近の最適化レポート(未実行ならnull)
        private GameObject _optimizedAvatar;                      // 直近に生成された _Opt 複製
        private PCDiagResult _beforeDiagnostics;                  // 実行時点の変換前診断(before/after表用)
        private PCDiagResult _afterDiagnostics;                   // 生成物を再診断した変換後(before/after表用)

        // ---- UI状態 ----
        private Vector2 _mainScroll;
        private Vector2 _reportScroll;
        private Vector2 _physBoneListScroll;
        private GUIStyle _wrapLabel;
        private GUIStyle _miniWrapLabel;
        private GUIStyle _verdictLabel;
        private GUIStyle _titleLabel;
        private GUIStyle _purposeLabel;
        private GUIStyle _foldoutHeader;

        // foldout開閉状態のEditorPrefsキャッシュ(毎OnGUIのEditorPrefsアクセスを避ける)
        private readonly Dictionary<string, bool> _foldPrefCache = new Dictionary<string, bool>();

        // ---- 導入・セクションfoldoutのEditorPrefsキー ----
        private const string IntroSeenPrefKey = "RARA.PCOptimizer.IntroSeen";
        private const string IntroFoldPrefKey = "RARA.PCOptimizer.Fold.Intro";
        private const string FoldKeyTexture = "RARA.PCOptimizer.Fold.Section.Texture";   // 4. テクスチャメモリ削減
        private const string FoldKeyAtlas = "RARA.PCOptimizer.Fold.Section.Atlas";       // 5. マテリアル統合(アトラス)
        private const string FoldKeyOutfit = "RARA.PCOptimizer.Fold.Section.Outfit";     // 6. メッシュ・トグル整理
        private const string FoldKeyPhysBone = "RARA.PCOptimizer.Fold.Section.PhysBone"; // 7. PhysBone整理
        private const string FoldKeyHelp = "RARA.PCOptimizer.Fold.Section.Help";         // 9. ヘルプ

        // 色(ダーク/ライト両スキンで読める中間トーン)
        private static readonly Color OverLimitColor = new Color(1f, 0.42f, 0.42f);
        private static readonly Color UploadOkColor = new Color(0.35f, 0.85f, 0.45f);
        private static readonly Color NoteYellowColor = new Color(1f, 0.85f, 0.35f);

        // 目標ランク選択トグルの表示名(PCTargetRankの並び順 Excellent, Good, Medium, Poor と一致させること)
        private static readonly GUIContent[] TargetRankLabels =
        {
            new GUIContent("Excellent", "最も軽いランク。三角数32,000以下など、最も厳しい目標です"),
            new GUIContent("Good(推奨)", "PCで軽量とされるランク。三角数70,000以下。本ツールが主に目指す目標です"),
            new GUIContent("Medium", "中程度のランク"),
            new GUIContent("Poor(最低ライン・Very Poor回避)", "重いランク。これを超えると Very Poor になり、既定の表示設定では他ユーザーに非表示になりやすくなります(PCではアップロード自体は可能)"),
        };

        // Phase 3: RARA メニューは統合ウィンドウの2項目のみへ整理。旧UIはメニューから外したが、
        // 互換のため Open() API は残す(統合ウィンドウ側や他コードから直接呼べる)。
        public static void Open()
        {
            var window = GetWindow<PCOptimizerWindow>();
            window.titleContent = new GUIContent("RARA PC軽量化");
            window.minSize = new Vector2(540f, 600f);
            window.Show();
        }

        /// <summary>他ツール(例: 外部)から対象アバターを指定して開くための入口。</summary>
        public static void Open(GameObject avatar)
        {
            var window = GetWindow<PCOptimizerWindow>();
            window.titleContent = new GUIContent("RARA PC軽量化");
            window.minSize = new Vector2(540f, 600f);
            if (avatar != null)
            {
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null && !EditorUtility.IsPersistent(descriptor))
                {
                    window.SetAvatar(descriptor);
                }
            }
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("RARA PC軽量化");
            LoadSettings();
            // スクリプトリロード後: _avatar はシリアライズで復元されるが、診断結果は復元されないため自動で再診断する
            if (_avatar != null && _diagnostics == null) QueueAutoDiagnosis();
        }

        private void OnDisable()
        {
            SaveSettings(false); // 閉じるときの保存では診断を古い扱いにしない(設定は変わっていない)
        }

        private void OnGUI()
        {
            EnsureStyles();
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            // 0. 常時表示の1行サマリー +「はじめての方へ」
            DrawIntroHeader();

            // 1〜2(背骨): アバター選択 → PC基準診断
            DrawAvatarSection();       // 1. アバター選択
            DrawDiagnosticsSection();  // 2. PC基準診断

            // 3. 目標ランク選択 + 自動提案
            DrawTargetRankSection();

            // 4〜7(詳細・上級。既定は畳む)
            DrawGroupDivider("軽量化の設定(必要に応じて開いてください)");
            DrawTextureSection();      // 4. テクスチャメモリ削減(Sections側)
            DrawAtlasSection();        // 5. マテリアル統合(アトラス)(Sections側)
            DrawOutfitToggleSection(); // 6. メッシュ・トグル整理(Sections側)
            DrawPhysBoneSection();     // 7. PhysBone整理(Sections側)

            // 8(背骨): 実行
            DrawGroupDivider("設定はここまで。非破壊で「_Opt」プレファブを生成します");
            DrawExecuteSection();      // 8. 実行(Sections側)

            DrawHelpSection();         // 9. ヘルプ(Sections側)
            EditorGUILayout.EndScrollView();
        }

        // ================================================================
        // 0. 導入ヘッダー(常時サマリー +「はじめての方へ」)
        // ================================================================

        /// <summary>ウィンドウ最上部に常時表示する1行サマリーと、「はじめての方へ」foldout(既定は畳む)を描画する。</summary>
        private void DrawIntroHeader()
        {
            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawTopSummaryLine();

                bool introSeen = EditorPrefs.GetBool(IntroSeenPrefKey, false);
                bool expanded = GetFoldPref(IntroFoldPrefKey, !introSeen); // 初回起動時だけ開いて表示する
                bool newExpanded = EditorGUILayout.Foldout(
                    expanded, "はじめての方へ(3つの目的と、使う前の準備)", true, _foldoutHeader);
                if (newExpanded != expanded) SetFoldPref(IntroFoldPrefKey, newExpanded);
                if (!introSeen) EditorPrefs.SetBool(IntroSeenPrefKey, true);

                if (newExpanded)
                {
                    EditorGUILayout.LabelField("RARA PC軽量化ツール", _titleLabel);
                    EditorGUILayout.LabelField(
                        "PCアバターの複製を作り、PC(Windows)基準のパフォーマンスランクを引き上げるツールです。" +
                        "元のアバターは変更しません。上から順に進めれば完了します。",
                        _wrapLabel);

                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField("あなたはどのタイプ?", EditorStyles.miniBoldLabel);
                    EditorGUILayout.HelpBox(
                        "(a) 雑にQuest対応したい → このツールではなく「RARA/Quest対応ツール」を使ってください。\n" +
                        "(b) PCのランクをまず Poor 以上にしたい → このツールの対象です。\n" +
                        "(c) PCで Good を目指したい → このツールの対象です。目標ランクで Good を選んでください。",
                        MessageType.Info);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("Quest対応コンバーターを開く",
                            "雑にQuest対応したい場合はこちら。RARA/Quest対応ツールを開きます"), GUILayout.Height(20f)))
                        {
                            OpenQuestConverter(null);
                        }
                    }

                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField("使う前の準備(重要)", EditorStyles.miniBoldLabel);
                    EditorGUILayout.HelpBox(
                        "このツールはポリゴン(三角数)を削りません。\n" +
                        "ポリゴンが多い場合は、先に外部の減面ツール(メッシュ簡略化)で 70,000 以下" +
                        "(PCの Good 閾値)まで減らしてから、本ツールで残りの雑な軽量化を行ってください。\n" +
                        "PCの三角数は Good/Medium/Poor がいずれも 70,000 で、70,000 を超えると一気に Very Poor になります。",
                        MessageType.Warning);

                    EditorGUILayout.LabelField(
                        "生成物は非破壊です。元アバターはそのまま残り、新しく「{名前}_Opt」のシーン複製と、" +
                        "同名のプレファブアセットが出力されます。",
                        _miniWrapLabel);
                }
            }
        }

        /// <summary>常時表示の1行サマリー(対象アバター / PC総合ランク / テクスチャメモリMB)。</summary>
        private void DrawTopSummaryLine()
        {
            var defaultColor = GUI.color;
            using (new EditorGUILayout.HorizontalScope())
            {
                string avatarName = _avatar != null ? _avatar.gameObject.name : "未選択";
                EditorGUILayout.LabelField(new GUIContent("アバター: " + avatarName, "現在の対象アバター"), EditorStyles.boldLabel);

                if (_diagnostics == null)
                {
                    EditorGUILayout.LabelField("PC総合: 未診断", EditorStyles.miniLabel, GUILayout.Width(150f));
                    EditorGUILayout.LabelField("テクスチャ: -", EditorStyles.miniLabel, GUILayout.Width(150f));
                }
                else
                {
                    string rank = string.IsNullOrEmpty(_diagnostics.overallRating) ? "不明" : _diagnostics.overallRating;
                    GUI.color = RatingColor(rank);
                    EditorGUILayout.LabelField("PC総合: " + DisplayRating(rank), EditorStyles.miniBoldLabel, GUILayout.Width(150f));
                    GUI.color = defaultColor;

                    float texLimit = PCRankLimits.GetLimit(_settings.targetRank, PCRankLimits.PCStat.TextureMemoryMB);
                    GUI.color = _diagnostics.textureMemoryMB > texLimit ? OverLimitColor : defaultColor;
                    EditorGUILayout.LabelField(
                        "テクスチャ: " + _diagnostics.textureMemoryMB.ToString("F1") + " MB",
                        EditorStyles.miniLabel, GUILayout.Width(150f));
                    GUI.color = defaultColor;
                }
            }
        }

        // ================================================================
        // 共通見出し・foldoutヘルパー(QuestConverterのUXを踏襲)
        // ================================================================

        /// <summary>常時表示セクションの統一見出し(番号+タイトル + 目的の折り返し説明)。</summary>
        private void DrawSectionHeader(int number, string title, string purpose)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(number + ". " + title, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(purpose)) EditorGUILayout.LabelField(purpose, _purposeLabel);
        }

        /// <summary>詳細・上級セクションの統一foldout見出し(開閉状態はEditorPrefsに保存。既定は畳む)。</summary>
        private bool DrawSectionFoldout(int number, string title, string purpose, string prefKey)
        {
            EditorGUILayout.Space(6f);
            bool expanded = GetFoldPref(prefKey, false);
            bool newExpanded = EditorGUILayout.Foldout(expanded, number + ". " + title, true, _foldoutHeader);
            if (newExpanded != expanded) SetFoldPref(prefKey, newExpanded);
            if (!string.IsNullOrEmpty(purpose)) EditorGUILayout.LabelField(purpose, _purposeLabel);
            return newExpanded;
        }

        /// <summary>セクション内の長い説明HelpBoxを畳むための「説明」foldout(開閉状態はEditorPrefsに保存)。</summary>
        private bool DrawExplainFoldout(string prefKey)
        {
            bool expanded = GetFoldPref(prefKey, false);
            bool newExpanded = EditorGUILayout.Foldout(expanded, "説明", true);
            if (newExpanded != expanded) SetFoldPref(prefKey, newExpanded);
            return newExpanded;
        }

        /// <summary>グループ境界を示す控えめな見出し行。</summary>
        private void DrawGroupDivider(string label)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
        }

        private bool GetFoldPref(string key, bool defaultValue)
        {
            if (_foldPrefCache.TryGetValue(key, out bool cached)) return cached;
            bool value = EditorPrefs.GetBool(key, defaultValue);
            _foldPrefCache[key] = value;
            return value;
        }

        private void SetFoldPref(string key, bool value)
        {
            _foldPrefCache[key] = value;
            EditorPrefs.SetBool(key, value);
        }

        // ================================================================
        // 1. アバター選択
        // ================================================================
        private void DrawAvatarSection()
        {
            UpdateStaleCloneFlag();
            DrawSectionHeader(1, "アバター選択",
                "軽量化するアバター(シーン上のVRCAvatarDescriptor)を指定します。指定すると自動でPC基準の診断を行います。");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var picked = EditorGUILayout.ObjectField(
                        new GUIContent("アバター", "軽量化元のVRCAvatarDescriptor(シーン上のオブジェクトのみ)"),
                        _avatar, typeof(VRCAvatarDescriptor), true) as VRCAvatarDescriptor;
                    if (picked != _avatar)
                    {
                        if (picked != null && EditorUtility.IsPersistent(picked))
                        {
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
                    EditorGUILayout.HelpBox("軽量化するアバター(VRCAvatarDescriptor)をシーンから指定してください。指定すると自動でPC基準の診断が実行されます。", MessageType.Info);
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
            if (_avatar == null) { _staleCloneDetected = false; return; }
            int id = _avatar.GetInstanceID();
            double now = EditorApplication.timeSinceStartup;
            if (id == _staleCloneAvatarId && now - _staleCloneCheckTime < 2.0) return;
            _staleCloneAvatarId = id;
            _staleCloneCheckTime = now;
            try
            {
                string safe = QuestConverterUtility.SanitizeAssetName(_avatar.gameObject.name);
                string meshesFolder = "Assets/RARA/PCOptimizer/Generated/" + safe + "/Meshes";
                _staleCloneDetected = MaterialAtlasser.SceneHasStaleAtlasClone(
                    _avatar.gameObject, _avatar.gameObject.name + "_Opt", meshesFolder);
            }
            catch
            {
                _staleCloneDetected = false;
            }
        }

        /// <summary>アバターを設定する(別アバターに変わったら診断・各プレビューをクリアして自動診断を予約)。</summary>
        private void SetAvatar(VRCAvatarDescriptor avatar)
        {
            if (_avatar == avatar) return;
            _avatar = avatar;
            _diagnostics = null;
            _textureSuggestions = null;
            _textureSuggestionsFailed = false;
            _avatarMaterials = null;
            _materialGuidCache.Clear();
            _atlasPlan = null;
            _atlasPlanFailed = false;
            _toggleGroups = null;
            _toggleGroupsFailed = false;
            _physBonePreview = null;
            _physBonePreviewFailed = false;
            _physBoneExpandedGroups.Clear();
            _diagnosisStale = false;
            QueueAutoDiagnosis();
        }

        /// <summary>アバター設定直後の自動診断を1回だけ予約する(OnGUI中の重い処理を避けてdelayCallで実行)。</summary>
        private void QueueAutoDiagnosis()
        {
            if (_avatar == null || _autoDiagnoseQueued) return;
            _autoDiagnoseQueued = true;
            EditorApplication.delayCall += () =>
            {
                _autoDiagnoseQueued = false;
                if (this == null) return;
                if (_avatar == null) return;
                RunDiagnostics();
                Repaint();
            };
        }

        /// <summary>シーン内のアバターを検出して選択させる(1体なら即設定、複数ならメニュー表示)。</summary>
        private void DetectAvatarsInScene()
        {
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
            var menu = new GenericMenu();
            foreach (var candidate in avatars)
            {
                if (candidate == null) continue;
                var local = candidate;
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
        // 2. PC基準診断
        // ================================================================
        private void DrawDiagnosticsSection()
        {
            DrawSectionHeader(2, "PC基準診断",
                "Windows(PC)基準で各項目を計測し、現在値 / Excellent・Good・Medium・Poor の閾値 / 現在ランクを表示します。");
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
                            "現在のアバターをWindows(PC)のパフォーマンス基準で診断します"), GUILayout.Height(24f)))
                        {
                            RunDiagnostics();
                        }
                    }
                    return;
                }

                DrawOverallRatingLine();

                if (_diagnosisStale)
                {
                    EditorGUILayout.HelpBox(
                        "設定が変更されたため、この診断結果は最新でない可能性があります。「再診断」を押すか、そのまま生成すると自動で再診断されます。",
                        MessageType.Warning);
                }

                DrawPerfTable(_diagnostics, true);
            }
        }

        /// <summary>PC総合ランクを色付きで表示し、再診断ボタンを並べる。</summary>
        private void DrawOverallRatingLine()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string rank = string.IsNullOrEmpty(_diagnostics.overallRating) ? "不明" : _diagnostics.overallRating;
                var prev = GUI.color;
                GUI.color = RatingColor(rank);
                EditorGUILayout.LabelField("PC総合ランク: " + DisplayRating(rank), _verdictLabel, GUILayout.Height(20f));
                GUI.color = prev;
                if (GUILayout.Button(new GUIContent("再診断", "現在の設定で診断をやり直します"), GUILayout.Width(70f)))
                {
                    RunDiagnostics();
                }
            }

            // Very Poor はPCではアップロード自体は可能である旨を明示(Questと違い全停止はしない)
            if (NormalizeRating(_diagnostics.overallRating) == "verypoor")
            {
                EditorGUILayout.HelpBox(
                    "Very Poor です。PCではアップロードは可能ですが、相手の表示設定(最低表示ランク)によっては" +
                    "非表示になることがあります。目標ランクまで軽量化することをおすすめします。",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// パフォーマンス統計テーブル。列: 項目 / 現在値 / Excellent / Good / Medium / Poor / ランク。
        /// showTargetHighlight=true のとき、選択中の目標ランクを超えている項目を赤く強調する。
        /// </summary>
        private void DrawPerfTable(PCDiagResult diag, bool showTargetHighlight)
        {
            if (diag == null || diag.rows == null || diag.rows.Count == 0) return;
            var defaultColor = GUI.color;

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(150f));
                    EditorGUILayout.LabelField("現在", EditorStyles.miniBoldLabel, GUILayout.Width(64f));
                    EditorGUILayout.LabelField("Excel", EditorStyles.miniBoldLabel, GUILayout.Width(52f));
                    EditorGUILayout.LabelField("Good", EditorStyles.miniBoldLabel, GUILayout.Width(52f));
                    EditorGUILayout.LabelField("Med", EditorStyles.miniBoldLabel, GUILayout.Width(52f));
                    EditorGUILayout.LabelField("Poor", EditorStyles.miniBoldLabel, GUILayout.Width(52f));
                    EditorGUILayout.LabelField("ランク", EditorStyles.miniBoldLabel, GUILayout.Width(68f));
                }

                foreach (PCDiagRow row in diag.rows)
                {
                    if (row == null) continue;
                    bool overTarget = showTargetHighlight && row.hasValue &&
                                      row.value > PCRankLimits.GetLimit(_settings.targetRank, row.stat) + 0.001f;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUI.color = overTarget ? OverLimitColor : defaultColor;
                        EditorGUILayout.LabelField(new GUIContent(row.label, row.tooltip), GUILayout.MinWidth(150f));
                        EditorGUILayout.LabelField(row.valueText ?? "-", GUILayout.Width(64f));

                        GUI.color = defaultColor;
                        EditorGUILayout.LabelField(FormatLimit(row, PCTargetRank.Excellent), EditorStyles.miniLabel, GUILayout.Width(52f));
                        EditorGUILayout.LabelField(FormatLimit(row, PCTargetRank.Good), EditorStyles.miniLabel, GUILayout.Width(52f));
                        EditorGUILayout.LabelField(FormatLimit(row, PCTargetRank.Medium), EditorStyles.miniLabel, GUILayout.Width(52f));
                        EditorGUILayout.LabelField(FormatLimit(row, PCTargetRank.Poor), EditorStyles.miniLabel, GUILayout.Width(52f));

                        GUI.color = RatingColor(row.rating);
                        EditorGUILayout.LabelField(DisplayRating(row.rating), EditorStyles.boldLabel, GUILayout.Width(68f));
                        GUI.color = defaultColor;
                    }
                }
                GUI.color = defaultColor;

                EditorGUILayout.LabelField(
                    "※三角数(ポリゴン)は Good/Medium/Poor いずれも 70,000 が上限で、70,000 を超えると即 Very Poor になります(崖)。本ツールは三角数を削りません(削減はMeshia連携でビルド時)。",
                    _miniWrapLabel);
            }
        }

        /// <summary>閾値セルの表示文字列(PCRankLimitsから取得。テクスチャメモリはMB付き)。</summary>
        private static string FormatLimit(PCDiagRow row, PCTargetRank rank)
        {
            int limit = PCRankLimits.GetLimit(rank, row.stat);
            return row.isMB ? limit.ToString() : limit.ToString("N0");
        }

        // ================================================================
        // 診断の実行(Windows/PC基準)
        // ================================================================

        /// <summary>診断を実行する(_isDiagnosingで再入ガード)。成功時にテクスチャ提案・トグル・PhysBoneプレビューも更新する。</summary>
        private void RunDiagnostics()
        {
            if (_avatar == null) return;
            if (_isDiagnosing) return;
            if (_settings == null) _settings = new PCOptimizeSettings();

            _isDiagnosing = true;
            try
            {
                _diagnostics = ComputePCPerformance(_avatar.gameObject);
                RefreshTextureSuggestions();
                RefreshAvatarMaterials();
                RefreshToggleGroups();
                RefreshPhysBonePreview();
                _diagnosisStale = false;
            }
            catch (Exception ex)
            {
                _diagnostics = null;
                Debug.LogError("[RARA PCOptimizer] 診断中に例外が発生しました: " + ex);
                EditorUtility.DisplayDialog("PC基準診断", "診断中に例外が発生しました:\n" + ex.Message, "OK");
            }
            finally
            {
                _isDiagnosing = false;
            }
        }

        /// <summary>診断結果テーブルの1行(項目・値・ランク・閾値参照用のPCStat)。</summary>
        private sealed class PCDiagRow
        {
            public string label;                 // 表示名(日本語)
            public string tooltip;               // 補足
            public PCRankLimits.PCStat stat;     // 閾値参照用の統計種別
            public string valueText;             // 現在値の表示文字列
            public float value;                  // 数値(閾値比較用)
            public bool hasValue;                // 未計測(Nullable)ならfalse
            public bool isMB;                    // テクスチャメモリ(MB)行か
            public string rating;                // Windows基準の項目別ランク
        }

        /// <summary>PC基準診断の結果一式。</summary>
        private sealed class PCDiagResult
        {
            public string overallRating;                 // Windows基準の総合ランク
            public float textureMemoryMB;                // テクスチャメモリ(MB)
            public List<PCDiagRow> rows = new List<PCDiagRow>();
        }

        /// <summary>閾値参照用の統計種別と、SDKのカテゴリ・表示名の対応。</summary>
        private struct PCStatDef
        {
            public string label;
            public string tooltip;
            public PCRankLimits.PCStat stat;
            public AvatarPerformanceCategory category;
            public bool isMB;
            public PCStatDef(string label, string tooltip, PCRankLimits.PCStat stat, AvatarPerformanceCategory category, bool isMB = false)
            {
                this.label = label;
                this.tooltip = tooltip;
                this.stat = stat;
                this.category = category;
                this.isMB = isMB;
            }
        }

        /// <summary>診断テーブルに出す項目(PCRankLimits.PCStat で閾値が取れる12項目)。</summary>
        private static readonly PCStatDef[] StatDefs =
        {
            new PCStatDef("三角数(ポリゴン)", "メッシュの三角ポリゴン数。PCは Good/Medium/Poor いずれも70,000が上限で、70,000超で即Very Poor(本ツールでは削減不可。削減はMeshia連携でビルド時)", PCRankLimits.PCStat.Triangles, AvatarPerformanceCategory.PolyCount),
            new PCStatDef("スキンメッシュ数", "SkinnedMeshRendererの数。トグル整理・アトラス統合・AAO結合で削減", PCRankLimits.PCStat.SkinnedMeshes, AvatarPerformanceCategory.SkinnedMeshCount),
            new PCStatDef("メッシュレンダラー数", "MeshRendererの数", PCRankLimits.PCStat.MeshRenderers, AvatarPerformanceCategory.MeshCount),
            new PCStatDef("マテリアルスロット数", "全レンダラーのマテリアルスロット合計。アトラス統合・トグル整理で削減", PCRankLimits.PCStat.MaterialSlots, AvatarPerformanceCategory.MaterialCount),
            new PCStatDef("テクスチャメモリ(MB)", "テクスチャのVRAM使用量。テクスチャ縮小で削減", PCRankLimits.PCStat.TextureMemoryMB, AvatarPerformanceCategory.TextureMegabytes, true),
            new PCStatDef("ボーン数", "スキニングに使うボーン数。AAOのTrace and Optimizeで未使用分を自動削減", PCRankLimits.PCStat.Bones, AvatarPerformanceCategory.BoneCount),
            new PCStatDef("PhysBoneコンポーネント数", "VRCPhysBoneの数。マージ・削除で削減", PCRankLimits.PCStat.PhysBoneComponents, AvatarPerformanceCategory.PhysBoneComponentCount),
            new PCStatDef("PhysBone対象Transform数", "PhysBoneが揺らすTransformの合計", PCRankLimits.PCStat.PhysBoneTransforms, AvatarPerformanceCategory.PhysBoneTransformCount),
            new PCStatDef("PhysBoneコライダー数", "PhysBoneColliderの数", PCRankLimits.PCStat.PhysBoneColliders, AvatarPerformanceCategory.PhysBoneColliderCount),
            new PCStatDef("PhysBone衝突チェック数", "PhysBoneの衝突判定の総回数", PCRankLimits.PCStat.PhysBoneCollisionChecks, AvatarPerformanceCategory.PhysBoneCollisionCheckCount),
            new PCStatDef("コンタクト数", "VRCContact(非ローカル)の数", PCRankLimits.PCStat.Contacts, AvatarPerformanceCategory.ContactCount),
            new PCStatDef("コンストレイント数", "VRCConstraintの数", PCRankLimits.PCStat.Constraints, AvatarPerformanceCategory.ConstraintsCount),
        };

        /// <summary>
        /// Windows(PC)基準でパフォーマンス統計を算出して診断結果を作る。
        /// アップロード時に除去されるEditorOnlyサブツリーを取り除いた一時複製に対して計測する
        /// (アバター本体は変更しない)。
        /// </summary>
        private PCDiagResult ComputePCPerformance(GameObject avatarRoot)
        {
            var result = new PCDiagResult();
            if (avatarRoot == null) return result;

            GameObject temp = UnityEngine.Object.Instantiate(avatarRoot);
            temp.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                StripEditorOnly(temp);
                RefreshConstraintGroups(temp);

                // false => Windows(PC)基準のレベルセットで評価する
                var stats = new AvatarPerformanceStats(false);
                AvatarPerformance.CalculatePerformanceStats(avatarRoot.name, temp, stats, false);

                foreach (PCStatDef def in StatDefs)
                {
                    bool has = ReadStatValue(stats, def.stat, out float value);
                    PerformanceRating rating = stats.GetPerformanceRatingForCategory(def.category);
                    var row = new PCDiagRow
                    {
                        label = def.label,
                        tooltip = def.tooltip,
                        stat = def.stat,
                        isMB = def.isMB,
                        hasValue = has,
                        value = value,
                        valueText = has ? (def.isMB ? value.ToString("F1") + " MB" : value.ToString("N0")) : "-",
                        rating = rating.ToString(),
                    };
                    result.rows.Add(row);
                    if (def.stat == PCRankLimits.PCStat.TextureMemoryMB && has) result.textureMemoryMB = value;
                }

                PerformanceRating overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
                result.overallRating = overall.ToString();
            }
            finally
            {
                if (temp != null) UnityEngine.Object.DestroyImmediate(temp);
            }
            return result;
        }

        /// <summary>統計種別に対応するSDKフィールドを読む(Nullableは未計測扱いでfalse)。</summary>
        private static bool ReadStatValue(AvatarPerformanceStats stats, PCRankLimits.PCStat stat, out float value)
        {
            switch (stat)
            {
                case PCRankLimits.PCStat.Triangles: return FromInt(stats.polyCount, out value);
                case PCRankLimits.PCStat.SkinnedMeshes: return FromInt(stats.skinnedMeshCount, out value);
                case PCRankLimits.PCStat.MeshRenderers: return FromInt(stats.meshCount, out value);
                case PCRankLimits.PCStat.MaterialSlots: return FromInt(stats.materialCount, out value);
                case PCRankLimits.PCStat.TextureMemoryMB: return FromFloat(stats.textureMegabytes, out value);
                case PCRankLimits.PCStat.Bones: return FromInt(stats.boneCount, out value);
                case PCRankLimits.PCStat.PhysBoneComponents: return FromInt(stats.physBone?.componentCount, out value);
                case PCRankLimits.PCStat.PhysBoneTransforms: return FromInt(stats.physBone?.transformCount, out value);
                case PCRankLimits.PCStat.PhysBoneColliders: return FromInt(stats.physBone?.colliderCount, out value);
                case PCRankLimits.PCStat.PhysBoneCollisionChecks: return FromInt(stats.physBone?.collisionCheckCount, out value);
                case PCRankLimits.PCStat.Contacts: return FromInt(stats.contactCount, out value);
                case PCRankLimits.PCStat.Constraints: return FromInt(stats.constraintsCount, out value);
                default: value = 0f; return false;
            }
        }

        private static bool FromInt(int? source, out float value)
        {
            value = source.HasValue ? source.Value : 0f;
            return source.HasValue;
        }

        private static bool FromFloat(float? source, out float value)
        {
            value = source.HasValue ? source.Value : 0f;
            return source.HasValue;
        }

        /// <summary>EditorOnlyタグの付いたサブツリーを一時複製から取り除く(アップロード時の除去に合わせる)。</summary>
        private static void StripEditorOnly(GameObject root)
        {
            var toDelete = new List<GameObject>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.CompareTag(QuestCompat.EditorOnlyTag)) toDelete.Add(t.gameObject);
            }
            foreach (GameObject go in toDelete)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go); // 親を先に消した場合、子はUnityのnull比較でスキップされる
            }
        }

        /// <summary>
        /// VRC.Dynamics.VRCConstraintManager.Sdk_ManuallyRefreshGroups(VRCConstraintBase[]) をリフレクションで呼ぶ。
        /// 型は internal のため直接参照できない。見つからない場合は続行する(コンストレイント数がやや不正確になり得る)。
        /// </summary>
        private static void RefreshConstraintGroups(GameObject root)
        {
            var constraints = root.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true);
            if (constraints == null || constraints.Length == 0) return;

            var managerType = QuestCompat.FindType("VRC.Dynamics.VRCConstraintManager");
            var method = managerType != null
                ? managerType.GetMethod(
                    "Sdk_ManuallyRefreshGroups",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null,
                    new Type[] { typeof(VRC.Dynamics.VRCConstraintBase[]) },
                    null)
                : null;
            if (method != null)
            {
                try { method.Invoke(null, new object[] { constraints }); }
                catch (Exception ex) { Debug.LogWarning("[RARA PCOptimizer] コンストレイントグループの更新に失敗しました: " + ex.Message); }
            }
        }

        // ================================================================
        // 3. 目標ランク選択 + 自動提案
        // ================================================================
        private void DrawTargetRankSection()
        {
            DrawSectionHeader(3, "目標ランク",
                "どのランクを目指すか選びます(既定: Good)。超過している項目と、それを解消できるセクションを提案します。");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new PCOptimizeSettings();

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent("目標ランク", "このランク以下に収めたいPCパフォーマンス目標を選びます(既定: Good)"), GUILayout.Width(72f));
                    int index = Mathf.Clamp((int)_settings.targetRank, 0, TargetRankLabels.Length - 1);
                    int newIndex = GUILayout.Toolbar(index, TargetRankLabels);
                    if (newIndex != index)
                    {
                        _settings.targetRank = (PCTargetRank)newIndex;
                        SaveSettings(); // 目標は診断表示の強調にも影響する
                    }
                }

                if (_diagnostics == null)
                {
                    EditorGUILayout.LabelField("アバターを診断すると、目標に対する超過項目と提案が表示されます。", EditorStyles.miniLabel);
                    return;
                }

                DrawTargetSuggestions();
            }
        }

        /// <summary>目標ランクに対して超過している項目を列挙し、解消できるセクションを提案する。</summary>
        private void DrawTargetSuggestions()
        {
            var overRows = new List<PCDiagRow>();
            foreach (PCDiagRow row in _diagnostics.rows)
            {
                if (row == null || !row.hasValue) continue;
                if (row.value > PCRankLimits.GetLimit(_settings.targetRank, row.stat) + 0.001f) overRows.Add(row);
            }

            if (overRows.Count == 0)
            {
                // 追跡12項目は満たしていても、SDK総合ランクは動的ライト/オーディオ/アニメーター/パーティクル等
                // (この表では追跡していない項目)を含む全カテゴリの最悪値で決まる。総合ランクが目標より悪い場合は
                // 「そのまま生成できます」と断言せず注意喚起する(すぐ上の総合ランク表示との矛盾を避ける)。
                int overallIndex = RatingToIndex(_diagnostics.overallRating);
                if (overallIndex > (int)_settings.targetRank)
                {
                    var prevW = GUI.color;
                    GUI.color = OverLimitColor;
                    EditorGUILayout.HelpBox(
                        "追跡している項目は目標ランク(" + TargetRankName(_settings.targetRank) + ")の閾値を満たしていますが、"
                        + "総合ランクは " + DisplayRating(_diagnostics.overallRating) + " です。"
                        + "この表で追跡していない項目(動的ライト・オーディオ・アニメーター・パーティクル等)が総合ランクを下げている可能性があります。"
                        + "上の総合ランク表示を確認してください。",
                        MessageType.Warning);
                    GUI.color = prevW;
                    return;
                }

                var prev = GUI.color;
                GUI.color = UploadOkColor;
                EditorGUILayout.HelpBox(
                    "目標ランク(" + TargetRankName(_settings.targetRank) + ")の閾値をすべて満たしています。そのまま生成できます。",
                    MessageType.Info);
                GUI.color = prev;
                return;
            }

            EditorGUILayout.LabelField(
                "目標(" + TargetRankName(_settings.targetRank) + ")を超えている項目と、削減に使えるセクションです。",
                _miniWrapLabel);
            var defaultColor = GUI.color;
            foreach (PCDiagRow row in overRows)
            {
                GUI.color = OverLimitColor;
                int limit = PCRankLimits.GetLimit(_settings.targetRank, row.stat);
                string limitText = row.isMB ? limit + " MB" : limit.ToString("N0");
                EditorGUILayout.LabelField(
                    "・" + row.label + ": 現在 " + (row.valueText ?? "-") + " / 目標 " + limitText + " — " + SuggestionForStat(row.stat),
                    _wrapLabel);
            }
            GUI.color = defaultColor;
        }

        /// <summary>統計種別ごとの推奨アクション(どのセクションで解消するか)。</summary>
        private static string SuggestionForStat(PCRankLimits.PCStat stat)
        {
            switch (stat)
            {
                case PCRankLimits.PCStat.Triangles:
                    return "本ツールでは削減不可。外部の減面ツールで70,000以下にしてください";
                case PCRankLimits.PCStat.TextureMemoryMB:
                    return "§4 テクスチャメモリ削減";
                case PCRankLimits.PCStat.MaterialSlots:
                    return "§5 マテリアル統合(アトラス) または §6 トグル整理";
                case PCRankLimits.PCStat.SkinnedMeshes:
                case PCRankLimits.PCStat.MeshRenderers:
                    return "§6 メッシュ・トグル整理(常時表示 → AAO結合)";
                case PCRankLimits.PCStat.Bones:
                    return "AAOのTrace and Optimizeで未使用ボーンを自動削減(§8で有効化)";
                case PCRankLimits.PCStat.PhysBoneComponents:
                case PCRankLimits.PCStat.PhysBoneTransforms:
                case PCRankLimits.PCStat.PhysBoneColliders:
                case PCRankLimits.PCStat.PhysBoneCollisionChecks:
                    return "§7 PhysBone整理";
                case PCRankLimits.PCStat.Contacts:
                    return "不要なVRCContactを削除(受信は Local 化でランク改善)";
                case PCRankLimits.PCStat.Constraints:
                    return "不要なコンストレイントを削除・統合";
                default:
                    return "各セクションで削減してください";
            }
        }

        // ================================================================
        // 設定の永続化 / スタイル / 色
        // ================================================================
        private void LoadSettings()
        {
            _settings = new PCOptimizeSettings();
            // 設定はプロジェクト別キー(<see cref="RARA.AvatarStudio.AvatarStudioSettingsIO.ProjectScopedKey"/>)へ保存する。
            // EditorPrefs はマシン全体で共有されるため、非スコープキーには別プロジェクトの設定が入っている可能性が
            // あり、そのまま読むと別アバターのパスが混入する(実測)。プロジェクト別キーが無い初回のみ、旧・非スコープ
            // キーからスカラー設定だけを一度移行し(アバター固有のパス/GUID/計画は破棄)、以後はスコープキーのみ扱う。
            string scopedKey = RARA.AvatarStudio.AvatarStudioSettingsIO.ProjectScopedKey(SettingsPrefsKey);
            var json = EditorPrefs.GetString(scopedKey, "");
            bool fromLegacy = false;
            if (string.IsNullOrEmpty(json))
            {
                json = EditorPrefs.GetString(SettingsPrefsKey, ""); // 旧・非スコープキー
                fromLegacy = !string.IsNullOrEmpty(json);
            }
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                JsonUtility.FromJsonOverwrite(json, _settings);

                // 移行対策: SkinnedMesh統合(mergeSkinnedMeshesMode)追加より前に保存された設定にはこのキーが
                // 無く、FromJsonOverwriteでは新規既定(MergeExceptFace)のまま残る。既存ユーザーのSMRレイアウトを
                // 不意に変えないよう、キーが無い旧設定は None(統合しない)へ戻す(新規設定の既定は推奨=統合)。
                if (json.IndexOf("mergeSkinnedMeshesMode", StringComparison.Ordinal) < 0)
                {
                    _settings.mergeSkinnedMeshesMode = RARA.QuestConverter.SkinnedMeshMergeMode.None;
                }

                // 移行対策: bool atlasUnifyOutline → enum atlasOutlineUnifyMode。
                // 新キーが無い旧設定は、旧 bool を見てモードを決める(FromJsonOverwrite は旧 bool キーを無視するため)。
                //  ・"atlasUnifyOutline":true → アウトライン付きに統一(旧動作を保持。瞳・顔は自動回避)。
                //  ・それ以外(false / 無し)      → しない(既定)。
                if (json.IndexOf("atlasOutlineUnifyMode", StringComparison.Ordinal) < 0)
                {
                    bool legacyUnify = json.IndexOf("\"atlasUnifyOutline\":true", StringComparison.Ordinal) >= 0;
                    _settings.atlasOutlineUnifyMode = legacyUnify
                        ? OutlineUnifyMode.アウトライン付きに統一
                        : OutlineUnifyMode.しない;
                    if (legacyUnify)
                    {
                        Debug.Log("[RARA PCOptimizer] 旧設定のアウトライン統一(ON)を『アウトライン付きに統一』へ移行しました。瞳・顔系マテリアルは自動でアウトライン付与を回避します。");
                    }
                }

                if (fromLegacy)
                {
                    // 旧・非スコープキーからの移行はスカラーのみ引き継ぐ。アバター固有のパス/GUID/計画リストは
                    // 別プロジェクト/別アバターの混入源になるため破棄し、プロジェクト別キーへ保存し直す。
                    RARA.AvatarStudio.AvatarStudioSettingsIO.StripAvatarSpecificPaths(_settings);
                    SaveSettings(false); // 以後はプロジェクト別キーのみ(非スコープキーには二度と書かない)
                }
            }
            catch (Exception)
            {
                _settings = new PCOptimizeSettings(); // 壊れた設定は既定値へ
            }
        }

        /// <summary>設定を保存する(ユーザー操作として既存の診断を「古い」扱いにする)。</summary>
        private void SaveSettings()
        {
            SaveSettings(true);
        }

        /// <summary>設定を保存する。markDiagnosisStale=true かつ診断済みなら診断を「古い」扱いにする。</summary>
        private void SaveSettings(bool markDiagnosisStale)
        {
            if (_settings == null) return;
            if (markDiagnosisStale && _diagnostics != null) _diagnosisStale = true;
            try
            {
                // プロジェクト別キーへ保存する(非スコープキーには書かない=プロジェクト間の混入を防ぐ)。
                string scopedKey = RARA.AvatarStudio.AvatarStudioSettingsIO.ProjectScopedKey(SettingsPrefsKey);
                EditorPrefs.SetString(scopedKey, JsonUtility.ToJson(_settings));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA PCOptimizer] 設定の保存に失敗しました: " + ex.Message);
            }
        }

        private void EnsureStyles()
        {
            if (_wrapLabel == null) _wrapLabel = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
            if (_miniWrapLabel == null) _miniWrapLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = false };
            if (_verdictLabel == null) _verdictLabel = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, richText = false };
            if (_titleLabel == null) _titleLabel = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, richText = false };
            if (_purposeLabel == null) _purposeLabel = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = false };
            if (_foldoutHeader == null) _foldoutHeader = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, richText = false };
        }

        /// <summary>ランク文字列を正規化する(空白除去+小文字化)。</summary>
        private static string NormalizeRating(string rating)
        {
            return (rating ?? "").Replace(" ", "").ToLowerInvariant();
        }

        /// <summary>総合ランク文字列を 0(Excellent)…4(Very Poor) の添字へ変換する(PCTargetRankと同じ並び。不明は -1)。</summary>
        private static int RatingToIndex(string rating)
        {
            switch (NormalizeRating(rating))
            {
                case "excellent": return 0;
                case "good": return 1;
                case "medium": return 2;
                case "poor": return 3;
                case "verypoor": return 4;
                default: return -1;
            }
        }

        /// <summary>ランク文字列の日本語混じり表示("VeryPoor"→"Very Poor")。</summary>
        private static string DisplayRating(string rating)
        {
            switch (NormalizeRating(rating))
            {
                case "excellent": return "Excellent";
                case "good": return "Good";
                case "medium": return "Medium";
                case "poor": return "Poor";
                case "verypoor": return "Very Poor";
                default: return string.IsNullOrEmpty(rating) ? "不明" : rating;
            }
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

        /// <summary>目標ランクの日本語表示名。</summary>
        private static string TargetRankName(PCTargetRank rank)
        {
            switch (rank)
            {
                case PCTargetRank.Excellent: return "Excellent";
                case PCTargetRank.Good: return "Good";
                case PCTargetRank.Medium: return "Medium";
                case PCTargetRank.Poor: return "Poor";
                default: return rank.ToString();
            }
        }

        /// <summary>
        /// Quest対応フローを開く。Phase 2 で統合ウィンドウ(AvatarStudioWindow)へ移行したため、
        /// PC軽量化の結果(または対象アバター)をそのまま統合ウィンドウの Quest 対象で開く。
        /// </summary>
        private static void OpenQuestConverter(GameObject avatar)
        {
            RARA.AvatarStudio.AvatarStudioWindow.Open(avatar, false, true);
        }
    }
}
#endif
