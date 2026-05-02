// IPhysBonePreset.cs
//
// Contract for a PhysBone preset. A preset takes a BoneSelectionAnalysis
// (the user's selection plus everything we could derive about it) and
// returns a *plan* describing what components to create. The plan is then
// previewed in the UI and committed by PhysBonePlanApplier.
//
// This split — analyse → preset → plan → apply — exists so:
//   1. The user sees what's about to happen before mutation.
//   2. Presets can be unit-testable: feed in an analysis, assert on the
//      plan they produce.
//   3. Apply is a single step that knows nothing about the source preset,
//      so all destructive logic lives in one place with one Undo group.
//
// Adding a preset: drop a new file under Editor/Tools/PhysBonePreset/Presets/
// implementing IPhysBonePreset with a parameterless constructor. The
// window auto-discovers it via reflection.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal interface IPhysBonePreset {
        /// <summary>Stable identifier for serialization (e.g. "tail").</summary>
        string Id { get; }

        /// <summary>Display name shown in the UI ("Tail").</summary>
        string DisplayName { get; }

        /// <summary>One-sentence description shown next to the picker.</summary>
        string Description { get; }

        /// <summary>
        /// 0..1 confidence that this preset fits the selection. The window
        /// auto-suggests the highest-scoring preset. Generic should always
        /// return a low non-zero score so it's the fallback if nothing else
        /// matches.
        /// </summary>
        float SuggestionScore(BoneSelectionAnalysis analysis);

        /// <summary>
        /// Optional: explain how the SuggestionScore was reached. Returns
        /// per-signal contributions (positive or negative). Used by the UI
        /// to show "why this preset matched" tooltips on the score bar.
        /// Default implementation: empty.
        /// </summary>
        IEnumerable<ScoringSignal> ExplainScore(BoneSelectionAnalysis analysis);

        /// <summary>
        /// Build a plan describing what would be created. Should not mutate
        /// project state.
        /// </summary>
        PhysBonePlan BuildPlan(BoneSelectionAnalysis analysis);
    }

    internal readonly struct ScoringSignal {
        public readonly string Name;
        public readonly float  Contribution;
        public ScoringSignal(string name, float contribution) {
            Name = name; Contribution = contribution;
        }
    }

    /// <summary>
    /// Output of a preset. A list of PhysBone components and collider
    /// definitions, plus human-readable notes shown in the preview.
    /// </summary>
    internal sealed class PhysBonePlan {
        public string PresetId;
        public string PresetDisplayName;
        public List<PhysBoneSpec> PhysBones = new List<PhysBoneSpec>();
        public List<ColliderSpec> Colliders = new List<ColliderSpec>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// One PhysBone component to add. <see cref="Root"/> is the chain root
    /// (the bone the component will be added to). <see cref="ColliderRefs"/>
    /// is a list of indices into the plan's <see cref="PhysBonePlan.Colliders"/>
    /// list — the applier resolves them to runtime references after the
    /// colliders are instantiated.
    /// </summary>
    internal sealed class PhysBoneSpec {
        public Transform Root;
        public List<Transform> IgnoreTransforms = new List<Transform>();
        public List<int> ColliderRefs = new List<int>();

        // Numeric parameters. Defaulted to VRChat's defaults; presets adjust
        // only what they care about. The applier writes every value so
        // stale fields from a previous component don't bleed through.
        public float Pull             = 0.2f;
        public float Spring           = 0.5f;
        public float Stiffness        = 0.4f;
        public float Gravity          = 0f;
        public float GravityFalloff   = 0f;
        public float Immobile         = 0f;
        public ImmobileTypeKind ImmobileType = ImmobileTypeKind.None;
        public float Radius           = 0.05f;
        public AllowKind AllowCollision = AllowKind.True;
        public AllowKind AllowGrabbing  = AllowKind.True;
        public AllowKind AllowPosing    = AllowKind.True;
        public float MaxStretch       = 0f;
        public bool   IsAnimated      = false;
        public string Parameter       = "";

        // Notes specific to this PhysBone, shown under it in the preview.
        public string Note = "";
    }

    internal enum ImmobileTypeKind {
        None,         // VRC: AllMotion (drives no immobile component)
        AllMotion,    // pinned in world
        WorldRotation // resists world rotation only — typical for ears
    }

    internal enum AllowKind {
        True,
        False,
        Other,
    }

    /// <summary>
    /// One collider to add. <see cref="AttachTo"/> is the GameObject that
    /// will host the VRCPhysBoneCollider component (we make a child under
    /// the bone with this as parent, so the collider is selectable / nameable
    /// without polluting the bone itself). <see cref="RootTransform"/> is
    /// what the collider's rootTransform points at.
    /// </summary>
    internal sealed class ColliderSpec {
        public string Name = "PhysBoneCollider";
        public Transform AttachTo;            // parent for the new GameObject
        public Transform RootTransform;       // bone the collider tracks
        public ColliderShape Shape = ColliderShape.Capsule;
        public float Radius = 0.04f;
        public float Height = 0.2f;
        public Vector3 Position = Vector3.zero;
        public Vector3 EulerRotation = Vector3.zero;
        public bool InsideBounds = false;
    }

    internal enum ColliderShape {
        Sphere,
        Capsule,
        Plane,
    }
}
