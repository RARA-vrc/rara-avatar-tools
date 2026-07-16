// RARA Quest Converter - メインウィンドウのセクション描画(partial)
// QuestConverterWindow.cs から分割したセクション3〜10の描画と、
// マテリアル個別設定(materialOverrides)・変換サマリーのヘルパーを持つ。
//   3.マテリアル設定 / 4.PhysBone設定 / 5.衣装・トグル整理 / 6.アトラス統合 / 7.メッシュ削減(AAO連携) / 8.Quest除外 / 9.変換設定 / 10.ポリゴン削減 / 11.実行 / 12.ヘルプ
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace RARA.QuestConverter
{
    public partial class QuestConverterWindow
    {
        /// <summary>
        /// PC最適化のみモード(ConversionMode.ConsolidateOnly)のとき、シェーダー/テクスチャ/アトラス変換を
        /// 伴うセクションに「このモードではスキップされる」注記を1行出す(それ以外のモードでは何も描画しない)。
        /// セクション自体は残したまま、黄色のミニ注記で視覚的に控えめに知らせる(仕様: セクションは削除しない)。
        /// </summary>
        private void DrawConsolidateOnlySkipNote(string what)
        {
            if (_settings == null || _settings.conversionMode != ConversionMode.ConsolidateOnly) return;
            var prev = GUI.color;
            GUI.color = NoteYellowColor;
            EditorGUILayout.LabelField("※ PC最適化のみモードのため、" + what + "はスキップされます。", _miniWrapLabel);
            GUI.color = prev;
        }

        // マテリアル個別設定のポップアップ表示名(MaterialOverrideの並び順 Auto..Keep と一致させること)
        private static readonly GUIContent[] OverrideModeLabels =
        {
            new GUIContent("自動(推奨)", "診断結果から最適な変換方法を自動で選びます。迷ったらこのままでOK"),
            new GUIContent("Toon Standard", "VRChat/Mobile/Toon Standard へ変換(不透明。影ランプ・ノーマル・エミッション対応)"),
            new GUIContent("Toon Lit", "VRChat/Mobile/Toon Lit へ変換(最軽量。陰影はテクスチャへベイク)"),
            new GUIContent("パーティクル加算", "Particles/Additive へ変換。黒が透明になる加算合成(光り物・ホロ向け)"),
            new GUIContent("パーティクル乗算", "Particles/Multiply へ変換。白が透明になる乗算合成(チーク・頬染め向け)"),
            new GUIContent("非表示", "Quest版では不可視マテリアルに差し替えて見えなくします"),
            new GUIContent("変換しない", "元のマテリアルのまま残します(非対応シェーダーのままだとアップロード不可の原因になります)"),
        };

        // 透過マテリアルの既定処理ドロップダウンの表示名(TransparentHandlingの並び順 Emulate, Hide, Opaque と一致させること)
        private static readonly GUIContent[] TransparentHandlingLabels =
        {
            new GUIContent("自動で半透明を再現(推奨)",
                "乗算・加算パーティクルシェーダーで半透明を近似します。設定不要でチーク・涙・ガラス系が表現されます(加算は暗所で光ります)"),
            new GUIContent("非表示にする(最軽量)",
                "透過(アルファブレンド)マテリアルをQuest版で非表示化します。最も軽量です"),
            new GUIContent("不透明に変換",
                "透過を無視して不透明シェーダー(Toon Standard/Lit)へ変換します(従来のスキップ相当)"),
        };

        // マテリアル状態バッジの色(ダーク/ライト両スキンで読める中間トーン)
        private static readonly Color BadgeTransparentColor = new Color(0.95f, 0.55f, 0.25f);
        private static readonly Color BadgeCutoutColor = new Color(0.85f, 0.72f, 0.25f);
        private static readonly Color BadgeParticleColor = new Color(0.3f, 0.72f, 0.85f);
        private static readonly Color BadgeAnimationColor = new Color(0.5f, 0.62f, 0.95f);
        private static readonly Color BadgeTmpColor = new Color(0.8f, 0.5f, 0.85f);
        private static readonly Color BadgeBrokenColor = new Color(0.95f, 0.35f, 0.35f);
        private static readonly Color BadgeMobileColor = new Color(0.35f, 0.78f, 0.42f);
        private static readonly Color BadgeComponentColor = new Color(0.95f, 0.55f, 0.7f);   // メニュー/ギミック参照

        // ================================================================
        // セクション3: マテリアル設定
        // ================================================================
        private void DrawMaterialSection()
        {
            if (!DrawSectionFoldout(3, "マテリアル設定",
                "マテリアルごとの変換方法。通常は自動でOK。透過(チーク/ガラス等)の見せ方をここで調整。",
                FoldKeyMaterial))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)

                DrawConsolidateOnlySkipNote("シェーダー/マテリアル変換(以下の設定)");

                // 透過マテリアルの既定処理(再現/非表示/不透明)のドロップダウンと表情デカールのプレビュー。
                // アバター未選択でも設定できるよう、下の「アバター未選択」ガードより前に描画する。
                DrawTransparentHandlingBlock();

                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると、マテリアルごとの変換方法を設定できます。", EditorStyles.miniLabel);
                    return;
                }
                if (_materialPreview == null)
                {
                    EditorGUILayout.LabelField("診断を実行するとマテリアル一覧が表示されます。", EditorStyles.miniLabel);
                    return;
                }
                if (_materialPreview.Count == 0)
                {
                    EditorGUILayout.LabelField("変換対象のマテリアルが見つかりませんでした。", EditorStyles.miniLabel);
                    return;
                }

                EditorGUILayout.LabelField(
                    "マテリアルごとの変換方法です(全 " + _materialPreview.Count + " 件)。通常は「自動(推奨)」のままで問題ありません。",
                    _miniWrapLabel);

                foreach (MaterialPreviewRow row in _materialPreview)
                {
                    DrawMaterialRow(row);
                }

                // 初めての人向け: 透過マテリアルをどのモードにすべきかの目安(長いので「説明」foldoutへ)
                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.Material"))
                {
                    EditorGUILayout.HelpBox(
                        "透過マテリアルは既定(自動で半透明を再現)では、明るさから乗算/加算を自動で選んで近似します。" +
                        "個別に上書きしたいときの目安:\n" +
                        "・チーク・頬染め・肌の重ね → 「パーティクル乗算」が近い見た目になります\n" +
                        "・光り物・ホログラム → 「パーティクル加算」\n" +
                        "・ガラス → 「パーティクル乗算/加算」で近似、消したいなら「非表示」、" +
                        "板でも良ければ「Toon Standard(不透明になります)」",
                        MessageType.Info);
                }
            }
        }

        // ================================================================
        // セクション3: 表情デカール(チーク/涙/アイハイライト)の自動非表示化
        // ================================================================

        /// <summary>表情デカール検出プレビューのフォールドアウト開閉状態のEditorPrefsキー。</summary>
        private const string ExpressionDecalFoldPrefKey = "RARA.QuestConverter.Fold.ExpressionDecals";

        /// <summary>
        /// 透過(アルファブレンド)マテリアルの既定処理(再現/非表示/不透明)を選ぶドロップダウンと、
        /// その補足、表情デカール(チーク/涙/アイハイライト)の検出プレビューを描画する。
        /// 「再現」= 乗算/加算パーティクルシェーダーで半透明を近似(既定・推奨。チーク・涙・ガラス系が表現される)。
        /// 「非表示」= 最軽量。「不透明」= 従来のスキップ相当。髪・大型メッシュの透過は保護され常に不透明変換になる。
        /// ドロップダウンはアバター未選択でも操作でき、検出プレビューは診断済みのときだけ表示する。
        /// </summary>
        private void DrawTransparentHandlingBlock()
        {
            if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                // 透過マテリアルの既定処理(再現/非表示/不透明)
                int handlingIndex = Mathf.Clamp((int)_settings.transparentHandling, 0, TransparentHandlingLabels.Length - 1);
                EditorGUI.BeginChangeCheck();
                int newHandlingIndex = EditorGUILayout.Popup(
                    new GUIContent("透過マテリアルの既定処理",
                        "アルファブレンド透過マテリアルをQuest版でどう扱うかの既定です。個別のマテリアルは下の一覧で上書きできます"),
                    handlingIndex, TransparentHandlingLabels);
                if (EditorGUI.EndChangeCheck() && newHandlingIndex != handlingIndex)
                {
                    _settings.transparentHandling = (TransparentHandling)newHandlingIndex;
                    SaveSettings();                // 変換内容に影響する(診断は古い扱いになる)
                    QueueMaterialPreviewRefresh(); // 予定・アトラス適性が変わるためプレビューを取り直す
                }

                // 1〜2行の補足
                EditorGUILayout.LabelField(
                    "再現=乗算・加算パーティクルシェーダーで近似(設定不要でチーク・涙・ガラス系が表現される。加算は暗所で光る)。" +
                    "非表示が最も軽量。髪・大型メッシュは常に不透明変換。",
                    _miniWrapLabel);

                // 表情デカール(チーク/涙/アイハイライト)の検出プレビュー(診断済みのときだけ)
                if (_avatar != null && _diagnostics != null)
                {
                    DrawExpressionDecalPreview();
                }
            }
        }

        /// <summary>
        /// 検出された表情デカール(AvatarQuestConverter.PreviewExpressionDecals の結果)の一覧を
        /// フォールドアウトで表示する。既定処理(transparentHandling)に応じて、これらが乗算/加算で
        /// 再現されるのか非表示化されるのかを1行で示す。1件も無ければ検出なしのInfoを出す(nullは未診断扱い)。
        /// </summary>
        private void DrawExpressionDecalPreview()
        {
            // Emulate(既定)ではデカールも乗算/加算で再現され非表示化されないため、
            // PreviewExpressionDecals は空リストを返す契約になっている(件数では検出有無を判定できない)。
            // このモードでは件数ベースの「検出なし」表示は誤解を招くので、専用の案内を出す。
            if (_settings.transparentHandling == TransparentHandling.Emulate)
            {
                EditorGUILayout.HelpBox(
                    "既定処理『自動で半透明を再現』のため、表情デカール(チーク/涙/アイハイライト)は" +
                    "乗算/加算パーティクルで再現されます(マテリアル一覧の『半透明を再現』を参照)。",
                    MessageType.Info);
                return;
            }

            List<DecalOverlayRow> rows = _expressionDecals;
            if (rows == null)
            {
                EditorGUILayout.LabelField("(診断後に表示されます)", EditorStyles.miniLabel);
                return;
            }
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("表情デカール(チーク/涙/アイハイライト)は検出されませんでした。", MessageType.Info);
                return;
            }

            // 既定処理でこれらのデカールがどう扱われるかを1行で示す(非表示・不透明では隠す=板化を防ぐ)。
            // Emulate は上で早期returnしているため、ここに来るのは Hide / Opaque のみ。
            string fate;
            switch (_settings.transparentHandling)
            {
                case TransparentHandling.Hide:
                    fate = "既定処理『非表示にする』により非表示化されます(顔本体・目・眉は残ります)。";
                    break;
                default: // Opaque
                    fate = "『不透明に変換』でも板状に見えてしまうため、表情デカールは非表示化されます(顔本体・目・眉は残ります)。";
                    break;
            }
            EditorGUILayout.LabelField(fate, _miniWrapLabel);

            bool expanded = GetFoldPref(ExpressionDecalFoldPrefKey, true);
            bool newExpanded = EditorGUILayout.Foldout(expanded,
                "検出された表情デカール (" + rows.Count + ")", true);
            if (newExpanded != expanded) SetFoldPref(ExpressionDecalFoldPrefKey, newExpanded);
            if (!newExpanded) return;

            using (new EditorGUI.IndentLevelScope())
            {
                foreach (DecalOverlayRow row in rows)
                {
                    DrawExpressionDecalRow(row);
                }
            }
        }

        /// <summary>表情デカール1件分の行(マテリアル参照+スロット / レンダラーパス / 理由 / ピン)を描画する。</summary>
        private void DrawExpressionDecalRow(DecalOverlayRow row)
        {
            if (row == null) return;
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
                    DrawExpressionDecalPingButton(row.rendererPath);
                }
                if (!string.IsNullOrEmpty(row.rendererPath))
                {
                    EditorGUILayout.LabelField(row.rendererPath, _miniWrapLabel);
                }
                if (!string.IsNullOrEmpty(row.reason))
                {
                    EditorGUILayout.LabelField("理由: " + row.reason, _miniWrapLabel);
                }
            }
        }

        /// <summary>デカールが乗るレンダラー(rendererPath)のGameObjectをピン表示するボタンを描画する。</summary>
        private void DrawExpressionDecalPingButton(string rendererPath)
        {
            Transform resolved = _avatar != null ? QuestCompat.FindByPath(_avatar.transform, rendererPath) : null;
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当メッシュ(デカールが乗るレンダラー)をハイライト表示します"), GUILayout.Width(36f)))
                {
                    EditorGUIUtility.PingObject(resolved.gameObject);
                }
            }
        }

        /// <summary>
        /// 表情デカール検出プレビュー(_expressionDecals)を再取得する(診断実行時に呼ばれる。読み取り専用検出)。
        /// アバター未選択・検出失敗時はnullにする(呼び出し側はnullを未診断扱いでガードする)。
        /// </summary>
        private void RefreshExpressionDecals()
        {
            _expressionDecals = null;
            if (_avatar == null) return;
            try
            {
                _expressionDecals = AvatarQuestConverter.PreviewExpressionDecals(_avatar, _settings);
            }
            catch (Exception ex)
            {
                _expressionDecals = null;
                Debug.LogError("[RARA QuestConverter] 表情デカール検出の取得に失敗しました: " + ex);
            }
        }

        /// <summary>マテリアル1件分の行(ObjectField+状態バッジ / 変換方法+予定 / アトラス統合)を描画する。</summary>
        private void DrawMaterialRow(MaterialPreviewRow row)
        {
            if (row == null) return;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                if (row.material == null)
                {
                    EditorGUILayout.LabelField("(欠落マテリアル)", EditorStyles.miniLabel);
                    return;
                }

                // 1行目: マテリアル参照 + 状態バッジ
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true)) // 読み取り専用表示
                    {
                        EditorGUILayout.ObjectField(row.material, typeof(Material), false, GUILayout.Width(150f));
                    }
                    DrawMaterialBadges(row);
                    GUILayout.FlexibleSpace();
                }

                // 2行目: 変換方法ポップアップ + 予定される処理
                string guid;
                bool hasGuid = TryGetMaterialGuid(row.material, out guid);
                MaterialOverrideEntry entry = FindOverrideEntry(row.material, false);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!hasGuid))
                    {
                        MaterialOverride mode = entry != null ? entry.mode : MaterialOverride.Auto;
                        int index = Mathf.Clamp((int)mode, 0, OverrideModeLabels.Length - 1);
                        int newIndex = EditorGUILayout.Popup(index, OverrideModeLabels, GUILayout.Width(145f));
                        if (newIndex != index)
                        {
                            entry = FindOverrideEntry(row.material, true);
                            if (entry != null)
                            {
                                entry.mode = (MaterialOverride)newIndex;
                                // 再診断を待たずに「予定」列だけ即時更新する(手動指定は結果が確定しているため)
                                row.plannedAction = PlannedActionLabel(entry.mode);
                                RemoveOverrideEntryIfDefault(entry);
                                SaveSettings(); // 診断は古い扱いになる
                                // アトラス適性(atlasEligible)も変換方法で変わるため、描画ループの外で
                                // プレビュー行を再取得する(非表示/変換しない等に変えた直後の
                                // 「アトラス統合」トグルが操作可能なまま残らないように)
                                QueueMaterialPreviewRefresh();
                            }
                        }
                    }
                    EditorGUILayout.LabelField("予定: " + (row.plannedAction ?? ""), _miniWrapLabel);
                }
                if (!hasGuid)
                {
                    EditorGUILayout.LabelField("(アセット化されていないマテリアルのため個別設定できません)", EditorStyles.miniLabel);
                }

                // 3行目: アトラス統合トグル(アトラス有効時のみ表示)
                if (_settings.enableAtlas)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool include = row.atlasEligible && !(entry != null && entry.excludeFromAtlas);
                        string tooltip = row.atlasEligible
                            ? "このマテリアルをアトラス統合(1枚のテクスチャへの統合)の対象に含めます"
                            : "対象外: " + (string.IsNullOrEmpty(row.atlasIneligibleReason) ? "条件を満たしていません" : row.atlasIneligibleReason);
                        using (new EditorGUI.DisabledScope(!row.atlasEligible || !hasGuid))
                        {
                            bool newInclude = EditorGUILayout.ToggleLeft(
                                new GUIContent("アトラス統合", tooltip), include, GUILayout.Width(100f));
                            if (row.atlasEligible && hasGuid && newInclude != include)
                            {
                                entry = FindOverrideEntry(row.material, true);
                                if (entry != null)
                                {
                                    entry.excludeFromAtlas = !newInclude;
                                    RemoveOverrideEntryIfDefault(entry);
                                    SaveSettings();
                                }
                            }
                        }
                        if (!row.atlasEligible)
                        {
                            EditorGUILayout.LabelField(
                                "(対象外: " + (string.IsNullOrEmpty(row.atlasIneligibleReason) ? "条件を満たしていません" : row.atlasIneligibleReason) + ")",
                                _miniWrapLabel);
                        }
                        else
                        {
                            GUILayout.FlexibleSpace();
                        }
                    }
                }
            }
        }

        /// <summary>マテリアルの状態を短い色付きバッジで表示する(透過/カットアウト/パーティクル/アニメ使用/TMP/破損/対応済)。</summary>
        private void DrawMaterialBadges(MaterialPreviewRow row)
        {
            if (row.transparency == QuestCompat.TransparencyClass.Transparent)
            {
                DrawBadge("透過", BadgeTransparentColor, "アルファブレンド透過。Questのメッシュでは透過表示できません(パーティクル系か非表示で対応)");
            }
            else if (row.transparency == QuestCompat.TransparencyClass.Cutout)
            {
                DrawBadge("カットアウト", BadgeCutoutColor, "アルファ抜き。Questでは不透明として変換されます(Toon Standardにカットアウトはありません)");
            }
            if (row.usedByParticle) DrawBadge("パーティクル", BadgeParticleColor, "パーティクル系レンダラーが使用しています");
            if (row.usedByAnimation) DrawBadge("アニメ使用", BadgeAnimationColor, "アニメーションのマテリアル差し替えで参照されています");
            if (row.usedByComponent)
            {
                string componentTip = "Renderer以外のコンポーネント(MA Material Setter等)から参照されているマテリアル。メニューで切り替わる衣装・目・表情差分など";
                if (!string.IsNullOrEmpty(row.componentSource)) componentTip += "\n参照元: " + row.componentSource;
                DrawBadge("メニュー/ギミック参照", BadgeComponentColor, componentTip);
            }
            if (row.isTMP) DrawBadge("TMP", BadgeTmpColor, "TextMeshPro用マテリアル(変換不可。Quest除外を推奨)");
            if (row.isBrokenShader) DrawBadge("破損", BadgeBrokenColor, "シェーダーが欠落または壊れています(修正してから再変換してください)");
            if (row.isMobileAlready) DrawBadge("対応済", BadgeMobileColor, "既にQuest対応シェーダーです(変換不要)");
        }

        /// <summary>短い色付きバッジを1つ描画する。</summary>
        private void DrawBadge(string text, Color color, string tooltip)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUILayout.Label(new GUIContent(text, tooltip), _badgeLabel, GUILayout.ExpandWidth(false));
            GUI.color = prev;
        }

        /// <summary>手動指定モードの「予定される処理」表示文字列。</summary>
        private static string PlannedActionLabel(MaterialOverride mode)
        {
            switch (mode)
            {
                case MaterialOverride.ToonStandard: return "Toon Standard へ変換(手動指定)";
                case MaterialOverride.ToonLit: return "Toon Lit へ変換(手動指定)";
                case MaterialOverride.ParticleAdditive: return "パーティクル加算へ変換(手動指定)";
                case MaterialOverride.ParticleMultiply: return "パーティクル乗算へ変換(手動指定)";
                case MaterialOverride.Hide: return "非表示化(手動指定)";
                case MaterialOverride.Keep: return "変換しない(手動指定)";
                default: return "自動判定(再診断で更新されます)";
            }
        }

        // ================================================================
        // マテリアル個別設定(materialOverrides)のヘルパー
        // ================================================================

        /// <summary>
        /// マテリアルプレビューを再取得する(診断実行時に呼ばれる)。
        /// あわせてGUIDキャッシュのクリアと、解決できなくなったoverrideエントリの掃除を行う。
        /// </summary>
        private void RefreshMaterialPreview()
        {
            _materialGuidCache.Clear();
            _materialPreview = null;
            if (_avatar == null) return;
            try
            {
                PruneOverrideEntries();
                _materialPreview = AvatarQuestConverter.PreviewMaterials(_avatar, _settings);
            }
            catch (Exception ex)
            {
                _materialPreview = null;
                Debug.LogError("[RARA QuestConverter] マテリアルプレビューの取得に失敗しました: " + ex);
            }
        }

        /// <summary>GUIDが解決できない(削除された等)materialOverridesエントリを取り除く。</summary>
        private void PruneOverrideEntries()
        {
            if (_settings == null || _settings.materialOverrides == null) return;
            var list = _settings.materialOverrides;
            bool removedAny = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                MaterialOverrideEntry entry = list[i];
                bool broken = entry == null || string.IsNullOrEmpty(entry.materialGuid);
                if (!broken)
                {
                    string path = AssetDatabase.GUIDToAssetPath(entry.materialGuid);
                    broken = string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<Material>(path) == null;
                }
                if (broken)
                {
                    list.RemoveAt(i);
                    removedAny = true;
                }
            }
            if (removedAny)
            {
                // 解決できないエントリは変換時も無視されるため、削除しても動作は変わらない(staleにしない)
                SaveSettings(false);
            }
        }

        /// <summary>
        /// マテリアルのGUIDを取得する(アセット化されたマテリアルのみ。結果はキャッシュされる)。
        /// シーン内マテリアル等、GUIDを持たないものはfalseを返す。
        /// </summary>
        private bool TryGetMaterialGuid(Material material, out string guid)
        {
            guid = null;
            if (material == null) return false;
            if (_materialGuidCache.TryGetValue(material, out guid))
            {
                return !string.IsNullOrEmpty(guid);
            }

            string resolved = null;
            long localId;
            string candidate;
            if (EditorUtility.IsPersistent(material) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out candidate, out localId) &&
                !string.IsNullOrEmpty(candidate))
            {
                resolved = candidate;
            }
            _materialGuidCache[material] = resolved;
            guid = resolved;
            return !string.IsNullOrEmpty(resolved);
        }

        /// <summary>
        /// マテリアルに対応するmaterialOverridesエントリを探す。
        /// createIfMissing=trueで見つからない場合に新規作成して登録する(GUIDが取れない場合はnull)。
        /// </summary>
        private MaterialOverrideEntry FindOverrideEntry(Material material, bool createIfMissing)
        {
            if (_settings == null) return null;
            if (_settings.materialOverrides == null) _settings.materialOverrides = new List<MaterialOverrideEntry>(); // 旧設定JSON読込対策

            string guid;
            if (!TryGetMaterialGuid(material, out guid)) return null;

            foreach (MaterialOverrideEntry entry in _settings.materialOverrides)
            {
                if (entry != null && entry.materialGuid == guid) return entry;
            }
            if (!createIfMissing) return null;

            var created = new MaterialOverrideEntry { materialGuid = guid };
            _settings.materialOverrides.Add(created);
            return created;
        }

        /// <summary>既定値(自動+アトラス対象)に戻ったエントリをリストから取り除く(設定JSONの肥大化防止)。</summary>
        private void RemoveOverrideEntryIfDefault(MaterialOverrideEntry entry)
        {
            if (entry == null || _settings == null || _settings.materialOverrides == null) return;
            if (entry.mode == MaterialOverride.Auto && !entry.excludeFromAtlas)
            {
                _settings.materialOverrides.Remove(entry);
            }
        }

        // ================================================================
        // セクション4: PhysBone 設定(揺れもの)
        // ================================================================

        /// <summary>
        /// PhysBone設定セクション。変換を実行せずにマージ後(POST-MERGE)のPhysBoneレイアウト
        /// (ComponentRemover.PreviewPhysBoneMerge のドライラン)をプレビューし、
        /// 単位(グループ/単独)ごとに 稼働(残す)/削除 と マージしない を選択できる。
        /// 選択制(OptIn・既定)では physBoneKeepPaths に「残す」指定を保存し、未選択のものは変換時に削除する。
        /// 従来方式(KeepAll)では physBoneRemovePaths に「削除」指定を保存する。
        /// いずれも physBoneNoMergePaths と併せて変換時に適用される。
        /// </summary>
        private void DrawPhysBoneSection()
        {
            if (!DrawSectionFoldout(4, "PhysBone 設定(揺れもの)",
                "髪・スカート等の揺れものをQuestの上限内に。残す揺れものを選び、まとめて削減できます。",
                FoldKeyPhysBone))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)
                EnsurePhysBoneSettingsLists();

                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると、PhysBoneのマージ結果のプレビューと稼働の選択ができます。", EditorStyles.miniLabel);
                    return;
                }

                // (e) この一覧がマージ適用後の構成であることを明示する(説明は「説明」foldoutへ)
                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.PhysBone"))
                {
                    EditorGUILayout.HelpBox(
                        "この一覧はマージ適用後の構成です(グループ行は複数チェーンが1つのPhysBoneに統合されます)。",
                        MessageType.None);
                }

                // 未計算(セクション初回表示・アバター変更直後など)なら遅延計算を1回だけ予約する
                // (ドライランのため軽量だが、毎OnGUIの再計算は行わない)
                if (_physBonePreview == null && !_physBonePreviewFailed && !_physBonePreviewRefreshQueued)
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

                EnsurePhysBoneRowDisplays();

                // 変換後に残る(稼働する)コンポーネント数を選択方式・選択内容・マージ設定を反映して算出する
                int projectedKept = ComputePhysBoneProjectedKeptCount();

                // (d) Poor上限超過の常時赤警告(セクション最上部に固定)
                if (projectedKept > QuestLimits.PoorPhysBoneComponents)
                {
                    EditorGUILayout.HelpBox(
                        "Poor上限(" + QuestLimits.PoorPhysBoneComponents + ")超過: この状態でアップロードすると" +
                        "モバイルでは全てのPhysBone・コンタクト・コンストレイントが無効化されます。" +
                        QuestLimits.PoorPhysBoneComponents + "以下にしてください。",
                        MessageType.Error);
                }

                DrawPhysBoneCountHeader(projectedKept);
                DrawPhysBoneModeAndTools();

                var rows = _physBonePreview.rows;
                if (rows == null || rows.Count == 0)
                {
                    EditorGUILayout.LabelField("PhysBoneが見つかりませんでした。", EditorStyles.miniLabel);
                }
                else
                {
                    DrawPhysBoneRowList();
                }

                DrawPhysBoneSavedSelections();
            }
        }

        // Quest(Android)PhysBoneコンポーネントのランク別上限のうち Excellent/Good は公開定数が無い
        // (QuestLimits は Medium/Poor のみ公開)。SDK 3.10.4 の StatsLevels/Android/{Excellent,Good}_Android.asset の
        // physBone.componentCount を直接引用してハードコードする。Medium/Poor は QuestLimits の公開定数を使う。
        private const int QuestExcellentPhysBoneComponents = 0;
        private const int QuestGoodPhysBoneComponents = 4;

        /// <summary>
        /// 現在→変換後(稼働)のPhysBoneコンポーネント数を上限とあわせて色付き表示する
        /// (Medium以下=緑 / Poor以下=黄 / Poor超過=赤)。Poor超過時の常時赤警告はセクション最上部に別途固定される。
        /// projected は選択方式(OptIn/KeepAll)・選択内容・マージ設定を反映した「変換後に残る数」。
        /// </summary>
        private void DrawPhysBoneCountHeader(int projected)
        {
            int current = _physBonePreview.currentComponentCount;
            var defaultColor = GUI.color;
            Color color = projected <= QuestLimits.MediumPhysBoneComponents ? UploadOkColor
                : projected <= QuestLimits.PoorPhysBoneComponents ? NoteYellowColor
                : OverLimitColor;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = color;
                EditorGUILayout.LabelField(
                    "現在 " + current + " 個 → 変換後 " + projected + " 個 (Medium " + QuestLimits.MediumPhysBoneComponents +
                    " / Poor " + QuestLimits.PoorPhysBoneComponents + ")",
                    EditorStyles.boldLabel);
                GUI.color = defaultColor;
                if (GUILayout.Button(new GUIContent("再計算", "現在のアバターと選択内容でプレビューを計算し直します"), GUILayout.Width(70f)))
                {
                    QueuePhysBonePreviewRefresh();
                }
            }

            if (!_settings.mergePhysBones)
            {
                GUI.color = NoteYellowColor;
                EditorGUILayout.LabelField(
                    "※「9. 変換設定」の「PhysBoneをマージして削減」がオフのため、マージは実行されません(グループも各チェーンが個別に残ります)。",
                    _miniWrapLabel);
                GUI.color = defaultColor;
            }

            DrawPhysBoneRankLadder(projected);
        }

        /// <summary>
        /// Quest(Android)のPhysBoneコンポーネント上限ラダー(1行)と、変換後の数から次ランクまでの
        /// 削減目安を色付きで表示する(Excellent/Good=緑 / Medium/Poor=黄 / Poor超過=赤)。
        /// Medium/Poor は QuestLimits の公開定数、Excellent/Good は公開定数が無いため Android StatsLevels 実値を用いる。
        /// </summary>
        private void DrawPhysBoneRankLadder(int projected)
        {
            EditorGUILayout.LabelField(
                "Quest ランク別コンポーネント上限: Excellent " + QuestExcellentPhysBoneComponents +
                " / Good " + QuestGoodPhysBoneComponents +
                " / Medium " + QuestLimits.MediumPhysBoneComponents +
                " / Poor " + QuestLimits.PoorPhysBoneComponents,
                _miniWrapLabel);

            int[] ceil = { QuestExcellentPhysBoneComponents, QuestGoodPhysBoneComponents,
                QuestLimits.MediumPhysBoneComponents, QuestLimits.PoorPhysBoneComponents };
            string[] names = { "Excellent", "Good", "Medium", "Poor" };
            int achieved = 4; // Poor 超過
            for (int i = 0; i < 4; i++) { if (projected <= ceil[i]) { achieved = i; break; } }

            string guidance;
            Color color;
            if (achieved == 0)
            {
                guidance = "Quest: " + names[0] + " 圏内(" + ceil[0] + "個以下)";
                color = UploadOkColor;
            }
            else
            {
                int next = achieved == 4 ? 3 : achieved - 1; // 一つ上のランク(超過時は Poor 復帰)
                guidance = "Quest: 現在" + projected + "個 → " + names[next] + "(" + ceil[next] +
                    "個以下)まであと" + (projected - ceil[next]) + "個削減";
                color = achieved <= 1 ? UploadOkColor : achieved == 4 ? OverLimitColor : NoteYellowColor;
            }

            var prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(guidance, EditorStyles.miniBoldLabel);
            GUI.color = prev;
        }

        /// <summary>
        /// グループ行(同じ親を持つチェーン群 → 1本へマージ)を描画する。
        /// OptInモード: 「稼働」オフ(既定)=削除、オン=全メンバーを physBoneKeepPaths に登録して残す。
        /// KeepAllモード: 「残す」オフ=全メンバーを physBoneRemovePaths に登録して削除。
        /// 「マージしない」オンは両モード共通で全メンバーをマージ除外にする。
        /// ルーズマージで作られた行には「設定差異あり」バッジを出す(ツールチップに差異メンバー一覧)。
        /// </summary>
        private void DrawPhysBoneGroupRow(PhysBoneRowDisplay display)
        {
            PhysBonePreviewRow row = display.row;
            List<string> members = row.memberPaths;
            if (members == null || members.Count == 0) return; // メンバーなしのグループ行は表示しない(保険)

            bool optIn = _settings.physBoneSelectionMode == PhysBoneSelectionMode.OptIn;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (optIn)
                    {
                        // 稼働(残す): 既定オフ。メンバーの誰かがkeep指定ならオン表示(オンで全メンバーをkeep登録)
                        bool kept = ContainsAnyPath(_settings.physBoneKeepPaths, members);
                        bool newKept = EditorGUILayout.ToggleLeft(
                            new GUIContent("稼働", "オンにするとこのグループのPhysBoneを残します(揺れます)。オフのままだと変換時に削除されます"),
                            kept, GUILayout.Width(58f));
                        if (newKept != kept)
                        {
                            SetPhysBoneKeepSelection(members, newKept);
                        }
                    }
                    else
                    {
                        // 残す: メンバーの誰かが削除指定されていればオフ表示(オンに戻すと全メンバーの削除指定を解除)
                        bool keep = !ContainsAnyPath(_settings.physBoneRemovePaths, members);
                        bool newKeep = EditorGUILayout.ToggleLeft(
                            new GUIContent("残す", "オフにするとこのグループのPhysBoneをすべて削除します(揺れなくなります)"),
                            keep, GUILayout.Width(58f));
                        if (newKeep != keep)
                        {
                            SetPhysBonePathSelection(_settings.physBoneRemovePaths, members, !newKeep);
                        }
                    }

                    EditorGUILayout.LabelField(display.mainLabel, _wrapLabel);

                    // ルーズマージ(設定差異あり)のバッジ
                    if (display.diffBadge != null)
                    {
                        var prevColor = GUI.color;
                        GUI.color = BadgeSettingsDiffColor;
                        GUILayout.Label(display.diffBadge, _badgeLabel, GUILayout.ExpandWidth(false));
                        GUI.color = prevColor;
                    }

                    bool noMerge = ContainsAllPaths(_settings.physBoneNoMergePaths, members);
                    bool newNoMerge = EditorGUILayout.ToggleLeft(
                        new GUIContent("マージしない", "オンにするとこのグループをマージ対象から外します(各PhysBoneはそのまま個別に残ります)"),
                        noMerge, GUILayout.Width(92f));
                    if (newNoMerge != noMerge)
                    {
                        SetPhysBonePathSelection(_settings.physBoneNoMergePaths, members, newNoMerge);
                    }

                    DrawPhysBonePingButton(display.pingPath);
                }

                // メンバー一覧のフォールドアウト(開閉状態は先頭メンバーの識別パスをキーに保持)
                bool expanded = _physBoneExpandedGroups.Contains(display.foldKey);
                bool newExpanded = EditorGUILayout.Foldout(expanded, display.foldLabel, true);
                if (newExpanded != expanded)
                {
                    if (newExpanded) _physBoneExpandedGroups.Add(display.foldKey);
                    else _physBoneExpandedGroups.Remove(display.foldKey);
                }
                if (newExpanded)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        // ラベルが取れない・数が合わない場合は識別パスで代用する
                        List<string> labels = row.memberLabels != null && row.memberLabels.Count == members.Count
                            ? row.memberLabels
                            : members;
                        foreach (string label in labels)
                        {
                            EditorGUILayout.LabelField("・" + (label ?? "(不明)"), _miniWrapLabel);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 単独行(マージされないPhysBone)を描画する。対象外の理由(skipReason)をグレー表示し、
        /// マージ除外指定済みのものには解除用トグルを出す(解除すると次回の再計算でマージ候補に戻る)。
        /// OptInモードは「稼働」(physBoneKeepPaths)、KeepAllモードは「残す」(physBoneRemovePaths)で選択する。
        /// </summary>
        private void DrawPhysBoneSingleRow(PhysBoneRowDisplay display)
        {
            PhysBonePreviewRow row = display.row;
            string path = row.singlePath ?? string.Empty;
            bool optIn = _settings.physBoneSelectionMode == PhysBoneSelectionMode.OptIn;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (optIn)
                    {
                        bool kept = _settings.physBoneKeepPaths.Contains(path);
                        bool newKept = EditorGUILayout.ToggleLeft(
                            new GUIContent("稼働", "オンにするとこのPhysBoneを残します(揺れます)。オフのままだと変換時に削除されます"),
                            kept, GUILayout.Width(58f));
                        if (newKept != kept)
                        {
                            SetPhysBoneKeepSelection(path, newKept);
                        }
                    }
                    else
                    {
                        bool keep = !_settings.physBoneRemovePaths.Contains(path);
                        bool newKeep = EditorGUILayout.ToggleLeft(
                            new GUIContent("残す", "オフにするとこのPhysBoneを削除します(揺れなくなります)"),
                            keep, GUILayout.Width(58f));
                        if (newKeep != keep)
                        {
                            SetPhysBonePathSelection(_settings.physBoneRemovePaths, path, !newKeep);
                        }
                    }

                    EditorGUILayout.LabelField(display.mainLabel, _wrapLabel);

                    // マージ除外指定されている場合のみ解除用トグルを出す(それ以外の単独行はマージ除外しても意味がない)
                    if (_settings.physBoneNoMergePaths.Contains(path))
                    {
                        bool newNoMerge = EditorGUILayout.ToggleLeft(
                            new GUIContent("マージしない", "オフに戻すと次回の再計算で再びマージ候補になります"),
                            true, GUILayout.Width(92f));
                        if (!newNoMerge)
                        {
                            SetPhysBonePathSelection(_settings.physBoneNoMergePaths, path, false);
                        }
                    }

                    DrawPhysBonePingButton(display.pingPath);
                }
                if (!string.IsNullOrEmpty(display.skipLabel))
                {
                    EditorGUILayout.LabelField(display.skipLabel, _miniWrapLabel);
                }
            }
        }

        /// <summary>
        /// 保存済みの選択のうちプレビュー行に現れないものを一覧表示する。
        /// ・KeepAllモード: 削除指定(physBoneRemovePaths)の全件(削除指定分はプレビュー行・予測数に含まれない
        ///   ため「戻す」で復元できるようにする)。
        /// ・OptInモード: 稼働指定(physBoneKeepPaths)のうち現在のアバターで解決できないもの(「解除」で削除)。
        /// ・両モード共通: マージ除外指定(physBoneNoMergePaths)のうち現在のアバターで見つからないもの。
        /// いずれも見つからないもの(アバターを変えた・改名した等)は黄色の「(見つかりません)」を添える。
        /// </summary>
        private void DrawPhysBoneSavedSelections()
        {
            if (_settings.physBoneSelectionMode == PhysBoneSelectionMode.KeepAll)
            {
                DrawPhysBoneRemoveList();
            }
            else
            {
                DrawPhysBoneStaleKeepList();
            }
            DrawPhysBoneStaleNoMergeList();
        }

        /// <summary>削除指定の全エントリを表示する(戻すボタン付き。見つからないものは黄色表示)。</summary>
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
                    if (!PhysBoneIdentityPathExists(path))
                    {
                        GUI.color = NoteYellowColor;
                        EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUI.color = defaultColor;
                    }
                    if (GUILayout.Button(new GUIContent("戻す", "削除指定を解除します(プレビューの一覧に戻ります)"), GUILayout.Width(44f)))
                    {
                        removeIndex = i;
                    }
                }
            }
            if (removeIndex >= 0)
            {
                list.RemoveAt(removeIndex);
                SaveSettings();                 // 選択の保存(診断は古い扱いになる)
                QueuePhysBonePreviewRefresh();  // 変更後のレイアウトを再計算
            }
        }

        /// <summary>マージ除外指定のうち現在のアバターで見つからないエントリを表示する(解除ボタン付き)。</summary>
        private void DrawPhysBoneStaleNoMergeList()
        {
            List<string> list = _settings.physBoneNoMergePaths;
            if (list == null || list.Count == 0) return;

            var defaultColor = GUI.color;
            int removeIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                string path = list[i];
                if (PhysBoneIdentityPathExists(path)) continue; // 見つかるものは単独行(手動でマージ除外)で表示済み
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("マージ除外指定: " + (string.IsNullOrEmpty(path) ? "(空)" : path), _miniWrapLabel);
                    GUI.color = NoteYellowColor;
                    EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                    GUI.color = defaultColor;
                    if (GUILayout.Button(new GUIContent("解除", "現在のアバターで見つからないこの保存済み指定を削除します"), GUILayout.Width(44f)))
                    {
                        removeIndex = i;
                    }
                }
            }
            if (removeIndex >= 0)
            {
                list.RemoveAt(removeIndex);
                SaveSettings();                 // 選択の保存(診断は古い扱いになる)
                QueuePhysBonePreviewRefresh();  // 変更後のレイアウトを再計算
            }
        }

        // ================================================================
        // セクション4: 選択方式・優先度自動選択・行表示キャッシュ(オプトイン対応の追加分)
        // ================================================================

        /// <summary>選択方式トグル(GUILayout.Toolbar)の表示ラベル(PhysBoneSelectionModeの並び OptIn, KeepAll と一致)。</summary>
        private static readonly GUIContent[] PhysBoneModeLabels =
        {
            new GUIContent("選んだものだけ残す(推奨)", "既定オフ(全停止)から、稼働させるPhysBoneだけを選びます。Poor上限内に絞りやすい方式です"),
            new GUIContent("外したものだけ削除", "従来方式。チェックを外したPhysBoneだけを削除し、残りはすべて残します"),
        };

        /// <summary>ルーズマージ(設定差異あり)バッジの色(ダーク/ライト両スキンで読める中間トーン)。</summary>
        private static readonly Color BadgeSettingsDiffColor = new Color(0.9f, 0.7f, 0.3f);

        /// <summary>PhysBone一覧を高さ固定スクロールに切り替える行数の閾値(これを超えたらスクロールビューに収める)。</summary>
        private const int PhysBoneListScrollThreshold = 10;

        /// <summary>PhysBone一覧スクロールビューの固定高さ(px)。100本超でもセクションが縦に伸びすぎないようにする。</summary>
        private const float PhysBoneListMaxHeight = 300f;

        /// <summary>
        /// セクション4のPhysBone行の表示キャッシュ(プレビュー構築時に1度だけ作る)。
        /// ラベル・ツールチップ・バッジ内容を保持し、100本超でも毎フレームの文字列生成を避ける。
        /// </summary>
        private sealed class PhysBoneRowDisplay
        {
            public PhysBonePreviewRow row;   // 対応するプレビュー行(選択状態の読み書きに使う)
            public bool isGroup;
            public GUIContent mainLabel;     // 親配下N本→1にマージ / 単独の識別パス(ツールチップに対象Transform数)
            public string foldLabel;         // グループ行のフォールドアウト見出し
            public string foldKey;           // グループ行の開閉状態キー(先頭メンバー識別パス)
            public GUIContent diffBadge;     // ルーズマージ(設定差異あり)バッジ。該当しなければnull
            public string skipLabel;         // 単独行のマージ対象外理由の表示文字列。無ければnull
            public string pingPath;          // ピン表示の対象パス(グループ=親 / 単独=識別パス)
        }

        /// <summary>選択方式トグル・ルーズマージトグル・優先度自動選択ボタン・自動選択の案内を描画する。</summary>
        private void DrawPhysBoneModeAndTools()
        {
            bool optIn = _settings.physBoneSelectionMode == PhysBoneSelectionMode.OptIn;

            // 選択方式トグル(切り替えても両方の選択リストは保持される。プレビューはremovePathsの扱いが変わるため再計算)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("選択方式", "残すPhysBoneの決め方。切り替えても両方の選択は保持されます"), GUILayout.Width(58f));
                int modeIndex = optIn ? 0 : 1;
                int newModeIndex = GUILayout.Toolbar(modeIndex, PhysBoneModeLabels);
                if (newModeIndex != modeIndex)
                {
                    _settings.physBoneSelectionMode = newModeIndex == 0 ? PhysBoneSelectionMode.OptIn : PhysBoneSelectionMode.KeepAll;
                    SaveSettings();                 // 選択方式の変更は結果に影響する(診断は古い扱いになる)
                    QueuePhysBonePreviewRefresh();  // OptInは全ユニットを一覧に出す / KeepAllは削除指定分を除くため再計算
                }
            }
            EditorGUILayout.LabelField(
                optIn
                    ? "チェックした揺れものだけを残します(既定は全停止)。100本超でも上限内に絞りやすい方式です。"
                    : "チェックを外した揺れものだけを削除します。残りはすべて残ります。",
                _miniWrapLabel);

            // ルーズマージトグル(変更でプレビュー再計算)
            EditorGUI.BeginChangeCheck();
            bool loose = EditorGUILayout.ToggleLeft(
                new GUIContent("設定が異なるチェーンもマージ(先頭の設定に統一)",
                    "同じ親を持つ揺れものを、設定が違っていても先頭メンバーの設定に統一して1つにまとめます。" +
                    "アニメ制御・パラメータ使用・カーブ設定などの安全ゲートは維持されます(それらは引き続きマージされません)"),
                _settings.physBoneLooseMerge);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.physBoneLooseMerge = loose;
                SaveSettings();
                QueuePhysBonePreviewRefresh();  // マージ結果が変わるため再計算(looseMergeをプレビューへ反映)
            }

            // 優先度で自動選択(OptInモードのみ)
            if (optIn)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("優先度で自動選択",
                        "髪・胸などの名前から優先度の高い揺れものを、Poor上限(" + QuestLimits.PoorPhysBoneComponents + ")内で自動的に稼働へ設定します。\n" +
                        "Quest ランク別コンポーネント上限: Excellent " + QuestExcellentPhysBoneComponents +
                        " / Good " + QuestGoodPhysBoneComponents + " / Medium " + QuestLimits.MediumPhysBoneComponents +
                        " / Poor " + QuestLimits.PoorPhysBoneComponents),
                        GUILayout.Height(22f)))
                    {
                        AutoSelectPhysBonesByPriority();
                    }
                    GUILayout.FlexibleSpace();
                }

                // 未選択かつ優先度キーワード該当行があるときは自動選択を勧める(勝手には適用しない)
                bool keepEmpty = _settings.physBoneKeepPaths == null || _settings.physBoneKeepPaths.Count == 0;
                if (keepEmpty && PhysBonePreviewHasPriorityRow())
                {
                    EditorGUILayout.HelpBox(
                        "まだ稼働する揺れものが選ばれていません(このままでは全て停止します)。" +
                        "「優先度で自動選択」を押すと、髪・胸などおすすめをPoor上限内で選べます。",
                        MessageType.Info);
                }
            }
        }

        /// <summary>PhysBone一覧を描画する(閾値を超えたら高さ固定スクロールに収める)。行ラベルはキャッシュ済み。</summary>
        private void DrawPhysBoneRowList()
        {
            List<PhysBoneRowDisplay> displays = _physBoneRowDisplays;
            if (displays == null) return; // EnsurePhysBoneRowDisplays 済みのため通常起きない(保険)

            bool useScroll = displays.Count > PhysBoneListScrollThreshold;
            if (useScroll)
            {
                _physBoneListScroll = EditorGUILayout.BeginScrollView(_physBoneListScroll, GUILayout.Height(PhysBoneListMaxHeight));
            }
            foreach (PhysBoneRowDisplay display in displays)
            {
                if (display == null || display.row == null) continue;
                if (display.isGroup) DrawPhysBoneGroupRow(display);
                else DrawPhysBoneSingleRow(display);
            }
            if (useScroll)
            {
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>
        /// 変換後に残る(稼働する)PhysBoneコンポーネント数を選択方式に応じて算出する。
        /// KeepAll: マージ有効なら予測数、無効ならマージ候補数(従来と同じ)。
        /// OptIn: 稼働選択されたユニット数(マージ有効ならグループは1、無効ならメンバー数)。
        /// </summary>
        private int ComputePhysBoneProjectedKeptCount()
        {
            if (_physBonePreview == null) return 0;
            if (_settings.physBoneSelectionMode == PhysBoneSelectionMode.KeepAll)
            {
                return _settings.mergePhysBones
                    ? _physBonePreview.projectedComponentCount
                    : _physBonePreview.nonMergedComponentCount;
            }

            // OptIn: 稼働選択された行だけを数える
            List<string> keep = _settings.physBoneKeepPaths;
            if (keep == null || keep.Count == 0) return 0;
            var keepSet = new HashSet<string>(keep);
            List<PhysBonePreviewRow> rows = _physBonePreview.rows;
            if (rows == null) return 0;

            int count = 0;
            foreach (PhysBonePreviewRow row in rows)
            {
                if (row == null) continue;
                if (row.isGroup)
                {
                    int keptMembers = 0;
                    if (row.memberPaths != null)
                    {
                        foreach (string member in row.memberPaths)
                        {
                            if (member != null && keepSet.Contains(member)) keptMembers++;
                        }
                    }
                    if (keptMembers == 0) continue;
                    // マージ有効ならグループは1本に統合される。無効なら各メンバーがそのまま残る。
                    count += _settings.mergePhysBones ? 1 : keptMembers;
                }
                else if (row.singlePath != null && keepSet.Contains(row.singlePath))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>プレビュー行に優先度キーワード該当(priorityScore != int.MaxValue)が1つでもあるか。</summary>
        private bool PhysBonePreviewHasPriorityRow()
        {
            if (_physBonePreview == null || _physBonePreview.rows == null) return false;
            foreach (PhysBonePreviewRow row in _physBonePreview.rows)
            {
                if (row != null && row.priorityScore != int.MaxValue) return true;
            }
            return false;
        }

        /// <summary>
        /// 名前の優先度(髪・胸など)が高い順に、Poor上限内で稼働するPhysBoneを自動選択する(OptInモード)。
        /// 既存の稼働選択(physBoneKeepPaths)は置き換える。確認ダイアログで選択内容(名前と個数)を提示する。
        /// </summary>
        private void AutoSelectPhysBonesByPriority()
        {
            if (_physBonePreview == null || _physBonePreview.rows == null) return;

            var sorted = new List<PhysBonePreviewRow>();
            foreach (PhysBonePreviewRow row in _physBonePreview.rows)
            {
                if (row != null) sorted.Add(row);
            }
            // 優先度昇順(小さいほど高優先)。同点はグループ優先、さらにメンバー数が多い順。
            sorted.Sort((a, b) =>
            {
                int c = a.priorityScore.CompareTo(b.priorityScore);
                if (c != 0) return c;
                if (a.isGroup != b.isGroup) return a.isGroup ? -1 : 1;
                int ac = a.memberPaths != null ? a.memberPaths.Count : 0;
                int bc = b.memberPaths != null ? b.memberPaths.Count : 0;
                return bc.CompareTo(ac);
            });

            var selectedRows = new List<PhysBonePreviewRow>();
            int kept = 0;
            foreach (PhysBonePreviewRow row in sorted)
            {
                // 稼働に加算されるコンポーネント数。マージ有効ならグループは1本に統合されるが、
                // マージ無効ならグループの各メンバーがそのまま残るため、メンバー数分を数える。
                int rowCost = 1;
                if (!_settings.mergePhysBones && row.isGroup && row.memberPaths != null)
                {
                    rowCost = Mathf.Max(1, row.memberPaths.Count);
                }
                if (kept + rowCost > QuestLimits.PoorPhysBoneComponents) break; // 上限を超える行は選ばない
                selectedRows.Add(row);
                kept += rowCost;
            }

            if (selectedRows.Count == 0)
            {
                EditorUtility.DisplayDialog("優先度で自動選択", "選択できるPhysBoneがありませんでした。", "OK");
                return;
            }

            var summary = new System.Text.StringBuilder();
            foreach (PhysBonePreviewRow row in selectedRows)
            {
                summary.AppendLine("・" + DescribePhysBoneRowForDialog(row, _settings.mergePhysBones));
            }
            // 実際に稼働として残るコンポーネント数(kept)を提示する。マージ無効時はグループの
            // メンバーが各1本残るため、選択行数(selectedRows.Count)ではなくコンポーネント数で表す。
            bool ok = EditorUtility.DisplayDialog("優先度で自動選択",
                "名前の優先度が高い順に、Poor上限(" + QuestLimits.PoorPhysBoneComponents + ")内で次の " +
                selectedRows.Count + " グループ/単独(稼働コンポーネント " + kept +
                " 個)を稼働に設定します(現在の稼働選択は置き換えられます):\n\n" +
                summary +
                "\n設定しますか?",
                "設定する", "キャンセル");
            if (!ok) return;

            _settings.physBoneKeepPaths.Clear();
            foreach (PhysBonePreviewRow row in selectedRows)
            {
                if (row.isGroup)
                {
                    if (row.memberPaths != null)
                    {
                        foreach (string member in row.memberPaths)
                        {
                            if (!string.IsNullOrEmpty(member) && !_settings.physBoneKeepPaths.Contains(member))
                            {
                                _settings.physBoneKeepPaths.Add(member);
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(row.singlePath) && !_settings.physBoneKeepPaths.Contains(row.singlePath))
                {
                    _settings.physBoneKeepPaths.Add(row.singlePath);
                }
            }
            SaveSettings();                 // 稼働選択の保存(診断は古い扱いになる)
            QueuePhysBonePreviewRefresh();  // 表示を更新
        }

        /// <summary>自動選択の確認ダイアログ用に、行を「名前(個数)」形式の1行文字列で表す。</summary>
        private static string DescribePhysBoneRowForDialog(PhysBonePreviewRow row, bool mergeEnabled)
        {
            if (row.isGroup)
            {
                string parentLabel = string.IsNullOrEmpty(row.parentPath) ? "(アバタールート)" : row.parentPath;
                int memberCount = row.memberPaths != null ? row.memberPaths.Count : 0;
                // マージ有効なら1個へ統合、無効なら各メンバーがそのまま残る。
                string suffix = mergeEnabled ? "(マージ後1個)" : "(マージ無効: " + memberCount + "個残存)";
                return parentLabel + " 配下 " + memberCount + "本" + suffix;
            }
            return string.IsNullOrEmpty(row.singlePath) ? "(アバタールート)" : row.singlePath;
        }

        /// <summary>行表示キャッシュを(未作成・件数不一致なら)構築する。プレビュー構築時にも呼ばれる。</summary>
        private void EnsurePhysBoneRowDisplays()
        {
            if (_physBonePreview == null)
            {
                _physBoneRowDisplays = null;
                return;
            }
            int rowCount = _physBonePreview.rows != null ? _physBonePreview.rows.Count : 0;
            if (_physBoneRowDisplays != null && _physBoneRowDisplays.Count == rowCount) return;
            _physBoneRowDisplays = BuildPhysBoneRowDisplays(_physBonePreview.rows);
        }

        /// <summary>プレビュー行から表示キャッシュ(ラベル・ツールチップ・バッジ)を作る。毎フレームの文字列生成を避けるため。</summary>
        private static List<PhysBoneRowDisplay> BuildPhysBoneRowDisplays(List<PhysBonePreviewRow> rows)
        {
            var list = new List<PhysBoneRowDisplay>();
            if (rows == null) return list;

            foreach (PhysBonePreviewRow row in rows)
            {
                if (row == null)
                {
                    list.Add(new PhysBoneRowDisplay { row = null }); // 行数を合わせるためのプレースホルダー(描画時にスキップ)
                    continue;
                }

                var display = new PhysBoneRowDisplay { row = row, isGroup = row.isGroup };
                string transformTip = "対象Transform数: " + row.transformCount;
                if (row.isGroup)
                {
                    List<string> members = row.memberPaths;
                    int memberCount = members != null ? members.Count : 0;
                    string parentLabel = string.IsNullOrEmpty(row.parentPath) ? "(アバタールート)" : row.parentPath;
                    display.mainLabel = new GUIContent(parentLabel + " 配下 " + memberCount + "本 → 1 にマージ", transformTip);
                    display.foldLabel = "マージされるPhysBone (" + memberCount + "本)";
                    display.foldKey = (members != null && memberCount > 0 ? members[0] : null) ?? (row.parentPath ?? string.Empty);
                    display.pingPath = row.parentPath;
                    if (row.looseMerged && row.settingsDiffMembers != null && row.settingsDiffMembers.Count > 0)
                    {
                        string tip = "設定が異なるチェーンを先頭の設定に統一してマージします。設定が異なるメンバー:\n・" +
                                     string.Join("\n・", row.settingsDiffMembers);
                        display.diffBadge = new GUIContent("設定差異あり", tip);
                    }
                }
                else
                {
                    string path = row.singlePath ?? string.Empty;
                    display.mainLabel = new GUIContent(path.Length == 0 ? "(アバタールート)" : path, transformTip);
                    display.pingPath = row.singlePath;
                    if (!string.IsNullOrEmpty(row.skipReason))
                    {
                        display.skipLabel = "マージ対象外: " + row.skipReason;
                    }
                }
                list.Add(display);
            }
            return list;
        }

        /// <summary>単一の識別パスを稼働選択リスト(physBoneKeepPaths)へ追加/削除する。keepPathsはプレビューへ影響しないため再計算しない。</summary>
        private void SetPhysBoneKeepSelection(string path, bool keep)
        {
            bool changed;
            if (keep)
            {
                changed = !_settings.physBoneKeepPaths.Contains(path);
                if (changed) _settings.physBoneKeepPaths.Add(path);
            }
            else
            {
                changed = _settings.physBoneKeepPaths.Remove(path);
            }
            if (changed)
            {
                SaveSettings(); // 稼働選択の保存(診断は古い扱いになる)
            }
        }

        /// <summary>複数の識別パスをまとめて稼働選択リスト(physBoneKeepPaths)へ追加/削除する。</summary>
        private void SetPhysBoneKeepSelection(List<string> paths, bool keep)
        {
            bool changed = false;
            foreach (string path in paths)
            {
                if (path == null) continue;
                if (keep)
                {
                    if (!_settings.physBoneKeepPaths.Contains(path)) { _settings.physBoneKeepPaths.Add(path); changed = true; }
                }
                else
                {
                    if (_settings.physBoneKeepPaths.Remove(path)) changed = true;
                }
            }
            if (changed)
            {
                SaveSettings(); // 稼働選択の保存(診断は古い扱いになる)
            }
        }

        /// <summary>稼働指定(physBoneKeepPaths)のうち現在のアバターで見つからないエントリを表示する(解除ボタン付き)。</summary>
        private void DrawPhysBoneStaleKeepList()
        {
            List<string> list = _settings.physBoneKeepPaths;
            if (list == null || list.Count == 0) return;

            var defaultColor = GUI.color;
            int removeIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                string path = list[i];
                if (PhysBoneIdentityPathExists(path)) continue; // 解決できるものはチェック済みの行として表示済み
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("稼働指定: " + (string.IsNullOrEmpty(path) ? "(空)" : path), _miniWrapLabel);
                    GUI.color = NoteYellowColor;
                    EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                    GUI.color = defaultColor;
                    if (GUILayout.Button(new GUIContent("解除", "現在のアバターで見つからないこの稼働指定を削除します"), GUILayout.Width(44f)))
                    {
                        removeIndex = i;
                    }
                }
            }
            if (removeIndex >= 0)
            {
                list.RemoveAt(removeIndex);
                SaveSettings(); // 稼働選択の保存(診断は古い扱いになる)。keepPathsはプレビューへ影響しないため再計算不要
            }
        }

        // ================================================================
        // PhysBone設定のヘルパー(プレビュー再計算・識別パス・選択リスト操作)
        // ================================================================

        /// <summary>設定のPhysBone選択リストのnullガード(旧設定JSON読込対策)。</summary>
        private void EnsurePhysBoneSettingsLists()
        {
            if (_settings.physBoneRemovePaths == null) _settings.physBoneRemovePaths = new List<string>();
            if (_settings.physBoneNoMergePaths == null) _settings.physBoneNoMergePaths = new List<string>();
            if (_settings.physBoneKeepPaths == null) _settings.physBoneKeepPaths = new List<string>();
        }

        /// <summary>
        /// PhysBoneプレビュー(マージ・削除のドライラン)を再計算する。
        /// 元アバターに対する厳密なドライランのため、シーンやアセットは一切変更されない。
        /// 失敗時はnullのまま失敗ラッチを立てる(毎OnGUIの再試行を防ぐ)。
        /// </summary>
        private void RefreshPhysBonePreview()
        {
            _physBonePreview = null;
            _physBoneRowDisplays = null;
            _physBonePreviewFailed = false;
            if (_avatar == null) return;
            if (_settings == null) _settings = new QuestConvertSettings();
            EnsurePhysBoneSettingsLists();
            try
            {
                // 変換時にEditorOnly化される除外サブツリー(Quest除外 / 透過レンダラー自動非表示)を
                // 元アバター上で解決し、プレビューでも同じPhysBoneを除外する
                // (マテリアルプレビュー・診断の集計と現在数/予測数を一致させる)。
                List<Transform> excludedRoots = AvatarQuestConverter.ResolvePreviewExcludedRoots(_avatar, _settings);
                // OptInモードでは removePaths を使わない(全ユニットを一覧に出して稼働を選ばせる)。
                // KeepAllモードでは従来どおり削除指定分を一覧・予測数から除外する。
                List<string> removeForPreview =
                    _settings.physBoneSelectionMode == PhysBoneSelectionMode.KeepAll
                        ? _settings.physBoneRemovePaths
                        : null;
                _physBonePreview = ComponentRemover.PreviewPhysBoneMerge(
                    _avatar.gameObject,
                    ComponentRemover.CollectPhysBoneTogglePaths(_avatar.gameObject),
                    removeForPreview,
                    _settings.physBoneNoMergePaths,
                    _settings.physBoneLooseMerge,
                    excludedRoots);
                _physBoneRowDisplays = BuildPhysBoneRowDisplays(_physBonePreview.rows);
            }
            catch (Exception ex)
            {
                _physBonePreview = null;
                _physBoneRowDisplays = null;
                _physBonePreviewFailed = true;
                Debug.LogError("[RARA QuestConverter] PhysBoneプレビューの計算に失敗しました: " + ex);
            }
        }

        /// <summary>単一の識別パスを選択リストへ追加/削除する(変更時は保存+プレビュー再計算)。</summary>
        private void SetPhysBonePathSelection(List<string> list, string path, bool selected)
        {
            bool changed;
            if (selected)
            {
                changed = !list.Contains(path);
                if (changed) list.Add(path);
            }
            else
            {
                changed = list.Remove(path);
            }
            if (changed)
            {
                SaveSettings();                 // 選択の保存(診断は古い扱いになる)
                QueuePhysBonePreviewRefresh();  // 選択反映後のレイアウトを再計算
            }
        }

        /// <summary>複数の識別パスをまとめて選択リストへ追加/削除する(変更時は保存+プレビュー再計算)。</summary>
        private void SetPhysBonePathSelection(List<string> list, List<string> paths, bool selected)
        {
            bool changed = false;
            foreach (string path in paths)
            {
                if (path == null) continue;
                if (selected)
                {
                    if (!list.Contains(path)) { list.Add(path); changed = true; }
                }
                else
                {
                    if (list.Remove(path)) changed = true;
                }
            }
            if (changed)
            {
                SaveSettings();                 // 選択の保存(診断は古い扱いになる)
                QueuePhysBonePreviewRefresh();  // 選択反映後のレイアウトを再計算
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

        /// <summary>listがpathsをすべて含むか(pathsが空ならfalse)。</summary>
        private static bool ContainsAllPaths(List<string> list, List<string> paths)
        {
            if (paths.Count == 0) return false;
            foreach (string path in paths)
            {
                if (path == null || !list.Contains(path)) return false;
            }
            return true;
        }

        /// <summary>
        /// PhysBoneの識別パス(相対パス。同一GameObject上に複数ある場合は "#序数" 付き)から
        /// GameObjectのTransformを解決する(見つからなければnull)。ピン表示用。
        /// </summary>
        private Transform ResolvePhysBoneIdentityTransform(string identityPath)
        {
            if (_avatar == null || identityPath == null) return null;

            // まず全体をそのままパスとして解決(通常の識別パスと、グループ行の親パス)
            Transform direct = QuestCompat.FindByPath(_avatar.transform, identityPath);
            if (direct != null) return direct;

            // "#序数" 付き識別パスからパス部分を取り出して解決する
            int hash = identityPath.LastIndexOf('#');
            if (hash < 0) return null;
            int index;
            if (!int.TryParse(identityPath.Substring(hash + 1), out index) || index < 0) return null;
            return QuestCompat.FindByPath(_avatar.transform, identityPath.Substring(0, hash));
        }

        /// <summary>
        /// 識別パスが現在のアバター上のPhysBoneへ解決できるか
        /// (Transformが見つかり、指定序数のPhysBoneが存在するか。序数は識別パス規則
        /// = ComponentRemover.GetPhysBoneIdentityPath と同じく VRCPhysBoneBase 単位で数える)。
        /// 保存済み設定の「(見つかりません)」判定と、実行前チェックの手動削除件数の集計に使う。
        /// </summary>
        private bool PhysBoneIdentityPathExists(string identityPath)
        {
            if (_avatar == null || identityPath == null) return false;

            // GetPhysBoneIdentityPath の規則:
            //   ・対象GameObjectのPhysBoneがちょうど1個 → "#序数" なしの素のパス
            //   ・2個以上           → "パス#序数"(GetComponents順・0始まり)
            // RemoveSelectedPhysBones は素の GetPhysBoneIdentityPath 文字列で厳密照合するため、
            // ここでも同じ規則で判定する(緩い pbIndex < 個数 だと、素のパスが個数2以上のGameObjectに
            // 誤って一致し、実際には変換で削除されないのに「解決可能」と過大報告してしまう)。
            Transform whole = QuestCompat.FindByPath(_avatar.transform, identityPath);
            if (whole != null)
            {
                // パス全体が解決できた = "#序数" なしの識別パス。PhysBoneがちょうど1個のときのみ一致する。
                return whole.GetComponents<VRCPhysBoneBase>().Length == 1;
            }

            int hash = identityPath.LastIndexOf('#');
            if (hash < 0) return false;
            int pbIndex;
            if (!int.TryParse(identityPath.Substring(hash + 1), out pbIndex) || pbIndex < 0) return false;
            Transform target = QuestCompat.FindByPath(_avatar.transform, identityPath.Substring(0, hash));
            if (target == null) return false;
            // "#序数" 付きは PhysBoneが2個以上のときにのみ生成される
            int count = target.GetComponents<VRCPhysBoneBase>().Length;
            return count > 1 && pbIndex < count;
        }

        /// <summary>解決できる識別パスの数を数える(実行前チェックの「手動削除 Z件」用)。</summary>
        private int CountResolvablePhysBonePaths(List<string> paths)
        {
            if (paths == null || paths.Count == 0 || _avatar == null) return 0;
            int count = 0;
            foreach (string path in paths)
            {
                if (PhysBoneIdentityPathExists(path)) count++;
            }
            return count;
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
        // セクション5: 衣装・トグル整理(トグルで切り替わる衣装/アクセサリの固定・削除)
        // ================================================================

        /// <summary>トグル整理の選択肢の表示名(ToggleLockChoiceの並び順 Keep, LockVisible, LockHidden と一致させること)。</summary>
        private static readonly GUIContent[] ToggleChoiceLabels =
        {
            new GUIContent("トグル維持", "現状のままトグルで切り替えられます(メッシュ・マテリアルスロットは減りません)"),
            new GUIContent("表示で固定", "常時表示にしてトグルを外し、AAOビルド時に結合対象にします(スキンメッシュ・マテリアルスロットが減ります)"),
            new GUIContent("非表示で固定", "このメッシュを削除します(EditorOnly化。Quest/PC両方から除外され、揺れ・スロットも消えます)"),
        };

        /// <summary>
        /// 衣装・トグル整理セクション。ToggleConsolidator.DetectToggleGroups で検出した
        /// トグル(FXのm_IsActive / MAオブジェクトトグルで切り替わる衣装・アクセサリ)を
        /// グループごとに「トグル維持 / 表示で固定 / 非表示で固定」から選ばせる。
        /// 「表示で固定」はAAO結合対象にしてメッシュ/スロットを削減、「非表示で固定」はメッシュ削除。
        /// 選択は settings.toggleChoices に groupId でupsert保存される。QuestとPCの両方に効く。
        /// </summary>
        private void DrawOutfitToggleSection()
        {
            if (!DrawSectionFoldout(5, "衣装・トグル整理",
                "トグルで切り替える衣装を固定してメッシュ・スロットを削減(固定するとトグルは無くなります)。",
                FoldKeyOutfit))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)
                EnsureToggleChoicesList();

                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.Outfit"))
                {
                    EditorGUILayout.HelpBox(
                        "トグルで切り替える衣装/アクセサリは、そのままだとメッシュ・マテリアルスロットが減りません。" +
                        "ここで「表示で固定」にするとAAOビルド時に結合され、スキンメッシュ/スロットを大きく削減できます(トグルは無くなります)。" +
                        "「非表示で固定」はそのメッシュを削除します。",
                        MessageType.Info);
                }

                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("アバターを指定すると、トグルで切り替わる衣装/アクセサリを検出して整理できます。", EditorStyles.miniLabel);
                    return;
                }

                // 未検出(セクション初回表示・アバター変更直後など)なら遅延検出を1回だけ予約する
                // (アニメーターコントローラーの走査のため軽くはないが、毎OnGUIの再検出は行わない)
                if (_toggleGroups == null && !_toggleGroupsFailed && !_toggleGroupsRefreshQueued)
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

                DrawToggleQuickButtons();

                foreach (ToggleGroup group in _toggleGroups)
                {
                    DrawToggleGroupRow(group);
                }

                DrawToggleProjectedNote();
            }
        }

        /// <summary>一括操作ボタン(現在の表示状態で固定 / すべてトグル維持に戻す)を描画する。</summary>
        private void DrawToggleQuickButtons()
        {
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
        }

        /// <summary>トグルグループ1件分の行(ラベル+メッシュ数+現在表示状態 / 選択ポップアップ / ピン)を描画する。</summary>
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

        /// <summary>トグルグループの代表オブジェクト(objectPathsの最初に解決できたもの)をピン表示するボタンを描画する。</summary>
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
                    "表示固定にしたメッシュは常時表示になり、AAOビルド時に結合対象になります(スキンメッシュ/スロット削減)。" +
                    "非表示固定のメッシュは削除されます。",
                    MessageType.None);
            }
        }

        // ================================================================
        // セクション5: 衣装・トグル整理のヘルパー(検出キャッシュ・選択リスト操作)
        // ================================================================

        /// <summary>設定のトグル選択リストのnullガード(旧設定JSON読込対策)。</summary>
        private void EnsureToggleChoicesList()
        {
            if (_settings.toggleChoices == null) _settings.toggleChoices = new List<ToggleGroupChoice>();
        }

        /// <summary>groupIdに対応するトグル選択エントリを探す(なければnull)。</summary>
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

        /// <summary>
        /// groupId のトグル選択を choice に設定する(保存はしない)。変更があれば true を返す。
        /// Keep(既定)はエントリを削除して設定JSONの肥大化を防ぐ。
        /// </summary>
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

        /// <summary>groupId のトグル選択を設定して保存する(変更時のみ。診断は古い扱いになる)。</summary>
        private void SetToggleChoice(string groupId, ToggleLockChoice choice)
        {
            if (ApplyToggleChoice(groupId, choice)) SaveSettings();
        }

        /// <summary>検出中の各トグルを、現在の表示状態に応じて 表示中→表示で固定 / 非表示→非表示で固定 に一括設定する。</summary>
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
            if (changed) SaveSettings(); // 選択の保存(診断は古い扱いになる)
        }

        /// <summary>検出中の各トグルの選択を「トグル維持」に戻す(該当エントリを削除する)。</summary>
        private void ResetAllToggleChoices()
        {
            if (_toggleGroups == null) return;
            bool changed = false;
            foreach (ToggleGroup group in _toggleGroups)
            {
                if (group == null || string.IsNullOrEmpty(group.id)) continue;
                if (ApplyToggleChoice(group.id, ToggleLockChoice.Keep)) changed = true;
            }
            if (changed) SaveSettings(); // 選択の保存(診断は古い扱いになる)
        }

        /// <summary>
        /// トグルグループの検出をやり直す(ToggleConsolidator.DetectToggleGroups。READ-ONLY)。
        /// 元アバターを走査するだけでシーン・アセットは変更しない。失敗時はnullのまま失敗ラッチを立てる。
        /// </summary>
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
                Debug.LogError("[RARA QuestConverter] トグルの検出に失敗しました: " + ex);
            }
        }

        /// <summary>
        /// トグルグループの再検出を1回だけ予約する。OnGUIの描画ループがリストを列挙している最中に
        /// 差し替えないよう delayCall で実行する(_toggleGroupsRefreshQueued で再入をガード)。
        /// </summary>
        private void QueueToggleGroupsRefresh()
        {
            if (_avatar == null || _toggleGroupsRefreshQueued) return;
            _toggleGroupsRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _toggleGroupsRefreshQueued = false;
                if (this == null) return;   // ウィンドウが閉じられた(Unityのnull比較)
                if (_avatar == null) return;
                RefreshToggleGroups();
                Repaint();
            };
        }

        // ================================================================
        // セクション6: アトラス統合
        // ================================================================
        private static readonly GUIContent[] AtlasSizeLabels =
        {
            new GUIContent("1024"),
            new GUIContent("2048(推奨)"),
        };
        private static readonly int[] AtlasSizeValues = { 1024, 2048 };

        private void DrawAtlasSection()
        {
            if (!DrawSectionFoldout(6, "アトラス統合",
                "複数マテリアルを1枚のテクスチャにまとめ、マテリアルスロット数を削減します(実験的)。",
                FoldKeyAtlas))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)

                DrawConsolidateOnlySkipNote("アトラス統合");

                EditorGUI.BeginChangeCheck();

                _settings.enableAtlas = EditorGUILayout.ToggleLeft(
                    new GUIContent("アトラス統合を有効化(実験的)",
                        "互換性のある変換後マテリアルを1枚のアトラステクスチャに統合し、マテリアルスロット数を削減します"),
                    _settings.enableAtlas);

                if (_settings.enableAtlas)
                {
                    _settings.atlasMaxSize = EditorGUILayout.IntPopup(
                        new GUIContent("アトラス最大サイズ", "統合後のアトラステクスチャの最大サイズ"),
                        _settings.atlasMaxSize, AtlasSizeLabels, AtlasSizeValues);

                    _settings.atlasUnifyRamps = EditorGUILayout.ToggleLeft(
                        new GUIContent("影ランプを統一してグループ化(スロット削減優先)",
                            "アトラス統合時、影ランプの違いを無視してグループ化する(1グループ=1代表ランプに統一)。" +
                            "影ランプが個別生成されるアバターでもスロットを大きく削減できる。影のトーンはグループ内で共通化される"),
                        _settings.atlasUnifyRamps);
                    EditorGUILayout.LabelField(
                        "影ランプが個別に生成されるアバター(マテリアルごとに影設定が異なる)では、これをオフにすると" +
                        "アトラスがほとんど効きません。オンにするとグループ内で影のトーンが共通化される代わりにスロットを大きく削減できます。",
                        _miniWrapLabel);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    SaveSettings(); // 変更のたびに保存(診断は古い扱いになる)
                }

                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.Atlas"))
                {
                    EditorGUILayout.HelpBox(
                        "互換マテリアルを1枚に統合し、マテリアルスロット数を削減します。\n" +
                        "メッシュ結合はAAO(Trace and Optimize)がビルド時に実施します。\n" +
                        "アニメで差し替えるマテリアルとUVタイリング使用は自動で除外されます。\n" +
                        "対象の個別選択は「3. マテリアル設定」の「アトラス統合」トグルで行えます。\n" +
                        "注意: 影ランプがマテリアルごとに個別生成されると、そのままではランプ違いでグループが分かれて" +
                        "統合が効きません。「影ランプを統一してグループ化」をオンにすると1グループ=1代表ランプに統一され、" +
                        "スロットを大きく削減できます(影のトーンはグループ内で共通化されます)。",
                        MessageType.Info);
                }
            }
        }

        // ================================================================
        // セクション7: メッシュ削減(AAO連携) - 隠れた肌などをブレンドシェイプ削除で消す
        // ================================================================

        /// <summary>
        /// メッシュ削減(AAO連携)セクション。服の下に完全に隠れて見えない肌などのメッシュを、
        /// アバター自身が持つ肌を縮める(shrink)ブレンドシェイプを使ってAAO(Avatar Optimizer)の
        /// RemoveMeshByBlendShapeでビルド時に削除し、ポリゴン数・容量を減らす(見えない部分のみ)。
        /// AAOMeshRemovalHelper.DetectShrinkShapes で候補メッシュを検出し(READ-ONLY)、
        /// ユーザーが対象を選ぶと settings.hiddenMeshRendererPaths に保存する。
        /// AAO未導入時は機能を無効化し案内を出す。shrinkブレンドシェイプが無い場合は手動削除の手引きを常時表示する。
        /// シェーダー変換とは独立した最適化のため、PC最適化のみモードでも有効。
        /// </summary>
        private void DrawHiddenMeshRemovalSection()
        {
            if (!DrawSectionFoldout(7, "メッシュ削減(AAO連携)",
                "服の下に隠れて見えない肌などを削除してポリゴン・容量を削減します(AAOが必要)。",
                FoldKeyHiddenMesh))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)
                EnsureHiddenMeshSettingsLists();

                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.HiddenMesh"))
                {
                    EditorGUILayout.HelpBox(
                        "服の下に完全に隠れて見えない肌などのメッシュを削除して、ポリゴン数・容量を減らします。" +
                        "アバター自身が持つ肌を縮める(shrink)ブレンドシェイプを使い、AAO(Avatar Optimizer)がビルド時に" +
                        "見えない部分だけを削除します(元アバターは変更されません)。",
                        MessageType.Info);
                }

                bool aaoInstalled = IsAAOInstalled();
                if (!aaoInstalled)
                {
                    EditorGUILayout.HelpBox("この機能にはAvatarOptimizer(AAO)が必要です。", MessageType.Warning);
                }

                // 有効化トグル + Trace and Optimize 追加トグル(AAO未導入時は操作不可)
                using (new EditorGUI.DisabledScope(!aaoInstalled))
                {
                    EditorGUI.BeginChangeCheck();
                    bool remove = EditorGUILayout.ToggleLeft(
                        new GUIContent("隠れた肌などをブレンドシェイプ削除で消す",
                            "服の下に隠れる肌などをAAOのブレンドシェイプ削除で消す(shrinkブレンドシェイプ検出時。見えない部分のみ)"),
                        _settings.removeHiddenMeshByBlendShape);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _settings.removeHiddenMeshByBlendShape = remove;
                        SaveSettings(); // 変換内容に影響する(診断は古い扱いになる)
                    }

                    EditorGUI.BeginChangeCheck();
                    bool ensureTao = EditorGUILayout.ToggleLeft(
                        new GUIContent("Trace and Optimizeが無ければ複製に追加",
                            "AAOのTrace and Optimizeが無ければ複製に追加してビルド時最適化を有効にする"),
                        _settings.ensureTraceAndOptimize);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _settings.ensureTraceAndOptimize = ensureTao;
                        SaveSettings();
                    }
                }

                // 候補リストは機能有効かつAAO導入時のみ表示する
                if (aaoInstalled && _settings.removeHiddenMeshByBlendShape)
                {
                    DrawHiddenMeshCandidateList();
                }

                // SkinnedMesh統合(顔以外を1つへ)ブロック
                EditorGUILayout.Space(6f);
                DrawSkinnedMeshMergeBlock(aaoInstalled);

                // 手動削除の手引きは常時表示する
                DrawHiddenMeshGuidance();
            }
        }

        /// <summary>
        /// SkinnedMesh統合(顔以外を1つへ)のUIブロック。モード切替・プレビュー表(統合する/しない+理由)・
        /// 統合後の想定SMR数・スロット重複排除の注記を描画する。QuestConverter・PCOptimizer 共通の
        /// SkinnedMeshMergePlanner / AAOMeshMergeHelper を使う。
        /// </summary>
        private void DrawSkinnedMeshMergeBlock(bool aaoInstalled)
        {
            if (_settings.skinnedMeshMergeOptOutPaths == null) _settings.skinnedMeshMergeOptOutPaths = new List<string>();

            EditorGUILayout.LabelField("SkinnedMesh統合(顔以外を1つへ)", EditorStyles.miniBoldLabel);

            if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.MeshMerge"))
            {
                EditorGUILayout.HelpBox(
                    "顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を AAO の Merge Skinned Mesh で1つへ統合し、" +
                    "SkinnedMesh数とマテリアルスロット数を確実に減らします(Quest Poor上限: SkinnedMesh 2 / スロット 4)。" +
                    "統合はビルド時(NDMF)にAAOが行い、ブレンドシェイプの改名・マテリアルスロットの再マップ・" +
                    "アニメーションの再パスもAAOが自動で行うため、表情(顔ブレンドシェイプ/ビセーム)は分離維持で保たれ、" +
                    "顔以外に残るブレンドシェイプ・マテリアルのアニメも追従して動き続けます。\n" +
                    "ただし表示/非表示の切り替え(トグル)は統合後は効かなくなります: 統合先は常時表示のため、" +
                    "AAOがビルド時にソース側の m_Enabled / m_IsActive アニメを無効化します(統合するとその衣装・装飾は常時表示になります)。" +
                    "切り替えを残したいメッシュは、下の一覧の「このメッシュは統合しない」で統合対象から外してください" +
                    "(常時表示でよいものは「衣装・トグル整理」で表示固定にすると統合できます)。\n" +
                    "注: 反映されるのはビルド時(Play/アップロード)です。保存された複製プレファブは統合前のメッシュ・" +
                    "スロット数のまま表示されます(既存のブレンドシェイプ削除・自動統合と同じ挙動)。",
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
                    _settings.mergeSkinnedMeshesMode = (SkinnedMeshMergeMode)picked;
                    SaveSettings();
                }
            }

            if (!aaoInstalled)
            {
                EditorGUILayout.HelpBox("SkinnedMesh統合にはAvatarOptimizer(AAO)が必要です。", MessageType.Warning);
                return;
            }

            // 無効時: レンダラーごとに同じ「統合しない」行を並べず、1つの案内 + 目立つ有効化ボタンだけを見せる。
            if (_settings.mergeSkinnedMeshesMode == SkinnedMeshMergeMode.None)
            {
                EditorGUILayout.HelpBox(
                    "SkinnedMesh統合は無効です。『顔以外を統合』にすると、顔以外のメッシュ(静的なMeshRenderer含む)を" +
                    "ビルド時に1つへ統合し、SkinnedMesh数を2まで削減できます(表示/非表示トグルは無効化されます)。",
                    MessageType.Info);
                if (GUILayout.Button(new GUIContent("顔以外を統合を有効にする",
                    "顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を1つへ統合するモードに切り替えます"), GUILayout.Height(28f)))
                {
                    _settings.mergeSkinnedMeshesMode = SkinnedMeshMergeMode.MergeExceptFace;
                    SaveSettings();
                }
                return;
            }
            if (_avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、統合プレビューを表示します。", EditorStyles.miniLabel);
                return;
            }

            // プレビューは元アバター上で作る(読み取り専用・軽量: コンポーネント列挙のみ)。
            SkinnedMeshMergePlan plan = SkinnedMeshMergePlanner.BuildPlan(
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

            foreach (SkinnedMeshMergeRow row in plan.rows)
            {
                DrawSkinnedMeshMergeRow(row);
            }
        }

        /// <summary>統合プレビュー1行(統合する/しない・理由・個別除外トグル・ピン)を描画する。</summary>
        private void DrawSkinnedMeshMergeRow(SkinnedMeshMergeRow row)
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
                    DrawHiddenMeshPingButton(row.rendererPath);
                }
                if (!string.IsNullOrEmpty(row.reason))
                {
                    EditorGUILayout.LabelField("理由: " + row.reason, _miniWrapLabel);
                }

                // 顔・EditorOnly 以外は「統合しない」で個別除外できる(ユーザーのオプトアウト)。
                if (!row.isFace && !row.isEditorOnly)
                {
                    bool optedOut = _settings.skinnedMeshMergeOptOutPaths.Contains(row.rendererPath);
                    EditorGUI.BeginChangeCheck();
                    bool newOptedOut = EditorGUILayout.ToggleLeft(
                        new GUIContent("このメッシュは統合しない", "このレンダラーを統合対象から外して分離維持します"),
                        optedOut);
                    if (EditorGUI.EndChangeCheck())
                    {
                        SetSkinnedMeshMergeOptOut(row.rendererPath, newOptedOut);
                    }
                }
            }
        }

        /// <summary>レンダラーパスを統合除外リスト(skinnedMeshMergeOptOutPaths)へ追加/削除する(変更時のみ保存)。</summary>
        private void SetSkinnedMeshMergeOptOut(string rendererPath, bool optOut)
        {
            if (string.IsNullOrEmpty(rendererPath)) return;
            if (_settings.skinnedMeshMergeOptOutPaths == null) _settings.skinnedMeshMergeOptOutPaths = new List<string>();
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

        /// <summary>
        /// 検出された shrinkブレンドシェイプ候補の一覧を描画する(未検出なら遅延検出を予約)。
        /// 候補ごとにチェックボックス(選ぶと rendererPath を hiddenMeshRendererPaths に登録)・名前・
        /// ブレンドシェイプ名フォールドアウト・理由・ピンを出す。1件も無ければ手動削除を促すHelpBoxを出す。
        /// </summary>
        private void DrawHiddenMeshCandidateList()
        {
            if (_avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると、shrinkブレンドシェイプを検出します。", EditorStyles.miniLabel);
                return;
            }

            // 未検出(セクション初回表示・アバター変更直後など)なら遅延検出を1回だけ予約する
            // (メッシュ走査のため軽くはないが、毎OnGUIの再検出は行わない)
            if (_hiddenMeshCandidates == null && !_hiddenMeshFailed && !_hiddenMeshRefreshQueued)
            {
                QueueHiddenMeshRefresh();
            }

            if (_hiddenMeshCandidates == null)
            {
                if (_hiddenMeshFailed)
                {
                    EditorGUILayout.HelpBox("shrinkブレンドシェイプの検出に失敗しました(Consoleを確認してください)。", MessageType.Error);
                    if (GUILayout.Button(new GUIContent("再検出", "shrinkブレンドシェイプを検出し直します"), GUILayout.Width(70f)))
                    {
                        QueueHiddenMeshRefresh();
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("shrinkブレンドシェイプを検出中...", EditorStyles.miniLabel);
                }
                return;
            }

            if (_hiddenMeshCandidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "shrinkブレンドシェイプは検出されませんでした。手動での箱指定削除(下の手引き)をご検討ください。",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("削除に使うメッシュを選んでください(チェックしたメッシュのみ対象)", EditorStyles.miniBoldLabel);
            foreach (ShrinkShapeCandidate candidate in _hiddenMeshCandidates)
            {
                DrawHiddenMeshCandidateRow(candidate);
            }

            int selected = CountSelectedHiddenMeshRenderers();
            EditorGUILayout.LabelField(
                selected == 0
                    ? "対象に選ばれたメッシュはありません(このままでは削除は行われません)。"
                    : "削除対象メッシュ " + selected + " 件。ビルド時にshrinkブレンドシェイプで隠れた部分が削除されます。",
                _miniWrapLabel);
        }

        /// <summary>shrinkブレンドシェイプ候補1件分の行(チェック+名前 / 理由 / ブレンドシェイプ名フォールドアウト / ピン)を描画する。</summary>
        private void DrawHiddenMeshCandidateRow(ShrinkShapeCandidate candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.rendererPath)) return;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool included = _settings.hiddenMeshRendererPaths.Contains(candidate.rendererPath);
                    bool newIncluded = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            string.IsNullOrEmpty(candidate.rendererName) ? candidate.rendererPath : candidate.rendererName,
                            "パス: " + candidate.rendererPath),
                        included);
                    if (newIncluded != included)
                    {
                        SetHiddenMeshRendererSelected(candidate.rendererPath, newIncluded);
                    }

                    GUILayout.FlexibleSpace();
                    DrawHiddenMeshPingButton(candidate.rendererPath);
                }

                if (!string.IsNullOrEmpty(candidate.reason))
                {
                    EditorGUILayout.LabelField(candidate.reason, _miniWrapLabel);
                }

                int shapeCount = candidate.blendShapeNames != null ? candidate.blendShapeNames.Count : 0;
                bool expanded = _hiddenMeshExpanded.Contains(candidate.rendererPath);
                bool newExpanded = EditorGUILayout.Foldout(expanded, "使用するブレンドシェイプ (" + shapeCount + ")", true);
                if (newExpanded != expanded)
                {
                    if (newExpanded) _hiddenMeshExpanded.Add(candidate.rendererPath);
                    else _hiddenMeshExpanded.Remove(candidate.rendererPath);
                }
                if (newExpanded)
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
                                EditorGUILayout.LabelField("・" + (shapeName ?? "(不明)"), _miniWrapLabel);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>候補メッシュ(rendererPath)の指すGameObjectをピン表示するボタンを描画する。</summary>
        private void DrawHiddenMeshPingButton(string rendererPath)
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

        /// <summary>shrinkブレンドシェイプが無い場合の手動削除の手引き(常時表示)。</summary>
        private void DrawHiddenMeshGuidance()
        {
            EditorGUILayout.Space(4f);
            const string manualKey = "RARA.QuestConverter.Fold.Explain.HiddenMeshManual";
            bool expanded = GetFoldPref(manualKey, false);
            bool newExpanded = EditorGUILayout.Foldout(expanded,
                "手動削除の手引き(shrinkブレンドシェイプが無い/足りない場合)", true);
            if (newExpanded != expanded) SetFoldPref(manualKey, newExpanded);
            if (!newExpanded) return;
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
        // セクション7: メッシュ削減(AAO連携)のヘルパー(AAO判定・検出キャッシュ・選択リスト操作)
        // ================================================================

        /// <summary>AAO(Avatar Optimizer)が導入されているか(TraceAndOptimize型の有無で判定。コンパイル時参照はしない)。</summary>
        private static bool IsAAOInstalled()
        {
            return QuestCompat.FindType("Anatawa12.AvatarOptimizer.TraceAndOptimize") != null;
        }

        /// <summary>設定の隠れメッシュ選択リストのnullガード(旧設定JSON読込対策)。</summary>
        private void EnsureHiddenMeshSettingsLists()
        {
            if (_settings.hiddenMeshRendererPaths == null) _settings.hiddenMeshRendererPaths = new List<string>();
        }

        /// <summary>候補メッシュ(rendererPath)を削除対象リスト(hiddenMeshRendererPaths)へ追加/削除する(変更時のみ保存)。</summary>
        private void SetHiddenMeshRendererSelected(string rendererPath, bool selected)
        {
            if (string.IsNullOrEmpty(rendererPath)) return;
            EnsureHiddenMeshSettingsLists();
            bool changed;
            if (selected)
            {
                changed = !_settings.hiddenMeshRendererPaths.Contains(rendererPath);
                if (changed) _settings.hiddenMeshRendererPaths.Add(rendererPath);
            }
            else
            {
                changed = _settings.hiddenMeshRendererPaths.Remove(rendererPath);
            }
            if (changed) SaveSettings(); // 選択の保存(診断は古い扱いになる)
        }

        /// <summary>検出中の候補のうち、削除対象に選ばれている件数を数える。</summary>
        private int CountSelectedHiddenMeshRenderers()
        {
            if (_settings == null || _settings.hiddenMeshRendererPaths == null || _hiddenMeshCandidates == null) return 0;
            int count = 0;
            foreach (ShrinkShapeCandidate candidate in _hiddenMeshCandidates)
            {
                if (candidate != null && candidate.rendererPath != null &&
                    _settings.hiddenMeshRendererPaths.Contains(candidate.rendererPath)) count++;
            }
            return count;
        }

        /// <summary>
        /// shrinkブレンドシェイプ候補の検出をやり直す(AAOMeshRemovalHelper.DetectShrinkShapes。READ-ONLY)。
        /// 元アバターを走査するだけでシーン・アセットは変更しない。失敗時はnullのまま失敗ラッチを立てる。
        /// </summary>
        private void RefreshHiddenMeshCandidates()
        {
            _hiddenMeshCandidates = null;
            _hiddenMeshFailed = false;
            if (_avatar == null) return;
            try
            {
                _hiddenMeshCandidates = AAOMeshRemovalHelper.DetectShrinkShapes(_avatar.gameObject) ?? new List<ShrinkShapeCandidate>();
            }
            catch (Exception ex)
            {
                _hiddenMeshCandidates = null;
                _hiddenMeshFailed = true;
                Debug.LogError("[RARA QuestConverter] shrinkブレンドシェイプの検出に失敗しました: " + ex);
            }
        }

        /// <summary>
        /// shrinkブレンドシェイプ候補の再検出を1回だけ予約する。OnGUIの描画ループがリストを列挙している最中に
        /// 差し替えないよう delayCall で実行する(_hiddenMeshRefreshQueued で再入をガード)。
        /// </summary>
        private void QueueHiddenMeshRefresh()
        {
            if (_avatar == null || _hiddenMeshRefreshQueued) return;
            _hiddenMeshRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _hiddenMeshRefreshQueued = false;
                if (this == null) return;   // ウィンドウが閉じられた(Unityのnull比較)
                if (_avatar == null) return;
                RefreshHiddenMeshCandidates();
                Repaint();
            };
        }

        // ================================================================
        // セクション8: Quest除外オブジェクト
        // ================================================================
        private void DrawExcludeSection()
        {
            if (!DrawSectionFoldout(8, "Quest除外オブジェクト",
                "Quest版だけから完全に外すオブジェクトを指定します(PC版はそのまま残ります)。",
                FoldKeyExclude))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)
                if (_settings.questExcludePaths == null) _settings.questExcludePaths = new List<string>(); // 旧設定JSON読込対策

                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.Exclude"))
                {
                    EditorGUILayout.HelpBox(
                        "ここに登録したオブジェクトは、変換後の_Quest複製でのみEditorOnlyタグ+非アクティブになり、ビルドから完全に除外されます。PC版には影響しません。\n" +
                        "アバタールートや、スキンメッシュが参照するボーンは登録しないでください。",
                        MessageType.Info);
                }

                using (new EditorGUI.DisabledScope(_avatar == null))
                {
                    // 常にnull値で描画するドロップ用スロット。割り当てられた瞬間にパス登録し、スロットは空に戻る。
                    var dropped = EditorGUILayout.ObjectField(
                        new GUIContent("除外に追加", "選択中のアバター配下のオブジェクト(Transform)をドロップすると除外リストへ登録します"),
                        null, typeof(Transform), true) as Transform;
                    if (dropped != null)
                    {
                        AddExcludePath(dropped);
                    }
                }
                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("対象アバターを指定すると登録できます。", EditorStyles.miniLabel);
                }
                if (!string.IsNullOrEmpty(_excludeAddError))
                {
                    EditorGUILayout.HelpBox(_excludeAddError, MessageType.Error);
                }

                DrawExcludePathList();
            }
        }

        /// <summary>登録済み除外パスの一覧表示(解決チェック・ピン・削除ボタン付き)。</summary>
        private void DrawExcludePathList()
        {
            var paths = _settings.questExcludePaths;
            if (paths.Count == 0)
            {
                EditorGUILayout.LabelField("登録された除外オブジェクトはありません。", EditorStyles.miniLabel);
                return;
            }

            var defaultColor = GUI.color;
            int removeIndex = -1;
            for (int i = 0; i < paths.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string path = paths[i] ?? "";
                    EditorGUILayout.LabelField(path, _wrapLabel);

                    // 選択中のアバター上でパスを解決(アバター未選択時はnull)
                    Transform resolved = _avatar != null ? QuestCompat.FindByPath(_avatar.transform, path) : null;
                    if (_avatar != null && resolved == null)
                    {
                        GUI.color = NoteYellowColor;
                        EditorGUILayout.LabelField("(見つかりません)", EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUI.color = defaultColor;
                    }

                    // ピン: シーン上の該当オブジェクトをハイライト表示(解決できた場合のみ)
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
                SaveSettings(); // 除外の変更は診断結果に影響するためstale扱い
            }
        }

        /// <summary>ドロップされたTransformを相対パスとして除外リストへ登録する(重複は無視)。</summary>
        private void AddExcludePath(Transform target)
        {
            _excludeAddError = null;
            if (_avatar == null || target == null) return;

            Transform root = _avatar.transform;
            if (target == root)
            {
                _excludeAddError = "アバタールート自体は除外に登録できません。";
                return;
            }
            if (!target.IsChildOf(root)) // 自身はroot判定で除外済みのため、ここは「配下かどうか」の判定になる
            {
                _excludeAddError = "「" + target.name + "」は選択中のアバターの配下ではないため登録できません。";
                return;
            }

            string path = QuestCompat.GetRelativePath(root, target);
            if (string.IsNullOrEmpty(path))
            {
                _excludeAddError = "相対パスを取得できませんでした: " + target.name;
                return;
            }
            // 名前パスは同名の兄弟がいると最初の一致へ解決される(Transform.Find仕様)。
            // ドロップした本人へ解決し直せないパスは、意図しないオブジェクトを除外してしまうため登録を拒否する。
            if (QuestCompat.FindByPath(root, path) != target)
            {
                _excludeAddError = "「" + target.name + "」の相対パス \"" + path + "\" は同名の別オブジェクトを指すため登録できません。" +
                    "同名の兄弟オブジェクトがある(または名前に \"/\" を含む)場合は、対象の名前を一意にしてから登録してください。";
                return;
            }
            if (!_settings.questExcludePaths.Contains(path)) // 重複登録は無視(dedupe)
            {
                _settings.questExcludePaths.Add(path);
                SaveSettings(); // 除外の変更は診断結果に影響するためstale扱い
            }
        }

        // ================================================================
        // セクション9: 変換設定(基本 + 詳細設定)
        // ================================================================
        private void DrawSettingsSection()
        {
            if (!DrawSectionFoldout(9, "変換設定",
                "変換先シェーダーやテクスチャ品質などの詳細設定。基本は既定のままでOKです。",
                FoldKeySettings))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)

                bool changed = false;

                // ---- 基本設定(いつも触るもの) ----
                EditorGUILayout.LabelField("基本", EditorStyles.miniBoldLabel);
                DrawConsolidateOnlySkipNote("変換先シェーダー・テクスチャ関連の設定(以下の基本設定)");
                EditorGUI.BeginChangeCheck();

                // 変換先シェーダー(日本語ラベル付きポップアップ)
                int shaderIndex = _settings.shaderTarget == QuestShaderTarget.ToonLit ? 1 : 0;
                shaderIndex = EditorGUILayout.Popup(
                    new GUIContent("変換先シェーダー", "変換先のQuest対応シェーダー"),
                    shaderIndex, ShaderTargetLabels);
                _settings.shaderTarget = shaderIndex == 1 ? QuestShaderTarget.ToonLit : QuestShaderTarget.ToonStandard;

                _settings.maxTextureSize = EditorGUILayout.IntPopup(
                    new GUIContent("最大テクスチャサイズ", "ベイク生成テクスチャの最大サイズ(Quest推奨: 1024)"),
                    _settings.maxTextureSize, TextureSizeLabels, TextureSizeValues);

                _settings.androidFormat = (TextureImporterFormat)EditorGUILayout.IntPopup(
                    new GUIContent("Android圧縮形式", "Androidプラットフォームのテクスチャ圧縮形式"),
                    (int)_settings.androidFormat, AndroidFormatLabels, AndroidFormatValues);

                // シェーダー別オプション
                if (_settings.shaderTarget == QuestShaderTarget.ToonLit)
                {
                    _settings.bakeShadowIntoMainTex = EditorGUILayout.ToggleLeft(
                        new GUIContent("影をメインテクスチャへベイク",
                            "Toon Lit時: lilToonの影色をメインテクスチャに乗算ベイクする(フラットな擬似陰影)"),
                        _settings.bakeShadowIntoMainTex);
                }
                if (_settings.shaderTarget == QuestShaderTarget.ToonStandard)
                {
                    _settings.generateShadowRamp = EditorGUILayout.ToggleLeft(
                        new GUIContent("影ランプを生成",
                            "Toon Standard時: lilToonの影設定 / NonToonの影グラデーションから影ランプテクスチャを生成する(オフ時はSDK既定ランプ)"),
                        _settings.generateShadowRamp);

                    _settings.mapRimLighting = EditorGUILayout.ToggleLeft(
                        new GUIContent("リムライトを近似変換",
                            "Toon Standard時: lilToon / NonToonのリムライトをToon Standardのリムライトへ近似変換する(既定はオフ)。" +
                            "リムの範囲・ぼかしの意味が異なるため、まぶた等に想定外のハイライト(謎の光)が出る場合はオフのままにしてください"),
                        _settings.mapRimLighting);
                }

                _settings.bakeEmission = EditorGUILayout.ToggleLeft(
                    new GUIContent("エミッションを変換",
                        "エミッションを変換する(Toon Lit: メインへ加算ベイク / Toon Standard: Emissionへマップ)"),
                    _settings.bakeEmission);

                _settings.aggressiveTextureReduction = EditorGUILayout.ToggleLeft(
                    new GUIContent("単色・低ディテールなテクスチャを極限まで縮小",
                        "アトラス化するテクスチャや容量見積りで、単色・低ディテール(のっぺりした)テクスチャを検出して極限まで縮小する。" +
                        "容量削減に有効ですが、微細な模様が消える場合はオフにしてください(既定はオン)"),
                    _settings.aggressiveTextureReduction);

                if (EditorGUI.EndChangeCheck()) changed = true;

                // ---- 詳細設定(危険な操作は⚠付き) ----
                _foldAdvancedSettings = EditorGUILayout.Foldout(_foldAdvancedSettings, "詳細設定", true);
                if (_foldAdvancedSettings)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUI.BeginChangeCheck();

                        _settings.convertAnimations = EditorGUILayout.ToggleLeft(
                            new GUIContent("アニメーションも変換",
                                "マテリアル差し替えアニメーション(FX等)も変換後マテリアルを参照するよう複製・差し替える"),
                            _settings.convertAnimations);

                        _settings.removeUnsupportedComponents = EditorGUILayout.ToggleLeft(
                            new GUIContent("⚠ 非対応コンポーネントを削除",
                                "生成される_Quest複製から、Android非対応コンポーネント(Cloth/Camera/Light/AudioSource等)を削除する(元アバターは変更されません)"),
                            _settings.removeUnsupportedComponents);

                        _settings.convertUnityConstraints = EditorGUILayout.ToggleLeft(
                            new GUIContent("UnityコンストレイントをVRCConstraintへ変換",
                                "Unityコンストレイントを見つけたらVRCConstraintへ変換する(SDKの変換APIを使用)"),
                            _settings.convertUnityConstraints);

                        _settings.mergePhysBones = EditorGUILayout.ToggleLeft(
                            new GUIContent("PhysBoneをマージして削減(揺れを維持)",
                                "設定が一致する兄弟PhysBoneチェーンを1つのコンポーネントへマージし、揺れを維持したまま" +
                                "コンポーネント数を削減する(Medium上限" + QuestLimits.MediumPhysBoneComponents +
                                "/Poor上限" + QuestLimits.PoorPhysBoneComponents + "への対策)。" +
                                "パラメータ使用・アニメーションによるON/OFF制御・カーブ設定があるものなど、" +
                                "挙動が変わる恐れのあるPhysBoneは自動で対象外になる(理由は変換レポートに表示)"),
                            _settings.mergePhysBones);

                        _settings.trimPhysBonesToPoorLimit = EditorGUILayout.ToggleLeft(
                            new GUIContent("⚠ マージ後もPoor上限を超える場合に超過分を削除",
                                "PhysBoneのマージ後もAndroidのPoor上限(コンポーネント" + QuestLimits.PoorPhysBoneComponents +
                                "/コライダー" + QuestLimits.PoorPhysBoneColliders + ")を超える場合、_Quest複製から超過分を自動削除する(揺れものが動かなくなることがあります)"),
                            _settings.trimPhysBonesToPoorLimit);

                        // 透過マテリアルの既定処理は「3. マテリアル設定」の「透過マテリアルの既定処理」
                        // ドロップダウン(再現/非表示/不透明)へ集約したため、ここには置かない。

                        _settings.deactivateOriginal = EditorGUILayout.ToggleLeft(
                            new GUIContent("変換後に元アバターを非アクティブ化",
                                "変換後に元のアバターを非アクティブ化する(元アバター自体は変更されません)"),
                            _settings.deactivateOriginal);

                        // 出力先フォルダ
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _settings.outputFolder = EditorGUILayout.TextField(
                                new GUIContent("出力先フォルダ", "生成アセットの出力先ルートフォルダ"),
                                _settings.outputFolder);
                            if (GUILayout.Button(new GUIContent("既定に戻す", "出力先フォルダを既定値に戻します"), GUILayout.Width(80f)))
                            {
                                _settings.outputFolder = new QuestConvertSettings().outputFolder;
                                GUI.FocusControl(null); // 入力中のTextFieldを解除して表示を更新
                                SaveSettings();
                            }
                        }
                        if (!IsValidOutputFolder(_settings.outputFolder))
                        {
                            EditorGUILayout.HelpBox("出力先は \"Assets/\" から始まるプロジェクト内フォルダを指定してください。", MessageType.Warning);
                        }

                        if (EditorGUI.EndChangeCheck()) changed = true;

                        // Auto-Fix相当のビルドフック(EditorPrefs直結のため設定JSONの変更チェックとは分離する。
                        // 診断結果には影響しないのでstale扱いにもしない)
                        EditorGUI.BeginChangeCheck();
                        bool autoStrip = EditorGUILayout.ToggleLeft(
                            new GUIContent("Android/iOSビルド時に非対応コンポーネントを自動削除(Auto Fix相当)",
                                "FaceEmo等のNDMFツールがビルド時に注入するコンポーネントも対象。通常のBuild/Uploadではビルド用コピーに適用され、保存済みシーンは変更されない。"),
                            QuestBuildPreprocessor.Enabled);
                        if (EditorGUI.EndChangeCheck())
                        {
                            QuestBuildPreprocessor.Enabled = autoStrip; // 変更時のみEditorPrefsへ書き込む
                        }
                    }
                }

                if (changed)
                {
                    SaveSettings(); // 変更のたびに保存(診断は古い扱いになる)
                }
            }
        }

        private static bool IsValidOutputFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            var normalized = folder.Replace('\\', '/');
            return normalized == "Assets" || normalized.StartsWith("Assets/", StringComparison.Ordinal);
        }

        // ================================================================
        // セクション10: ポリゴン削減(メッシュ簡略化で目標ポリゴン数へ)
        //   ・目標ランク(7,500 / 10,000 / 15,000 / 20,000 = Quest StatsLevels の polyCount)に合わせて
        //     メッシュごとの削減目標を自動配分する。顔(リップシンク対象)・髪は強く保護される。
        //   ・「自動で配分計画を作成」で PolygonBudgetPlanner.BuildPlan を呼び、UI作業コピー
        //     (_decimationPlanRows。currentTris/カテゴリ付き)を作る。行の編集は都度
        //     _settings.decimationPlan(rendererPath+targetTris)へ同期し保存する。
        //   ・変換時は AvatarQuestConverter が _settings.decimationPlan を読んで簡略化を適用する。
        //   ・実装は Decimation 名前空間(PolygonBudgetPlanner / MeshDecimatorUnity)へ委譲する。
        // ================================================================

        /// <summary>ポリゴン削減の目標ランク別プリセット(Quest StatsLevels の polyCount。Excellent/Good/Medium/Poor)。</summary>
        private static readonly int[] DecimationRankPresets = { 7500, 10000, 15000, 20000 };

        /// <summary>目標ランク選択トグルのラベル(DecimationRankPresetsの並び順と一致させること)。</summary>
        private static readonly GUIContent[] DecimationRankLabels =
        {
            new GUIContent("Excellent 7,500", "最も厳しい目標。7,500ポリゴン以下(顔・髪を強く保護すると到達できないことがあります)"),
            new GUIContent("Good 10,000", "10,000ポリゴン以下"),
            new GUIContent("Medium 15,000", "Questの既定表示ランク。15,000ポリゴン以下"),
            new GUIContent("Poor 20,000", "アップロードできる上限。20,000ポリゴン以下(まずはここを目標にすると崩れにくい)"),
        };

        // カテゴリ別バッジ色(ダーク/ライト両スキンで読める中間トーン)
        private static readonly Color CategoryFaceColor = new Color(0.95f, 0.45f, 0.5f);   // 顔(最も強く保護)
        private static readonly Color CategoryHairColor = new Color(0.9f, 0.62f, 0.35f);   // 髪
        private static readonly Color CategoryBodyColor = new Color(0.5f, 0.72f, 0.9f);    // 素体
        private static readonly Color CategoryClothesColor = new Color(0.55f, 0.8f, 0.5f); // 衣装
        private static readonly Color CategoryOtherColor = new Color(0.72f, 0.72f, 0.76f); // その他

        /// <summary>削減計画のUI作業コピー(currentTris/カテゴリ付き。非シリアライズ。作成ボタンで構築)。</summary>
        private List<Decimation.PolygonPlanEntry> _decimationPlanRows;

        /// <summary>作成直後の自動配分目標値(rendererPath→targetTris)。行の「戻す」で復元する。</summary>
        private readonly Dictionary<string, int> _decimationSuggestedTargets = new Dictionary<string, int>();

        /// <summary>現在の作業計画を作成したときの目標ポリゴン数(-1=未作成)。目標変更検知に使う。</summary>
        private int _decimationPlanBuiltForTarget = -1;

        private void DrawPolygonReductionSection()
        {
            if (!DrawSectionFoldout(10, "ポリゴン削減",
                "目標ポリゴン数に合わせてメッシュを自動で簡略化します(顔・髪は強く保護)。",
                FoldKeyPolygon))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_settings == null) _settings = new QuestConvertSettings(); // 念のため(リロード直後など)
                EnsureDecimationSettingsLists();

                if (DrawExplainFoldout("RARA.QuestConverter.Fold.Explain.Polygon"))
                {
                    EditorGUILayout.HelpBox(
                        "頂点を間引いてポリゴン数を減らします。既存の頂点だけを残す方式なので、UV・法線・ボーンウェイト・" +
                        "ブレンドシェイプ(表情)はそのまま保持され、破綻を抑えられます。" +
                        "顔(リップシンク対象)と髪は既定で強く保護され、控えめにしか削減されません。" +
                        "元アバターは変更されず、削減は複製のメッシュにのみ適用されます。",
                        MessageType.Info);
                }

                // まず無料の削減を促す注記(同じ目標でも削る量が減り、画質を保ちやすくなる)
                EditorGUILayout.HelpBox(
                    "先に『メッシュ削減(AAO連携)』と『衣装・トグル整理』で不要なメッシュを消してから使うと、" +
                    "同じ目標でも削る量が減り、画質を保ちやすくなります。",
                    MessageType.None);

                // 有効化トグル
                EditorGUI.BeginChangeCheck();
                bool enable = EditorGUILayout.ToggleLeft(
                    new GUIContent("ポリゴン削減を有効にする",
                        "有効にすると、下で作成した削減計画が変換時に適用されます(無効なら計画があっても削減しません)"),
                    _settings.enableDecimation);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.enableDecimation = enable;
                    SaveSettings(); // 変換内容に影響する(診断は古い扱いになる)
                }

                if (!_settings.enableDecimation)
                {
                    EditorGUILayout.LabelField(
                        "有効にすると、目標ランクを選んで削減計画を作成できます(ポリゴン削減は既定でオフです)。", EditorStyles.miniLabel);
                    DrawDecimationDisabledOverBudgetHint();
                    return;
                }

                DrawDecimationTargetPicker();
                DrawDecimationPlanControls();
                DrawDecimationPlanTable();
            }
        }

        /// <summary>目標ランク(プリセット)トグル + 目標ポリゴン数の数値入力 + 現在→削減後(予測)の一行。</summary>
        private void DrawDecimationTargetPicker()
        {
            EditorGUILayout.Space(2f);

            // 目標ランクのトグル(プリセットへ同期。カスタム値のときは選択なし)
            int presetIndex = GetDecimationPresetIndex(_settings.decimationTargetTriangles);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    new GUIContent("目標ランク", "到達したいポリゴン数の目安。ボタンで目標ポリゴン数が設定されます"),
                    GUILayout.Width(72f));
                int newIndex = GUILayout.Toolbar(presetIndex, DecimationRankLabels);
                if (newIndex != presetIndex && newIndex >= 0 && newIndex < DecimationRankPresets.Length)
                {
                    _settings.decimationTargetTriangles = DecimationRankPresets[newIndex];
                    SaveSettings();
                }
            }

            // 目標ポリゴン数(数値での微調整。ランクボタンと双方向)
            EditorGUI.BeginChangeCheck();
            int newTarget = EditorGUILayout.IntField(
                new GUIContent("目標ポリゴン数", "この数値以下に収まるよう配分します(ランクボタンで既定値に設定できます)"),
                _settings.decimationTargetTriangles);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.decimationTargetTriangles = Mathf.Max(1, newTarget);
                SaveSettings();
            }

            // 現在 → 削減後(予測) / 目標(診断のポリゴン数=除外反映済みを基準にする)
            int? currentTotal = GetDiagnosticsPolyCount();
            if (!currentTotal.HasValue)
            {
                EditorGUILayout.LabelField("診断すると現在のポリゴン数が表示されます。", EditorStyles.miniLabel);
                return;
            }

            int projected = Mathf.Max(0, currentTotal.Value - ComputeDecimationReduction());
            bool over = projected > _settings.decimationTargetTriangles;
            var prev = GUI.color;
            GUI.color = over ? OverLimitColor : UploadOkColor;
            EditorGUILayout.LabelField(
                "現在 " + FormatTri(currentTotal.Value) + " → 削減後(予測) " + FormatTri(projected) +
                " / 目標 " + FormatTri(_settings.decimationTargetTriangles),
                _wrapLabel);
            GUI.color = prev;

            if (currentTotal.Value <= _settings.decimationTargetTriangles &&
                (_decimationPlanRows == null || _decimationPlanRows.Count == 0))
            {
                EditorGUILayout.LabelField(
                    "現在のポリゴン数は目標以下です。削減は不要です。", _miniWrapLabel);
            }

            // 作業計画は作成時の目標に合わせて配分済み。以後に目標を変えても計画は再配分されないため、
            // 目標が緩んだ(削り過ぎ)/厳しくなった(未達)どちらの向きでもズレを明示して再作成を促す。
            if (_decimationPlanRows != null && _decimationPlanRows.Count > 0 &&
                _decimationPlanBuiltForTarget > 0 &&
                _decimationPlanBuiltForTarget != _settings.decimationTargetTriangles)
            {
                EditorGUILayout.HelpBox(
                    "目標ポリゴン数が変更されました(計画は " + FormatTri(_decimationPlanBuiltForTarget) +
                    " 向けに配分済み)。現在の目標へ合わせるには「自動で配分計画を作成」で再作成してください。",
                    MessageType.Info);
            }
        }

        /// <summary>配分計画の作成/クリアボタンと、保存済み計画があるが作業コピーが無いときの案内。</summary>
        private void DrawDecimationPlanControls()
        {
            EditorGUILayout.Space(2f);
            if (_avatar == null)
            {
                EditorGUILayout.LabelField("アバターを指定すると削減計画を作成できます。", EditorStyles.miniLabel);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("自動で配分計画を作成",
                    "目標ポリゴン数に合わせて、メッシュごとの削減目標を自動配分します(顔・髪は保護)")))
                {
                    BuildDecimationPlan();
                    GUIUtility.ExitGUI(); // リストを差し替えたので、この描画パスはここで終える
                }
                using (new EditorGUI.DisabledScope(!HasDecimationPlan()))
                {
                    if (GUILayout.Button(new GUIContent("計画をクリア", "作成した削減計画を破棄します"), GUILayout.Width(90f)))
                    {
                        ClearDecimationPlan();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            // スクリプト再読込直後など、保存済み計画はあるが作業コピー(現在数・カテゴリ)が無いとき
            if (_decimationPlanRows == null && _settings.decimationPlan != null && _settings.decimationPlan.Count > 0)
            {
                EditorGUILayout.LabelField(
                    "保存済みの削減計画: " + _settings.decimationPlan.Count + " メッシュ。" +
                    "『自動で配分計画を作成』で再計算すると、メッシュごとに調整できます(計画は変換時に適用されます)。",
                    _miniWrapLabel);
            }
        }

        /// <summary>メッシュごとの削減目標テーブル(空=削減不要)と、目標超過の赤警告を描画する。</summary>
        private void DrawDecimationPlanTable()
        {
            if (_decimationPlanRows == null) return; // まだ作成していない(controls側で案内)
            if (_decimationPlanRows.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "削減対象のメッシュはありません(現在のポリゴン数が目標以下です)。", MessageType.Info);
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(
                "メッシュごとの削減目標(顔・髪は保護のため控えめに配分されます)", EditorStyles.miniBoldLabel);

            // 行の描画中はリストを変更せず、除外は行番号を控えてループ後に反映する(レイアウト崩れ防止)
            int removeIndex = -1;
            for (int i = 0; i < _decimationPlanRows.Count; i++)
            {
                if (DrawDecimationPlanRow(_decimationPlanRows[i])) removeIndex = i;
            }
            if (removeIndex >= 0)
            {
                _decimationPlanRows.RemoveAt(removeIndex);
                SyncDecimationPlanToSettings();
                SaveSettings();
                GUIUtility.ExitGUI();
            }

            DrawDecimationBudgetWarning();
        }

        /// <summary>削減計画1行(名前+カテゴリ+ピン+除外 / 現在→目標スライダー+戻す / 保護下限)を描画する。除外要求時にtrue。</summary>
        private bool DrawDecimationPlanRow(Decimation.PolygonPlanEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) return false;
            bool removeRequested = false;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        new GUIContent(ShortRendererName(entry.rendererPath), "パス: " + entry.rendererPath),
                        GUILayout.MinWidth(70f));
                    DrawCategoryBadge(entry.category);
                    GUILayout.FlexibleSpace();
                    DrawDecimationPingButton(entry.rendererPath);
                    if (GUILayout.Button(new GUIContent("除外", "このメッシュを削減対象から外します(元のまま残します)"),
                        GUILayout.Width(44f)))
                    {
                        removeRequested = true;
                    }
                }

                int current = Mathf.Max(1, entry.currentTris);
                int qualityMin = Mathf.Clamp(
                    Mathf.CeilToInt(current * Mathf.Clamp01(entry.qualityFloor)), 1, current);
                // 予算超過時、PolygonBudgetPlanner は非顔エントリを品質下限より下(サブメッシュ数まで)へ
                // 強制することがある。スライダー下限を品質下限で固定すると、実際の計画値を隠したうえ、
                // スライダーに触れた瞬間に値が品質下限へ吊り上がり、より浅い削減が保存されてしまう。
                // そこで実際の計画値(entry.targetTris)を下回らない範囲でスライダー下限を決める。
                int plannedTarget = Mathf.Clamp(entry.targetTris, 1, current);
                int sliderMin = Mathf.Min(qualityMin, plannedTarget);
                bool belowQualityFloor = sliderMin < qualityMin;

                if (sliderMin >= current)
                {
                    // 完全保護(品質下限が高く、これ以上は削れない)
                    EditorGUILayout.LabelField(
                        "現在 " + FormatTri(current) + " — 保護のため削減されません", EditorStyles.miniLabel);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("現在 " + FormatTri(current), GUILayout.Width(96f));
                        EditorGUILayout.LabelField("→", GUILayout.Width(14f));
                        EditorGUI.BeginChangeCheck();
                        int newTarget = EditorGUILayout.IntSlider(
                            Mathf.Clamp(entry.targetTris, sliderMin, current), sliderMin, current);
                        if (EditorGUI.EndChangeCheck())
                        {
                            entry.targetTris = Mathf.Clamp(newTarget, sliderMin, current);
                            SyncDecimationPlanToSettings();
                            SaveSettings();
                        }
                        using (new EditorGUI.DisabledScope(!HasSuggestedTarget(entry.rendererPath)))
                        {
                            if (GUILayout.Button(new GUIContent("戻す", "自動配分の目標値に戻します"), GUILayout.Width(44f)))
                            {
                                entry.targetTris = Mathf.Clamp(
                                    GetSuggestedTarget(entry.rendererPath, entry.targetTris), sliderMin, current);
                                SyncDecimationPlanToSettings();
                                SaveSettings();
                            }
                        }
                    }
                    EditorGUILayout.LabelField(
                        belowQualityFloor
                            ? "下限 " + FormatTri(sliderMin) + "(予算超過のため品質下限を下回る配分です)"
                            : "下限 " + FormatTri(sliderMin) + "(保護のためこれより下げられません)",
                        EditorStyles.miniLabel);
                }
            }
            return removeRequested;
        }

        /// <summary>
        /// ポリゴン削減がオフのまま、現在のポリゴン数が目標を超えているときに、削減の有効化が必要である旨を
        /// 琥珀で知らせ「有効にする」ボタンを出す(既定オフのまま超過に気づけるように)。診断前は何も出さない。
        /// </summary>
        private void DrawDecimationDisabledOverBudgetHint()
        {
            int? currentTotal = GetDiagnosticsPolyCount();
            if (!currentTotal.HasValue || currentTotal.Value <= _settings.decimationTargetTriangles) return;

            var prev = GUI.color;
            GUI.color = NoteYellowColor;
            EditorGUILayout.LabelField(
                "目標ランク到達にはポリゴン削減の有効化が必要です(現在 " + FormatTri(currentTotal.Value) +
                " / 目標 " + FormatTri(_settings.decimationTargetTriangles) + ")。",
                _wrapLabel);
            GUI.color = prev;

            if (GUILayout.Button(new GUIContent("ポリゴン削減を有効にする",
                "ポリゴン削減を有効化します(既定はオフ。顔・髪は強く保護されます)"), GUILayout.Height(24f)))
            {
                _settings.enableDecimation = true;
                SaveSettings(); // 変換内容に影響する(診断は古い扱いになる)
            }
        }

        /// <summary>計画を適用しても目標を超える場合の赤警告(顔は保護のため削れないことを添える)。</summary>
        private void DrawDecimationBudgetWarning()
        {
            int? currentTotal = GetDiagnosticsPolyCount();
            if (!currentTotal.HasValue) return; // 現在値が無ければ判定不能
            int projected = Mathf.Max(0, currentTotal.Value - ComputeDecimationReduction());
            if (projected <= _settings.decimationTargetTriangles) return;

            var prev = GUI.color;
            GUI.color = OverLimitColor;
            EditorGUILayout.HelpBox(
                "この計画を適用しても、予測 " + FormatTri(projected) + " ポリゴンで目標 " +
                FormatTri(_settings.decimationTargetTriangles) + " を超えています。\n" +
                "顔以外のメッシュの目標をさらに下げるか、メッシュ削減(AAO連携)・衣装整理・Quest除外で先に減らしてください" +
                "(顔は表情保護のため強くは削れません)。",
                MessageType.Warning);
            GUI.color = prev;
        }

        /// <summary>カテゴリ名(顔/髪/素体/衣装/その他)に応じた色付きバッジを描画する。</summary>
        private void DrawCategoryBadge(string category)
        {
            string label = string.IsNullOrEmpty(category) ? Decimation.PolygonBudgetPlanner.CategoryOther : category;
            Color color;
            string tip;
            if (label == Decimation.PolygonBudgetPlanner.CategoryFace)
            {
                color = CategoryFaceColor;
                tip = "顔(リップシンク/表情対象)。既定で最も強く保護され、控えめにしか削減されません";
            }
            else if (label == Decimation.PolygonBudgetPlanner.CategoryHair)
            {
                color = CategoryHairColor;
                tip = "髪。形が崩れやすいため強めに保護されます";
            }
            else if (label == Decimation.PolygonBudgetPlanner.CategoryBody)
            {
                color = CategoryBodyColor;
                tip = "素体(肌)";
            }
            else if (label == Decimation.PolygonBudgetPlanner.CategoryClothes)
            {
                color = CategoryClothesColor;
                tip = "衣装・アクセサリ";
            }
            else
            {
                color = CategoryOtherColor;
                tip = "その他のメッシュ";
            }
            DrawBadge(label, color, tip);
        }

        /// <summary>対象レンダラー(rendererPath)の指すGameObjectをピン表示するボタンを描画する。</summary>
        private void DrawDecimationPingButton(string rendererPath)
        {
            Transform resolved = _avatar != null ? QuestCompat.FindByPath(_avatar.transform, rendererPath) : null;
            using (new EditorGUI.DisabledScope(resolved == null))
            {
                if (GUILayout.Button(new GUIContent("ピン", "シーン上の該当メッシュをハイライト表示します"), GUILayout.Width(36f)))
                {
                    if (resolved != null) EditorGUIUtility.PingObject(resolved.gameObject);
                }
            }
        }

        /// <summary>「自動で配分計画を作成」: PolygonBudgetPlannerで配分し、作業コピーと設定へ反映する。</summary>
        private void BuildDecimationPlan()
        {
            if (_avatar == null) return;
            try
            {
                List<Decimation.PolygonPlanEntry> plan =
                    Decimation.PolygonBudgetPlanner.BuildPlan(
                        _avatar.gameObject, _settings.decimationTargetTriangles, _settings);
                _decimationPlanRows = plan ?? new List<Decimation.PolygonPlanEntry>();

                // 自動配分値を控えておく(行の「戻す」で復元する)
                _decimationSuggestedTargets.Clear();
                foreach (Decimation.PolygonPlanEntry e in _decimationPlanRows)
                {
                    if (e == null || string.IsNullOrEmpty(e.rendererPath)) continue;
                    _decimationSuggestedTargets[e.rendererPath] = e.targetTris;
                }

                _decimationPlanBuiltForTarget = _settings.decimationTargetTriangles;
                SyncDecimationPlanToSettings();
                SaveSettings();
            }
            catch (Exception ex)
            {
                _decimationPlanRows = null;
                _decimationPlanBuiltForTarget = -1;
                Debug.LogError("[RARA QuestConverter] 削減計画の作成に失敗しました: " + ex);
                EditorUtility.DisplayDialog("ポリゴン削減",
                    "削減計画の作成に失敗しました。Consoleを確認してください。", "OK");
            }
        }

        /// <summary>作成した削減計画を破棄する(作業コピー・保存計画の両方)。</summary>
        private void ClearDecimationPlan()
        {
            _decimationPlanRows = null;
            _decimationPlanBuiltForTarget = -1;
            _decimationSuggestedTargets.Clear();
            if (_settings.decimationPlan != null) _settings.decimationPlan.Clear();
            SaveSettings();
        }

        /// <summary>UI作業コピー(_decimationPlanRows)を保存用の decimationPlan(パス+目標数)へ同期する。</summary>
        private void SyncDecimationPlanToSettings()
        {
            EnsureDecimationSettingsLists();
            _settings.decimationPlan.Clear();
            if (_decimationPlanRows == null) return;
            foreach (Decimation.PolygonPlanEntry e in _decimationPlanRows)
            {
                if (e == null || string.IsNullOrEmpty(e.rendererPath)) continue;
                _settings.decimationPlan.Add(new PolygonPlanEntryData
                {
                    rendererPath = e.rendererPath,
                    targetTris = e.targetTris,
                });
            }
        }

        /// <summary>設定の削減計画リストのnullガード(旧設定JSON読込対策)。</summary>
        private void EnsureDecimationSettingsLists()
        {
            if (_settings.decimationPlan == null) _settings.decimationPlan = new List<PolygonPlanEntryData>();
        }

        /// <summary>作業コピーまたは保存計画のいずれかに削減計画があるか。</summary>
        private bool HasDecimationPlan()
        {
            if (_decimationPlanRows != null && _decimationPlanRows.Count > 0) return true;
            return _settings != null && _settings.decimationPlan != null && _settings.decimationPlan.Count > 0;
        }

        /// <summary>作業コピーの各行 (currentTris - targetTris) の合計 = 予測削減三角形数。</summary>
        private int ComputeDecimationReduction()
        {
            if (_decimationPlanRows == null) return 0;
            int total = 0;
            foreach (Decimation.PolygonPlanEntry e in _decimationPlanRows)
            {
                if (e == null) continue;
                int cur = Mathf.Max(0, e.currentTris);
                int tgt = Mathf.Clamp(e.targetTris, 0, cur);
                total += cur - tgt;
            }
            return total;
        }

        /// <summary>診断結果のポリゴン数(除外反映済み)を取り出す。未診断・解析不可ならnull。</summary>
        private int? GetDiagnosticsPolyCount()
        {
            if (_diagnostics == null || _diagnostics.perfRows == null) return null;
            foreach (DiagnosticsRow row in _diagnostics.perfRows)
            {
                if (row != null && row.category == "ポリゴン数")
                {
                    return TryParsePerfValue(row.value, out float v) ? Mathf.RoundToInt(v) : (int?)null;
                }
            }
            return null;
        }

        /// <summary>rendererPath に自動配分の控え値があるか。</summary>
        private bool HasSuggestedTarget(string rendererPath)
        {
            return rendererPath != null && _decimationSuggestedTargets.ContainsKey(rendererPath);
        }

        /// <summary>rendererPath の自動配分の控え値(無ければfallback)。</summary>
        private int GetSuggestedTarget(string rendererPath, int fallback)
        {
            if (rendererPath != null && _decimationSuggestedTargets.TryGetValue(rendererPath, out int v)) return v;
            return fallback;
        }

        /// <summary>レンダラー相対パスの末尾セグメント(表示用の短い名前)。</summary>
        private static string ShortRendererName(string rendererPath)
        {
            if (string.IsNullOrEmpty(rendererPath)) return "(不明)";
            int slash = rendererPath.LastIndexOf('/');
            return slash >= 0 && slash < rendererPath.Length - 1 ? rendererPath.Substring(slash + 1) : rendererPath;
        }

        /// <summary>目標ポリゴン数がプリセット(7500/10000/15000/20000)のどれかなら添字、なければ-1(カスタム)。</summary>
        private static int GetDecimationPresetIndex(int target)
        {
            for (int i = 0; i < DecimationRankPresets.Length; i++)
            {
                if (DecimationRankPresets[i] == target) return i;
            }
            return -1;
        }

        /// <summary>三角形数を桁区切り付きで表示する("15,000")。</summary>
        private static string FormatTri(int n)
        {
            return n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ================================================================
        // セクション11: 実行(実行前チェック + 生成 + レポート)
        // ================================================================
        private void DrawConvertSection()
        {
            DrawSectionHeader(11, "実行",
                "設定内容を確認して、Quest対応の複製を生成します(元アバターは変更しません)。");
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawPreflightBox();

                using (new EditorGUI.DisabledScope(_avatar == null))
                {
                    if (GUILayout.Button(new GUIContent("Quest対応版を生成",
                        "アバターの複製を作成してQuest対応へ変換します(元アバターは変更されません)"), GUILayout.Height(30f)))
                    {
                        RunConversion();
                    }
                }
                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("対象アバターを指定すると実行できます。", EditorStyles.miniLabel);
                }

                DrawReport();
            }
        }

        /// <summary>実行前チェック(ビルドターゲット・診断サマリー・変換内容の要約)を表示する。</summary>
        private void DrawPreflightBox()
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("実行前チェック", EditorStyles.miniBoldLabel);
                var defaultColor = GUI.color;

                // ビルドターゲット(Android以外なら注意)
                var buildTarget = EditorUserBuildSettings.activeBuildTarget;
                if (buildTarget == BuildTarget.Android)
                {
                    EditorGUILayout.LabelField("・ビルドターゲット: Android", _wrapLabel);
                }
                else
                {
                    GUI.color = NoteYellowColor;
                    EditorGUILayout.LabelField("・ビルドターゲット: " + buildTarget, _wrapLabel);
                    GUI.color = defaultColor;
                    EditorGUILayout.LabelField("  アップロード時にAndroidへ切替が必要(SDKコントロールパネル)", _miniWrapLabel);
                }

                // 診断サマリー(ランク + 可否)
                if (_diagnostics == null)
                {
                    GUI.color = NoteYellowColor;
                    EditorGUILayout.LabelField("・診断: 未実行(生成時に自動で診断されます)", _wrapLabel);
                    GUI.color = defaultColor;
                }
                else
                {
                    string rank = string.IsNullOrEmpty(_diagnostics.overallRating) ? "不明" : _diagnostics.overallRating;
                    bool ok = _diagnostics.canUploadToAndroid;
                    GUI.color = ok ? UploadOkColor : OverLimitColor;
                    EditorGUILayout.LabelField(
                        "・診断: 総合ランク " + rank + " / Android アップロード: " + (ok ? "可" : "不可(Very Poor)"),
                        _wrapLabel);
                    GUI.color = defaultColor;
                    if (_diagnosisStale)
                    {
                        GUI.color = NoteYellowColor;
                        EditorGUILayout.LabelField("  設定変更あり: 生成時に自動で再診断されます", _miniWrapLabel);
                        GUI.color = defaultColor;
                    }
                }

                // この変換で行われること(マテリアルプレビューからの自動要約)
                EditorGUILayout.LabelField("この変換で行われること:", EditorStyles.miniBoldLabel);
                if (_avatar == null)
                {
                    EditorGUILayout.LabelField("(アバターを指定すると表示されます)", EditorStyles.miniLabel);
                }
                else if (_materialPreview == null)
                {
                    EditorGUILayout.LabelField("(診断後に表示されます)", EditorStyles.miniLabel);
                }
                else
                {
                    foreach (string line in BuildConversionSummaryLines())
                    {
                        EditorGUILayout.LabelField("・" + line, _miniWrapLabel);
                    }
                }
            }
        }

        /// <summary>
        /// マテリアルプレビューと個別設定から「この変換で行われること」の要約行を作る
        /// (実行前チェックと確認ダイアログで共用)。
        /// </summary>
        private List<string> BuildConversionSummaryLines()
        {
            var lines = new List<string>();
            if (_settings == null) _settings = new QuestConvertSettings();

            // 変換モード
            bool consolidateOnly = _settings.conversionMode == ConversionMode.ConsolidateOnly;
            lines.Add(consolidateOnly
                ? "変換モード: PC最適化のみ(シェーダー/テクスチャ変換なし)"
                : "変換モード: Quest対応(シェーダー変換あり)");

            if (consolidateOnly)
            {
                // PC最適化のみ: シェーダー/テクスチャ/アトラスは変換しないため件数要約は出さない
                lines.Add("シェーダー/テクスチャ/アトラスは変換せず、現状のマテリアルを維持します");
            }
            else if (_materialPreview != null)
            {
                int toonStandard = 0, toonLit = 0, particle = 0, hide = 0, keep = 0, atlasTargets = 0;
                int transEmulate = 0, transHide = 0, transOpaque = 0; // 半透明(アルファブレンド)マテリアルの扱いの内訳
                foreach (MaterialPreviewRow row in _materialPreview)
                {
                    if (row == null || row.material == null) continue;
                    MaterialOverrideEntry entry = FindOverrideEntry(row.material, false);
                    MaterialOverride mode = entry != null ? entry.mode : MaterialOverride.Auto;

                    // 自動(Auto)のときに透過マテリアルが辿る既定処理。髪・大型メッシュで保護される
                    // 透過(suppressTransparentHide)は常に不透明変換になる。
                    TransparentHandling autoFate = row.suppressTransparentHide
                        ? TransparentHandling.Opaque
                        : _settings.transparentHandling;

                    switch (mode)
                    {
                        case MaterialOverride.ToonStandard: toonStandard++; break;
                        case MaterialOverride.ToonLit: toonLit++; break;
                        case MaterialOverride.ParticleAdditive:
                        case MaterialOverride.ParticleMultiply: particle++; break;
                        case MaterialOverride.Hide: hide++; break;
                        case MaterialOverride.Keep: keep++; break;
                        default:
                            // 自動判定の既定動作を要約用に再現する(実際の変換はコンバーター側の判定に従う)
                            if (row.isMobileAlready || row.isTMP || row.isBrokenShader) keep++;
                            // 効果専用シェーダー(疑似影/アウトライン)は自動で非表示化される(Convert step 6.5)
                            else if (QuestCompat.IsOverlayOnlyShader(row.material.shader, row.material.name)) hide++;
                            else if (row.usedByParticle && !row.usedByMesh) particle++;
                            else if (row.transparency == QuestCompat.TransparencyClass.Transparent)
                            {
                                // 既定処理に従って 再現(パーティクル)/非表示/不透明 へ振り分ける
                                if (autoFate == TransparentHandling.Emulate) particle++;
                                else if (autoFate == TransparentHandling.Hide) hide++;
                                else if (_settings.shaderTarget == QuestShaderTarget.ToonLit) toonLit++;
                                else toonStandard++;
                            }
                            else if (_settings.shaderTarget == QuestShaderTarget.ToonLit) toonLit++;
                            else toonStandard++;
                            break;
                    }

                    // 半透明(アルファブレンド)マテリアルの扱いの内訳。パーティクル専用は元から
                    // Mobile/Particlesへ変換され透過表示できるため集計対象外にする。
                    if (row.transparency == QuestCompat.TransparencyClass.Transparent &&
                        !(row.usedByParticle && !row.usedByMesh))
                    {
                        switch (mode)
                        {
                            case MaterialOverride.ParticleAdditive:
                            case MaterialOverride.ParticleMultiply: transEmulate++; break;
                            case MaterialOverride.Hide: transHide++; break;
                            case MaterialOverride.ToonStandard:
                            case MaterialOverride.ToonLit:
                            case MaterialOverride.Keep: transOpaque++; break;
                            default: // 自動 → 既定処理(保護時は不透明)
                                if (autoFate == TransparentHandling.Emulate) transEmulate++;
                                else if (autoFate == TransparentHandling.Hide) transHide++;
                                else transOpaque++;
                                break;
                        }
                    }

                    if (_settings.enableAtlas && row.atlasEligible && !(entry != null && entry.excludeFromAtlas))
                    {
                        atlasTargets++;
                    }
                }
                if (toonStandard > 0) lines.Add(toonStandard + "件を Toon Standard へ変換");
                if (toonLit > 0) lines.Add(toonLit + "件を Toon Lit へ変換");
                if (particle > 0) lines.Add(particle + "件をパーティクル(加算/乗算)へ変換");
                if (hide > 0) lines.Add(hide + "件を非表示化");
                if (keep > 0) lines.Add(keep + "件はそのまま(変換なし)");

                // 半透明の扱いの内訳(再現/非表示/不透明)を1行で示す
                if (transEmulate + transHide + transOpaque > 0)
                {
                    lines.Add("半透明の扱い: 再現(乗算/加算) " + transEmulate + " 件 / 非表示 " + transHide +
                              " 件 / 不透明 " + transOpaque + " 件");
                }

                // 診断が古い間は対象件数も古い可能性があるため注記する(実行時は自動再診断で更新される)
                lines.Add(_settings.enableAtlas
                    ? "アトラス統合: 有効(対象 " + atlasTargets + " 件" + (_diagnosisStale ? "・再診断で更新されます" : "") + ")"
                    : "アトラス統合: 無効");

                // 表情デカール(チーク/涙/アイハイライト)の非表示化件数。
                // Emulate では PreviewExpressionDecals が空リストを返すため、ここに載るのは Hide / Opaque のみ
                // (Emulateでの再現件数は上の「半透明の扱い: 再現(乗算/加算) N件」で示している)。
                if (_expressionDecals != null && _expressionDecals.Count > 0)
                {
                    lines.Add("表情デカール(チーク/涙/アイハイライト)を非表示化: " + _expressionDecals.Count + " 件(顔本体・目・眉は残す)");
                }
            }

            // PhysBoneの稼働/マージ/削除の予定(セクション4のキャッシュ済みプレビューと設定から算出)
            if (_settings.physBoneSelectionMode == PhysBoneSelectionMode.OptIn)
            {
                // 選択制(既定): 稼働選択された数(マージ後)を示す。未選択なら全停止。
                if (_physBonePreview == null)
                {
                    lines.Add("PhysBone: 稼働する揺れものを選択(マージ後の一覧・再診断で確定)");
                }
                else
                {
                    int keptCount = ComputePhysBoneProjectedKeptCount();
                    if (keptCount <= 0)
                    {
                        lines.Add("PhysBone: 未選択のため全停止(すべて削除・揺れなくなります)");
                    }
                    else
                    {
                        string physBoneLine = "PhysBone: 稼働 " + keptCount + "個を選択(マージ後)";
                        if (keptCount > QuestLimits.PoorPhysBoneComponents)
                        {
                            physBoneLine += " ※Poor上限" + QuestLimits.PoorPhysBoneComponents + "超過";
                        }
                        lines.Add(physBoneLine);
                    }
                }
            }
            else
            {
                var physBoneParts = new List<string>();
                if (_settings.mergePhysBones && _physBonePreview != null &&
                    _physBonePreview.projectedComponentCount < _physBonePreview.currentComponentCount)
                {
                    physBoneParts.Add("マージで " + _physBonePreview.currentComponentCount + "→" + _physBonePreview.projectedComponentCount);
                }
                int physBoneRemoveCount = CountResolvablePhysBonePaths(_settings.physBoneRemovePaths);
                if (physBoneRemoveCount > 0)
                {
                    physBoneParts.Add("手動削除 " + physBoneRemoveCount + "件");
                }
                if (physBoneParts.Count > 0)
                {
                    lines.Add("PhysBone: " + string.Join(" / ", physBoneParts));
                }
            }

            // 衣装・トグル整理(表示固定=結合対象 / 非表示固定=削除)。設定に保存された選択から集計する。
            if (_settings.toggleChoices != null && _settings.toggleChoices.Count > 0)
            {
                int lockVisible = 0, lockHidden = 0;
                foreach (ToggleGroupChoice choice in _settings.toggleChoices)
                {
                    if (choice == null) continue;
                    if (choice.choice == ToggleLockChoice.LockVisible) lockVisible++;
                    else if (choice.choice == ToggleLockChoice.LockHidden) lockHidden++;
                }
                if (lockVisible + lockHidden > 0)
                {
                    lines.Add("衣装・トグル整理: 表示で固定 " + lockVisible + " / 非表示で固定 " + lockHidden +
                              "(トグルを外してメッシュ・スロットを削減)");
                }
            }

            // メッシュ削減(AAO連携): 隠れた肌などのブレンドシェイプ削除(対象メッシュを選択済みのときのみ)
            if (_settings.removeHiddenMeshByBlendShape)
            {
                int hiddenMeshCount = _settings.hiddenMeshRendererPaths != null ? _settings.hiddenMeshRendererPaths.Count : 0;
                if (hiddenMeshCount > 0)
                {
                    lines.Add("メッシュ削減: 隠れた肌など " + hiddenMeshCount + " メッシュをブレンドシェイプ削除(AAO・ビルド時)");
                }
            }

            // SkinnedMesh統合(顔以外を1つへ): 有効かつアバター指定時のみ想定SMR数を示す
            if (_settings.mergeSkinnedMeshesMode != SkinnedMeshMergeMode.None && _avatar != null)
            {
                SkinnedMeshMergePlan mergePlan = SkinnedMeshMergePlanner.BuildPlan(
                    _avatar.gameObject, _settings.mergeSkinnedMeshesMode, _settings.skinnedMeshMergeOptOutPaths);
                if (mergePlan.WillMergeAnything)
                {
                    lines.Add("SkinnedMesh統合: 顔以外を1つへ統合(想定 " + mergePlan.beforeCount + "→" + mergePlan.expectedCount + " ・AAO・ビルド時)");
                }
            }

            // ポリゴン削減: 有効かつ配分計画があるときのみ(顔・髪は保護)
            if (_settings.enableDecimation && _settings.decimationPlan != null && _settings.decimationPlan.Count > 0)
            {
                lines.Add("ポリゴン削減: " + _settings.decimationPlan.Count + " メッシュを簡略化(目標 " +
                          FormatTri(_settings.decimationTargetTriangles) + " ポリゴン・顔/髪は保護)");
            }

            int excludeCount = _settings.questExcludePaths != null ? _settings.questExcludePaths.Count : 0;
            lines.Add("Quest除外オブジェクト: " + excludeCount + " 件");
            return lines;
        }

        /// <summary>
        /// 変換を実行する。診断が未実行・古い場合は先に自動で再診断し、
        /// 確認ダイアログに「この変換で行われること」の要約を含める。
        /// </summary>
        private void RunConversion()
        {
            if (_avatar == null) return;
            if (_settings == null) _settings = new QuestConvertSettings();

            // 診断が未実行・古い場合は自動で診断してから進む(古い前提での変換を防ぐ)
            if (_diagnostics == null || _diagnosisStale)
            {
                RunDiagnostics();
                if (_diagnostics == null) return; // 診断失敗(RunDiagnostics内でダイアログ表示済み)
            }

            string shaderName = _settings.shaderTarget == QuestShaderTarget.ToonLit
                ? QuestCompat.ToonLitShaderName
                : QuestCompat.ToonStandardShaderName;

            var summary = new System.Text.StringBuilder();
            foreach (string line in BuildConversionSummaryLines())
            {
                summary.AppendLine("・" + line);
            }

            bool ok = EditorUtility.DisplayDialog(
                "Quest対応版の生成",
                "アバター「" + _avatar.gameObject.name + "」のQuest対応版を生成します。\n\n" +
                "・アバターの複製を作成して変換します。元のアバターは変更しません。\n" +
                "・変換先シェーダー: " + shaderName + "\n" +
                "・生成先: " + _settings.outputFolder + "\n\n" +
                "【この変換で行われること】\n" + summary +
                "\n実行しますか?",
                "生成する", "キャンセル");
            if (!ok) return;

            SaveSettings(false); // 実行直前の保存(設定は変わっていないためstaleにしない)

            var report = new ConversionReport();
            GameObject converted = null;
            try
            {
                converted = AvatarQuestConverter.Convert(_avatar, _settings, report);
            }
            catch (Exception ex)
            {
                // 例外が起きてもウィンドウは動作を継続し、レポートへエラーとして残す
                report.Error("変換中に例外が発生しました: " + ex.Message + "\n" + ex.StackTrace);
            }

            _lastReport = report;
            _convertedAvatar = converted;
            _resultPairs = null; // 変換結果チェックのキャッシュを破棄(次にフォルドアウトを開いたとき再構築)
            _reportScroll = Vector2.zero;

            if (converted != null)
            {
                Selection.activeGameObject = converted;
                EditorGUIUtility.PingObject(converted);
            }
            Repaint();
        }

        /// <summary>直近の変換レポートを表示フィルター(全て/警告以上)付きスクロール領域で表示する。</summary>
        private void DrawReport()
        {
            if (_lastReport == null) return;

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("変換レポート", EditorStyles.boldLabel, GUILayout.Width(90f));
                GUILayout.FlexibleSpace();

                // 表示フィルター(全て / 警告以上)
                if (GUILayout.Toggle(!_reportWarnOnly, new GUIContent("全て", "すべての項目を表示します"),
                    EditorStyles.miniButtonLeft, GUILayout.Width(46f)) && _reportWarnOnly)
                {
                    _reportWarnOnly = false;
                }
                if (GUILayout.Toggle(_reportWarnOnly, new GUIContent("警告以上", "警告とエラーのみ表示します"),
                    EditorStyles.miniButtonRight, GUILayout.Width(66f)) && !_reportWarnOnly)
                {
                    _reportWarnOnly = true;
                }

                if (GUILayout.Button(new GUIContent("レポートをコピー", "レポート全文(フィルター前)をクリップボードへコピーします"), GUILayout.Width(120f)))
                {
                    EditorGUIUtility.systemCopyBuffer = _lastReport.ToText();
                    ShowNotification(new GUIContent("レポートをコピーしました"));
                }
            }

            if (_convertedAvatar != null)
            {
                using (new EditorGUI.DisabledScope(true)) // 読み取り専用表示
                {
                    EditorGUILayout.ObjectField(new GUIContent("生成されたアバター"), _convertedAvatar, typeof(GameObject), true);
                }
            }

            string summary = _lastReport.HasErrors
                ? "エラーがあります。レポートを確認してください。"
                : (_lastReport.WarningCount > 0
                    ? "完了しました(警告 " + _lastReport.WarningCount + " 件)。"
                    : "完了しました。");
            EditorGUILayout.HelpBox(summary,
                _lastReport.HasErrors ? MessageType.Error
                : (_lastReport.WarningCount > 0 ? MessageType.Warning : MessageType.Info));

            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                _reportScroll = EditorGUILayout.BeginScrollView(_reportScroll, GUILayout.Height(150f));
                int shownCount = 0;
                foreach (var entry in _lastReport.entries)
                {
                    if (_reportWarnOnly && entry.severity == ConversionReport.Severity.Info) continue;
                    shownCount++;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(SeverityIcon(entry.severity), GUILayout.Width(20f), GUILayout.Height(18f));
                        EditorGUILayout.LabelField(entry.message ?? "", _wrapLabel);
                    }
                }
                if (shownCount == 0)
                {
                    EditorGUILayout.LabelField(
                        _reportWarnOnly ? "(警告・エラーはありません)" : "(レポート項目なし)",
                        EditorStyles.miniLabel);
                }
                EditorGUILayout.EndScrollView();
            }

            DrawResultCheckSection();
        }

        // ================================================================
        // 変換結果チェック(元マテリアル ← → 変換後マテリアルの見比べ)
        // ================================================================

        /// <summary>変換結果チェックの表示上限行数。</summary>
        private const int ResultCheckRowMax = 30;

        /// <summary>変換結果チェックの1行(元マテリアルと変換後マテリアルのペア)。</summary>
        private class ConvertedPairRow
        {
            public Material source;              // 元マテリアル(名前から特定できない場合はnull)
            public Material converted;           // 変換後マテリアル(出力フォルダ配下)
            public string note;                  // レポートから抽出した一言メモ(該当なしはnull)
            public Texture2D sourcePreview;      // AssetPreviewのキャッシュ(非同期ロード完了後に保持)
            public Texture2D convertedPreview;   // 同上
        }

        /// <summary>
        /// 変換成功後に、元マテリアルと変換後マテリアルをサムネイル付きで見比べるフォルドアウト。
        /// 行の構築とサムネイル取得はフォルドアウトを開くまで行わない(軽量化)。
        /// AssetPreviewは非同期ロードのため、ロード中はミニサムネイルで代用しつつRepaintで更新する。
        /// </summary>
        private void DrawResultCheckSection()
        {
            if (_lastReport == null || _convertedAvatar == null) return;

            EditorGUILayout.Space(4f);
            _foldResultCheck = EditorGUILayout.Foldout(_foldResultCheck,
                "変換結果チェック(元 ← → 変換後)", true);
            if (!_foldResultCheck) return;

            if (_resultPairs == null) _resultPairs = BuildResultPairs();

            using (new EditorGUI.IndentLevelScope())
            {
                if (_resultPairs.Count == 0)
                {
                    EditorGUILayout.LabelField("出力フォルダ配下の変換後マテリアルが見つかりませんでした。", EditorStyles.miniLabel);
                    return;
                }

                int shown = Mathf.Min(_resultPairs.Count, ResultCheckRowMax);
                EditorGUILayout.LabelField(
                    "変換されたマテリアルの見比べ一覧です(" + shown + "/" + _resultPairs.Count + " 件)。サムネイルをクリックするとピン表示します。",
                    _miniWrapLabel);
                for (int i = 0; i < shown; i++)
                {
                    DrawResultPairRow(_resultPairs[i]);
                }
                if (_resultPairs.Count > shown)
                {
                    EditorGUILayout.LabelField("(残り " + (_resultPairs.Count - shown) + " 件は省略。全件はレポートを確認してください)", EditorStyles.miniLabel);
                }
            }

            // AssetPreviewは非同期で生成される。ロード中は再描画を要求してサムネイルを差し替える。
            if (AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        /// <summary>
        /// 生成アバターのレンダラーから出力フォルダ配下のマテリアルを集め、
        /// 名前の前方一致({元名}_Quest / _QuestHidden。アトラスはRARA_Atlas_*で対象外)で
        /// 元マテリアル(マテリアル設定テーブルの行)とペアにする。特定できない場合は変換後のみ表示する。
        /// </summary>
        private List<ConvertedPairRow> BuildResultPairs()
        {
            var pairs = new List<ConvertedPairRow>();
            if (_convertedAvatar == null || _settings == null || string.IsNullOrEmpty(_settings.outputFolder)) return pairs;

            string outputRoot = _settings.outputFolder.Replace('\\', '/').TrimEnd('/') + "/";
            var seen = new HashSet<Material>();
            foreach (Renderer renderer in _convertedAvatar.GetComponentsInChildren<Renderer>(true))
            {
                Material[] shared = renderer.sharedMaterials;
                if (shared == null) continue;
                foreach (Material mat in shared)
                {
                    if (mat == null || !seen.Add(mat)) continue;
                    string path = AssetDatabase.GetAssetPath(mat);
                    if (string.IsNullOrEmpty(path) ||
                        !path.Replace('\\', '/').StartsWith(outputRoot, StringComparison.Ordinal))
                    {
                        continue; // 出力フォルダ配下の生成マテリアルのみ対象(未変換・SDK既定などは除く)
                    }

                    var pair = new ConvertedPairRow { converted = mat };
                    pair.source = FindSourceMaterialForConverted(mat.name);
                    pair.note = BuildResultNote(pair);
                    pairs.Add(pair);
                }
            }
            // 変換後マテリアル名で安定ソート(毎回同じ並びで見比べられるように)
            pairs.Sort((a, b) => string.CompareOrdinal(a.converted.name, b.converted.name));
            return pairs;
        }

        /// <summary>
        /// 変換後マテリアル名から元マテリアルを推定する(マテリアル設定テーブルの行から前方一致で検索)。
        /// FinalizeMaterialの命名規則 {元名}{_Quest|_QuestHidden}(重複時は末尾に " 1" 等)に合わせ、
        /// "{元名}_Quest" で始まるものを一致とみなす。複数一致時は元名が最長のものを採用する
        /// (例: "Body" と "Body2" では "Body2_Quest" を "Body2" に対応させる)。
        /// アトラス統合の生成物(RARA_Atlas_*)はどれにも一致せず、変換後のみの行になる。
        /// </summary>
        private Material FindSourceMaterialForConverted(string convertedName)
        {
            if (string.IsNullOrEmpty(convertedName) || _materialPreview == null) return null;

            Material best = null;
            int bestLength = -1;
            foreach (MaterialPreviewRow row in _materialPreview)
            {
                if (row == null || row.material == null) continue;
                string rawName = row.material.name;
                // アセットファイル名は使えない文字が '_' に置換されるため、両方の形で照合する
                string sanitized = QuestConverterUtility.SanitizeAssetName(rawName);
                bool matched =
                    convertedName.StartsWith(rawName + "_Quest", StringComparison.Ordinal) ||
                    convertedName.StartsWith(sanitized + "_Quest", StringComparison.Ordinal);
                if (matched && rawName.Length > bestLength)
                {
                    best = row.material;
                    bestLength = rawName.Length;
                }
            }
            return best;
        }

        /// <summary>
        /// レポートからこのマテリアルに関する一言メモを抽出する(リム無効化・透過→不透明・非表示化など)。
        /// 警告・エラーを優先し、なければ定型の変換完了行以外の情報、それもなければ変換完了行を使う。
        /// </summary>
        private string BuildResultNote(ConvertedPairRow pair)
        {
            if (_lastReport == null || pair == null) return null;
            string key = pair.source != null ? pair.source.name
                : (pair.converted != null ? pair.converted.name : null);
            if (string.IsNullOrEmpty(key)) return null;

            string firstInfo = null;    // 定型の変換完了行以外で最初の情報
            string genericInfo = null;  // 定型の変換完了行("...へ変換しました: パス")
            foreach (ConversionReport.Entry entry in _lastReport.entries)
            {
                string message = entry.message;
                if (string.IsNullOrEmpty(message) || message.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                if (entry.severity != ConversionReport.Severity.Info)
                {
                    return ToSingleLineNote(message); // 最初の警告・エラーを最優先
                }
                if (message.IndexOf("へ変換しました", StringComparison.Ordinal) >= 0)
                {
                    if (genericInfo == null) genericInfo = message;
                }
                else if (firstInfo == null)
                {
                    firstInfo = message;
                }
            }
            string picked = firstInfo != null ? firstInfo : genericInfo;
            return picked != null ? ToSingleLineNote(picked) : null;
        }

        /// <summary>レポート文を1行の短いメモへ整形する(改行除去+長文は省略)。</summary>
        private static string ToSingleLineNote(string message)
        {
            const int maxLength = 90;
            string line = message.Replace("\r", "").Replace('\n', ' ');
            return line.Length <= maxLength ? line : line.Substring(0, maxLength) + "…";
        }

        /// <summary>変換結果チェックの1行(元サムネイル → 変換後サムネイル + 名前・メモ)を描画する。</summary>
        private void DrawResultPairRow(ConvertedPairRow pair)
        {
            if (pair == null || pair.converted == null) return;
            using (new EditorGUILayout.HorizontalScope(GUI.skin.box))
            {
                pair.sourcePreview = DrawResultThumb(pair.source, pair.sourcePreview, "元マテリアル");
                GUILayout.Label("→", GUILayout.Width(18f), GUILayout.Height(64f));
                pair.convertedPreview = DrawResultThumb(pair.converted, pair.convertedPreview, "変換後マテリアル");
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(
                        "元: " + (pair.source != null ? pair.source.name : "(特定できません)"),
                        EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField("変換後: " + pair.converted.name, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(pair.note))
                    {
                        EditorGUILayout.LabelField(pair.note, _miniWrapLabel);
                    }
                }
            }
        }

        /// <summary>
        /// 64pxのマテリアルサムネイルを1つ描画し、キャッシュ(ロード完了後のプレビュー)を返す。
        /// AssetPreview.GetAssetPreviewは非同期ロード中nullを返すため、その間はミニサムネイルで代用する。
        /// クリックで対象マテリアルをピン表示する。materialがnullの場合はプレースホルダーを表示する。
        /// </summary>
        private Texture2D DrawResultThumb(Material material, Texture2D cached, string label)
        {
            Rect rect = GUILayoutUtility.GetRect(64f, 64f, GUILayout.Width(64f), GUILayout.Height(64f));
            if (material == null)
            {
                GUI.Label(rect, new GUIContent("なし", label + "は特定できませんでした(アトラス統合の生成物など)"), EditorStyles.centeredGreyMiniLabel);
                return null;
            }

            if (cached == null) cached = AssetPreview.GetAssetPreview(material); // ロード中はnull
            Texture tex = cached != null ? (Texture)cached : AssetPreview.GetMiniThumbnail(material);
            if (tex != null) GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);

            // サムネイル全面を透明ボタンにしてピン表示(ツールチップで名前も確認できる)
            if (GUI.Button(rect, new GUIContent("", label + ": " + material.name + "(クリックでピン表示)"), GUIStyle.none))
            {
                EditorGUIUtility.PingObject(material);
            }
            return cached;
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
        // セクション12: ヘルプ(できること・できないこと / 用語ミニ解説)
        // ================================================================

        // 用語ミニ解説(用語, 説明)
        private static readonly string[][] GlossaryEntries =
        {
            new[] { "Toon Standard", "VRChat公式のQuest向けトゥーンシェーダー。影ランプ・ノーマルマップ・エミッションに対応。透過(半透明)は表現できない。" },
            new[] { "ASTC", "Androidで使われるテクスチャ圧縮形式。4x4が最高品質・最大容量、8x8が低品質・最小容量(6x6が推奨)。" },
            new[] { "縮小計画(テクスチャ)", "削減提案の「適用」や「10MB以下まで自動調整」で登録される、テクスチャごとの縮小予定サイズ。元のテクスチャは変更されず、変換時に縮小コピーが出力フォルダへ生成されて変換後のマテリアルがそれを参照する。「縮小計画をクリア」でいつでも解除できる。" },
            new[] { "Android上書き(テクスチャ)", "テクスチャのインポート設定のうちAndroidプラットフォームにのみ適用される上書き。本ツールの削減提案は元テクスチャのこの設定を変更せず、縮小計画に登録して変換時に縮小コピーを生成する。旧バージョンの本ツールが元テクスチャへ直接適用した上書きは元アセットに残ったままのため(サイズ一覧に「Android上書きあり」と表示)、不要ならテクスチャのインポート設定(Androidタブ)から手動で解除できる。" },
            new[] { "PhysBone", "髪や服を揺らすVRChatのコンポーネント。Androidではコンポーネント8個/コライダー16個(Poor上限)を超えるとアップロード不可。" },
            new[] { "EditorOnly", "このタグが付いたオブジェクトは、アップロード時にアバターから完全に取り除かれる(PC版のシーンには残る)。" },
            new[] { "パフォーマンスランク", "Excellent/Good/Medium/Poor/Very Poorの5段階。QuestではVery Poorはアップロード不可、既定で他の人に表示されるのはMedium以上。" },
            new[] { "NDMF (Non-Destructive Modular Framework)", "Modular Avatar / FaceEmo / AAO などが共通で使う「ビルド時にアバターを加工する」仕組みの土台。シーン上のアバターは変更せず、アップロード用のコピーにだけ変換が適用される。" },
            new[] { "AAO (Avatar Optimizer)", "ビルド時にメッシュ結合・重複マテリアル参照の統合などを自動で行う別ツール。本ツールのアトラス統合と組み合わせるとスロット数削減の効果が高い。" },
        };

        private void DrawHelpSection()
        {
            if (!DrawSectionFoldout(12, "ヘルプ",
                "Questでできること/できないことと、用語のミニ解説。",
                FoldKeyHelp))
            {
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("このツールで できること", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・シェーダー変換(lilToon / NonToon等の質感をテクスチャへベイク)\n" +
                    "・影ランプ生成 / エミッション変換\n" +
                    "・パーティクルの近似変換(加算/乗算)\n" +
                    "・半透明マテリアルの自動再現(乗算/加算パーティクルで近似。既定処理は 再現/非表示/不透明 から選択)\n" +
                    "・マテリアル/オブジェクトの非表示化\n" +
                    "・表情デカール(チーク/涙/アイハイライト)の再現/非表示化(Questで不透明の板に見えるのを防ぐ。顔本体・目・眉は残す)\n" +
                    "・アトラス統合(複数マテリアルを1枚に統合。影ランプ統一で、ランプが個別生成されるアバターもスロット削減)\n" +
                    "・隠れた肌などのメッシュ削減(AAOのブレンドシェイプ削除。服の下に隠れて見えない部分を削除)\n" +
                    "・衣装/アクセサリのトグル整理(表示で固定してメッシュ・スロットを結合 / 非表示で固定して削除)\n" +
                    "・非対応コンポーネントの整理\n" +
                    "・マテリアル差し替えアニメーションの追随",
                    _wrapLabel);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("できないこと", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(
                    "・メッシュの透過(Quest仕様: 透過はParticles系シェーダーのみ)\n" +
                    "・カットアウト(アルファ抜き)\n" +
                    "・アウトライン\n" +
                    "・ファー・ラメ・屈折などの特殊効果\n" +
                    "・リムライト・マットキャップ等の質感は既定では再現されない(リムは変換設定で近似可能)\n" +
                    "・ダウンロードサイズ10MB超の回避\n" +
                    "・Very Poorアバターのアップロード",
                    _wrapLabel);

                _foldGlossary = EditorGUILayout.Foldout(_foldGlossary, "用語ミニ解説", true);
                if (_foldGlossary)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        foreach (string[] entry in GlossaryEntries)
                        {
                            EditorGUILayout.LabelField(entry[0], EditorStyles.miniBoldLabel);
                            EditorGUILayout.LabelField(entry[1], _miniWrapLabel);
                        }
                    }
                }
            }
            EditorGUILayout.Space(8f);
        }

        // ================================================================
        // 目標ランク達成ガイド(診断結果の直後に表示。DrawDiagnosticsSection から呼ばれる)
        //   ・診断結果(_diagnostics.perfRows + sizeEstimate)を「目標ランク」と突き合わせ、
        //     超過している項目と推奨アクションだけを一覧する。
        //   ・操作できるのは目標ランクの選択のみ(それ以外は読み取り専用の表示)。
        //   ・診断未実行なら何も描画しない(呼び出し側でも診断済みだが二重に保険)。
        // ================================================================

        /// <summary>目標ランクの永続化に使うEditorPrefsキー(0=Excellent〜3=Poor)。</summary>
        private const string TargetRankPrefsKey = "RARA.QuestConverter.TargetRank";

        /// <summary>目標ランク(0=Excellent / 1=Good / 2=Medium / 3=Poor)。既定はMedium。</summary>
        private int _targetRank = 2;

        /// <summary>目標ランクのEditorPrefs読込を初回1回だけ行うためのフラグ。</summary>
        private bool _goalGuideRankLoaded;

        /// <summary>目標ランクの内部名(表示・ランク比較用。添字=重症度で小さいほど軽い)。</summary>
        private static readonly string[] GoalRankNames = { "Excellent", "Good", "Medium", "Poor" };

        /// <summary>目標ランク選択トグル(GoalRankNamesの並び順と一致させること)。</summary>
        private static readonly GUIContent[] GoalTargetRankLabels =
        {
            new GUIContent("Excellent", "最も軽いランク。誰にでも最軽量で表示されます(最も厳しい目標)"),
            new GUIContent("Good", "軽いランク"),
            new GUIContent("Medium(推奨)", "Questの既定表示ランク。既定で他の人に表示されるにはMedium以上が必要です"),
            new GUIContent("Poor", "アップロードは可能ですが、既定では他の人に表示されません(相手が表示上限を上げる必要があります)"),
        };

        /// <summary>目標ランク達成ガイドの1行(表示文字列と、赤=上限超/黄=目標超の色分け)。</summary>
        private struct GoalOverRow
        {
            public string text;      // 「・{項目}: 現在{値} / 目標{上限} — {推奨アクション}」
            public bool hardOver;    // true=VeryPoor相当(赤・アップロード不可) / false=目標超だが上限内(黄)
        }

        /// <summary>
        /// 目標ランク達成ガイド本体。診断済みのときだけ描画する。
        /// 目標ランクを選ばせ、超過項目があれば色付きで、無ければ達成メッセージを出す。
        /// </summary>
        private void DrawGoalGuidePanel()
        {
            if (_diagnostics == null) return; // 診断前は何も出さない(保険。通常は呼び出し側で診断済み)
            LoadTargetRankPref();

            int targetRank = Mathf.Clamp(_targetRank, 0, GoalRankNames.Length - 1);
            string targetName = GoalRankNames[targetRank];

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField("目標ランク達成ガイド", EditorStyles.boldLabel);

                // 目標ランク選択(このパネルで唯一の操作可能コントロール)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        new GUIContent("目標ランク", "このランク以下に収めたいパフォーマンス目標を選びます(既定: Medium)"),
                        GUILayout.Width(72f));
                    int newRank = GUILayout.Toolbar(targetRank, GoalTargetRankLabels);
                    if (newRank != targetRank)
                    {
                        _targetRank = Mathf.Clamp(newRank, 0, GoalRankNames.Length - 1);
                        EditorPrefs.SetInt(TargetRankPrefsKey, _targetRank);
                        targetRank = _targetRank;
                        targetName = GoalRankNames[targetRank];
                    }
                }

                List<GoalOverRow> overRows = CollectGoalOverRows(targetRank);
                if (overRows.Count == 0)
                {
                    // 目標達成: 緑のHelpBox(GUI.colorで緑に色付け)
                    var prevColor = GUI.color;
                    GUI.color = UploadOkColor;
                    EditorGUILayout.HelpBox(
                        "目標ランク(" + targetName + ")を満たしています。生成してアップロードできます。",
                        MessageType.Info);
                    GUI.color = prevColor;
                }
                else
                {
                    EditorGUILayout.LabelField(
                        "目標(" + targetName + ")を超えている項目です。右の推奨アクションで削減してください。",
                        _miniWrapLabel);
                    var prevColor = GUI.color;
                    foreach (GoalOverRow row in overRows)
                    {
                        GUI.color = row.hardOver ? OverLimitColor : NoteYellowColor;
                        EditorGUILayout.LabelField(row.text, _wrapLabel);
                    }
                    GUI.color = prevColor;
                }
            }
        }

        /// <summary>目標ランクをEditorPrefsから初回1回だけ読み込む(既定: 2=Medium)。</summary>
        private void LoadTargetRankPref()
        {
            if (_goalGuideRankLoaded) return;
            _goalGuideRankLoaded = true;
            _targetRank = Mathf.Clamp(EditorPrefs.GetInt(TargetRankPrefsKey, 2), 0, GoalRankNames.Length - 1);
        }

        /// <summary>
        /// 診断結果の各項目を目標ランク(0=Excellent〜3=Poor)と突き合わせ、超過している項目の行を作る。
        /// ・数値上限(QuestLimits)がある項目(Medium/Poorのみ)は数値で比較する。
        /// ・数値上限が無い項目(Excellent/Goodや定数の無い項目)はランク文字列で比較する。
        /// ・ダウンロードサイズ(10MB上限)はランク非依存の実質ハード上限として別途チェックする。
        /// </summary>
        private List<GoalOverRow> CollectGoalOverRows(int targetRank)
        {
            var list = new List<GoalOverRow>();
            if (_diagnostics == null) return list;

            if (_diagnostics.perfRows != null)
            {
                foreach (DiagnosticsRow row in _diagnostics.perfRows)
                {
                    if (row == null || string.IsNullOrEmpty(row.category)) continue;

                    float? limit = GetTargetLimitForCategory(row.category, targetRank);
                    float current;
                    bool hasCurrent = TryParsePerfValue(row.value, out current);

                    bool over;
                    string currentText;
                    string limitText;
                    if (limit.HasValue && hasCurrent)
                    {
                        // 数値比較(誤差を避けるため微小マージンを取る)
                        over = current > limit.Value + 0.001f;
                        currentText = row.value ?? "-";
                        limitText = FormatGoalLimit(row.category, limit.Value);
                    }
                    else
                    {
                        // ランク文字列で比較(数値上限が無い項目のフォールバック)
                        int rowSeverity = RatingNameSeverity(row.rating);
                        if (rowSeverity < 0) continue; // ランク不明は判定不能なのでスキップ
                        over = rowSeverity > targetRank;
                        currentText = (row.value ?? "-") + "(" + (row.rating ?? "不明") + ")";
                        limitText = GoalRankNames[Mathf.Clamp(targetRank, 0, GoalRankNames.Length - 1)] + "以下";
                    }
                    if (!over) continue;

                    bool hardOver = row.overLimit || RatingNameSeverity(row.rating) >= 4; // VeryPoor=赤
                    list.Add(new GoalOverRow
                    {
                        text = "・" + row.category + ": 現在 " + currentText + " / 目標 " + limitText +
                               " — " + GetGoalActionForCategory(row.category),
                        hardOver = hardOver,
                    });
                }
            }

            // ダウンロードサイズ(10MB上限。ランクに関わらずアップロードの実質ハード上限)
            SizeEstimateResult est = _diagnostics.sizeEstimate;
            if (est != null && est.estimatedDownloadMB > QuestLimits.HardDownloadSizeCapMB)
            {
                list.Add(new GoalOverRow
                {
                    text = "・ダウンロードサイズ(推定): 現在 " + est.estimatedDownloadMB.ToString("F1") +
                           " MB / 目標 " + QuestLimits.HardDownloadSizeCapMB + " MB — サイズ診断の『10MB自動調整』, 単色極限縮小",
                    hardOver = true,
                });
            }
            return list;
        }

        /// <summary>
        /// 項目名と目標ランクから数値上限を返す(存在しなければnull=ランク文字列でのフォールバック比較へ)。
        /// 数値上限が定義されているのはMedium(2)・Poor(3)のみ。Excellent(0)・Good(1)は常にnull。
        /// </summary>
        private static float? GetTargetLimitForCategory(string category, int targetRank)
        {
            if (targetRank == 2) // Medium
            {
                switch (category)
                {
                    case "ポリゴン数": return QuestLimits.MediumPolygons;
                    case "スキンメッシュ数": return QuestLimits.MediumSkinnedMeshes;
                    case "基本メッシュ数": return QuestLimits.MediumBasicMeshes;
                    case "マテリアルスロット数": return QuestLimits.MediumMaterialSlots;
                    case "ボーン数": return QuestLimits.MediumBones;
                    case "テクスチャメモリ(MB)": return QuestLimits.MediumTextureMemoryMB;
                    case "PhysBoneコンポーネント数": return QuestLimits.MediumPhysBoneComponents;
                    default: return null;
                }
            }
            if (targetRank == 3) // Poor
            {
                switch (category)
                {
                    case "ポリゴン数": return QuestLimits.PoorPolygons;
                    case "スキンメッシュ数": return QuestLimits.PoorSkinnedMeshes;
                    case "基本メッシュ数": return QuestLimits.PoorBasicMeshes;
                    case "マテリアルスロット数": return QuestLimits.PoorMaterialSlots;
                    case "ボーン数": return QuestLimits.PoorBones;
                    case "テクスチャメモリ(MB)": return QuestLimits.PoorTextureMemoryMB;
                    case "PhysBoneコンポーネント数": return QuestLimits.PoorPhysBoneComponents;
                    case "PhysBone対象Transform数": return QuestLimits.PoorPhysBoneTransforms;
                    case "PhysBoneコライダー数": return QuestLimits.PoorPhysBoneColliders;
                    case "コンタクト数": return QuestLimits.PoorContacts;
                    case "コンストレイント数": return QuestLimits.PoorConstraints;
                    default: return null;
                }
            }
            return null; // Excellent / Good は数値定数なし → ランク文字列比較
        }

        /// <summary>数値上限の表示文字列(テクスチャメモリはMB、それ以外は個数)。</summary>
        private static string FormatGoalLimit(string category, float limit)
        {
            if (category == "テクスチャメモリ(MB)") return limit.ToString("0") + " MB";
            return Mathf.RoundToInt(limit).ToString();
        }

        /// <summary>超過項目ごとの推奨アクション文言を返す(該当外の項目は汎用の案内)。</summary>
        private static string GetGoalActionForCategory(string category)
        {
            switch (category)
            {
                case "ポリゴン数":
                    return "AAO隠面メッシュ削除, 不要衣装をQuest除外, ポリゴン削減で目標へ配分";
                case "スキンメッシュ数":
                case "基本メッシュ数":
                case "マテリアルスロット数":
                    return "衣装整理で『表示で固定』, またはアトラス統合";
                case "テクスチャメモリ(MB)":
                    return "サイズ診断の『10MB自動調整』, 単色極限縮小";
                case "ボーン数":
                    return "AAOのTrace&Optimizeで未使用ボーン削減(自動)";
                case "PhysBoneコンポーネント数":
                case "PhysBone対象Transform数":
                case "PhysBoneコライダー数":
                case "PhysBone衝突チェック数":
                    return "PhysBone設定で選別(優先度で自動選択)";
                case "コンタクト数":
                    return "不要なコンタクト(VRCContact)を削除";
                case "コンストレイント数":
                    return "不要なコンストレイントを削除・統合";
                case "パーティクルシステム数":
                    return "不要なパーティクルシステムを削除";
                case "アニメーター数":
                    return "不要なアニメーター/サブアバターを整理";
                default:
                    return "各セクションで削減してください";
            }
        }

        /// <summary>
        /// 診断表示値(FormatCount="15,000" / FormatMegabytes="25.00 MB" / 未計測="-")から数値を取り出す。
        /// 桁区切りのカンマや単位・記号を除き、数字・小数点・符号のみで解釈する。数値が無ければfalse。
        /// </summary>
        private static bool TryParsePerfValue(string value, out float parsed)
        {
            parsed = 0f;
            if (string.IsNullOrEmpty(value)) return false;
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsDigit(c) || c == '.' || c == '-') sb.Append(c);
            }
            if (sb.Length == 0) return false; // "-" 等、数字が無い
            return float.TryParse(sb.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out parsed);
        }

        /// <summary>ランク文字列の重症度(Excellent=0〜VeryPoor=4)。不明は-1。NormalizeRatingで正規化する。</summary>
        private static int RatingNameSeverity(string rating)
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
    }
}
#endif
