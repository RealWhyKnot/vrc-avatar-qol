// DressPreset.cs
//
// Many parallel chains hanging from the hips/spine. Skirts, dresses, kimonos.
// Wants:
//   - Heavier gravity (cloth pulled down),
//   - Low pull (lots of swing),
//   - Auto-attached leg colliders so it doesn't clip through the legs when
//     walking.
//
// The leg colliders are real capsules along LeftUpperLeg/LeftLowerLeg and the
// right counterparts. Capsule height = distance from each bone to its child.
// Radius is derived from avatar scale, then nudged up a hair so the cloth
// has a tiny safety buffer (skirts that *just* clip a thigh look worse than
// skirts that float a millimetre off).
//
// Auto-suggest: scores high for many chains under Hips with verticality ≥ ~0.7.

using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.Presets {

    internal sealed class DressPreset : IPhysBonePreset {
        public string Id          => "dress";
        public string DisplayName => "Dress / Skirt";
        public string Description => "Many vertical chains under the hips. Heavier gravity, low pull, auto-adds leg colliders so cloth doesn't clip through the legs.";

        public float SuggestionScore(BoneSelectionAnalysis a) {
            if (a.Chains.Count == 0) return 0f;
            float score = 0f;
            if (a.NearestHumanoidBoneType == HumanBodyBones.Hips) score += 0.35f;
            else if (a.NearestHumanoidBoneType == HumanBodyBones.Spine) score += 0.2f;
            if (a.Chains.Count >= 4) score += 0.25f;
            else if (a.Chains.Count >= 6) score += 0.35f;
            float vert = ScaleHelpers.Verticality(a.AverageChainOrientationLocal);
            if (vert > 0.7f) score += 0.3f;
            else if (vert > 0.5f) score += 0.15f;
            return Mathf.Clamp01(score);
        }

        public PhysBonePlan BuildPlan(BoneSelectionAnalysis a) {
            var plan = new PhysBonePlan { PresetId = Id, PresetDisplayName = DisplayName };
            if (a.Chains.Count == 0) {
                plan.Notes.Add("No chains detected.");
                return plan;
            }

            // Build leg colliders. Each leg bone gets a capsule along its
            // length toward the child bone. We capture the indices into
            // plan.Colliders so the per-chain spec can reference them.
            var legColliderIndices = new System.Collections.Generic.List<int>();
            if (a.SideMap != null && a.HostAnimator != null) {
                AppendLegCapsule(plan, legColliderIndices, a.HostAnimator,
                    HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  a.AvatarHeightApprox);
                AppendLegCapsule(plan, legColliderIndices, a.HostAnimator,
                    HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      a.AvatarHeightApprox);
                AppendLegCapsule(plan, legColliderIndices, a.HostAnimator,
                    HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, a.AvatarHeightApprox);
                AppendLegCapsule(plan, legColliderIndices, a.HostAnimator,
                    HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     a.AvatarHeightApprox);
            } else {
                plan.Notes.Add("Avatar isn't Humanoid — leg colliders skipped.");
            }
            if (legColliderIndices.Count > 0) {
                plan.Notes.Add($"Auto-added {legColliderIndices.Count} leg capsule collider(s).");
            }

            float radius = ScaleHelpers.RadiusFromBoneSize(a.AverageBoneSize, fallback: 0.02f);

            foreach (var c in a.Chains) {
                Vector3 localOrient = a.SideMap?.Hips != null
                    ? a.SideMap.Hips.InverseTransformPoint(c.Tip.position) - a.SideMap.Hips.InverseTransformPoint(c.Root.position)
                    : c.Tip.position - c.Root.position;
                float vert = ScaleHelpers.Verticality(localOrient);
                float gravity = Mathf.Lerp(0.15f, 0.35f, vert);

                var spec = new PhysBoneSpec {
                    Root = c.Root,
                    Pull = 0.05f,
                    Spring = 0.2f,
                    Stiffness = 0.15f,
                    Gravity = gravity,
                    GravityFalloff = 0.2f,
                    ImmobileType = ImmobileTypeKind.None,
                    Immobile = 0f,
                    Radius = radius,
                    AllowCollision = AllowKind.True,
                    AllowGrabbing = AllowKind.True,
                    AllowPosing = AllowKind.True,
                    Note = $"{c.Bones.Count}-bone strand, gravity={gravity:F2}.",
                };
                spec.ColliderRefs.AddRange(legColliderIndices);
                plan.PhysBones.Add(spec);
            }
            return plan;
        }

        private static void AppendLegCapsule(
            PhysBonePlan plan,
            System.Collections.Generic.List<int> indices,
            Animator animator,
            HumanBodyBones bone,
            HumanBodyBones childBone,
            float avatarHeight) {

            var t = animator.GetBoneTransform(bone);
            var ct = animator.GetBoneTransform(childBone);
            if (t == null || ct == null) return;

            // Capsule extends along bone-local Y; PhysBoneCollider's default
            // axis matches that. Height = distance to child; position the
            // collider at the bone midpoint so the capsule covers from
            // joint to joint.
            var localChild = t.InverseTransformPoint(ct.position);
            float height = localChild.magnitude;
            float radius = Mathf.Clamp(avatarHeight * 0.045f, 0.025f, 0.10f);
            // Align capsule along the actual direction to the child (might
            // not be straight down in local space — sometimes legs are
            // rigged with a slight forward tilt at bind pose).
            var dir = localChild.normalized;
            // Capsule's natural axis is +Y in its local frame; rotate that
            // to match `dir`.
            var rotation = Quaternion.FromToRotation(Vector3.up, dir);

            int idx = plan.Colliders.Count;
            plan.Colliders.Add(new ColliderSpec {
                Name = $"PhysCol_{bone}",
                AttachTo = t,
                RootTransform = t,
                Shape = ColliderShape.Capsule,
                Radius = radius,
                Height = height,
                Position = localChild * 0.5f,    // midpoint of the bone
                EulerRotation = rotation.eulerAngles,
            });
            indices.Add(idx);
        }
    }
}
