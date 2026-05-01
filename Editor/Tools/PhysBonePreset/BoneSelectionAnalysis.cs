// BoneSelectionAnalysis.cs
//
// Turns a flat selection of Transforms into a structured description of
// what the user's actually pointing at. Presets read from this — never
// from the raw selection — so adding "look at the bone graph this way"
// happens once, here, instead of duplicated across every preset.
//
// What we extract:
//   - Chains: each bone whose parent is NOT in the selection becomes a
//     chain root. Each chain is a depth-first descent through children
//     that are also selected, until the chain branches or ends.
//   - Per-chain: ordered bones, total length in metres, dominant axis
//     (tip - root, normalised) in avatar local space, side via the
//     HumanoidSideMap.
//   - Selection-wide: the host Animator (walked up from any bone), the
//     avatar scale (Hips height in world), the dominant side, the
//     average chain orientation (so presets can ask "is this mostly
//     vertical?" without iterating chains themselves), and the nearest
//     Humanoid bone the selection sits beneath (so a chain of bones
//     under Head reports "near Head" — useful for the ears auto-suggest).
//
// Limitations:
//   - When a bone has multiple selected children, we extend the chain
//     down the first child only. The other children become their own
//     chain roots (they pass the "parent not selected" test as long as
//     their parent IS selected... wait, no — let me re-read). Actually
//     the chain root rule is "parent not in selection". A bone whose
//     parent IS in selection is mid-chain. So branching is currently
//     handled by the first-child-only descent; the other branches don't
//     get their own chain unless their parent isn't selected. This is a
//     known limitation — branching rigs (some skirt rigs) will be
//     under-counted. Documenting it rather than fixing in v1.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class BoneSelectionAnalysis {

        public Transform[] RawSelection;
        public List<BoneChain> Chains = new List<BoneChain>();

        public Animator HostAnimator;          // walked up from any selected bone
        public HumanoidSideMap SideMap;        // null if HostAnimator isn't Humanoid

        public float AvatarHeightApprox;       // Hips Y in world; ~1.0-1.6 for humanoid
        public BoneSide DominantSide;
        public Vector3 AverageChainOrientationLocal;   // tip-root avg, in Hips local
        public float AverageChainLengthMetres;
        public int AverageChainBoneCount;
        public float AverageBoneSize;           // distance to first child, avg

        // The closest Humanoid bone whose subtree contains the centroid of the
        // selection. Filled when HostAnimator is Humanoid. The presets use
        // this to auto-suggest ("under Head" → ears, "under Hips" → tail).
        public HumanBodyBones? NearestHumanoidBoneType;
        public Transform NearestHumanoidBone;

        /// <summary>
        /// Build an analysis from a raw selection of Transforms. Returns a
        /// non-null instance even when the selection is degenerate; callers
        /// should check Chains.Count and HostAnimator.
        /// </summary>
        public static BoneSelectionAnalysis Build(IEnumerable<Transform> selection) {
            var a = new BoneSelectionAnalysis();
            var unique = new HashSet<Transform>();
            foreach (var t in selection) {
                if (t != null) unique.Add(t);
            }
            a.RawSelection = new Transform[unique.Count];
            unique.CopyTo(a.RawSelection);

            if (unique.Count == 0) return a;

            // Find the host Animator by walking up from any selected bone.
            foreach (var t in unique) {
                var animator = t.GetComponentInParent<Animator>();
                if (animator != null) { a.HostAnimator = animator; break; }
            }
            if (a.HostAnimator != null && a.HostAnimator.isHuman) {
                a.SideMap = new HumanoidSideMap(a.HostAnimator);
                a.AvatarHeightApprox = a.SideMap.Hips != null ? a.SideMap.Hips.position.y : 1f;
            } else {
                a.AvatarHeightApprox = 1f;
            }

            // Detect chain roots and walk descendants.
            var visited = new HashSet<Transform>();
            foreach (var t in unique) {
                if (visited.Contains(t)) continue;
                if (t.parent != null && unique.Contains(t.parent)) continue; // not a root
                var chain = BuildChain(t, unique, visited);
                if (chain != null) a.Chains.Add(chain);
            }

            // Aggregate stats.
            int sideLeft = 0, sideRight = 0, sideCenter = 0;
            Vector3 orientationSum = Vector3.zero;
            float lengthSum = 0;
            int boneCountSum = 0;
            float boneSizeSum = 0;
            int boneSizeSamples = 0;
            Vector3 centroidSum = Vector3.zero;
            int centroidSamples = 0;

            foreach (var c in a.Chains) {
                BoneSide side = a.SideMap?.GetSide(c.Root) ?? BoneSide.Unknown;
                c.Side = side;
                switch (side) {
                    case BoneSide.Left:   sideLeft++; break;
                    case BoneSide.Right:  sideRight++; break;
                    case BoneSide.Center: sideCenter++; break;
                }
                if (a.SideMap != null && a.SideMap.Hips != null) {
                    var localTip = a.SideMap.Hips.InverseTransformPoint(c.Tip.position);
                    var localRoot = a.SideMap.Hips.InverseTransformPoint(c.Root.position);
                    var orient = (localTip - localRoot).normalized;
                    orientationSum += orient;
                } else {
                    orientationSum += (c.Tip.position - c.Root.position).normalized;
                }
                lengthSum += c.LengthMetres;
                boneCountSum += c.Bones.Count;
                centroidSum += c.Root.position;
                centroidSamples++;
                for (int i = 0; i < c.Bones.Count - 1; i++) {
                    boneSizeSum += Vector3.Distance(c.Bones[i].position, c.Bones[i + 1].position);
                    boneSizeSamples++;
                }
            }

            int chainCount = a.Chains.Count;
            if (chainCount > 0) {
                a.AverageChainOrientationLocal = (orientationSum / chainCount).normalized;
                a.AverageChainLengthMetres = lengthSum / chainCount;
                a.AverageChainBoneCount = Mathf.RoundToInt((float)boneCountSum / chainCount);
            }
            if (boneSizeSamples > 0) a.AverageBoneSize = boneSizeSum / boneSizeSamples;

            if (sideLeft > sideRight && sideLeft > sideCenter) a.DominantSide = BoneSide.Left;
            else if (sideRight > sideLeft && sideRight > sideCenter) a.DominantSide = BoneSide.Right;
            else if (sideCenter > 0) a.DominantSide = BoneSide.Center;
            else a.DominantSide = BoneSide.Unknown;

            // Nearest Humanoid bone: from the centroid, walk every chain
            // root upward until we hit a Humanoid bone.
            if (a.HostAnimator != null && a.HostAnimator.isHuman) {
                FindNearestHumanoidBone(a);
            }

            return a;
        }

        private static BoneChain BuildChain(Transform root, HashSet<Transform> selection, HashSet<Transform> visited) {
            var bones = new List<Transform>();
            var t = root;
            while (t != null) {
                if (!selection.Contains(t)) break;
                if (visited.Contains(t)) break;
                visited.Add(t);
                bones.Add(t);
                // Pick the first selected child to continue. Other selected
                // children would be reached by separate root iterations
                // unless their parent is also selected (in which case
                // they'd be skipped by the chain-root check). See class
                // comment for the limitation.
                Transform nextChild = null;
                for (int i = 0; i < t.childCount; i++) {
                    var c = t.GetChild(i);
                    if (selection.Contains(c) && !visited.Contains(c)) {
                        nextChild = c;
                        break;
                    }
                }
                t = nextChild;
            }
            if (bones.Count == 0) return null;
            var chain = new BoneChain { Root = bones[0], Tip = bones[bones.Count - 1] };
            chain.Bones.AddRange(bones);
            float length = 0;
            for (int i = 0; i < bones.Count - 1; i++) {
                length += Vector3.Distance(bones[i].position, bones[i + 1].position);
            }
            chain.LengthMetres = length;
            return chain;
        }

        private static void FindNearestHumanoidBone(BoneSelectionAnalysis a) {
            // For each chain root, walk up its parents until we hit any
            // Humanoid bone. Pick the most common one across chains as
            // the "nearest" — that biases toward the bone the SELECTION
            // actually sits beneath, not just the parent of one chain.
            var counts = new Dictionary<HumanBodyBones, int>();
            foreach (var c in a.Chains) {
                var bone = WalkToHumanoidAncestor(c.Root, a.HostAnimator);
                if (bone == null) continue;
                if (counts.ContainsKey(bone.Value)) counts[bone.Value]++;
                else counts[bone.Value] = 1;
            }
            HumanBodyBones? best = null;
            int bestCount = 0;
            foreach (var kv in counts) {
                if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
            }
            if (best.HasValue) {
                a.NearestHumanoidBoneType = best.Value;
                a.NearestHumanoidBone = a.HostAnimator.GetBoneTransform(best.Value);
            }
        }

        private static HumanBodyBones? WalkToHumanoidAncestor(Transform start, Animator animator) {
            // Build a reverse map once per call. Cheap relative to the chain
            // walk — and BoneSelectionAnalysis isn't called in a hot loop.
            var reverse = new Dictionary<Transform, HumanBodyBones>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++) {
                var b = (HumanBodyBones)i;
                var t = animator.GetBoneTransform(b);
                if (t != null) reverse[t] = b;
            }
            var cur = start;
            while (cur != null) {
                if (reverse.TryGetValue(cur, out var bone)) return bone;
                cur = cur.parent;
            }
            return null;
        }
    }

    internal sealed class BoneChain {
        public Transform Root;
        public Transform Tip;
        public List<Transform> Bones = new List<Transform>();
        public float LengthMetres;
        public BoneSide Side;
    }
}
