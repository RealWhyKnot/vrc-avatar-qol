// EarsPreset.cs
//
// Tuned for animal ears (cat, fox, bunny, etc.) — short chains parented
// somewhere under the Head bone. Wants:
//   - Stiffness high (return to pose),
//   - Pull moderate, spring low (no excessive bounce),
//   - Gravity near zero (ears don't visibly droop under gravity),
//   - Immobile = WorldRotation (so head turns don't flick the ears around).
//
// Auto-suggest: scores high when the selection sits under the Head Humanoid
// bone and the average chain has only 2-3 bones. Falls off otherwise.

using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.Presets {

    internal sealed class EarsPreset : IPhysBonePreset {
        public string Id          => "ears";
        public string DisplayName => "Ears";
        public string Description => "Short chains parented under the head — stiff with WorldRotation immobility so they hold pose when you turn your head.";

        public float SuggestionScore(BoneSelectionAnalysis a) {
            if (a.Chains.Count == 0) return 0f;
            float score = 0f;
            if (a.NearestHumanoidBoneType == HumanBodyBones.Head) score += 0.6f;
            else if (a.NearestHumanoidBoneType == HumanBodyBones.Neck) score += 0.3f;
            if (a.AverageChainBoneCount >= 1 && a.AverageChainBoneCount <= 4) score += 0.3f;
            if (a.Chains.Count >= 2 && a.Chains.Count <= 4) score += 0.1f;
            return Mathf.Clamp01(score);
        }

        public System.Collections.Generic.IEnumerable<ScoringSignal> ExplainScore(BoneSelectionAnalysis a) {
            if (a.Chains.Count == 0) {
                yield return new ScoringSignal("no chains detected", 0f);
                yield break;
            }
            if (a.NearestHumanoidBoneType == HumanBodyBones.Head)
                yield return new ScoringSignal("under Head", 0.6f);
            else if (a.NearestHumanoidBoneType == HumanBodyBones.Neck)
                yield return new ScoringSignal("under Neck", 0.3f);
            if (a.AverageChainBoneCount >= 1 && a.AverageChainBoneCount <= 4)
                yield return new ScoringSignal($"chain length {a.AverageChainBoneCount} matches ear range", 0.3f);
            if (a.Chains.Count >= 2 && a.Chains.Count <= 4)
                yield return new ScoringSignal($"{a.Chains.Count} chains (ear-pair / horn-set)", 0.1f);
        }

        public PhysBonePlan BuildPlan(BoneSelectionAnalysis a) {
            var plan = new PhysBonePlan { PresetId = Id, PresetDisplayName = DisplayName };
            if (a.Chains.Count == 0) {
                plan.Notes.Add("No chains detected.");
                return plan;
            }

            float radius = ScaleHelpers.RadiusFromBoneSize(a.AverageBoneSize, fallback: 0.02f);
            // Adapt stiffness slightly to chain length: longer ears can flop a
            // little more, shorter ears stay rigid.
            float stiffness = a.AverageChainBoneCount <= 2 ? 0.85f : 0.7f;

            foreach (var c in a.Chains) {
                plan.PhysBones.Add(new PhysBoneSpec {
                    Root = c.Root,
                    Pull = 0.5f,
                    Spring = 0.2f,
                    Stiffness = stiffness,
                    Gravity = 0.05f,
                    GravityFalloff = 0f,
                    ImmobileType = ImmobileTypeKind.WorldRotation,
                    Immobile = 0.5f,
                    Radius = radius,
                    AllowCollision = AllowKind.True,
                    AllowGrabbing = AllowKind.True,
                    AllowPosing = AllowKind.True,
                    Note = $"{c.Bones.Count}-bone ear, {c.LengthMetres:F3} m.",
                });
            }
            if (a.NearestHumanoidBoneType != HumanBodyBones.Head) {
                plan.Notes.Add($"Note: selection isn't directly under Head ({a.NearestHumanoidBoneType?.ToString() ?? "no Humanoid ancestor"}). The preset still applies but auto-suggest scored low.");
            }
            plan.Notes.Add($"Stiffness {stiffness:F2}, immobile=WorldRotation 0.50 — ears resist head-turn flick.");
            return plan;
        }
    }
}
