// RARA Quest Converter - アバター変換オーケストレーター
// PCアバターを複製し、マテリアル変換・アニメーション複製・コンポーネント整理を一括で行う。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using RARA.QuestConverter.Decimation;

namespace RARA.QuestConverter
{
    /// <summary>
    /// PreviewMaterials が返す、マテリアル1件ぶんのプレビュー情報。
    /// 変換を実行せずに「このマテリアルがどう扱われる予定か」をウィンドウの一覧に表示するための行。
    /// </summary>
    public class MaterialPreviewRow
    {
        public Material material;
        public bool usedByMesh;
        public bool usedByParticle;
        public bool usedByAnimation;
        /// <summary>コンポーネントのシリアライズ参照(Modular AvatarのMaterial Setter等)から参照されているか。</summary>
        public bool usedByComponent;
        /// <summary>コンポーネント参照元の短い説明(「型名: GameObject名」)。コンポーネント参照が無ければnull。</summary>
        public string componentSource;
        public QuestCompat.TransparencyClass transparency;
        public bool isMobileAlready;
        public bool isTMP;
        public bool isBrokenShader;
        /// <summary>変換時に行われる処理の短い説明(日本語)。</summary>
        public string plannedAction;
        /// <summary>アトラス化の候補になり得るか(enableAtlas設定とは独立に判定)。</summary>
        public bool atlasEligible;
        /// <summary>アトラス対象外の理由(対象ならnull)。</summary>
        public string atlasIneligibleReason;
        /// <summary>
        /// 大型メッシュ・髪で使用される透過マテリアルのため、半透明の再現・非表示化が抑制され
        /// 不透明として変換されるか(transparentHandling によらず保護される。Convert の step 3.7 に対応)。
        /// </summary>
        public bool suppressTransparentHide;
    }

    /// <summary>
    /// PreviewExpressionDecals / DetectExpressionDecals が返す、検出した表情デカール
    /// (顔のチーク・涙・アイハイライト等の透過オーバーレイ)1件ぶんの情報。
    /// material は検出されたデカールマテリアル、rendererPath はアバタールート相対のレンダラーパス、
    /// slotIndex はそのレンダラー上のマテリアルスロット番号、reason は検出理由
    /// ("structural"=不透明ベース+透過サブメッシュ / "name"=名前トークン一致)。
    /// </summary>
    public class DecalOverlayRow
    {
        public Material material;
        public string rendererPath;
        public int slotIndex;
        public string reason;
    }

    /// <summary>
    /// 1回の変換で生成するアセットのパス管理コンテキスト。
    /// QuestAssetPersistence.StablePathRegistry により「実行内の同名衝突は決定的に解決しつつ、
    /// 実行間では安定する」パスを払い出す。生成アセットは安定パスへGUIDを保持したまま
    /// 上書き保存されるため、シーンに残った前回の _Quest クローンや、ユーザーが元アバターへ
    /// 手動割り当てした生成マテリアルの参照が再変換で切れない。
    /// あわせて今回の実行で書き込んだパスを記録し、変換末尾の「前回の生成物(未使用)」報告に使う。
    /// テクスチャ縮小コピーのキャッシュも保持する(同じ元テクスチャ×目標サイズの二重生成防止)。
    /// </summary>
    public sealed class ConversionAssetContext
    {
        private readonly QuestAssetPersistence.StablePathRegistry _registry = new QuestAssetPersistence.StablePathRegistry();
        // Windowsのファイル系・StablePathRegistryの衝突判定に合わせ、大文字小文字は区別しない
        private readonly HashSet<string> _writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> _downscaledCopies = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        /// <summary>安定パスを払い出し、今回の書き込み先として記録して返す。</summary>
        public string Claim(string desiredPath)
        {
            string path = _registry.Claim(desiredPath);
            if (!string.IsNullOrEmpty(path)) _writtenPaths.Add(path);
            return path;
        }

        /// <summary>path が今回の実行で書き込まれた(Claimされた)パスか。</summary>
        public bool WasWritten(string path)
        {
            return path != null && _writtenPaths.Contains(path);
        }

        /// <summary>生成済みのテクスチャ縮小コピーを取得する(破棄済みインスタンスは無視する)。</summary>
        public bool TryGetDownscaledCopy(string cacheKey, out Texture2D copy)
        {
            return _downscaledCopies.TryGetValue(cacheKey, out copy) && copy != null;
        }

        /// <summary>生成したテクスチャ縮小コピーをキャッシュへ登録する。</summary>
        public void RegisterDownscaledCopy(string cacheKey, Texture2D copy)
        {
            _downscaledCopies[cacheKey] = copy;
        }
    }

    /// <summary>
    /// アバター全体のQuest(Android)変換を統括する。
    /// 元のアバターは変更せず、複製("_Quest")に対して変換を適用する。
    /// </summary>
    public static class AvatarQuestConverter
    {
        private const string UndoLabel = "Quest変換";
        private const string ProgressTitle = "RARA Quest変換";

        // ---- 透過の扱い(transparentHandling)によるレンダラー単位の分岐ヘルパー ----

        /// <summary>
        /// 全スロット透過レンダラーをレンダラーごとEditorOnly化(ビルド除外)するのは Hide モードのみ。
        /// Emulate は乗算/加算でマテリアルを再現するためレンダラーを残す。Opaque も不透明化して残す。
        /// </summary>
        private static bool ShouldHideFullyTransparentRenderers(QuestConvertSettings settings)
        {
            return settings.transparentHandling == TransparentHandling.Hide;
        }

        /// <summary>
        /// 大型メッシュ・髪で使われる透過マテリアルの不透明変換保護(suppressTransparentHide)は
        /// Emulate / Hide で必要(乗算/加算・非表示化から髪を守る)。Opaque では全て不透明化されるため不要。
        /// </summary>
        private static bool ShouldCollectSuppressTransparentHide(QuestConvertSettings settings)
        {
            return settings.transparentHandling != TransparentHandling.Opaque;
        }

        /// <summary>
        /// シーン上のPCアバターを複製してQuest対応へ変換する。
        /// 成功時は複製した変換済みアバターを返す。検証エラー時はnullを返す。
        /// </summary>
        public static GameObject Convert(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor sourceAvatar, QuestConvertSettings settings, ConversionReport report)
        {
            if (report == null) report = new ConversionReport(); // 呼び出し側の渡し忘れ対策(結果は破棄される)

            // --- 1. 検証 ---
            if (sourceAvatar == null)
            {
                report.Error("変換元アバター(VRCAvatarDescriptor)が指定されていません。");
                return null;
            }
            if (!sourceAvatar.gameObject.scene.IsValid())
            {
                report.Error("シーン上に配置されたアバターを指定してください(プレハブアセットは直接変換できません)。");
                return null;
            }
            if (settings == null)
            {
                report.Error("変換設定(QuestConvertSettings)がnullです。");
                return null;
            }
            if (string.IsNullOrEmpty(settings.outputFolder) || !settings.outputFolder.Replace('\\', '/').StartsWith("Assets", StringComparison.Ordinal))
            {
                report.Error($"出力フォルダは \"Assets\" 配下を指定してください: {settings.outputFolder}");
                return null;
            }

            GameObject sourceGo = sourceAvatar.gameObject;
            // 変換モード: QuestConvert=フル変換(シェーダー/テクスチャ変換あり) /
            //            ConsolidateOnly=PC最適化のみ(衣装・トグル整理とコンポーネント整理のみ。マテリアルはPCシェーダーのまま)
            bool questConvert = settings.conversionMode == ConversionMode.QuestConvert;
            GameObject clone = null;
            // 変換全体(前回クローン削除・ペーストボード複製・マテリアル/レンダラー記録・元アバター非アクティブ化)を
            // 1回のUndoにまとめる。ペーストボード複製は独自のUndoグループ名で登録されるため、開始時のグループ番号を
            // 捕捉し、finallyでCollapseUndoOperationsして単一グループに折り畳む(コメントで謳う「同一グループ」を実際に担保)。
            int undoGroup = Undo.GetCurrentGroup();

            try
            {
                // --- 2. 出力フォルダ作成 ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "出力フォルダを作成中...", 0.05f);
                string outputDir = settings.outputFolder.Replace('\\', '/').TrimEnd('/') + "/" + QuestConverterUtility.SanitizeAssetName(sourceGo.name);
                // 再変換でも前回の生成フォルダは削除しない。生成アセットは実行間で安定したパスへ
                // GUID を保持したまま上書き保存されるため(ConversionAssetContext / QuestAssetPersistence)、
                // シーンに残った前回の _Quest クローンや、ユーザーが元アバターへ手動割り当てした
                // 生成マテリアルの参照が切れない(フォルダ全削除だと参照が Missing になり、
                // 2回目の変換で InternalErrorShader エラーになる)。
                // 今回書き込まれなかった残存ファイルは変換末尾に「前回の生成物(未使用)」として報告する。
                QuestConverterUtility.EnsureFolder(outputDir);
                QuestConverterUtility.EnsureFolder(outputDir + "/Materials");
                QuestConverterUtility.EnsureFolder(outputDir + "/Textures");
                QuestConverterUtility.EnsureFolder(outputDir + "/Animations");
                var assets = new ConversionAssetContext();

                // --- 3. アバター複製(プレハブインスタンス状態を保持するためペーストボード複製) ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "アバターを複製中...", 0.1f);
                // 再変換の冪等化: 前回の変換で作られた同名クローン("{sourceName}_Quest")がシーンに
                // 残っていると、複製が溜まってどれをアップロードすべきか分からなくなり、古い(縮小前の)
                // クローンが誤ってビルド・アップロードされる。新しい複製を作る前に、このアバター由来の
                // 前回クローンを除去して作り直す(元アバターは絶対に削除しない)。
                // QuestConvert は "_Quest"、ConsolidateOnly(PC最適化)は "_Opt" とし、PC最適化クローンを
                // Quest変換クローンと取り違えないようにする。RemovePriorGeneratedClones はこの cloneName を
                // 基準に前回の「同名」クローンだけを掃除するため、_Quest と _Opt は互いを削除しない。
                string cloneName = sourceGo.name + (questConvert ? "_Quest" : "_Opt");
                int removedPriorClones = RemovePriorGeneratedClones(sourceGo, cloneName, report);
                clone = DuplicateAvatar(sourceGo, report);
                clone.name = cloneName;
                // 新しい複製は必ずアクティブにする。元アバターが(前回の変換等で)非アクティブな状態から
                // 複製すると複製もそのまま非アクティブになり、正しい複製が無効のまま放置される不具合があった。
                clone.SetActive(true);
                Undo.RegisterCreatedObjectUndo(clone, UndoLabel);
                report.Info($"アバターを複製しました: {clone.name}");
                if (!questConvert)
                {
                    report.Info("PC最適化モード(シェーダー変換なし): マテリアルは元(PCシェーダー)のまま残し、衣装・トグル整理とコンポーネント整理のみ行います。");
                }

                // --- 3.5. Quest除外(EditorOnly化)---
                // マテリアル収集より前に適用し、除外サブツリーのマテリアルが無駄にベイクされないようにする。
                EditorUtility.DisplayProgressBar(ProgressTitle, "除外オブジェクトをEditorOnly化中...", 0.12f);
                int excludedCount = ApplyQuestExclusions(clone, settings, report);

                // --- 3.55. 衣装・トグル整理(LockVisible / LockHidden)---
                // マテリアル収集(step 4)より前・アニメーション変換(step 5)より前に適用する。理由:
                //  ・LockHidden はメッシュを EditorOnly 化するため、この後のマテリアル収集
                //    (IsInEditorOnlySubtree で EditorOnly サブツリーを除外)から外れ、消すメッシュ専用の
                //    マテリアルが無駄にベイクされない(step 3.5 Quest除外・step 3.6 透過除外と同じ狙い)。
                //  ・LockVisible は常時 ON 化 + m_IsActive バインディング除去により、AAO が同一の
                //    activeness バケットへ入れてメッシュ・マテリアルスロットを統合できるようにする(研究の結論)。
                //  ・バインディング除去をアニメーション変換(step 5)より前に済ませることで、step 5 が
                //    複製するクリップにロック済みトグルのバインディングが持ち越されない。
                //  QuestConvert / ConsolidateOnly の両モードで実行する(Quest・PC 双方でメッシュ/スロットを畳むため)。
                //  Keep のみ(=実質何もしない)のときは呼ばない。
                if (HasConsolidationWork(settings))
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "衣装・トグルを整理中...", 0.125f);
                    // 生成物(_Consolidated.anim / .overrideController)を本変換と同じ outputDir へ書き出し、
                    // 同一の ConversionAssetContext(assets)に記録させる。これにより ReportStaleGeneratedAssets が
                    // 生成直後で今も参照中のクリップを「前回の生成物(未使用)」と誤報告せず、
                    // カスタム出力フォルダ指定時も生成物が既定ルートへ散らばらない。
                    ToggleConsolidator.ApplyConsolidation(clone, settings.toggleChoices, report, outputDir, assets);
                }

                // --- 3.6. 全スロット透過レンダラーの自動除外(hideTransparentMaterials設定) ---
                // 透過チーク・アルファフェード演出用クアッド等、全スロットが透過のレンダラーは
                // 不可視マテリアルへの変換よりも描画自体を除外した方が軽い。
                // マテリアル収集より前に適用し、除外レンダラー専用のマテリアルが変換されないようにする
                // (残るレンダラーやアニメーションから参照されるものは通常どおり収集・変換される)。
                // 大型メッシュ・髪(重要レンダラー)は対象外(髪が丸ごと消える事故を防ぐ)。
                // 透過処理はQuest向けのマテリアル対応(不可視化)とセットの最適化のため、
                // ConsolidateOnly(PC最適化・マテリアルは元のまま)では行わない。
                int autoHiddenCount = 0;
                if (questConvert && ShouldHideFullyTransparentRenderers(settings))
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "透過レンダラーを除外中...", 0.13f);
                    autoHiddenCount = HideFullyTransparentRenderers(clone, report);
                }
                else if (questConvert && settings.transparentHandling != TransparentHandling.Hide)
                {
                    // Emulate/Opaque では全スロット透過レンダラーもEditorOnly化せず残し、マテリアルパスで
                    // 再現(乗算/加算)または不透明化する。残したレンダラーはメッシュ数・ポリゴン数の上限に
                    // 数えられる点だけ注意喚起する(常時アクティブなものはビルド時にAAOが統合する)。
                    int keptTransparentRenderers = SimulateFullyTransparentRendererExclusion(clone, new List<Transform>()).Count;
                    if (keptTransparentRenderers > 0)
                    {
                        string outcome = settings.transparentHandling == TransparentHandling.Emulate ? "乗算/加算で半透明を再現" : "不透明化";
                        report.Info($"全スロットが透過のレンダラー {keptTransparentRenderers} 件はEditorOnly化せずQuest版に残します(マテリアルを{outcome})。残したレンダラーはメッシュ数・ポリゴン数の上限に数えられます(常時アクティブなものはビルド時にAAOが統合します)。");
                    }
                }

                // --- 3.65. 効果専用シェーダー(疑似影/アウトライン)のみのレンダラーの自動除外 ---
                // 全スロットが効果専用の補助 lilToon シェーダー(FakeShadow / OutlineOnly)であるレンダラーは、
                // ベース面を持たず Quest では再現できないため、レンダラーごと EditorOnly 化してビルドから除外する
                // (透過の扱いに関わらず常に行う。表情デカールの常時非表示と同じ precedence)。混在レンダラー
                // (疑似影がサブメッシュの一枚に過ぎない髪メッシュ等)は除外せず、疑似影スロットのみ step 4 の
                // マテリアル変換で不可視マテリアルへ差し替える。マテリアル収集(step 4)より前に行い、除外レンダラー
                // 専用のマテリアルが無駄にベイクされないようにする(step 3.5/3.55/3.6 と同じ狙い)。
                int overlayOnlyHiddenCount = 0;
                if (questConvert)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "効果専用シェーダーのレンダラーを除外中...", 0.135f);
                    overlayOnlyHiddenCount = HideOverlayOnlyShaderRenderers(clone, settings, report);
                }

                // --- 3.7. 透過の不透明変換保護(suppressTransparentHide)対象マテリアルの収集 ---
                // Emulate: 髪ストランド本体(髪と名前判定されるレンダラー上の、オーバーレイ名を持たない透過)
                //   のみ不透明として変換する。大型メッシュだけの重要性(非髪の衣装・肌等)や、髪レンダラー上でも
                //   影・デカール・ストッキング等のオーバーレイ名を持つ透過は保護せず、乗算/加算で再現する
                //   (保護=不透明化すると影板・ストッキング・デカールがかえって板になるため)。
                // Hide: 大型メッシュ・髪(重要レンダラー)で使われる透過は不可視マテリアルへ差し替えず不透明化する
                //   (髪・衣装が丸ごと消える事故を防ぐ)。小型オーバーレイのみで使われる透過は非表示化へ回る。
                // いずれのモードでも settings.hideExpressionOverlays 有効時は、表情デカール
                // (不透明ベース+透過サブメッシュ、またはチーク・涙・Front_Shadow 等の名前を持つ透過オーバーレイ)は
                // 抑制せず、非表示化(Hide)/再現(Emulate)へ回す(顔本体は不透明のまま残る)。
                // step 3.6 の後に収集し、EditorOnly化済みサブツリーは一貫して無視する。
                HashSet<Material> suppressTransparentHide = (questConvert && ShouldCollectSuppressTransparentHide(settings))
                    ? CollectSuppressTransparentHideMaterials(clone, null, settings, report)
                    : new HashSet<Material>();

                // --- 3.75. Opaqueモードでの表情デカール強制非表示 ---
                // Opaque モードは透過を一律不透明化するが、表情デカール(チーク・涙等の透過オーバーレイ)を
                // 不透明化すると顔に赤/青の板が出るため、デカールだけは非表示化する(ピン留め契約)。
                // Emulate/Hide では通常のマテリアルパスがデカールを再現/非表示化するためここでは何もしない。
                HashSet<Material> forceHideDecals = new HashSet<Material>();
                if (questConvert && settings.transparentHandling == TransparentHandling.Opaque)
                {
                    foreach (DecalOverlayRow decalRow in DetectExpressionDecals(clone, null, settings))
                    {
                        if (decalRow.material != null) forceHideDecals.Add(decalRow.material);
                    }
                }

                // --- 3.8. ポリゴン削減(メッシュ簡略化) ---
                // 配分計画(settings.decimationPlan)に従い、各レンダラーのメッシュをQEMエッジcollapse
                // (端点配置のみ=頂点は元メッシュの部分集合)で簡略化する。UV・法線・ボーンウェイト・
                // ブレンドシェイプ差分は部分集合として不変のまま保たれる(補間しない)= ビセーム・表情は保たれる。
                // 実行位置の根拠(研究):
                //  ・step 3.6/3.7/3.75(透過処理)は三角形数・髪名でメッシュを判定するため、その後に置く
                //    (削減で髪・衣装が小型オーバーレイ(600三角形以下)扱いされ誤って非表示化される事故を防ぐ)。
                //  ・step 4.5 アトラス化(UV0再配置・サブメッシュ結合)より前に置く(削減後のUV/トポロジーを壊さない。
                //    アトラス化はレンダラーの sharedMesh を実行時に読むため、差し替え済みの簡略化メッシュを自然に使う)。
                //  ・step 9.5 AAO隠面削除(同じSMRメッシュを触る)より前。
                //  ・衣装・トグル整理(3.55)・AAO(9.5)と同様、QuestConvert / ConsolidateOnly の両モードで実行する
                //    (if(questConvert)ブロックの外)。透過処理はここまでで完了済み。
                // EditorOnly化済み(Quest除外・トグル非表示・透過非表示)のレンダラーは絶対に削減しない。
                if (settings.enableDecimation && settings.decimationPlan != null && settings.decimationPlan.Count > 0)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "ポリゴンを削減中...", 0.14f);
                    ApplyPolygonDecimation(clone, settings, outputDir, assets, report);
                }

                // --- 4〜6.5. マテリアル/アニメーション/コンポーネント参照の Quest 変換 ---
                // QuestConvert のみ実行する。ConsolidateOnly(PC最適化)はマテリアルを元(PCシェーダー)の
                // まま残すため、マテリアル収集・シェーダー/テクスチャ変換・アトラス化・マテリアル差し替え
                // アニメの複製・レンダラー/コンポーネント参照のスロット差し替えをまとめてスキップする。
                // materialMap は空・duplicatedClipCount は 0 のまま後段のサマリーへ渡す。
                var materialMap = new Dictionary<Material, Material>();
                int duplicatedClipCount = 0;
                if (questConvert)
                {
                // --- 4. マテリアル収集と変換 ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "マテリアルを収集中...", 0.15f);
                int animationOnlyCount;
                HashSet<Material> meshUsed, particleUsed, animationUsed, componentUsed;
                Dictionary<Material, string> componentSources;
                List<Material> materials = CollectUniqueMaterials(clone, out animationOnlyCount, out meshUsed, out particleUsed, out animationUsed, out componentUsed, out componentSources);

                // コンポーネント参照のみ(レンダラー・アニメーション未参照)のマテリアルを列挙する。
                // Modular AvatarのMaterial Setter等が持つ参照はここで収集しないと変換対象から漏れ、
                // ビルド時にMA等が生成するアニメーションがPC用マテリアルを指して表示が壊れる。
                var componentOnlyNames = new List<string>();
                foreach (Material m in materials)
                {
                    if (!componentUsed.Contains(m) || meshUsed.Contains(m) || particleUsed.Contains(m) || animationUsed.Contains(m)) continue;
                    string source;
                    componentOnlyNames.Add(componentSources.TryGetValue(m, out source) ? m.name + "(" + source + ")" : m.name);
                }
                report.Info($"変換対象マテリアル: {materials.Count} 件(うちアニメーションのみ参照: {animationOnlyCount} 件、コンポーネント参照のみ: {componentOnlyNames.Count} 件)");
                if (componentOnlyNames.Count > 0)
                {
                    report.Info("コンポーネント参照のみのマテリアル(Modular AvatarのMaterial Setter等。レンダラーには未設定): " + string.Join(", ", componentOnlyNames));
                }

                // Canvas(TextMeshProUGUI等)はRendererではないためマテリアル収集対象外。明示的に警告する。
                if (clone.GetComponentInChildren<CanvasRenderer>(true) != null)
                {
                    report.Warn("アバター配下にCanvas(uGUI / TextMeshProUGUI等)が見つかりました。UI系マテリアルは変換対象外のためPC用シェーダーのまま残ります。該当オブジェクトはQuest除外(questExcludePaths)を推奨します。");
                }

                // materialMap は上位スコープ(モード分岐の外)で宣言済み。
                var particleMaterialMap = new Dictionary<Material, Material>(); // パーティクル系レンダラー専用の変換結果
                // 手動オーバーライド(GUID→マテリアル)を一度だけ解決する。未指定のマテリアルは Auto(従来の自動判定)。
                Dictionary<Material, MaterialOverrideEntry> overrides = QuestCompat.ResolveOverrides(settings);
                for (int i = 0; i < materials.Count; i++)
                {
                    Material src = materials[i];
                    float progress = 0.2f + 0.4f * ((i + 1f) / Mathf.Max(1, materials.Count));
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"マテリアル変換中 ({i + 1}/{materials.Count}): {src.name}", progress);

                    bool usedByParticle = particleUsed.Contains(src);
                    // アニメーションのみ参照は通常扱い。コンポーネント参照(MA Material Setter等)は
                    // メッシュへ適用されるマテリアルのため、パーティクル併用でも通常(Default)変換を必ず作る。
                    bool usedByMesh = meshUsed.Contains(src) || componentUsed.Contains(src) || !usedByParticle;

                    // このマテリアルの手動オーバーライドモード(未指定なら Auto)
                    MaterialOverrideEntry overrideEntry;
                    MaterialOverride overrideMode = overrides != null && overrides.TryGetValue(src, out overrideEntry)
                        ? overrideEntry.mode
                        : MaterialOverride.Auto;

                    // Opaqueモードでも表情デカールは非表示化する(不透明な板になる事故を防ぐ)。
                    // 手動オーバーライド指定がある場合はそれを優先する(Auto のときのみ強制)。
                    if (overrideMode == MaterialOverride.Auto && forceHideDecals.Contains(src))
                    {
                        overrideMode = MaterialOverride.Hide;
                        report.Info(string.Format("表情デカール『{0}』は不透明化すると板として見えるため非表示化します(透過の扱い=不透明化)。", src.name));
                    }

                    if (usedByMesh)
                    {
                        // 大型メッシュ・髪で使用される透過マテリアルは非表示化を抑制して不透明変換する
                        // (メッシュ用途のみ。手動指定の Hide は変換器側で常に優先される)
                        Material converted = MaterialQuestConverter.Convert(src, settings, outputDir, report, MaterialUsage.Default, overrideMode, suppressTransparentHide.Contains(src), assets);
                        if (converted != null && converted != src)
                        {
                            materialMap[src] = converted;
                        }
                    }
                    if (usedByParticle)
                    {
                        // パーティクル系レンダラーが参照するマテリアルはパーティクル用に変換する。
                        // メッシュと両方で使われている場合は2種類生成し、レンダラー種別ごとに差し替える。
                        // 手動オーバーライド(ToonStandard/ToonLit等)もそのまま渡し、最終判断は
                        // MaterialQuestConverter のラダーに委ねる(結果はレポートで明示される)。
                        Material convertedParticle = MaterialQuestConverter.Convert(src, settings, outputDir, report, MaterialUsage.Particle, overrideMode, assets: assets);
                        if (convertedParticle != null && convertedParticle != src)
                        {
                            particleMaterialMap[src] = convertedParticle;
                            if (!usedByMesh && !materialMap.ContainsKey(src))
                            {
                                // アニメーション差し替え等から参照された場合もパーティクル版を使う
                                materialMap[src] = convertedParticle;
                            }
                        }
                    }
                }

                // アニメーションから参照される透過マテリアルも同じmaterialMapで不可視マテリアルへ
                // 差し替えられる(PC版で表情等によりフェードイン表示する演出は、Quest版では
                // 非表示のままになる)。該当マテリアルを情報として列挙する(非表示化モードのみ)。
                if (settings.transparentHandling == TransparentHandling.Hide && animationUsed.Count > 0)
                {
                    var hiddenAnimatedNames = new List<string>();
                    foreach (Material src in materials)
                    {
                        if (!animationUsed.Contains(src)) continue;
                        if (!materialMap.ContainsKey(src)) continue;
                        // パーティクル専用マテリアルはパーティクル変換されており非表示化対象外
                        if (particleUsed.Contains(src) && !meshUsed.Contains(src)) continue;
                        // 大型メッシュ・髪で使用される透過マテリアルは非表示化されず不透明変換のため対象外
                        if (suppressTransparentHide.Contains(src)) continue;
                        if (QuestCompat.ClassifyTransparency(src) != QuestCompat.TransparencyClass.Transparent) continue;
                        hiddenAnimatedNames.Add(src.name);
                    }
                    if (hiddenAnimatedNames.Count > 0)
                    {
                        report.Info("アニメーションが参照する透過マテリアルは不可視マテリアルへ差し替えられます(PC版でフェード表示される演出はQuest版では非表示のままです): " + string.Join(", ", hiddenAnimatedNames));
                    }
                }

                // --- 4.5. マテリアルアトラス化(enableAtlas設定) ---
                // マテリアル変換ループの後・アニメーション変換(step5)の前に実行する。
                // この時点ではレンダラーのスロットはまだ元マテリアルのままなので(差し替えはstep6)、
                // RemapMeshesAndMergeSlots は元マテリアル基準でUV再配置・スロット統合を行える。
                // その後 materialMap をアトラスマテリアルで上書きすることで、以降の
                // アニメーション変換(step5)とスロット差し替え(step6)がアトラスマテリアルを参照する。
                bool atlasApplied = false;
                if (settings.enableAtlas)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "マテリアルをアトラス化中...", 0.62f);
                    var atlas = MaterialAtlasser.BuildAtlases(clone, materialMap, animationUsed, overrides, settings, outputDir, report, assets);
                    if (atlas != null)
                    {
                        if (atlas.atlasMap != null && atlas.atlasMap.Count > 0)
                        {
                            // スロットはまだ元マテリアルを保持している(統合後のスロットには先頭メンバーの
                            // 元マテリアルが残り、それが下のmap上書きでアトラスマテリアルへ解決される)
                            MaterialAtlasser.RemapMeshesAndMergeSlots(clone, atlas, outputDir, report, assets);
                            foreach (var kv in atlas.atlasMap) materialMap[kv.Key] = kv.Value;
                            atlasApplied = true; // アトラス統合が実際に行われた(コンポーネント参照差し替え時の整合警告に使う)
                        }
                        if (atlas.excluded != null)
                        {
                            foreach (var ex in atlas.excluded) report.Info("アトラス対象外: " + ex);
                        }
                    }
                }

                // --- 5. アニメーション変換(マテリアル差し替えアニメの複製・差し替え) ---
                if (settings.convertAnimations)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "アニメーションを変換中...", 0.65f);
                    // particleMaterialMap も渡し、パーティクル系レンダラーを対象とする
                    // マテリアル差し替えカーブにはパーティクル用変換結果を優先適用させる
                    AnimationConverter.ConvertAvatarAnimations(clone, materialMap, outputDir, report, particleMaterialMap, assets);
                }
                duplicatedClipCount = CountGeneratedClips(clone, outputDir);

                // --- 6. レンダラーのマテリアル差し替え ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "マテリアルを差し替え中...", 0.75f);
                int swapCount = ApplyMaterialMap(clone, materialMap, particleMaterialMap);
                report.Info($"レンダラーのマテリアルを {swapCount} スロット差し替えました。");

                // --- 6.5. コンポーネント参照マテリアルの差し替え ---
                // Modular AvatarのMaterial Setter / Avatar Menu Creator等が持つシリアライズ済み
                // Material参照はレンダラー差し替え(step6)では書き換わらないため、ここで差し替える
                // (ビルド時にMA等が生成するアニメーションが変換後マテリアルを指すようになる)。
                EditorUtility.DisplayProgressBar(ProgressTitle, "コンポーネント参照のマテリアルを差し替え中...", 0.78f);
                ApplyMaterialMapToComponents(clone, materialMap, overrides, atlasApplied, report);
                } // if (questConvert) --- マテリアル/アニメーション/コンポーネント参照の Quest 変換ここまで ---

                // --- 7〜9. コンポーネント整理(削除数は前後の総数差で計測) ---
                int componentCountBefore = CountComponents(clone);

                // コンストレイント変換・Android非対応コンポーネント削除はQuest向けの処理。
                // ConsolidateOnly(PC最適化)ではPCで有効な構成を保つため行わない。
                if (questConvert && settings.convertUnityConstraints)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "UnityコンストレイントをVRCConstraintへ変換中...", 0.8f);
                    ComponentRemover.ConvertUnityConstraints(clone, outputDir, report);
                }
                if (questConvert && settings.removeUnsupportedComponents)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Android非対応コンポーネントを削除中...", 0.85f);
                    ComponentRemover.RemoveUnsupported(clone, report);
                }
                // --- PhysBone 選択(マージより先に適用。削除済みのPhysBoneがマージのグループ判定に混ざらないようにする)---
                // 一覧・選択はいずれもマージ後(POST-MERGE)のレイアウト基準だが、選択の保存はマージ前の
                // 識別パス(グループはメンバー全員のパス)で行われるため、ここで不要ユニットを先に削除しても、
                // 残ったメンバーは続くマージで1本へ統合される。
                EditorUtility.DisplayProgressBar(ProgressTitle, "PhysBoneの選択を適用中...", 0.87f);
                if (settings.physBoneSelectionMode == PhysBoneSelectionMode.OptIn)
                {
                    // 既定(オプトイン): keepPathsに含まれないPhysBoneをすべて削除する。
                    // keepPathsが空の場合は全PhysBoneが削除される(ユーザーがまだ選択していない初期状態)。
                    if (settings.physBoneKeepPaths == null || settings.physBoneKeepPaths.Count == 0)
                    {
                        report.Info("稼働するPhysBoneが選択されていません(PhysBone設定セクションで選択できます)。");
                    }
                    ComponentRemover.RemoveAllExceptKept(clone, settings.physBoneKeepPaths, report);
                }
                else if (settings.physBoneRemovePaths != null && settings.physBoneRemovePaths.Count > 0)
                {
                    // KeepAllモード: プレビューで「削除」指定されたPhysBone(physBoneRemovePaths)を削除する。
                    // 手動削除はmergePhysBonesのオン/オフに関わらずユーザー指定として適用する。
                    ComponentRemover.RemoveSelectedPhysBones(clone, settings.physBoneRemovePaths, report);
                }
                if (settings.mergePhysBones)
                {
                    // 削除(TrimPhysBones)より先にマージし、揺れを維持したままコンポーネント数を減らす。
                    // アニメーションでON/OFF・プロパティ制御されるPhysBoneをマージから除外するため、
                    // 到達可能な全コントローラー(AnimationConverter.CollectControllersと同範囲)の
                    // 対象パスを渡す。プレビューで「マージしない」指定されたPhysBone
                    // (physBoneNoMergePaths)もマージ対象から除外される。
                    // physBoneLooseMergeが有効なら、設定が異なる兄弟チェーンも先頭メンバーの設定に統一してマージする。
                    EditorUtility.DisplayProgressBar(ProgressTitle, "PhysBoneをマージ中...", 0.88f);
                    HashSet<string> animatedTogglePaths = ComponentRemover.CollectPhysBoneTogglePaths(clone);
                    ComponentRemover.MergePhysBones(clone, animatedTogglePaths, settings.physBoneNoMergePaths, settings.physBoneLooseMerge, report);
                }
                if (settings.trimPhysBonesToPoorLimit)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "PhysBoneをPoor上限内に調整中...", 0.9f);
                    ComponentRemover.TrimPhysBones(clone, report);
                }

                // --- PhysBone Poor上限(コンポーネント8)の厳格チェック ---
                // モバイルではランク計算がPoor上限を超えると、実行時に全PhysBone・コンタクト・
                // コンストレイントが無効化される(揺れが完全に停止する)。ユーザーの厳格化要望により、
                // マージ・削除後も超過している場合は警告ではなくエラーとして報告する(変換自体は完了させる)。
                int survivingPhysBoneCount = 0;
                foreach (VRCPhysBone survivingPhysBone in clone.GetComponentsInChildren<VRCPhysBone>(true))
                {
                    if (!IsInEditorOnlySubtree(survivingPhysBone.transform, clone.transform)) survivingPhysBoneCount++;
                }
                // Poor上限(コンポーネント8)超過のエラーはモバイル(Quest)のランク計算に基づく厳格チェック。
                // ConsolidateOnly(PC最適化)はモバイルのPoor上限が適用されないため、このエラーは出さない
                // (PhysBoneの選択・マージ・トリム自体はモード共通でコンポーネント数削減に寄与する)。
                if (questConvert && survivingPhysBoneCount > QuestLimits.PoorPhysBoneComponents)
                {
                    report.Error($"PhysBoneが{survivingPhysBoneCount}個でPoor上限({QuestLimits.PoorPhysBoneComponents})を超えています。モバイルではランク計算超過時に全PhysBone・コンタクト・コンストレイントが無効化されるため、PhysBone設定で{QuestLimits.PoorPhysBoneComponents}以下に減らしてください。");
                }

                int removedComponentCount = Mathf.Max(0, componentCountBefore - CountComponents(clone));

                // --- 9.5. AAO ブレンドシェイプによる隠面メッシュ削除 / Trace and Optimize 付与 ---
                // 服の下に隠れる肌等を、アバター同梱の shrink/hide ブレンドシェイプで削除し、
                // ポリゴン・メッシュ容量を削減する。付与はすべて AAOMeshRemovalHelper 内のリフレクション
                // 経由(AAO はコンパイル時参照不可)。AAO 未導入時は Warn を出して no-op になる。
                //
                // 【配置理由(順序)】
                //  ・複製作成(step 3)・衣装/トグル整理(step 3.55)・コンポーネント整理(step 7〜9)より後に置く。
                //    LockHidden で EditorOnly 化されたレンダラーや削除済みレンダラーは DetectShrinkShapes(clone)
                //    に現れず、選択パスのフィルタで自然に除外される。
                //  ・削除コンポーネント数(removedComponentCount)の計測より後に追加し、AAO タグ追加が
                //    その集計へ影響しないようにする。
                //  ・最終保存(step 11)より前に置き、追加した AAO コンポーネントが保存対象に含まれるようにする。
                //  ・RemoveMeshByBlendShape は SkinnedMeshRenderer と同じ GameObject に載る必要があるが、
                //    Android 非対応コンポーネント削除は SkinnedMeshRenderer を消さないため、この位置でも対象は残っている。
                //  ・両モード(QuestConvert / ConsolidateOnly)で実行する。隠面メッシュ削除は PC のランク削減にも効くため。
                if (settings.removeHiddenMeshByBlendShape &&
                    settings.hiddenMeshRendererPaths != null && settings.hiddenMeshRendererPaths.Count > 0)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "隠面メッシュ削除(AAO)を適用中...", 0.92f);
                    // 複製上で検出し直し、ユーザーが選択したレンダラーパスに一致する候補だけへ絞る。
                    var chosenSet = new HashSet<string>(settings.hiddenMeshRendererPaths);
                    List<ShrinkShapeCandidate> detected = AAOMeshRemovalHelper.DetectShrinkShapes(clone);
                    var chosen = new List<ShrinkShapeCandidate>();
                    foreach (ShrinkShapeCandidate candidate in detected)
                    {
                        if (candidate != null && candidate.rendererPath != null && chosenSet.Contains(candidate.rendererPath))
                        {
                            chosen.Add(candidate);
                        }
                    }
                    if (chosen.Count > 0)
                    {
                        AAOMeshRemovalHelper.ApplyRemoveMeshByBlendShape(clone, chosen, report);
                    }
                    else
                    {
                        report.Warn("隠面メッシュ削除が有効ですが、選択したレンダラーに対応する隠し/縮小ブレンドシェイプが複製内で見つかりませんでした(スキップ)。");
                    }
                }
                // --- 9.55. SkinnedMesh統合(顔以外を1つへ) ---
                // 顔(ビセーム/まばたき)以外の SkinnedMeshRenderer を AAO MergeSkinnedMesh で1つへ統合し、
                // SMR数・マテリアルスロット数を確実に削減する(Quest Poor上限 SMR2/スロット4対策)。
                // 統合ソースの選定(顔=分離維持 / Cloth・EditorOnly・個別除外=対象外)は
                // SkinnedMeshMergePlanner が行い、実際の結合・ブレンドシェイプ改名・スロット再マップ・
                // アニメ再パスはビルド時(NDMF)にAAOが行う(RemoveMeshByBlendShape と同じ挙動)。
                // 【順序】衣装/トグル整理(LockVisible)より後で、統合ソースが常時表示バケットに揃った状態で走る。
                //   ヘルパー内で Trace and Optimize も確保するため、下の ensureTraceAndOptimize より前でも成立する。
                if (settings.mergeSkinnedMeshesMode != SkinnedMeshMergeMode.None)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "SkinnedMeshを統合中...", 0.93f);
                    SkinnedMeshMergePlan mergePlan = SkinnedMeshMergePlanner.BuildPlan(
                        clone, settings.mergeSkinnedMeshesMode, settings.skinnedMeshMergeOptOutPaths, settings.smrMergeGroups);
                    AAOMeshMergeHelper.ApplyMergeSkinnedMesh(clone, mergePlan, report);
                }

                // Trace and Optimize は AAO のビルド時最適化(RemoveMeshByBlendShape の適用や
                // メッシュ・スロット統合)を走らせるための前提。無ければ複製ルートへ追加する。
                if (settings.ensureTraceAndOptimize)
                {
                    AAOMeshRemovalHelper.EnsureTraceAndOptimize(clone, report);
                }

                // --- 10. 元アバターの非アクティブ化 ---
                if (settings.deactivateOriginal)
                {
                    Undo.RecordObject(sourceGo, UndoLabel);
                    sourceGo.SetActive(false);
                    report.Info($"元のアバター「{sourceGo.name}」を非アクティブ化しました。");
                }

                // --- 11. 仕上げ ---
                EditorUtility.DisplayProgressBar(ProgressTitle, "仕上げ処理中...", 0.95f);
                // アップロード対象の複製をシーンで選択・ハイライトし、ユーザーが取り違えないようにする
                Selection.activeGameObject = clone;
                EditorGUIUtility.PingObject(clone);
                EditorSceneManager.MarkSceneDirty(clone.scene);
                AssetDatabase.SaveAssets();

                // 今回の実行で書き込まれなかった出力フォルダ内の残存ファイルを報告する(削除はしない)。
                // ConsolidateOnly(PC最適化)はこの出力フォルダへ何も書き込まないため、以前のQuest変換の
                // 生成物をすべて「未使用」と誤報告してしまう。よってQuest変換モードでのみ報告する。
                if (questConvert) ReportStaleGeneratedAssets(outputDir, assets, report);

                string uploadStateNote;
                if (settings.deactivateOriginal && removedPriorClones > 0) uploadStateNote = "元アバターは非アクティブ化し、古い複製は削除済みです。";
                else if (settings.deactivateOriginal) uploadStateNote = "元アバターは非アクティブ化済みです。";
                else if (removedPriorClones > 0) uploadStateNote = "古い複製は削除済みです。元アバターはアクティブなまま残っているので、アップロード対象を取り違えないよう注意してください。";
                else uploadStateNote = "元アバターはアクティブなまま残っているので、アップロード対象を取り違えないよう注意してください。";
                report.Info($"アップロードするのはこの複製 '{cloneName}' です(シーンでハイライト表示しました)。{uploadStateNote}");
                if (questConvert)
                {
                    report.Info($"変換完了 - 変換マテリアル数: {materialMap.Count} / 複製アニメ数: {duplicatedClipCount} / 削除コンポーネント数: {removedComponentCount} / Quest除外数: {excludedCount} / 透過自動除外数: {autoHiddenCount} / 効果専用除外数: {overlayOnlyHiddenCount} / 生成先: {outputDir}");
                }
                else
                {
                    string toggleNote = HasConsolidationWork(settings) ? "適用" : "なし";
                    report.Info($"PC最適化完了(シェーダー変換なし) - 衣装・トグル整理: {toggleNote} / 削除コンポーネント数: {removedComponentCount} / Quest除外数: {excludedCount}。マテリアルは元(PCシェーダー)のまま変更していません。");
                }

                // MA互換監査: クローンに載る Modular Avatar コンポーネントの把握状況を1行で報告する(R7)。
                MACompatAudit.AuditCoverage(clone, "Quest変換", report);
                return clone;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                report.Error($"変換中に予期しない例外が発生しました: {ex.Message}");
                // 途中まで変換された複製は削除せず残す(ユーザーが状態を確認できるように)
                return clone;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                // 成功・例外いずれの経路でも、この変換で積まれたUndo操作を単一グループへ折り畳む。
                Undo.SetCurrentGroupName(UndoLabel);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        /// <summary>
        /// settings.toggleChoices に Keep 以外(LockVisible / LockHidden)の指定が1件でもあるか。
        /// 実質何も変えない(全て Keep・空・null)場合は ToggleConsolidator.ApplyConsolidation を
        /// 呼ばないためのガード(step 3.55 / 完了サマリーの両方で使う)。
        /// </summary>
        private static bool HasConsolidationWork(QuestConvertSettings settings)
        {
            if (settings == null || settings.toggleChoices == null) return false;
            foreach (ToggleGroupChoice choice in settings.toggleChoices)
            {
                if (choice != null && choice.choice != ToggleLockChoice.Keep) return true;
            }
            return false;
        }

        /// <summary>
        /// 出力フォルダ配下のうち今回の変換で書き込まれなかったアセットを
        /// 「前回の生成物(未使用)」として情報報告する。削除はしない
        /// (シーン上の古い _Quest クローン等から参照されている可能性があるため)。
        /// </summary>
        private static void ReportStaleGeneratedAssets(string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            try
            {
                if (!AssetDatabase.IsValidFolder(outputDir)) return;
                var stale = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { outputDir }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (AssetDatabase.IsValidFolder(path)) continue; // フォルダ自体は対象外
                    if (assets.WasWritten(path)) continue;
                    stale.Add(path);
                }
                if (stale.Count == 0) return;
                report.Info(string.Format(
                    "前回の生成物(未使用): 今回の変換で書き込まれなかったファイルが出力フォルダに {0} 件残っています(削除はしません。以前の変換結果やシーン上の古い _Quest クローンから参照されている可能性があるため、不要と確認できた場合のみ手動で削除してください):\n  {1}",
                    stale.Count, string.Join("\n  ", stale)));
            }
            catch (Exception ex)
            {
                // 報告のみの補助処理のため、失敗しても変換自体は成功として扱う
                Debug.LogWarning("[RARA QuestConverter] 前回の生成物(未使用)の列挙に失敗しました: " + ex.Message);
            }
        }

        /// <summary>
        /// プレハブインスタンス状態を保持したままアバターを複製する。
        /// ペーストボード複製が使えない場合は Object.Instantiate にフォールバックする。
        /// </summary>
        private static GameObject DuplicateAvatar(GameObject source, ConversionReport report)
        {
            GameObject clone = null;
            try
            {
                Selection.activeGameObject = source;
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                GameObject duplicated = Selection.activeGameObject;
                // 複製結果の検証: 元と別オブジェクトが選択されていれば複製成功とみなす。
                // 名前による検証はしない(Unityは "Nita (1)" → "Nita (2)"、"Avatar 1" → "Avatar 2" のように
                // 末尾の数字をインクリメントするため、名前の前方一致では正当な複製を誤って拒否し、
                // 複製がシーンに孤児として残ってしまう)。
                if (duplicated != null && duplicated != source)
                {
                    if (duplicated.scene == source.scene)
                    {
                        clone = duplicated;
                    }
                    else
                    {
                        // 想定外の複製結果は孤児として残さず破棄してからフォールバックする
                        UnityEngine.Object.DestroyImmediate(duplicated);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            if (clone == null)
            {
                report.Warn("ペーストボード複製に失敗したため Object.Instantiate で複製します(プレハブ接続は保持されません)。");
                clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.transform.localPosition = source.transform.localPosition;
                clone.transform.localRotation = source.transform.localRotation;
                clone.transform.localScale = source.transform.localScale;
                clone.name = source.name; // "(Clone)" を除去(直後に "_Quest" 付与でリネームされる)
            }
            return clone;
        }

        /// <summary>
        /// 再変換の冪等化: sourceGo と同じ親(親が無ければシーンルート)にある、前回の変換で生成された
        /// クローンをシーンから除去する。判定ヒューリスティックは「名前が targetName
        /// ("{sourceName}_Quest"。Unityの重複サフィックス " (1)"/" (2)" 等を許容)と一致し、かつ
        /// VRCAvatarDescriptor を(ルート直下に)持つ」。マーカー用の隠しコンポーネントや子オブジェクトは
        /// 追加せず、名前+デスクリプターのみで識別する(階層を汚さないため)。
        /// 限界: ユーザーが手作業で「X_Quest」という名前を付け VRCAvatarDescriptor を持たせた無関係な
        /// オブジェクトも削除対象になり得る。そのため削除ごとに report.Warn で対象名を明示する。
        /// sourceGo 自身は(名前が一致しても)絶対に削除しない。これにより元アバターや、
        /// 既に "_Quest" クローンを変換元にした場合の変換元も保護される
        /// (なお targetName は常に sourceName より長いため、sourceGo 自身が名前一致することは無い)。
        /// 削除は Undo.DestroyObjectImmediate で行い、変換の Undo(UndoLabel)と同一グループに含める。
        /// </summary>
        private static int RemovePriorGeneratedClones(GameObject sourceGo, string targetName, ConversionReport report)
        {
            // 同じ親の兄弟だけでなく、シーン内の全ルート配下(非アクティブ・別の親へ移動された旧複製も含む)を
            // 走査する。「名前が targetName(重複サフィックス許容)と一致し VRCAvatarDescriptor を持つ」ものが対象。
            // 変換元とその階層(祖先・子孫)は絶対に削除しない。列挙中の階層変化を避けるため先に集めてから削除する。
            var toRemove = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            foreach (GameObject rootGo in sourceGo.scene.GetRootGameObjects())
            {
                if (rootGo == null) continue;
                foreach (VRCAvatarDescriptor desc in rootGo.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                {
                    if (desc == null) continue;
                    GameObject go = desc.gameObject;
                    if (!seen.Add(go)) continue;
                    if (IsSourceOrRelated(go, sourceGo)) continue; // 変換元とその階層は絶対に削除しない
                    if (!IsGeneratedCloneName(go.name, targetName)) continue;
                    toRemove.Add(go);
                }
            }

            foreach (GameObject prior in toRemove)
            {
                report.Warn($"以前の変換結果 '{prior.name}' を削除して作り直します");
                try
                {
                    Undo.DestroyObjectImmediate(prior);
                }
                catch (Exception ex)
                {
                    report.Warn($"以前の複製 '{prior.name}' を削除できませんでした(手動で削除してください): {ex.Message}");
                }
            }

            // 削除しきれず残ったものが無いか最終確認する(黙って残さない)
            foreach (GameObject prior in toRemove)
            {
                if (prior != null)
                {
                    report.Warn($"複製 '{prior.name}' が削除されずシーンに残っています。手動で削除してから再実行してください");
                }
            }
            return toRemove.Count;
        }

        /// <summary>candidate が sourceGo 自身、または sourceGo と祖先・子孫の関係にあるか。</summary>
        private static bool IsSourceOrRelated(GameObject candidate, GameObject sourceGo)
        {
            if (candidate == null || sourceGo == null) return true;
            if (candidate == sourceGo) return true;
            for (Transform p = candidate.transform; p != null; p = p.parent) if (p == sourceGo.transform) return true;
            for (Transform p = sourceGo.transform; p != null; p = p.parent) if (p == candidate.transform) return true;
            return false;
        }

        /// <summary>
        /// name がクローンの目標名 targetName("{sourceName}_Quest")そのものか、Unityの重複サフィックス付き
        /// ("targetName (1)"、"targetName (2)" 等。末尾が半角スペース+括弧+数値)かを判定する。
        /// </summary>
        private static bool IsGeneratedCloneName(string name, string targetName)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(targetName)) return false;
            if (string.Equals(name, targetName, StringComparison.Ordinal)) return true;
            string prefix = targetName + " (";
            if (name.Length > prefix.Length + 1 &&
                name.StartsWith(prefix, StringComparison.Ordinal) &&
                name[name.Length - 1] == ')')
            {
                string inner = name.Substring(prefix.Length, name.Length - prefix.Length - 1);
                int suffix;
                return inner.Length > 0 && int.TryParse(inner, out suffix);
            }
            return false;
        }

        /// <summary>
        /// settings.questExcludePaths の各パスをクローン上で解決し、EditorOnlyタグ付与+非アクティブ化する。
        /// EditorOnlyタグの付いたオブジェクトはVRChatビルドでアバターから除去される
        /// (クローンにのみ適用するため、PC版=元アバターには影響しない)。除外できた件数を返す。
        /// </summary>
        private static int ApplyQuestExclusions(GameObject clone, QuestConvertSettings settings, ConversionReport report)
        {
            if (settings.questExcludePaths == null || settings.questExcludePaths.Count == 0) return 0;

            int excludedCount = 0;
            foreach (string path in settings.questExcludePaths)
            {
                if (path == null) continue;

                Transform target = QuestCompat.FindByPath(clone.transform, path);
                if (target == null)
                {
                    report.Warn($"除外パスが見つかりません: {path}");
                    continue;
                }
                if (target == clone.transform)
                {
                    // ルート自身をEditorOnly化するとアバター全体がビルドから消えるため許可しない
                    report.Warn($"除外パスがアバタールート自身を指しているためスキップしました: \"{path}\"");
                    continue;
                }

                // ボーン安全チェック: 除外サブツリー外の(=残る)SkinnedMeshRendererが
                // 除外サブツリー内のボーンを参照している場合、ビルド時にボーンが削除されて
                // メッシュが潰れるため警告する(除外自体はユーザー指定を尊重して実行する)。
                WarnIfExcludedSubtreeContainsUsedBones(clone, target, path, report);

                // デスクリプター参照チェック: アイトラッキングの目ボーン・まぶた、
                // リップシンクの口メッシュが除外サブツリー内にある場合も参照切れになるため警告する。
                WarnIfExcludedSubtreeContainsDescriptorReferences(clone, target, path, report);

                // MA互換チェック: 除外サブツリーへ入り込む/そこから持ち出される MA 参照を警告する(R3/H4)。
                MACompatAudit.WarnMaReferencesIntoExcludedSubtree(clone, target, path, report);

                GameObject go = target.gameObject;
                go.tag = QuestCompat.EditorOnlyTag;
                go.SetActive(false);
                excludedCount++;
                report.Info($"Quest除外: {path}(EditorOnly化)");
            }

            // 全レンダラーが除外された場合、Quest版アバターは何も表示されなくなる
            if (excludedCount > 0)
            {
                bool anyRendererKept = false;
                foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                {
                    if (!IsInEditorOnlySubtree(renderer.transform, clone.transform))
                    {
                        anyRendererKept = true;
                        break;
                    }
                }
                if (!anyRendererKept)
                {
                    report.Warn("Quest除外によりレンダラーが1つも残っていません。このままビルドするとQuest版アバターは何も表示されません。");
                }
            }
            return excludedCount;
        }

        /// <summary>
        /// 除外予定サブツリー(target配下)に、除外されずに残るSkinnedMeshRendererが参照する
        /// ボーン(bones / rootBone)が含まれる場合に警告する。
        /// </summary>
        private static void WarnIfExcludedSubtreeContainsUsedBones(GameObject clone, Transform target, string path, ConversionReport report)
        {
            foreach (SkinnedMeshRenderer smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                // 除外サブツリー内・既存のEditorOnly配下のSMRはビルドに残らないため対象外
                if (smr.transform == target || smr.transform.IsChildOf(target)) continue;
                if (IsInEditorOnlySubtree(smr.transform, clone.transform)) continue;

                bool usesExcludedBone = smr.rootBone != null && (smr.rootBone == target || smr.rootBone.IsChildOf(target));
                if (!usesExcludedBone)
                {
                    Transform[] bones = smr.bones;
                    if (bones != null)
                    {
                        foreach (Transform bone in bones)
                        {
                            if (bone != null && (bone == target || bone.IsChildOf(target)))
                            {
                                usesExcludedBone = true;
                                break;
                            }
                        }
                    }
                }
                if (usesExcludedBone)
                {
                    report.Warn($"Quest除外 \"{path}\" のサブツリーには、残るメッシュ「{smr.name}」が参照するボーンが含まれています。ビルド時にボーンが削除され、このメッシュが潰れて表示される可能性があります。除外パスを見直してください。");
                }
            }
        }

        /// <summary>
        /// 除外予定サブツリー(target配下)に、VRCAvatarDescriptorが参照するオブジェクト
        /// (アイトラッキングの目ボーン・まぶた、リップシンクの口メッシュ)が含まれる場合に警告する。
        /// EditorOnly化されたオブジェクトはビルド時に削除されるため、参照が切れて
        /// アイトラッキング・リップシンクが黙って動かなくなったり、SDKのビルド検証でエラーになる。
        /// </summary>
        private static void WarnIfExcludedSubtreeContainsDescriptorReferences(GameObject clone, Transform target, string path, ConversionReport report)
        {
            var descriptor = clone.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor == null) return;

            var lost = new List<string>();

            // リップシンク(ビセーム/あごブレンドシェイプ)用の口メッシュ
            if ((descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape ||
                 descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape) &&
                descriptor.VisemeSkinnedMesh != null &&
                IsSameOrChildOf(descriptor.VisemeSkinnedMesh.transform, target))
            {
                lost.Add("リップシンクの口メッシュ(Face Mesh)");
            }

            // アイトラッキング(目ボーン・まぶた)
            if (descriptor.enableEyeLook)
            {
                var eye = descriptor.customEyeLookSettings;
                if (IsSameOrChildOf(eye.leftEye, target) || IsSameOrChildOf(eye.rightEye, target))
                {
                    lost.Add("アイトラッキングの目ボーン(Left/Right Eye Bone)");
                }
                if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes &&
                    eye.eyelidsSkinnedMesh != null &&
                    IsSameOrChildOf(eye.eyelidsSkinnedMesh.transform, target))
                {
                    lost.Add("まぶた用メッシュ(Eyelids Mesh)");
                }
                if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Bones &&
                    (IsSameOrChildOf(eye.upperLeftEyelid, target) || IsSameOrChildOf(eye.upperRightEyelid, target) ||
                     IsSameOrChildOf(eye.lowerLeftEyelid, target) || IsSameOrChildOf(eye.lowerRightEyelid, target)))
                {
                    lost.Add("まぶたボーン(Eyelid Bones)");
                }
            }

            foreach (string item in lost)
            {
                report.Warn($"Quest除外 \"{path}\" のサブツリーには、VRCAvatarDescriptorが参照する{item}が含まれています。ビルド時に削除されて参照が切れ、アイトラッキング・リップシンクが動かなくなったり、SDKのビルド検証でエラーになる可能性があります。除外パスを見直してください。");
            }
        }

        /// <summary>t が target 自身またはその配下か(t が null なら false)。</summary>
        private static bool IsSameOrChildOf(Transform t, Transform target)
        {
            return t != null && (t == target || t.IsChildOf(target));
        }

        /// <summary>
        /// 全マテリアルスロットが透過(アルファブレンド)クラスの Mesh/SkinnedMeshRenderer を
        /// EditorOnlyタグ+非アクティブ化してQuest版のビルドから除外し、除外した件数を返す。
        /// これは1回の変換内の処理であり、settings.questExcludePaths には追加しない。
        /// ・パーティクル系レンダラーは対象外(パーティクル変換で対応)。
        /// ・大型メッシュ・髪(IsSignificantRenderer)は対象外(髪・衣装が丸ごと消える事故を防ぐ。
        ///   透過マテリアルは非表示化を抑制して不透明として変換される)。
        /// ・配下に残すレンダラーを含むオブジェクトは除外しない(サブツリーごと消えてしまうため。
        ///   透過マテリアル自体は不可視マテリアルへの変換で対応される)。
        /// ・全レンダラーが対象になる場合は何もしない(アバターが空になるのを防ぐガード)。
        /// </summary>
        private static int HideFullyTransparentRenderers(GameObject clone, ConversionReport report)
        {
            var candidates = new List<Renderer>();
            bool anyRendererKept = false;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, clone.transform)) continue;
                if (!IsFullyTransparentRenderer(renderer))
                {
                    anyRendererKept = true;
                    continue;
                }
                // 大型メッシュ・髪は非表示化の候補にしない(残るレンダラーとして数える)。
                // 透過マテリアルは suppressTransparentHide により不透明として変換される。
                if (IsSignificantRenderer(renderer))
                {
                    string keptPath = QuestCompat.GetRelativePath(clone.transform, renderer.transform);
                    report.Info($"『{keptPath}』は透過マテリアルですが大型メッシュ/髪と判定したため非表示化しません(不透明として変換)。見た目はマテリアル設定で調整できます。");
                    anyRendererKept = true;
                    continue;
                }
                candidates.Add(renderer);
            }
            if (candidates.Count == 0) return 0;

            // ガード: 全レンダラーが透過のみのアバターを空にしない
            if (!anyRendererKept)
            {
                report.Warn("すべてのレンダラーが透過マテリアルのみのため、レンダラー単位のQuest除外は行いません(アバターが空になるのを防ぐため、透過マテリアルは不可視マテリアルへの変換で対応します)。");
                return 0;
            }

            // 配下に「残すレンダラー」(候補以外)を含む候補はEditorOnly化しない
            // (EditorOnlyはサブツリーごとビルドから除去されるため)
            var candidateSet = new HashSet<Renderer>(candidates);
            var hidden = new List<Renderer>();
            foreach (Renderer candidate in candidates)
            {
                // ルート自身はEditorOnly化するとアバター全体がビルドから消えるため対象外
                // (ApplyQuestExclusionsと同じ方針。透過マテリアル自体は不可視マテリアルへの変換で対応される)
                if (candidate.transform == clone.transform)
                {
                    report.Info("アバタールート自身のレンダラーは全スロットが透過ですが、オブジェクト除外はせず不可視マテリアルへの変換で対応します。");
                    continue;
                }

                bool containsKeptRenderer = false;
                foreach (Renderer descendant in candidate.GetComponentsInChildren<Renderer>(true))
                {
                    // EditorOnlyサブツリー内の子孫はどのみちビルドで除去されるため「残すレンダラー」に数えない
                    if (IsInEditorOnlySubtree(descendant.transform, clone.transform)) continue;
                    if (!candidateSet.Contains(descendant))
                    {
                        containsKeptRenderer = true;
                        break;
                    }
                }
                if (containsKeptRenderer)
                {
                    string skippedPath = QuestCompat.GetRelativePath(clone.transform, candidate.transform);
                    report.Info($"『{skippedPath}』は全スロットが透過ですが、配下に残すレンダラーがあるためオブジェクト除外はせず、不可視マテリアルへの変換で対応します。");
                    continue;
                }
                hidden.Add(candidate);
            }

            // 先に全対象をEditorOnly化してからボーン安全チェックを行う
            // (除外対象どうしがボーンを参照し合っていても誤警告しないように)
            foreach (Renderer renderer in hidden)
            {
                GameObject go = renderer.gameObject;
                go.tag = QuestCompat.EditorOnlyTag;
                go.SetActive(false);
            }
            foreach (Renderer renderer in hidden)
            {
                string path = QuestCompat.GetRelativePath(clone.transform, renderer.transform);
                WarnIfExcludedSubtreeContainsUsedBones(clone, renderer.transform, path, report);
                report.Warn($"全スロットが透過のため『{path}』をQuest版から除外しました(EditorOnly化+非アクティブ化。透過の扱い=非表示)。");
            }
            return hidden.Count;
        }

        /// <summary>
        /// レンダラーが Mesh/SkinnedMeshRenderer で、かつ全スロットが透過(アルファブレンド)マテリアルか。
        /// 空スロット(null)を含む場合やスロットが無い場合は対象外(nullはOpaque扱い)。
        /// </summary>
        private static bool IsFullyTransparentRenderer(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return false;
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return false;
            foreach (Material material in materials)
            {
                if (QuestCompat.ClassifyTransparency(material) != QuestCompat.TransparencyClass.Transparent)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 全マテリアルスロットが効果専用の補助 lilToon シェーダー(疑似影 / アウトラインのみ)である
        /// Mesh/SkinnedMeshRenderer を EditorOnly タグ+非アクティブ化して Quest 版ビルドから除外し、除外数を返す。
        /// これらはベース面を持たない演出パスで Quest では再現できず、スロット単位では materialMap 経由で
        /// 不可視マテリアルへ差し替えられる。レンダラーの全スロットがこの種別だけなら、レンダラーごと除外した
        /// 方が軽い(メッシュ数・ポリゴン数に数えられず、FX でトグルされても無害)。混在レンダラー(疑似影が
        /// サブメッシュの一枚に過ぎない髪メッシュ等)は除外しない(ベース面のスロットが残るため。疑似影スロット
        /// のみ不可視化される)。ユーザーが明示的に別の変換方法を選んだスロットを含むレンダラーも除外しない
        /// (指定を尊重する)。ガードは HideFullyTransparentRenderers と同じ: ルート自身・残すレンダラーを配下に
        /// 含むもの・全レンダラーが対象になる場合は除外しない(アバターを空にしない)。
        /// </summary>
        private static int HideOverlayOnlyShaderRenderers(GameObject clone, QuestConvertSettings settings, ConversionReport report)
        {
            Dictionary<Material, MaterialOverrideEntry> overrides = QuestCompat.ResolveOverrides(settings);

            var candidates = new List<Renderer>();
            bool anyRendererKept = false;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, clone.transform)) continue;
                if (!IsFullyOverlayOnlyRenderer(renderer) || HasVisibleMaterialOverride(renderer, overrides))
                {
                    anyRendererKept = true;
                    continue;
                }
                candidates.Add(renderer);
            }
            if (candidates.Count == 0) return 0;

            // ガード: 全レンダラーが効果専用のみ(通常あり得ない)のときはアバターを空にしない。
            // スロット単位の不可視化(materialMap)で対応する。
            if (!anyRendererKept)
            {
                report.Warn("すべてのレンダラーが効果専用シェーダー(疑似影/アウトライン)のみのため、レンダラー単位の除外は行いません(スロット単位で不可視化します)。");
                return 0;
            }

            // 配下に「残すレンダラー」(候補以外)を含む候補は EditorOnly 化しない(サブツリーごと消えるため)。
            var candidateSet = new HashSet<Renderer>(candidates);
            var hidden = new List<Renderer>();
            foreach (Renderer candidate in candidates)
            {
                if (candidate.transform == clone.transform)
                {
                    // ルート自身を EditorOnly 化するとアバター全体が消えるため対象外(スロット単位で不可視化)。
                    continue;
                }
                bool containsKeptRenderer = false;
                foreach (Renderer descendant in candidate.GetComponentsInChildren<Renderer>(true))
                {
                    if (IsInEditorOnlySubtree(descendant.transform, clone.transform)) continue;
                    if (!candidateSet.Contains(descendant))
                    {
                        containsKeptRenderer = true;
                        break;
                    }
                }
                if (containsKeptRenderer) continue;
                hidden.Add(candidate);
            }

            // 先に全対象を EditorOnly 化してからボーン安全チェックを行う(対象どうしのボーン参照で誤警告しない)。
            foreach (Renderer renderer in hidden)
            {
                GameObject go = renderer.gameObject;
                go.tag = QuestCompat.EditorOnlyTag;
                go.SetActive(false);
            }
            foreach (Renderer renderer in hidden)
            {
                string path = QuestCompat.GetRelativePath(clone.transform, renderer.transform);
                WarnIfExcludedSubtreeContainsUsedBones(clone, renderer.transform, path, report);
                report.Warn($"全スロットが効果専用シェーダー(疑似影/アウトライン)のため『{path}』をQuest版から除外しました(EditorOnly化+非アクティブ化)。");
            }
            return hidden.Count;
        }

        /// <summary>
        /// レンダラーが Mesh/SkinnedMeshRenderer で、全スロットが効果専用の補助 lilToon シェーダー
        /// (疑似影 / アウトラインのみ。QuestCompat.IsOverlayOnlyShader)か。
        /// 空スロット(null)を含む場合やスロットが無い場合は false(混在扱い=除外しない)。
        /// </summary>
        private static bool IsFullyOverlayOnlyRenderer(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) return false;
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return false;
            foreach (Material material in materials)
            {
                if (material == null) return false;
                if (!QuestCompat.IsOverlayOnlyShader(material.shader, material.name)) return false;
            }
            return true;
        }

        /// <summary>
        /// レンダラーのいずれかのスロットのマテリアルに、可視化する手動オーバーライド指定
        /// (Keep / ToonStandard / ToonLit / ParticleAdditive / ParticleMultiply)があるか。
        /// これらはユーザーが「見えるように」指定したものなので、レンダラーごとの除外対象から外す
        /// (Auto=自動非表示・Hide=どのみち非表示 は除外してよい)。
        /// </summary>
        private static bool HasVisibleMaterialOverride(Renderer renderer, Dictionary<Material, MaterialOverrideEntry> overrides)
        {
            if (overrides == null || overrides.Count == 0) return false;
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null) continue;
                MaterialOverrideEntry entry;
                if (overrides.TryGetValue(material, out entry) && entry != null &&
                    entry.mode != MaterialOverride.Auto && entry.mode != MaterialOverride.Hide)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 透過自動非表示(hideTransparentMaterials)の対象外とすべき「重要な」レンダラーか。
        /// ・メッシュの総三角形数が QuestCompat.AutoHideMaxTriangles を超える(髪・衣装など大型メッシュ)
        /// ・GameObject名・メッシュ名・いずれかのスロットマテリアル名が髪を示す(QuestCompat.IsHairLikeName)
        /// のいずれかに該当すれば重要と判定する(頬染めクアッド等の小型オーバーレイのみが非表示化対象)。
        /// メッシュがnullの場合は三角形数0(小型)として扱う。
        /// </summary>
        private static bool IsSignificantRenderer(Renderer renderer)
        {
            Mesh mesh = GetRendererMesh(renderer);
            if (GetMeshTriangleCount(mesh) > QuestCompat.AutoHideMaxTriangles) return true;
            return IsHairNamedRenderer(renderer);
        }

        /// <summary>
        /// レンダラーが髪と「名前」で判定されるか(IsSignificantRenderer の髪名判定ぶんのみ。三角形数は見ない)。
        /// GameObject名・メッシュ名・いずれかのスロットマテリアル名が髪(QuestCompat.IsHairLikeName)を示せば true。
        /// Emulate時の透過保護(乗算/加算での再現を抑制して不透明化するのは髪ストランド本体のみ)に使う。
        /// 大型メッシュだけの重要性(非髪の衣装・肌等)は Emulate では保護せず、乗算/加算で見た目を残す。
        /// </summary>
        private static bool IsHairNamedRenderer(Renderer renderer)
        {
            if (QuestCompat.IsHairLikeName(renderer.gameObject.name)) return true;
            Mesh mesh = GetRendererMesh(renderer);
            if (mesh != null && QuestCompat.IsHairLikeName(mesh.name)) return true;
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null && QuestCompat.IsHairLikeName(material.name)) return true;
            }
            return false;
        }

        /// <summary>
        /// メッシュの総三角形数(全サブメッシュのインデックス数合計 ÷ 3)。nullなら0。
        /// GetIndexCount はメッシュのRead/Write設定に関わらず取得できる。
        /// </summary>
        private static int GetMeshTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                total += mesh.GetIndexCount(i);
            }
            return (int)Math.Min(total / 3, int.MaxValue);
        }

        /// <summary>
        /// 透過の不透明変換保護(suppressTransparentHide)対象マテリアルを収集する。
        /// Mesh/SkinnedMeshRenderer のみ対象で、EditorOnlyサブツリー配下と extraExcludedRoots 配下は
        /// 一貫して無視する(Convert では step 3.6 の後に呼び、プレビューでは除外シミュレーション後に呼ぶ)。
        ///
        /// 【保護対象の判定(透過の扱いで分岐)】
        ///   ・Emulate: 髪ストランド本体のみ保護する。すなわち「髪と名前判定されるレンダラー
        ///     (IsHairNamedRenderer)」上の透過マテリアルのうち、オーバーレイ名トークン
        ///     (QuestCompat.IsOverlayEmulationName = 影・デカール・ストッキング・涙・ハイライト等)を
        ///     持たないものだけを不透明変換保護する。大型メッシュだけの重要性(非髪の衣装・肌等)は
        ///     Emulate では保護しない — 乗算/加算で見た目を残すため、保護(不透明化)するとかえって
        ///     影板・ストッキング・デカール等が板になる。髪レンダラー上のオーバーレイ(Front_Shadow 等)も
        ///     名前トークンで保護から外し、乗算/加算で再現する。
        ///   ・Hide: 従来どおり重要レンダラー(IsSignificantRenderer = 大型メッシュ・髪)を保護する
        ///     (髪・衣装が丸ごと消えるのを防ぐ)。Opaque は本メソッドを呼ばない(全て不透明化)。
        ///
        /// 小型オーバーレイのみで使われる透過マテリアルはこの集合に入らず、非表示化(Hide)/再現(Emulate)へ回る。
        /// settings.hideExpressionOverlays 有効時は、重要レンダラー上でも表情デカール
        /// (DetectExpressionDecals が検出する透過オーバーレイ。顔の不透明ベース上の透過サブメッシュや、
        /// チーク・涙・Front_Shadow 等の名前を持つ透過マテリアル)はこの抑制集合へ入れず、
        /// 非表示化パス(MaterialQuestConverter が不可視Multiplyマテリアルへ差し替え)へ回す。
        /// これにより顔本体(不透明スロット)は残しつつ、デカールのスロットだけ不可視化できる。
        /// report が指定された場合、非表示化する表情デカールを個別に報告し、あわせて
        /// 抑制されたマテリアルを小型レンダラー(非表示化対象)側でも使っている箇所を個別に警告する
        /// (そのレンダラーは非表示化されず不透明の板として表示されるため)。
        /// </summary>
        private static HashSet<Material> CollectSuppressTransparentHideMaterials(GameObject root, List<Transform> extraExcludedRoots, QuestConvertSettings settings, ConversionReport report)
        {
            var result = new HashSet<Material>();

            // 表情デカール(透過オーバーレイ)を検出する。重要レンダラー上でも非表示化するため、
            // 検出したマテリアルは以降の抑制集合(不透明変換)へ入れない。
            var decalMaterials = new Dictionary<Material, DecalOverlayRow>();
            foreach (DecalOverlayRow decalRow in DetectExpressionDecals(root, extraExcludedRoots, settings))
            {
                if (decalRow.material != null && !decalMaterials.ContainsKey(decalRow.material))
                {
                    decalMaterials[decalRow.material] = decalRow;
                }
            }

            // Emulate は髪ストランド本体の透過のみ不透明化保護する(乗算/加算はUnlitのため髪の陰影に不向き)。
            // Hide は従来どおり大型メッシュ・髪(IsSignificantRenderer)を保護する。
            bool emulate = settings.transparentHandling == TransparentHandling.Emulate;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                if (IsUnderAny(renderer.transform, extraExcludedRoots)) continue;
                bool protectsRenderer = emulate ? IsHairNamedRenderer(renderer) : IsSignificantRenderer(renderer);
                if (!protectsRenderer) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    if (QuestCompat.ClassifyTransparency(material) != QuestCompat.TransparencyClass.Transparent) continue;
                    // 表情デカールは重要レンダラー上でも非表示化/再現へ回す(不透明変換の抑制集合へ入れない)。
                    if (decalMaterials.ContainsKey(material)) continue;
                    // Emulate: 髪レンダラー上でも、影・デカール・ストッキング等のオーバーレイ名を持つ透過は
                    // 乗算/加算で再現する(不透明化しない)。髪ストランド本体(オーバーレイ名なし)のみ保護する。
                    if (emulate && QuestCompat.IsOverlayEmulationName(material.name)) continue;
                    result.Add(material);
                }
            }

            // 表情デカールの扱いは透過モードに従う(Hide=非表示化 / Emulate=乗算/加算で再現)。
            // 本メソッドは Emulate/Hide のみで呼ばれる(Opaqueは呼ばれず、デカール強制非表示は変換ループ側で処理)。
            string decalOutcomeVerb = settings.transparentHandling == TransparentHandling.Hide
                ? "非表示化"
                : "乗算/加算で半透明を再現";

            // 表情デカールを個別に報告する(実変換時のみ。プレビューは report=null)。
            if (report != null && decalMaterials.Count > 0)
            {
                foreach (KeyValuePair<Material, DecalOverlayRow> kv in decalMaterials)
                {
                    DecalOverlayRow decalRow = kv.Value;
                    string reasonText = decalRow.reason == "structural"
                        ? "不透明ベース+透過サブメッシュ"
                        : "名前一致(デカール)";
                    report.Info(string.Format(
                        "表情デカールを{4}: {0}({1} スロット{2}, {3})",
                        kv.Key.name,
                        string.IsNullOrEmpty(decalRow.rendererPath) ? "(ルート)" : decalRow.rendererPath,
                        decalRow.slotIndex, reasonText, decalOutcomeVerb));
                }
            }

            // 抑制対象マテリアルを小型レンダラーでも共有している場合の個別警告。
            // 全スロット透過の小型レンダラーは step 3.6 でレンダラーごと非表示化されるため、
            // ここで検出されるのは非透過スロットが混在して残るレンダラーのみ。
            if (report != null && result.Count > 0)
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                    if (IsUnderAny(renderer.transform, extraExcludedRoots)) continue;
                    if (IsSignificantRenderer(renderer)) continue;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null || !result.Contains(material)) continue;
                        report.Warn(string.Format(
                            "小型レンダラー '{0}' は大型メッシュ/髪と透過マテリアル『{1}』を共有しているため、半透明の再現も非表示化もされず不透明の板として表示される可能性があります。不要な場合はQuest除外(questExcludePaths)への追加を検討してください。",
                            GetHierarchyPath(renderer.transform, root.transform), material.name));
                        break; // 同一レンダラーへの警告は1回で十分
                    }
                }
            }

            // 非表示化する表情デカールのマテリアルが、別の大型メッシュのスロット0(主マテリアル)
            // としても共有されている場合の警告。materialMap はマテリアル単位で差し替えるため、
            // このデカールを隠すと同マテリアルを使う大型メッシュ本体も一緒に消えてしまう(スロット単位の
            // 個別非表示化はできない)。重要レンダラーのスロット0は名前・構造いずれのルールでも検出源に
            // ならない(スロット0は構造対象外、かつ重要レンダラーのスロット0は名前ルール対象外)ため、
            // ここでヒットするのは「他所で検出されたデカールを大型メッシュの主マテリアルとして再利用」した場合のみ。
            if (report != null && decalMaterials.Count > 0)
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                    if (IsUnderAny(renderer.transform, extraExcludedRoots)) continue;
                    if (!IsSignificantRenderer(renderer)) continue;
                    Material[] mats = renderer.sharedMaterials;
                    if (mats == null || mats.Length == 0) continue;
                    Material primary = mats[0];
                    if (primary == null || !decalMaterials.ContainsKey(primary)) continue;
                    report.Warn(string.Format(
                        "大型メッシュ '{0}' の主マテリアル『{1}』は他所で表情デカールとして{2}されるため、このメッシュ本体も同じ扱い({2})になります。表示を残したい場合はマテリアル設定で『{1}』の変換方法を個別指定してください。",
                        GetHierarchyPath(renderer.transform, root.transform), primary.name, decalOutcomeVerb));
                }
            }
            return result;
        }

        /// <summary>
        /// 表情デカール(顔のチーク・涙・アイハイライト等の透過オーバーレイ)を検出する。
        /// settings.hideExpressionOverlays が有効なとき、Mesh/SkinnedMeshRenderer の
        /// 透過(アルファブレンド)マテリアルのうち、次のいずれかに該当するものを列挙する
        /// (EditorOnlyサブツリー配下と extraExcludedRoots 配下は一貫して無視する)。
        ///   (A) 構造("structural"): スロット番号 >= 1 で、かつ同レンダラーのスロット0が不透明(Opaque)。
        ///       「不透明ベース + 透過デカールのサブメッシュ」パターン(顔メッシュのスロット1に載る
        ///       チーク/アイハイライト等。例: MAYO_Face の上の MAYO_Face_Other)を捕捉する。
        ///   (B) 名前("name"): QuestCompat.IsDecalOverlayName(マテリアル名, メインテクスチャ名)。
        ///       Front_Shadow / Alpha.png テクスチャ / blush・cheek・涙 等の名前を持つ透過マテリアルを捕捉する。
        /// 【非表示化しないもの(誤検出防止)】
        ///   ・不透明・カットアウトは対象外(透過のみ)。まぶた・目・眉などの不透明スロットは常に残る。
        ///   ・髪(QuestCompat.IsHairLikeName)は、マテリアル名だけでなくレンダラーのGameObject名・
        ///     メッシュ名のいずれかが髪を示す場合も対象外にする("alpha" 等の汎用トークンで髪のアルファが
        ///     消える事故を防ぐ)。髪メッシュ上のデカール(Front_Shadow 等)は非先頭スロットの構造ルールで捕捉する。
        ///   ・重要レンダラー(IsSignificantRenderer=大型メッシュ・髪等)のスロット0(主マテリアル)には
        ///     名前ルール(B)を適用しない。衣装・髪本体の主マテリアルが汎用トークンで誤って非表示化されるのを防ぐ
        ///     (構造ルール(A)は非先頭スロットのみなので影響しない)。
        /// 返り値はレンダラー/スロット単位の行(同一マテリアルが複数スロットに現れれば複数行)。
        /// </summary>
        private static List<DecalOverlayRow> DetectExpressionDecals(GameObject root, List<Transform> extraExcludedRoots, QuestConvertSettings settings)
        {
            var rows = new List<DecalOverlayRow>();
            if (root == null || settings == null || !settings.hideExpressionOverlays) return rows;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                if (IsUnderAny(renderer.transform, extraExcludedRoots)) continue;

                Material[] mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                // スロット0が不透明かどうか(構造ルールAの前提: 不透明ベースの上に載る透過サブメッシュ)
                bool slot0Opaque = mats[0] != null &&
                    QuestCompat.ClassifyTransparency(mats[0]) == QuestCompat.TransparencyClass.Opaque;

                // このレンダラーが「重要」(大型メッシュ・髪など)か。重要レンダラーのスロット0(主マテリアル)は
                // 衣装・髪本体である可能性が高いため、汎用トークン("alpha" 等)による名前ルールで
                // 見えているマテリアルを誤って非表示化しないよう対象外にする(構造ルールは非先頭スロットのみ)。
                bool significant = IsSignificantRenderer(renderer);
                // 髪判定はマテリアル名だけでなくレンダラー(GameObject名・メッシュ名)単位でも行い、
                // 髪ストランド本体のアルファ(テクスチャ名が汎用トークンを含む場合等)が消える事故を防ぐ。
                Mesh rendererMesh = GetRendererMesh(renderer);
                bool rendererHairLike =
                    QuestCompat.IsHairLikeName(renderer.gameObject.name) ||
                    (rendererMesh != null && QuestCompat.IsHairLikeName(rendererMesh.name));

                for (int slot = 0; slot < mats.Length; slot++)
                {
                    Material mat = mats[slot];
                    if (mat == null) continue;
                    // 不透明・カットアウトは絶対に非表示化しない(透過=アルファブレンドのみが対象)。
                    if (QuestCompat.ClassifyTransparency(mat) != QuestCompat.TransparencyClass.Transparent) continue;
                    // 髪マテリアルは非表示化しない(髪のアルファが消える事故を防ぐ)。
                    // マテリアル名・GameObject名・メッシュ名のいずれかが髪を示せば対象外。
                    if (rendererHairLike || QuestCompat.IsHairLikeName(mat.name)) continue;

                    string reason = null;
                    if (slot >= 1 && slot0Opaque)
                    {
                        // (A) 構造: 不透明ベース(スロット0)+ 非先頭スロットの透過サブメッシュ
                        reason = "structural";
                    }
                    else if (!(slot == 0 && significant))
                    {
                        // (B) 名前: デカール特有トークン(マテリアル名 or メインテクスチャ名)。
                        // ただし重要レンダラーのスロット0(=衣装/髪本体の主マテリアル)には適用しない。
                        // (汎用トークンで見えている大型メッシュを誤って非表示化しないため)
                        string mainTexName = mat.mainTexture != null ? mat.mainTexture.name : null;
                        if (QuestCompat.IsDecalOverlayName(mat.name, mainTexName)) reason = "name";
                    }
                    if (reason == null) continue;

                    rows.Add(new DecalOverlayRow
                    {
                        material = mat,
                        rendererPath = QuestCompat.GetRelativePath(root.transform, renderer.transform),
                        slotIndex = slot,
                        reason = reason,
                    });
                }
            }
            return rows;
        }

        /// <summary>
        /// 表情デカール(顔のチーク・涙・アイハイライト等の透過オーバーレイ)のうち
        /// 「Quest版で非表示化されるもの」を変換せずに検出し、UI表示用の行として返す
        /// (読み取り専用。アバターの複製・アセット書き込みは行わない)。
        /// 収集範囲は PreviewMaterials と同じ規則で、questExcludePaths 配下と
        /// (Hideモードの)全スロット透過レンダラー配下は除外してシミュレートする。
        /// settings.hideExpressionOverlays が無効なら常に空リストを返す。
        /// デカールが非表示化されるのは Hide / Opaque モードのみ(Opaque でもデカールは板を防ぐため非表示化)。
        /// Emulate モードではデカールも乗算/加算で再現され非表示化されないため、
        /// 実挙動に合わせて空リストを返す(デカールはマテリアル一覧に「半透明を再現」として現れる)。
        /// </summary>
        public static List<DecalOverlayRow> PreviewExpressionDecals(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor avatar, QuestConvertSettings settings)
        {
            if (avatar == null || settings == null || !settings.hideExpressionOverlays ||
                settings.transparentHandling == TransparentHandling.Emulate) return new List<DecalOverlayRow>();

            GameObject root = avatar.gameObject;
            List<Transform> excludedRoots = ResolveExcludedRoots(root, settings);
            if (ShouldHideFullyTransparentRenderers(settings))
            {
                excludedRoots.AddRange(SimulateFullyTransparentRendererExclusion(root, excludedRoots));
            }
            return DetectExpressionDecals(root, excludedRoots, settings);
        }

        /// <summary>レポート表示用: rootからの階層パス("Root/Armature/Hips" 形式)を返す。</summary>
        private static string GetHierarchyPath(Transform t, Transform root)
        {
            if (t == null) return "(不明)";
            var names = new List<string>();
            Transform current = t;
            while (current != null)
            {
                names.Add(current.name);
                if (current == root) break;
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        /// <summary>tがroot配下のEditorOnlyサブツリー(自身または祖先にEditorOnlyタグ)に含まれるか。rootまでで判定を打ち切る。</summary>
        private static bool IsInEditorOnlySubtree(Transform t, Transform root)
        {
            Transform current = t;
            while (current != null)
            {
                if (current.CompareTag(QuestCompat.EditorOnlyTag)) return true;
                if (current == root) break;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// 複製アバターが参照する固有マテリアルを収集する。
        /// レンダラーのsharedMaterialsに加え、到達可能な全コントローラー
        /// (デスクリプターのレイヤー / Animator / MergeAnimator等の任意コンポーネント)の全クリップの
        /// ObjectReferenceカーブが参照するマテリアル(マテリアル差し替えアニメ)も含める。
        /// さらに全コンポーネントのシリアライズ済みObjectReferenceプロパティが参照するマテリアル
        /// (Modular AvatarのMaterial Setter等)も収集する。
        /// 収集範囲は AnimationConverter.CollectControllers と共通(アニメーション変換との不整合防止)。
        /// animationUsed にはアニメーションから参照される全マテリアル(レンダラーと重複するものを含む)が入る。
        /// componentUsed にはコンポーネント参照される全マテリアル、componentSources には
        /// マテリアルごとの参照元の短い説明(「型名: GameObject名」。最初に見つかった1件)が入る。
        /// </summary>
        private static List<Material> CollectUniqueMaterials(GameObject clone, out int animationOnlyCount, out HashSet<Material> meshUsed, out HashSet<Material> particleUsed, out HashSet<Material> animationUsed, out HashSet<Material> componentUsed, out Dictionary<Material, string> componentSources)
        {
            return CollectUniqueMaterialsCore(clone, null, out animationOnlyCount, out meshUsed, out particleUsed, out animationUsed, out componentUsed, out componentSources);
        }

        /// <summary>
        /// CollectUniqueMaterials の本体。Convert(クローン)と PreviewMaterials(元アバター)で共用する。
        /// extraExcludedRoots が指定された場合、そのサブツリー配下のレンダラー・コンポーネントも収集対象外にする
        /// (プレビュー時に questExcludePaths を EditorOnly 化せずにシミュレートするため)。
        /// </summary>
        private static List<Material> CollectUniqueMaterialsCore(GameObject root, List<Transform> extraExcludedRoots, out int animationOnlyCount, out HashSet<Material> meshUsed, out HashSet<Material> particleUsed, out HashSet<Material> animationUsed, out HashSet<Material> componentUsed, out Dictionary<Material, string> componentSources)
        {
            var result = new List<Material>();
            meshUsed = new HashSet<Material>();
            particleUsed = new HashSet<Material>();
            animationUsed = new HashSet<Material>();

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                // EditorOnly(Quest除外)サブツリー配下のレンダラーはビルドから除去されるため収集しない
                if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                // プレビュー時: questExcludePaths 相当のサブツリーも収集対象外
                if (IsUnderAny(renderer.transform, extraExcludedRoots)) continue;
                bool isParticleLike = IsParticleLikeRenderer(renderer);
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    if (!result.Contains(material)) result.Add(material);
                    if (isParticleLike) particleUsed.Add(material);
                    else meshUsed.Add(material);
                }
            }

            animationOnlyCount = 0;
            var seenClips = new HashSet<AnimationClip>();
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(root))
            {
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seenClips.Add(clip)) continue;
                    foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (keys == null) continue;
                        foreach (ObjectReferenceKeyframe key in keys)
                        {
                            var material = key.value as Material;
                            if (material == null) continue;
                            animationUsed.Add(material);
                            if (!result.Contains(material))
                            {
                                result.Add(material);
                                animationOnlyCount++;
                            }
                        }
                    }
                }
            }

            // コンポーネントのシリアライズ済みMaterial参照(Modular AvatarのMaterial Setter /
            // Avatar Menu Creator等)。レンダラーのsharedMaterialsにもアニメーションカーブにも
            // 現れないため、ここで収集しないと変換対象から漏れる(目のテクスチャ消失等の原因)。
            var componentUsedLocal = new HashSet<Material>();       // outパラメータはラムダから参照できないためローカル経由
            var componentSourcesLocal = new Dictionary<Material, string>();
            SweepComponentMaterialReferences(root, extraExcludedRoots, (component, property, material) =>
            {
                componentUsedLocal.Add(material);
                if (!componentSourcesLocal.ContainsKey(material))
                {
                    componentSourcesLocal[material] = component.GetType().Name + ": " + component.gameObject.name;
                }
                if (!result.Contains(material)) result.Add(material);
                return false; // 収集のみ(参照は書き換えない)
            });
            componentUsed = componentUsedLocal;
            componentSources = componentSourcesLocal;
            return result;
        }

        /// <summary>
        /// root配下の全コンポーネント(Renderer/Transformを除く。EditorOnly・除外サブツリー配下も除く)の
        /// シリアライズ済みObjectReferenceプロパティを走査し、Material参照ごとにvisitorを呼ぶ。
        /// visitorが参照を書き換えてtrueを返した場合、そのコンポーネントに
        /// ApplyModifiedPropertiesWithoutUndo を適用する。
        /// SerializedObjectの走査に失敗したコンポーネントは警告してスキップする(走査全体を中断しない)。
        /// 走査方法は AnimationConverter のコンポーネント走査と同じ流儀(Next(true)で全深度)。
        /// </summary>
        private static void SweepComponentMaterialReferences(GameObject root, List<Transform> extraExcludedRoots, Func<Component, SerializedProperty, Material, bool> visitor)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;      // Missing Script等
                if (component is Transform) continue; // マテリアル参照を持たないため省略
                if (component is Renderer) continue;  // sharedMaterialsはレンダラー収集・差し替えで処理済み
                if (IsInEditorOnlySubtree(component.transform, root.transform)) continue;
                if (IsUnderAny(component.transform, extraExcludedRoots)) continue;

                try
                {
                    var serializedObject = new SerializedObject(component);
                    SerializedProperty property = serializedObject.GetIterator();
                    bool modified = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var material = property.objectReferenceValue as Material;
                        if (material == null) continue;
                        if (visitor(component, property, material)) modified = true;
                    }
                    if (modified)
                    {
                        serializedObject.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(string.Format(
                        "[RARA QuestConverter] コンポーネントのマテリアル参照走査に失敗したためスキップしました: {0} ({1}): {2}",
                        component.GetType().Name, component.gameObject.name, ex.Message));
                }
            }
        }

        /// <summary>
        /// クローン上の全コンポーネント(Renderer除く)のシリアライズ済みMaterial参照を
        /// 変換マップに従って差し替え、差し替えた参照数を返す。
        /// コンポーネント参照には常にメッシュ用(Default)の変換結果=materialMapを使う
        /// (particleMaterialMapは使わない。MA Material Setter等はメッシュへ適用されるため)。
        /// マップに無い参照は、既にQuest対応・TMP・Keep指定のものを除いて警告する
        /// (収集(CollectUniqueMaterialsCore)と同じ範囲を走査するため通常発生しないが、念のためのガード)。
        /// </summary>
        private static int ApplyMaterialMapToComponents(GameObject clone, Dictionary<Material, Material> materialMap, Dictionary<Material, MaterialOverrideEntry> overrides, bool atlasApplied, ConversionReport report)
        {
            int swapCount = 0;
            var swapSources = new List<string>();          // 報告用(最大10件)
            var seenSources = new HashSet<string>();       // 同一参照元の重複列挙防止
            var evaluatedUnmapped = new HashSet<Material>(); // 未変換警告はマテリアルごとに1回
            var warnedMaComponents = new HashSet<Component>(); // MA整合警告はコンポーネントごとに1回

            SweepComponentMaterialReferences(clone, null, (component, property, material) =>
            {
                Material quest;
                if (materialMap.TryGetValue(material, out quest))
                {
                    property.objectReferenceValue = quest;
                    swapCount++;
                    string source = component.GetType().Name + ": " + component.gameObject.name;
                    if (seenSources.Add(source) && swapSources.Count < 10)
                    {
                        swapSources.Add(source);
                    }
                    // アトラス統合が行われた場合、MA Material Setter / Material Swap の参照を差し替えると、
                    // Material Setter のスロット番号(MaterialIndex)や Material Swap の変換元(From)同一性が
                    // アトラス統合(スロット番号の付け替え・マテリアルの1本化)とずれ、Quest版で意図した
                    // 差し替えにならない可能性がある(通常、参照マテリアルはアトラス詰め込みから除外されるが、
                    // アトラスマテリアルを指すよう差し替わった場合は同一性が共有され得るため念のため警告する)。
                    if (atlasApplied && MACompatUtility.IsMaterialSetterOrSwap(component) && warnedMaComponents.Add(component))
                    {
                        report.Warn(string.Format(
                            "アトラス統合後にMAコンポーネント {0}({1})のマテリアル参照を差し替えました。Material Setter のスロット番号や Material Swap の変換元(From)同一性がアトラス統合とずれ、Quest版で意図した差し替えにならない可能性があります。",
                            component.GetType().Name, component.gameObject.name));
                    }
                    return true;
                }

                // マップに無い = 変換されなかったマテリアル。既にQuest対応・TMP・Keep指定は正常。
                if (!evaluatedUnmapped.Add(material)) return false;
                string shaderName = material.shader != null ? material.shader.name : string.Empty;
                bool isMobile = QuestCompat.IsMobileShader(material.shader);
                bool isTMP = shaderName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             shaderName.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0;
                MaterialOverrideEntry overrideEntry;
                bool isKeep = overrides != null && overrides.TryGetValue(material, out overrideEntry) &&
                              overrideEntry != null && overrideEntry.mode == MaterialOverride.Keep;
                if (!isMobile && !isTMP && !isKeep)
                {
                    report.Warn(string.Format(
                        "『{0}』はコンポーネント参照のみで変換対象外でした({1}: {2})。Quest版で正しく表示されない可能性があります。",
                        material.name, component.GetType().Name, component.gameObject.name));
                }
                return false;
            });

            string detail = swapSources.Count > 0
                ? " (" + string.Join(", ", swapSources) + (seenSources.Count > swapSources.Count ? ", ほか" : string.Empty) + ")"
                : string.Empty;
            report.Info(string.Format("コンポーネント参照のマテリアルを差し替え: {0}件{1}", swapCount, detail));
            return swapCount;
        }

        /// <summary>出力フォルダ配下に複製されたアニメーションクリップ数を数える(サマリー表示用)。</summary>
        private static int CountGeneratedClips(GameObject clone, string outputDir)
        {
            string prefix = outputDir + "/";
            var counted = new HashSet<AnimationClip>();
            int count = 0;
            foreach (RuntimeAnimatorController controller in AnimationConverter.CollectControllers(clone))
            {
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !counted.Add(clip)) continue;
                    string path = AssetDatabase.GetAssetPath(clip);
                    if (!string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.Ordinal)) count++;
                }
            }
            return count;
        }

        /// <summary>マテリアルをパーティクル用シェーダーへ変換すべきレンダラー種別か。</summary>
        private static bool IsParticleLikeRenderer(Renderer renderer)
        {
            return renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
        }

        /// <summary>
        /// 変換マップに従って全レンダラーのsharedMaterialsを差し替え、差し替えスロット数を返す。
        /// パーティクル系レンダラーには particleMaterialMap(パーティクル用変換結果)を優先して適用する。
        /// </summary>
        private static int ApplyMaterialMap(GameObject clone, Dictionary<Material, Material> materialMap, Dictionary<Material, Material> particleMaterialMap)
        {
            if (materialMap.Count == 0 && particleMaterialMap.Count == 0) return 0;

            int swapCount = 0;
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                // EditorOnly(Quest除外)サブツリー配下のレンダラーは差し替え対象外(マテリアル未変換のため)
                if (IsInEditorOnlySubtree(renderer.transform, clone.transform)) continue;
                bool preferParticle = IsParticleLikeRenderer(renderer);
                Material[] shared = renderer.sharedMaterials;
                var replaced = new Material[shared.Length];
                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    Material current = shared[i];
                    Material quest;
                    if (current != null &&
                        ((preferParticle && particleMaterialMap.TryGetValue(current, out quest)) ||
                         materialMap.TryGetValue(current, out quest)))
                    {
                        replaced[i] = quest;
                        changed = true;
                        swapCount++;
                    }
                    else
                    {
                        replaced[i] = current;
                    }
                }
                if (changed)
                {
                    Undo.RecordObject(renderer, UndoLabel);
                    renderer.sharedMaterials = replaced; // 配列を作り直して一括代入
                }
            }
            return swapCount;
        }

        /// <summary>削除数計測用: 配下の全コンポーネント数(Transform含む・非アクティブ含む)。</summary>
        private static int CountComponents(GameObject root)
        {
            return root.GetComponentsInChildren<Component>(true).Length;
        }

        // ================================================================
        // 変換プレビュー(読み取り専用)
        // ================================================================

        /// <summary>
        /// 変換を実行せずに、各マテリアルが設定・手動オーバーライドの下でどう扱われる予定かを
        /// 一覧行にして返す(読み取り専用。アバターの複製・アセット書き込みは一切行わない)。
        /// 収集ロジックは Convert と共通(CollectUniqueMaterialsCore)で、questExcludePaths 配下と
        /// (hideTransparentMaterials 有効時の)全スロット透過レンダラーの配下は
        /// 収集から除外してシミュレートする。エラー時は途中までの行を返す。
        /// </summary>
        public static List<MaterialPreviewRow> PreviewMaterials(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor avatar, QuestConvertSettings settings)
        {
            var rows = new List<MaterialPreviewRow>();
            try
            {
                if (avatar == null || settings == null)
                {
                    Debug.LogWarning("[RARA QuestConverter] プレビュー: アバターまたは設定がnullのため一覧を作成できません。");
                    return rows;
                }

                GameObject root = avatar.gameObject;

                // Quest除外パスを元アバター上で解決する(ConvertはクローンをEditorOnly化するが、
                // プレビューでは収集からの除外で同じ結果になるようにする)
                List<Transform> excludedRoots = ResolveExcludedRoots(root, settings);

                // Hideモード時は、Convert が step 3.6 で行う
                // 「全スロット透過レンダラーのEditorOnly化」も収集除外としてシミュレートする
                // (該当レンダラー専用のマテリアルは実変換では収集すらされないため、
                // プレビューに載せると件数・処理内容が実動作とずれる)
                if (ShouldHideFullyTransparentRenderers(settings))
                {
                    List<Transform> autoHidden = SimulateFullyTransparentRendererExclusion(root, excludedRoots);
                    excludedRoots.AddRange(autoHidden);
                }

                // 透過の不透明変換保護(Emulate=髪ストランド本体のみ / Hide=大型メッシュ・髪)の対象を
                // Convert の step 3.7 と同じ規則(CollectSuppressTransparentHideMaterials)でシミュレートする。
                // Opaque は全て不透明のため対象外。planner==executor を保つため実変換と同じメソッドを使う。
                HashSet<Material> suppressTransparentHide = ShouldCollectSuppressTransparentHide(settings)
                    ? CollectSuppressTransparentHideMaterials(root, excludedRoots, settings, null) // プレビューはレポート無し(警告は実行時に出す)
                    : new HashSet<Material>();

                // Opaqueモードでの表情デカール強制非表示を Convert(step 3.75)と同じ規則でシミュレートする。
                // これを行わないと Opaque+デカールの予定行が「不透明として変換」を表示し、
                // 実行時の強制Hide(ConvertToHidden)と食い違う(planner==executor を保つ)。
                HashSet<Material> forceHideDecals = new HashSet<Material>();
                if (settings.transparentHandling == TransparentHandling.Opaque)
                {
                    foreach (DecalOverlayRow decalRow in DetectExpressionDecals(root, excludedRoots, settings))
                    {
                        if (decalRow.material != null) forceHideDecals.Add(decalRow.material);
                    }
                }

                int animationOnlyCount;
                HashSet<Material> meshUsed, particleUsed, animationUsed, componentUsed;
                Dictionary<Material, string> componentSources;
                List<Material> materials = CollectUniqueMaterialsCore(root, excludedRoots, out animationOnlyCount, out meshUsed, out particleUsed, out animationUsed, out componentUsed, out componentSources);

                Dictionary<Material, MaterialOverrideEntry> overrides = QuestCompat.ResolveOverrides(settings);

                // 実行(BuildAtlases)と同じく、MA Material Setter / Material Swap が参照するマテリアルは
                // アトラス対象外にする(planner==executor を保つ)。MA 未導入時は空集合。
                HashSet<Material> maReferencedMaterials = MACompatUtility.CollectReferencedMaterials(root);

                foreach (Material src in materials)
                {
                    try
                    {
                        MaterialOverrideEntry overrideEntry = null;
                        if (overrides != null) overrides.TryGetValue(src, out overrideEntry);
                        MaterialOverride mode = overrideEntry != null ? overrideEntry.mode : MaterialOverride.Auto;

                        bool usedByMesh = meshUsed.Contains(src);
                        bool usedByParticle = particleUsed.Contains(src);
                        bool usedByAnimation = animationUsed.Contains(src);
                        bool usedByComponent = componentUsed.Contains(src);
                        string componentSource;
                        componentSources.TryGetValue(src, out componentSource); // 参照が無ければnull
                        // Convert と同じ規則: アニメーション・コンポーネントのみ参照のマテリアルは通常(メッシュ)扱い
                        bool effectiveMeshUse = usedByMesh || usedByComponent || !usedByParticle;

                        string shaderName = src.shader != null ? src.shader.name : string.Empty;
                        bool isMobileAlready = QuestCompat.IsMobileShader(src.shader);
                        bool isBrokenShader = src.shader == null || shaderName == "Hidden/InternalErrorShader";
                        // MaterialQuestConverter.Convert と同じTMP判定
                        bool isTMP = shaderName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     shaderName.IndexOf("TMP", StringComparison.OrdinalIgnoreCase) >= 0;
                        QuestCompat.TransparencyClass transparency = QuestCompat.ClassifyTransparency(src);

                        var row = new MaterialPreviewRow
                        {
                            material = src,
                            usedByMesh = usedByMesh,
                            usedByParticle = usedByParticle,
                            usedByAnimation = usedByAnimation,
                            usedByComponent = usedByComponent,
                            componentSource = componentSource,
                            transparency = transparency,
                            isMobileAlready = isMobileAlready,
                            isTMP = isTMP,
                            isBrokenShader = isBrokenShader,
                        };
                        bool suppressedHide = suppressTransparentHide.Contains(src);
                        row.suppressTransparentHide = suppressedHide;
                        // Auto のときのみ強制Hideされる(手動オーバーライドは Convert 側で優先される)。
                        bool forceHideDecal = mode == MaterialOverride.Auto && forceHideDecals.Contains(src);
                        row.plannedAction = BuildPlannedAction(src, settings, mode, effectiveMeshUse, usedByParticle, transparency, isMobileAlready, isTMP, isBrokenShader, suppressedHide, forceHideDecal);
                        bool usedByMA = maReferencedMaterials.Contains(src);
                        row.atlasIneligibleReason = GetAtlasIneligibleReason(root, excludedRoots, src, settings, overrideEntry, usedByMesh, usedByParticle, usedByAnimation, usedByMA, transparency, isMobileAlready, isTMP, isBrokenShader, suppressedHide);
                        row.atlasEligible = row.atlasIneligibleReason == null;
                        rows.Add(row);
                    }
                    catch (Exception rowEx)
                    {
                        Debug.LogWarning(string.Format("[RARA QuestConverter] プレビュー: マテリアル '{0}' の判定中にエラーが発生したためスキップしました: {1}", src != null ? src.name : "(null)", rowEx.Message));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RARA QuestConverter] プレビューの作成中にエラーが発生しました(途中までの一覧を返します): " + ex.Message);
            }
            return rows;
        }

        /// <summary>
        /// PhysBoneプレビュー用に、変換時にクローン上でEditorOnly化される除外サブツリーのルートを
        /// 元アバター上で解決して返す(PreviewMaterials と同じ規則)。
        /// ・questExcludePaths(手動のQuest除外)
        /// ・Hideモード時: 全スロット透過レンダラーの自動非表示(step 3.6 相当)
        /// これを ComponentRemover.PreviewPhysBoneMerge へ渡すことで、変換後は除外される
        /// PhysBoneをプレビューの現在数・予測数・行から除き、マテリアル/診断の集計と一致させる。
        /// </summary>
        public static List<Transform> ResolvePreviewExcludedRoots(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor avatar, QuestConvertSettings settings)
        {
            if (avatar == null || settings == null) return new List<Transform>();
            GameObject root = avatar.gameObject;
            List<Transform> excluded = ResolveExcludedRoots(root, settings);
            if (ShouldHideFullyTransparentRenderers(settings))
            {
                excluded.AddRange(SimulateFullyTransparentRendererExclusion(root, excluded));
            }
            return excluded;
        }

        /// <summary>questExcludePaths を root 上で解決する(ルート自身は Convert と同様に除外対象にしない)。</summary>
        private static List<Transform> ResolveExcludedRoots(GameObject root, QuestConvertSettings settings)
        {
            var excluded = new List<Transform>();
            if (settings.questExcludePaths == null) return excluded;
            foreach (string path in settings.questExcludePaths)
            {
                if (path == null) continue;
                Transform target = QuestCompat.FindByPath(root.transform, path);
                if (target != null && target != root.transform) excluded.Add(target);
            }
            return excluded;
        }

        /// <summary>
        /// HideFullyTransparentRenderers(Convert の step 3.6)が EditorOnly 化するレンダラーを、
        /// 変換せずに求める(プレビュー用の読み取り専用シミュレーション)。
        /// ガード条件(全レンダラーが透過のみ・アバタールート自身・配下に残すレンダラーを含む・
        /// 大型メッシュ/髪(IsSignificantRenderer)は対象外)も本体と同じ扱いにする。
        /// EditorOnly の代わりに excludedRoots を除外済みサブツリーとして扱う。
        /// </summary>
        private static List<Transform> SimulateFullyTransparentRendererExclusion(GameObject root, List<Transform> excludedRoots)
        {
            var result = new List<Transform>();
            var candidates = new List<Renderer>();
            bool anyRendererKept = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                if (IsUnderAny(renderer.transform, excludedRoots)) continue;
                if (!IsFullyTransparentRenderer(renderer))
                {
                    anyRendererKept = true;
                    continue;
                }
                // 本体と同じく、大型メッシュ・髪は候補にせず「残るレンダラー」として数える
                if (IsSignificantRenderer(renderer))
                {
                    anyRendererKept = true;
                    continue;
                }
                candidates.Add(renderer);
            }
            // ガード: 全レンダラーが透過のみの場合、本体は何もしない(アバターが空になるのを防ぐ)
            if (candidates.Count == 0 || !anyRendererKept) return result;

            var candidateSet = new HashSet<Renderer>(candidates);
            foreach (Renderer candidate in candidates)
            {
                // ルート自身は本体でもオブジェクト除外しない(不可視マテリアル変換で対応される)
                if (candidate.transform == root.transform) continue;

                bool containsKeptRenderer = false;
                foreach (Renderer descendant in candidate.GetComponentsInChildren<Renderer>(true))
                {
                    if (IsInEditorOnlySubtree(descendant.transform, root.transform)) continue;
                    if (IsUnderAny(descendant.transform, excludedRoots)) continue;
                    if (!candidateSet.Contains(descendant))
                    {
                        containsKeptRenderer = true;
                        break;
                    }
                }
                if (!containsKeptRenderer) result.Add(candidate.transform);
            }
            return result;
        }

        /// <summary>t が roots のいずれかの配下(自身を含む)にあるか。roots が null なら false。</summary>
        private static bool IsUnderAny(Transform t, List<Transform> roots)
        {
            if (roots == null) return false;
            foreach (Transform root in roots)
            {
                if (root != null && (t == root || t.IsChildOf(root))) return true;
            }
            return false;
        }

        /// <summary>
        /// MaterialQuestConverter.Convert のラダーを変換せずになぞり、行われる予定の処理を短い日本語で返す。
        /// ガードの順序は変換本体と同一: シェーダー破損 → Keep → Hide → 既にQuest対応 → TMP → 自動判定。
        /// (Hide指定は既にモバイルシェーダー・TMPのマテリアルでも尊重されて非表示化されるため、
        /// 既にQuest対応/TMPの判定より先に評価しないと予定表示が実動作とずれる)
        /// suppressTransparentHide は Convert の step 3.7(大型メッシュ・髪で使用される透過マテリアルの
        /// 非表示化抑制)に対応する(メッシュ用途の自動判定にのみ影響する)。
        /// </summary>
        private static string BuildPlannedAction(Material src, QuestConvertSettings settings, MaterialOverride mode, bool effectiveMeshUse, bool usedByParticle, QuestCompat.TransparencyClass transparency, bool isMobileAlready, bool isTMP, bool isBrokenShader, bool suppressTransparentHide, bool forceHideDecal)
        {
            if (isBrokenShader) return "変換しない(シェーダー破損)";

            // 用途に関わらず全体へ効く手動オーバーライド(既にQuest対応・TMPより優先)
            if (mode == MaterialOverride.Keep) return "変換しない(手動指定)";
            if (mode == MaterialOverride.Hide) return "非表示化(手動指定)";

            // Opaqueモードで表情デカールは不透明化せず強制的に非表示化される(Convert step 3.75)。
            // Auto のときのみ適用され、実行時の ConvertToHidden と一致させる。
            if (forceHideDecal) return "非表示化(表情デカール)";

            if (isMobileAlready) return "既にQuest対応";
            if (isTMP) return "TMP: 変換不可(除外推奨)";

            // 効果専用シェーダー(疑似影/アウトライン)は自動で常に非表示化される(Convert step 6.5)。
            // 手動指定は上の Keep/Hide 分岐と meshAction/particleAction 側で処理されるため、Auto のときのみ。
            if (mode == MaterialOverride.Auto)
            {
                QuestCompat.OverlayOnlyShaderKind auxKind = QuestCompat.ClassifyOverlayOnlyShader(src.shader, src.name);
                if (auxKind == QuestCompat.OverlayOnlyShaderKind.OutlineOnly) return "非表示化(アウトライン)";
                if (auxKind == QuestCompat.OverlayOnlyShaderKind.FakeShadow) return "非表示化(疑似影)";
            }

            string meshAction = effectiveMeshUse ? BuildMeshPlannedAction(src, settings, mode, transparency, suppressTransparentHide) : null;
            string particleAction = usedByParticle ? BuildParticlePlannedAction(src, mode) : null;

            if (meshAction != null && particleAction != null) return meshAction + " / パーティクル用: " + particleAction;
            if (particleAction != null) return particleAction;
            return meshAction;
        }

        /// <summary>メッシュ用途(アニメーションのみ参照を含む)の変換予定。</summary>
        private static string BuildMeshPlannedAction(Material src, QuestConvertSettings settings, MaterialOverride mode, QuestCompat.TransparencyClass transparency, bool suppressTransparentHide)
        {
            // Toon Standard / Toon Lit は透過・カットアウト非対応(不透明として変換される)
            string opaqueNote = transparency != QuestCompat.TransparencyClass.Opaque ? "・不透明として変換(透過は失われる)" : string.Empty;

            switch (mode)
            {
                case MaterialOverride.ToonStandard: return "Toon Standardへ変換(手動指定" + opaqueNote + ")";
                case MaterialOverride.ToonLit: return "Toon Litへ変換(手動指定" + opaqueNote + ")";
                case MaterialOverride.ParticleAdditive: return "パーティクル(加算)へ変換(手動指定)";
                case MaterialOverride.ParticleMultiply: return "パーティクル(乗算)へ変換(手動指定)";
            }

            // Auto: MaterialQuestConverter のラダー(透過の扱い → lilToon → 汎用)をなぞる
            if (transparency == QuestCompat.TransparencyClass.Transparent)
            {
                // 保護対象の透過マテリアルは再現・非表示化が抑制され、不透明として変換される。
                // Emulate は髪ストランド本体のみ保護(オーバーレイ名は再現へ回る)、Hide は大型メッシュ・髪を保護。
                if (suppressTransparentHide)
                {
                    return settings.transparentHandling == TransparentHandling.Emulate
                        ? "不透明として変換(髪ストランドのため半透明再現の対象外)"
                        : "不透明として変換(大型メッシュ/髪のため非表示化しない)";
                }
                switch (settings.transparentHandling)
                {
                    case TransparentHandling.Emulate: return "半透明を乗算/加算で再現(自動判定)";
                    case TransparentHandling.Hide: return "非表示化";
                    // Opaque は下の lilToon/汎用パスへ落ちて「不透明として変換・透過は失われる」を表示。
                    // 表情デカールは呼び出し元(BuildPlannedAction)で forceHideDecal により
                    // 先に「非表示化(表情デカール)」へ振り分けられるため、ここへは通常の透過のみ落ちる。
                }
            }
            if (QuestCompat.IsLilToonShader(src.shader))
            {
                string target = settings.shaderTarget == QuestShaderTarget.ToonStandard ? "Toon Standardへ変換" : "Toon Litへ変換";
                return transparency != QuestCompat.TransparencyClass.Opaque
                    ? target + "(不透明として変換・透過は失われる)"
                    : target;
            }

            // 非lilToon汎用: パーティクル系シェーダー名なら近似差し替え、それ以外はベイク+Toon Lit
            string shaderName = src.shader != null ? src.shader.name : string.Empty;
            if (shaderName.IndexOf("particle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (shaderName.IndexOf("add", StringComparison.OrdinalIgnoreCase) >= 0) return "パーティクル(加算)へ変換";
                if (shaderName.IndexOf("multiply", StringComparison.OrdinalIgnoreCase) >= 0) return "パーティクル(乗算)へ変換";
            }
            return transparency != QuestCompat.TransparencyClass.Opaque
                ? "Toon Litへ変換(近似・不透明として変換・透過は失われる)"
                : "Toon Litへ変換(近似)";
        }

        /// <summary>パーティクル系レンダラー用途の変換予定。</summary>
        private static string BuildParticlePlannedAction(Material src, MaterialOverride mode)
        {
            switch (mode)
            {
                case MaterialOverride.ParticleAdditive: return "パーティクル(加算)へ変換(手動指定)";
                case MaterialOverride.ParticleMultiply: return "パーティクル(乗算)へ変換(手動指定)";
                // ToonStandard/ToonLit の強制指定はそのまま変換器へ渡され、ラダーが最終判断する
                case MaterialOverride.ToonStandard: return "Toon Standardへ変換(手動指定・パーティクル用途)";
                case MaterialOverride.ToonLit: return "Toon Litへ変換(手動指定・パーティクル用途)";
            }
            return IsMultiplyParticleMaterial(src) ? "パーティクル(乗算)へ変換" : "パーティクル(加算)へ変換";
        }

        /// <summary>MaterialQuestConverter.ConvertParticle と同じ乗算ブレンド判定。</summary>
        private static bool IsMultiplyParticleMaterial(Material src)
        {
            string shaderName = src.shader != null ? src.shader.name : string.Empty;
            return shaderName.IndexOf("multiply", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (src.HasProperty("_DstBlend") && Mathf.RoundToInt(src.GetFloat("_DstBlend")) == (int)UnityEngine.Rendering.BlendMode.SrcColor);
        }

        /// <summary>
        /// MaterialAtlasser.BuildAtlases の候補判定を(アトラスを生成せずに)なぞり、
        /// アトラス対象外の理由を返す(対象になり得るなら null)。
        /// suppressTransparentHide が true の透過マテリアルは非表示化されず Toon Standard/Toon Lit へ
        /// 変換されるため、アトラス候補になり得る(実変換のアトラス判定と一致させる)。
        /// </summary>
        private static string GetAtlasIneligibleReason(GameObject root, List<Transform> excludedRoots, Material src, QuestConvertSettings settings, MaterialOverrideEntry overrideEntry, bool usedByMesh, bool usedByParticle, bool usedByAnimation, bool usedByMA, QuestCompat.TransparencyClass transparency, bool isMobileAlready, bool isTMP, bool isBrokenShader, bool suppressTransparentHide)
        {
            MaterialOverride mode = overrideEntry != null ? overrideEntry.mode : MaterialOverride.Auto;

            // 変換されない(materialMapに乗らない)マテリアルはアトラスの対象にならない
            if (isMobileAlready || isTMP || isBrokenShader || mode == MaterialOverride.Keep) return "変換対象外";
            if (mode == MaterialOverride.Hide) return "非表示化されるため";
            if (mode == MaterialOverride.ParticleAdditive || mode == MaterialOverride.ParticleMultiply) return "パーティクル用";

            // 効果専用シェーダー(疑似影/アウトライン)は Auto で常に非表示化され(Convert step 6.5)、
            // 変換後シェーダーが乗算(Toon Standard/Lit ではない)ためアトラス対象から外れる
            // (MaterialAtlasser.BuildAtlases が黙って除外する)。予定表示と実動作を一致させる。
            if (mode == MaterialOverride.Auto && QuestCompat.IsOverlayOnlyShader(src.shader, src.name)) return "非表示化されるため(効果専用シェーダー)";

            if (usedByAnimation) return "アニメで差し替えあり";
            if (usedByMA) return "MAマテリアル設定が参照するため";
            if (usedByParticle && !usedByMesh) return "パーティクル用";
            // コンポーネント参照のみ等、どのメッシュスロットにも設定されていないマテリアルは
            // MaterialAtlasser.BuildAtlases が「メッシュで使用されていないため」として除外する
            if (!usedByMesh && !usedByParticle) return "メッシュで使用されていないため";
            // 透過(アルファブレンド)マテリアルは扱いに応じてアトラス対象外になる:
            //   Emulate → 乗算/加算のパーティクルシェーダーへ(不透明アトラスに載らない)
            //   Hide    → 非表示化される
            //   Opaque  → 不透明Toonへ変換されるためアトラス候補になり得る(除外しない)
            // 大型メッシュ・髪で抑制される透過は不透明化されるためアトラス候補になり得る(除外しない)。
            if (transparency == QuestCompat.TransparencyClass.Transparent && !suppressTransparentHide)
            {
                if (settings.transparentHandling == TransparentHandling.Hide) return "非表示化されるため";
                if (settings.transparentHandling == TransparentHandling.Emulate) return "乗算/加算で半透明を再現するため";
            }
            if (overrideEntry != null && overrideEntry.excludeFromAtlas) return "手動で除外";
            if (!AreMaterialUvsInUnitRange(root, excludedRoots, src)) return "UVが0..1範囲外";
            return null;
        }

        /// <summary>
        /// このマテリアルを使う全 Mesh/SkinnedMeshRenderer のサブメッシュUV(UV0)が 0..1 に収まっているか。
        /// タイリング・オフセットが恒等でない場合も実効UVがセル外へはみ出すため範囲外扱い。
        /// メッシュが読み取れない場合はアトラスの再配置ができないため範囲外扱いにする。
        /// </summary>
        private static bool AreMaterialUvsInUnitRange(GameObject root, List<Transform> excludedRoots, Material src)
        {
            const float epsilon = 0.001f;

            if (src.HasProperty("_MainTex"))
            {
                Vector2 scale = src.GetTextureScale("_MainTex");
                Vector2 offset = src.GetTextureOffset("_MainTex");
                if (Mathf.Abs(scale.x - 1f) > epsilon || Mathf.Abs(scale.y - 1f) > epsilon ||
                    Mathf.Abs(offset.x) > epsilon || Mathf.Abs(offset.y) > epsilon)
                {
                    return false;
                }
            }

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                if (IsInEditorOnlySubtree(renderer.transform, root.transform)) continue;
                if (IsUnderAny(renderer.transform, excludedRoots)) continue;

                Mesh mesh = GetRendererMesh(renderer);
                if (mesh == null || mesh.subMeshCount == 0) continue;

                Material[] shared = renderer.sharedMaterials;
                for (int slot = 0; slot < shared.Length; slot++)
                {
                    if (shared[slot] != src) continue;
                    try
                    {
                        Vector2[] uvs = mesh.uv;
                        if (uvs == null || uvs.Length == 0) continue; // UVなし → (0,0)固定サンプリング = 範囲内
                        // スロット数がサブメッシュ数を超える場合、超過スロットは最後のサブメッシュを再描画する
                        int subMesh = Mathf.Min(slot, mesh.subMeshCount - 1);
                        foreach (int index in mesh.GetIndices(subMesh))
                        {
                            if (index < 0 || index >= uvs.Length) continue;
                            Vector2 uv = uvs[index];
                            if (uv.x < -epsilon || uv.x > 1f + epsilon || uv.y < -epsilon || uv.y > 1f + epsilon)
                            {
                                return false;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        return false; // メッシュ読み取り不可(Read/Write無効等)
                    }
                }
            }
            return true;
        }

        /// <summary>レンダラーが参照する共有メッシュを返す(SkinnedMeshRenderer / MeshFilter)。</summary>
        private static Mesh GetRendererMesh(Renderer renderer)
        {
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // ================================================================
        // ポリゴン削減(メッシュ簡略化) step 3.8
        // ================================================================

        /// <summary>
        /// 配分計画(settings.decimationPlan)に従って複製アバターの各レンダラーのメッシュを簡略化し、
        /// 生成メッシュを安定パスへ保存してレンダラーへ差し替える(元アバターは触らない)。
        /// ・計画のパスは複製上で解決する。見つからない/対象外はスキップして警告する。
        /// ・EditorOnly化済み(Quest除外・トグル非表示・透過非表示)のレンダラーは絶対に削減しない(警告してスキップ)。
        /// ・現在の三角形数が目標以下のレンダラーは削減不要としてスキップ(無警告)。
        /// ・保存名は {元メッシュ名}_QuestDecim_{計画ハッシュ8桁} とし、計画が変わると別アセット(別GUID)になる。
        ///   これにより計画変更時も古い _Quest クローンの参照メッシュを上書きで壊さない(アトラスのlayoutHashと同じ狙い)。
        /// 実際のメッシュ簡略化(QEMエッジcollapse・部分集合化)は MeshDecimatorUnity.Decimate に委ねる。
        /// </summary>
        private static void ApplyPolygonDecimation(GameObject clone, QuestConvertSettings settings, string outputDir, ConversionAssetContext assets, ConversionReport report)
        {
            if (settings.decimationPlan == null || settings.decimationPlan.Count == 0) return;

            string planHash = ComputeDecimationPlanHash(settings.decimationPlan);
            string meshFolder = outputDir + "/Meshes";
            QuestConverterUtility.EnsureFolder(meshFolder);

            int reducedCount = 0;
            foreach (PolygonPlanEntryData entry in settings.decimationPlan)
            {
                if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) continue;
                if (entry.targetTris < 1) continue;

                Transform target = QuestCompat.FindByPath(clone.transform, entry.rendererPath);
                if (target == null)
                {
                    report.Warn($"ポリゴン削減: レンダラーが複製内に見つかりません(スキップ): {entry.rendererPath}");
                    continue;
                }
                Renderer renderer = target.GetComponent<Renderer>();
                if (renderer == null || (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)))
                {
                    report.Warn($"ポリゴン削減: 対象がスキンメッシュ/メッシュレンダラーではありません(スキップ): {entry.rendererPath}");
                    continue;
                }
                // EditorOnly化済み(Quest除外・トグル非表示・透過非表示)のレンダラーは絶対に削減しない。
                if (IsInEditorOnlySubtree(renderer.transform, clone.transform))
                {
                    report.Warn($"ポリゴン削減: 除外(EditorOnly)のレンダラーのためスキップ: {entry.rendererPath}");
                    continue;
                }
                // Unity Cloth は SkinnedMeshRenderer と同じGameObjectに付き、その coefficients(ClothSkinningCoefficient[])
                // は元メッシュの頂点数に束縛される。ここでメッシュを差し替えると係数配列が新頂点数と一致せず
                // シミュレーションが壊れる(Unityコンソールに coefficients 不一致エラー)。しかも Cloth を除去する
                // ComponentRemover は QuestConvert かつ removeUnsupportedComponents 時の後段(step7)でしか走らないため、
                // ConsolidateOnly では Cloth が残り続け不整合が永続化する。Cloth 付きレンダラーは削減対象から除外する。
                if (renderer.GetComponent<Cloth>() != null)
                {
                    report.Warn($"ポリゴン削減: Cloth付きのため頂点数不整合を避けてスキップ: {entry.rendererPath}");
                    continue;
                }

                Mesh mesh = GetRendererMesh(renderer);
                if (mesh == null)
                {
                    report.Warn($"ポリゴン削減: メッシュが見つかりません(スキップ): {entry.rendererPath}");
                    continue;
                }
                int before = GetMeshTriangleCount(mesh);
                if (before <= entry.targetTris) continue; // 既に目標以下 → 削減不要(無警告)

                Mesh reduced = MeshDecimatorUnity.Decimate(mesh, entry.targetTris, null, report);
                if (reduced == null)
                {
                    report.Warn($"ポリゴン削減: 簡略化に失敗しました(スキップ): {entry.rendererPath}");
                    continue;
                }
                reduced.name = mesh.name + "_QuestDecim_" + planHash;

                // 実行間で安定したパスへ GUID を保持したまま上書き保存(アトラスと同じ保存イディオム)。
                string path = assets.Claim(meshFolder + "/" + QuestConverterUtility.SanitizeAssetName(reduced.name) + ".asset");
                Mesh savedMesh = QuestAssetPersistence.SaveOrOverwriteMesh(reduced, path);
                // 既存アセットへ上書きした場合、メモリ上の一時メッシュは不要(未アセットのみ破棄する)。
                if (savedMesh != null && !ReferenceEquals(savedMesh, reduced) && reduced != null && !AssetDatabase.Contains(reduced))
                {
                    UnityEngine.Object.DestroyImmediate(reduced);
                }
                if (savedMesh == null) savedMesh = reduced;

                AssignRendererMesh(renderer, savedMesh);
                int after = GetMeshTriangleCount(savedMesh);
                report.Info($"ポリゴン削減: {entry.rendererPath} {before}→{after}(メッシュ: {path})");
                reducedCount++;
            }

            if (reducedCount == 0)
            {
                report.Info("ポリゴン削減: 削減されたレンダラーはありません(計画が空、または全て現在数が目標以下でした)。");
            }
        }

        /// <summary>
        /// レンダラーの共有メッシュだけを差し替える(SkinnedMeshRenderer / MeshFilter)。マテリアルは触らない。
        /// バウンズはジオメトリ不変(簡略化メッシュは元バウンズを引き継ぐ)ため localBounds は触らない。
        /// </summary>
        private static void AssignRendererMesh(Renderer renderer, Mesh newMesh)
        {
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null)
            {
                Undo.RecordObject(smr, UndoLabel);
                smr.sharedMesh = newMesh;
                return;
            }
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null)
            {
                Undo.RecordObject(filter, UndoLabel);
                filter.sharedMesh = newMesh;
            }
        }

        /// <summary>
        /// 配分計画全体の決定的ハッシュ(8桁hex)。エントリを (rendererPath, targetTris) で安定ソートし、
        /// FNV-1a 32bit で畳み込む。計画が1つでも変わると別ハッシュ=別アセット名になり、
        /// 計画変更時も古い _Quest クローンが参照する簡略化メッシュを上書きで壊さない
        /// (MaterialAtlasser の layoutHash と同じ狙い)。
        /// </summary>
        private static string ComputeDecimationPlanHash(List<PolygonPlanEntryData> plan)
        {
            var ordered = new List<PolygonPlanEntryData>();
            foreach (PolygonPlanEntryData e in plan)
            {
                if (e == null || string.IsNullOrEmpty(e.rendererPath)) continue;
                ordered.Add(e);
            }
            ordered.Sort((a, b) =>
            {
                int byPath = string.CompareOrdinal(a.rendererPath, b.rendererPath);
                if (byPath != 0) return byPath;
                return a.targetTris.CompareTo(b.targetTris);
            });

            uint hash = 2166136261u; // FNV-1a 32bit
            foreach (PolygonPlanEntryData e in ordered)
            {
                foreach (char c in e.rendererPath) hash = (hash ^ c) * 16777619u;
                hash = (hash ^ '|') * 16777619u;
                uint t = unchecked((uint)e.targetTris);
                for (int i = 0; i < 4; i++)
                {
                    hash = (hash ^ (t & 0xFFu)) * 16777619u;
                    t >>= 8;
                }
                hash = (hash ^ '\n') * 16777619u; // エントリ区切り(連結の曖昧さ回避)
            }
            return hash.ToString("x8");
        }
    }
}
#endif
