// BlendShapeUtility.cs
//
// Helpers for adding/replacing a blendshape by name on a mesh, plus
// name-keyed weight snapshot/restore around the swap.
//
// Why the read-all/clear/re-add dance: Unity's Mesh API can only
// AddBlendShapeFrame to a new shape or the last existing shape -- inserting
// a frame into the middle of an existing non-last shape is not supported.
// To replace ANY blendshape we therefore have to read every other shape's
// frames out, ClearBlendShapes, re-add the others in their original order,
// then append the new one. Names are the stable key (indices shift),
// preserved exactly so animation clips that bind by name keep working.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal static class BlendShapeUtility {

        private sealed class Snapshot {
            public string Name;
            public readonly List<float> Weights = new List<float>();
            public readonly List<Vector3[]> DeltaVertices = new List<Vector3[]>();
            public readonly List<Vector3[]> DeltaNormals = new List<Vector3[]>();
            public readonly List<Vector3[]> DeltaTangents = new List<Vector3[]>();
        }

        /// <summary>
        /// Insert or replace a single-frame blendshape on the mesh, preserving
        /// every other shape's frames byte-for-byte and in original order.
        /// Returns true on success, false on null/mismatched-vertex-count input.
        /// </summary>
        public static bool AddOrReplace(Mesh mesh, string shapeName, Vector3[] deltaVertices) {
            if (mesh == null || string.IsNullOrEmpty(shapeName) || deltaVertices == null) return false;
            if (deltaVertices.Length != mesh.vertexCount) return false;

            var snapshots = new List<Snapshot>();
            int vertexCount = mesh.vertexCount;
            for (int i = 0; i < mesh.blendShapeCount; i++) {
                string existingName = mesh.GetBlendShapeName(i);
                if (string.Equals(existingName, shapeName, StringComparison.Ordinal)) continue;
                var snap = new Snapshot { Name = existingName };
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
            return true;
        }

        /// <summary>
        /// Read every blendshape weight on the renderer into a name-keyed map.
        /// Indices are not stable across mesh-mutation paths; names are.
        /// </summary>
        public static Dictionary<string, float> CaptureWeights(SkinnedMeshRenderer renderer, Mesh mesh) {
            var output = new Dictionary<string, float>();
            if (renderer == null || mesh == null) return output;
            for (int i = 0; i < mesh.blendShapeCount; i++) {
                output[mesh.GetBlendShapeName(i)] = renderer.GetBlendShapeWeight(i);
            }
            return output;
        }

        /// <summary>
        /// Apply each captured weight back onto the renderer by name. Weights
        /// whose shape no longer exists on the destination mesh are skipped
        /// silently; this is expected after a blendshape-replace operation
        /// dropped a stale shape.
        /// </summary>
        public static void RestoreWeights(SkinnedMeshRenderer renderer, Mesh mesh, Dictionary<string, float> weightsByName) {
            if (renderer == null || mesh == null || weightsByName == null) return;
            foreach (var kv in weightsByName) {
                int index = mesh.GetBlendShapeIndex(kv.Key);
                if (index >= 0) renderer.SetBlendShapeWeight(index, kv.Value);
            }
        }

        public static void SetWeight(SkinnedMeshRenderer renderer, Mesh mesh, string shapeName, float weight) {
            if (renderer == null || mesh == null) return;
            int index = mesh.GetBlendShapeIndex(shapeName);
            if (index >= 0) renderer.SetBlendShapeWeight(index, weight);
        }
    }
}
