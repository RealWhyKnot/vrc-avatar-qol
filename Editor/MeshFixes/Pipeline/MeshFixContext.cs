// MeshFixContext.cs
//
// Concrete IMeshFixContext implementation used by MeshFixPipeline. One
// instance per pipeline run.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal sealed class MeshFixContext : IMeshFixContext {

        private readonly MeshFixSession _session;
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> _cloneByRenderer =
            new Dictionary<SkinnedMeshRenderer, Mesh>();
        private readonly Dictionary<(SkinnedMeshRenderer, string), IMeshOperation> _shapeOwners =
            new Dictionary<(SkinnedMeshRenderer, string), IMeshOperation>();
        private readonly List<OpRecord> _opRecords = new List<OpRecord>();
        private readonly HashSet<SkinnedMeshRenderer> _clothRefused = new HashSet<SkinnedMeshRenderer>();

        public MeshFixContext(GameObject avatarRoot, MeshFixSession session, MeshFixMode mode, bool verbose) {
            AvatarRoot = avatarRoot;
            _session = session;
            Mode = mode;
            Verbose = verbose;
        }

        public MeshFixMode Mode { get; }
        public bool Verbose { get; }
        public GameObject AvatarRoot { get; }
        public IList<string> Errors { get; } = new List<string>();
        public IList<string> Warnings { get; } = new List<string>();

        internal IReadOnlyList<OpRecord> OpRecords => _opRecords;
        internal int MeshesCloned => _cloneByRenderer.Count;

        public void RecordOpSuccess(string opId, SkinnedMeshRenderer renderer, string detail) {
            _opRecords.Add(new OpRecord(opId, renderer, detail));
        }

        public Mesh GetOrCloneEditableMesh(SkinnedMeshRenderer renderer) {
            if (renderer == null || renderer.sharedMesh == null) return null;

            // Cloth snapshots its mesh at init; mid-play sharedMesh swap
            // leaves the simulation referencing stale vertex layout. Refuse
            // once per renderer and surface a clear warning so the user
            // knows which renderer was skipped.
            if (HasCloth(renderer)) {
                if (_clothRefused.Add(renderer)) {
                    Warnings.Add($"{RendererPath(renderer)}: has a Cloth component; mid-run mesh swap would desync the cloth simulation. Skipped.");
                }
                return null;
            }

            if (_cloneByRenderer.TryGetValue(renderer, out var existing) && existing != null) return existing;

            var original = renderer.sharedMesh;

            // Capture original BEFORE swapping; Session restores it on Dispose.
            _session.Capture(renderer);

            var clone = Object.Instantiate(original);
            clone.name = original.name + " (WhyKnot mesh fix)";
            clone.hideFlags = HideFlags.DontSave;
            _session.Adopt(clone);
            _cloneByRenderer[renderer] = clone;

            var savedWeights = BlendShapeUtility.CaptureWeights(renderer, original);
            renderer.sharedMesh = clone;
            BlendShapeUtility.RestoreWeights(renderer, clone, savedWeights);
            return clone;
        }

        public bool TryReserveShape(SkinnedMeshRenderer renderer, string shapeName, IMeshOperation owner) {
            if (renderer == null || string.IsNullOrEmpty(shapeName)) return false;
            var key = (renderer, shapeName);
            if (_shapeOwners.TryGetValue(key, out var existing) && existing != owner) return false;
            _shapeOwners[key] = owner;
            return true;
        }

        internal IReadOnlyDictionary<(SkinnedMeshRenderer, string), IMeshOperation> ShapeOwners => _shapeOwners;

        private static bool HasCloth(SkinnedMeshRenderer renderer) {
            if (renderer == null) return false;
            return renderer.GetComponent<Cloth>() != null;
        }

        private static string RendererPath(SkinnedMeshRenderer renderer) {
            if (renderer == null) return "(null)";
            var t = renderer.transform;
            var parts = new List<string>();
            while (t != null) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts);
        }

        internal readonly struct OpRecord {
            public OpRecord(string opId, SkinnedMeshRenderer renderer, string detail) {
                OpId = opId;
                Renderer = renderer;
                Detail = detail;
            }
            public string OpId { get; }
            public SkinnedMeshRenderer Renderer { get; }
            public string Detail { get; }
        }
    }
}
