// RARA Quest Converter - AAO(AvatarOptimizer)ブレンドシェイプ・メッシュ削除ヘルパー
// 服の下に隠れる肌などを、アバター同梱の「shrink/hide」ブレンドシェイプで消して
// ポリゴン数・メッシュ容量を削減する(Quest 10MB/40MB上限対策)。
//
// 【重要】AAO の型は com.anatawa12.avatar-optimizer.* アセンブリにあり、
// Assembly-CSharp-Editor(このプロジェクトは asmdef 無し)からはコンパイル時に参照できない。
// そのため AAO コンポーネントの追加・設定はすべてリフレクションで行う
// (QuestCompat.FindType + Undo.AddComponent(go, type) + 公開APIまたは SerializedObject)。
// AAO への using / 型参照は絶対に書かないこと。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RARA.QuestConverter
{
    /// <summary>
    /// SkinnedMeshRenderer 1件ぶんの「shrink/hide(隠し)」ブレンドシェイプ検出結果。
    /// AAO RemoveMeshByBlendShape で削除する候補として UI に提示し、ユーザーが
    /// 適用対象を選ぶ(選択は QuestConvertSettings.hiddenMeshRendererPaths に保存される)。
    /// </summary>
    public class ShrinkShapeCandidate
    {
        /// <summary>アバタールート相対のレンダラーパス(QuestCompat.GetRelativePath 準拠)。</summary>
        public string rendererPath;

        /// <summary>レンダラーの GameObject 名(UI 表示用)。</summary>
        public string rendererName;

        /// <summary>このレンダラーで検出された削除候補ブレンドシェイプ名。</summary>
        public List<string> blendShapeNames;

        /// <summary>候補として挙げた理由(名前一致・幾何判定など。日本語)。</summary>
        public string reason;
    }

    /// <summary>
    /// AAO(AvatarOptimizer)の RemoveMeshByBlendShape / TraceAndOptimize を
    /// リフレクション経由で扱うヘルパー。検出は読み取り専用、付与は複製アバターに対して行う。
    /// </summary>
    public static class AAOMeshRemovalHelper
    {
        // ---- AAO 型のフルネーム(リフレクション解決用。AAO 未導入なら FindType が null を返す)----
        private const string RemoveMeshByBlendShapeTypeName = "Anatawa12.AvatarOptimizer.RemoveMeshByBlendShape";
        private const string TraceAndOptimizeTypeName = "Anatawa12.AvatarOptimizer.TraceAndOptimize";

        // ================================================================
        // 検出(読み取り専用)
        // ================================================================

        /// <summary>
        /// 名前が「隠し/削除/縮小」系で「単体で強い」トークン(部分一致・大文字小文字無視)。
        /// これ単体で候補とみなす。offset 等への誤反応を避けるため短すぎるトークン("off"等)は含めない。
        /// </summary>
        private static readonly string[] StrongHideTokens =
        {
            "shrink", "hide", "hidden", "erase", "delete", "remove",
            "縮小", "非表示", "削除", "消し", "消す", "隠す", "隠し", "けす",
        };

        /// <summary>
        /// 単体では弱いオフ系トークン(部分一致・大文字小文字無視)。
        /// 部位トークンと組み合わさった時のみ候補とみなす(誤反応低減)。
        /// </summary>
        private static readonly string[] OffTokens =
        {
            "off", "del", "none", "cut", "オフ", "無し", "なし", "消",
        };

        /// <summary>
        /// 身体部位トークン(部分一致・大文字小文字無視)。オフ系トークンと同居する時に候補化する。
        /// MMD 表情モーフと衝突する単漢字("上""下""前"等)は誤検出を招くため入れない。
        /// </summary>
        private static readonly string[] RegionTokens =
        {
            "hip", "bra", "pants", "shorts", "shoe", "socks", "skirt", "tops", "bottoms",
            "body", "chest", "foot", "hand", "underwear", "leg", "arm", "waist", "inner", "skin", "spat",
            "ブラ", "パンツ", "スカート", "靴下", "靴", "素体", "肌", "胸", "足", "手", "下着", "腕", "腰",
            "インナー", "スパッツ",
        };

        /// <summary>
        /// 表情/顔まわりの(削除してはいけない)トークン。メッシュ名・ブレンドシェイプ名に含まれる場合、
        /// 名前一致・幾何判定のいずれの経路でも削除候補にしない(表情モーフ・顔ディテールの誤削除防止)。
        /// 名前が隠し/縮小系("消し"等)でも、顔ディテール(涙袋消し・まつ毛消し・eyelineHide 等)を
        /// 巻き込まないよう、これらは名前一致判定より前にガードする。
        /// </summary>
        private static readonly string[] FaceGuardTokens =
        {
            "face", "head", "mouth", "eye", "brow", "teeth", "tongue", "cheek", "eyelash", "eyeline", "eyelid",
            "顔", "頭", "口", "目", "眉", "歯", "舌", "頬", "まぶた", "まゆ", "ほほ",
            "涙", "まつ毛", "まつげ", "ほくろ", "そばかす",
        };

        /// <summary>
        /// AAO の TraceAndOptimize.mmdWorldCompatibility(既定オン)が保護する標準 MMD 表情モーフ名。
        /// これらは(名前一致・幾何判定いずれでも)絶対に削除候補にしない(完全一致・大文字小文字無視)。
        /// </summary>
        private static readonly HashSet<string> MmdMorphNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 母音(リップシンク)
            "あ", "い", "う", "え", "お", "ん", "ワ", "▲", "∧", "ω", "ω□", "□", "はんっ！",
            // まばたき・ウィンク系
            "まばたき", "笑い", "ウィンク", "ウィンク右", "ウィンク2", "ウィンク２", "ウィンク2右", "ウィンク２右",
            "ウインク", "ウインク右", "ｳｨﾝｸ", "なごみ", "はぅ", "はちゅ目", "びっくり", "じと目", "じとめ",
            "なぬ！", "ｷﾘｯ", "星目", "ハート", "ハート目", "瞳小", "瞳縦潰れ", "光下", "ハイライト消",
            "映り込み消", "恐ろしい子！",
            // 眉・感情
            "真面目", "困る", "にこり", "怒り", "前", "上", "下", "喜び", "わぉ?!", "照れ", "涙", "がーん",
            "にやり", "にやり2", "にっこり", "ぺろっ", "てへぺろ", "てへぺろ2", "口角上げ", "口角下げ", "口横広げ",
            "歯無し上", "歯無し下",
            // 英語 MMD 名
            "blink", "blink_l", "blink_r", "wink", "wink_l", "wink_r", "smile",
            "serious", "anger", "angry", "sorrow", "surprised", "a", "i", "u", "e", "o",
        };

        /// <summary>
        /// 幾何判定: 最終フレームで「メッシュ境界対角長の一定割合以上」変位した頂点が
        /// 全体のこの割合以上あれば、身体を消す/縮める系の形状とみなす(保守的な閾値)。
        /// </summary>
        private const float GeometricDisplacedFraction = 0.40f;

        /// <summary>幾何判定で「強く変位した」とみなす、境界対角長に対する変位量の割合。</summary>
        private const float GeometricDeltaRatio = 0.05f;

        /// <summary>幾何判定を行う最小頂点数(小さすぎるメッシュは対象外)。</summary>
        private const int GeometricMinVertexCount = 500;

        /// <summary>
        /// avatarRoot 配下の各 SkinnedMeshRenderer を走査し、服の下の肌等を消す
        /// 「shrink/hide」ブレンドシェイプを検出する(読み取り専用。ディスク・シーンを変更しない)。
        /// 判定は (1) 名前パターン一致(強トークン、または 部位トークン+オフトークン)、
        /// (2) 名前非一致でも最終フレームで頂点の大部分を強く変位させる幾何ヒューリスティック。
        /// 標準 MMD 表情モーフ・顔まわりの形状は除外する。レンダラー単位でまとめて返す。
        /// </summary>
        public static List<ShrinkShapeCandidate> DetectShrinkShapes(GameObject avatarRoot)
        {
            var results = new List<ShrinkShapeCandidate>();
            if (avatarRoot == null) return results;

            // R5: MA ShapeChanger が既に対象にしているブレンドシェイプは、AAO の自動削除候補から外す
            // (二重に削除指定しないため。ShapeChanger(Delete)によるポリゴン削減はビルド時に適用される)。MA未導入なら空辞書。
            Dictionary<SkinnedMeshRenderer, HashSet<string>> shapeChangerShapes = MACompatAudit.CollectShapeChangerShapes(avatarRoot);

            SkinnedMeshRenderer[] renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer smr in renderers)
            {
                try
                {
                    if (smr == null) continue;
                    // EditorOnly(LockHidden で統合済み等)のレンダラーは VRChat ビルドで除去されるため候補にしない。
                    // これらへ RemoveMeshByBlendShape を付けても無意味(ホストごと剥がれる)なので検出段階で除外する。
                    if (QuestCompat.IsEditorOnly(smr.transform)) continue;
                    Mesh mesh = smr.sharedMesh;
                    if (mesh == null || mesh.blendShapeCount == 0) continue;

                    string meshName = mesh.name ?? string.Empty;
                    string goName = smr.gameObject.name ?? string.Empty;
                    bool meshLooksFacial = ContainsAny(meshName, FaceGuardTokens) || ContainsAny(goName, FaceGuardTokens);

                    // このレンダラーで MA ShapeChanger が扱うシェイプ(あれば)。自動削除候補から除外する。
                    HashSet<string> maShapeChangerShapes;
                    if (!shapeChangerShapes.TryGetValue(smr, out maShapeChangerShapes)) maShapeChangerShapes = null;

                    var matchedNames = new List<string>();
                    bool anyGeometric = false;
                    Vector3[] deltaBuffer = null; // 幾何判定に必要になった時だけ確保して使い回す

                    for (int shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
                    {
                        string shapeName = mesh.GetBlendShapeName(shapeIndex);
                        if (string.IsNullOrEmpty(shapeName)) continue;
                        if (MmdMorphNames.Contains(shapeName.Trim())) continue; // MMD 表情モーフは常に除外
                        // R5: MA ShapeChanger が既に扱うシェイプはビルド時に処理されるため、自動削除候補に入れない。
                        if (maShapeChangerShapes != null && maShapeChangerShapes.Contains(shapeName)) continue;

                        // 表情/顔まわりの形状は名前一致・幾何判定のいずれでも候補にしない。
                        // 顔ディテールの隠しモーフ(涙袋消し・まつ毛消し・eyelineHide 等)が
                        // 名前一致で顔メッシュを誤削除するのを防ぐため、名前一致判定より前にガードする。
                        if (meshLooksFacial || ContainsAny(shapeName, FaceGuardTokens)) continue;

                        if (NameLooksLikeHideShape(shapeName))
                        {
                            matchedNames.Add(shapeName);
                            continue;
                        }

                        // 名前非一致 → 幾何ヒューリスティック(標準 MMD は既に除外済み)
                        if (mesh.vertexCount < GeometricMinVertexCount) continue;

                        if (deltaBuffer == null || deltaBuffer.Length < mesh.vertexCount)
                        {
                            deltaBuffer = new Vector3[mesh.vertexCount];
                        }
                        if (IsGeometricHideShape(mesh, shapeIndex, deltaBuffer))
                        {
                            matchedNames.Add(shapeName);
                            anyGeometric = true;
                        }
                    }

                    if (matchedNames.Count == 0) continue;

                    string rendererPath = QuestCompat.GetRelativePath(avatarRoot.transform, smr.transform);
                    if (rendererPath == null) continue; // avatarRoot 配下でない(通常ありえない)

                    results.Add(new ShrinkShapeCandidate
                    {
                        rendererPath = rendererPath,
                        rendererName = goName,
                        blendShapeNames = matchedNames,
                        reason = anyGeometric
                            ? "名前が隠し/縮小系、または頂点の大部分を強く変位させる形状(幾何判定)を検出"
                            : "名前が隠し/縮小系のブレンドシェイプを検出",
                    });
                }
                catch (Exception)
                {
                    // 1つのメッシュの解析失敗が検出全体を止めないようにする(検出は読み取り専用のため握りつぶす)
                }
            }
            return results;
        }

        /// <summary>名前が隠し/縮小系ブレンドシェイプに見えるか(強トークン、または 部位+オフ)。</summary>
        private static bool NameLooksLikeHideShape(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (ContainsAny(name, StrongHideTokens)) return true;
            if (ContainsAny(name, RegionTokens) && ContainsAny(name, OffTokens)) return true;
            return false;
        }

        private static bool ContainsAny(string haystack, string[] tokens)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            foreach (string token in tokens)
            {
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 最終フレームの頂点変位を調べ、境界対角長の一定割合以上動いた頂点が
        /// 全体の GeometricDisplacedFraction 以上なら true(身体を消す/縮める系とみなす)。
        /// </summary>
        private static bool IsGeometricHideShape(Mesh mesh, int shapeIndex, Vector3[] deltaBuffer)
        {
            int frameCount = mesh.GetBlendShapeFrameCount(shapeIndex);
            if (frameCount <= 0) return false;

            int vertexCount = mesh.vertexCount;
            if (vertexCount <= 0) return false;

            // 変位が最大になりやすい最終フレーム(通常ウェイト100)を使う
            mesh.GetBlendShapeFrameVertices(shapeIndex, frameCount - 1, deltaBuffer, null, null);

            Vector3 boundsSize = mesh.bounds.size;
            float diagonal = boundsSize.magnitude;
            if (diagonal <= Mathf.Epsilon) return false;
            float threshold = diagonal * GeometricDeltaRatio;
            float thresholdSqr = threshold * threshold;

            int displaced = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                if (deltaBuffer[i].sqrMagnitude >= thresholdSqr) displaced++;
            }
            return (float)displaced / vertexCount >= GeometricDisplacedFraction;
        }

        // ================================================================
        // 付与(複製アバターに対する破壊的変更。すべてリフレクション)
        // ================================================================

        /// <summary>
        /// chosen の各レンダラー(cloneRoot 相対パスで解決)に AAO RemoveMeshByBlendShape を追加し、
        /// 削除するブレンドシェイプ名を設定する。AAO のビルド時パスがそのシェイプで動く肌等を削除する。
        /// リフレクションで実装(AAO はコンパイル時参照不可)。追加できた件数を返す。
        /// AAO 型が見つからない場合は report.Warn を出して 0 を返す(絶対に例外を投げない)。
        /// </summary>
        public static int ApplyRemoveMeshByBlendShape(GameObject cloneRoot, List<ShrinkShapeCandidate> chosen, ConversionReport report)
        {
            if (report == null) report = new ConversionReport();
            if (cloneRoot == null || chosen == null || chosen.Count == 0) return 0;

            Type rmbsType = QuestCompat.FindType(RemoveMeshByBlendShapeTypeName);
            if (rmbsType == null)
            {
                report.Warn("AvatarOptimizer(AAO)が見つからないため、ブレンドシェイプによるメッシュ削除をスキップしました。VCC等でAvatarOptimizerを導入するか、対象レンダラーへ手動でRemove Mesh By BlendShapeを追加してください。");
                return 0;
            }

            int applied = 0;
            foreach (ShrinkShapeCandidate candidate in chosen)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.rendererPath)) continue;
                try
                {
                    Transform target = QuestCompat.FindByPath(cloneRoot.transform, candidate.rendererPath);
                    if (target == null)
                    {
                        report.Warn($"メッシュ削除対象のレンダラーが複製内に見つかりませんでした(スキップ): {candidate.rendererPath}");
                        continue;
                    }
                    var smr = target.GetComponent<SkinnedMeshRenderer>();
                    if (smr == null || smr.sharedMesh == null)
                    {
                        report.Warn($"メッシュ削除対象がSkinnedMeshRendererではないためスキップしました: {candidate.rendererPath}");
                        continue;
                    }

                    // メッシュに実在するブレンドシェイプ名だけへ絞る(実行時に存在しない名前は無視)
                    List<string> present = FilterPresentShapes(smr.sharedMesh, candidate.blendShapeNames);
                    if (present.Count == 0)
                    {
                        report.Warn($"指定ブレンドシェイプがメッシュに見つからないためメッシュ削除をスキップしました: {candidate.rendererPath}");
                        continue;
                    }

                    Component component = Undo.AddComponent(target.gameObject, rmbsType);
                    if (component == null)
                    {
                        report.Warn($"Remove Mesh By BlendShapeの追加に失敗しました(スキップ): {candidate.rendererPath}");
                        continue;
                    }
                    // 追加直後に既定挙動バージョンを固定(公式APIの作法。失敗しても致命的ではない)
                    TryInitialize(component, rmbsType, 1);

                    if (SetShapeKeys(component, rmbsType, present, report, candidate.rendererPath))
                    {
                        applied++;
                        report.Info($"ブレンドシェイプによるメッシュ削除を追加: {candidate.rendererPath} → {string.Join(", ", present)}");
                    }
                }
                catch (Exception ex)
                {
                    report.Warn($"ブレンドシェイプによるメッシュ削除の設定に失敗しました({candidate.rendererPath}): {ex.Message}。対象レンダラーへ手動でRemove Mesh By BlendShapeを追加してください。");
                }
            }
            return applied;
        }

        /// <summary>names のうち mesh に実在するブレンドシェイプ名だけを(重複除去して)返す。</summary>
        private static List<string> FilterPresentShapes(Mesh mesh, List<string> names)
        {
            var present = new List<string>();
            if (mesh == null || names == null) return present;

            var meshShapes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < mesh.blendShapeCount; i++) meshShapes.Add(mesh.GetBlendShapeName(i));

            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (meshShapes.Contains(name) && !present.Contains(name)) present.Add(name);
            }
            return present;
        }

        /// <summary>
        /// RemoveMeshByBlendShape のブレンドシェイプ名集合(PrefabSafeSet&lt;string&gt;)に names を追加する。
        /// まず公開スクリプティングAPI(ShapeKeys.UnionWith)を試し、失敗時は SerializedObject 経由で
        /// shapeKeysSet.mainSet へ直接追加する(ビルド時の複製はネスト0のため mainSet が正しい追加先)。
        /// いずれかで追加できたら true。
        /// </summary>
        private static bool SetShapeKeys(Component component, Type rmbsType, List<string> names, ConversionReport report, string rendererPath)
        {
            // ルート1: 公開API ShapeKeys(API.PrefabSafeSetAccessor<string>).UnionWith(IEnumerable<string>)
            try
            {
                PropertyInfo shapeKeysProp = rmbsType.GetProperty("ShapeKeys", BindingFlags.Public | BindingFlags.Instance);
                if (shapeKeysProp != null)
                {
                    object accessor = shapeKeysProp.GetValue(component);
                    if (accessor != null)
                    {
                        // PrefabSafeSetAccessor は参照型 PrefabSafeSet を包む readonly struct のため、
                        // ボックス化したコピーに対して呼んでも同一集合が更新される。
                        MethodInfo unionWith = accessor.GetType().GetMethod("UnionWith", BindingFlags.Public | BindingFlags.Instance);
                        if (unionWith != null)
                        {
                            unionWith.Invoke(accessor, new object[] { names });
                            EditorUtility.SetDirty(component);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Warn($"AAO公開APIでのブレンドシェイプ設定に失敗したためSerializedObjectで再試行します({rendererPath}): {ex.Message}");
            }

            // ルート2: SerializedObject で shapeKeysSet.mainSet へ直接追加(ネスト0の複製が前提)
            try
            {
                var so = new SerializedObject(component);
                SerializedProperty mainSet = so.FindProperty("shapeKeysSet.mainSet");
                if (mainSet == null || !mainSet.isArray)
                {
                    report.Warn($"AAOのブレンドシェイプ集合(shapeKeysSet.mainSet)が取得できずメッシュ削除を設定できませんでした({rendererPath})。手動で設定してください。");
                    return false;
                }

                // 既存要素を控えて重複追加を避ける
                var existing = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < mainSet.arraySize; i++)
                {
                    existing.Add(mainSet.GetArrayElementAtIndex(i).stringValue);
                }
                foreach (string name in names)
                {
                    if (existing.Contains(name)) continue;
                    int idx = mainSet.arraySize;
                    mainSet.arraySize = idx + 1;
                    mainSet.GetArrayElementAtIndex(idx).stringValue = name;
                    existing.Add(name);
                }
                so.ApplyModifiedProperties();
                return true;
            }
            catch (Exception ex)
            {
                report.Warn($"ブレンドシェイプによるメッシュ削除の設定に失敗しました({rendererPath}): {ex.Message}。手動で設定してください。");
                return false;
            }
        }

        // ================================================================
        // Trace and Optimize(AAO のビルド時最適化を有効化)
        // ================================================================

        /// <summary>
        /// cloneRoot に AAO TraceAndOptimize が無ければ追加する(RemoveMeshByBlendShape 等の
        /// EditSkinnedMeshComponent はこの存在下でビルド時に処理されるため必須)。
        /// リフレクションで実装。存在/追加できたら true。
        /// AAO 未導入なら report.Warn を出して false を返す(絶対に例外を投げない)。
        /// </summary>
        public static bool EnsureTraceAndOptimize(GameObject cloneRoot, ConversionReport report)
        {
            if (report == null) report = new ConversionReport();
            if (cloneRoot == null) return false;

            Type taoType = QuestCompat.FindType(TraceAndOptimizeTypeName);
            if (taoType == null)
            {
                report.Warn("AvatarOptimizer(AAO未導入)のため、Trace and Optimizeを追加できませんでした。AAOを導入するとブレンドシェイプ削除やメッシュ統合がビルド時に有効になります。");
                return false;
            }

            try
            {
                // ルート専用コンポーネントだが念のため子も含めて存在確認する
                Component[] existing = cloneRoot.GetComponentsInChildren(taoType, true);
                if (existing != null && existing.Length > 0)
                {
                    report.Info("Trace and Optimizeは既に存在します(ビルド時最適化は有効)。");
                    return true;
                }
                Component added = Undo.AddComponent(cloneRoot, taoType);
                if (added == null)
                {
                    report.Warn("Trace and Optimizeの追加に失敗しました。複製アバターのルートへ手動で追加してください。");
                    return false;
                }
                report.Info("Trace and Optimizeを複製アバターに追加しました(AAOのビルド時最適化を有効化)。");
                return true;
            }
            catch (Exception ex)
            {
                report.Warn($"Trace and Optimizeの追加に失敗しました: {ex.Message}。複製アバターのルートへ手動で追加してください。");
                return false;
            }
        }

        /// <summary>
        /// AAO コンポーネントの Initialize(int) を(存在すれば)呼んで既定挙動バージョンを固定する。
        /// 見つからない・失敗しても致命的ではないため握りつぶす(コンポーネント自体は機能する)。
        /// </summary>
        private static void TryInitialize(Component component, Type type, int version)
        {
            try
            {
                MethodInfo initialize = type.GetMethod(
                    "Initialize",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int) },
                    null);
                if (initialize != null) initialize.Invoke(component, new object[] { version });
            }
            catch (Exception)
            {
                // ベストエフォート(Initialize が無い/失敗してもコンポーネントは動作する)
            }
        }
    }
}
#endif
