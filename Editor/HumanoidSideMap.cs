// HumanoidSideMap.cs
//
// Maps each Transform in an avatar's bone hierarchy to one of:
//   Left    — the Transform IS, or descends from, a Humanoid Left* bone.
//   Right   — same for Right*.
//   Center  — the Transform IS, or descends from, a center bone (Hips,
//             Spine, Chest, Neck, Head, etc).
//   Unknown — no Humanoid ancestor (loose bone, prop bone, etc).
//
// Used by tools that reason about "this vertex is on the avatar's left"
// vs "this bone deforms the avatar's right side". The mapping is built
// once per Animator from the Humanoid bone bindings; descendants inherit
// the side of their nearest Humanoid ancestor, so a bone you've added
// under LeftUpperLeg (say, a custom skirt panel rig) is correctly tagged
// as Left even though it isn't itself a Humanoid bone.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol {

    internal enum BoneSide {
        Unknown,
        Center,
        Left,
        Right,
    }

    internal sealed class HumanoidSideMap {

        private readonly Animator _animator;
        private readonly Dictionary<Transform, HumanBodyBones> _humanoid;
        private readonly Dictionary<Transform, BoneSide> _cache = new Dictionary<Transform, BoneSide>();

        /// <summary>
        /// Sign of the avatar's "left" axis in Hips local space. Computed once
        /// from the actual position of LeftUpperLeg relative to Hips, so we don't
        /// have to assume any particular Unity coordinate convention — a vertex
        /// is on the avatar's left iff sign(hipsLocal.x) == LeftSign.
        /// </summary>
        public float LeftSignInHipsLocal { get; }

        public Transform Hips { get; }

        public bool IsValid => _animator != null && Hips != null && _humanoid != null && _humanoid.Count > 0;

        public HumanoidSideMap(Animator animator) {
            _animator = animator;
            if (animator == null || !animator.isHuman) {
                _humanoid = new Dictionary<Transform, HumanBodyBones>();
                Hips = null;
                LeftSignInHipsLocal = 1f;
                return;
            }

            _humanoid = new Dictionary<Transform, HumanBodyBones>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++) {
                var b = (HumanBodyBones)i;
                var t = animator.GetBoneTransform(b);
                if (t == null) continue;
                _humanoid[t] = b;
            }
            Hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            LeftSignInHipsLocal = ComputeLeftSign();
        }

        /// <summary>
        /// Categorise a Transform in this avatar's hierarchy. Walks up the
        /// parent chain until it hits a known Humanoid bone (or runs out of
        /// parents).
        /// </summary>
        public BoneSide GetSide(Transform bone) {
            if (bone == null) return BoneSide.Unknown;
            if (_cache.TryGetValue(bone, out var cached)) return cached;
            var side = ResolveUncached(bone);
            _cache[bone] = side;
            return side;
        }

        private BoneSide ResolveUncached(Transform bone) {
            var t = bone;
            while (t != null) {
                if (_humanoid.TryGetValue(t, out var b)) {
                    var name = b.ToString();
                    if (name.StartsWith("Left")) return BoneSide.Left;
                    if (name.StartsWith("Right")) return BoneSide.Right;
                    return BoneSide.Center;
                }
                t = t.parent;
            }
            return BoneSide.Unknown;
        }

        /// <summary>
        /// Derive the avatar's "left" sign on the X axis of Hips local space
        /// from the actual position of LeftUpperLeg. If LeftUpperLeg is at
        /// hipsLocal.x = +0.1, then +X is left. If at -0.1, -X is left.
        /// Falls back to +1 (Unity convention) when the bone isn't bound.
        /// </summary>
        private float ComputeLeftSign() {
            if (_animator == null || Hips == null) return 1f;
            var leftLeg = _animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            if (leftLeg == null) return 1f;
            var local = Hips.InverseTransformPoint(leftLeg.position);
            if (Mathf.Abs(local.x) < 0.0001f) return 1f;
            return Mathf.Sign(local.x);
        }

        /// <summary>
        /// Classify a world-space position as on the avatar's left, right,
        /// or center band. <paramref name="centerMargin"/> is in metres in
        /// Hips local space — vertices closer to the centerline than this
        /// don't get classified as either side (avoids spurious "this spine
        /// vertex is weighted to a left bone" reports).
        /// </summary>
        public BoneSide ClassifyWorldPosition(Vector3 worldPos, float centerMargin) {
            if (Hips == null) return BoneSide.Unknown;
            var local = Hips.InverseTransformPoint(worldPos);
            float signed = local.x * LeftSignInHipsLocal;
            if (signed > centerMargin) return BoneSide.Left;
            if (signed < -centerMargin) return BoneSide.Right;
            return BoneSide.Center;
        }
    }
}
