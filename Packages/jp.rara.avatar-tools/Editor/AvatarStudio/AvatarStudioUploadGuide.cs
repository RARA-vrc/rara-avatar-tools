// RARA アバター軽量化・Quest/iOS対応ツール - アップロード手順ガイド(実装者C所有)
// 生成後の「実行」ステップに常設し、RunAll 成功直後にも目立つ形で出す、PC/Quest 両対応アップロードの手引き。
//
// 目的: 1つの Blueprint ID で PC(_Opt)と Quest(_Quest)を同一アバターの2プラットフォーム版として上げるための
//       正しい順序(Windowsで _Opt をアップロード → その ID を _Quest へ同期 → Androidで _Quest をアップロード)を、
//       状態表示と実行ボタン付きで案内する。
//
// 【検証済みの前提(VRCCore-Editor.dll の IL/リフレクションで確認)】
//   ・VRC.Core.PipelineManager.blueprintId は public+serialized の素の文字列。setter 検証は無い。
//   ・ID のコピーは AssignId ではなく直接代入(dst.blueprintId = src.blueprintId; Undo+SetDirty)で行う。
//     AssignId は元IDを無視して avtr_<Guid.NewGuid()> を無条件で振り直すため、コピー用途では使わない。
//   ・_Opt/_Quest 複製は GameObject 複製時に元の blueprintId を継承する。ここでは再コピー/再割当はしない。
//   ・宛先(_Quest)が「別の非空ID」を持つ場合、無言上書きは別アバターを指し得るため必ず確認ダイアログを出す。
//   ・どの ID が既に PC/Quest ビルドを持つかはオフラインでは判定不能(認証付きAPIが要る)。ここでは案内しない。
//   ・ビルドターゲット切替は EditorUserBuildSettings.activeBuildTarget が全て。SwitchActiveBuildTargetAsync は
//     切替完了前に戻るため、実際の切替完了(=activeBuildTargetが一致)後にアップロードする旨を注記する。
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace RARA.AvatarStudio
{
    /// <summary>PC/Quest 両対応アップロードの手順ガイド(実行ステップ末尾に常設・RunAll後にも表示)。</summary>
    internal static class AvatarStudioUploadGuide
    {
        // VRChat SDK コントロールパネルのメニューパス(SDK3)。
        private const string SdkControlPanelMenu = "VRChat SDK/Show Control Panel";

        /// <summary>
        /// アップロード手順ガイドを描画する。avatar は元アバター、lastRun は直近の実行結果(未実行なら null)。
        /// 複製(_Opt/_Quest)は lastRun があればそれを、無ければシーンから名前で探す(生成後いつでも使える)。
        /// </summary>
        internal static void Draw(VRCAvatarDescriptor avatar, AvatarStudioRunResult lastRun)
        {
            EditorGUILayout.Space(6f);
            if (!AvatarStudioUI.Fold("UploadGuide", "アップロード手順ガイド(PC/Quest 両対応)", true)) return;

            GameObject optGo = FindClone(avatar, lastRun != null ? lastRun.pcClone : null, "_Opt");
            GameObject questGo = FindClone(avatar, lastRun != null ? lastRun.questClone : null, "_Quest");
            string questPrefabPath = lastRun != null ? lastRun.questPrefabPath : null;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "1つの Blueprint ID で PC(_Opt)と Quest(_Quest)を同一アバターの両対応版として上げる手順です。",
                    AvatarStudioUI.MiniWrapLabel);

                EditorGUILayout.Space(2f);
                DrawStatusBlock(avatar, optGo, questGo);

                // [B] ビルドターゲットとアクティブなアバターが食い違うときの警告(手順の前に目立つ位置で出す)。
                DrawTargetMismatchWarning(avatar != null ? avatar.gameObject : null, optGo, questGo);

                EditorGUILayout.Space(4f);
                DrawSteps(avatar != null ? avatar.gameObject : null, optGo, questGo, questPrefabPath);
            }
        }

        // ==============================================================
        // 状態ブロック(ビルドターゲット / 各 Blueprint ID)
        // ==============================================================

        private static void DrawStatusBlock(VRCAvatarDescriptor avatar, GameObject optGo, GameObject questGo)
        {
            BuildTarget bt = EditorUserBuildSettings.activeBuildTarget;
            bool isWin = IsWindows(bt);
            bool isAndroid = bt == BuildTarget.Android;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("現在のビルドターゲット", GUILayout.Width(150f));
                Color prev = GUI.color;
                GUI.color = isWin ? AvatarStudioUI.UploadOkColor
                    : (isAndroid ? AvatarStudioUI.AccentColor : AvatarStudioUI.NoteYellowColor);
                string label = isWin ? "Windows(PC)" : (isAndroid ? "Android(Quest/iOS)" : bt.ToString());
                GUILayout.Label("● " + label, EditorStyles.boldLabel);
                GUI.color = prev;
                GUILayout.FlexibleSpace();
            }

            string srcId = GetBlueprintId(avatar != null ? avatar.gameObject : null);
            string optId = GetBlueprintId(optGo);
            string questId = GetBlueprintId(questGo);

            DrawIdRow("元アバター", avatar != null ? avatar.gameObject : null, srcId);
            DrawIdRow("PC版(_Opt)", optGo, optId);
            DrawIdRow("Quest版(_Quest)", questGo, questId);

            // _Opt/_Quest がそろっているときだけ、両対応の準備状況を色付きで示す。
            if (optGo != null && questGo != null)
            {
                bool bothSet = !string.IsNullOrEmpty(optId) && !string.IsNullOrEmpty(questId);
                Color prev = GUI.color;
                if (bothSet && string.Equals(optId, questId, StringComparison.Ordinal))
                {
                    GUI.color = AvatarStudioUI.UploadOkColor;
                    EditorGUILayout.LabelField("✔ _Opt と _Quest は同一IDです(両対応の準備OK)", EditorStyles.miniBoldLabel);
                }
                else if (bothSet)
                {
                    GUI.color = AvatarStudioUI.OverLimitColor;
                    EditorGUILayout.LabelField("● _Opt と _Quest のIDが異なります(下の②で同期してください)", EditorStyles.miniBoldLabel);
                }
                GUI.color = prev;
            }
        }

        /// <summary>「ラベル / ID(先頭8文字…、空なら未割当、未生成なら未生成)」の1行。</summary>
        private static void DrawIdRow(string label, GameObject go, string id)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(150f));
                Color prev = GUI.color;
                if (go == null)
                {
                    GUI.color = AvatarStudioUI.NoteYellowColor;
                    EditorGUILayout.LabelField("(未生成)", EditorStyles.miniLabel);
                }
                else if (string.IsNullOrEmpty(id))
                {
                    GUI.color = AvatarStudioUI.NoteYellowColor;
                    EditorGUILayout.LabelField("未割当", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(new GUIContent(ShortId(id), id), EditorStyles.miniLabel);
                }
                GUI.color = prev;
                GUILayout.FlexibleSpace();
            }
        }

        // ==============================================================
        // 4ステップの手引き(各ステップに可能な限り実行ボタンを添える)
        // ==============================================================

        private static void DrawSteps(GameObject originalGo, GameObject optGo, GameObject questGo, string questPrefabPath)
        {
            BuildTarget bt = EditorUserBuildSettings.activeBuildTarget;
            bool isWin = IsWindows(bt);
            bool isAndroid = bt == BuildTarget.Android;
            string optId = GetBlueprintId(optGo);
            string questId = GetBlueprintId(questGo);

            // ① Windowsへ切替 → SDKパネルで _Opt をアップロード
            StepHeader(1, "PC版(_Opt)を Windows でアップロード");
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                // [C] Multi-Platform Build & Publish 併用禁止の常設注意(本ツールは複製ごとに別々にアップロードする方式のため)。
                EditorGUILayout.HelpBox(
                    "SDKの「Multi-Platform Build & Publish」(複数プラットフォーム同時ビルド)は使わないでください。" +
                    "本ツールはPC版とQuest版を別々の複製でアップロードする方式のため、" +
                    "プラットフォームは1つずつ選び、手順①〜④の順に進めます。",
                    MessageType.Warning);

                using (new EditorGUI.DisabledScope(isWin))
                {
                    if (GUILayout.Button(new GUIContent("ビルドターゲット: Windows に切替",
                        "アクティブなビルドターゲットを StandaloneWindows64 へ切り替えます(全アセットの再インポートに時間がかかります)")))
                    {
                        SwitchTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
                    }
                }
                if (isWin)
                {
                    EditorGUILayout.LabelField("既に Windows です。", EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField(
                    "切替はテクスチャ等の再インポートを伴い時間がかかります。切替が完了してからアップロードしてください。",
                    AvatarStudioUI.MiniWrapLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("SDKパネルを開く", "VRChat SDK コントロールパネルを開きます"), GUILayout.Width(140f)))
                    {
                        OpenSdkPanel();
                    }
                    EditorGUILayout.LabelField("SDKパネルで _Opt をアップロードします。", AvatarStudioUI.MiniWrapLabel);
                }

                // [A] PC版(_Opt)だけをアクティブ化し、元アバターと _Quest を非アクティブにする(SDKパネルの誤選択防止)。
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(optGo == null))
                    {
                        if (GUILayout.Button(new GUIContent("_Optだけをアクティブ化",
                            "PC版(_Opt)だけをシーンでアクティブにし、元アバターと Quest版(_Quest)を非アクティブにします。" +
                            "SDKパネルはアクティブなシーン内アバターを列挙するため、誤って別のアバターを選ぶ事故を防げます。Undoで元に戻せます"),
                            GUILayout.Width(180f)))
                        {
                            ActivateOnly(optGo, originalGo, optGo, questGo);
                        }
                    }
                    EditorGUILayout.LabelField(BuildActiveStateLabel(originalGo, optGo, questGo), AvatarStudioUI.MiniWrapLabel);
                }

                if (optGo == null) DrawNotGeneratedNote("先に PC版(_Opt)を生成してください。");
            }

            // ② PC版のIDを Quest版へ同期
            StepHeader(2, "PC版のIDを Quest版へ同期");
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                bool clonesReady = optGo != null && questGo != null;
                bool optHasId = !string.IsNullOrEmpty(optId);
                using (new EditorGUI.DisabledScope(!(clonesReady && optHasId)))
                {
                    if (GUILayout.Button(new GUIContent("PC版のIDを Quest版へ同期",
                        "PC版(_Opt)の Blueprint ID を Quest版(_Quest)へコピーします(直接代入。別IDがある場合は確認します)")))
                    {
                        SyncId(optGo, questGo, questPrefabPath);
                    }
                }

                if (!clonesReady)
                {
                    DrawNotGeneratedNote("先に _Opt と _Quest の両方を生成してください。");
                }
                else if (!optHasId)
                {
                    EditorGUILayout.HelpBox(
                        "PC版(_Opt)をアップロードすると Blueprint ID が割り当てられます。その後にこのボタンを押してください。",
                        MessageType.Info);
                }
                else if (!string.IsNullOrEmpty(questId) && string.Equals(optId, questId, StringComparison.Ordinal))
                {
                    Color prev = GUI.color;
                    GUI.color = AvatarStudioUI.UploadOkColor;
                    EditorGUILayout.LabelField(
                        "既に同一IDです(元アバターがアップロード済みだった場合は、複製生成時から最初から同一になります)。",
                        AvatarStudioUI.MiniWrapLabel);
                    GUI.color = prev;
                }
            }

            // ③ Androidへ切替
            StepHeader(3, "ビルドターゲットを Android に切替");
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUI.DisabledScope(isAndroid))
                {
                    if (GUILayout.Button(new GUIContent("ビルドターゲット: Android に切替",
                        "アクティブなビルドターゲットを Android へ切り替えます(全アセットの再インポートに時間がかかります)")))
                    {
                        SwitchTarget(BuildTargetGroup.Android, BuildTarget.Android);
                    }
                }
                if (isAndroid)
                {
                    EditorGUILayout.LabelField("既に Android です。", EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField(
                    "切替はテクスチャ等の再インポートを伴い時間がかかります。切替が完了してからアップロードしてください。",
                    AvatarStudioUI.MiniWrapLabel);
            }

            // ④ SDKパネルで _Quest をアップロード
            StepHeader(4, "Quest版(_Quest)をアップロード");
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("SDKパネルを開く", "VRChat SDK コントロールパネルを開きます"), GUILayout.Width(140f)))
                    {
                        OpenSdkPanel();
                    }
                    EditorGUILayout.LabelField("SDKパネルで _Quest をアップロードします。", AvatarStudioUI.MiniWrapLabel);
                }

                // [A] Quest版(_Quest)だけをアクティブ化し、元アバターと _Opt を非アクティブにする(SDKパネルの誤選択防止)。
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(questGo == null))
                    {
                        if (GUILayout.Button(new GUIContent("_Questだけをアクティブ化",
                            "Quest版(_Quest)だけをシーンでアクティブにし、元アバターと PC版(_Opt)を非アクティブにします。" +
                            "SDKパネルはアクティブなシーン内アバターを列挙するため、PC用アバターを選んでMobile非対応シェーダーの警告が出る事故を防げます。Undoで元に戻せます"),
                            GUILayout.Width(180f)))
                        {
                            ActivateOnly(questGo, originalGo, optGo, questGo);
                        }
                    }
                    EditorGUILayout.LabelField(BuildActiveStateLabel(originalGo, optGo, questGo), AvatarStudioUI.MiniWrapLabel);
                }

                EditorGUILayout.LabelField(
                    "PC版と同じ Blueprint ID のままアップロードすると、1つのアバターの PC/Quest 両対応(フォールバック)になります。",
                    AvatarStudioUI.MiniWrapLabel);
                if (questGo == null) DrawNotGeneratedNote("先に Quest版(_Quest)を生成してください。");
            }
        }

        // ==============================================================
        // 操作の実体
        // ==============================================================

        /// <summary>アクティブなビルドターゲットを非同期切替する(既に一致していれば何もしない)。</summary>
        private static void SwitchTarget(BuildTargetGroup group, BuildTarget target)
        {
            if (EditorUserBuildSettings.activeBuildTarget == target) return;
            // 非同期切替は完了前に戻る。完了は activeBuildTarget が target に一致してから確認できる。
            EditorUserBuildSettings.SwitchActiveBuildTargetAsync(group, target);
        }

        /// <summary>VRChat SDK コントロールパネルを開く(メニューが無ければ案内する)。</summary>
        private static void OpenSdkPanel()
        {
            if (!EditorApplication.ExecuteMenuItem(SdkControlPanelMenu))
            {
                EditorUtility.DisplayDialog("SDKパネル",
                    "SDKコントロールパネルのメニューが見つかりませんでした。メニューバーの『VRChat SDK』から開いてください。", "OK");
            }
        }

        /// <summary>
        /// [A] 対象クローンだけをシーンでアクティブにし、関連する既知のアバター(元アバターと、同じ元の
        /// もう一方の複製 _Opt/_Quest)だけを非アクティブにする。無関係なアバターや非アバターには一切触れない。
        /// SDK Builder パネルはアクティブなシーン内アバターを列挙するため、他を非アクティブにすることで
        /// 誤ったアバター(PC用シェーダーのまま等)を選んでしまう事故を防ぐ。
        /// 切り替えた各 GameObject は Undo 登録するので、ユーザーは Ctrl+Z で元に戻せる。
        /// </summary>
        private static void ActivateOnly(GameObject target, GameObject originalGo, GameObject optGo, GameObject questGo)
        {
            if (target == null) return;

            // 触れてよいのは「関連する既知のアバター」= 元・_Opt・_Quest の3つだけ(無関係な対象は列挙しない)。
            GameObject[] related = { originalGo, optGo, questGo };

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("複製のアクティブ化を切り替え");
            foreach (GameObject go in related)
            {
                if (go == null) continue;
                bool wantActive = go == target;
                if (go.activeSelf == wantActive) continue; // 状態が変わるものだけ Undo 登録して切り替える。
                Undo.RecordObject(go, "複製のアクティブ化を切り替え");
                go.SetActive(wantActive);
            }
            Undo.CollapseUndoOperations(group); // 3つの切り替えを1回の Undo にまとめる。
        }

        /// <summary>
        /// [B] ビルドターゲットと各複製のアクティブ状態が食い違うときに警告する。
        /// Android/iOS なのに _Quest が非アクティブで元/_Opt がアクティブ → SDKパネルはアクティブな
        /// PC用アバターを拾い、Mobile非対応シェーダーの警告が大量に出てビルドできない(赤・重大)。
        /// Windows なのに _Opt が非アクティブで _Quest がアクティブ → PC用として正しく選べていない(情報・軽度)。
        /// </summary>
        private static void DrawTargetMismatchWarning(GameObject originalGo, GameObject optGo, GameObject questGo)
        {
            BuildTarget bt = EditorUserBuildSettings.activeBuildTarget;
            bool isMobile = bt == BuildTarget.Android || bt == BuildTarget.iOS;
            bool isWin = IsWindows(bt);

            bool questActive = IsActive(questGo);
            bool optActive = IsActive(optGo);
            bool origActive = IsActive(originalGo);

            if (isMobile && questGo != null && !questActive && (origActive || optActive))
            {
                EditorGUILayout.HelpBox(
                    "ビルドターゲットがAndroidですが、_Questがアクティブではありません。" +
                    "SDKパネルでPC用アバターを選んでいるとMobile非対応シェーダーの警告が大量に出てビルドできません。" +
                    "手順④の「_Questだけをアクティブ化」を押してください",
                    MessageType.Error);
            }
            else if (isWin && optGo != null && !optActive && questActive)
            {
                EditorGUILayout.HelpBox(
                    "ビルドターゲットがWindowsですが、_Optがアクティブではありません(_Questがアクティブです)。" +
                    "SDKパネルでQuest用アバターを選んでいるとPC版として正しくアップロードできません。" +
                    "手順①の「_Optだけをアクティブ化」を押してください。",
                    MessageType.Info);
            }
        }

        /// <summary>
        /// PC版(_Opt)の Blueprint ID を Quest版(_Quest)へ直接代入で同期する。
        /// 宛先が別の非空IDを持つ場合は確認ダイアログを出し、承諾時のみ上書きする(別アバターの誤上書き防止)。
        /// 保存済み _Quest プレファブがあれば、そちらへの反映も任意で行う。
        /// </summary>
        private static void SyncId(GameObject optGo, GameObject questGo, string questPrefabPath)
        {
            if (optGo == null || questGo == null) return;

            var src = optGo.GetComponent<VRC.Core.PipelineManager>();
            var dst = questGo.GetComponent<VRC.Core.PipelineManager>();
            if (src == null || string.IsNullOrEmpty(src.blueprintId))
            {
                EditorUtility.DisplayDialog("IDを同期",
                    "PC版(_Opt)に Blueprint ID がありません。先に PC版をアップロードして ID を割り当ててください。", "OK");
                return;
            }
            if (dst == null)
            {
                EditorUtility.DisplayDialog("IDを同期",
                    "Quest版(_Quest)に PipelineManager が見つかりませんでした。", "OK");
                return;
            }

            // 宛先が別の非空IDを持つときは無言上書きしない(次回アップロードで別アバターを上書きし得るため)。
            if (!string.IsNullOrEmpty(dst.blueprintId) &&
                !string.Equals(dst.blueprintId, src.blueprintId, StringComparison.Ordinal))
            {
                bool ok = EditorUtility.DisplayDialog("別のアバターIDを上書きします",
                    "Quest版(_Quest)には既に別の Blueprint ID が設定されています。\n\n" +
                    "現在: " + dst.blueprintId + "\n上書き後: " + src.blueprintId + "\n\n" +
                    "このまま上書きすると、_Quest はPC版(_Opt)と同じアバターのQuest版として扱われます。続けますか?",
                    "上書きする", "キャンセル");
                if (!ok) return;
            }

            Undo.RecordObject(dst, "Blueprint IDを同期");
            dst.blueprintId = src.blueprintId;
            EditorUtility.SetDirty(dst);
            // プレファブインスタンス上ではプロパティオーバーライドとして確定させる。
            if (PrefabUtility.IsPartOfPrefabInstance(dst))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(dst);
            }

            // 保存済み _Quest プレファブがあれば、そちらへの反映も任意で行う(次回再生成でも継承されるが即時反映用)。
            if (!string.IsNullOrEmpty(questPrefabPath) &&
                AssetDatabase.LoadAssetAtPath<GameObject>(questPrefabPath) != null)
            {
                bool updatePrefab = EditorUtility.DisplayDialog("保存済みプレファブも更新",
                    "保存済みの _Quest プレファブにも同じ Blueprint ID を反映しますか?\n" + questPrefabPath,
                    "更新する", "しない");
                if (updatePrefab) UpdatePrefabBlueprintId(questPrefabPath, src.blueprintId);
            }

            EditorUtility.DisplayDialog("IDを同期",
                "Quest版(_Quest)の Blueprint ID をPC版と同一にしました。\n" + src.blueprintId, "OK");
        }

        /// <summary>保存済みプレファブアセットの PipelineManager.blueprintId を書き換えて保存する。</summary>
        private static void UpdatePrefabBlueprintId(string prefabPath, string id)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return;
            try
            {
                var pm = root.GetComponent<VRC.Core.PipelineManager>();
                if (pm != null)
                {
                    pm.blueprintId = id;
                    EditorUtility.SetDirty(pm);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ==============================================================
        // ヘルパ
        // ==============================================================

        /// <summary>実行結果の複製があればそれを、無ければシーンから「{元名}{接尾辞}」のアバター複製を探す。</summary>
        private static GameObject FindClone(VRCAvatarDescriptor avatar, GameObject fromRun, string suffix)
        {
            if (fromRun != null) return fromRun;
            if (avatar == null) return null;

            string wanted = avatar.gameObject.name + suffix;
            Scene scene = avatar.gameObject.scene;
            if (!scene.IsValid()) return null;

            foreach (GameObject rootGo in scene.GetRootGameObjects())
            {
                if (rootGo == null) continue;
                foreach (VRCAvatarDescriptor d in rootGo.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                {
                    if (d == null) continue;
                    GameObject go = d.gameObject;
                    if (go == avatar.gameObject) continue;
                    string trimmed = StripDuplicateSuffix(go.name);
                    if (string.Equals(trimmed, wanted, StringComparison.Ordinal)) return go;
                }
            }
            return null;
        }

        /// <summary>Unityの重複サフィックス " (1)" 等を末尾から取り除く。</summary>
        private static string StripDuplicateSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
            string s = name;
            while (s.Length > 3 && s[s.Length - 1] == ')')
            {
                int open = s.LastIndexOf(" (", StringComparison.Ordinal);
                if (open < 0) break;
                string inner = s.Substring(open + 2, s.Length - (open + 2) - 1);
                if (inner.Length > 0 && int.TryParse(inner, out _)) s = s.Substring(0, open);
                else break;
            }
            return s;
        }

        private static string GetBlueprintId(GameObject go)
        {
            if (go == null) return null;
            var pm = go.GetComponent<VRC.Core.PipelineManager>();
            return pm != null ? pm.blueprintId : null;
        }

        /// <summary>シーン内で実際にアクティブか(親も含めた activeInHierarchy。SDKパネルが拾う判定に合わせる)。</summary>
        private static bool IsActive(GameObject go)
        {
            return go != null && go.activeInHierarchy;
        }

        /// <summary>元・_Opt・_Quest の現在のアクティブ状態を「現在: _Opt=… / _Quest=… / 元=…」の1行にする。</summary>
        private static string BuildActiveStateLabel(GameObject originalGo, GameObject optGo, GameObject questGo)
        {
            return "現在: _Opt=" + ActiveWord(optGo) + " / _Quest=" + ActiveWord(questGo) + " / 元=" + ActiveWord(originalGo);
        }

        /// <summary>アクティブ状態の日本語表記(未生成は「未生成」)。</summary>
        private static string ActiveWord(GameObject go)
        {
            if (go == null) return "未生成";
            return go.activeInHierarchy ? "アクティブ" : "非アクティブ";
        }

        /// <summary>Blueprint ID を先頭8文字…で短く表示する(空は呼び出し側で処理済み)。</summary>
        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id ?? string.Empty;
            return id.Length <= 8 ? id : id.Substring(0, 8) + "…";
        }

        private static bool IsWindows(BuildTarget bt)
        {
            return bt == BuildTarget.StandaloneWindows64 || bt == BuildTarget.StandaloneWindows;
        }

        private static void StepHeader(int n, string title)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(AvatarStudioUI.Circled(n) + " " + title, EditorStyles.boldLabel);
        }

        private static void DrawNotGeneratedNote(string message)
        {
            Color prev = GUI.color;
            GUI.color = AvatarStudioUI.NoteYellowColor;
            EditorGUILayout.LabelField(message, AvatarStudioUI.MiniWrapLabel);
            GUI.color = prev;
        }
    }
}
#endif
