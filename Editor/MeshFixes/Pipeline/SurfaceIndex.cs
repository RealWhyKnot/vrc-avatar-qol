// SurfaceIndex.cs
//
// Spatial-hash triangle lookup used by mesh-fix operations that need to
// find the nearest surface point on a body or garment mesh. Built once
// per mesh per pipeline run, then queried many times.
//
// Query API: TryFindNearest(world, radius, out hit) returns true with
// the nearest point on any triangle within radius, false otherwise.
//
// Memory: triangle deltas live in a single flat list; cells point into it
// by index. Worst-case fanout is O(triangles in radius cube around point),
// not O(triangles in mesh), which keeps body-hide / garment-tighten scans
// linear in the number of vertices being projected.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal struct SurfaceHit {
        public Vector3 Point;
        public Vector3 Normal;
    }

    internal struct SurfaceTriangle {
        public Vector3 A;
        public Vector3 B;
        public Vector3 C;
        public Vector3 Normal;
    }

    internal sealed class SurfaceIndex {

        private readonly float _cellSize;
        private readonly List<SurfaceTriangle> _triangles = new List<SurfaceTriangle>();
        private readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();
        private int[] _seen;
        private int _queryToken;

        public int TriangleCount => _triangles.Count;

        private SurfaceIndex(float cellSize) {
            _cellSize = Mathf.Max(0.005f, cellSize);
        }

        public static SurfaceIndex Build(SkinnedMeshRenderer renderer, Mesh mesh, SkinningCache skin, float cellSize) {
            var index = new SurfaceIndex(cellSize);
            if (renderer == null || mesh == null || skin == null) return index;

            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) {
                var tris = mesh.GetTriangles(submesh);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    int a = tris[i];
                    int b = tris[i + 1];
                    int c = tris[i + 2];
                    if (a < 0 || b < 0 || c < 0 ||
                        a >= mesh.vertexCount || b >= mesh.vertexCount || c >= mesh.vertexCount) continue;

                    var p0 = skin.ToWorldPoint(a);
                    var p1 = skin.ToWorldPoint(b);
                    var p2 = skin.ToWorldPoint(c);
                    var normal = Vector3.Cross(p1 - p0, p2 - p0);
                    if (normal.sqrMagnitude < 0.00000001f) continue;
                    index.Add(new SurfaceTriangle {
                        A = p0,
                        B = p1,
                        C = p2,
                        Normal = normal.normalized,
                    });
                }
            }
            index._seen = new int[index._triangles.Count];
            return index;
        }

        private void Add(SurfaceTriangle tri) {
            int idx = _triangles.Count;
            _triangles.Add(tri);
            var min = Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C));
            var max = Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C));
            var cMin = Cell(min);
            var cMax = Cell(max);
            for (int x = cMin.x; x <= cMax.x; x++) {
                for (int y = cMin.y; y <= cMax.y; y++) {
                    for (int z = cMin.z; z <= cMax.z; z++) {
                        var cell = new Vector3Int(x, y, z);
                        if (!_cells.TryGetValue(cell, out var list)) {
                            list = new List<int>();
                            _cells[cell] = list;
                        }
                        list.Add(idx);
                    }
                }
            }
        }

        public bool TryFindNearest(Vector3 point, float radius, out SurfaceHit hit) {
            hit = default;
            if (_triangles.Count == 0 || radius <= 0f) return false;

            _queryToken++;
            if (_queryToken == int.MaxValue) {
                Array.Clear(_seen, 0, _seen.Length);
                _queryToken = 1;
            }

            float bestSqr = radius * radius;
            bool found = false;
            int r = Mathf.CeilToInt(radius / _cellSize);
            var center = Cell(point);
            for (int x = center.x - r; x <= center.x + r; x++) {
                for (int y = center.y - r; y <= center.y + r; y++) {
                    for (int z = center.z - r; z <= center.z + r; z++) {
                        if (!_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                        foreach (var idx in list) {
                            if (idx < 0 || idx >= _triangles.Count) continue;
                            if (_seen[idx] == _queryToken) continue;
                            _seen[idx] = _queryToken;

                            var tri = _triangles[idx];
                            var nearest = ClosestPointOnTriangle(point, tri.A, tri.B, tri.C);
                            float sqr = (nearest - point).sqrMagnitude;
                            if (sqr > bestSqr) continue;
                            bestSqr = sqr;
                            found = true;
                            hit = new SurfaceHit {
                                Point = nearest,
                                Normal = tri.Normal,
                            };
                        }
                    }
                }
            }
            return found;
        }

        private Vector3Int Cell(Vector3 p) {
            return new Vector3Int(
                Mathf.FloorToInt(p.x / _cellSize),
                Mathf.FloorToInt(p.y / _cellSize),
                Mathf.FloorToInt(p.z / _cellSize));
        }

        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
                float v = d1 / (d1 - d3);
                return a + v * ab;
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
                float w = d2 / (d2 - d6);
                return a + w * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + w * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float v2 = vb * denom;
            float w2 = vc * denom;
            return a + ab * v2 + ac * w2;
        }
    }
}
