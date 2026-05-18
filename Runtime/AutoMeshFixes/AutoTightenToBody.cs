using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    public enum BodyHideMode {
        PushAlongNormal,
        CollapseToRendererRoot,
        CollapseToNearestBone,
    }

    public enum VertexSelectionMode {
        AllVertices,
        VertexColorRed,
        VertexColorGreen,
        VertexColorBlue,
        VertexColorAlpha,
    }

    [AddComponentMenu("WhyKnot/Avatar QoL/Auto Tighten To Body")]
    [DisallowMultipleComponent]
    public sealed class AutoTightenToBody : MonoBehaviour, IEditorOnly {

        [Header("Meshes")]
        public SkinnedMeshRenderer garmentRenderer;
        public SkinnedMeshRenderer bodyRenderer;

        [Header("Generated Blendshapes")]
        public string garmentTightenBlendShapeName = "AUTO_TightenToBody";
        public string bodyHideBlendShapeName = "AUTO_HideBodyUnderGarment";

        [Header("Garment Tighten")]
        [Min(0f)] public float garmentSurfaceOffset = 0.003f;
        [Min(0f)] public float maxProjectionDistance = 0.08f;
        public bool createGarmentTightenShape = true;
        public bool setGarmentTightenWeightTo100 = true;

        [Header("Body Hide")]
        [Min(0f)] public float bodyHideDepth = 0.05f;
        [Min(0f)] public float bodyHideRadius = 0.02f;
        public bool createBodyHideShape = true;
        public bool setBodyHideWeightTo100 = true;

        [Header("Selection")]
        public BodyHideMode bodyHideMode = BodyHideMode.PushAlongNormal;
        public VertexSelectionMode selectionMode = VertexSelectionMode.AllVertices;

        [Header("When To Run")]
        public bool processInPlayMode = true;
        public bool processOnUpload = true;
        public bool verboseLog;

        private void Reset() {
            garmentRenderer = GetComponent<SkinnedMeshRenderer>();
            if (bodyRenderer == null) {
                var animator = GetComponentInParent<Animator>();
                if (animator != null) {
                    foreach (var renderer in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                        if (renderer != garmentRenderer && renderer.name.ToLowerInvariant().Contains("body")) {
                            bodyRenderer = renderer;
                            break;
                        }
                    }
                }
            }
        }
    }
}
