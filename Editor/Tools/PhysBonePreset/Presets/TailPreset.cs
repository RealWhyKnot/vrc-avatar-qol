// TailPreset.cs
//
// One long-ish chain parented near Hips. Gravity pulls down, spring lets it
// swing, pull keeps it returning to base pose without snapping. Adapts:
//   - Spring decreases as the chain gets longer (more bones = more swing).
//   - Gravity scales with verticality — a tail that hangs down gets full
//     gravity; a tail that points back gets less, so it doesn't "sink"
//     visibly when standing still.
//
// Auto-suggest: highest when the selection sits under Hips with one chain
// of 5+ bones. Multiple chains lower the score (probably dual tails or
// something else entirely; still applies cleanly though).

using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.Presets {

    internal sealed class TailPreset : IPhysBonePreset {
        public string Id          => "tail";
        public string DisplayName => "Tail";
        public string Description => "Single long chain near the hips. Gravity + moderate spring; pull adapts to chain length.";

        public float SuggestionScore(BoneSelectionAnalysis a) {
            if (a.Chains.Count == 0) return 0f;
            float score = 0f;
            if (a.NearestHumanoidBoneType == HumanBodyBones.Hips) score += 0.55f;
            else if (a.NearestHumanoidBoneType == HumanBodyBones.Spine) score += 0.25f;
            if (a.AverageChainBoneCount >= 5) score += 0.25f;
            if (a.Chains.Count == 1) score += 0.15f;
            else if (a.Chains.Count > 3) score -= 0.2f;
            return Mathf.Clamp01(score);
        }

        public System.Collections.Generic.IEnumerable<ScoringSignal> ExplainScore(BoneSelectionAnalysis a) {
            if (a.Chains.Count == 0) { yield return new ScoringSignal("no chains detected", 0f); yield break; }
            if (a.NearestHumanoidBoneType == HumanBodyBones.Hips)
                yield return new ScoringSignal("under Hips", 0.55f);
            else if (a.NearestHumanoidBoneType == HumanBodyBones.Spine)
                yield return new ScoringSignal("under Spine", 0.25f);
            if (a.AverageChainBoneCount >= 5)
                yield return new ScoringSignal($"long chain ({a.AverageChainBoneCount} bones)", 0.25f);
            if (a.Chains.Count == 1)
                yield return new ScoringSignal("single chain", 0.15f);
            else if (a.Chains.Count > 3)
                yield return new ScoringSignal($"{a.Chains.Count} chains (more than typical tail)", -0.2f);
        }

        public PhysBonePlan BuildPlan(BoneSelectionAnalysis a) {
            var plan = new PhysBonePlan { PresetId = Id, PresetDisplayName = DisplayName };
            if (a.Chains.Count == 0) {
                plan.Notes.Add("No chains detected.");
                return plan;
            }

            float radius = ScaleHelpers.RadiusFromBoneSize(a.AverageBoneSize, fallback: 0.03f);

            foreach (var c in a.Chains) {
                // Per-chain adaptation: longer chains get less spring (they
                // already swing through inertia), shorter get more.
                float spring = Mathf.Clamp(0.7f - 0.04f * c.Bones.Count, 0.3f, 0.7f);
                // Gravity by verticality: the more this chain points down,
                // the more gravity should affect it.
                Vector3 localOrient = a.SideMap?.Hips != null
                    ? a.SideMap.Hips.InverseTransformPoint(c.Tip.position) - a.SideMap.Hips.InverseTransformPoint(c.Root.position)
                    : c.Tip.position - c.Root.position;
                float vert = ScaleHelpers.Verticality(localOrient);
                float gravity = Mathf.Lerp(0.05f, 0.3f, vert);

                plan.PhysBones.Add(new PhysBoneSpec {
                    Root = c.Root,
                    Pull = 0.15f,
                    Spring = spring,
                    Stiffness = 0.3f,
                    Gravity = gravity,
                    GravityFalloff = 0.3f,
                    ImmobileType = ImmobileTypeKind.None,
                    Immobile = 0f,
                    Radius = radius,
                    AllowCollision = AllowKind.True,
                    AllowGrabbing = AllowKind.True,
                    AllowPosing = AllowKind.True,
                    Note = $"{c.Bones.Count}-bone chain, {c.LengthMetres:F3} m. spring={spring:F2}, gravity={gravity:F2} (vert={vert:F2}).",
                });
            }
            return plan;
        }
    }
}
