// RARA Quest Converter - メッシュ削減(ポリゴンデシメーション)Unityアダプタ
// VRChat Avatars SDK 3.10.4 / Unity 2022.3 向け。Assembly-CSharp-Editor でコンパイルされる(asmdefなし)。
//
// このファイルは純粋C#のQEMデシメータ(MeshDecimatorCore.cs / QemDecimator)と
// UnityのMesh APIを橋渡しするエディタ専用アダプタである。役割:
//   1. Unity Mesh → DecimationMesh(頂点座標・サブメッシュ三角形・シーム判定・保護度)を構築する。
//   2. QemDecimator.Decimate を呼び出して「残す頂点(元頂点のサブセット)」を得る。
//   3. DecimationResult.keptVertices を使って全頂点チャンネルをサブセットし直した新しいMeshを作る。
//
// 設計上の要点(ピン留めアーキテクチャに準拠):
//   - デシメータはエンドポイント配置のみ(新座標を合成しない)。残る頂点は必ず元頂点の部分集合なので、
//     UV・法線・接線・頂点カラー・ボーンウェイト・ブレンドシェイプのデルタはすべて「間引くだけ」で正しく保たれる。
//   - シーム頂点(UV継ぎ目/開放境界)は追加コストで保護する。
//   - ブレンドシェイプが大きく動く頂点(口・目など表情領域)は自動的に保護度を上げる。
//   - サブメッシュ(マテリアルスロット)の個数と順序は必ず維持する。三角形が0になっても空スロットとして残す。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RARA.QuestConverter.Decimation
{
    /// <summary>
    /// Unity Mesh に対してポリゴン削減(QEMエッジ収縮・エンドポイント配置)を適用するアダプタ。
    /// </summary>
    public static class MeshDecimatorUnity
    {
        // 座標の溶接(同一位置判定)量子化グリッド。1e-5 = 0.01mm 相当。
        private const float PositionQuantum = 1e-5f;
        // UV継ぎ目判定でUVの差を丸める量子化。
        private const float UvQuantum = 1e-5f;
        // 法線の差を丸める量子化(法線は単位長のため 1e-4 で十分)。
        private const float NormalQuantum = 1e-4f;
        // ブレンドシェイプ活動量の正規化上限(保護度は最大0.9まで。1.0=完全ロックは避ける)。
        private const float BlendShapeProtectionMax = 0.9f;

        /// <summary>
        /// メッシュをおおよそ targetTriangles 三角形まで削減した新しいメッシュを返す。
        /// </summary>
        /// <param name="src">元メッシュ(変更しない。読み取りのみ)。</param>
        /// <param name="targetTriangles">目標三角形数(メッシュ全体。近似的に到達する)。</param>
        /// <param name="extraVertexProtection">
        /// 追加の頂点保護度(0..1、length V、nullなら未指定)。カテゴリ床(顔・髪など)を
        /// 呼び出し側で頂点単位に展開して渡す想定。ブレンドシェイプ活動量とのmaxを取る。
        /// </param>
        /// <param name="report">変換レポート(警告・情報の記録先)。</param>
        /// <returns>
        /// 削減後の新しい未保存メッシュ。削減不要・不能・効果なしの場合は null(呼び出し側は元メッシュを維持する)。
        /// 名前は src.name のまま(削減パラメータの接尾辞付けは呼び出し側が行う)。
        /// </returns>
        public static Mesh Decimate(Mesh src, int targetTriangles, float[] extraVertexProtection, ConversionReport report)
        {
            if (src == null) return null;
            int vertexCount = src.vertexCount;
            if (vertexCount == 0) return null;

            int subMeshCount = src.subMeshCount;
            if (subMeshCount == 0) return null;

            int currentTriangles = TriangleCount(src);
            if (currentTriangles <= 0) return null;

            // 目標が現在数以上なら削減不要。
            if (targetTriangles <= 0 || targetTriangles >= currentTriangles)
            {
                return null;
            }

            try
            {
                // ---- トポロジー確認(QEMコアは三角形のみを扱う) ----
                for (int s = 0; s < subMeshCount; s++)
                {
                    if (src.GetTopology(s) != MeshTopology.Triangles)
                    {
                        report.Warn(string.Format(
                            "ポリゴン削減: メッシュ '{0}' は三角形以外のトポロジー(サブメッシュ{1})を含むためスキップします。",
                            src.name, s));
                        return null;
                    }
                }

                // ---- 入力チャンネルを読み出す ----
                var positions = new List<Vector3>(vertexCount);
                src.GetVertices(positions);
                if (positions.Count != vertexCount)
                {
                    report.Warn(string.Format(
                        "ポリゴン削減: メッシュ '{0}' の頂点を読み取れなかったためスキップします。", src.name));
                    return null;
                }

                // xyzインターリーブの座標配列(コア入力)。
                var posFlat = new float[vertexCount * 3];
                for (int v = 0; v < vertexCount; v++)
                {
                    Vector3 p = positions[v];
                    posFlat[3 * v + 0] = p.x;
                    posFlat[3 * v + 1] = p.y;
                    posFlat[3 * v + 2] = p.z;
                }

                // サブメッシュ三角形(元頂点インデックス)。
                var submeshIndices = new int[subMeshCount][];
                for (int s = 0; s < subMeshCount; s++)
                {
                    submeshIndices[s] = src.GetTriangles(s);
                }

                // シーム頂点(UV継ぎ目・材質境界に相当する位置一致差異、および開放境界)。
                // あわせて溶接グループ(同一位置頂点の連結収縮単位)も得る。
                bool[] seamVertex = ComputeSeamVertices(src, positions, submeshIndices, vertexCount,
                    out int[] vertexWeldGroup);

                // 頂点保護度(カテゴリ床 と ブレンドシェイプ活動量 の大きい方)。
                float[] vertexProtection = ComputeVertexProtection(src, extraVertexProtection, vertexCount);

                // UV0・法線をフラット配列で読み出す(溶接グループ内の一致先選択と
                // シームコストの発散スケーリングにコアが使用する。無い場合は null)。
                float[] uv0Flat = ReadUv0Flat(src, vertexCount);
                float[] normalsFlat = ReadNormalsFlat(src, vertexCount);

                // ---- コアで削減計画(残す頂点の部分集合)を得る ----
                var decimationMesh = new DecimationMesh
                {
                    positions = posFlat,
                    submeshIndices = submeshIndices,
                    vertexProtection = vertexProtection,
                    seamVertex = seamVertex,
                    vertexWeldGroup = vertexWeldGroup,
                    uv0 = uv0Flat,
                    normals = normalsFlat,
                };
                var options = new DecimationOptions();
                DecimationResult result = QemDecimator.Decimate(decimationMesh, targetTriangles, options);

                if (result == null || result.keptVertices == null || result.keptVertices.Length == 0)
                {
                    report.Warn(string.Format(
                        "ポリゴン削減: メッシュ '{0}' の削減結果が空になったためスキップします。", src.name));
                    return null;
                }

                // 効果なし(結果が現在と同数以上)なら新規生成せず維持。
                if (result.triangleCount >= currentTriangles)
                {
                    report.Info(string.Format(
                        "ポリゴン削減: メッシュ '{0}' はこれ以上安全に削減できませんでした(保護のため維持)。", src.name));
                    return null;
                }

                // ---- 結果を全チャンネルサブセットで新メッシュへ適用 ----
                return BuildSubsetMesh(src, result, vertexCount, subMeshCount, positions, report);
            }
            catch (Exception ex)
            {
                report.Warn(string.Format(
                    "ポリゴン削減: メッシュ '{0}' の処理に失敗したためスキップします({1})。", src.name, ex.Message));
                return null;
            }
        }

        // ================================================================
        // 入力構築: シーム頂点判定
        // ================================================================

        /// <summary>
        /// シーム頂点(削減時に強く保護すべき頂点)を判定する。
        ///   (a) 同一位置(量子化)を共有しつつ UV0 か 法線 が異なる頂点 → UV継ぎ目/法線硬エッジ。
        ///   (b) 溶接後のエッジで隣接三角形が1枚しかない辺の端点 → 開放境界(穴・メッシュの縁)。
        /// (a)(b)いずれかに該当する頂点を true にする。
        /// </summary>
        private static bool[] ComputeSeamVertices(Mesh src, List<Vector3> positions, int[][] submeshIndices, int vertexCount,
            out int[] vertexWeldGroup)
        {
            var seam = new bool[vertexCount];

            // 位置を量子化して溶接IDを割り当てる。
            var weldId = new Dictionary<(long, long, long), int>(vertexCount);
            var weldOf = new int[vertexCount];
            var membersByWeld = new Dictionary<int, List<int>>();
            for (int v = 0; v < vertexCount; v++)
            {
                (long, long, long) key = QuantizeVec3(positions[v], PositionQuantum);
                int id;
                if (!weldId.TryGetValue(key, out id))
                {
                    id = weldId.Count;
                    weldId[key] = id;
                }
                weldOf[v] = id;
                List<int> list;
                if (!membersByWeld.TryGetValue(id, out list))
                {
                    list = new List<int>(2);
                    membersByWeld[id] = list;
                }
                list.Add(v);
            }

            // 溶接グループ(コアの Stage1/2 連結収縮用)を出力する。
            // 同一位置に2頂点以上ある溶接IDのみ共有IDを与え、単独頂点は -1 とする。
            vertexWeldGroup = new int[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                int id = weldOf[v];
                vertexWeldGroup[v] = (membersByWeld[id].Count >= 2) ? id : -1;
            }

            // (a) 位置一致するが UV0/法線 が食い違う頂点グループをシームにする。
            List<Vector2> uv0 = null;
            if (src.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                uv0 = new List<Vector2>(vertexCount);
                src.GetUVs(0, uv0);
                if (uv0.Count != vertexCount) uv0 = null;
            }
            List<Vector3> normals = null;
            if (src.HasVertexAttribute(VertexAttribute.Normal))
            {
                normals = new List<Vector3>(vertexCount);
                src.GetNormals(normals);
                if (normals.Count != vertexCount) normals = null;
            }

            if (uv0 != null || normals != null)
            {
                foreach (KeyValuePair<int, List<int>> group in membersByWeld)
                {
                    List<int> members = group.Value;
                    if (members.Count < 2) continue;

                    bool diverges = false;
                    (long, long) firstUv = default;
                    (long, long, long) firstN = default;
                    bool haveFirst = false;
                    foreach (int v in members)
                    {
                        (long, long) qu = uv0 != null ? QuantizeVec2(uv0[v], UvQuantum) : default;
                        (long, long, long) qn = normals != null ? QuantizeVec3(normals[v], NormalQuantum) : default;
                        if (!haveFirst)
                        {
                            firstUv = qu;
                            firstN = qn;
                            haveFirst = true;
                            continue;
                        }
                        if ((uv0 != null && !qu.Equals(firstUv)) || (normals != null && !qn.Equals(firstN)))
                        {
                            diverges = true;
                            break;
                        }
                    }
                    if (diverges)
                    {
                        foreach (int v in members) seam[v] = true;
                    }
                }
            }

            // (b) 溶接後エッジの隣接三角形数を数え、1枚しかない辺(開放境界)の端点をシームにする。
            var edgeCount = new Dictionary<(int, int), int>();
            for (int s = 0; s < submeshIndices.Length; s++)
            {
                int[] tri = submeshIndices[s];
                if (tri == null) continue;
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    AccumulateEdge(edgeCount, weldOf[tri[i]], weldOf[tri[i + 1]]);
                    AccumulateEdge(edgeCount, weldOf[tri[i + 1]], weldOf[tri[i + 2]]);
                    AccumulateEdge(edgeCount, weldOf[tri[i + 2]], weldOf[tri[i]]);
                }
            }
            for (int s = 0; s < submeshIndices.Length; s++)
            {
                int[] tri = submeshIndices[s];
                if (tri == null) continue;
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    MarkBorderIfOpen(edgeCount, seam, weldOf, tri[i], tri[i + 1]);
                    MarkBorderIfOpen(edgeCount, seam, weldOf, tri[i + 1], tri[i + 2]);
                    MarkBorderIfOpen(edgeCount, seam, weldOf, tri[i + 2], tri[i]);
                }
            }

            return seam;
        }

        private static void AccumulateEdge(Dictionary<(int, int), int> edgeCount, int a, int b)
        {
            if (a == b) return; // 溶接後に潰れた辺は無視
            (int, int) key = a < b ? (a, b) : (b, a);
            int c;
            edgeCount.TryGetValue(key, out c);
            edgeCount[key] = c + 1;
        }

        private static void MarkBorderIfOpen(Dictionary<(int, int), int> edgeCount, bool[] seam, int[] weldOf, int oa, int ob)
        {
            int wa = weldOf[oa];
            int wb = weldOf[ob];
            if (wa == wb) return;
            (int, int) key = wa < wb ? (wa, wb) : (wb, wa);
            if (edgeCount.TryGetValue(key, out int c) && c == 1)
            {
                seam[oa] = true;
                seam[ob] = true;
            }
        }

        // ================================================================
        // 入力構築: 頂点保護度
        // ================================================================

        /// <summary>
        /// 頂点保護度 [0..1] を計算する。カテゴリ床(extraVertexProtection)と
        /// ブレンドシェイプ活動量(全シェイプ・全フレームのデルタ最大値を[0,0.9]へ正規化)の
        /// 大きい方を採用する。表情が動く領域(口・目など)を自動的に保護する。
        /// </summary>
        private static float[] ComputeVertexProtection(Mesh src, float[] extraVertexProtection, int vertexCount)
        {
            var protection = new float[vertexCount];

            bool hasExtra = extraVertexProtection != null && extraVertexProtection.Length == vertexCount;
            if (hasExtra)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    protection[v] = Mathf.Clamp01(extraVertexProtection[v]);
                }
            }

            int shapeCount = src.blendShapeCount;
            if (shapeCount > 0)
            {
                // 頂点ごとの最大デルタ二乗長を全シェイプ・全フレームで集計。
                var maxSqr = new float[vertexCount];
                var dv = new Vector3[vertexCount];
                for (int s = 0; s < shapeCount; s++)
                {
                    int frames = src.GetBlendShapeFrameCount(s);
                    for (int f = 0; f < frames; f++)
                    {
                        // 頂点デルタのみ必要(法線/接線はnullで省略)。
                        src.GetBlendShapeFrameVertices(s, f, dv, null, null);
                        for (int v = 0; v < vertexCount; v++)
                        {
                            float m = dv[v].sqrMagnitude;
                            if (m > maxSqr[v]) maxSqr[v] = m;
                        }
                    }
                }

                float globalMaxSqr = 0f;
                for (int v = 0; v < vertexCount; v++)
                {
                    if (maxSqr[v] > globalMaxSqr) globalMaxSqr = maxSqr[v];
                }
                if (globalMaxSqr > 1e-18f)
                {
                    float invMax = 1f / Mathf.Sqrt(globalMaxSqr);
                    for (int v = 0; v < vertexCount; v++)
                    {
                        float activity = BlendShapeProtectionMax * (Mathf.Sqrt(maxSqr[v]) * invMax);
                        if (activity > protection[v]) protection[v] = activity;
                    }
                }
            }

            return protection;
        }

        // ================================================================
        // 出力構築: keptVertices による全チャンネルサブセット
        // ================================================================

        /// <summary>
        /// DecimationResult.keptVertices(新→旧の部分集合写像)に沿って全頂点チャンネルを
        /// 間引いた新しいメッシュを構築する。サブメッシュ個数・順序は必ず維持する。
        /// </summary>
        private static Mesh BuildSubsetMesh(Mesh src, DecimationResult result, int vertexCount, int subMeshCount,
            List<Vector3> positions, ConversionReport report)
        {
            int[] kept = result.keptVertices;
            int newVertexCount = kept.Length;

            var dst = new Mesh { name = src.name };
            // インデックス形式は個別SetTrianglesより前に確定させる。
            dst.indexFormat = newVertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;

            // ---- 頂点座標(既読リストを再利用) ----
            dst.SetVertices(SubsetList(positions, kept));

            // ---- 法線・接線 ----
            if (src.HasVertexAttribute(VertexAttribute.Normal))
            {
                var normals = new List<Vector3>(vertexCount);
                src.GetNormals(normals);
                if (normals.Count == vertexCount) dst.SetNormals(SubsetList(normals, kept));
            }
            if (src.HasVertexAttribute(VertexAttribute.Tangent))
            {
                var tangents = new List<Vector4>(vertexCount);
                src.GetTangents(tangents);
                if (tangents.Count == vertexCount) dst.SetTangents(SubsetList(tangents, kept));
            }

            // ---- 頂点カラー(元の格納形式を保つ: UNorm8=Color32 / それ以外=Color) ----
            if (src.HasVertexAttribute(VertexAttribute.Color))
            {
                if (src.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                {
                    var colors = new List<Color32>(vertexCount);
                    src.GetColors(colors);
                    if (colors.Count == vertexCount) dst.SetColors(SubsetList(colors, kept));
                }
                else
                {
                    var colors = new List<Color>(vertexCount);
                    src.GetColors(colors);
                    if (colors.Count == vertexCount) dst.SetColors(SubsetList(colors, kept));
                }
            }

            // ---- UV0..7(次元を保ってサブセット) ----
            for (int ch = 0; ch < 8; ch++)
            {
                int dim = src.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + ch));
                if (dim == 2)
                {
                    var l = new List<Vector2>(vertexCount);
                    src.GetUVs(ch, l);
                    if (l.Count == vertexCount) dst.SetUVs(ch, SubsetList(l, kept));
                }
                else if (dim == 3)
                {
                    var l = new List<Vector3>(vertexCount);
                    src.GetUVs(ch, l);
                    if (l.Count == vertexCount) dst.SetUVs(ch, SubsetList(l, kept));
                }
                else if (dim == 4)
                {
                    var l = new List<Vector4>(vertexCount);
                    src.GetUVs(ch, l);
                    if (l.Count == vertexCount) dst.SetUVs(ch, SubsetList(l, kept));
                }
            }

            // ---- ボーンウェイト(4本表現)+ バインドポーズ(そのまま) ----
            BoneWeight[] boneWeights = src.boneWeights;
            if (boneWeights != null && boneWeights.Length == vertexCount && vertexCount > 0)
            {
                var newWeights = new BoneWeight[newVertexCount];
                for (int i = 0; i < newVertexCount; i++) newWeights[i] = boneWeights[kept[i]];
                dst.bindposes = src.bindposes; // バインドポーズはボーン集合不変のため未変更
                dst.boneWeights = newWeights;
            }

            // ---- インデックス(サブメッシュ個数・順序を維持。空になっても空スロットとして残す) ----
            int[][] resultSub = result.submeshIndices;
            if (resultSub != null && resultSub.Length != subMeshCount)
            {
                report.Warn(string.Format(
                    "ポリゴン削減: メッシュ '{0}' のサブメッシュ数が変化しました({1}→{2})。スロット整合のため元の数を維持します。",
                    src.name, subMeshCount, resultSub.Length));
            }
            dst.subMeshCount = subMeshCount; // 個別SetTrianglesより前に必ず設定
            int emptied = 0;
            for (int s = 0; s < subMeshCount; s++)
            {
                int[] tris = (resultSub != null && s < resultSub.Length && resultSub[s] != null)
                    ? resultSub[s]
                    : new int[0];
                dst.SetTriangles(tris, s, false);
                if (tris.Length == 0 && src.GetIndexCount(s) > 0) emptied++;
            }
            if (emptied > 0)
            {
                report.Warn(string.Format(
                    "ポリゴン削減: メッシュ '{0}' で {1} 個のマテリアルスロットが三角形0になりました(スロット自体は維持)。",
                    src.name, emptied));
            }

            // ---- ブレンドシェイプ(全シェイプ・全フレームのデルタをサブセットして再構築) ----
            int shapeCount = src.blendShapeCount;
            if (shapeCount > 0)
            {
                var dv = new Vector3[vertexCount];
                var dn = new Vector3[vertexCount];
                var dt = new Vector3[vertexCount];
                for (int s = 0; s < shapeCount; s++)
                {
                    string shapeName = src.GetBlendShapeName(s);
                    int frames = src.GetBlendShapeFrameCount(s);
                    for (int f = 0; f < frames; f++)
                    {
                        float weight = src.GetBlendShapeFrameWeight(s, f);
                        // dn/dt は使い回しバッファのため、法線/接線デルタを持たないフレームで
                        // 直前シェイプの値が残る事故を防ぐため毎回クリアする(Unityはゼロ充填する規約だが保険)。
                        Array.Clear(dn, 0, dn.Length);
                        Array.Clear(dt, 0, dt.Length);
                        src.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                        Vector3[] subN = SubsetArray(dn, kept);
                        Vector3[] subT = SubsetArray(dt, kept);
                        // 位置のみのシェイプ(多くのビセーム・MMD表情)は法線/接線デルタが全ゼロ。
                        // その場合 null を渡し、全ゼロチャンネルの明示保存(サイズ約3倍)を避ける。
                        dst.AddBlendShapeFrame(shapeName, weight,
                            SubsetArray(dv, kept),
                            IsAllZero(subN) ? null : subN,
                            IsAllZero(subT) ? null : subT);
                    }
                }
            }

            // ---- バウンズ(サブセットは元の内側に収まるため元のバウンズをそのまま使う) ----
            dst.bounds = src.bounds;

            return dst;
        }

        // ================================================================
        // ユーティリティ
        // ================================================================

        /// <summary>全サブメッシュのインデックス数合計 ÷ 3。GetIndexCountはRead/Write設定に依存しない。</summary>
        private static int TriangleCount(Mesh mesh)
        {
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) total += mesh.GetIndexCount(i);
            return (int)Math.Min(total / 3, int.MaxValue);
        }

        private static List<T> SubsetList<T>(List<T> source, int[] kept)
        {
            var r = new List<T>(kept.Length);
            for (int i = 0; i < kept.Length; i++) r.Add(source[kept[i]]);
            return r;
        }

        private static Vector3[] SubsetArray(Vector3[] source, int[] kept)
        {
            var r = new Vector3[kept.Length];
            for (int i = 0; i < kept.Length; i++) r[i] = source[kept[i]];
            return r;
        }

        /// <summary>全要素がゼロベクトルか(ブレンドシェイプの法線/接線デルタ省略判定用)。</summary>
        private static bool IsAllZero(Vector3[] a)
        {
            if (a == null) return true;
            for (int i = 0; i < a.Length; i++)
            {
                Vector3 v = a[i];
                if (v.x != 0f || v.y != 0f || v.z != 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// UV0 を xy インターリーブの float[2*V] で返す。UV0 が無い/長さ不一致なら null。
        /// コアが溶接グループ内の一致先選択(UV0最近傍)とシーム発散コストに使う。
        /// </summary>
        private static float[] ReadUv0Flat(Mesh src, int vertexCount)
        {
            if (!src.HasVertexAttribute(VertexAttribute.TexCoord0)) return null;
            var uv0 = new List<Vector2>(vertexCount);
            src.GetUVs(0, uv0);
            if (uv0.Count != vertexCount) return null;
            var flat = new float[vertexCount * 2];
            for (int v = 0; v < vertexCount; v++)
            {
                flat[2 * v + 0] = uv0[v].x;
                flat[2 * v + 1] = uv0[v].y;
            }
            return flat;
        }

        /// <summary>
        /// 法線を xyz インターリーブの float[3*V] で返す。法線が無い/長さ不一致なら null。
        /// コアが同一半球判定(dot&gt;0)と一致先タイブレーク、シーム発散コストに使う。
        /// </summary>
        private static float[] ReadNormalsFlat(Mesh src, int vertexCount)
        {
            if (!src.HasVertexAttribute(VertexAttribute.Normal)) return null;
            var normals = new List<Vector3>(vertexCount);
            src.GetNormals(normals);
            if (normals.Count != vertexCount) return null;
            var flat = new float[vertexCount * 3];
            for (int v = 0; v < vertexCount; v++)
            {
                flat[3 * v + 0] = normals[v].x;
                flat[3 * v + 1] = normals[v].y;
                flat[3 * v + 2] = normals[v].z;
            }
            return flat;
        }

        private static (long, long) QuantizeVec2(Vector2 v, float q)
        {
            double inv = 1.0 / q;
            return ((long)Math.Round(v.x * inv), (long)Math.Round(v.y * inv));
        }

        private static (long, long, long) QuantizeVec3(Vector3 v, float q)
        {
            double inv = 1.0 / q;
            return ((long)Math.Round(v.x * inv), (long)Math.Round(v.y * inv), (long)Math.Round(v.z * inv));
        }
    }
}
#endif
