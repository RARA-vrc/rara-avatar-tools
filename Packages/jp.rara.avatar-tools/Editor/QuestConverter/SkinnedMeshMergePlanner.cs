// RARA Quest Converter / PC軽量化ツール 共有 - SkinnedMesh統合プランナー
// 複製アバターの SkinnedMeshRenderer を「顔(分離維持)」「統合対象」「除外(理由あり)」へ分類し、
// AAO MergeSkinnedMesh を1つのターゲットへ集約するための計画(SkinnedMeshMergePlan)を作る。
//
// 【方針(研究の結論)】
//  ・顔(ビセーム/まばたき)メッシュは分離を維持する。VRCAvatarDescriptor が VisemeSkinnedMesh /
//    eyelidsSkinnedMesh を PreserveProperties(ミューテーション)で登録しているだけでは AAO の自動統合から
//    除外されないため、明示的に分離対象として扱い、統合ソースから外す(実行側で AAO の除外にも登録する)。
//  ・それ以外の SkinnedMeshRenderer は原則すべて1つへ統合する(ユーザー方針: 表情以外の
//    表示/非表示トグルはもう不要 = 常時表示ロック)。ただし Cloth(布シミュレーション)・EditorOnly
//    (ビルド除外)・ユーザーが個別除外したものは統合しない(理由を提示する)。
//
// このファイルは Assembly-CSharp-Editor(asmdef無し)でコンパイルされ、RARA.QuestConverter と
// RARA.PCOptimizer の両ツールから参照される(同一アセンブリ)。AAO 型は一切参照しない(実行は
// AAOMeshMergeHelper がリフレクションで行う)。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace RARA.QuestConverter
{
    /// <summary>
    /// SkinnedMesh統合モード。JsonUtility では int としてシリアライズされる。
    /// 【移行】保存済みJSONにキーが無い(旧ユーザー)場合の既定は None(=0)。
    /// 新規設定の既定は各ウィンドウの LoadSettings 移行ガードで MergeExceptFace に設定する。
    /// </summary>
    public enum SkinnedMeshMergeMode
    {
        /// <summary>統合しない(従来どおり。SMR数・スロット数は削減されない)。</summary>
        None = 0,

        /// <summary>顔(ビセーム/まばたき)以外の全 SkinnedMeshRenderer を1つへ統合する(推奨)。</summary>
        MergeExceptFace = 1,
    }

    /// <summary>SkinnedMesh統合プレビューの1行(レンダラー1件の扱い)。</summary>
    public class SkinnedMeshMergeRow
    {
        /// <summary>アバタールート相対のレンダラーパス(QuestCompat.GetRelativePath 準拠)。</summary>
        public string rendererPath;

        /// <summary>レンダラーの GameObject 名(UI表示用)。</summary>
        public string rendererName;

        /// <summary>このレンダラーを統合ターゲットへまとめるか(true=統合 / false=分離維持)。</summary>
        public bool willMerge;

        /// <summary>統合する/しない の理由(日本語。UI表示用)。</summary>
        public string reason;

        /// <summary>顔(ビセーム/まばたき)メッシュか(UI強調・カウント用)。</summary>
        public bool isFace;

        /// <summary>EditorOnly サブツリー(ビルドで除去)か。最終カウントから除外する。</summary>
        public bool isEditorOnly;

        /// <summary>ブレンドシェイプ数(UI表示用)。</summary>
        public int blendShapeCount;
    }

    /// <summary>
    /// SkinnedMesh統合計画。プレビュー表示にも実行(AAOMeshMergeHelper)にも使う。
    /// </summary>
    public class SkinnedMeshMergePlan
    {
        /// <summary>レンダラーごとの扱い(顔・統合・除外)。UIのプレビュー表に使う。</summary>
        public List<SkinnedMeshMergeRow> rows = new List<SkinnedMeshMergeRow>();

        /// <summary>統合ソースにするレンダラーのパス(willMerge==true のもの)。</summary>
        public List<string> mergeSourcePaths = new List<string>();

        /// <summary>分離維持する顔レンダラーのパス(AAO の自動統合からも除外させる対象)。</summary>
        public List<string> faceRendererPaths = new List<string>();

        /// <summary>統合前の(ビルドに残る=EditorOnlyでない)SkinnedMeshRenderer 数。</summary>
        public int beforeCount;

        /// <summary>統合後に期待される SkinnedMeshRenderer 数。</summary>
        public int expectedCount;

        /// <summary>実際に統合が行われるか(統合ソースが2件以上のときのみ true)。</summary>
        public bool WillMergeAnything { get { return mergeSourcePaths.Count >= 2; } }
    }

    /// <summary>
    /// 複製アバターの SkinnedMeshRenderer を分類し、SkinnedMesh統合計画を作る(読み取り専用)。
    /// QuestConverter・PCOptimizer の両ツールから使う共有プランナー。
    /// </summary>
    public static class SkinnedMeshMergePlanner
    {
        /// <summary>
        /// avatarRoot 配下の各 SkinnedMeshRenderer を分類して統合計画を返す。
        /// mode==None のときは全行を「統合しない」で返す(実行側もスキップする)。
        /// optOutPaths はユーザーが個別に統合から除外したレンダラーの相対パス集合(null可)。
        /// </summary>
        public static SkinnedMeshMergePlan BuildPlan(GameObject avatarRoot, SkinnedMeshMergeMode mode, IEnumerable<string> optOutPaths)
        {
            var plan = new SkinnedMeshMergePlan();
            if (avatarRoot == null) return plan;

            var optOut = new HashSet<string>();
            if (optOutPaths != null)
            {
                foreach (string p in optOutPaths)
                {
                    if (!string.IsNullOrEmpty(p)) optOut.Add(p);
                }
            }

            HashSet<SkinnedMeshRenderer> faceRenderers = CollectFaceRenderers(avatarRoot);

            SkinnedMeshRenderer[] renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer smr in renderers)
            {
                if (smr == null) continue;
                string path = QuestCompat.GetRelativePath(avatarRoot.transform, smr.transform);
                if (path == null) continue; // avatarRoot 配下でない(通常ありえない)

                bool editorOnly = QuestCompat.IsEditorOnly(smr.transform);
                bool isFace = faceRenderers.Contains(smr);
                Mesh mesh = smr.sharedMesh;

                var row = new SkinnedMeshMergeRow
                {
                    rendererPath = path,
                    rendererName = smr.gameObject.name,
                    isFace = isFace,
                    isEditorOnly = editorOnly,
                    blendShapeCount = mesh != null ? mesh.blendShapeCount : 0,
                };

                if (mode == SkinnedMeshMergeMode.None)
                {
                    row.willMerge = false;
                    row.reason = "統合しない(SkinnedMesh統合が無効)";
                }
                else if (isFace)
                {
                    row.willMerge = false;
                    row.reason = "顔メッシュ(ビセーム/まばたき)のため分離を維持(表情を保持)";
                }
                else if (editorOnly)
                {
                    row.willMerge = false;
                    row.reason = "EditorOnly(ビルド除外)のため統合対象外";
                }
                else if (mesh == null)
                {
                    row.willMerge = false;
                    row.reason = "メッシュが未設定のため統合対象外";
                }
                else if (HasCloth(smr))
                {
                    row.willMerge = false;
                    row.reason = "Clothコンポーネントがあるため統合対象外(布シミュレーションを保持)";
                }
                else if (optOut.Contains(path))
                {
                    row.willMerge = false;
                    row.reason = "ユーザーが統合対象から除外";
                }
                else
                {
                    row.willMerge = true;
                    row.reason = "他メッシュと1つに統合";
                }

                if (isFace) plan.faceRendererPaths.Add(path);
                if (row.willMerge) plan.mergeSourcePaths.Add(path);
                plan.rows.Add(row);
            }

            // 統合前(ビルドに残る)SMR数と、統合後の期待SMR数を算出する。
            int survivingSeparate = 0; // 統合されず、かつビルドに残る(EditorOnlyでない)レンダラー
            int surviving = 0;         // ビルドに残る(EditorOnlyでない)レンダラー総数
            foreach (SkinnedMeshMergeRow row in plan.rows)
            {
                if (row.isEditorOnly) continue;
                surviving++;
                if (!row.willMerge) survivingSeparate++;
            }
            plan.beforeCount = surviving;

            int mergeCount = plan.mergeSourcePaths.Count;
            int mergedResult = mergeCount >= 2 ? 1 : mergeCount; // ソース1件は統合しない(そのまま残る)
            plan.expectedCount = survivingSeparate + mergedResult;

            return plan;
        }

        /// <summary>
        /// VRCAvatarDescriptor が参照する顔レンダラー(リップシンクの口メッシュ・まばたき用メッシュ)を集める。
        /// これらは分離を維持し、統合ソースからも AAO の自動統合からも外す。
        /// </summary>
        private static HashSet<SkinnedMeshRenderer> CollectFaceRenderers(GameObject avatarRoot)
        {
            var set = new HashSet<SkinnedMeshRenderer>();
            VRCAvatarDescriptor descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor == null) return set;

            // リップシンク(ビセーム/あごブレンドシェイプ)用の口メッシュ
            if ((descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape ||
                 descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape) &&
                descriptor.VisemeSkinnedMesh != null)
            {
                set.Add(descriptor.VisemeSkinnedMesh);
            }

            // アイトラッキングのまばたき(ブレンドシェイプ方式)用メッシュ
            if (descriptor.enableEyeLook)
            {
                VRCAvatarDescriptor.CustomEyeLookSettings eye = descriptor.customEyeLookSettings;
                if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes &&
                    eye.eyelidsSkinnedMesh != null)
                {
                    set.Add(eye.eyelidsSkinnedMesh);
                }
            }

            return set;
        }

        /// <summary>この SkinnedMeshRenderer と同じ GameObject に Unity Cloth が付いているか。</summary>
        private static bool HasCloth(SkinnedMeshRenderer smr)
        {
            if (smr == null) return false;
            return smr.GetComponent<Cloth>() != null;
        }
    }
}
#endif
