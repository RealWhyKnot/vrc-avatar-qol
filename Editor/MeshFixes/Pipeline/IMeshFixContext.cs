// IMeshFixContext.cs
//
// Per-pipeline-run scratch space available to every IMeshOperation. Holds
// the shared Session (mesh clones + originals), the active mode flags,
// the error/warning collectors, and the per-renderer "get or clone an
// editable mesh" helper that guarantees one clone per renderer per run.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal enum MeshFixMode {
        Validate,      // Plan only, no mutation; pipeline restores immediately
        Preview,       // Edit-mode preview via cloned avatar
        PlayMode,      // ExitingEditMode -> mutate scene avatars in place
        Upload,        // IVRCSDKPreprocessAvatarCallback for VRC SDK Build & Publish
    }

    internal interface IMeshFixContext {

        MeshFixMode Mode { get; }

        bool Verbose { get; }

        GameObject AvatarRoot { get; }

        IList<string> Errors { get; }
        IList<string> Warnings { get; }

        /// <summary>
        /// Per-renderer accumulating result counters surfaced in the post-run
        /// log and on the controller inspector.
        /// </summary>
        void RecordOpSuccess(string opId, SkinnedMeshRenderer renderer, string detail);

        /// <summary>
        /// Returns the renderer's in-memory editable mesh, cloning the
        /// original on first request and capturing it in the Session.
        /// Subsequent calls for the same renderer return the same clone so
        /// multiple ops share writes.
        ///
        /// Refuses (returns null + writes a warning) if the renderer has a
        /// Cloth component, which would desync from a mid-play sharedMesh
        /// swap.
        /// </summary>
        Mesh GetOrCloneEditableMesh(SkinnedMeshRenderer renderer);

        /// <summary>
        /// Reserve a blendshape name for an op on a renderer. Returns true
        /// if the reservation succeeds (no other op has claimed it); false
        /// if it collides, in which case the op should bail Apply (it
        /// should have noticed in Validate). Pipeline collision detection
        /// runs before Apply, so this is the second line of defense.
        /// </summary>
        bool TryReserveShape(SkinnedMeshRenderer renderer, string shapeName, IMeshOperation owner);
    }
}
