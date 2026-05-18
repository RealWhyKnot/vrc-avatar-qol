// GarmentTightenOp.cs
//
// Pulls clothing vertices toward the nearest point on a body mesh,
// stored as a single blendshape on the clothing mesh's in-memory clone.
// Source of intent: one AutoTightenToBody component on the garment
// GameObject (per-component DisallowMultipleComponent enforced by the
// runtime type).

using System.Collections.Generic;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Pipeline;

namespace WhyKnot.AvatarQol.MeshFixes.Operations {

    internal sealed class GarmentTightenOp : IMeshOperation {

        private const string DefaultShapeName = "AUTO_TightenToBody";

        private readonly AutoTightenToBody _setup;

        public GarmentTightenOp(AutoTightenToBody setup) { _setup = setup; }

        public string Id => $"{_setup.GetInstanceID()}:GarmentTighten";
        public string DisplayName => "Garment tighten";
        public Object Owner => _setup;

        public IEnumerable<SkinnedMeshRenderer> Reads {
            get { if (_setup.bodyRenderer != null) yield return _setup.bodyRenderer; }
        }

        public IEnumerable<SkinnedMeshRenderer> Writes {
            get { if (_setup.garmentRenderer != null) yield return _setup.garmentRenderer; }
        }

        public IEnumerable<(SkinnedMeshRenderer Renderer, string ShapeName)> ProducedShapes {
            get {
                if (_setup.garmentRenderer != null) {
                    yield return (_setup.garmentRenderer, ResolveShapeName(_setup));
                }
            }
        }

        public bool Validate(IMeshFixContext ctx) {
            if (_setup.garmentRenderer == null) {
                ctx.Errors.Add($"{_setup.name}: Garment tighten -- choose the clothing/garment SkinnedMeshRenderer.");
                return false;
            }
            if (_setup.bodyRenderer == null) {
                ctx.Errors.Add($"{_setup.name}: Garment tighten -- choose the body SkinnedMeshRenderer.");
                return false;
            }
            if (_setup.garmentRenderer == _setup.bodyRenderer) {
                ctx.Errors.Add($"{_setup.name}: Garment tighten -- clothing and body cannot be the same renderer.");
                return false;
            }
            if (_setup.garmentRenderer.sharedMesh == null) {
                ctx.Errors.Add($"{_setup.garmentRenderer.name}: garment renderer has no mesh.");
                return false;
            }
            if (_setup.bodyRenderer.sharedMesh == null) {
                ctx.Errors.Add($"{_setup.bodyRenderer.name}: body renderer has no mesh.");
                return false;
            }
            if (!_setup.garmentRenderer.sharedMesh.isReadable) {
                ctx.Errors.Add($"{_setup.garmentRenderer.name}: garment mesh is not readable. Enable Read/Write on the model import.");
                return false;
            }
            if (!_setup.bodyRenderer.sharedMesh.isReadable) {
                ctx.Errors.Add($"{_setup.bodyRenderer.name}: body mesh is not readable. Enable Read/Write on the model import.");
                return false;
            }
            return true;
        }

        public void Apply(IMeshFixContext ctx) {
            var garmentRenderer = _setup.garmentRenderer;
            var bodyRenderer = _setup.bodyRenderer;
            // Garment is the write target; clone + capture it via the context.
            var garmentMesh = ctx.GetOrCloneEditableMesh(garmentRenderer);
            // Body is read-only here. Use whatever sharedMesh the renderer
            // currently has -- if a prior op (BodyHideOp from another setup,
            // WeightFixer's persistent clone) replaced it, the latest mesh is
            // the right read target. We must NOT clone + swap a read-only
            // renderer; that would surprise users with a transient sharedMesh
            // swap and inflate the cloned-mesh count.
            var bodyMesh = bodyRenderer != null ? bodyRenderer.sharedMesh : null;
            if (garmentMesh == null || bodyMesh == null) return;

            var bodySkin = new SkinningCache(bodyRenderer, bodyMesh);
            var bodyIndex = SurfaceIndex.Build(bodyRenderer, bodyMesh, bodySkin,
                Mathf.Max(0.01f, _setup.maxProjectionDistance * 0.5f));
            if (bodyIndex.TriangleCount == 0) {
                ctx.Errors.Add($"{bodyRenderer.name}: body mesh has no triangles to project against.");
                return;
            }

            var garmentSkin = new SkinningCache(garmentRenderer, garmentMesh);
            var verts = garmentMesh.vertices;
            var delta = new Vector3[garmentMesh.vertexCount];
            int moved = 0;
            float search = Mathf.Max(0.001f, _setup.maxProjectionDistance);

            for (int v = 0; v < verts.Length; v++) {
                if (!VertexSelection.IsSelected(garmentMesh, _setup.selectionMode, v)) continue;
                var world = garmentSkin.ToWorldPoint(v);
                if (!bodyIndex.TryFindNearest(world, search, out var hit)) continue;

                var targetWorld = hit.Point + hit.Normal * Mathf.Max(0f, _setup.garmentSurfaceOffset);
                var targetLocal = garmentSkin.WorldToMeshLocal(v, targetWorld);
                var d = targetLocal - verts[v];
                if (d.sqrMagnitude <= 0.0000000001f) continue;
                delta[v] = d;
                moved++;
            }

            if (moved == 0) {
                ctx.Warnings.Add($"{garmentRenderer.name}: no garment vertices were close enough to the body to tighten.");
                return;
            }

            var shapeName = ResolveShapeName(_setup);
            if (!ctx.TryReserveShape(garmentRenderer, shapeName, this)) {
                ctx.Errors.Add($"{garmentRenderer.name}: shape '{shapeName}' is already claimed by another operation on the same renderer.");
                return;
            }

            var savedWeights = BlendShapeUtility.CaptureWeights(garmentRenderer, garmentMesh);
            if (!BlendShapeUtility.AddOrReplace(garmentMesh, shapeName, delta)) {
                ctx.Errors.Add($"{garmentRenderer.name}: blendshape '{shapeName}' write failed.");
                return;
            }
            garmentMesh.RecalculateBounds();
            BlendShapeUtility.RestoreWeights(garmentRenderer, garmentMesh, savedWeights);
            if (_setup.setGarmentTightenWeightTo100) {
                BlendShapeUtility.SetWeight(garmentRenderer, garmentMesh, shapeName, 100f);
            }
            ctx.RecordOpSuccess(Id, garmentRenderer, $"tightened {moved} vert(s) as '{shapeName}'");
        }

        internal static string ResolveShapeName(AutoTightenToBody setup) {
            if (setup == null) return DefaultShapeName;
            var name = setup.garmentTightenBlendShapeName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            // Derive a per-garment name from the renderer so two manually-added
            // setups with the default constant do not silently fight over the
            // same blendshape on a shared body.
            if (setup.garmentRenderer != null) return $"AUTO_Tighten_{setup.garmentRenderer.name}";
            return DefaultShapeName;
        }
    }
}
