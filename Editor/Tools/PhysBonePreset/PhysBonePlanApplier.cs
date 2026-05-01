// PhysBonePlanApplier.cs
//
// The only file that touches VRC SDK types directly. Wrapped in
// #if VRC_SDK_VRCSDK3 so the rest of vrc-avatar-qol still compiles when
// the SDK isn't installed; outside that guard we expose a stub that
// reports "SDK not detected" so the UI can warn rather than crash.
//
// Apply order matters:
//   1. Create collider GameObjects + components first, in plan order.
//   2. Create PhysBone components, resolving each spec's ColliderRefs to
//      the actual VRCPhysBoneColliderBase instances created in step 1.
//   3. Wrap the whole thing in one Undo group so Ctrl+Z reverts cleanly.
//
// Every PhysBone field we care about is written explicitly — never
// assume a freshly-added component starts at the values we want, because
// VRChat ships with non-zero defaults for some of them.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

#if VRC_SDK_VRCSDK3
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal static class PhysBonePlanApplier {

        internal static bool SdkAvailable {
            get {
#if VRC_SDK_VRCSDK3
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Returns the number of PhysBones created on success, or -1 with a
        /// populated <paramref name="error"/> on failure. Failures roll back
        /// the entire group so the user never sees a half-applied plan.
        /// </summary>
        internal static int Apply(PhysBonePlan plan, out string error) {
            error = null;
#if !VRC_SDK_VRCSDK3
            error = "VRChat SDK 3 (PhysBone) is not installed in this project.";
            return -1;
#else
            if (plan == null || plan.PhysBones == null || plan.PhysBones.Count == 0) {
                error = "Plan has no PhysBones to add.";
                return -1;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Avatar QoL: Apply {plan.PresetDisplayName} preset");

            try {
                // Step 1: instantiate colliders.
                var colliderRefs = new List<VRCPhysBoneColliderBase>(plan.Colliders.Count);
                foreach (var spec in plan.Colliders) {
                    colliderRefs.Add(CreateCollider(spec));
                }

                // Step 2: instantiate PhysBones with resolved collider refs.
                int created = 0;
                foreach (var spec in plan.PhysBones) {
                    if (spec.Root == null) continue;
                    var pb = Undo.AddComponent<VRCPhysBone>(spec.Root.gameObject);
                    Undo.RegisterCompleteObjectUndo(pb, "Configure PhysBone");
                    ApplySpec(pb, spec, colliderRefs);
                    EditorUtility.SetDirty(pb);
                    created++;
                }

                Undo.CollapseUndoOperations(group);
                return created;
            } catch (System.Exception ex) {
                Undo.RevertAllInCurrentGroup();
                Debug.LogException(ex);
                error = ex.Message;
                return -1;
            }
#endif
        }

#if VRC_SDK_VRCSDK3
        private static VRCPhysBoneColliderBase CreateCollider(ColliderSpec spec) {
            // Each collider gets its own GameObject under the AttachTo
            // bone — keeps the collider selectable / nameable without
            // dropping a component on the bone itself (which could collide
            // with future tools that scan bones for components).
            var holder = new GameObject(spec.Name);
            Undo.RegisterCreatedObjectUndo(holder, "Create PhysBoneCollider holder");
            holder.transform.SetParent(spec.AttachTo, worldPositionStays: false);
            holder.transform.localPosition = Vector3.zero;
            holder.transform.localRotation = Quaternion.identity;

            var col = Undo.AddComponent<VRCPhysBoneCollider>(holder);
            Undo.RegisterCompleteObjectUndo(col, "Configure PhysBoneCollider");
            col.shapeType    = ToShape(spec.Shape);
            col.rootTransform = spec.RootTransform;
            col.radius       = spec.Radius;
            col.height       = spec.Height;
            col.position     = spec.Position;
            col.rotation     = Quaternion.Euler(spec.EulerRotation);
            col.insideBounds = spec.InsideBounds;
            EditorUtility.SetDirty(col);
            return col;
        }

        private static void ApplySpec(VRCPhysBone pb, PhysBoneSpec spec, List<VRCPhysBoneColliderBase> colliders) {
            // Root: PhysBone simulates the bone the component is on plus all
            // descendants. Leaving rootTransform unset = the GameObject the
            // PhysBone is attached to, which is what we want here.
            pb.rootTransform = null;
            pb.ignoreTransforms = new List<Transform>(spec.IgnoreTransforms);

            pb.pull            = spec.Pull;
            pb.spring          = spec.Spring;
            pb.stiffness       = spec.Stiffness;
            pb.gravity         = spec.Gravity;
            pb.gravityFalloff  = spec.GravityFalloff;
            pb.immobile        = spec.Immobile;
            pb.immobileType    = ToImmobile(spec.ImmobileType);
            pb.radius          = spec.Radius;
            pb.allowCollision  = ToAdvanced(spec.AllowCollision);
            pb.allowGrabbing   = ToAdvanced(spec.AllowGrabbing);
            pb.allowPosing     = ToAdvanced(spec.AllowPosing);
            pb.maxStretch      = spec.MaxStretch;
            pb.isAnimated      = spec.IsAnimated;
            pb.parameter       = spec.Parameter ?? "";

            // Collider list: copy in only the ones referenced by this spec.
            pb.colliders = new List<VRCPhysBoneColliderBase>(spec.ColliderRefs.Count);
            foreach (var idx in spec.ColliderRefs) {
                if (idx >= 0 && idx < colliders.Count && colliders[idx] != null) {
                    pb.colliders.Add(colliders[idx]);
                }
            }
        }

        private static VRCPhysBoneColliderBase.ShapeType ToShape(ColliderShape shape) {
            switch (shape) {
                case ColliderShape.Sphere:  return VRCPhysBoneColliderBase.ShapeType.Sphere;
                case ColliderShape.Capsule: return VRCPhysBoneColliderBase.ShapeType.Capsule;
                case ColliderShape.Plane:   return VRCPhysBoneColliderBase.ShapeType.Plane;
                default:                    return VRCPhysBoneColliderBase.ShapeType.Capsule;
            }
        }

        private static VRCPhysBoneBase.ImmobileType ToImmobile(ImmobileTypeKind kind) {
            // ImmobileType has had different second-value names across SDK
            // versions (WorldRotation / World / etc). The integer values are
            // stable: 0 = AllMotion, 1 = the rotation-only mode. Cast from int
            // so we don't pin a specific name we may not have on every SDK.
            int value;
            switch (kind) {
                case ImmobileTypeKind.AllMotion:     value = 0; break;
                case ImmobileTypeKind.WorldRotation: value = 1; break;
                default:                              value = 0; break;
            }
            return (VRCPhysBoneBase.ImmobileType)value;
        }

        private static VRCPhysBoneBase.AdvancedBool ToAdvanced(AllowKind kind) {
            switch (kind) {
                case AllowKind.True:  return VRCPhysBoneBase.AdvancedBool.True;
                case AllowKind.False: return VRCPhysBoneBase.AdvancedBool.False;
                default:              return VRCPhysBoneBase.AdvancedBool.Other;
            }
        }
#endif
    }
}
