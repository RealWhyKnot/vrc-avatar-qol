// SkinningCache.cs
//
// Per-renderer cached read of bone weights + bindposes with helpers to
// convert vertex indices into world-space points and normals (and back).
//
// Math: world = sum_i ( w_i * bones[b_i].localToWorld * bindposes[b_i] * v_local )
// renormalised by total weight. This is standard Unity skinning math and
// is what mesh tools should use to find a vertex's actual world position
// regardless of how the renderer transform is set up.
//
// Do NOT use renderer.transform.TransformPoint(verts[v]) -- wrong on rigs
// where the renderer GameObject does not sit where the mesh was authored.
// Do NOT use renderer.rootBone.TransformPoint(verts[v]) -- collapses
// everything to near-origin in Hips local space on Humanoid rigs.
//
// Assumes the avatar is at or near bind pose during the scan; pause the
// animator if needed.

using System;
using System.Linq;
using Unity.Collections;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal sealed class SkinningCache {

        private readonly SkinnedMeshRenderer _renderer;
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
            _bones = renderer != null ? renderer.bones : Array.Empty<Transform>();
            _bindposes = mesh != null ? mesh.bindposes : Array.Empty<Matrix4x4>();
            _vertices = mesh != null ? mesh.vertices : Array.Empty<Vector3>();
            _normals = mesh != null && mesh.normals != null && mesh.normals.Length == mesh.vertexCount
                ? mesh.normals
                : Enumerable.Repeat(Vector3.up, mesh != null ? mesh.vertexCount : 0).ToArray();

            // GetAllBoneWeights / GetBonesPerVertex return NativeArrays whose
            // lifetime the caller owns; copy into managed arrays then dispose
            // immediately. Without Dispose, native memory leaks until GC or
            // domain reload.
            var allWeights = mesh != null ? mesh.GetAllBoneWeights() : default(NativeArray<BoneWeight1>);
            var bpv = mesh != null ? mesh.GetBonesPerVertex() : default(NativeArray<byte>);
            _weights = allWeights.IsCreated ? allWeights.ToArray() : Array.Empty<BoneWeight1>();
            if (allWeights.IsCreated) allWeights.Dispose();
            _bonesPerVertex = bpv.IsCreated ? bpv.ToArray() : Array.Empty<byte>();
            if (bpv.IsCreated) bpv.Dispose();
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

        public int VertexCount => _vertices.Length;

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
}
