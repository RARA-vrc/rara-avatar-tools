// RARA アバター軽量化・Quest/iOS対応ツール(統合ウィンドウ) - Quest固有パネル群(実装者B所有)
// 旧 QuestConverterWindowSections.cs(セクション6アトラス / 7メッシュ削減 / 8除外 / 9変換設定 /
// 表情デカール)の「操作パターン」を写経した Quest 専用パネル。旧ウィンドウクラス
// (QuestConverterWindow)の internal メンバーは一切参照せず、公開エンジン API のみを呼ぶ。
//
// 各 Draw は (AvatarStudioSettings s, GameObject avatar) を受け取り、設定を編集したら true を返す
// (呼び出し側=実装者D が SaveSettings する)。GUILayout はスコープ収支を各分岐で合わせる。
// 重い READ-ONLY プレビュー(PreviewMaterials / DetectShrinkShapes / PreviewExpressionDecals)は
// AvatarStudioPreviewPanels と同じ AvatarStudioPreviewCache(入力シグネチャ付き)でキャッシュする。
//
// 【契約(Implementer A のピン留めフィールド)】このファイルが読み書きする AvatarStudioSettings メンバー:
//   Quest固有: questExcludePaths, questEnableAtlas, questAtlasMaxSize, questAtlasUnifyRamps,
//              materialOverrides(excludeFromAtlas), shaderTarget,
//              questMaxTextureSize, questAndroidFormat, questGenerateShadowRamp, questBakeEmission,
//              questBakeShadowIntoMainTex, questMapRimLighting, questAggressiveTextureReduction,
//              questConvertAnimations, questRemoveUnsupportedComponents, questConvertUnityConstraints,
//              questTrimPhysBonesToPoorLimit, questOutputFolder,
//              questRemoveHiddenMeshByBlendShape, questHiddenMeshRendererPaths, ensureTraceAndOptimize
//   (QuestBuildPreprocessor.Enabled は EditorPrefs 直結のため設定JSONの changed には含めない。)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RARA.QuestConverter;
using VRC.SDK3.Avatars.Components;

namespace RARA.AvatarStudio
{
    /// <summary>統合ウィンドウの Quest 固有パネル群(静的)。各 Draw は変更があれば true を返す。</summary>
    public static class AvatarStudioQuestPanels
    {
        private const int MaxRows = 60;

        /// <summary>重い READ-ONLY プレビュー(PreviewMaterials/DetectShrinkShapes/PreviewExpressionDecals)の
        /// 入力シグネチャ付きキャッシュ。パネルはウィンドウの cache を受け取らないため専用に1つ持つ
        /// (キーにアバターInstanceID+関連設定を含めるので、アバター切替・設定変更で自然に無効化される)。</summary>
        private static readonly AvatarStudioPreviewCache _cache = new AvatarStudioPreviewCache();

        /// <summary>除外パス追加時の検証エラー(直近1件)。ドロップ検証に失敗したときだけ表示する。</summary>
        private static string _excludeAddError;

        // Quest テクスチャサイズ(ベイク生成の最大サイズ)ポップアップ。QuestConverterWindow と同一の選択肢。
        private static readonly GUIContent[] TextureSizeLabels =
        {
            new GUIContent("512"),
            new GUIContent("1024(推奨)"),
            new GUIContent("2048"),
        };
        private static readonly int[] TextureSizeValues = { 512, 1024, 2048 };

        // アトラス最大サイズ。契約により 1024/2048/4096 を提示する。
        private static readonly GUIContent[] AtlasSizeLabels =
        {
            new GUIContent("1024"),
            new GUIContent("2048(推奨)"),
            new GUIContent("4096"),
        };
        private static readonly int[] AtlasSizeValues = { 1024, 2048, 4096 };

        // Android圧縮形式(ASTCのみ)。QuestConverterWindow と同一の選択肢。
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

        // ================================================================
        // 1. Quest除外オブジェクト(questExcludePaths)
        //    旧セクション8: ドラッグ&ドロップ追加(アバター配下検証)/ 解決チェック /
        //    ピン(EditorGUIUtility.PingObject)/ 削除 / ボーン誤登録の注意書き。
        // ================================================================
        public static bool DrawQuestExcludePanel(AvatarStudioSettings s, GameObject avatar)
        {
            if (s == null) { EditorGUILayout.HelpBox("設定が初期化されていません。", MessageType.Info); return false; }
            EnsureList(ref s.questExcludePaths);

            bool changed = false;

            // 検証エラー表示の有無を「このOnGUIパスの開始時点の値」で固定する。下の ObjectField への
            // ドロップ(DragPerform イベント)は AddExcludePath を呼んで _excludeAddError を null↔非null に
            // 変えるが、その新しい値でこの下の HelpBox の有無を決めると、同一パスの Layout が確保した
            // コントロール数と食い違い「control N in a group with only M controls」例外になる。
            // 捕捉値で描画すれば、失敗ドロップで生じたエラーは(ドロップで走る再描画の)次パスから表示される。
            string pendingExcludeError = _excludeAddError;

            if (AvatarStudioUI.ExplainFold("QuestExclude", "説明(Quest除外の効果とボーン誤登録の注意)"))
            {
                EditorGUILayout.HelpBox(
                    "ここに登録したオブジェクトは、Quest/iOS版の複製でのみ EditorOnly タグ + 非アクティブになり、" +
                    "ビルドから完全に除外されます(PC版には影響しません)。\n" +
                    "アバタールートや、スキンメッシュが参照するボーンは登録しないでください" +
                    "(ボーンを除外するとメッシュが崩れます)。",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(avatar == null))
            {
                // 常に null で描画するドロップ用スロット。割り当てられた瞬間にパス登録し、スロットは空へ戻る。
                var dropped = EditorGUILayout.ObjectField(
                    new GUIContent("除外に追加", "選択中のアバター配下のオブジェクト(Transform)をドロップすると除外リストへ登録します"),
                    null, typeof(Transform), true) as Transform;
                if (dropped != null)
                {
                    if (AddExcludePath(s, avatar, dropped)) changed = true;
                }
            }
            if (avatar == null)
            {
                EditorGUILayout.LabelField("対象アバターを指定すると登録できます。", EditorStyles.miniLabel);
            }
            if (!string.IsNullOrEmpty(pendingExcludeError))
            {
                EditorGUILayout.HelpBox(pendingExcludeError, MessageType.Error);
            }

            changed |= DrawExcludePathList(s, avatar);
            return changed;
        }

        /// <summary>登録済み除外パスの一覧(解決チェック・ピン・削除)。変更があれば true。</summary>
        private static bool DrawExcludePathList(AvatarStudioSettings s, GameObject avatar)
        {
            List<string> paths = s.questExcludePaths;
            if (paths.Count == 0)
            {
                EditorGUILayout.LabelField("登録された除外オブジェクトはありません。", EditorStyles.miniLabel);
                return false;
            }

            Color defaultColor = GUI.color;
            int removeIndex = -1;
            int shown = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                using (new EditorGUILayout.HorizontalScope())
                {
                    string path = paths[i] ?? "";
                    EditorGUILayout.LabelField(new GUIContent(Trunc(path, 60), path),
                        AvatarStudioUI.WrapLabel, GUILayout.MinWidth(120f), GUILayout.MaxWidth(360f));

                    // 選択中のアバター上でパスを解決(アバター未選択時は null)
                    Transform resolved = avatar != null ? QuestCompat.FindByPath(avatar.transform, path) : null;
                    if (avatar != null && resolved == null)
                    {
                        GUI.color = AvatarStudioUI.NoteYellowColor;
                        EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUI.color = defaultColor;
                    }
                    // [1.5.1] この登録パネルは全件表示する(唯一の非表示しない面)。既に EditorOnly タグが
                    // 付いているオブジェクト(登録前から手動でタグ付け済み)には「EditorOnly」バッジを付ける。
                    else if (resolved != null && QuestCompat.IsEditorOnly(resolved))
                    {
                        GUI.color = AvatarStudioUI.NoteYellowColor;
                        EditorGUILayout.LabelField(new GUIContent("EditorOnly", "このオブジェクトは既に EditorOnly タグが付いています(ビルドから除外されます)"),
                            EditorStyles.miniLabel, GUILayout.Width(72f));
                        GUI.color = defaultColor;
                    }
                    GUILayout.FlexibleSpace();

                    // ピン: シーン上の該当オブジェクトをハイライト(解決できた場合のみ)
                    using (new EditorGUI.DisabledScope(resolved == null))
                    {
                        if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当オブジェクトをハイライト表示します"), GUILayout.Width(36f)))
                        {
                            EditorGUIUtility.PingObject(resolved.gameObject);
                        }
                    }
                    if (GUILayout.Button(new GUIContent("削除", "この除外登録を解除します"), GUILayout.Width(44f)))
                    {
                        removeIndex = i;
                    }
                }
            }
            GUI.color = defaultColor;

            if (removeIndex >= 0)
            {
                paths.RemoveAt(removeIndex);
                return true;
            }
            return false;
        }

        /// <summary>
        /// ドロップされた Transform を相対パスとして除外リストへ登録する(重複無視)。変更があれば true。
        /// アバタールート・アバター配下でないもの・同名兄弟で別オブジェクトを指すパスは弾く(ボーン安全)。
        /// </summary>
        private static bool AddExcludePath(AvatarStudioSettings s, GameObject avatar, Transform target)
        {
            _excludeAddError = null;
            if (avatar == null || target == null) return false;

            Transform root = avatar.transform;
            if (target == root)
            {
                _excludeAddError = "アバタールート自体は除外に登録できません。";
                return false;
            }
            if (!target.IsChildOf(root))
            {
                _excludeAddError = "「" + target.name + "」は選択中のアバターの配下ではないため登録できません。";
                return false;
            }

            string path = QuestCompat.GetRelativePath(root, target);
            if (string.IsNullOrEmpty(path))
            {
                _excludeAddError = "相対パスを取得できませんでした: " + target.name;
                return false;
            }
            // 名前パスは同名の兄弟がいると最初の一致へ解決される(Transform.Find仕様)。
            // ドロップした本人へ解決し直せないパスは、意図しないオブジェクト(=ボーン等)を除外してしまうため拒否する。
            if (QuestCompat.FindByPath(root, path) != target)
            {
                _excludeAddError = "「" + target.name + "」の相対パス \"" + path + "\" は同名の別オブジェクトを指すため登録できません。" +
                    "同名の兄弟オブジェクトがある(または名前に \"/\" を含む)場合は、対象の名前を一意にしてから登録してください。";
                return false;
            }
            if (s.questExcludePaths.Contains(path)) return false; // 重複登録は無視(dedupe)

            s.questExcludePaths.Add(path);
            return true;
        }

        // ================================================================
        // 2. アトラス統合(questEnableAtlas / questAtlasMaxSize / questAtlasUnifyRamps
        //    + マテリアル個別のアトラス除外)。旧セクション6 + セクション3のアトラス行。
        // ================================================================
        public static bool DrawQuestAtlasPanel(AvatarStudioSettings s, GameObject avatar)
        {
            if (s == null) { EditorGUILayout.HelpBox("設定が初期化されていません。", MessageType.Info); return false; }
            EnsureList(ref s.materialOverrides);

            bool changed = false;

            // トグルで表示量(サブ設定+マテリアル一覧)が変わるため、値は描画前に捕捉する。
            // 同一フレーム内で Layout/イベントのコントロール数が食い違う(Mismatched LayoutGroup)のを防ぐ(反映は次回OnGUIから)。
            bool atlasEnabled = s.questEnableAtlas;

            bool enable = EditorGUILayout.ToggleLeft(
                new GUIContent("アトラス統合を有効化(実験的)",
                    "互換性のある変換後マテリアルを1枚のアトラステクスチャに統合し、マテリアルスロット数を削減します"),
                s.questEnableAtlas);
            if (enable != s.questEnableAtlas) { s.questEnableAtlas = enable; changed = true; }

            if (atlasEnabled)
            {
                int newMax = EditorGUILayout.IntPopup(
                    new GUIContent("アトラス最大サイズ", "統合後のアトラステクスチャの最大サイズ"),
                    s.questAtlasMaxSize, AtlasSizeLabels, AtlasSizeValues);
                if (newMax != s.questAtlasMaxSize) { s.questAtlasMaxSize = newMax; changed = true; }

                bool unify = EditorGUILayout.ToggleLeft(
                    new GUIContent("影ランプを統一してグループ化(スロット削減優先)",
                        "アトラス統合時、影ランプの違いを無視してグループ化する(1グループ=1代表ランプに統一)。" +
                        "影ランプが個別生成されるアバターでもスロットを大きく削減できる。影のトーンはグループ内で共通化される"),
                    s.questAtlasUnifyRamps);
                if (unify != s.questAtlasUnifyRamps) { s.questAtlasUnifyRamps = unify; changed = true; }
                EditorGUILayout.LabelField(
                    "影ランプが個別に生成されるアバターでは、これをオフにするとアトラスがほとんど効きません。" +
                    "オンにするとグループ内で影のトーンが共通化される代わりにスロットを大きく削減できます。",
                    AvatarStudioUI.MiniWrapLabel);
            }

            if (AvatarStudioUI.ExplainFold("QuestAtlas", "説明(アトラス統合の仕組みと除外)"))
            {
                EditorGUILayout.HelpBox(
                    "互換マテリアルを1枚に統合し、マテリアルスロット数を削減します。\n" +
                    "メッシュ結合は AAO(Trace and Optimize)がビルド時に実施します。\n" +
                    "アニメで差し替えるマテリアルと UV タイリング使用は自動で除外されます。\n" +
                    "下の一覧の「アトラス統合」で、マテリアルごとに対象へ含める/外すを個別指定できます。",
                    MessageType.Info);
            }

            // 有効時のみ、マテリアル個別のアトラス除外を出す(除外は materialOverrides.excludeFromAtlas に保存)。
            // 表示判定は描画前に捕捉した値を使う(上のトグルと同一フレームでコントロール数を変えないため)。
            if (atlasEnabled)
            {
                changed |= DrawAtlasMaterialList(s, avatar);
            }

            return changed;
        }

        /// <summary>
        /// マテリアルごとにアトラス統合の対象/除外を切り替える一覧(PreviewMaterials の atlasEligible /
        /// atlasIneligibleReason を利用)。除外は materialOverrides の excludeFromAtlas エントリに書く。
        /// </summary>
        private static bool DrawAtlasMaterialList(AvatarStudioSettings s, GameObject avatar)
        {
            if (avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、マテリアルごとのアトラス対象/除外を設定できます。", EditorStyles.miniLabel);
                return false;
            }
            VRCAvatarDescriptor descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                EditorGUILayout.LabelField("VRCAvatarDescriptor が見つかりません(アバター直下に必要です)。", EditorStyles.miniLabel);
                return false;
            }

            QuestConvertSettings quest = AvatarStudioMapping.BuildQuestConvertSettings(s);
            string sig = avatar.GetInstanceID() + "|" + (int)s.shaderTarget + "|" + (int)s.transparentHandling
                + "|" + s.questAtlasUnifyRamps + "|" + HashOverrides(s.materialOverrides);
            List<MaterialPreviewRow> rows = _cache.GetOrBuild(
                "questatlasmat", sig, () => AvatarQuestConverter.PreviewMaterials(descriptor, quest));

            if (rows == null) { EditorGUILayout.HelpBox("マテリアルプレビューの計算に失敗しました。", MessageType.Warning); return false; }
            if (rows.Count == 0) { EditorGUILayout.LabelField("変換対象のマテリアルが見つかりませんでした。", EditorStyles.miniLabel); return false; }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("アトラス統合の対象マテリアル(チェックで統合対象)", EditorStyles.miniBoldLabel);

            bool changed = false;
            int shown = 0;
            foreach (MaterialPreviewRow row in rows)
            {
                if (row == null || row.material == null) continue;
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField(string.Format("...他 {0} 件", rows.Count - MaxRows), EditorStyles.miniLabel); break; }

                string guid = GetAssetGuid(row.material);
                bool hasGuid = !string.IsNullOrEmpty(guid);
                MaterialOverrideEntry entry = FindEntry(s.materialOverrides, guid);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(row.material, typeof(Material), false, GUILayout.MinWidth(110f), GUILayout.MaxWidth(180f));

                    bool include = row.atlasEligible && !(entry != null && entry.excludeFromAtlas);
                    string tooltip = row.atlasEligible
                        ? "このマテリアルをアトラス統合(1枚のテクスチャへの統合)の対象に含めます"
                        : "対象外: " + (string.IsNullOrEmpty(row.atlasIneligibleReason) ? "条件を満たしていません" : row.atlasIneligibleReason);
                    using (new EditorGUI.DisabledScope(!row.atlasEligible || !hasGuid))
                    {
                        bool newInclude = EditorGUILayout.ToggleLeft(
                            new GUIContent("アトラス統合", tooltip), include, GUILayout.Width(110f));
                        if (row.atlasEligible && hasGuid && newInclude != include)
                        {
                            SetExcludeFromAtlas(s.materialOverrides, guid, !newInclude);
                            changed = true;
                        }
                    }

                    if (!row.atlasEligible)
                    {
                        string reason = string.IsNullOrEmpty(row.atlasIneligibleReason) ? "条件を満たしていません" : row.atlasIneligibleReason;
                        GUILayout.Label(new GUIContent("(対象外: " + Trunc(reason, 40) + ")", reason),
                            EditorStyles.miniLabel, GUILayout.MinWidth(120f));
                    }
                    else if (!hasGuid)
                    {
                        GUILayout.Label("(アセット化されていないため個別設定できません)", EditorStyles.miniLabel, GUILayout.MinWidth(120f));
                    }
                    GUILayout.FlexibleSpace();
                }
            }

            EditorGUILayout.LabelField("※ 実際の統合はビルド前の前処理で行われます。対象/除外は概算の目安です。", EditorStyles.miniLabel);
            return changed;
        }

        // ================================================================
        // 3. 変換設定(基本スカラー群 + QuestBuildPreprocessor)。旧セクション9。
        //    シェーダー別オプション(影ランプ/影ベイク/リム)は shaderTarget に応じて出し分ける。
        // ================================================================
        public static bool DrawQuestConvertSettingsPanel(AvatarStudioSettings s, GameObject avatar)
        {
            if (s == null) { EditorGUILayout.HelpBox("設定が初期化されていません。", MessageType.Info); return false; }

            bool changed = false;

            // ---- テクスチャ・圧縮 ----
            int newMax = EditorGUILayout.IntPopup(
                new GUIContent("最大テクスチャサイズ", "ベイク生成テクスチャの最大サイズ(Quest推奨: 1024)"),
                s.questMaxTextureSize, TextureSizeLabels, TextureSizeValues);
            if (newMax != s.questMaxTextureSize) { s.questMaxTextureSize = newMax; changed = true; }

            var newFormat = (TextureImporterFormat)EditorGUILayout.IntPopup(
                new GUIContent("Android圧縮形式", "Androidプラットフォームのテクスチャ圧縮形式"),
                (int)s.questAndroidFormat, AndroidFormatLabels, AndroidFormatValues);
            if (!Equals(newFormat, s.questAndroidFormat)) { s.questAndroidFormat = newFormat; changed = true; }

            // ---- シェーダー別オプション(変換先シェーダーは「マテリアル」パネルで選ぶため、ここでは読むだけ) ----
            if (s.shaderTarget == QuestShaderTarget.ToonStandard)
            {
                bool ramp = EditorGUILayout.ToggleLeft(
                    new GUIContent("影ランプを生成",
                        "Toon Standard時: lilToonの影設定 / NonToonの影グラデーションから影ランプテクスチャを生成する(オフ時はSDK既定ランプ)"),
                    s.questGenerateShadowRamp);
                if (ramp != s.questGenerateShadowRamp) { s.questGenerateShadowRamp = ramp; changed = true; }
            }

            bool emission = EditorGUILayout.ToggleLeft(
                new GUIContent("エミッションを変換",
                    "エミッションを変換する(Toon Lit: メインへ加算ベイク / Toon Standard: Emissionへマップ)"),
                s.questBakeEmission);
            if (emission != s.questBakeEmission) { s.questBakeEmission = emission; changed = true; }

            if (s.shaderTarget == QuestShaderTarget.ToonLit)
            {
                bool bakeShadow = EditorGUILayout.ToggleLeft(
                    new GUIContent("影をメインテクスチャへベイク",
                        "Toon Lit時: lilToonの影色をメインテクスチャに乗算ベイクする(フラットな擬似陰影)"),
                    s.questBakeShadowIntoMainTex);
                if (bakeShadow != s.questBakeShadowIntoMainTex) { s.questBakeShadowIntoMainTex = bakeShadow; changed = true; }
            }

            if (s.shaderTarget == QuestShaderTarget.ToonStandard)
            {
                bool rim = EditorGUILayout.ToggleLeft(
                    new GUIContent("リムライトを近似変換",
                        "Toon Standard時: lilToon / NonToonのリムライトをToon Standardのリムライトへ近似変換する(既定はオフ)。" +
                        "まぶた等に想定外のハイライト(謎の光)が出る場合はオフのままにしてください"),
                    s.questMapRimLighting);
                if (rim != s.questMapRimLighting) { s.questMapRimLighting = rim; changed = true; }
            }

            bool aggressive = EditorGUILayout.ToggleLeft(
                new GUIContent("単色・低ディテールなテクスチャを極限まで縮小",
                    "アトラス化するテクスチャや容量見積りで、単色・低ディテールなテクスチャを検出して極限まで縮小する。" +
                    "微細な模様が消える場合はオフにしてください(既定はオン)"),
                s.questAggressiveTextureReduction);
            if (aggressive != s.questAggressiveTextureReduction) { s.questAggressiveTextureReduction = aggressive; changed = true; }

            // ---- 詳細設定 ----
            if (AvatarStudioUI.Fold("QuestConvertAdvanced", "詳細設定", false))
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    bool anim = EditorGUILayout.ToggleLeft(
                        new GUIContent("アニメーションも変換",
                            "マテリアル差し替えアニメーション(FX等)も変換後マテリアルを参照するよう複製・差し替える"),
                        s.questConvertAnimations);
                    if (anim != s.questConvertAnimations) { s.questConvertAnimations = anim; changed = true; }

                    bool removeUnsupported = EditorGUILayout.ToggleLeft(
                        new GUIContent("⚠ 非対応コンポーネントを削除",
                            "生成されるQuest複製から、Android非対応コンポーネント(Cloth/Camera/Light/AudioSource等)を削除する(元アバターは変更されません)"),
                        s.questRemoveUnsupportedComponents);
                    if (removeUnsupported != s.questRemoveUnsupportedComponents) { s.questRemoveUnsupportedComponents = removeUnsupported; changed = true; }

                    bool convertConstraints = EditorGUILayout.ToggleLeft(
                        new GUIContent("UnityコンストレイントをVRCConstraintへ変換",
                            "Unityコンストレイントを見つけたらVRCConstraintへ変換する(SDKの変換APIを使用)"),
                        s.questConvertUnityConstraints);
                    if (convertConstraints != s.questConvertUnityConstraints) { s.questConvertUnityConstraints = convertConstraints; changed = true; }

                    bool trim = EditorGUILayout.ToggleLeft(
                        new GUIContent("⚠ マージ後もPoor上限を超える場合に超過分を削除",
                            "PhysBoneのマージ後もAndroidのPoor上限(コンポーネント" + QuestLimits.PoorPhysBoneComponents +
                            "/コライダー" + QuestLimits.PoorPhysBoneColliders + ")を超える場合、Quest複製から超過分を自動削除する(揺れものが動かなくなることがあります)"),
                        s.questTrimPhysBonesToPoorLimit);
                    if (trim != s.questTrimPhysBonesToPoorLimit) { s.questTrimPhysBonesToPoorLimit = trim; changed = true; }

                    // 出力先フォルダ + 既定に戻す
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string newFolder = EditorGUILayout.TextField(
                            new GUIContent("出力先フォルダ", "生成アセットの出力先ルートフォルダ"),
                            s.questOutputFolder ?? string.Empty);
                        if (!string.Equals(newFolder, s.questOutputFolder, StringComparison.Ordinal)) { s.questOutputFolder = newFolder; changed = true; }
                        if (GUILayout.Button(new GUIContent("既定に戻す", "出力先フォルダを既定値に戻します"), GUILayout.Width(80f)))
                        {
                            s.questOutputFolder = new QuestConvertSettings().outputFolder;
                            GUI.FocusControl(null); // 入力中のTextFieldを解除して表示を更新
                            changed = true;
                        }
                    }
                    if (!IsValidOutputFolder(s.questOutputFolder))
                    {
                        EditorGUILayout.HelpBox("出力先は \"Assets/\" から始まるプロジェクト内フォルダを指定してください。", MessageType.Warning);
                    }

                    // Auto-Fix相当のビルドフック(EditorPrefs直結。設定JSONの変更チェックとは分離するため changed には含めない)
                    bool autoStrip = EditorGUILayout.ToggleLeft(
                        new GUIContent("Android/iOSビルド時に非対応コンポーネントを自動削除(Auto Fix相当)",
                            "FaceEmo等のNDMFツールがビルド時に注入するコンポーネントも対象。通常のBuild/Uploadではビルド用コピーに適用され、保存済みシーンは変更されない。"),
                        QuestBuildPreprocessor.Enabled);
                    if (autoStrip != QuestBuildPreprocessor.Enabled)
                    {
                        QuestBuildPreprocessor.Enabled = autoStrip; // 変更時のみEditorPrefsへ書き込む
                    }

                    EditorGUILayout.LabelField(
                        "※ 変換後の元アバターの非アクティブ化はスタジオ側で管理します(このパネルでは設定しません)。",
                        AvatarStudioUI.MiniWrapLabel);
                }
            }

            return changed;
        }

        private static bool IsValidOutputFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string normalized = folder.Replace('\\', '/');
            return normalized == "Assets" || normalized.StartsWith("Assets/", StringComparison.Ordinal);
        }

        // ================================================================
        // 4. メッシュ削減(AAO連携) - 隠れた肌などをブレンドシェイプ削除で消す。旧セクション7。
        //    AAOMeshRemovalHelper.DetectShrinkShapes 候補 + per-renderer 選択 + ブレンドシェイプ名foldout +
        //    AAO未導入ガード + RemoveMeshInBox/ByMask の手動手引きfoldout。
        // ================================================================
        public static bool DrawHiddenMeshPanel(AvatarStudioSettings s, GameObject avatar)
        {
            if (s == null) { EditorGUILayout.HelpBox("設定が初期化されていません。", MessageType.Info); return false; }
            EnsureList(ref s.questHiddenMeshRendererPaths);

            bool changed = false;

            if (AvatarStudioUI.ExplainFold("HiddenMesh", "説明(隠れメッシュ削除の仕組み)"))
            {
                EditorGUILayout.HelpBox(
                    "服の下に完全に隠れて見えない肌などのメッシュを削除して、ポリゴン数・容量を減らします。" +
                    "アバター自身が持つ肌を縮める(shrink)ブレンドシェイプを使い、AAO(Avatar Optimizer)がビルド時に" +
                    "見えない部分だけを削除します(元アバターは変更されません)。",
                    MessageType.Info);
            }

            if (AvatarStudioUI.ExplainFold("HiddenMeshHow", "どうやって検出しているか"))
            {
                EditorGUILayout.HelpBox(
                    "① 名前トークンで候補化: ブレンドシェイプ名が隠し/縮小系トークン" +
                    "(shrink / hide / 縮小 / 非表示 / 削除 / 消し / 隠し 等)を含むもの、または" +
                    "部位トークン(肌 / 素体 / パンツ / インナー 等)とオフ系トークン(off / オフ / なし 等)が" +
                    "同居するものを候補にします。\n" +
                    "② 顔まわりは名前・ジオメトリの両面で除外: 顔ディテール・表情・MMD標準モーフ・リップシンク" +
                    "(顔 / 目 / 眉 / 口 / 涙 / まつ毛 / ほくろ、あ・い・う・え・お、まばたき 等)は、名前一致でも" +
                    "幾何判定でも削除候補にしません(表情・顔ディテールの誤削除防止)。\n" +
                    "③ 名前が一致しなくても、最終フレームで頂点の大部分(全体の約40%以上)を強く動かす形状だけを" +
                    "幾何判定で候補化します(保守的な閾値。頂点数が少なすぎるメッシュは対象外)。\n\n" +
                    "削除されるのは、選んだシェイプが動かす領域だけです(=制作者が「隠れる前提」で設計した箇所)。" +
                    "そのため、その服を後から非表示にすると下の肌が無くなる点に注意してください。\n" +
                    "削除は変換後の複製に付く AAO の Remove Mesh By BlendShape がビルド時に適用します。" +
                    "そのコンポーネントのプレビューで、削除後の状態を確認できます(元アバターは無改変)。",
                    MessageType.None);
            }

            bool aaoInstalled = IsAAOInstalled();
            if (!aaoInstalled)
            {
                EditorGUILayout.HelpBox("この機能にはAvatarOptimizer(AAO)が必要です。", MessageType.Warning);
                AvatarStudioUI.DrawAAOInstallCTA();
            }

            // 候補リストの表示/非表示でコントロール数が変わるため、表示判定は描画前に捕捉する(反映は次回OnGUIから)。
            bool showHiddenList = s.questRemoveHiddenMeshByBlendShape;

            using (new EditorGUI.DisabledScope(!aaoInstalled))
            {
                bool remove = EditorGUILayout.ToggleLeft(
                    new GUIContent("隠れた肌などをブレンドシェイプ削除で消す",
                        "服の下に隠れる肌などをAAOのブレンドシェイプ削除で消す(shrinkブレンドシェイプ検出時。見えない部分のみ)"),
                    s.questRemoveHiddenMeshByBlendShape);
                if (remove != s.questRemoveHiddenMeshByBlendShape) { s.questRemoveHiddenMeshByBlendShape = remove; changed = true; }

                bool ensureTao = EditorGUILayout.ToggleLeft(
                    new GUIContent("Trace and Optimizeが無ければ複製に追加",
                        "AAOのTrace and Optimizeが無ければ複製に追加してビルド時最適化を有効にする"),
                    s.ensureTraceAndOptimize);
                if (ensureTao != s.ensureTraceAndOptimize) { s.ensureTraceAndOptimize = ensureTao; changed = true; }
            }

            // 候補リストは機能有効かつAAO導入時のみ表示する(表示判定は描画前に捕捉した値を使う)
            if (aaoInstalled && showHiddenList)
            {
                changed |= DrawHiddenMeshCandidateList(s, avatar);
            }

            // 手動削除の手引き(常時表示)
            DrawHiddenMeshManualGuidance();
            return changed;
        }

        /// <summary>
        /// shrinkブレンドシェイプ候補の一覧(DetectShrinkShapes をキャッシュ)。候補ごとに
        /// チェック(questHiddenMeshRendererPaths へ登録)・名前・ブレンドシェイプ名foldout・理由・ピンを描画する。
        /// </summary>
        private static bool DrawHiddenMeshCandidateList(AvatarStudioSettings s, GameObject avatar)
        {
            if (avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、shrinkブレンドシェイプを検出します。", EditorStyles.miniLabel);
                return false;
            }

            List<ShrinkShapeCandidate> candidates = _cache.GetOrBuild(
                "questhidden", avatar.GetInstanceID().ToString(),
                () => AAOMeshRemovalHelper.DetectShrinkShapes(avatar) ?? new List<ShrinkShapeCandidate>());

            if (candidates == null)
            {
                EditorGUILayout.HelpBox("shrinkブレンドシェイプの検出に失敗しました(コンソールを確認してください)。", MessageType.Warning);
                return false;
            }
            if (candidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "shrinkブレンドシェイプは検出されませんでした。手動での箱指定削除(下の手引き)をご検討ください。",
                    MessageType.Info);
                return false;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("削除に使うメッシュを選んでください(チェックしたメッシュのみ対象)", EditorStyles.miniBoldLabel);

            bool changed = false;
            int shown = 0;
            foreach (ShrinkShapeCandidate candidate in candidates)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.rendererPath)) continue;
                if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                changed |= DrawHiddenMeshCandidateRow(s, avatar, candidate);
            }

            int selected = CountSelectedHiddenMeshRenderers(s, candidates);
            EditorGUILayout.LabelField(
                selected == 0
                    ? "対象に選ばれたメッシュはありません(このままでは削除は行われません)。"
                    : "削除対象メッシュ " + selected + " 件。ビルド時にshrinkブレンドシェイプで隠れた部分が削除されます。",
                AvatarStudioUI.MiniWrapLabel);
            return changed;
        }

        /// <summary>shrinkブレンドシェイプ候補1件分の行(チェック / 理由 / ブレンドシェイプ名foldout / ピン)。</summary>
        private static bool DrawHiddenMeshCandidateRow(AvatarStudioSettings s, GameObject avatar, ShrinkShapeCandidate candidate)
        {
            bool changed = false;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool included = s.questHiddenMeshRendererPaths.Contains(candidate.rendererPath);
                    bool newIncluded = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            string.IsNullOrEmpty(candidate.rendererName) ? candidate.rendererPath : candidate.rendererName,
                            "パス: " + candidate.rendererPath),
                        included);
                    if (newIncluded != included)
                    {
                        TogglePath(s.questHiddenMeshRendererPaths, candidate.rendererPath, newIncluded);
                        changed = true;
                    }
                    GUILayout.FlexibleSpace();
                    DrawPingButton(avatar, candidate.rendererPath);
                }

                if (!string.IsNullOrEmpty(candidate.reason))
                {
                    EditorGUILayout.LabelField(candidate.reason, AvatarStudioUI.MiniWrapLabel);
                }

                int shapeCount = candidate.blendShapeNames != null ? candidate.blendShapeNames.Count : 0;
                if (AvatarStudioUI.Fold("HiddenMeshShapes." + candidate.rendererPath, "使用するブレンドシェイプ (" + shapeCount + ")", false))
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        if (shapeCount == 0)
                        {
                            EditorGUILayout.LabelField("(なし)", EditorStyles.miniLabel);
                        }
                        else
                        {
                            foreach (string shapeName in candidate.blendShapeNames)
                            {
                                EditorGUILayout.LabelField("・" + (shapeName ?? "(不明)"), AvatarStudioUI.MiniWrapLabel);
                            }
                        }
                    }
                }
            }
            return changed;
        }

        /// <summary>shrinkブレンドシェイプが無い場合の手動削除の手引き(常時表示・既定は閉じる)。</summary>
        private static void DrawHiddenMeshManualGuidance()
        {
            EditorGUILayout.Space(4f);
            if (!AvatarStudioUI.Fold("HiddenMeshManual", "手動削除の手引き(shrinkブレンドシェイプが無い/足りない場合)", false)) return;
            EditorGUILayout.HelpBox(
                "shrinkブレンドシェイプが無い / 足りない場合の手動削除の手引き:\n" +
                "服で完全に隠れて穴が開かない肌部分は、ポリゴンごと削除して問題ありません。" +
                "隠したいスキンメッシュのGameObjectに、AAOの次の削除コンポーネントを追加して範囲を指定します" +
                "(削除は複製のビルド時にAAOが行い、元アバターは変更されません):\n" +
                "・Remove Mesh in Box(箱で囲った範囲を削除): シーンビューの箱で囲んだ頂点を削除。胴・脚などを範囲で消すときに。\n" +
                "・Remove Mesh By Mask(マスク画像で削除): 白黒マスクテクスチャで削除範囲を指定。UVに沿って細かく消すときに。\n" +
                "・Remove Mesh By UV Tile(UVタイルで削除): UVタイル単位で削除。タイルで分かれたメッシュに。",
                MessageType.None);
        }

        // ================================================================
        // 5. 表情デカール(チーク/涙/アイハイライト)の予定表示。旧セクション3の表情デカールプレビュー。
        //    AvatarQuestConverter.PreviewExpressionDecals の行(transparentHandling ごとの fate)+ ピン。
        // ================================================================
        public static bool DrawExpressionDecalPanel(AvatarStudioSettings s, GameObject avatar)
        {
            if (s == null) { EditorGUILayout.HelpBox("設定が初期化されていません。", MessageType.Info); return false; }
            if (avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、表情デカール(チーク/涙/アイハイライト)を検出します。", EditorStyles.miniLabel);
                return false;
            }
            VRCAvatarDescriptor descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
            {
                EditorGUILayout.LabelField("VRCAvatarDescriptor が見つかりません(アバター直下に必要です)。", EditorStyles.miniLabel);
                return false;
            }

            // Emulate(既定)ではデカールも乗算/加算で再現され非表示化されないため、
            // PreviewExpressionDecals は空リストを返す契約。件数では検出有無を判定できないので専用の案内を出す。
            if (s.transparentHandling == TransparentHandling.Emulate)
            {
                EditorGUILayout.HelpBox(
                    "既定処理『自動で半透明を再現』のため、表情デカール(チーク/涙/アイハイライト)は" +
                    "乗算/加算パーティクルで再現されます(非表示化されません)。",
                    MessageType.Info);
                return false;
            }

            QuestConvertSettings quest = AvatarStudioMapping.BuildQuestConvertSettings(s);
            List<DecalOverlayRow> rows = _cache.GetOrBuild(
                "questdecal", avatar.GetInstanceID() + "|" + (int)s.transparentHandling,
                () => AvatarQuestConverter.PreviewExpressionDecals(descriptor, quest));

            if (rows == null) { EditorGUILayout.HelpBox("表情デカール検出の計算に失敗しました。", MessageType.Warning); return false; }
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("表情デカール(チーク/涙/アイハイライト)は検出されませんでした。", MessageType.Info);
                return false;
            }

            // 既定処理でこれらのデカールがどう扱われるかを1行で示す(Emulate は上で早期return済み。ここは Hide / Opaque)。
            string fate = s.transparentHandling == TransparentHandling.Hide
                ? "既定処理『非表示にする』により非表示化されます(顔本体・目・眉は残ります)。"
                : "『不透明に変換』でも板状に見えてしまうため、表情デカールは非表示化されます(顔本体・目・眉は残ります)。";
            EditorGUILayout.LabelField(fate, AvatarStudioUI.MiniWrapLabel);

            if (AvatarStudioUI.Fold("ExpressionDecals", "検出された表情デカール (" + rows.Count + ")", true))
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    int shown = 0;
                    foreach (DecalOverlayRow row in rows)
                    {
                        if (row == null) continue;
                        if (shown++ >= MaxRows) { EditorGUILayout.LabelField("...(以下省略)", EditorStyles.miniLabel); break; }
                        DrawExpressionDecalRow(avatar, row);
                    }
                }
            }

            // このパネルは読み取り専用(予定表示のみ)。設定は変更しないため常に false。
            return false;
        }

        /// <summary>表情デカール1件分の行(マテリアル参照+スロット / レンダラーパス / 理由 / ピン)。</summary>
        private static void DrawExpressionDecalRow(GameObject avatar, DecalOverlayRow row)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true)) // 読み取り専用表示
                    {
                        EditorGUILayout.ObjectField(row.material, typeof(Material), false, GUILayout.Width(150f));
                    }
                    EditorGUILayout.LabelField("スロット " + row.slotIndex, EditorStyles.miniLabel, GUILayout.Width(70f));
                    GUILayout.FlexibleSpace();
                    DrawPingButton(avatar, row.rendererPath);
                }
                if (!string.IsNullOrEmpty(row.rendererPath))
                {
                    EditorGUILayout.LabelField(row.rendererPath, AvatarStudioUI.MiniWrapLabel);
                }
                if (!string.IsNullOrEmpty(row.reason))
                {
                    EditorGUILayout.LabelField("理由: " + row.reason, AvatarStudioUI.MiniWrapLabel);
                }
            }
        }

        // ================================================================
        // 共有ヘルパ
        // ================================================================

        /// <summary>rendererPath の指すGameObjectをピン表示するボタン(解決できた場合のみ有効)。</summary>
        private static void DrawPingButton(GameObject avatar, string rendererPath)
        {
            Transform resolved = avatar != null ? QuestCompat.FindByPath(avatar.transform, rendererPath) : null;
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当メッシュをハイライト表示します"), GUILayout.Width(36f)))
                {
                    EditorGUIUtility.PingObject(resolved.gameObject);
                }
            }
        }

        /// <summary>AAO(Avatar Optimizer)が導入されているか(TraceAndOptimize型の有無で判定。コンパイル時参照はしない)。</summary>
        private static bool IsAAOInstalled()
        {
            return QuestCompat.FindType("Anatawa12.AvatarOptimizer.TraceAndOptimize") != null;
        }

        private static int CountSelectedHiddenMeshRenderers(AvatarStudioSettings s, List<ShrinkShapeCandidate> candidates)
        {
            if (s.questHiddenMeshRendererPaths == null || candidates == null) return 0;
            int count = 0;
            foreach (ShrinkShapeCandidate candidate in candidates)
            {
                if (candidate != null && candidate.rendererPath != null &&
                    s.questHiddenMeshRendererPaths.Contains(candidate.rendererPath)) count++;
            }
            return count;
        }

        private static void EnsureList<T>(ref List<T> list) { if (list == null) list = new List<T>(); }

        private static void TogglePath(List<string> list, string path, bool present)
        {
            if (list == null || string.IsNullOrEmpty(path)) return;
            bool has = list.Contains(path);
            if (present && !has) list.Add(path);
            else if (!present && has) list.Remove(path);
        }

        private static string GetAssetGuid(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            string path = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
        }

        /// <summary>materialOverrides から guid のエントリを探す(見つからなければ null)。</summary>
        private static MaterialOverrideEntry FindEntry(List<MaterialOverrideEntry> list, string guid)
        {
            if (list == null || string.IsNullOrEmpty(guid)) return null;
            foreach (MaterialOverrideEntry e in list)
                if (e != null && e.materialGuid == guid) return e;
            return null;
        }

        /// <summary>
        /// guid のマテリアルのアトラス除外フラグを設定する。既定(mode=Auto かつ exclude=false)に戻ったエントリは
        /// リストから取り除き(設定JSONの肥大化防止)、除外指定が付いたら必要ならエントリを新規作成する。
        /// </summary>
        private static void SetExcludeFromAtlas(List<MaterialOverrideEntry> list, string guid, bool exclude)
        {
            if (list == null || string.IsNullOrEmpty(guid)) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].materialGuid == guid)
                {
                    list[i].excludeFromAtlas = exclude;
                    // mode=Auto かつ 除外なし = 既定 → エントリ削除(materialOverrides を肥大化させない)
                    if (list[i].mode == MaterialOverride.Auto && !list[i].excludeFromAtlas) list.RemoveAt(i);
                    return;
                }
            }
            if (exclude) // 既定(除外なし)ならエントリを作らない
                list.Add(new MaterialOverrideEntry { materialGuid = guid, mode = MaterialOverride.Auto, excludeFromAtlas = true });
        }

        private static int HashOverrides(List<MaterialOverrideEntry> list)
        {
            if (list == null) return 0;
            int h = 17;
            foreach (MaterialOverrideEntry e in list)
            {
                if (e == null) { h = h * 31; continue; }
                h = h * 31 + (e.materialGuid != null ? e.materialGuid.GetHashCode() : 0);
                h = h * 31 + (int)e.mode;
                h = h * 31 + (e.excludeFromAtlas ? 1 : 0);
            }
            return h;
        }

        private static string Trunc(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
#endif
