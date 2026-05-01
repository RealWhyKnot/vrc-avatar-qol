// HairPreset.cs
//
// Multiple chains under Head (or Neck). Light gravity, moderate pull. Auto-
// adds a sphere collider on Head to keep hair from clipping into the skull
// during fast head turns. The sphere radius is derived from the avatar's
// scale (Hips Y) — taller avatar, bigger head, bigger collider.
//
// Auto-suggest: scores high under Head with 4+ chains and short-to-medium
// chain length. A single short chain under Head looks more like ears, so
// suggestion drops in that case.

using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.Presets {

    internal sealed class HairPreset : IPhysBonePreset {
        public string Id          => "hair";
        public string DisplayName => "Hair";
        public string Description => "Many chains under the head. Light gravity, moderate pull, auto-adds a head collider so hair doesn't clip into the skull.";

        public float SuggestionScore(BoneSelectionAnalysis a) {
            if (a.Chains.Count == 0) return 0f;
            float score = 0f;
            if (a.NearestHumanoidBoneType == HumanBodyBones.Head) score += 0.45f;
            else if (a.NearestHumanoidBoneType == HumanBodyBones.Neck) score += 0.15f;
            if (a.Chains.Count >= 4) score += 0.35f;
            else if (a.Chains.Count <= 2) score -= 0.15f; // looks more like ears
            if (a.AverageChainBoneCount >= 3 && a.AverageChainBoneCount <= 8) score += 0.15f;
            return Mathf.Clamp01(score);
        }

        public PhysBonePlan BuildPlan(BoneSelectionAnalysis a) {
            var plan = new PhysBonePlan { PresetId = Id, PresetDisplayName = DisplayName };
            if (a.Chains.Count == 0) {
                plan.Notes.Add("No chains detected.");
                return plan;
            }

            float radius = ScaleHelpers.RadiusFromBoneSize(a.AverageBoneSize, fallback: 0.015f);

            // Head collider — only added if we know where Head is. Sphere
            // sized to the avatar; small enough not to over-eat hair, big
            // enough to actually catch.
            int colliderIndex = -1;
            if (a.SideMap != null) {
                var head = a.HostAnimator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null) {
                    float headRadius = Mathf.Clamp(a.AvatarHeightApprox * 0.07f, 0.05f, 0.18f);
                    colliderIndex = plan.Colliders.Count;
                    plan.Colliders.Add(new ColliderSpec {
                        Name = "PhysCol_Head",
                        AttachTo = head,
                        RootTransform = head,
                        Shape = ColliderShape.Sphere,
                        Radius = headRadius,
                        Position = new Vector3(0f, headRadius * 0.6f, 0.02f),
                    });
                    plan.Notes.Add($"Head sphere collider radius {headRadius:F3} m (derived from avatar height {a.AvatarHeightApprox:F2}).");
                }
            }

            foreach (var c in a.Chains) {
                Vector3 localOrient = a.SideMap?.Hips != null
                    ? a.SideMap.Hips.InverseTransformPoint(c.Tip.position) - a.SideMap.Hips.InverseTransformPoint(c.Root.position)
                    : c.Tip.position - c.Root.position;
                float vert = ScaleHelpers.Verticality(localOrient);
                float gravity = Mathf.Lerp(0.04f, 0.18f, vert);

                var spec = new PhysBoneSpec {
                    Root = c.Root,
                    Pull = 0.25f,
                    Spring = 0.3f,
                    Stiffness = 0.4f,
                    Gravity = gravity,
                    GravityFalloff = 0.5f,
                    ImmobileType = ImmobileTypeKind.None,
                    Immobile = 0f,
                    Radius = radius,
                    AllowCollision = AllowKind.True,
                    AllowGrabbing = AllowKind.True,
                    AllowPosing = AllowKind.True,
                    Note = $"{c.Bones.Count}-bone strand, gravity={gravity:F2}.",
                };
                if (colliderIndex >= 0) spec.ColliderRefs.Add(colliderIndex);
                plan.PhysBones.Add(spec);
            }
            return plan;
        }
    }
}
