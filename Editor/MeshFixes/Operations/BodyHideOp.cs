// BodyHideOp.cs
//
// Collapses body vertices that sit underneath the garment into a single
// blendshape on the body mesh's in-memory clone. Three collapse modes:
// push inward along the body normal, collapse toward the renderer root,
// or collapse toward the bone that already dominates the vertex's skin
// weight.

using System.Collections.Generic;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Pipeline;

namespace WhyKnot.AvatarQol.MeshFixes.Operations {

    internal sealed class BodyHideOp : IMeshOperation {

        private const string DefaultShapeName = "AUTO_HideBodyUnderGarment";

        private readonly AutoTightenToBody _setup;

        public BodyHideOp(AutoTightenToBody setup) { _setup = setup; }

        public string Id => $"{_setup.GetInstanceID()}:BodyHide";
        public string DisplayName => "Body hide under garment";
        public Object Owner => _setup;

        public IEnumerable<SkinnedMeshRenderer> Reads {
            get { if (_setup.garmentRenderer != null) yield return _setup.garmentRenderer; }
        }

        public IEnumerable<SkinnedMeshRenderer> Writes {
            get { if (_setup.bodyRenderer != null) yield return _setup.bodyRenderer; }
        }

        public IEnumerable<(SkinnedMeshRenderer Renderer, string ShapeName)> ProducedShapes {
            get {
                if (_setup.bodyRenderer != null) {
                    yield return (_setup.bodyRenderer, ResolveShapeName(_setup));
                }
            }
        }

        public bool Validate(IMeshFixContext ctx) {
            if (_setup.garmentRenderer == null) {
                ctx.Errors.Add($"{_setup.name}: Body hide -- choose the clothing/garment SkinnedMeshRenderer.");
                return false;
            }
            if (_setup.bodyRenderer == null) {
                ctx.Errors.Add($"{_setup.name}: Body hide -- choose the body SkinnedMeshRenderer.");
                return false;
            }
            if (_setup.garmentRenderer == _setup.bodyRenderer) {
                ctx.Errors.Add($"{_setup.name}: Body hide -- clothing and body cannot be the same renderer.");
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
            var garmentMesh = ctx.GetOrCloneEditableMesh(garmentRenderer);
            var bodyMesh = ctx.GetOrCloneEditableMesh(bodyRenderer);
            if (garmentMesh == null || bodyMesh == null) return;

            var garmentSkin = new SkinningCache(garmentRenderer, garmentMesh);
            var garmentIndex = SurfaceIndex.Build(garmentRenderer, garmentMesh, garmentSkin,
                Mathf.Max(0.01f, _setup.bodyHideRadius));
            if (garmentIndex.TriangleCount == 0) {
                ctx.Errors.Add($"{garmentRenderer.name}: garment mesh has no triangles to use for body hiding.");
                return;
            }

            var bodySkin = new SkinningCache(bodyRenderer, bodyMesh);
            var verts = bodyMesh.vertices;
            var delta = new Vector3[bodyMesh.vertexCount];
            float radius = Mathf.Max(0.001f, _setup.bodyHideRadius);
            float depth = Mathf.Max(0f, _setup.bodyHideDepth);
            int moved = 0;

            for (int v = 0; v < verts.Length; v++) {
                var world = bodySkin.ToWorldPoint(v);
                if (!garmentIndex.TryFindNearest(world, radius, out _)) continue;

                var normal = bodySkin.ToWorldNormal(v);
                if (normal.sqrMagnitude < 0.0001f) normal = bodyRenderer.transform.up;
                var targetWorld = BuildHideTarget(_setup.bodyHideMode, bodyRenderer, bodySkin, v, world, normal, depth);
                var targetLocal = bodySkin.WorldToMeshLocal(v, targetWorld);
                var d = targetLocal - verts[v];
                if (d.sqrMagnitude <= 0.0000000001f) continue;
                delta[v] = d;
                moved++;
            }

            if (moved == 0) {
                ctx.Warnings.Add($"{bodyRenderer.name}: no body vertices were close enough to the garment to hide.");
                return;
            }

            var shapeName = ResolveShapeName(_setup);
            if (!ctx.TryReserveShape(bodyRenderer, shapeName, this)) {
                ctx.Errors.Add($"{bodyRenderer.name}: shape '{shapeName}' is already claimed by another operation on the same renderer.");
                return;
            }

            var savedWeights = BlendShapeUtility.CaptureWeights(bodyRenderer, bodyMesh);
            if (!BlendShapeUtility.AddOrReplace(bodyMesh, shapeName, delta)) {
                ctx.Errors.Add($"{bodyRenderer.name}: blendshape '{shapeName}' write failed.");
                return;
            }
            bodyMesh.RecalculateBounds();
            BlendShapeUtility.RestoreWeights(bodyRenderer, bodyMesh, savedWeights);
            if (_setup.setBodyHideWeightTo100) {
                BlendShapeUtility.SetWeight(bodyRenderer, bodyMesh, shapeName, 100f);
            }
            ctx.RecordOpSuccess(Id, bodyRenderer, $"hid {moved} vert(s) as '{shapeName}'");
        }

        internal static string ResolveShapeName(AutoTightenToBody setup) {
            if (setup == null) return DefaultShapeName;
            var name = setup.bodyHideBlendShapeName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            // Same default-collision protection as garment tighten: derive the
            // body-hide name from the garment renderer so two manually-added
            // setups against a shared body do not silently clobber each other.
            if (setup.garmentRenderer != null) return $"AUTO_HideBody_{setup.garmentRenderer.name}";
            return DefaultShapeName;
        }

        private static Vector3 BuildHideTarget(
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
    }
}
