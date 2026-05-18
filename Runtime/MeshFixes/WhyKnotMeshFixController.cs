// WhyKnotMeshFixController.cs
//
// Optional root-level coordinator for the mesh-fix pipeline. Auto-added
// to the avatar Animator GameObject when the user opens Auto Mesh Fixes
// against an avatar that does not have one. Holds avatar-wide overrides
// (master enable, verbose log) and acts as a discoverable anchor for the
// pipeline inspector. Per-component AutoTightenToBody storage is still
// the source of truth -- this controller is convenience, not gating.
//
// IEditorOnly so the VRChat SDK strips it on upload; field shape is
// intentionally tiny so future additions can stay backwards-compatible.

using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    [AddComponentMenu("WhyKnot/Avatar QoL/Mesh Fix Controller")]
    [DisallowMultipleComponent]
    public sealed class WhyKnotMeshFixController : MonoBehaviour, IEditorOnly {

        [Tooltip("Master enable for every mesh fix on this avatar. Turn off to disable preview, play-mode processing, and upload processing in one place without touching individual setups.")]
        public bool enableAll = true;

        [Tooltip("Force verbose pipeline logging regardless of per-setup flags. Useful when debugging a misbehaving fix without editing individual components.")]
        public bool forceVerboseLog;
    }
}
