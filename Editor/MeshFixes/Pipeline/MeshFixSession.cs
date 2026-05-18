// MeshFixSession.cs
//
// Holds the per-pipeline-run lifetime of mesh clones and their original
// references so Dispose deterministically unwinds every swap.
//
// Why HideFlags.DontSave (not DontSaveInEditor):
//   HideFlags.DontSave = DontSaveInBuild | DontSaveInEditor | DontUnloadUnusedAsset
//   is the documented compound. Using DontSaveInEditor alone has been
//   reported to behave AS IF DontUnloadUnusedAssets were also set, but
//   the docs do not promise this; relying on it would be a silent leak
//   path. DontSave is predictable and the canonical choice; pair it with
//   explicit DestroyImmediate in Dispose.
//
// Why per-renderer, not per-op: multiple ops on the same renderer share
// one clone and one captured original. Body-hide from Shirt and body-hide
// from Skirt both write into the same generated body mesh.
//
// Why NOT Undo.RegisterCreatedObjectUndo on the clones: it would pin them
// in the undo stack and balloon memory. Lifetime is owned by this Session.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal sealed class MeshFixSession : System.IDisposable {

        private readonly Dictionary<SkinnedMeshRenderer, RendererState> _states =
            new Dictionary<SkinnedMeshRenderer, RendererState>();
        private readonly List<Object> _generated = new List<Object>();

        public bool HasChanges => _states.Count > 0 || _generated.Count > 0;
        public int CapturedRendererCount => _states.Count;
        public int GeneratedObjectCount => _generated.Count;

        public void Capture(SkinnedMeshRenderer renderer) {
            if (renderer == null || _states.ContainsKey(renderer)) return;
            _states[renderer] = new RendererState(renderer);
        }

        public void Adopt(Object generated) {
            if (generated != null) _generated.Add(generated);
        }

        public void Merge(MeshFixSession other) {
            if (other == null || ReferenceEquals(other, this)) return;
            foreach (var kv in other._states) {
                if (kv.Key != null && !_states.ContainsKey(kv.Key)) _states[kv.Key] = kv.Value;
            }
            foreach (var g in other._generated) if (g != null) _generated.Add(g);
            other._states.Clear();
            other._generated.Clear();
        }

        public void Restore() => Dispose();

        public void Dispose() {
            foreach (var state in _states.Values) state.Restore();
            _states.Clear();

            for (int i = _generated.Count - 1; i >= 0; i--) {
                var obj = _generated[i];
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _generated.Clear();
        }

        private sealed class RendererState {
            private readonly SkinnedMeshRenderer _renderer;
            private readonly Mesh _mesh;
            private readonly Dictionary<string, float> _weightsByName;

            public RendererState(SkinnedMeshRenderer renderer) {
                _renderer = renderer;
                _mesh = renderer != null ? renderer.sharedMesh : null;
                _weightsByName = BlendShapeUtility.CaptureWeights(renderer, _mesh);
            }

            public void Restore() {
                if (_renderer == null) return;
                _renderer.sharedMesh = _mesh;
                BlendShapeUtility.RestoreWeights(_renderer, _mesh, _weightsByName);
            }
        }
    }
}
