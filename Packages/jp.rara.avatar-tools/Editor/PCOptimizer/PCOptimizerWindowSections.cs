// RARA PC軽量化ツール - メインウィンドウのセクション描画(partial)
// PCOptimizerWindow.cs から分割したセクション4〜9の描画と、各検出・提案の再取得ヘルパーを持つ。
//   4.テクスチャメモリ削減 / 5.マテリアル統合(アトラス) / 6.メッシュ・トグル整理 / 7.PhysBone整理 / 8.実行 / 9.ヘルプ
// RARA.QuestConverter の検出・削減エンジン(ToggleConsolidator / ComponentRemover)を再利用する。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RARA.QuestConverter;

namespace RARA.PCOptimizer
{
    public partial class PCOptimizerWindow
    {
        // ================================================================
        // 4. テクスチャメモリ削減
        // ================================================================
        private void DrawTextureSection()
        {
            if (!DrawSectionFoldout(4, "テクスチャメモリ削減",
                "テクスチャの解像度を下げてテクスチャメモリ(VRAM)を削減します。元テクスチャは変更せず、生成時に縮小コピーを作ります。",
                FoldKeyTexture))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new PCOptimizeSettings();
                EnsureTexturePlanList();

                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると、テクスチャの削減提案が表示されます。", EditorStyles.miniLabel);
                    return;
                }
                if (_diagnostics == null)
                {
                    EditorGUILayout.LabelField("診断するとテクスチャメモリと削減提案が表示されます。", EditorStyles.miniLabel);
                    return;
                }

                // 現在のテクスチャメモリ vs 目標閾値
                float current = _diagnostics.textureMemoryMB;
                int limit = PCRankLimits.GetLimit(_settings.targetRank, PCRankLimits.PCStat.TextureMemoryMB);
                bool over = current > limit;
                EditorGUILayout.HelpBox(
                    "テクスチャメモリ(VRAM): " + current.ToString("F1") + " MB / 目標(" + TargetRankName(_settings.targetRank) + ") " + limit + " MB" +
                    (over ? "\n目標を超えています。以下の提案で縮小してください。" : "\n目標を満たしています。"),
                    over ? MessageType.Warning : MessageType.Info);

                int planCount = _settings.texturePlan != null ? _settings.texturePlan.Count : 0;

                // 未計算なら遅延取得を1回だけ予約する
                if (_textureSuggestions == null && !_textureSuggestionsFailed && !_textureSuggestionsQueued)
                {
                    QueueTextureSuggestionsRefresh();
                }

                if (_textureSuggestions == null)
                {
                    EditorGUILayout.LabelField(_textureSuggestionsFailed ? "提案の計算に失敗しました。" : "提案を計算中...", EditorStyles.miniLabel);
                }
                else
                {
                    DrawTextureBulkButtons(current, limit);
                    if (_textureSuggestions.Count == 0)
                    {
                        EditorGUILayout.LabelField("縮小できるテクスチャは見つかりませんでした。", EditorStyles.miniLabel);
                    }
                    else
                    {
                        foreach (PCTexturePlanner.PCTextureSuggestion suggestion in _textureSuggestions)
                        {
                            DrawTextureSuggestionRow(suggestion);
                        }
                    }
                }

                // 縮小計画の件数とクリア
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        "現在の縮小計画: " + planCount + " 件(元テクスチャは変更せず、生成時に縮小コピーを作ります)",
                        _miniWrapLabel);
                    using (new EditorGUI.DisabledScope(planCount == 0))
                    {
                        if (GUILayout.Button(new GUIContent("クリア", "登録済みの縮小計画をすべて削除します"), EditorStyles.miniButton, GUILayout.Width(70f)))
                        {
                            _settings.texturePlan.Clear();
                            SaveSettings();
                        }
                    }
                }
            }
        }

        /// <summary>テクスチャ削減提案の一括ボタン(すべて適用 / 目標まで自動調整)。</summary>
        private void DrawTextureBulkButtons(float currentMB, int targetMB)
        {
            if (_textureSuggestions == null || _textureSuggestions.Count == 0) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("すべて適用 (" + _textureSuggestions.Count + "件)",
                    "すべての縮小提案を縮小計画に登録します(元テクスチャは変更しません)"), GUILayout.Height(22f)))
                {
                    int applied = 0;
                    foreach (PCTexturePlanner.PCTextureSuggestion s in _textureSuggestions)
                    {
                        if (s != null && s.texture != null && s.suggestedSize > 0 && UpsertTexturePlan(s.texture, s.suggestedSize)) applied++;
                    }
                    if (applied > 0) SaveSettings();
                    Debug.Log("[RARA PCOptimizer] テクスチャ縮小提案を一括登録: " + applied + " 件。");
                }

                if (currentMB > targetMB)
                {
                    if (GUILayout.Button(new GUIContent("目標まで自動調整",
                        "目標(" + targetMB + "MB)を下回るよう、削減効果の大きいテクスチャから順に縮小計画へ登録します(概算・元テクスチャは変更しません)"), GUILayout.Height(22f)))
                    {
                        RunTextureBudgetFit(currentMB, targetMB);
                    }
                }
            }
        }

        /// <summary>削減効果の大きい順に、目標MBを下回る見込みになるまで縮小計画へ登録する(概算)。</summary>
        private void RunTextureBudgetFit(float currentMB, int targetMB)
        {
            var sorted = new List<PCTexturePlanner.PCTextureSuggestion>();
            foreach (PCTexturePlanner.PCTextureSuggestion s in _textureSuggestions)
            {
                if (s != null && s.texture != null && s.suggestedSize > 0) sorted.Add(s);
            }
            sorted.Sort((a, b) => b.saveMB.CompareTo(a.saveMB));

            float remaining = currentMB;
            int applied = 0;
            foreach (PCTexturePlanner.PCTextureSuggestion s in sorted)
            {
                if (remaining <= targetMB) break;
                if (UpsertTexturePlan(s.texture, s.suggestedSize))
                {
                    remaining -= s.saveMB;
                    applied++;
                }
            }
            if (applied > 0) SaveSettings();
            EditorUtility.DisplayDialog("目標まで自動調整",
                applied + " 件のテクスチャを縮小計画に登録しました(概算で約 " + remaining.ToString("F1") + " MB 見込み)。\n" +
                "元テクスチャは変更しません。「再診断」で反映後の値を確認できます。", "OK");
        }

        /// <summary>テクスチャ削減提案1件分の行(参照 / 現在→推奨サイズ / 削減MB / 理由 / 適用)を描画する。</summary>
        private void DrawTextureSuggestionRow(PCTexturePlanner.PCTextureSuggestion suggestion)
        {
            if (suggestion == null || suggestion.texture == null) return;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(suggestion.texture, typeof(Texture2D), false, GUILayout.Width(150f));
                    }
                    EditorGUILayout.LabelField(
                        suggestion.currentSize + "px → " + suggestion.suggestedSize + "px  (-" + suggestion.saveMB.ToString("F1") + "MB)",
                        GUILayout.MinWidth(150f));
                    GUILayout.FlexibleSpace();
                    bool alreadyPlanned = IsTexturePlanned(suggestion.texture, suggestion.suggestedSize);
                    using (new EditorGUI.DisabledScope(suggestion.suggestedSize <= 0 || alreadyPlanned))
                    {
                        if (GUILayout.Button(new GUIContent(alreadyPlanned ? "登録済" : "適用",
                            "このテクスチャを縮小計画に登録します(元テクスチャは変更せず、生成時に縮小コピーを作ります)"), GUILayout.Width(56f)))
                        {
                            if (UpsertTexturePlan(suggestion.texture, suggestion.suggestedSize)) SaveSettings();
                        }
                    }
                }
                if (!string.IsNullOrEmpty(suggestion.reason))
                {
                    EditorGUILayout.LabelField("理由: " + suggestion.reason, _miniWrapLabel);
                }
            }
        }

        // ================================================================
        // 5. マテリアル統合(アトラス)
        // ================================================================
        private static readonly GUIContent[] AtlasSizeLabels = { new GUIContent("1024"), new GUIContent("2048(推奨)"), new GUIContent("4096") };
        private static readonly int[] AtlasSizeValues = { 1024, 2048, 4096 };

        /// <summary>アウトライン統合モードの選択肢(OutlineUnifyMode の並び 0=しない / 1=外して統合 / 2=付きに統一 と一致させること)。</summary>
        private static readonly GUIContent[] AtlasOutlineModeLabels =
        {
            new GUIContent("アウトライン統合しない", "アウトライン有無が異なるマテリアルは統合しません(別グループのまま)"),
            new GUIContent("アウトラインを外して統合(推奨)", "アウトライン付きと無しを統合し、プレーンlilToonへ揃えます。外す=服の輪郭線が消えますが、瞳・顔に黒縁は付きません"),
            new GUIContent("アウトライン付きに統一", "アウトライン付き側へ揃えます。付き=輪郭の無かった部分にアウトラインが付きます(瞳・顔は自動回避)"),
        };

        private void DrawAtlasSection()
        {
            if (!DrawSectionFoldout(5, "マテリアル統合(アトラス)",
                "複数マテリアルを1枚のテクスチャにまとめ、マテリアルスロット数とテクスチャメモリを削減します。",
                FoldKeyAtlas))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new PCOptimizeSettings();
                EnsureAtlasExcludeList();

                EditorGUI.BeginChangeCheck();

                _settings.enableAtlas = EditorGUILayout.ToggleLeft(
                    new GUIContent("アトラス統合を有効化",
                        "互換性のあるマテリアルを1枚のアトラステクスチャに統合し、マテリアルスロット数を削減します"),
                    _settings.enableAtlas);

                if (_settings.enableAtlas)
                {
                    _settings.atlasMaxSize = EditorGUILayout.IntPopup(
                        new GUIContent("アトラス最大サイズ", "統合後のアトラステクスチャの最大サイズ"),
                        _settings.atlasMaxSize, AtlasSizeLabels, AtlasSizeValues);

                    _settings.atlasColorOnlyMaterials = EditorGUILayout.ToggleLeft(
                        new GUIContent("テクスチャ無し(色だけ)の材質も統合",
                            "メインテクスチャを持たない単色マテリアルも、その色をアトラスのセルに焼き込んで統合対象にします(見た目は近似)"),
                        _settings.atlasColorOnlyMaterials);

                    _settings.atlasBakeEmissionMask = EditorGUILayout.ToggleLeft(
                        new GUIContent("エミッションをアトラスへ焼き込む",
                            "エミッション色/マップをエミッション用アトラスのセルに焼き込みます(発光を維持)"),
                        _settings.atlasBakeEmissionMask);

                    _settings.atlasIgnoreCull = EditorGUILayout.ToggleLeft(
                        new GUIContent("カリング差を無視",
                            "カリング(片面/両面)の違いを無視して統合します。統合後のマテリアルは両面描画(Cull Off)になります。見た目の破綻はまれですが、フィルレートが少し増えます"),
                        _settings.atlasIgnoreCull);

                    _settings.atlasOutlineUnifyMode = (OutlineUnifyMode)EditorGUILayout.Popup(
                        new GUIContent("アウトライン",
                            "アトラス統合時のアウトライン(輪郭線)の扱い。外す=服の輪郭線が消える / 付き=輪郭の無かった部分に付く(瞳・顔は自動回避)"),
                        Mathf.Clamp((int)_settings.atlasOutlineUnifyMode, 0, AtlasOutlineModeLabels.Length - 1),
                        AtlasOutlineModeLabels);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    SaveSettings();
                    QueueAtlasPlanRefresh(); // 設定変更で統合プレビューを再計算する
                }

                if (DrawExplainFoldout("RARA.PCOptimizer.Fold.Explain.Atlas"))
                {
                    EditorGUILayout.HelpBox(
                        "互換マテリアルを1枚のアトラスに統合してマテリアルスロット数を削減します。\n" +
                        "テクスチャ無しの単色マテリアルは、その色をアトラスのセルに焼き込むことで統合できます(近似)。\n" +
                        "メッシュ結合はAAO(Trace and Optimize)がビルド時に実施します。\n" +
                        "UVタイリングを使うマテリアルなど統合できないものは自動で除外されます。",
                        MessageType.Info);
                }

                if (_settings.enableAtlas)
                {
                    DrawAtlasMergePreview();
                    DrawAtlasExcludeList();
                }
            }
        }

        /// <summary>
        /// 統合予定グループ / 統合できないマテリアルと理由を、ベイクせずにプレビュー表示する。
        /// 実行(BuildAndApply)と同じキーロジック(PCMaterialAtlasser.PreviewPlan)で計算するため、
        /// ここに出るグループがそのまま生成結果になる(トグル固定・AAO結合は反映前の概算)。
        /// </summary>
        private void DrawAtlasMergePreview()
        {
            if (_avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、統合予定と統合できない理由が表示されます。", EditorStyles.miniLabel);
                return;
            }

            // 遅延計算(アバター/設定変更で _atlasPlan は null 化される)
            if (_atlasPlan == null && !_atlasPlanFailed && !_atlasPlanQueued)
            {
                QueueAtlasPlanRefresh();
            }

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("統合プレビュー(現在の設定)", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("再計算", "現在のアバターと設定で統合プレビューを計算し直します"), EditorStyles.miniButton, GUILayout.Width(70f)))
                {
                    QueueAtlasPlanRefresh();
                }
            }

            if (_atlasPlan == null)
            {
                EditorGUILayout.LabelField(_atlasPlanFailed ? "統合プレビューの計算に失敗しました(Consoleを確認してください)。" : "統合プレビューを計算中...", EditorStyles.miniLabel);
                return;
            }

            var plan = _atlasPlan;

            // 統合後の概算マテリアル数
            EditorGUILayout.HelpBox(
                "統合対象 " + plan.candidateMaterialCount + " マテリアル → 統合後 おおよそ " + plan.projectedMaterialCount + " マテリアル。\n" +
                "実際のマテリアルスロット数は、この統合に加えてレンダラーの結合(AAOのTrace and Optimizeやトグル固定)にも依存します。",
                MessageType.Info);

            // 統合予定グループ
            if (plan.groups.Count > 0)
            {
                EditorGUILayout.LabelField("統合予定グループ", EditorStyles.miniBoldLabel);
                foreach (PCMaterialAtlasser.AtlasPlanGroup group in plan.groups)
                {
                    if (group == null) continue;
                    using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent(group.shaderLabel + ": " + group.memberNames.Count + " 材質 → 1", "シェーダー系統: " + group.shaderLabel),
                            EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(string.Join(", ", group.memberNames), _miniWrapLabel);
                        if (group.twoSided)
                        {
                            EditorGUILayout.LabelField("・両面描画(Cull Off)に統一されます(カリング差を無視)", _miniWrapLabel);
                        }
                        if (group.outlineAdded != null && group.outlineAdded.Count > 0)
                        {
                            EditorGUILayout.LabelField("・アウトラインが付与されます: " + string.Join(", ", group.outlineAdded), _miniWrapLabel);
                        }
                        if (group.outlineRemoved != null && group.outlineRemoved.Count > 0)
                        {
                            EditorGUILayout.LabelField("・アトラス統合によりアウトラインが外れます: " + string.Join(", ", group.outlineRemoved), _miniWrapLabel);
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.LabelField("統合予定のグループはありません。", EditorStyles.miniLabel);
            }

            // 統合できないマテリアルと理由
            if (plan.blocked.Count > 0)
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("統合できないマテリアルと理由", EditorStyles.miniBoldLabel);
                var defaultColor = GUI.color;
                foreach (PCMaterialAtlasser.AtlasPlanBlocked item in plan.blocked)
                {
                    if (item == null) continue;
                    EditorGUILayout.LabelField("・" + item.name + ": " + item.reason, _miniWrapLabel);
                    if (!string.IsNullOrEmpty(item.hint))
                    {
                        GUI.color = NoteYellowColor;
                        EditorGUILayout.LabelField("　→ " + item.hint, _miniWrapLabel);
                        GUI.color = defaultColor;
                    }
                }
                GUI.color = defaultColor;
            }
        }

        /// <summary>アバターが使うマテリアルごとにアトラス除外を切り替えるリストを描画する。</summary>
        private void DrawAtlasExcludeList()
        {
            if (_avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、マテリアルごとの除外指定ができます。", EditorStyles.miniLabel);
                return;
            }
            if (_avatarMaterials == null)
            {
                EditorGUILayout.LabelField("(診断後にマテリアル一覧が表示されます)", EditorStyles.miniLabel);
                return;
            }
            if (_avatarMaterials.Count == 0)
            {
                EditorGUILayout.LabelField("マテリアルが見つかりませんでした。", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("アトラスから除外するマテリアル", EditorStyles.miniBoldLabel);
            foreach (Material mat in _avatarMaterials)
            {
                if (mat == null) continue;
                string guid = GetAssetGuid(mat);
                bool hasGuid = !string.IsNullOrEmpty(guid);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(mat, typeof(Material), false, GUILayout.Width(150f));
                    }
                    using (new EditorGUI.DisabledScope(!hasGuid))
                    {
                        bool excluded = hasGuid && _settings.atlasExcludeMaterialGuids.Contains(guid);
                        bool newExcluded = EditorGUILayout.ToggleLeft(
                            new GUIContent("除外", "このマテリアルをアトラス統合の対象から外します"), excluded, GUILayout.Width(60f));
                        if (hasGuid && newExcluded != excluded)
                        {
                            if (newExcluded) _settings.atlasExcludeMaterialGuids.Add(guid);
                            else _settings.atlasExcludeMaterialGuids.Remove(guid);
                            SaveSettings();
                            QueueAtlasPlanRefresh(); // 除外指定の変更で統合プレビューを再計算する
                        }
                    }
                    string reason = AtlasIneligibleReason(mat);
                    if (!string.IsNullOrEmpty(reason))
                    {
                        var prev = GUI.color;
                        GUI.color = NoteYellowColor;
                        EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                        GUI.color = prev;
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        /// <summary>マテリアルがアトラス統合に向かない理由の簡易プレビュー(安価に分かる範囲だけ)。無ければnull。</summary>
        private static string AtlasIneligibleReason(Material mat)
        {
            if (mat == null || mat.shader == null) return "シェーダー欠落";
            if (mat.shader.name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0) return "TMP(統合不可)";
            return null;
        }

        // ================================================================
        // 6. メッシュ・トグル整理
        // ================================================================

        /// <summary>トグル整理の選択肢の表示名(ToggleLockChoiceの並び順 Keep, LockVisible, LockHidden と一致させること)。</summary>
        private static readonly GUIContent[] ToggleChoiceLabels =
        {
            new GUIContent("トグル維持", "現状のままトグルで切り替えられます(メッシュ・マテリアルスロットは減りません)"),
            new GUIContent("表示で固定", "常時表示にしてトグルを外し、AAOビルド時に結合対象にします(スキンメッシュ・マテリアルスロットが減ります)"),
            new GUIContent("非表示で固定", "このメッシュを削除します(EditorOnly化。スロット・揺れも消えます)"),
        };

        private void DrawOutfitToggleSection()
        {
            if (!DrawSectionFoldout(6, "メッシュ・トグル整理",
                "トグルで切り替える衣装を固定してメッシュ・スロットを削減します(固定するとトグルは無くなります)。",
                FoldKeyOutfit))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new PCOptimizeSettings();
                EnsureToggleChoicesList();

                if (DrawExplainFoldout("RARA.PCOptimizer.Fold.Explain.Outfit"))
                {
                    EditorGUILayout.HelpBox(
                        "トグルで切り替える衣装/アクセサリは、そのままだとメッシュ・マテリアルスロットが減りません。" +
                        "「表示で固定」にするとAAO(Trace and Optimize)がビルド時に結合し、スキンメッシュ/スロットを削減できます(トグルは無くなります)。" +
                        "「非表示で固定」はそのメッシュを削除します。",
                        MessageType.Info);
                }

                // SkinnedMesh統合(顔以外を1つへ)ブロック(トグル整理より上に置き、メッシュ数削減の要点として提示)
                DrawSkinnedMeshMergeBlockPC();
                EditorGUILayout.Space(6f);

                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると、トグルで切り替わる衣装/アクセサリを検出して整理できます。", EditorStyles.miniLabel);
                    return;
                }

                if (_toggleGroups == null && !_toggleGroupsFailed && !_toggleGroupsQueued)
                {
                    QueueToggleGroupsRefresh();
                }

                if (_toggleGroups == null)
                {
                    if (_toggleGroupsFailed)
                    {
                        EditorGUILayout.HelpBox("トグルの検出に失敗しました(Consoleを確認してください)。", MessageType.Error);
                        if (GUILayout.Button(new GUIContent("再検出", "トグルを検出し直します"), GUILayout.Width(70f)))
                        {
                            QueueToggleGroupsRefresh();
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("トグルを検出中...", EditorStyles.miniLabel);
                    }
                    return;
                }

                if (_toggleGroups.Count == 0)
                {
                    EditorGUILayout.LabelField("トグルは検出されませんでした。", EditorStyles.miniLabel);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("現在の表示状態で固定(全トグル)",
                        "表示中のトグルは「表示で固定」、非表示のトグルは「非表示で固定」に一括設定します"), GUILayout.Height(22f)))
                    {
                        LockAllTogglesToCurrentState();
                    }
                    if (GUILayout.Button(new GUIContent("すべてトグル維持に戻す",
                        "検出中のトグルをすべて「トグル維持」に戻します"), GUILayout.Height(22f)))
                    {
                        ResetAllToggleChoices();
                    }
                }

                foreach (ToggleGroup group in _toggleGroups)
                {
                    DrawToggleGroupRow(group);
                }

                DrawToggleProjectedNote();
            }
        }

        /// <summary>トグルグループ1件分の行(ラベル+メッシュ数+現在状態 / 選択ポップアップ / ピン)を描画する。</summary>
        private void DrawToggleGroupRow(ToggleGroup group)
        {
            if (group == null || string.IsNullOrEmpty(group.id)) return;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string stateText = group.defaultActive ? "表示中" : "非表示";
                    string tip = "ID: " + group.id;
                    if (!string.IsNullOrEmpty(group.source)) tip += "\n検出元: " + group.source;
                    EditorGUILayout.LabelField(
                        new GUIContent(
                            (string.IsNullOrEmpty(group.label) ? group.id : group.label) +
                            "  (メッシュ " + group.rendererCount + " / 現在: " + stateText + ")",
                            tip),
                        _wrapLabel);

                    GUILayout.FlexibleSpace();

                    ToggleGroupChoice entry = FindToggleChoice(group.id);
                    ToggleLockChoice choice = entry != null ? entry.choice : ToggleLockChoice.Keep;
                    int index = Mathf.Clamp((int)choice, 0, ToggleChoiceLabels.Length - 1);
                    int newIndex = EditorGUILayout.Popup(index, ToggleChoiceLabels, GUILayout.Width(120f));
                    if (newIndex != index)
                    {
                        SetToggleChoice(group.id, (ToggleLockChoice)newIndex);
                    }

                    DrawTogglePingButton(group);
                }
            }
        }

        /// <summary>トグルグループの代表オブジェクトをピン表示するボタンを描画する。</summary>
        private void DrawTogglePingButton(ToggleGroup group)
        {
            Transform resolved = null;
            if (_avatar != null && group != null && group.objectPaths != null)
            {
                foreach (string path in group.objectPaths)
                {
                    resolved = QuestCompat.FindByPath(_avatar.transform, path);
                    if (resolved != null) break;
                }
            }
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当オブジェクトをハイライト表示します"), GUILayout.Width(36f)))
                {
                    EditorGUIUtility.PingObject(resolved.gameObject);
                }
            }
        }

        /// <summary>固定するトグル数(表示固定/非表示固定)の予測ノートを描画する。</summary>
        private void DrawToggleProjectedNote()
        {
            if (_toggleGroups == null) return;
            int lockVisible = 0, lockHidden = 0;
            foreach (ToggleGroup group in _toggleGroups)
            {
                if (group == null || string.IsNullOrEmpty(group.id)) continue;
                ToggleGroupChoice entry = FindToggleChoice(group.id);
                ToggleLockChoice choice = entry != null ? entry.choice : ToggleLockChoice.Keep;
                if (choice == ToggleLockChoice.LockVisible) lockVisible++;
                else if (choice == ToggleLockChoice.LockHidden) lockHidden++;
            }
            int lockedTotal = lockVisible + lockHidden;

            EditorGUILayout.Space(2f);
            if (lockedTotal == 0)
            {
                EditorGUILayout.LabelField("固定するトグルはありません(すべてトグル維持)。", _miniWrapLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "固定するトグル数 " + lockedTotal + "(表示固定 " + lockVisible + " / 非表示固定 " + lockHidden + ")。" +
                    "表示固定にしたメッシュは常時表示になり、AAOビルド時に結合対象になります。非表示固定のメッシュは削除されます。",
                    MessageType.None);
            }
        }

        /// <summary>
        /// SkinnedMesh統合(顔以外を1つへ)のUIブロック(PC軽量化ツール版)。QuestConverter と同じ
        /// SkinnedMeshMergePlanner / AAOMeshMergeHelper を使い、モード切替・プレビュー表・想定SMR数を描画する。
        /// PC の Good/Excellent 上限(SkinnedMesh 2/1)対策。表情は分離維持で保たれ、顔以外のブレンドシェイプ・
        /// アニメもAAOのバインディング書き換えで追従する。
        /// </summary>
        private void DrawSkinnedMeshMergeBlockPC()
        {
            if (_settings.skinnedMeshMergeOptOutPaths == null)
                _settings.skinnedMeshMergeOptOutPaths = new List<string>();

            bool aaoInstalled = QuestCompat.FindType("Anatawa12.AvatarOptimizer.TraceAndOptimize") != null;

            EditorGUILayout.LabelField("SkinnedMesh統合(顔以外を1つへ)", EditorStyles.miniBoldLabel);

            if (DrawExplainFoldout("RARA.PCOptimizer.Fold.Explain.MeshMerge"))
            {
                EditorGUILayout.HelpBox(
                    "顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を AAO の Merge Skinned Mesh で1つへ統合し、" +
                    "SkinnedMesh数とマテリアルスロット数を減らします(PC Good上限: SkinnedMesh 2 / Excellent上限 1)。" +
                    "統合はビルド時(NDMF・Play/アップロード)にAAOが行い、ブレンドシェイプの改名・マテリアルスロットの再マップ・" +
                    "アニメーションの再パスもAAOが自動で行うため、表情は分離維持で保たれ、顔以外に残るブレンドシェイプ・" +
                    "マテリアルのアニメも追従して動き続けます。\n" +
                    "ただし表示/非表示の切り替え(トグル)は統合後は効かなくなります: 統合先は常時表示のため、" +
                    "AAOがビルド時にソース側の m_Enabled / m_IsActive アニメを無効化します(統合するとその衣装・装飾は常時表示になります)。" +
                    "切り替えを残したいメッシュは、下の一覧の「このメッシュは統合しない」で統合対象から外してください" +
                    "(常時表示でよいものは「衣装・トグル整理」で表示固定にすると統合できます)。\n" +
                    "注: 反映はビルド時です。保存された複製プレファブは統合前のメッシュ・スロット数のまま表示されます。",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(!aaoInstalled))
            {
                var modeLabels = new[]
                {
                    new GUIContent("統合しない", "従来どおり。SkinnedMesh数・スロット数は削減されません"),
                    new GUIContent("顔以外を統合(推奨)", "顔(ビセーム/まばたき)以外の全SkinnedMeshRendererを1つへ統合します"),
                };
                int current = (int)_settings.mergeSkinnedMeshesMode;
                if (current < 0 || current >= modeLabels.Length) current = 0;
                EditorGUI.BeginChangeCheck();
                int picked = EditorGUILayout.Popup(new GUIContent("SkinnedMesh統合", "顔以外のメッシュを1つへ統合してSMR数・スロット数を削減する"), current, modeLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.mergeSkinnedMeshesMode = (RARA.QuestConverter.SkinnedMeshMergeMode)picked;
                    SaveSettings();
                }
            }

            if (!aaoInstalled)
            {
                EditorGUILayout.HelpBox("SkinnedMesh統合にはAvatarOptimizer(AAO)が必要です。", MessageType.Warning);
                return;
            }

            // 無効時: レンダラーごとに同じ「統合しない」行を並べず、1つの案内 + 目立つ有効化ボタンだけを見せる。
            if (_settings.mergeSkinnedMeshesMode == RARA.QuestConverter.SkinnedMeshMergeMode.None)
            {
                EditorGUILayout.HelpBox(
                    "SkinnedMesh統合は無効です。『顔以外を統合』にすると、顔以外のメッシュ(静的なMeshRenderer含む)を" +
                    "ビルド時に1つへ統合し、SkinnedMesh数を2まで削減できます(表示/非表示トグルは無効化されます)。",
                    MessageType.Info);
                if (GUILayout.Button(new GUIContent("顔以外を統合を有効にする",
                    "顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を1つへ統合するモードに切り替えます"), GUILayout.Height(28f)))
                {
                    _settings.mergeSkinnedMeshesMode = RARA.QuestConverter.SkinnedMeshMergeMode.MergeExceptFace;
                    SaveSettings();
                }
                return;
            }
            if (_avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、統合プレビューを表示します。", EditorStyles.miniLabel);
                return;
            }

            RARA.QuestConverter.SkinnedMeshMergePlan plan = RARA.QuestConverter.SkinnedMeshMergePlanner.BuildPlan(
                _avatar.gameObject, _settings.mergeSkinnedMeshesMode, _settings.skinnedMeshMergeOptOutPaths);

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    string.Format("統合前 SMR {0} → 統合後(概算) {1}", plan.beforeCount, plan.expectedCount),
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.LabelField(
                plan.WillMergeAnything ? "顔以外を1つへ統合します。" : "統合対象が2件未満のため統合は行われません。",
                _miniWrapLabel);
            EditorGUILayout.LabelField(
                "※「統合前」は現在のエディタ上のSkinnedMesh数(ビルド前)です。VRChatの性能パネルはAAOの自動統合後の数を表示するため、"
                + "そちらの数値とは一致しないことがあります。またこのプレビューは変換前のアバターで計算しているため、"
                + "衣装・トグル整理(非表示固定=削除)の後は実際の数が変わることがあります(概算)。",
                _miniWrapLabel);
            EditorGUILayout.LabelField(
                "マテリアルスロットは統合時に同一マテリアルのサブメッシュがビルド時に重複排除されます(アトラスと併用でさらに削減)。",
                _miniWrapLabel);

            foreach (RARA.QuestConverter.SkinnedMeshMergeRow row in plan.rows)
            {
                DrawSkinnedMeshMergeRowPC(row);
            }
        }

        /// <summary>統合プレビュー1行(PC版: 統合する/しない・理由・個別除外トグル・ピン)を描画する。</summary>
        private void DrawSkinnedMeshMergeRowPC(RARA.QuestConverter.SkinnedMeshMergeRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.rendererPath)) return;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string tag = row.willMerge ? "統合する" : (row.isFace ? "顔(分離)" : "統合しない");
                    EditorGUILayout.LabelField(
                        new GUIContent((string.IsNullOrEmpty(row.rendererName) ? row.rendererPath : row.rendererName)
                            + "  [" + tag + "]", "パス: " + row.rendererPath),
                        row.willMerge ? EditorStyles.boldLabel : EditorStyles.label);
                    GUILayout.FlexibleSpace();
                    DrawMergeRowPingButton(row.rendererPath);
                }
                if (!string.IsNullOrEmpty(row.reason))
                {
                    EditorGUILayout.LabelField("理由: " + row.reason, _miniWrapLabel);
                }
                if (!row.isFace && !row.isEditorOnly)
                {
                    bool optedOut = _settings.skinnedMeshMergeOptOutPaths.Contains(row.rendererPath);
                    EditorGUI.BeginChangeCheck();
                    bool newOptedOut = EditorGUILayout.ToggleLeft(
                        new GUIContent("このメッシュは統合しない", "このレンダラーを統合対象から外して分離維持します"),
                        optedOut);
                    if (EditorGUI.EndChangeCheck())
                    {
                        SetSkinnedMeshMergeOptOutPC(row.rendererPath, newOptedOut);
                    }
                }
            }
        }

        /// <summary>統合プレビュー行のピンボタン(該当レンダラーをシーンでハイライト)。</summary>
        private void DrawMergeRowPingButton(string rendererPath)
        {
            Transform resolved = _avatar != null ? QuestCompat.FindByPath(_avatar.transform, rendererPath) : null;
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当メッシュをハイライト表示します"), GUILayout.Width(36f)))
                {
                    EditorGUIUtility.PingObject(resolved.gameObject);
                }
            }
        }

        /// <summary>レンダラーパスを統合除外リスト(skinnedMeshMergeOptOutPaths)へ追加/削除する(変更時のみ保存)。</summary>
        private void SetSkinnedMeshMergeOptOutPC(string rendererPath, bool optOut)
        {
            if (string.IsNullOrEmpty(rendererPath)) return;
            if (_settings.skinnedMeshMergeOptOutPaths == null)
                _settings.skinnedMeshMergeOptOutPaths = new List<string>();
            bool changed;
            if (optOut)
            {
                changed = !_settings.skinnedMeshMergeOptOutPaths.Contains(rendererPath);
                if (changed) _settings.skinnedMeshMergeOptOutPaths.Add(rendererPath);
            }
            else
            {
                changed = _settings.skinnedMeshMergeOptOutPaths.Remove(rendererPath);
            }
            if (changed) SaveSettings();
        }

        // ================================================================
        // 7. PhysBone整理
        // ================================================================
        private void DrawPhysBoneSection()
        {
            if (!DrawSectionFoldout(7, "PhysBone整理",
                "揺れもの(PhysBone)をマージ・削除してコンポーネント数を減らします。PCは超過しても動作しますがランクは下がります。",
                FoldKeyPhysBone))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new PCOptimizeSettings();
                EnsurePhysBoneLists();

                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると、PhysBoneのマージ結果をプレビューして整理できます。", EditorStyles.miniLabel);
                    return;
                }

                if (DrawExplainFoldout("RARA.PCOptimizer.Fold.Explain.PhysBone"))
                {
                    EditorGUILayout.HelpBox(
                        "この一覧はマージ適用後の構成です(グループ行は複数チェーンが1つのPhysBoneに統合されます)。\n" +
                        "PC(Windows)では、PhysBoneがPoor上限を超えてもアップロード・動作は可能で、ランクが下がるだけです" +
                        "(モバイル/Questのように全PhysBone・コンタクト・コンストレイントが停止することはありません)。",
                        MessageType.Info);
                }

                // マージ設定
                EditorGUI.BeginChangeCheck();
                bool merge = EditorGUILayout.ToggleLeft(
                    new GUIContent("PhysBoneをマージして削減",
                        "設定が一致する兄弟チェーンを1つのPhysBoneに統合し、揺れを維持したままコンポーネント数を減らします"),
                    _settings.mergePhysBones);
                bool loose = _settings.physBoneLooseMerge;
                using (new EditorGUI.DisabledScope(!merge))
                {
                    loose = EditorGUILayout.ToggleLeft(
                        new GUIContent("設定が異なるチェーンもマージ(先頭の設定に統一)",
                            "設定が違う兄弟チェーンも先頭メンバーの設定に統一してまとめます(揺れ方が先頭に揃います)"),
                        _settings.physBoneLooseMerge);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.mergePhysBones = merge;
                    _settings.physBoneLooseMerge = loose;
                    SaveSettings();
                    QueuePhysBonePreviewRefresh();
                }

                if (_physBonePreview == null && !_physBonePreviewFailed && !_physBonePreviewQueued)
                {
                    QueuePhysBonePreviewRefresh();
                }

                if (_physBonePreview == null)
                {
                    if (_physBonePreviewFailed)
                    {
                        EditorGUILayout.HelpBox("PhysBoneプレビューの計算に失敗しました(Consoleを確認してください)。", MessageType.Error);
                        if (GUILayout.Button(new GUIContent("再計算", "PhysBoneプレビューを計算し直します"), GUILayout.Width(70f)))
                        {
                            QueuePhysBonePreviewRefresh();
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("プレビューを計算中...", EditorStyles.miniLabel);
                    }
                    return;
                }

                DrawPhysBoneCountHeader();

                var rows = _physBonePreview.rows;
                if (rows == null || rows.Count == 0)
                {
                    EditorGUILayout.LabelField("PhysBoneが見つかりませんでした。", EditorStyles.miniLabel);
                }
                else
                {
                    DrawPhysBoneRowList();
                }

                DrawPhysBoneRemoveList();
            }
        }

        /// <summary>現在→変換後(マージ・削除反映)のPhysBoneコンポーネント数を、PCのGood/Poor閾値とあわせて表示する。</summary>
        private void DrawPhysBoneCountHeader()
        {
            int current = _physBonePreview.currentComponentCount;
            int projected = _settings.mergePhysBones ? _physBonePreview.projectedComponentCount : _physBonePreview.nonMergedComponentCount;
            int goodLimit = PCRankLimits.GetLimit(PCTargetRank.Good, PCRankLimits.PCStat.PhysBoneComponents);
            int poorLimit = PCRankLimits.GetLimit(PCTargetRank.Poor, PCRankLimits.PCStat.PhysBoneComponents);

            var defaultColor = GUI.color;
            Color color = projected <= goodLimit ? UploadOkColor : projected <= poorLimit ? NoteYellowColor : OverLimitColor;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = color;
                EditorGUILayout.LabelField(
                    "現在 " + current + " 個 → 変換後 " + projected + " 個 (Good " + goodLimit + " / Poor " + poorLimit + ")",
                    EditorStyles.boldLabel);
                GUI.color = defaultColor;
                if (GUILayout.Button(new GUIContent("再計算", "現在のアバターと選択でプレビューを計算し直します"), GUILayout.Width(70f)))
                {
                    QueuePhysBonePreviewRefresh();
                }
            }
            EditorGUILayout.LabelField(
                "各行の「残す」を外すと、その揺れものは削除されます(揺れなくなります)。既定はすべて残します。",
                _miniWrapLabel);

            DrawPhysBonePCRankLadder(projected);
        }

        /// <summary>
        /// PC(Windows)のPhysBoneコンポーネント上限ラダー(1行)と、変換後の数から次ランクまでの削減目安を
        /// 色付きで表示する(Excellent/Good=緑 / Medium/Poor=黄 / Poor超過=赤)。数値は PCRankLimits(SDK判定アセットに追随)から取得する。
        /// </summary>
        private void DrawPhysBonePCRankLadder(int projected)
        {
            int excellent = PCRankLimits.GetLimit(PCTargetRank.Excellent, PCRankLimits.PCStat.PhysBoneComponents);
            int good = PCRankLimits.GetLimit(PCTargetRank.Good, PCRankLimits.PCStat.PhysBoneComponents);
            int medium = PCRankLimits.GetLimit(PCTargetRank.Medium, PCRankLimits.PCStat.PhysBoneComponents);
            int poor = PCRankLimits.GetLimit(PCTargetRank.Poor, PCRankLimits.PCStat.PhysBoneComponents);

            EditorGUILayout.LabelField(
                "PC ランク別コンポーネント上限: Excellent " + excellent + " / Good " + good +
                " / Medium " + medium + " / Poor " + poor,
                _miniWrapLabel);

            int[] ceil = { excellent, good, medium, poor };
            string[] names = { "Excellent", "Good", "Medium", "Poor" };
            int achieved = 4; // Poor 超過
            for (int i = 0; i < 4; i++) { if (projected <= ceil[i]) { achieved = i; break; } }

            string guidance;
            Color color;
            if (achieved == 0)
            {
                guidance = "PC: " + names[0] + " 圏内(" + ceil[0] + "個以下)";
                color = UploadOkColor;
            }
            else
            {
                int next = achieved == 4 ? 3 : achieved - 1; // 一つ上のランク(超過時は Poor 復帰)
                guidance = "PC: 現在" + projected + "個 → " + names[next] + "(" + ceil[next] +
                    "個以下)まであと" + (projected - ceil[next]) + "個削減";
                color = achieved <= 1 ? UploadOkColor : achieved == 4 ? OverLimitColor : NoteYellowColor;
            }

            var prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(guidance, EditorStyles.miniBoldLabel);
            GUI.color = prev;
        }

        /// <summary>PhysBone一覧を描画する(10件超は高さ固定スクロールに収める)。</summary>
        private void DrawPhysBoneRowList()
        {
            List<PhysBonePreviewRow> rows = _physBonePreview.rows;
            bool useScroll = rows.Count > 10;
            if (useScroll)
            {
                _physBoneListScroll = EditorGUILayout.BeginScrollView(_physBoneListScroll, GUILayout.Height(300f));
            }
            foreach (PhysBonePreviewRow row in rows)
            {
                if (row == null) continue;
                if (row.isGroup) DrawPhysBoneGroupRow(row);
                else DrawPhysBoneSingleRow(row);
            }
            if (useScroll)
            {
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>グループ行(複数チェーン → 1にマージ)を描画する。「残す」オフで全メンバーを削除指定する。</summary>
        private void DrawPhysBoneGroupRow(PhysBonePreviewRow row)
        {
            List<string> members = row.memberPaths;
            if (members == null || members.Count == 0) return;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool keep = !ContainsAnyPath(_settings.physBoneRemovePaths, members);
                    bool newKeep = EditorGUILayout.ToggleLeft(
                        new GUIContent("残す", "オフにするとこのグループのPhysBoneをすべて削除します(揺れなくなります)"),
                        keep, GUILayout.Width(58f));
                    if (newKeep != keep)
                    {
                        SetPhysBonePathSelection(members, !newKeep);
                    }

                    string parentLabel = string.IsNullOrEmpty(row.parentPath) ? "(アバタールート)" : row.parentPath;
                    EditorGUILayout.LabelField(
                        new GUIContent(parentLabel + " 配下 " + members.Count + "本 → 1 にマージ", "対象Transform数: " + row.transformCount),
                        _wrapLabel);

                    DrawPhysBonePingButton(row.parentPath);
                }

                string foldKey = members[0] ?? (row.parentPath ?? string.Empty);
                bool expanded = _physBoneExpandedGroups.Contains(foldKey);
                bool newExpanded = EditorGUILayout.Foldout(expanded, "マージされるPhysBone (" + members.Count + "本)", true);
                if (newExpanded != expanded)
                {
                    if (newExpanded) _physBoneExpandedGroups.Add(foldKey);
                    else _physBoneExpandedGroups.Remove(foldKey);
                }
                if (newExpanded)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        List<string> labels = row.memberLabels != null && row.memberLabels.Count == members.Count ? row.memberLabels : members;
                        foreach (string label in labels)
                        {
                            EditorGUILayout.LabelField("・" + (label ?? "(不明)"), _miniWrapLabel);
                        }
                    }
                }
            }
        }

        /// <summary>単独行(マージされないPhysBone)を描画する。「残す」オフで削除指定する。</summary>
        private void DrawPhysBoneSingleRow(PhysBonePreviewRow row)
        {
            string path = row.singlePath ?? string.Empty;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool keep = !_settings.physBoneRemovePaths.Contains(path);
                    bool newKeep = EditorGUILayout.ToggleLeft(
                        new GUIContent("残す", "オフにするとこのPhysBoneを削除します(揺れなくなります)"),
                        keep, GUILayout.Width(58f));
                    if (newKeep != keep)
                    {
                        SetPhysBonePathSelection(path, !newKeep);
                    }

                    EditorGUILayout.LabelField(
                        new GUIContent(path.Length == 0 ? "(アバタールート)" : path, "対象Transform数: " + row.transformCount),
                        _wrapLabel);

                    DrawPhysBonePingButton(row.singlePath);
                }
                if (!string.IsNullOrEmpty(row.skipReason))
                {
                    EditorGUILayout.LabelField("マージ対象外: " + row.skipReason, _miniWrapLabel);
                }
            }
        }

        /// <summary>削除指定のうちプレビュー行に現れないもの(改名・別アバター等で見つからないもの)を「戻す」ボタン付きで表示する。</summary>
        private void DrawPhysBoneRemoveList()
        {
            List<string> list = _settings.physBoneRemovePaths;
            if (list == null || list.Count == 0) return;

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("削除指定(変換時に削除され、揺れなくなります)", EditorStyles.miniBoldLabel);
            var defaultColor = GUI.color;
            int removeIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                string path = list[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("・" + (string.IsNullOrEmpty(path) ? "(空)" : path), _miniWrapLabel);
                    if (ResolvePhysBoneIdentityTransform(path) == null)
                    {
                        GUI.color = NoteYellowColor;
                        EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUI.color = defaultColor;
                    }
                    if (GUILayout.Button(new GUIContent("戻す", "削除指定を解除します"), GUILayout.Width(44f)))
                    {
                        removeIndex = i;
                    }
                }
            }
            if (removeIndex >= 0)
            {
                list.RemoveAt(removeIndex);
                SaveSettings();
                QueuePhysBonePreviewRefresh();
            }
        }

        // ================================================================
        // 8. 実行
        // ================================================================
        private void DrawExecuteSection()
        {
            DrawSectionHeader(8, "実行",
                "設定内容を確認して、非破壊で「_Opt」プレファブを生成します(元アバターは変更しません)。");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new PCOptimizeSettings();

                // 生成オプション(プレファブ保存 / AAO Trace and Optimize)
                EditorGUI.BeginChangeCheck();
                bool savePrefab = EditorGUILayout.ToggleLeft(
                    new GUIContent("プレファブアセットとして保存する",
                        "生成した _Opt 複製をプレファブアセットとして出力フォルダに保存します(非破壊)"),
                    _settings.savePrefab);
                bool ensureTao = EditorGUILayout.ToggleLeft(
                    new GUIContent("AAOのTrace and Optimizeを追加(未導入時はスキップ)",
                        "AAO(Avatar Optimizer)がある場合、複製にTrace and Optimizeを追加してビルド時の未使用ボーン削減・メッシュ結合を有効にします"),
                    _settings.ensureTraceAndOptimize);
                bool assignNetworkIds = EditorGUILayout.ToggleLeft(
                    new GUIContent("Network IDを割り当てる(PC/Quest間の揺れ物の掴み同期)",
                        "PhysBoneなどの揺れ物へNetwork IDを割り当て、PC(_Opt)版とQuest(_Quest)版で同じ揺れ物が同じIDになるようにします。" +
                        "他ユーザーから見た掴み/ポーズ/ストレッチの同期がPC/Quest間でズレるのを防ぎます(元アバター基準で採番)。既定はオン"),
                    _settings.assignNetworkIds);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.savePrefab = savePrefab;
                    _settings.ensureTraceAndOptimize = ensureTao;
                    _settings.assignNetworkIds = assignNetworkIds;
                    SaveSettings();
                }

                DrawPreflightBox();

                using (new EditorGUI.DisabledScope(_avatar == null))
                {
                    if (GUILayout.Button(new GUIContent("「_Opt」プレファブを生成",
                        "アバターの複製を作ってPC軽量化を適用します(元アバターは変更されません)"), GUILayout.Height(30f)))
                    {
                        RunOptimize();
                    }
                }
                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("対象アバターを指定すると実行できます。", EditorStyles.miniLabel);
                }

                DrawReport();
            }
        }

        /// <summary>実行前チェック(この最適化で行われることの要約)を表示する。</summary>
        private void DrawPreflightBox()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("この最適化で行われること", EditorStyles.miniBoldLabel);
                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("(アバターを指定すると表示されます)", EditorStyles.miniLabel);
                    return;
                }
                foreach (string line in BuildOptimizeSummaryLines())
                {
                    EditorGUILayout.LabelField("・" + line, _miniWrapLabel);
                }
            }
        }

        /// <summary>設定から「この最適化で行われること」の要約行を作る(実行前チェックと確認ダイアログで共用)。</summary>
        private List<string> BuildOptimizeSummaryLines()
        {
            var lines = new List<string>();
            if (_settings == null) _settings = new PCOptimizeSettings();

            lines.Add("目標ランク: " + TargetRankName(_settings.targetRank));
            lines.Add(_settings.savePrefab
                ? "非破壊で「" + (_avatar != null ? _avatar.gameObject.name : "アバター") + "_Opt」のシーン複製とプレファブを生成"
                : "非破壊で「" + (_avatar != null ? _avatar.gameObject.name : "アバター") + "_Opt」のシーン複製を生成(プレファブ保存なし)");

            int planCount = _settings.texturePlan != null ? _settings.texturePlan.Count : 0;
            lines.Add(planCount > 0 ? "テクスチャ縮小: " + planCount + " 件(縮小コピーを生成)" : "テクスチャ縮小: なし");

            if (_settings.enableAtlas)
            {
                int excl = _settings.atlasExcludeMaterialGuids != null ? _settings.atlasExcludeMaterialGuids.Count : 0;
                string atlasLine = "アトラス統合: 有効(最大 " + _settings.atlasMaxSize + "px";
                if (_settings.atlasColorOnlyMaterials) atlasLine += " / 単色材質も焼き込み";
                if (_settings.atlasBakeEmissionMask) atlasLine += " / エミッション焼き込み";
                if (_settings.atlasIgnoreCull) atlasLine += " / カリング差無視(両面化)";
                if (_settings.atlasOutlineUnifyMode == OutlineUnifyMode.アウトラインを外して統合) atlasLine += " / アウトライン外して統合";
                else if (_settings.atlasOutlineUnifyMode == OutlineUnifyMode.アウトライン付きに統一) atlasLine += " / アウトライン付きに統一";
                if (excl > 0) atlasLine += " / 除外 " + excl + " 件";
                atlasLine += ")";
                lines.Add(atlasLine);
            }
            else
            {
                lines.Add("アトラス統合: 無効");
            }

            if (_settings.toggleChoices != null && _settings.toggleChoices.Count > 0)
            {
                int lockVisible = 0, lockHidden = 0;
                foreach (ToggleGroupChoice c in _settings.toggleChoices)
                {
                    if (c == null) continue;
                    if (c.choice == ToggleLockChoice.LockVisible) lockVisible++;
                    else if (c.choice == ToggleLockChoice.LockHidden) lockHidden++;
                }
                if (lockVisible + lockHidden > 0)
                {
                    lines.Add("トグル整理: 表示で固定 " + lockVisible + " / 非表示で固定 " + lockHidden + "(メッシュ・スロット削減)");
                }
            }

            // SkinnedMesh統合(顔以外を1つへ): 有効かつアバター指定時のみ想定SMR数を示す
            if (_settings.mergeSkinnedMeshesMode != RARA.QuestConverter.SkinnedMeshMergeMode.None && _avatar != null)
            {
                RARA.QuestConverter.SkinnedMeshMergePlan mergePlan = RARA.QuestConverter.SkinnedMeshMergePlanner.BuildPlan(
                    _avatar.gameObject, _settings.mergeSkinnedMeshesMode, _settings.skinnedMeshMergeOptOutPaths);
                if (mergePlan.WillMergeAnything)
                {
                    lines.Add("SkinnedMesh統合: 顔以外を1つへ統合(想定 " + mergePlan.beforeCount + "→" + mergePlan.expectedCount + " ・AAO・ビルド時)");
                }
            }

            var physBoneParts = new List<string>();
            if (_settings.mergePhysBones && _physBonePreview != null &&
                _physBonePreview.projectedComponentCount < _physBonePreview.currentComponentCount)
            {
                physBoneParts.Add("マージで " + _physBonePreview.currentComponentCount + "→" + _physBonePreview.projectedComponentCount);
            }
            int removeCount = _settings.physBoneRemovePaths != null ? _settings.physBoneRemovePaths.Count : 0;
            if (removeCount > 0) physBoneParts.Add("削除 " + removeCount + " 件");
            if (physBoneParts.Count > 0) lines.Add("PhysBone: " + string.Join(" / ", physBoneParts));

            lines.Add(_settings.ensureTraceAndOptimize
                ? "AAO Trace and Optimize: 追加(未導入時はスキップ)"
                : "AAO Trace and Optimize: 追加しない");

            lines.Add("三角数(ポリゴン)は変更しません(削減は外部ツールで)");
            return lines;
        }

        /// <summary>最適化を実行する。診断が未実行・古い場合は先に自動で再診断し、確認後に生成する。</summary>
        private void RunOptimize()
        {
            if (_avatar == null) return;
            if (_settings == null) _settings = new PCOptimizeSettings();

            if (_diagnostics == null || _diagnosisStale)
            {
                RunDiagnostics();
                if (_diagnostics == null) return;
            }

            var summary = new System.Text.StringBuilder();
            foreach (string line in BuildOptimizeSummaryLines()) summary.AppendLine("・" + line);

            bool ok = EditorUtility.DisplayDialog(
                "PC軽量化版の生成",
                "アバター「" + _avatar.gameObject.name + "」のPC軽量化版を生成します。\n\n" +
                "・アバターの複製を作成して最適化します。元のアバターは変更しません。\n\n" +
                "【この最適化で行われること】\n" + summary +
                "\n実行しますか?",
                "生成する", "キャンセル");
            if (!ok) return;

            SaveSettings(false);

            PCDiagResult before = _diagnostics;
            var report = new ConversionReport();
            GameObject optimized = null;
            try
            {
                optimized = PCOptimizer.Optimize(_avatar.gameObject, _settings, report);
            }
            catch (Exception ex)
            {
                report.Error("最適化中に例外が発生しました: " + ex.Message + "\n" + ex.StackTrace);
            }

            _lastReport = report;
            _optimizedAvatar = optimized;
            _beforeDiagnostics = before;
            _afterDiagnostics = null;
            _reportScroll = Vector2.zero;

            if (optimized != null)
            {
                try { _afterDiagnostics = ComputePCPerformance(optimized); }
                catch (Exception ex) { Debug.LogError("[RARA PCOptimizer] 生成物の再診断に失敗しました: " + ex); }

                Selection.activeGameObject = optimized;
                EditorGUIUtility.PingObject(optimized);
            }
            Repaint();
        }

        /// <summary>直近の最適化レポート、生成物、変換前後の比較、Quest変換への受け渡しを表示する。</summary>
        private void DrawReport()
        {
            if (_lastReport == null) return;

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("最適化レポート", EditorStyles.boldLabel, GUILayout.Width(100f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("レポートをコピー", "レポート全文をクリップボードへコピーします"), GUILayout.Width(120f)))
                {
                    EditorGUIUtility.systemCopyBuffer = _lastReport.ToText();
                    ShowNotification(new GUIContent("レポートをコピーしました"));
                }
            }

            if (_optimizedAvatar != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(new GUIContent("生成された _Opt アバター"), _optimizedAvatar, typeof(GameObject), true);
                }
            }

            string summary = _lastReport.HasErrors
                ? "エラーがあります。レポートを確認してください。"
                : (_lastReport.WarningCount > 0 ? "完了しました(警告 " + _lastReport.WarningCount + " 件)。" : "完了しました。");
            EditorGUILayout.HelpBox(summary,
                _lastReport.HasErrors ? MessageType.Error : (_lastReport.WarningCount > 0 ? MessageType.Warning : MessageType.Info));

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                _reportScroll = EditorGUILayout.BeginScrollView(_reportScroll, GUILayout.Height(150f));
                foreach (var entry in _lastReport.entries)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(SeverityIcon(entry.severity), GUILayout.Width(20f), GUILayout.Height(18f));
                        EditorGUILayout.LabelField(entry.message ?? "", _wrapLabel);
                    }
                }
                if (_lastReport.entries.Count == 0)
                {
                    EditorGUILayout.LabelField("(レポート項目なし)", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndScrollView();
            }

            DrawBeforeAfterTable();

            // 生成物をそのままQuest変換フローへ渡す
            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(_optimizedAvatar == null && _avatar == null))
            {
                if (GUILayout.Button(new GUIContent("このアバターをQuest対応で開く",
                    "PC軽量化した結果(または対象アバター)を統合ウィンドウ『RARA アバター軽量化・Quest/iOS対応ツール』のQuest対象で開きます"), GUILayout.Height(24f)))
                {
                    GameObject target = _optimizedAvatar != null ? _optimizedAvatar : (_avatar != null ? _avatar.gameObject : null);
                    OpenQuestConverter(target);
                }
            }
        }

        /// <summary>変換前後のPC基準診断を項目ごとに並べた比較表を描画する(生成物の再診断が取れた場合のみ)。</summary>
        private void DrawBeforeAfterTable()
        {
            if (_beforeDiagnostics == null || _afterDiagnostics == null) return;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("変換前後の比較(PC基準)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(150f));
                    EditorGUILayout.LabelField("変換前", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                    EditorGUILayout.LabelField("変換後", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                    EditorGUILayout.LabelField("目標", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
                }

                var defaultColor = GUI.color;
                for (int i = 0; i < _beforeDiagnostics.rows.Count && i < _afterDiagnostics.rows.Count; i++)
                {
                    PCDiagRow before = _beforeDiagnostics.rows[i];
                    PCDiagRow after = _afterDiagnostics.rows[i];
                    if (before == null || after == null) continue;

                    int limit = PCRankLimits.GetLimit(_settings.targetRank, after.stat);
                    string limitText = after.isMB ? limit + " MB" : limit.ToString("N0");
                    bool afterOver = after.hasValue && after.value > limit + 0.001f;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(before.label, GUILayout.MinWidth(150f));
                        EditorGUILayout.LabelField(before.valueText ?? "-", GUILayout.Width(90f));
                        GUI.color = afterOver ? OverLimitColor : UploadOkColor;
                        EditorGUILayout.LabelField(after.valueText ?? "-", EditorStyles.boldLabel, GUILayout.Width(90f));
                        GUI.color = defaultColor;
                        EditorGUILayout.LabelField(limitText, EditorStyles.miniLabel, GUILayout.Width(70f));
                    }
                }
                GUI.color = defaultColor;

                string beforeRank = DisplayRating(_beforeDiagnostics.overallRating);
                string afterRank = DisplayRating(_afterDiagnostics.overallRating);
                EditorGUILayout.LabelField("総合ランク: " + beforeRank + " → " + afterRank, EditorStyles.miniBoldLabel);
            }
        }

        private static GUIContent SeverityIcon(ConversionReport.Severity severity)
        {
            switch (severity)
            {
                case ConversionReport.Severity.Error: return EditorGUIUtility.IconContent("console.erroricon.sml");
                case ConversionReport.Severity.Warning: return EditorGUIUtility.IconContent("console.warnicon.sml");
                default: return EditorGUIUtility.IconContent("console.infoicon.sml");
            }
        }

        // ================================================================
        // 9. ヘルプ
        // ================================================================
        private void DrawHelpSection()
        {
            if (!DrawSectionFoldout(9, "ヘルプ",
                "このツールでできること/できないことと、PCランクの閾値。",
                FoldKeyHelp))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("このツールで できること", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・非破壊で「_Opt」複製 + プレファブを生成(元アバターは変更しない)\n" +
                    "・テクスチャ縮小でテクスチャメモリを削減\n" +
                    "・アトラス統合でマテリアルスロット数を削減(単色材質は色をアトラスへ焼き込み)\n" +
                    "・衣装トグルの固定でメッシュ・スロットを削減(AAO結合)\n" +
                    "・PhysBoneのマージ・削除でコンポーネント数を削減\n" +
                    "・AAOのTrace and Optimizeでビルド時の未使用ボーン削減・メッシュ結合\n" +
                    "・生成後、そのままQuest変換フローへ受け渡し",
                    _wrapLabel);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("できないこと・割り切ること", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・ポリゴン(三角数)の削減はしない(外部の減面ツールで70,000以下にしてから使う)\n" +
                    "・シェーダーは変えない(見た目はほぼ維持。ただし単色材質のアトラス焼き込みなど、統合部分は近似になる)\n" +
                    "・アトラス統合・単色/エミッションの焼き込みは、ある程度の見た目の近似が起きる",
                    _wrapLabel);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("PC(Windows)ランクの主な閾値(この値以下でそのランク)", EditorStyles.miniBoldLabel);
                DrawHelpThresholdTable();
            }
            EditorGUILayout.Space(8f);
        }

        /// <summary>ヘルプのPC閾値表(PCRankLimitsから引く。診断表と同じ12項目)。</summary>
        private void DrawHelpThresholdTable()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("項目", EditorStyles.miniBoldLabel, GUILayout.MinWidth(150f));
                    EditorGUILayout.LabelField("Excellent", EditorStyles.miniBoldLabel, GUILayout.Width(72f));
                    EditorGUILayout.LabelField("Good", EditorStyles.miniBoldLabel, GUILayout.Width(64f));
                    EditorGUILayout.LabelField("Medium", EditorStyles.miniBoldLabel, GUILayout.Width(64f));
                    EditorGUILayout.LabelField("Poor", EditorStyles.miniBoldLabel, GUILayout.Width(64f));
                }
                foreach (PCStatDef def in StatDefs)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(def.label, GUILayout.MinWidth(150f));
                        EditorGUILayout.LabelField(FormatHelpLimit(def, PCTargetRank.Excellent), EditorStyles.miniLabel, GUILayout.Width(72f));
                        EditorGUILayout.LabelField(FormatHelpLimit(def, PCTargetRank.Good), EditorStyles.miniLabel, GUILayout.Width(64f));
                        EditorGUILayout.LabelField(FormatHelpLimit(def, PCTargetRank.Medium), EditorStyles.miniLabel, GUILayout.Width(64f));
                        EditorGUILayout.LabelField(FormatHelpLimit(def, PCTargetRank.Poor), EditorStyles.miniLabel, GUILayout.Width(64f));
                    }
                }
            }
        }

        private static string FormatHelpLimit(PCStatDef def, PCTargetRank rank)
        {
            int limit = PCRankLimits.GetLimit(rank, def.stat);
            return def.isMB ? limit + " MB" : limit.ToString("N0");
        }

        // ================================================================
        // 検出・提案の再取得ヘルパー(診断実行時 / 遅延予約)
        // ================================================================

        private void EnsureTexturePlanList()
        {
            if (_settings.texturePlan == null) _settings.texturePlan = new List<TextureSizePlanEntry>();
        }

        private void EnsureAtlasExcludeList()
        {
            if (_settings.atlasExcludeMaterialGuids == null) _settings.atlasExcludeMaterialGuids = new List<string>();
        }

        private void EnsureToggleChoicesList()
        {
            if (_settings.toggleChoices == null) _settings.toggleChoices = new List<ToggleGroupChoice>();
        }

        private void EnsurePhysBoneLists()
        {
            if (_settings.physBoneRemovePaths == null) _settings.physBoneRemovePaths = new List<string>();
            if (_settings.physBoneKeepPaths == null) _settings.physBoneKeepPaths = new List<string>();
        }

        /// <summary>テクスチャ削減提案(PCTexturePlanner.BuildSuggestions)を再取得する(READ-ONLY)。</summary>
        private void RefreshTextureSuggestions()
        {
            _textureSuggestions = null;
            _textureSuggestionsFailed = false;
            if (_avatar == null) return;
            try
            {
                _textureSuggestions = PCTexturePlanner.BuildSuggestions(_avatar.gameObject, _settings.targetRank) ?? new List<PCTexturePlanner.PCTextureSuggestion>();
            }
            catch (Exception ex)
            {
                _textureSuggestions = null;
                _textureSuggestionsFailed = true;
                Debug.LogError("[RARA PCOptimizer] テクスチャ削減提案の取得に失敗しました: " + ex);
            }
        }

        private void QueueTextureSuggestionsRefresh()
        {
            if (_avatar == null || _textureSuggestionsQueued) return;
            _textureSuggestionsQueued = true;
            EditorApplication.delayCall += () =>
            {
                _textureSuggestionsQueued = false;
                if (this == null) return;
                if (_avatar == null) return;
                RefreshTextureSuggestions();
                Repaint();
            };
        }

        /// <summary>アトラス統合プレビュー(PCMaterialAtlasser.PreviewPlan)を再計算する(READ-ONLY・ベイクなし)。</summary>
        private void RefreshAtlasPlan()
        {
            _atlasPlan = null;
            _atlasPlanFailed = false;
            if (_avatar == null) return;
            if (_settings == null) _settings = new PCOptimizeSettings();
            try
            {
                _atlasPlan = PCMaterialAtlasser.PreviewPlan(_avatar.gameObject, _settings);
            }
            catch (Exception ex)
            {
                _atlasPlan = null;
                _atlasPlanFailed = true;
                Debug.LogError("[RARA PCOptimizer] アトラス統合プレビューの計算に失敗しました: " + ex);
            }
        }

        private void QueueAtlasPlanRefresh()
        {
            _atlasPlan = null;
            _atlasPlanFailed = false;
            if (_avatar == null || _atlasPlanQueued) return;
            _atlasPlanQueued = true;
            EditorApplication.delayCall += () =>
            {
                _atlasPlanQueued = false;
                if (this == null) return;
                if (_avatar == null) return;
                RefreshAtlasPlan();
                Repaint();
            };
        }

        /// <summary>アバターが使用するマテリアル一覧(重複なし)を集める(アトラス除外指定用。READ-ONLY)。</summary>
        private void RefreshAvatarMaterials()
        {
            _avatarMaterials = null;
            _materialGuidCache.Clear();
            if (_avatar == null) return;
            var seen = new HashSet<Material>();
            var list = new List<Material>();
            foreach (Renderer r in _avatar.gameObject.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (seen.Add(m)) list.Add(m);
                }
            }
            _avatarMaterials = list;
        }

        /// <summary>衣装・トグルグループの検出をやり直す(ToggleConsolidator.DetectToggleGroups。READ-ONLY)。</summary>
        private void RefreshToggleGroups()
        {
            _toggleGroups = null;
            _toggleGroupsFailed = false;
            if (_avatar == null) return;
            try
            {
                _toggleGroups = ToggleConsolidator.DetectToggleGroups(_avatar.gameObject) ?? new List<ToggleGroup>();
            }
            catch (Exception ex)
            {
                _toggleGroups = null;
                _toggleGroupsFailed = true;
                Debug.LogError("[RARA PCOptimizer] トグルの検出に失敗しました: " + ex);
            }
        }

        private void QueueToggleGroupsRefresh()
        {
            if (_avatar == null || _toggleGroupsQueued) return;
            _toggleGroupsQueued = true;
            EditorApplication.delayCall += () =>
            {
                _toggleGroupsQueued = false;
                if (this == null) return;
                if (_avatar == null) return;
                RefreshToggleGroups();
                Repaint();
            };
        }

        /// <summary>PhysBoneマージ・削除のドライランプレビューを再計算する(ComponentRemover.PreviewPhysBoneMerge。READ-ONLY)。</summary>
        private void RefreshPhysBonePreview()
        {
            _physBonePreview = null;
            _physBonePreviewFailed = false;
            if (_avatar == null) return;
            if (_settings == null) _settings = new PCOptimizeSettings();
            EnsurePhysBoneLists();
            try
            {
                _physBonePreview = ComponentRemover.PreviewPhysBoneMerge(
                    _avatar.gameObject,
                    ComponentRemover.CollectPhysBoneTogglePaths(_avatar.gameObject),
                    _settings.physBoneRemovePaths,  // 削除指定はプレビュー行・予測数から除外される(KeepAll方式: 既定は全て残す)
                    new List<string>(),             // マージ除外指定は本ツールでは持たない
                    _settings.physBoneLooseMerge,
                    null);
            }
            catch (Exception ex)
            {
                _physBonePreview = null;
                _physBonePreviewFailed = true;
                Debug.LogError("[RARA PCOptimizer] PhysBoneプレビューの計算に失敗しました: " + ex);
            }
        }

        private void QueuePhysBonePreviewRefresh()
        {
            if (_avatar == null || _physBonePreviewQueued) return;
            _physBonePreviewQueued = true;
            EditorApplication.delayCall += () =>
            {
                _physBonePreviewQueued = false;
                if (this == null) return;
                if (_avatar == null) return;
                RefreshPhysBonePreview();
                Repaint();
            };
        }

        // ================================================================
        // テクスチャ縮小計画(settings.texturePlan)の操作
        // ================================================================

        /// <summary>テクスチャを縮小計画へ登録/縮小する(GUIDで参照。より小さい目標を優先)。変更があればtrueを返す(保存は呼び出し側)。</summary>
        private bool UpsertTexturePlan(Texture2D texture, int targetSize)
        {
            if (texture == null || targetSize <= 0) return false;
            string guid = GetAssetGuid(texture);
            if (string.IsNullOrEmpty(guid)) return false;
            EnsureTexturePlanList();

            foreach (TextureSizePlanEntry entry in _settings.texturePlan)
            {
                if (entry != null && entry.textureGuid == guid)
                {
                    if (targetSize < entry.targetSize) { entry.targetSize = targetSize; return true; }
                    return false; // 既に同じか、より小さい目標がある
                }
            }
            _settings.texturePlan.Add(new TextureSizePlanEntry { textureGuid = guid, targetSize = targetSize });
            return true;
        }

        /// <summary>テクスチャが指定サイズ以下で縮小計画に登録済みか。</summary>
        private bool IsTexturePlanned(Texture2D texture, int targetSize)
        {
            if (texture == null || _settings.texturePlan == null) return false;
            string guid = GetAssetGuid(texture);
            if (string.IsNullOrEmpty(guid)) return false;
            foreach (TextureSizePlanEntry entry in _settings.texturePlan)
            {
                if (entry != null && entry.textureGuid == guid && entry.targetSize <= targetSize) return true;
            }
            return false;
        }

        // ================================================================
        // 衣装・トグル選択(settings.toggleChoices)の操作
        // ================================================================
        private ToggleGroupChoice FindToggleChoice(string groupId)
        {
            if (_settings == null || string.IsNullOrEmpty(groupId)) return null;
            EnsureToggleChoicesList();
            foreach (ToggleGroupChoice entry in _settings.toggleChoices)
            {
                if (entry != null && entry.groupId == groupId) return entry;
            }
            return null;
        }

        /// <summary>groupId のトグル選択を choice に設定する(保存はしない)。変更があればtrueを返す。Keepはエントリ削除。</summary>
        private bool ApplyToggleChoice(string groupId, ToggleLockChoice choice)
        {
            if (_settings == null || string.IsNullOrEmpty(groupId)) return false;
            EnsureToggleChoicesList();

            ToggleGroupChoice entry = null;
            foreach (ToggleGroupChoice candidate in _settings.toggleChoices)
            {
                if (candidate != null && candidate.groupId == groupId) { entry = candidate; break; }
            }
            if (choice == ToggleLockChoice.Keep)
            {
                if (entry != null) { _settings.toggleChoices.Remove(entry); return true; }
                return false;
            }
            if (entry == null)
            {
                _settings.toggleChoices.Add(new ToggleGroupChoice { groupId = groupId, choice = choice });
                return true;
            }
            if (entry.choice != choice) { entry.choice = choice; return true; }
            return false;
        }

        private void SetToggleChoice(string groupId, ToggleLockChoice choice)
        {
            if (ApplyToggleChoice(groupId, choice)) SaveSettings();
        }

        private void LockAllTogglesToCurrentState()
        {
            if (_toggleGroups == null) return;
            bool changed = false;
            foreach (ToggleGroup group in _toggleGroups)
            {
                if (group == null || string.IsNullOrEmpty(group.id)) continue;
                ToggleLockChoice desired = group.defaultActive ? ToggleLockChoice.LockVisible : ToggleLockChoice.LockHidden;
                if (ApplyToggleChoice(group.id, desired)) changed = true;
            }
            if (changed) SaveSettings();
        }

        private void ResetAllToggleChoices()
        {
            if (_toggleGroups == null) return;
            bool changed = false;
            foreach (ToggleGroup group in _toggleGroups)
            {
                if (group == null || string.IsNullOrEmpty(group.id)) continue;
                if (ApplyToggleChoice(group.id, ToggleLockChoice.Keep)) changed = true;
            }
            if (changed) SaveSettings();
        }

        // ================================================================
        // PhysBone識別パス(settings.physBoneRemovePaths)の操作・解決
        // ================================================================

        /// <summary>単一の識別パスを削除指定リストへ追加/削除する(変更時は保存+プレビュー再計算)。</summary>
        private void SetPhysBonePathSelection(string path, bool selected)
        {
            EnsurePhysBoneLists();
            bool changed;
            if (selected)
            {
                changed = !_settings.physBoneRemovePaths.Contains(path);
                if (changed) _settings.physBoneRemovePaths.Add(path);
            }
            else
            {
                changed = _settings.physBoneRemovePaths.Remove(path);
            }
            if (changed)
            {
                SaveSettings();
                QueuePhysBonePreviewRefresh();
            }
        }

        /// <summary>複数の識別パスをまとめて削除指定リストへ追加/削除する(変更時は保存+プレビュー再計算)。</summary>
        private void SetPhysBonePathSelection(List<string> paths, bool selected)
        {
            EnsurePhysBoneLists();
            bool changed = false;
            foreach (string path in paths)
            {
                if (path == null) continue;
                if (selected)
                {
                    if (!_settings.physBoneRemovePaths.Contains(path)) { _settings.physBoneRemovePaths.Add(path); changed = true; }
                }
                else
                {
                    if (_settings.physBoneRemovePaths.Remove(path)) changed = true;
                }
            }
            if (changed)
            {
                SaveSettings();
                QueuePhysBonePreviewRefresh();
            }
        }

        /// <summary>listがpathsのいずれかを含むか。</summary>
        private static bool ContainsAnyPath(List<string> list, List<string> paths)
        {
            foreach (string path in paths)
            {
                if (path != null && list.Contains(path)) return true;
            }
            return false;
        }

        /// <summary>識別パス(相対パス。同一GameObjectに複数ある場合は "#序数" 付き)からTransformを解決する(見つからなければnull)。</summary>
        private Transform ResolvePhysBoneIdentityTransform(string identityPath)
        {
            if (_avatar == null || identityPath == null) return null;
            Transform direct = QuestCompat.FindByPath(_avatar.transform, identityPath);
            if (direct != null) return direct;
            int hash = identityPath.LastIndexOf('#');
            if (hash < 0) return null;
            return QuestCompat.FindByPath(_avatar.transform, identityPath.Substring(0, hash));
        }

        /// <summary>識別パス(またはグループの親パス)の指すGameObjectをピン表示するボタンを描画する。</summary>
        private void DrawPhysBonePingButton(string identityPath)
        {
            Transform resolved = ResolvePhysBoneIdentityTransform(identityPath);
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当オブジェクトをハイライト表示します"), GUILayout.Width(36f)))
                {
                    EditorGUIUtility.PingObject(resolved.gameObject);
                }
            }
        }

        // ================================================================
        // 共通ユーティリティ
        // ================================================================

        /// <summary>アセットのGUIDを返す(アセット化されていない場合は空文字。結果はキャッシュ)。</summary>
        private string GetAssetGuid(UnityEngine.Object asset)
        {
            if (asset == null) return string.Empty;
            if (asset is Material mat && _materialGuidCache.TryGetValue(mat, out string cached)) return cached;

            string result = string.Empty;
            if (EditorUtility.IsPersistent(asset) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long _) &&
                !string.IsNullOrEmpty(guid))
            {
                result = guid;
            }
            if (asset is Material m) _materialGuidCache[m] = result;
            return result;
        }
    }
}
#endif
