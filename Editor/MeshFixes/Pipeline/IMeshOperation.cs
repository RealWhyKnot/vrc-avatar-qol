// IMeshOperation.cs
//
// One unit of work the pipeline will execute against a single avatar's
// mesh state. Operations declare the renderers they read from, the
// renderers they write to, and the blendshape names they will produce
// so the pipeline can detect collisions before any mutation happens.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal interface IMeshOperation {

        /// <summary>
        /// Stable identifier used in logs, the plan view, and the shape-name
        /// registry. Recommended format: <c>"{ownerComponentInstanceId}:{opKind}"</c>.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Human-readable short name for the plan view, e.g. "Garment Tighten".
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Component this operation is sourced from. Used to ping in the
        /// hierarchy, to record undo, and to detect when the component has
        /// been destroyed mid-pipeline (skip rather than crash).
        /// </summary>
        Object Owner { get; }

        /// <summary>
        /// Renderers this operation reads from but does not mutate. Used by
        /// the pipeline only for plan-display and dependency tracking.
        /// </summary>
        IEnumerable<SkinnedMeshRenderer> Reads { get; }

        /// <summary>
        /// Renderers this operation mutates (sharedMesh swap, blendshape add).
        /// The pipeline captures the original sharedMesh for each of these
        /// into the Session before any Apply runs.
        /// </summary>
        IEnumerable<SkinnedMeshRenderer> Writes { get; }

        /// <summary>
        /// Blendshape names this operation will produce on its Writes targets.
        /// Used by the pipeline for collision detection; an op that produces
        /// the same name on the same renderer as another op fails Plan.
        /// </summary>
        IEnumerable<(SkinnedMeshRenderer Renderer, string ShapeName)> ProducedShapes { get; }

        /// <summary>
        /// Cheap input validation. Adds to ctx.Errors / ctx.Warnings; returns
        /// true if the op is structurally able to Apply (renderers non-null,
        /// meshes readable, parameters in range). The pipeline collects every
        /// op's Validate output before running any Apply so the user sees all
        /// problems at once.
        /// </summary>
        bool Validate(IMeshFixContext ctx);

        /// <summary>
        /// Perform the mesh mutation. May allocate work meshes via
        /// ctx.GetOrCloneEditableMesh, may add blendshape frames via the
        /// utility helpers, must not call AssetDatabase, must not register
        /// Undo for in-memory clones (the Session owns clone lifetime).
        /// Throwing aborts ONLY this op's work for the current renderer;
        /// the pipeline rolls back the renderer's Session capture and
        /// continues with other ops.
        /// </summary>
        void Apply(IMeshFixContext ctx);
    }
}
