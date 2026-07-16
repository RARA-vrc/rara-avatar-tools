// MeshDecimatorCore.cs
//
// PURE C# quadric-error-metric (Garland-Heckbert) edge-collapse mesh decimator.
//
// HARD CONSTRAINTS (do not violate):
//   * NO UnityEngine, NO UnityEditor, NO #if UNITY_EDITOR anywhere in this file.
//   * Must compile standalone with plain csc against netstandard for out-of-Unity
//     unit testing, AND inside the Unity editor assembly unchanged.
//   * Only System / System.Collections.Generic are used.
//
// ALGORITHM: QEM edge collapse with ENDPOINT PLACEMENT ONLY. A collapse merges the
// two endpoints of an edge onto ONE of the two ORIGINAL vertices (never a synthesized
// midpoint). Because the surviving vertex set is a strict SUBSET of the originals, the
// Unity adapter can subset every per-vertex channel (UV0-7, normals, tangents, colors,
// bone weights and blendshape deltas) directly by index with nothing interpolated,
// which is categorically safe for skinned / blendshaped avatar meshes.
//
// namespace RARA.QuestConverter.Decimation

using System;
using System.Collections.Generic;

namespace RARA.QuestConverter.Decimation
{
    // -------------------------------------------------------------------------
    // Tiny internal double-precision vector (no UnityEngine dependency).
    // -------------------------------------------------------------------------
    internal struct Double3
    {
        public double x, y, z;

        public Double3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }

        public static Double3 operator -(Double3 a, Double3 b) => new Double3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Double3 operator +(Double3 a, Double3 b) => new Double3(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Double3 Cross(Double3 a, Double3 b) =>
            new Double3(a.y * b.z - a.z * b.y,
                        a.z * b.x - a.x * b.z,
                        a.x * b.y - a.y * b.x);

        public static double Dot(Double3 a, Double3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public double Length => Math.Sqrt(x * x + y * y + z * z);
    }

    // -------------------------------------------------------------------------
    // Symmetric 4x4 error quadric stored as its 10 unique upper-triangle entries.
    //   [ q11 q12 q13 q14 ]
    //   [ q12 q22 q23 q24 ]
    //   [ q13 q23 q33 q34 ]
    //   [ q14 q24 q34 q44 ]
    // -------------------------------------------------------------------------
    internal struct Quadric
    {
        public double q11, q12, q13, q14, q22, q23, q24, q33, q34, q44;

        // Accumulate an area-weighted plane quadric (a,b,c) must be a unit normal, d = -n.p0.
        public void AddPlane(double a, double b, double c, double d, double w)
        {
            q11 += w * a * a; q12 += w * a * b; q13 += w * a * c; q14 += w * a * d;
            q22 += w * b * b; q23 += w * b * c; q24 += w * b * d;
            q33 += w * c * c; q34 += w * c * d;
            q44 += w * d * d;
        }

        public static Quadric operator +(Quadric a, Quadric b)
        {
            Quadric r;
            r.q11 = a.q11 + b.q11; r.q12 = a.q12 + b.q12; r.q13 = a.q13 + b.q13; r.q14 = a.q14 + b.q14;
            r.q22 = a.q22 + b.q22; r.q23 = a.q23 + b.q23; r.q24 = a.q24 + b.q24;
            r.q33 = a.q33 + b.q33; r.q34 = a.q34 + b.q34;
            r.q44 = a.q44 + b.q44;
            return r;
        }

        // v^T Q v for the homogeneous point (p.x, p.y, p.z, 1).
        public double Eval(Double3 p)
        {
            double x = p.x, y = p.y, z = p.z;
            return q11 * x * x + 2.0 * q12 * x * y + 2.0 * q13 * x * z + 2.0 * q14 * x
                 + q22 * y * y + 2.0 * q23 * y * z + 2.0 * q24 * y
                 + q33 * z * z + 2.0 * q34 * z
                 + q44;
        }
    }

    // -------------------------------------------------------------------------
    // Public input mesh (topology + per-vertex hints). All arrays are shared, not
    // copied; the decimator does not mutate them.
    // -------------------------------------------------------------------------
    public sealed class DecimationMesh
    {
        // xyz interleaved, length 3*V.
        public float[] positions;
        // Triangle index lists, one array per submesh (indices into the vertex pool).
        public int[][] submeshIndices;
        // Length V. 0 = free to collapse .. 1 = near-locked. Raises collapse cost.
        public float[] vertexProtection;
        // Length V. true = UV-seam / border vertex; adds extra collapse cost.
        public bool[] seamVertex;

        // OPTIONAL (all default null = legacy behaviour). Position-coincident WELD GROUPS:
        // vertices at the same location split only by attribute discontinuity (UV seam,
        // hard-normal edge, material boundary). Length V. Value = a shared group id for
        // every member of a multi-member group; -1 for singletons (vertices not welded to
        // any other). When present, coincident members are treated as a LINKED collapse
        // unit: their quadrics are coupled (Stage 1) and they collapse together across the
        // seam so no side is left behind to crack (Stage 2). Group ids need not be dense.
        public int[] vertexWeldGroup;

        // OPTIONAL uv0 (length 2*V, uv interleaved) and normals (length 3*V, xyz
        // interleaved). Used ONLY when vertexWeldGroup is non-null: to pick each dying
        // member's matching survivor by UV0 proximity / same-hemisphere normal during a
        // linked collapse, and to scale the seam cost by measured attribute divergence.
        // Both null => linked collapse disabled, seam cost falls back to the flat multiplier.
        public float[] uv0;
        public float[] normals;
    }

    // -------------------------------------------------------------------------
    // Tunable cost weights / robustness knobs.
    // -------------------------------------------------------------------------
    public sealed class DecimationOptions
    {
        public double seamCostMultiplier = 8.0;   // UPPER BOUND of the seam cost factor (see below), and per material boundary edge
        public double borderCostMultiplier = 8.0; // open-border edge (exactly 1 adjacent triangle)
        public double protectionCostScale = 100.0;// scales the 1/(1-p) protection term
        public int maxDegeneratePasses = 3;        // final degenerate/duplicate cleanup sweeps

        // Stage 2 (linked group collapse) master switch. Only takes effect when
        // DecimationMesh.vertexWeldGroup and uv0/normals are supplied. false disables ONLY the
        // Stage-2 linked collapse, so collapses become single again; Stage-1 group-quadric
        // coupling, group-max protection coupling, and the divergence-proportional seam cost
        // still apply whenever multi-member weld groups exist (it is NOT full legacy behaviour).
        // The strict legacy result is obtained by supplying no weld groups at all.
        public bool weldLinking = true;
    }

    // -------------------------------------------------------------------------
    // Result: a vertex SUBSET mapping plus remapped topology.
    // -------------------------------------------------------------------------
    public sealed class DecimationResult
    {
        // NEW vertex index -> OLD vertex index. The adapter subsets every channel with this.
        public int[] keptVertices;
        // NEW indices per submesh (degenerate / duplicate triangles removed).
        public int[][] submeshIndices;
        // Total surviving triangle count.
        public int triangleCount;
    }

    // -------------------------------------------------------------------------
    // Entry point.
    // -------------------------------------------------------------------------
    public static class QemDecimator
    {
        public static DecimationResult Decimate(DecimationMesh mesh, int targetTriangles, DecimationOptions options)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (options == null) options = new DecimationOptions();
            return new Impl(mesh, options).Run(targetTriangles);
        }

        // =====================================================================
        // Worker. Holds all mutable state so the hot path avoids parameter churn.
        // =====================================================================
        private sealed class Impl
        {
            // Immutable per-vertex data (indexed by ORIGINAL vertex id).
            private readonly int _v;
            private readonly Double3[] _pos;
            private readonly float[] _prot;
            private readonly bool[] _seam;

            // Mutable per-vertex data.
            // Effective per-vertex protection: max protection across a vertex's coincident
            // weld-group members (identical to _prot for singletons / when no weld groups).
            // Protecting one copy of a seam vertex must protect ALL its copies, otherwise the
            // unprotected copy's cheap edge drags the whole group (protected copy included)
            // into a linked collapse. Precomputed once; static across decimation.
            private readonly float[] _protEff;

            private readonly Quadric[] _q;
            private readonly bool[] _vAlive;
            private readonly List<int>[] _vTris; // incident (alive) triangle ids

            // Triangles (flattened across submeshes). Vertex slots always hold the
            // current alive representative.
            private readonly int _triCount;
            private readonly int[] _t0, _t1, _t2, _tSub;
            private readonly bool[] _tAlive;
            private int _liveTri;

            // Per-submesh live triangle counts (to never empty a nonempty submesh).
            private readonly int _subCount;
            private readonly int[] _subLive;

            // Edge records (growable parallel arrays).
            private int[] _edgeA, _edgeB, _edgeVer;
            private bool[] _edgeRemoved;
            private int _edgeCount;
            private readonly Dictionary<long, int> _pairToEdge;

            // Binary min-heap of pending collapses. Ordered by (cost, a, b); a<b so
            // ordering is fully deterministic. Stale entries are filtered on pop via
            // version stamps.
            private double[] _hCost;
            private int[] _hA, _hB, _hEdge, _hVer;
            private int _hSize;

            // Scratch stamp buffers (no per-collapse allocation).
            private readonly int[] _vStamp;
            private int _stamp;
            private readonly int[] _tStamp;
            private int _tStampVal;

            // Reusable scratch collections.
            private readonly List<int> _neighborBuf = new List<int>(16);
            private readonly List<int> _sharedBuf = new List<int>(4);
            private readonly Dictionary<int, int> _subDelta = new Dictionary<int, int>(4);
            private readonly Dictionary<long, int> _dupBuf = new Dictionary<long, int>(16);

            private readonly DecimationOptions _opt;
            private readonly double _epsGeo; // geometric floor so seam/protection weighting still bites on flat regions

            // -----------------------------------------------------------------
            // Weld-group state (Stage 1 quadric coupling + Stage 2 linked collapse).
            // All null / empty and every flag false when DecimationMesh.vertexWeldGroup
            // is not supplied -> the class behaves exactly as before.
            // -----------------------------------------------------------------
            private int[] _grpOf;            // ORIGINAL vertex id -> internal weld-group index, or -1 (singleton)
            private int[][] _grpMembers;     // internal group index -> ascending original vertex ids (>=2 members)
            private int[] _grpParent;        // union-find over group indices; a dying group points at its survivor group
            private int _grpCount;
            private double[] _grpDivergence;  // per group: seam cost factor in [1, seamCostMultiplier] from measured UV0/normal spread

            private double[] _uvX, _uvY;      // per original vertex UV0 (null if not supplied)
            private Double3[] _nrm;           // per original vertex normal (null if not supplied)

            private bool _hasWeld;            // multi-member weld groups exist -> Stage 1 active
            private bool _divergenceCost;     // weld groups + (uv0 or normals) -> divergence-proportional seam cost active
            private bool _linkEnabled;        // weldLinking + weld groups + uv0 + normals -> Stage 2 active

            // Reusable scratch for a single group collapse (typical group size 2-4).
            private readonly List<int> _grpDying = new List<int>(4);
            private readonly List<int> _grpSurv = new List<int>(4);
            private readonly List<int> _grpTarget = new List<int>(4);
            private readonly Dictionary<int, int> _grpSubDelta = new Dictionary<int, int>(4);

            // -----------------------------------------------------------------
            public Impl(DecimationMesh mesh, DecimationOptions opt)
            {
                _opt = opt;

                float[] positions = mesh.positions ?? Array.Empty<float>();
                _v = positions.Length / 3;

                _pos = new Double3[_v];
                for (int i = 0; i < _v; i++)
                    _pos[i] = new Double3(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);

                _prot = new float[_v];
                if (mesh.vertexProtection != null)
                {
                    int n = Math.Min(_v, mesh.vertexProtection.Length);
                    for (int i = 0; i < n; i++)
                    {
                        float p = mesh.vertexProtection[i];
                        if (p < 0f) p = 0f; else if (p > 1f) p = 1f;
                        _prot[i] = p;
                    }
                }

                _seam = new bool[_v];
                if (mesh.seamVertex != null)
                {
                    int n = Math.Min(_v, mesh.seamVertex.Length);
                    for (int i = 0; i < n; i++) _seam[i] = mesh.seamVertex[i];
                }

                _q = new Quadric[_v];
                _vAlive = new bool[_v];
                for (int i = 0; i < _v; i++) _vAlive[i] = true;
                _vTris = new List<int>[_v];
                for (int i = 0; i < _v; i++) _vTris[i] = new List<int>(6);
                _vStamp = new int[_v];

                // Flatten triangles.
                int[][] subs = mesh.submeshIndices ?? Array.Empty<int[]>();
                _subCount = subs.Length;
                _subLive = new int[_subCount];

                int total = 0;
                for (int s = 0; s < _subCount; s++)
                    if (subs[s] != null) total += subs[s].Length / 3;

                _triCount = total;
                _t0 = new int[total];
                _t1 = new int[total];
                _t2 = new int[total];
                _tSub = new int[total];
                _tAlive = new bool[total];
                _tStamp = new int[total];

                int ti = 0;
                for (int s = 0; s < _subCount; s++)
                {
                    int[] idx = subs[s];
                    if (idx == null) continue;
                    int triN = idx.Length / 3;
                    for (int k = 0; k < triN; k++)
                    {
                        int a = idx[3 * k], b = idx[3 * k + 1], c = idx[3 * k + 2];
                        // Guard against out-of-range indices (malformed input).
                        if (a < 0 || a >= _v || b < 0 || b >= _v || c < 0 || c >= _v)
                        {
                            _tAlive[ti] = false;
                            _t0[ti] = _t1[ti] = _t2[ti] = 0;
                            _tSub[ti] = s;
                            ti++;
                            continue;
                        }
                        _t0[ti] = a; _t1[ti] = b; _t2[ti] = c; _tSub[ti] = s;
                        _tAlive[ti] = true;
                        _vTris[a].Add(ti); _vTris[b].Add(ti); _vTris[c].Add(ti);
                        _subLive[s]++;
                        _liveTri++;
                        ti++;
                    }
                }

                // Per-vertex quadrics from area-weighted face planes.
                for (int t = 0; t < _triCount; t++)
                {
                    if (!_tAlive[t]) continue;
                    int a = _t0[t], b = _t1[t], c = _t2[t];
                    Double3 p0 = _pos[a], p1 = _pos[b], p2 = _pos[c];
                    Double3 nrm = Double3.Cross(p1 - p0, p2 - p0);
                    double len = nrm.Length;
                    if (len < 1e-20) continue; // degenerate face contributes no plane
                    double inv = 1.0 / len;
                    double na = nrm.x * inv, nb = nrm.y * inv, nc = nrm.z * inv;
                    double d = -(na * p0.x + nb * p0.y + nc * p0.z);
                    double area = 0.5 * len;
                    _q[a].AddPlane(na, nb, nc, d, area);
                    _q[b].AddPlane(na, nb, nc, d, area);
                    _q[c].AddPlane(na, nb, nc, d, area);
                }

                // Geometric floor relative to mesh scale so seam/border/protection
                // multipliers still order flat regions correctly.
                _epsGeo = ComputeEpsGeo();

                // Weld groups + optional per-vertex attributes (all no-ops when absent).
                BuildAttributes(mesh);
                BuildWeldGroups(mesh);
                _divergenceCost = _hasWeld && (_uvX != null || _nrm != null);
                _linkEnabled = _opt.weldLinking && _hasWeld && _uvX != null && _nrm != null;
                ApplyQuadricCoupling();   // Stage 1: coincident members share the summed group quadric
                ComputeDivergences();     // seam cost proportional to attribute spread across each group

                // Protection couples across coincident copies: every member of a weld group
                // takes the group's MAX protection so a partially-protected seam stays intact
                // as a unit (protection is a scheduling cost; the linked collapse itself has no
                // per-vertex cost gate, so this coupling is what makes protection group-wide).
                if (_hasWeld)
                {
                    float[] pe = (float[])_prot.Clone();
                    for (int g = 0; g < _grpCount; g++)
                    {
                        int[] m = _grpMembers[g];
                        float mx = 0f;
                        for (int i = 0; i < m.Length; i++) if (_prot[m[i]] > mx) mx = _prot[m[i]];
                        for (int i = 0; i < m.Length; i++) pe[m[i]] = mx;
                    }
                    _protEff = pe;
                }
                else
                {
                    _protEff = _prot;
                }

                // Edge structures.
                _pairToEdge = new Dictionary<long, int>(_triCount * 2 + 8);
                int ecap = Math.Max(16, _triCount * 3);
                _edgeA = new int[ecap];
                _edgeB = new int[ecap];
                _edgeVer = new int[ecap];
                _edgeRemoved = new bool[ecap];

                int hcap = Math.Max(16, _triCount * 3);
                _hCost = new double[hcap];
                _hA = new int[hcap];
                _hB = new int[hcap];
                _hEdge = new int[hcap];
                _hVer = new int[hcap];
            }

            private double ComputeEpsGeo()
            {
                if (_v == 0) return 1e-12;
                double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
                double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
                for (int i = 0; i < _v; i++)
                {
                    Double3 p = _pos[i];
                    if (p.x < minx) minx = p.x; if (p.x > maxx) maxx = p.x;
                    if (p.y < miny) miny = p.y; if (p.y > maxy) maxy = p.y;
                    if (p.z < minz) minz = p.z; if (p.z > maxz) maxz = p.z;
                }
                double dx = maxx - minx, dy = maxy - miny, dz = maxz - minz;
                double diag2 = dx * dx + dy * dy + dz * dz;
                if (diag2 <= 0.0 || double.IsNaN(diag2) || double.IsInfinity(diag2)) return 1e-12;
                return diag2 * 1e-10;
            }

            // =================================================================
            // Weld-group construction (deterministic: input array order throughout).
            // =================================================================

            // Read optional UV0 / normal channels into per-vertex scratch (null if absent
            // or mis-sized). These are static across decimation.
            private void BuildAttributes(DecimationMesh mesh)
            {
                float[] uv = mesh.uv0;
                if (uv != null && uv.Length >= 2 * _v)
                {
                    _uvX = new double[_v];
                    _uvY = new double[_v];
                    for (int i = 0; i < _v; i++) { _uvX[i] = uv[2 * i]; _uvY[i] = uv[2 * i + 1]; }
                }
                float[] nr = mesh.normals;
                if (nr != null && nr.Length >= 3 * _v)
                {
                    _nrm = new Double3[_v];
                    for (int i = 0; i < _v; i++) _nrm[i] = new Double3(nr[3 * i], nr[3 * i + 1], nr[3 * i + 2]);
                }
            }

            // Group coincident vertices by their supplied weld-group id. Only groups with
            // >=2 members become internal groups; everything else stays a singleton (-1).
            private void BuildWeldGroups(DecimationMesh mesh)
            {
                _grpOf = new int[_v];
                for (int i = 0; i < _v; i++) _grpOf[i] = -1;

                int[] wg = mesh.vertexWeldGroup;
                if (wg == null || wg.Length < _v)
                {
                    _grpCount = 0;
                    _grpMembers = Array.Empty<int[]>();
                    _grpParent = Array.Empty<int>();
                    _hasWeld = false;
                    return;
                }

                // Bucket by input group id, preserving first-seen order for determinism.
                var byId = new Dictionary<int, List<int>>();
                var order = new List<int>();
                for (int v = 0; v < _v; v++)
                {
                    int gid = wg[v];
                    if (gid < 0) continue;
                    if (!byId.TryGetValue(gid, out List<int> list))
                    {
                        list = new List<int>(2);
                        byId[gid] = list;
                        order.Add(gid);
                    }
                    list.Add(v); // v strictly increasing -> member arrays stay ascending
                }

                var members = new List<int[]>();
                for (int i = 0; i < order.Count; i++)
                {
                    List<int> list = byId[order[i]];
                    if (list.Count < 2) continue;
                    int idx = members.Count;
                    int[] arr = list.ToArray();
                    members.Add(arr);
                    for (int k = 0; k < arr.Length; k++) _grpOf[arr[k]] = idx;
                }

                _grpCount = members.Count;
                _grpMembers = members.ToArray();
                _grpParent = new int[_grpCount];
                for (int i = 0; i < _grpCount; i++) _grpParent[i] = i;
                _hasWeld = _grpCount > 0;
            }

            // Stage 1: because the incident-face sets of coincident copies are disjoint,
            // their summed quadric is exactly the geometric quadric of the shared position.
            // Give every member that shared group quadric so a seam cannot be under-costed.
            private void ApplyQuadricCoupling()
            {
                if (!_hasWeld) return;
                for (int g = 0; g < _grpCount; g++)
                {
                    int[] m = _grpMembers[g];
                    Quadric sum = default;
                    for (int i = 0; i < m.Length; i++) sum = sum + _q[m[i]];
                    for (int i = 0; i < m.Length; i++) _q[m[i]] = sum;
                }
            }

            // Seam cost factor per group: proportional to the largest UV0 gap / normal
            // splay among its members, mapped into [1, seamCostMultiplier]. A group whose
            // members agree (no real seam, mere duplicate) costs ~1; a hard seam costs up
            // to the full multiplier. Replaces the old flat per-seam-vertex penalty.
            private void ComputeDivergences()
            {
                if (!_divergenceCost) return;
                _grpDivergence = new double[_grpCount];
                double seamMult = _opt.seamCostMultiplier;
                for (int g = 0; g < _grpCount; g++)
                {
                    int[] m = _grpMembers[g];
                    double uvDiv = 0.0;   // max UV0 Euclidean distance between members
                    double nMinDot = 1.0; // most divergent normal pair
                    for (int i = 0; i < m.Length; i++)
                    {
                        for (int j = i + 1; j < m.Length; j++)
                        {
                            if (_uvX != null)
                            {
                                double du = _uvX[m[i]] - _uvX[m[j]];
                                double dv = _uvY[m[i]] - _uvY[m[j]];
                                double d = Math.Sqrt(du * du + dv * dv);
                                if (d > uvDiv) uvDiv = d;
                            }
                            if (_nrm != null)
                            {
                                double dot = Double3.Dot(_nrm[m[i]], _nrm[m[j]]);
                                if (dot < nMinDot) nMinDot = dot;
                            }
                        }
                    }
                    double uvPart = _uvX != null ? Clamp01(uvDiv) : 0.0;          // UV distance capped at 1 uv-unit
                    double nPart = _nrm != null ? Clamp01((1.0 - nMinDot) * 0.5) : 0.0; // 0 identical .. 1 opposite
                    double t = uvPart > nPart ? uvPart : nPart;
                    double mult = 1.0 + t * (seamMult - 1.0);
                    if (mult < 1.0) mult = 1.0; else if (mult > seamMult) mult = seamMult;
                    _grpDivergence[g] = mult;
                }
            }

            private static double Clamp01(double x) => x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x);

            // Union-find root of a weld group (path-halving). -1 stays -1 (singleton).
            private int GroupFind(int g)
            {
                if (g < 0) return -1;
                while (_grpParent[g] != g)
                {
                    _grpParent[g] = _grpParent[_grpParent[g]];
                    g = _grpParent[g];
                }
                return g;
            }

            private int AliveCount(int g)
            {
                int[] m = _grpMembers[g];
                int c = 0;
                for (int i = 0; i < m.Length; i++) if (_vAlive[m[i]]) c++;
                return c;
            }

            // -----------------------------------------------------------------
            public DecimationResult Run(int targetTriangles)
            {
                // Build the initial edge set (dedup via dictionary), then seed the heap.
                for (int t = 0; t < _triCount; t++)
                {
                    if (!_tAlive[t]) continue;
                    int a = _t0[t], b = _t1[t], c = _t2[t];
                    GetOrCreateEdge(a, b);
                    GetOrCreateEdge(b, c);
                    GetOrCreateEdge(c, a);
                }
                int initialEdges = _edgeCount;
                for (int e = 0; e < initialEdges; e++)
                    RefreshEdgeById(e);

                // Main collapse loop.
                while (_liveTri > targetTriangles && _hSize > 0)
                {
                    PopMin(out double cost, out int a, out int b, out int edge, out int ver);

                    if (_edgeRemoved[edge]) continue;
                    if (ver != _edgeVer[edge]) continue;          // stale cost
                    if (!_vAlive[a] || !_vAlive[b]) { _edgeRemoved[edge] = true; continue; }

                    TryCollapse(edge, a, b);
                    // Success or legality-reject both just consume this item. A legality
                    // reject leaves the edge registered so a neighbouring collapse can
                    // refresh (re-push) it later.
                }

                // Final degenerate / duplicate cleanup sweeps.
                CleanupPasses();

                return BuildResult();
            }

            // =================================================================
            // Edge helpers.
            // =================================================================
            private static long PairKey(int a, int b) => ((long)a << 32) | (uint)b;

            private int GetOrCreateEdge(int u, int v)
            {
                if (u == v) return -1;
                int a = u < v ? u : v;
                int b = u < v ? v : u;
                long key = PairKey(a, b);
                if (_pairToEdge.TryGetValue(key, out int id) && !_edgeRemoved[id])
                    return id;

                id = _edgeCount++;
                if (id >= _edgeA.Length) GrowEdges();
                _edgeA[id] = a; _edgeB[id] = b; _edgeVer[id] = 0; _edgeRemoved[id] = false;
                _pairToEdge[key] = id;
                return id;
            }

            private void GrowEdges()
            {
                int n = _edgeA.Length * 2;
                Array.Resize(ref _edgeA, n);
                Array.Resize(ref _edgeB, n);
                Array.Resize(ref _edgeVer, n);
                Array.Resize(ref _edgeRemoved, n);
            }

            private void RefreshPair(int u, int v)
            {
                int id = GetOrCreateEdge(u, v);
                if (id >= 0) RefreshEdgeById(id);
            }

            private void RefreshEdgeById(int id)
            {
                int a = _edgeA[id], b = _edgeB[id];
                if (a == b || !_vAlive[a] || !_vAlive[b]) { _edgeRemoved[id] = true; return; }
                if (!ComputeCandidate(a, b, out double cost, out _))
                {
                    _edgeRemoved[id] = true; // no longer a real edge
                    return;
                }
                _edgeVer[id]++;
                Push(cost, a, b, id, _edgeVer[id]);
            }

            // Cost of collapsing edge (a,b) with endpoint placement. Returns false when
            // (a,b) no longer share any live triangle (not an edge anymore).
            private bool ComputeCandidate(int a, int b, out double cost, out int survivor)
            {
                cost = 0.0; survivor = -1;

                // Scan the smaller incidence list for shared triangles.
                List<int> la = _vTris[a], lb = _vTris[b];
                int otherV;
                List<int> scan;
                if (la.Count <= lb.Count) { scan = la; otherV = b; }
                else { scan = lb; otherV = a; }

                int shared = 0;
                int firstSub = -1;
                bool multiSub = false;
                for (int i = 0; i < scan.Count; i++)
                {
                    int t = scan[i];
                    if (!_tAlive[t]) continue;
                    if (!TriContains(t, otherV)) continue;
                    shared++;
                    int sub = _tSub[t];
                    if (firstSub < 0) firstSub = sub;
                    else if (sub != firstSub) multiSub = true;
                }
                if (shared == 0) return false;

                Quadric qs = _q[a] + _q[b];
                double ea = qs.Eval(_pos[a]);
                double eb = qs.Eval(_pos[b]);
                if (ea < 0.0) ea = 0.0;
                if (eb < 0.0) eb = 0.0;

                double baseCost;
                if (ea < eb || (ea == eb && a < b)) { survivor = a; baseCost = ea; }
                else { survivor = b; baseCost = eb; }

                double mult = ProtFactor(a) * ProtFactor(b);
                if (_divergenceCost)
                {
                    // Seam cost scaled by the actual attribute divergence of each endpoint's
                    // weld group (clamped to [1, seamCostMultiplier]); non-grouped seam/border
                    // vertices keep the flat multiplier via SeamFactor.
                    mult *= SeamFactor(a);
                    mult *= SeamFactor(b);
                }
                else
                {
                    if (_seam[a]) mult *= _opt.seamCostMultiplier;
                    if (_seam[b]) mult *= _opt.seamCostMultiplier;
                }
                if (shared == 1) mult *= _opt.borderCostMultiplier; // open border
                if (multiSub) mult *= _opt.seamCostMultiplier;      // material boundary

                cost = (baseCost + _epsGeo) * mult;
                return true;
            }

            private double ProtFactor(int v)
            {
                double p = _protEff[v];
                if (p <= 0.0) return 1.0;
                return 1.0 + _opt.protectionCostScale * (p / (1.0 - p + 1e-3));
            }

            // Divergence-proportional seam factor for a vertex. Grouped vertices use their
            // group's measured divergence; ungrouped seam/border vertices keep the flat
            // multiplier so open borders stay protected. Only called when _divergenceCost.
            private double SeamFactor(int v)
            {
                int g = _grpOf[v];
                if (g >= 0 && _grpDivergence != null) return _grpDivergence[g];
                if (_seam[v]) return _opt.seamCostMultiplier;
                return 1.0;
            }

            private bool TriContains(int t, int x) => _t0[t] == x || _t1[t] == x || _t2[t] == x;

            // =================================================================
            // Collapse.
            // =================================================================
            private void TryCollapse(int edge, int a, int b)
            {
                if (!ComputeCandidate(a, b, out _, out int survivor))
                {
                    _edgeRemoved[edge] = true;
                    return;
                }
                int s = survivor;
                int r = (s == a) ? b : a;

                // Stage 2: if the DYING endpoint is a multi-member weld group (coincident
                // copies split by a seam), collapse the WHOLE group together so no copy is
                // left behind to crack. The survivor side may be another weld group (each
                // copy remaps to its UV0-nearest survivor) OR a plain vertex (all copies
                // weld onto it). If the linked collapse is rejected we do NOT fall back to a
                // single collapse: that would move one copy and tear the seam, the exact
                // failure this feature exists to prevent. Instead reject the edge (leave it
                // for a later, possibly-legal retry) and let decimation proceed elsewhere.
                if (_linkEnabled)
                {
                    int gd = GroupFind(_grpOf[r]);        // dying group (>=0 => has coincident copies)
                    int hs = GroupFind(_grpOf[survivor]); // survivor group, or -1 for a singleton survivor
                    if (gd >= 0 && gd != hs && AliveCount(gd) >= 2)
                    {
                        if (TryGroupCollapse(gd, hs, survivor)) _edgeRemoved[edge] = true;
                        return; // linked succeeded (edge removed) or was rejected (seam kept intact)
                    }
                }

                // Gather dying (shared) triangles = those containing both endpoints.
                _sharedBuf.Clear();
                {
                    List<int> lr = _vTris[r];
                    for (int i = 0; i < lr.Count; i++)
                    {
                        int t = lr[i];
                        if (_tAlive[t] && TriContains(t, s)) _sharedBuf.Add(t);
                    }
                }
                if (_sharedBuf.Count == 0) { _edgeRemoved[edge] = true; return; }

                // (1) Never empty a nonempty submesh.
                _subDelta.Clear();
                for (int i = 0; i < _sharedBuf.Count; i++)
                {
                    int sub = _tSub[_sharedBuf[i]];
                    _subDelta.TryGetValue(sub, out int c);
                    _subDelta[sub] = c + 1;
                }
                foreach (KeyValuePair<int, int> kv in _subDelta)
                    if (_subLive[kv.Key] - kv.Value < 1) return; // would empty submesh -> reject

                // (2) Link condition (manifold preservation).
                if (!LinkConditionOK(a, b)) return;

                // (3) Normal-flip rejection on the surviving triangles that move r -> s.
                if (!NormalFlipOK(s, r)) return;

                // -------- Perform the collapse. --------
                PerformCollapse(s, r);
                RefreshAround(s);

                _edgeRemoved[edge] = true;
            }

            // =================================================================
            // Linked (weld-group) collapse. Collapses every alive member of the dying
            // group gd onto its matching survivor in group hs, atomically: all legality
            // is checked against the pre-collapse state and, if any member fails, the
            // whole group collapse is rejected (no mutation). Survivors remain ORIGINAL
            // vertices, so the subset-safety property is preserved.
            // =================================================================
            private bool TryGroupCollapse(int gd, int hs, int survivorVertex)
            {
                // Candidate targets: alive members of the survivor group hs, or - when the
                // survivor is a plain vertex (hs < 0) - just that vertex. Ascending id order.
                _grpSurv.Clear();
                if (hs >= 0)
                {
                    int[] m = _grpMembers[hs];
                    for (int i = 0; i < m.Length; i++) if (_vAlive[m[i]]) _grpSurv.Add(m[i]);
                }
                else if (_vAlive[survivorVertex])
                {
                    _grpSurv.Add(survivorVertex);
                }
                if (_grpSurv.Count == 0) return false;

                // Each alive dying member picks its matching survivor (UV0-nearest, same
                // hemisphere). Any member without a valid target aborts the group collapse.
                _grpDying.Clear();
                _grpTarget.Clear();
                {
                    int[] m = _grpMembers[gd];
                    for (int i = 0; i < m.Length; i++)
                    {
                        int g = m[i];
                        if (!_vAlive[g]) continue;
                        int h = PickTarget(g);
                        if (h < 0) return false; // no same-hemisphere UV match -> reject
                        _grpDying.Add(g);
                        _grpTarget.Add(h);
                    }
                }
                if (_grpDying.Count == 0) return false;

                // All members are validated against the SAME pre-collapse state but committed
                // sequentially, so two dying members resolving to the same survivor vertex would
                // let the second collapse run against a fan the first collapse just enlarged -
                // a fold/non-manifold edge the stale pre-collapse check never saw. Reject the
                // whole group collapse in that case (all copies stay alive => seam kept intact)
                // rather than commit a collapse whose legality was never verified against live state.
                for (int i = 0; i < _grpTarget.Count; i++)
                    for (int j = i + 1; j < _grpTarget.Count; j++)
                        if (_grpTarget[i] == _grpTarget[j]) return false;

                // Validate legality for EVERY member against the current state, and tally
                // dying triangles per submesh so no submesh is fully emptied.
                _grpSubDelta.Clear();
                for (int i = 0; i < _grpDying.Count; i++)
                {
                    int g = _grpDying[i], h = _grpTarget[i];
                    if (g == h) return false; // distinct groups guarantee g!=h, guard anyway
                    if (!LinkConditionOK(g, h)) return false;
                    if (!NormalFlipOK(h, g)) return false;
                    List<int> lg = _vTris[g];
                    for (int k = 0; k < lg.Count; k++)
                    {
                        int t = lg[k];
                        if (_tAlive[t] && TriContains(t, h))
                        {
                            int sub = _tSub[t];
                            _grpSubDelta.TryGetValue(sub, out int c);
                            _grpSubDelta[sub] = c + 1;
                        }
                    }
                }
                foreach (KeyValuePair<int, int> kv in _grpSubDelta)
                    if (_subLive[kv.Key] - kv.Value < 1) return false; // would empty submesh

                // Commit: perform every per-member collapse, then merge the groups.
                for (int i = 0; i < _grpDying.Count; i++)
                    PerformCollapse(_grpTarget[i], _grpDying[i]);

                if (hs >= 0) _grpParent[gd] = hs; // union: dying group resolves to survivor group

                // Refresh edges around every (still-alive) survivor.
                for (int i = 0; i < _grpSurv.Count; i++)
                    if (_vAlive[_grpSurv[i]]) RefreshAround(_grpSurv[i]);

                return true;
            }

            // Choose the survivor (in _grpSurv) that best matches dying member g: minimum
            // UV0 distance, tie-break maximum normal dot, then minimum id. Only survivors
            // in the same normal hemisphere (dot > 0) are eligible; returns -1 if none.
            private int PickTarget(int g)
            {
                double gu = _uvX[g], gv = _uvY[g];
                Double3 gn = _nrm[g];
                int best = -1;
                double bestDist = 0.0, bestDot = 0.0;
                for (int i = 0; i < _grpSurv.Count; i++)
                {
                    int h = _grpSurv[i];
                    double dn = Double3.Dot(gn, _nrm[h]);
                    if (dn <= 0.0) continue; // same-hemisphere gate
                    double du = _uvX[h] - gu, dv = _uvY[h] - gv;
                    double d = du * du + dv * dv;
                    if (best < 0
                        || d < bestDist
                        || (d == bestDist && (dn > bestDot || (dn == bestDot && h < best))))
                    {
                        best = h; bestDist = d; bestDot = dn;
                    }
                }
                return best;
            }

            // Mechanical collapse of r onto survivor s (no legality checks; caller has
            // validated). Kills triangles containing both, remaps r->s in the rest,
            // accumulates the quadric, and dedups the survivor fan.
            private void PerformCollapse(int s, int r)
            {
                List<int> rl = _vTris[r];
                for (int i = 0; i < rl.Count; i++)
                {
                    int t = rl[i];
                    if (!_tAlive[t]) continue;
                    if (TriContains(t, s))
                    {
                        // Dying triangle.
                        _tAlive[t] = false;
                        _liveTri--;
                        _subLive[_tSub[t]]--;
                    }
                    else
                    {
                        ReplaceVertex(t, r, s);
                        _vTris[s].Add(t);
                    }
                }
                rl.Clear();
                _vAlive[r] = false;

                _q[s] = _q[s] + _q[r];

                CompactAndDedup(s);
            }

            // Re-price every edge incident to survivor s (push fresh heap entries).
            private void RefreshAround(int s)
            {
                _stamp++;
                int ns = _stamp;
                _neighborBuf.Clear();
                List<int> sl = _vTris[s];
                for (int i = 0; i < sl.Count; i++)
                {
                    int t = sl[i];
                    if (!_tAlive[t]) continue;
                    AddNeighbor(_t0[t], s, ns);
                    AddNeighbor(_t1[t], s, ns);
                    AddNeighbor(_t2[t], s, ns);
                }
                for (int i = 0; i < _neighborBuf.Count; i++)
                    RefreshPair(s, _neighborBuf[i]);
            }

            private void AddNeighbor(int v, int s, int stampVal)
            {
                if (v == s || !_vAlive[v]) return;
                if (_vStamp[v] == stampVal) return;
                _vStamp[v] = stampVal;
                _neighborBuf.Add(v);
            }

            private void ReplaceVertex(int t, int from, int to)
            {
                if (_t0[t] == from) _t0[t] = to;
                else if (_t1[t] == from) _t1[t] = to;
                else if (_t2[t] == from) _t2[t] = to;
            }

            // Compact _vTris[s] to alive/distinct ids, then drop duplicate faces that
            // share the same opposite-vertex pair (created at the collapse site).
            private void CompactAndDedup(int s)
            {
                List<int> list = _vTris[s];

                // Compact: alive + distinct triangle ids.
                _tStampVal++;
                int stampVal = _tStampVal;
                int write = 0;
                for (int read = 0; read < list.Count; read++)
                {
                    int t = list[read];
                    if (!_tAlive[t]) continue;
                    if (_tStamp[t] == stampVal) continue;
                    _tStamp[t] = stampVal;
                    list[write++] = t;
                }
                if (write < list.Count) list.RemoveRange(write, list.Count - write);

                // Duplicate faces (same {s,u,w}) and self-degenerate (u==w) removal.
                _dupBuf.Clear();
                bool killed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    int t = list[i];
                    if (!_tAlive[t]) continue;
                    OtherTwo(t, s, out int u, out int w);
                    if (u == w)
                    {
                        // Degenerate (two slots equal s already handled as dying; this is
                        // the u==w case) -> kill unconditionally, it has no area. This
                        // intentionally bypasses the per-submesh floor: a zero-area triangle
                        // is dropped by BuildResult regardless, so keeping it alive here would
                        // not preserve a nonempty submesh - a submesh whose last live triangle
                        // degenerates is allowed to empty (the Unity adapter tolerates empty slots).
                        KillTriangle(t, ignoreSubmeshFloor: true);
                        killed = true;
                        continue;
                    }
                    long key = u < w ? PairKey(u, w) : PairKey(w, u);
                    if (_dupBuf.ContainsKey(key))
                    {
                        if (KillTriangle(t, ignoreSubmeshFloor: false)) killed = true;
                    }
                    else
                    {
                        _dupBuf[key] = t;
                    }
                }

                if (killed)
                {
                    write = 0;
                    for (int read = 0; read < list.Count; read++)
                    {
                        int t = list[read];
                        if (_tAlive[t]) list[write++] = t;
                    }
                    if (write < list.Count) list.RemoveRange(write, list.Count - write);
                }
            }

            private void OtherTwo(int t, int s, out int u, out int w)
            {
                int a = _t0[t], b = _t1[t], c = _t2[t];
                if (a == s) { u = b; w = c; }
                else if (b == s) { u = a; w = c; }
                else { u = a; w = b; }
            }

            private bool KillTriangle(int t, bool ignoreSubmeshFloor)
            {
                if (!_tAlive[t]) return false;
                int sub = _tSub[t];
                if (!ignoreSubmeshFloor && _subLive[sub] <= 1) return false; // keep >=1 per submesh
                _tAlive[t] = false;
                _liveTri--;
                _subLive[sub]--;
                return true;
            }

            // Standard link condition: the only vertices adjacent to BOTH endpoints must
            // be the third vertices of the shared triangle(s). Any extra common neighbour
            // would fold the mesh non-manifoldly.
            private bool LinkConditionOK(int a, int b)
            {
                _stamp++;
                int sa = _stamp;
                List<int> la = _vTris[a];
                for (int i = 0; i < la.Count; i++)
                {
                    int t = la[i];
                    if (!_tAlive[t]) continue;
                    MarkOtherTwo(t, a, sa);
                }

                // Overwrite third vertices of shared triangles with a distinct stamp.
                _stamp++;
                int st = _stamp;
                for (int i = 0; i < la.Count; i++)
                {
                    int t = la[i];
                    if (!_tAlive[t] || !TriContains(t, b)) continue;
                    int c = ThirdVertex(t, a, b);
                    if (c >= 0) _vStamp[c] = st;
                }

                List<int> lb = _vTris[b];
                for (int i = 0; i < lb.Count; i++)
                {
                    int t = lb[i];
                    if (!_tAlive[t]) continue;
                    if (CheckLinkVert(_t0[t], a, b, sa)) return false;
                    if (CheckLinkVert(_t1[t], a, b, sa)) return false;
                    if (CheckLinkVert(_t2[t], a, b, sa)) return false;
                }
                return true;
            }

            private void MarkOtherTwo(int t, int self, int stampVal)
            {
                if (_t0[t] != self) _vStamp[_t0[t]] = stampVal;
                if (_t1[t] != self) _vStamp[_t1[t]] = stampVal;
                if (_t2[t] != self) _vStamp[_t2[t]] = stampVal;
            }

            // Returns true if vertex v (a neighbour of b) is a common neighbour of a that
            // is NOT a shared third vertex -> link condition violated.
            private bool CheckLinkVert(int v, int a, int b, int sa)
            {
                if (v == b || v == a) return false;
                return _vStamp[v] == sa; // marked as neighbour-of-a but not as shared third vertex
            }

            private int ThirdVertex(int t, int a, int b)
            {
                int x = _t0[t], y = _t1[t], z = _t2[t];
                if (x != a && x != b) return x;
                if (y != a && y != b) return y;
                if (z != a && z != b) return z;
                return -1;
            }

            // Reject if any surviving triangle incident to r flips its normal (or becomes
            // a sliver) when r is relocated onto s.
            private bool NormalFlipOK(int s, int r)
            {
                List<int> lr = _vTris[r];
                Double3 ps = _pos[s];
                for (int i = 0; i < lr.Count; i++)
                {
                    int t = lr[i];
                    if (!_tAlive[t]) continue;
                    if (TriContains(t, s)) continue; // dying triangle, ignore

                    int a = _t0[t], b = _t1[t], c = _t2[t];
                    Double3 pa = _pos[a], pb = _pos[b], pc = _pos[c];
                    Double3 nBefore = Double3.Cross(pb - pa, pc - pa);
                    double lenB = nBefore.Length;
                    if (lenB < 1e-20) continue; // already degenerate; nothing to preserve

                    if (a == r) pa = ps; else if (b == r) pb = ps; else if (c == r) pc = ps;
                    Double3 nAfter = Double3.Cross(pb - pa, pc - pa);
                    double lenA = nAfter.Length;
                    if (lenA < 1e-20) return false; // would create a sliver / zero-area face

                    double cosang = Double3.Dot(nBefore, nAfter) / (lenB * lenA);
                    if (cosang < 0.1) return false; // normal flip / near-flip
                }
                return true;
            }

            // =================================================================
            // Binary min-heap (array based). Order: cost asc, then a asc, then b asc.
            // =================================================================
            private void Push(double cost, int a, int b, int edge, int ver)
            {
                if (_hSize >= _hCost.Length) GrowHeap();
                int i = _hSize++;
                _hCost[i] = cost; _hA[i] = a; _hB[i] = b; _hEdge[i] = edge; _hVer[i] = ver;
                SiftUp(i);
            }

            private void PopMin(out double cost, out int a, out int b, out int edge, out int ver)
            {
                cost = _hCost[0]; a = _hA[0]; b = _hB[0]; edge = _hEdge[0]; ver = _hVer[0];
                int last = --_hSize;
                if (last > 0)
                {
                    _hCost[0] = _hCost[last]; _hA[0] = _hA[last]; _hB[0] = _hB[last];
                    _hEdge[0] = _hEdge[last]; _hVer[0] = _hVer[last];
                    SiftDown(0);
                }
            }

            private void SiftUp(int i)
            {
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (!Less(i, parent)) break;
                    SwapHeap(i, parent);
                    i = parent;
                }
            }

            private void SiftDown(int i)
            {
                for (; ; )
                {
                    int l = 2 * i + 1;
                    int rr = l + 1;
                    int smallest = i;
                    if (l < _hSize && Less(l, smallest)) smallest = l;
                    if (rr < _hSize && Less(rr, smallest)) smallest = rr;
                    if (smallest == i) break;
                    SwapHeap(i, smallest);
                    i = smallest;
                }
            }

            private bool Less(int i, int j)
            {
                if (_hCost[i] != _hCost[j]) return _hCost[i] < _hCost[j];
                if (_hA[i] != _hA[j]) return _hA[i] < _hA[j];
                if (_hB[i] != _hB[j]) return _hB[i] < _hB[j];
                return _hEdge[i] < _hEdge[j];
            }

            private void SwapHeap(int i, int j)
            {
                double c = _hCost[i]; _hCost[i] = _hCost[j]; _hCost[j] = c;
                int a = _hA[i]; _hA[i] = _hA[j]; _hA[j] = a;
                int b = _hB[i]; _hB[i] = _hB[j]; _hB[j] = b;
                int e = _hEdge[i]; _hEdge[i] = _hEdge[j]; _hEdge[j] = e;
                int v = _hVer[i]; _hVer[i] = _hVer[j]; _hVer[j] = v;
            }

            private void GrowHeap()
            {
                int n = _hCost.Length * 2;
                Array.Resize(ref _hCost, n);
                Array.Resize(ref _hA, n);
                Array.Resize(ref _hB, n);
                Array.Resize(ref _hEdge, n);
                Array.Resize(ref _hVer, n);
            }

            // =================================================================
            // Final cleanup + result assembly.
            // =================================================================
            private void CleanupPasses()
            {
                int passes = _opt.maxDegeneratePasses;
                if (passes < 1) passes = 1;

                for (int pass = 0; pass < passes; pass++)
                {
                    bool changed = false;

                    // Degenerate triangles (repeated vertex).
                    for (int t = 0; t < _triCount; t++)
                    {
                        if (!_tAlive[t]) continue;
                        int a = _t0[t], b = _t1[t], c = _t2[t];
                        if (a == b || b == c || a == c)
                        {
                            // サブメッシュ床(元々非空のスロットは空にしない)を collapse ループと同様に守る。
                            // 最後の生存三角形が退化していても、退化1枚を残してスロット消滅を防ぐ。
                            if (_subLive[_tSub[t]] <= 1) continue;
                            _tAlive[t] = false;
                            _liveTri--;
                            _subLive[_tSub[t]]--;
                            changed = true;
                        }
                    }

                    // Duplicate faces (identical sorted vertex triple within a submesh).
                    if (_v > 0)
                    {
                        var seen = new Dictionary<long, int>(_liveTri + 8);
                        long vlong = _v;
                        for (int t = 0; t < _triCount; t++)
                        {
                            if (!_tAlive[t]) continue;
                            int a = _t0[t], b = _t1[t], c = _t2[t];
                            // sort a<=b<=c
                            if (a > b) { int tmp = a; a = b; b = tmp; }
                            if (b > c) { int tmp = b; b = c; c = tmp; }
                            if (a > b) { int tmp = a; a = b; b = tmp; }
                            long key = ((a * vlong + b) * vlong + c) * (_subCount + 1) + _tSub[t];
                            if (seen.ContainsKey(key))
                            {
                                if (_subLive[_tSub[t]] > 1)
                                {
                                    _tAlive[t] = false;
                                    _liveTri--;
                                    _subLive[_tSub[t]]--;
                                    changed = true;
                                }
                            }
                            else
                            {
                                seen[key] = t;
                            }
                        }
                    }

                    if (!changed) break;
                }
            }

            private DecimationResult BuildResult()
            {
                // Collect used original vertex ids (ascending) -> new index.
                int[] oldToNew = new int[_v];
                for (int i = 0; i < _v; i++) oldToNew[i] = -1;

                _stamp++;
                int used = _stamp;
                for (int t = 0; t < _triCount; t++)
                {
                    if (!_tAlive[t]) continue;
                    _vStamp[_t0[t]] = used;
                    _vStamp[_t1[t]] = used;
                    _vStamp[_t2[t]] = used;
                }

                int keptCount = 0;
                for (int i = 0; i < _v; i++)
                    if (_vStamp[i] == used) keptCount++;

                int[] kept = new int[keptCount];
                int ni = 0;
                for (int i = 0; i < _v; i++)
                {
                    if (_vStamp[i] != used) continue;
                    kept[ni] = i;
                    oldToNew[i] = ni;
                    ni++;
                }

                // Per-submesh index lists (preserve original submesh order/count).
                var subLists = new List<int>[_subCount];
                for (int s = 0; s < _subCount; s++)
                    subLists[s] = new List<int>(Math.Max(3, _subLive[s] * 3));

                int liveCheck = 0;
                for (int t = 0; t < _triCount; t++)
                {
                    if (!_tAlive[t]) continue;
                    int a = _t0[t], b = _t1[t], c = _t2[t];
                    if (a == b || b == c || a == c) continue; // safety
                    int na = oldToNew[a], nb = oldToNew[b], nc = oldToNew[c];
                    if (na < 0 || nb < 0 || nc < 0) continue;  // safety
                    List<int> list = subLists[_tSub[t]];
                    list.Add(na); list.Add(nb); list.Add(nc);
                    liveCheck++;
                }

                int[][] outIdx = new int[_subCount][];
                for (int s = 0; s < _subCount; s++)
                    outIdx[s] = subLists[s].ToArray();

                return new DecimationResult
                {
                    keptVertices = kept,
                    submeshIndices = outIdx,
                    triangleCount = liveCheck
                };
            }
        }
    }
}
