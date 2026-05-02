// GenericPreset.cs
//
// Fallback preset. Always selectable; suggests itself with a low non-zero
// score so it wins by default if no specialised preset matches.
//
// Behaviour: one PhysBone per chain root, sensible neutral values, no
// colliders. Radius scales with the average bone size in the selection
// so big bones get a thicker simulated cylinder and small bones stay
// thin without manual tuning.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.Presets {

    internal sealed class GenericPreset : IPhysBonePreset {
        public string Id          => "generic";
        public string DisplayName => "Generic";
        public string Description => "Neutral defaults. One PhysBone per chain. No colliders.";

        public float SuggestionScore(BoneSelectionAnalysis a) => 0.10f;

        public System.Collections.Generic.IEnumerable<ScoringSignal> ExplainScore(BoneSelectionAnalysis a) {
            yield return new ScoringSignal("baseline (always selectable as fallback)", 0.10f);
        }

        public PhysBonePlan BuildPlan(BoneSelectionAnalysis a) {
            var plan = new PhysBonePlan { PresetId = Id, PresetDisplayName = DisplayName };
            if (a.Chains.Count == 0) {
                plan.Notes.Add("No bone chains detected in the selection.");
                return plan;
            }

            float radius = ScaleHelpers.RadiusFromBoneSize(a.AverageBoneSize, fallback: 0.04f);
            foreach (var c in a.Chains) {
                plan.PhysBones.Add(new PhysBoneSpec {
                    Root = c.Root,
                    Pull = 0.2f,
                    Spring = 0.3f,
                    Stiffness = 0.4f,
                    Gravity = 0f,
                    GravityFalloff = 0f,
                    ImmobileType = ImmobileTypeKind.None,
                    Immobile = 0f,
                    Radius = radius,
                    AllowCollision = AllowKind.True,
                    AllowGrabbing = AllowKind.True,
                    AllowPosing = AllowKind.True,
                    Note = $"Chain of {c.Bones.Count} bone(s), {c.LengthMetres:F3} m total.",
                });
            }
            plan.Notes.Add($"Generic preset; radius derived from avg bone size {a.AverageBoneSize:F3} m.");
            return plan;
        }
    }

    /// <summary>
    /// Helpers shared across presets. Kept tiny on purpose — anything that
    /// grows here should probably be its own type.
    /// </summary>
    internal static class ScaleHelpers {
        /// <summary>
        /// PhysBone radius derived from the average distance between
        /// adjacent bones in a chain. The cylinder around each segment
        /// should be ~25% of the segment length by default — that gives
        /// a "lightly bulky" feel without poking through the mesh.
        /// </summary>
        public static float RadiusFromBoneSize(float averageBoneSize, float fallback) {
            if (averageBoneSize <= 0f) return fallback;
            return Mathf.Clamp(averageBoneSize * 0.25f, 0.005f, 0.2f);
        }

        /// <summary>
        /// "How much of this chain points down?" 0 = horizontal, 1 = straight
        /// down. Used by gravity-affected presets to decide how much to apply.
        /// Operates on the avatar-local average orientation, so a sideways
        /// avatar (uncommon but possible) reads correctly.
        /// </summary>
        public static float Verticality(Vector3 avatarLocalOrientation) {
            if (avatarLocalOrientation.sqrMagnitude < 0.0001f) return 0f;
            return Mathf.Clamp01(-avatarLocalOrientation.normalized.y);
        }
    }
}
