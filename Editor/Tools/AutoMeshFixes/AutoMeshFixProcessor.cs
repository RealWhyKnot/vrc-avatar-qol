// AutoMeshFixProcessor.cs
//
// Non-destructive mesh/blendshape generation for AutoTightenToBody.
// The processor only assigns temporary in-memory mesh clones to renderers.
// Source FBX/model meshes and permanent assets are never written.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.Tools.AutoMeshFixes {

    internal static class AutoMeshFixProcessor {

        internal sealed class Options {
            public bool Upload;
            public bool PlayMode;
            public bool Preview;
            public bool DestroyStorageComponents;
            public bool Verbose;
        }

        internal sealed class Result {
            public readonly Session Session = new Session();
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public int ComponentsProcessed;
            public int GarmentShapesCreated;
            public int BodyShapesCreated;
            public int MeshesCloned;

            public bool Success => Errors.Count == 0;

            public string Summary {
                get {
                    if (ComponentsProcessed == 0) return "No Auto Mesh Fix components found.";
                    return $"Processed {ComponentsProcessed} setup(s), cloned {MeshesCloned} mesh(es), generated {GarmentShapesCreated + BodyShapesCreated} blendshape(s).";
                }
            }
        }

        internal sealed class Session {
            private readonly Dictionary<SkinnedMeshRenderer, RendererState> _states =
                new Dictionary<SkinnedMeshRenderer, RendererState>();
            private readonly List<UnityEngine.Object> _generatedObjects = new List<UnityEngine.Object>();

            internal bool HasChanges => _states.Count > 0 || _generatedObjects.Count > 0;

            internal void CaptureRenderer(SkinnedMeshRenderer renderer) {
                if (renderer == null || _states.ContainsKey(renderer)) return;
                _states[renderer] = new RendererState(renderer);
            }

            internal void AddGenerated(UnityEngine.Object obj) {
                if (obj != null) _generatedObjects.Add(obj);
            }

            internal void Merge(Session other) {
                if (other == null) return;
                foreach (var kv in other._states) {
                    if (kv.Key != null && !_states.ContainsKey(kv.Key)) _states[kv.Key] = kv.Value;
                }
                _generatedObjects.AddRange(other._generatedObjects.Where(o => o != null));
            }

            internal void Restore() {
                foreach (var state in _states.Values) state.Restore();
                _states.Clear();

                for (int i = _generatedObjects.Count - 1; i >= 0; i--) {
                    var obj = _generatedObjects[i];
                    if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
                }
                _generatedObjects.Clear();
            }
        }

        private sealed class RendererState {
            private readonly SkinnedMeshRenderer _renderer;
            private readonly Mesh _mesh;
            private readonly Dictionary<string, float> _weightsByName;

            public RendererState(SkinnedMeshRenderer renderer) {
                _renderer = renderer;
                _mesh = renderer != null ? renderer.sharedMesh : null;
                _weightsByName = CaptureWeights(renderer, _mesh);
            }

            public void Restore() {
                if (_renderer == null) return;
                _renderer.sharedMesh = _mesh;
                RestoreWeights(_renderer, _mesh, _weightsByName);
            }
        }

        private sealed class ProcessorState {
            public readonly Result Result = new Result();
            public readonly Dictionary<SkinnedMeshRenderer, Mesh> GeneratedMeshes =
                new Dictionary<SkinnedMeshRenderer, Mesh>();
        }

        internal static Result ProcessAvatar(GameObject avatarRoot, Options options) {
            options = options ?? new Options();
            var state = new ProcessorState();
            if (avatarRoot == null) {
                state.Result.Errors.Add("No avatar root was provided.");
                return state.Result;
            }

            var components = avatarRoot.GetComponentsInChildren<AutoTightenToBody>(true)
                .Where(c => ShouldProcess(c, options))
                .ToList();
            foreach (var component in components) {
                ProcessComponent(component, options, state);
            }

            if (options.DestroyStorageComponents) {
                foreach (var component in components) {
                    if (component != null) UnityEngine.Object.DestroyImmediate(component);
                }
            }

            return state.Result;
        }

        private static bool ShouldProcess(AutoTightenToBody component, Options options) {
            if (component == null || !component.enabled) return false;
            if (options.Upload && !component.processOnUpload) return false;
            if (options.PlayMode && !component.processInPlayMode) return false;
            return true;
        }

        private static void ProcessComponent(AutoTightenToBody component, Options options, ProcessorState state) {
            var result = state.Result;
            if (!Validate(component, result)) return;

            result.ComponentsProcessed++;
            try {
                if (component.createGarmentTightenShape) {
                    if (GenerateGarmentTighten(component, state)) {
                        result.GarmentShapesCreated++;
                    }
                }
                if (component.createBodyHideShape) {
                    if (GenerateBodyHide(component, state)) {
                        result.BodyShapesCreated++;
                    }
                }
            } catch (Exception ex) {
                result.Errors.Add($"{component.name}: {ex.Message}");
                Debug.LogException(ex);
            }
        }

        private static bool Validate(AutoTightenToBody component, Result result) {
            if (component.garmentRenderer == null) {
                result.Errors.Add($"{component.name}: choose the clothing/garment SkinnedMeshRenderer.");
                return false;
            }
            if (component.bodyRenderer == null) {
                result.Errors.Add($"{component.name}: choose the body SkinnedMeshRenderer.");
                return false;
            }
            if (component.garmentRenderer.sharedMesh == null) {
                result.Errors.Add($"{component.garmentRenderer.name}: garment renderer has no mesh.");
                return false;
            }
            if (component.bodyRenderer.sharedMesh == null) {
                result.Errors.Add($"{component.bodyRenderer.name}: body renderer has no mesh.");
                return false;
            }
            if (!component.garmentRenderer.sharedMesh.isReadable) {
                result.Errors.Add($"{component.garmentRenderer.name}: garment mesh is not readable. Enable Read/Write on the model import.");
                return false;
            }
            if (!component.bodyRenderer.sharedMesh.isReadable) {
                result.Errors.Add($"{component.bodyRenderer.name}: body mesh is not readable. Enable Read/Write on the model import.");
                return false;
            }
            if (!component.createGarmentTightenShape && !component.createBodyHideShape) {
                result.Warnings.Add($"{component.name}: both generated blendshapes are disabled.");
                return false;
            }
            return true;
        }

        private static bool GenerateGarmentTighten(AutoTightenToBody component, ProcessorState state) {
            var garmentRenderer = component.garmentRenderer;
            var bodyRenderer = component.bodyRenderer;
            var garmentMesh = ResolveGeneratedMesh(garmentRenderer, state);
            var bodyMesh = ResolveGeneratedMesh(bodyRenderer, state);
            if (garmentMesh == null || bodyMesh == null) return false;

            var bodySkin = new SkinningCache(bodyRenderer, bodyMesh);
            var bodyIndex = SurfaceIndex.Build(bodyRenderer, bodyMesh, bodySkin,
                Mathf.Max(0.01f, component.maxProjectionDistance * 0.5f));
            if (bodyIndex.TriangleCount == 0) {
                state.Result.Errors.Add($"{bodyRenderer.name}: body mesh has no triangles to project against.");
                return false;
            }

            var garmentSkin = new SkinningCache(garmentRenderer, garmentMesh);
            var verts = garmentMesh.vertices;
            var delta = new Vector3[garmentMesh.vertexCount];
            int moved = 0;
            float search = Mathf.Max(0.001f, component.maxProjectionDistance);

            for (int v = 0; v < verts.Length; v++) {
                if (!VertexSelected(garmentMesh, component.selectionMode, v)) continue;
                var world = garmentSkin.ToWorldPoint(v);
                if (!bodyIndex.TryFindNearest(world, search, out var hit)) continue;

                var targetWorld = hit.Point + hit.Normal * Mathf.Max(0f, component.garmentSurfaceOffset);
                var targetLocal = garmentSkin.WorldToMeshLocal(v, targetWorld);
                var d = targetLocal - verts[v];
                if (d.sqrMagnitude <= 0.0000000001f) continue;
                delta[v] = d;
                moved++;
            }

            if (moved == 0) {
                state.Result.Warnings.Add($"{garmentRenderer.name}: no garment vertices were close enough to the body to tighten.");
                return false;
            }

            var shapeName = CleanShapeName(component.garmentTightenBlendShapeName, "AUTO_TightenToBody");
            var savedWeights = CaptureWeights(garmentRenderer, garmentMesh);
            AddOrReplaceBlendShape(garmentMesh, shapeName, delta);
            garmentMesh.RecalculateBounds();
            RestoreWeights(garmentRenderer, garmentMesh, savedWeights);
            if (component.setGarmentTightenWeightTo100) {
                SetBlendShapeWeight(garmentRenderer, garmentMesh, shapeName, 100f);
            }
            return true;
        }

        private static bool GenerateBodyHide(AutoTightenToBody component, ProcessorState state) {
            var garmentRenderer = component.garmentRenderer;
            var bodyRenderer = component.bodyRenderer;
            var garmentMesh = ResolveGeneratedMesh(garmentRenderer, state);
            var bodyMesh = ResolveGeneratedMesh(bodyRenderer, state);
            if (garmentMesh == null || bodyMesh == null) return false;

            var garmentSkin = new SkinningCache(garmentRenderer, garmentMesh);
            var garmentIndex = SurfaceIndex.Build(garmentRenderer, garmentMesh, garmentSkin,
                Mathf.Max(0.01f, component.bodyHideRadius));
            if (garmentIndex.TriangleCount == 0) {
                state.Result.Errors.Add($"{garmentRenderer.name}: garment mesh has no triangles to use for body hiding.");
                return false;
            }

            var bodySkin = new SkinningCache(bodyRenderer, bodyMesh);
            var verts = bodyMesh.vertices;
            var delta = new Vector3[bodyMesh.vertexCount];
            float radius = Mathf.Max(0.001f, component.bodyHideRadius);
            float depth = Mathf.Max(0f, component.bodyHideDepth);
            int moved = 0;

            for (int v = 0; v < verts.Length; v++) {
                var world = bodySkin.ToWorldPoint(v);
                if (!garmentIndex.TryFindNearest(world, radius, out _)) continue;

                var normal = bodySkin.ToWorldNormal(v);
                if (normal.sqrMagnitude < 0.0001f) normal = bodyRenderer.transform.up;
                var targetWorld = BuildBodyHideTarget(component.bodyHideMode, bodyRenderer, bodySkin, v, world, normal, depth);
                var targetLocal = bodySkin.WorldToMeshLocal(v, targetWorld);
                var d = targetLocal - verts[v];
                if (d.sqrMagnitude <= 0.0000000001f) continue;
                delta[v] = d;
                moved++;
            }

            if (moved == 0) {
                state.Result.Warnings.Add($"{bodyRenderer.name}: no body vertices were close enough to the garment to hide.");
                return false;
            }

            var shapeName = CleanShapeName(component.bodyHideBlendShapeName, "AUTO_HideBodyUnderGarment");
            var savedWeights = CaptureWeights(bodyRenderer, bodyMesh);
            AddOrReplaceBlendShape(bodyMesh, shapeName, delta);
            bodyMesh.RecalculateBounds();
            RestoreWeights(bodyRenderer, bodyMesh, savedWeights);
            if (component.setBodyHideWeightTo100) {
                SetBlendShapeWeight(bodyRenderer, bodyMesh, shapeName, 100f);
            }
            return true;
        }

        private static Vector3 BuildBodyHideTarget(
            BodyHideMode mode,
            SkinnedMeshRenderer renderer,
            SkinningCache skin,
            int vertexIndex,
            Vector3 world,
            Vector3 normal,
            float depth) {
            switch (mode) {
                case BodyHideMode.CollapseToRendererRoot: {
                    var center = renderer.rootBone != null ? renderer.rootBone.position : renderer.transform.position;
                    var dir = center - world;
                    return dir.sqrMagnitude < 0.0001f ? world - normal.normalized * depth : world + dir.normalized * depth;
                }
                case BodyHideMode.CollapseToNearestBone: {
                    var bone = skin.PrimaryBoneTransform(vertexIndex);
                    var center = bone != null ? bone.position : (renderer.rootBone != null ? renderer.rootBone.position : renderer.transform.position);
                    var dir = center - world;
                    return dir.sqrMagnitude < 0.0001f ? world - normal.normalized * depth : world + dir.normalized * depth;
                }
                default:
                    return world - normal.normalized * depth;
            }
        }

        private static Mesh ResolveGeneratedMesh(SkinnedMeshRenderer renderer, ProcessorState state) {
            if (renderer == null || renderer.sharedMesh == null) return null;
            if (state.GeneratedMeshes.TryGetValue(renderer, out var existing) && existing != null) return existing;

            var original = renderer.sharedMesh;
            state.Result.Session.CaptureRenderer(renderer);

            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = original.name + " (Avatar QoL Generated)";
            clone.hideFlags = HideFlags.DontSaveInEditor;
            state.Result.Session.AddGenerated(clone);
            state.GeneratedMeshes[renderer] = clone;
            state.Result.MeshesCloned++;

            var savedWeights = CaptureWeights(renderer, original);
            renderer.sharedMesh = clone;
            RestoreWeights(renderer, clone, savedWeights);
            return clone;
        }

        private static Dictionary<string, float> CaptureWeights(SkinnedMeshRenderer renderer, Mesh mesh) {
            var output = new Dictionary<string, float>();
            if (renderer == null || mesh == null) return output;
            for (int i = 0; i < mesh.blendShapeCount; i++) {
                output[mesh.GetBlendShapeName(i)] = renderer.GetBlendShapeWeight(i);
            }
            return output;
        }

        private static void RestoreWeights(SkinnedMeshRenderer renderer, Mesh mesh, Dictionary<string, float> weightsByName) {
            if (renderer == null || mesh == null || weightsByName == null) return;
            foreach (var kv in weightsByName) {
                int index = mesh.GetBlendShapeIndex(kv.Key);
                if (index >= 0) renderer.SetBlendShapeWeight(index, kv.Value);
            }
        }

        private static void SetBlendShapeWeight(SkinnedMeshRenderer renderer, Mesh mesh, string shapeName, float weight) {
            if (renderer == null || mesh == null) return;
            int index = mesh.GetBlendShapeIndex(shapeName);
            if (index >= 0) renderer.SetBlendShapeWeight(index, weight);
        }

        private static bool VertexSelected(Mesh mesh, VertexSelectionMode mode, int vertexIndex) {
            if (mode == VertexSelectionMode.AllVertices) return true;
            var colors = mesh.colors32;
            if (colors == null || colors.Length != mesh.vertexCount || vertexIndex < 0 || vertexIndex >= colors.Length) return false;
            var c = colors[vertexIndex];
            switch (mode) {
                case VertexSelectionMode.VertexColorRed: return c.r > 16;
                case VertexSelectionMode.VertexColorGreen: return c.g > 16;
                case VertexSelectionMode.VertexColorBlue: return c.b > 16;
                case VertexSelectionMode.VertexColorAlpha: return c.a > 16;
                default: return true;
            }
        }

        private static string CleanShapeName(string shapeName, string fallback) {
            return string.IsNullOrWhiteSpace(shapeName) ? fallback : shapeName.Trim();
        }

        private sealed class BlendShapeSnapshot {
            public string Name;
            public readonly List<float> Weights = new List<float>();
            public readonly List<Vector3[]> DeltaVertices = new List<Vector3[]>();
            public readonly List<Vector3[]> DeltaNormals = new List<Vector3[]>();
            public readonly List<Vector3[]> DeltaTangents = new List<Vector3[]>();
        }

        private static void AddOrReplaceBlendShape(Mesh mesh, string shapeName, Vector3[] deltaVertices) {
            var snapshots = new List<BlendShapeSnapshot>();
            int vertexCount = mesh.vertexCount;
            for (int i = 0; i < mesh.blendShapeCount; i++) {
                string existingName = mesh.GetBlendShapeName(i);
                if (string.Equals(existingName, shapeName, StringComparison.Ordinal)) continue;
                var snap = new BlendShapeSnapshot { Name = existingName };
                int frames = mesh.GetBlendShapeFrameCount(i);
                for (int f = 0; f < frames; f++) {
                    var dv = new Vector3[vertexCount];
                    var dn = new Vector3[vertexCount];
                    var dt = new Vector3[vertexCount];
                    mesh.GetBlendShapeFrameVertices(i, f, dv, dn, dt);
                    snap.Weights.Add(mesh.GetBlendShapeFrameWeight(i, f));
                    snap.DeltaVertices.Add(dv);
                    snap.DeltaNormals.Add(dn);
                    snap.DeltaTangents.Add(dt);
                }
                snapshots.Add(snap);
            }

            mesh.ClearBlendShapes();
            foreach (var snap in snapshots) {
                for (int f = 0; f < snap.Weights.Count; f++) {
                    mesh.AddBlendShapeFrame(
                        snap.Name,
                        snap.Weights[f],
                        snap.DeltaVertices[f],
                        snap.DeltaNormals[f],
                        snap.DeltaTangents[f]);
                }
            }

            mesh.AddBlendShapeFrame(shapeName, 100f, deltaVertices, null, null);
        }

        private sealed class SkinningCache {
            private readonly SkinnedMeshRenderer _renderer;
            private readonly Mesh _mesh;
            private readonly Transform[] _bones;
            private readonly Matrix4x4[] _bindposes;
            private readonly Vector3[] _vertices;
            private readonly Vector3[] _normals;
            private readonly BoneWeight1[] _weights;
            private readonly byte[] _bonesPerVertex;
            private readonly int[] _weightStart;
            private readonly int[] _primaryBone;

            public SkinningCache(SkinnedMeshRenderer renderer, Mesh mesh) {
                _renderer = renderer;
                _mesh = mesh;
                _bones = renderer != null ? renderer.bones : Array.Empty<Transform>();
                _bindposes = mesh != null ? mesh.bindposes : Array.Empty<Matrix4x4>();
                _vertices = mesh != null ? mesh.vertices : Array.Empty<Vector3>();
                _normals = mesh != null && mesh.normals != null && mesh.normals.Length == mesh.vertexCount
                    ? mesh.normals
                    : Enumerable.Repeat(Vector3.up, mesh != null ? mesh.vertexCount : 0).ToArray();

                var allWeights = mesh != null ? mesh.GetAllBoneWeights() : default(NativeArray<BoneWeight1>);
                var bpv = mesh != null ? mesh.GetBonesPerVertex() : default(NativeArray<byte>);
                _weights = allWeights.IsCreated ? allWeights.ToArray() : Array.Empty<BoneWeight1>();
                _bonesPerVertex = bpv.IsCreated ? bpv.ToArray() : Array.Empty<byte>();
                _weightStart = new int[_bonesPerVertex.Length];
                _primaryBone = new int[_bonesPerVertex.Length];

                int cursor = 0;
                for (int v = 0; v < _bonesPerVertex.Length; v++) {
                    _weightStart[v] = cursor;
                    int count = _bonesPerVertex[v];
                    int primary = -1;
                    float best = 0f;
                    for (int w = 0; w < count && cursor + w < _weights.Length; w++) {
                        var bw = _weights[cursor + w];
                        if (bw.boneIndex < 0 || bw.boneIndex >= _bones.Length || _bones[bw.boneIndex] == null) continue;
                        if (bw.weight > best) {
                            primary = bw.boneIndex;
                            best = bw.weight;
                        }
                    }
                    _primaryBone[v] = primary;
                    cursor += count;
                }
            }

            public Vector3 ToWorldPoint(int vertexIndex) {
                if (!HasUsableWeights(vertexIndex)) return _renderer.transform.TransformPoint(_vertices[vertexIndex]);
                var output = Vector3.zero;
                int start = _weightStart[vertexIndex];
                int count = _bonesPerVertex[vertexIndex];
                float total = 0f;
                for (int w = 0; w < count && start + w < _weights.Length; w++) {
                    var bw = _weights[start + w];
                    if (!UsableBone(bw.boneIndex)) continue;
                    output += _bones[bw.boneIndex].TransformPoint(_bindposes[bw.boneIndex].MultiplyPoint3x4(_vertices[vertexIndex])) * bw.weight;
                    total += bw.weight;
                }
                return total > 0.0001f ? output / total : _renderer.transform.TransformPoint(_vertices[vertexIndex]);
            }

            public Vector3 ToWorldNormal(int vertexIndex) {
                var localNormal = _normals[vertexIndex];
                if (!HasUsableWeights(vertexIndex)) return _renderer.transform.TransformDirection(localNormal).normalized;

                var output = Vector3.zero;
                int start = _weightStart[vertexIndex];
                int count = _bonesPerVertex[vertexIndex];
                for (int w = 0; w < count && start + w < _weights.Length; w++) {
                    var bw = _weights[start + w];
                    if (!UsableBone(bw.boneIndex)) continue;
                    output += _bones[bw.boneIndex].TransformDirection(_bindposes[bw.boneIndex].MultiplyVector(localNormal)) * bw.weight;
                }
                return output.sqrMagnitude > 0.0001f ? output.normalized : _renderer.transform.TransformDirection(localNormal).normalized;
            }

            public Vector3 WorldToMeshLocal(int vertexIndex, Vector3 world) {
                int boneIndex = vertexIndex >= 0 && vertexIndex < _primaryBone.Length ? _primaryBone[vertexIndex] : -1;
                if (UsableBone(boneIndex)) {
                    var boneLocal = _bones[boneIndex].InverseTransformPoint(world);
                    return _bindposes[boneIndex].inverse.MultiplyPoint3x4(boneLocal);
                }
                return _renderer.transform.InverseTransformPoint(world);
            }

            public Transform PrimaryBoneTransform(int vertexIndex) {
                int boneIndex = vertexIndex >= 0 && vertexIndex < _primaryBone.Length ? _primaryBone[vertexIndex] : -1;
                return UsableBone(boneIndex) ? _bones[boneIndex] : null;
            }

            private bool HasUsableWeights(int vertexIndex) {
                return _renderer != null &&
                       _vertices != null &&
                       vertexIndex >= 0 &&
                       vertexIndex < _vertices.Length &&
                       vertexIndex < _bonesPerVertex.Length &&
                       _bonesPerVertex[vertexIndex] > 0 &&
                       _weights.Length > 0 &&
                       _bones != null &&
                       _bindposes != null;
            }

            private bool UsableBone(int boneIndex) {
                return boneIndex >= 0 &&
                       _bones != null &&
                       boneIndex < _bones.Length &&
                       _bones[boneIndex] != null &&
                       _bindposes != null &&
                       boneIndex < _bindposes.Length;
            }
        }

        private struct SurfaceHit {
            public Vector3 Point;
            public Vector3 Normal;
        }

        private struct SurfaceTriangle {
            public Vector3 A;
            public Vector3 B;
            public Vector3 C;
            public Vector3 Normal;
        }

        private sealed class SurfaceIndex {
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
}
