// AutoTightenToBodyEditor.cs
//
// Replacement for the previous dead-end inspector (Open Auto Mesh Fixes
// button + read-only labels). Shows the full PropertyField surface
// inline so selecting the component in the inspector reveals state
// without forcing the user into a separate window.
//
// Multi-target safe: every field draw resets EditorGUI.showMixedValue
// after the call so the mixed-value indicator does not bleed into
// neighbouring editors (mirror of Modular Avatar 1.17.0 #1936).
//
// After every property change, conditionally records prefab-instance
// overrides so changes made on a prefab-instance avatar survive the
// next prefab refresh.

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Lifecycle;
using WhyKnot.AvatarQol.MeshFixes.Pipeline;

namespace WhyKnot.AvatarQol.MeshFixes.UI {

    [CustomEditor(typeof(AutoTightenToBody))]
    [CanEditMultipleObjects]
    internal sealed class AutoTightenToBodyEditor : Editor {

        private SerializedProperty _garment;
        private SerializedProperty _body;
        private SerializedProperty _garmentName;
        private SerializedProperty _bodyName;
        private SerializedProperty _surfaceOffset;
        private SerializedProperty _maxProjection;
        private SerializedProperty _createTighten;
        private SerializedProperty _tightenTo100;
        private SerializedProperty _hideDepth;
        private SerializedProperty _hideRadius;
        private SerializedProperty _createHide;
        private SerializedProperty _hideTo100;
        private SerializedProperty _hideMode;
        private SerializedProperty _selectionMode;
        private SerializedProperty _processInPlay;
        private SerializedProperty _processOnUpload;
        private SerializedProperty _verbose;

        private void OnEnable() {
            _garment = serializedObject.FindProperty("garmentRenderer");
            _body = serializedObject.FindProperty("bodyRenderer");
            _garmentName = serializedObject.FindProperty("garmentTightenBlendShapeName");
            _bodyName = serializedObject.FindProperty("bodyHideBlendShapeName");
            _surfaceOffset = serializedObject.FindProperty("garmentSurfaceOffset");
            _maxProjection = serializedObject.FindProperty("maxProjectionDistance");
            _createTighten = serializedObject.FindProperty("createGarmentTightenShape");
            _tightenTo100 = serializedObject.FindProperty("setGarmentTightenWeightTo100");
            _hideDepth = serializedObject.FindProperty("bodyHideDepth");
            _hideRadius = serializedObject.FindProperty("bodyHideRadius");
            _createHide = serializedObject.FindProperty("createBodyHideShape");
            _hideTo100 = serializedObject.FindProperty("setBodyHideWeightTo100");
            _hideMode = serializedObject.FindProperty("bodyHideMode");
            _selectionMode = serializedObject.FindProperty("selectionMode");
            _processInPlay = serializedObject.FindProperty("processInPlayMode");
            _processOnUpload = serializedObject.FindProperty("processOnUpload");
            _verbose = serializedObject.FindProperty("verboseLog");
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            EditorGUILayout.LabelField("Auto Tighten To Body", AvatarQolStyles.SubsectionTitle);
            EditorGUILayout.HelpBox(
                "Stored mesh fix. The pipeline picks this up automatically during preview, play mode, and upload.",
                MessageType.Info);

            // ---- Meshes ----
            EditorGUILayout.Space(4);
            DrawProp(_garment, "Clothing mesh", "The clothing, accessory, or garment mesh to tighten.");
            DrawProp(_body, "Body mesh", "The body mesh to project toward and optionally hide under the clothing.");

            DrawReadWriteRemediation(_garment.objectReferenceValue as SkinnedMeshRenderer, "Clothing");
            DrawReadWriteRemediation(_body.objectReferenceValue as SkinnedMeshRenderer, "Body");

            // ---- Garment tighten ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Garment tighten", EditorStyles.boldLabel);
            DrawProp(_createTighten, "Tighten clothing", "Generate a blendshape that pulls the clothing toward the body surface.");
            using (new EditorGUI.DisabledScope(!_createTighten.boolValue)) {
                DrawSlider(_surfaceOffset, "Surface gap", "Small gap left between body and clothing.", 0f, 0.02f);
                DrawSlider(_maxProjection, "Search distance", "How far a clothing vertex can look for body surface.", 0.005f, 0.2f);
            }

            // ---- Body hide ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Body hide", EditorStyles.boldLabel);
            DrawProp(_createHide, "Hide body underneath", "Generate a body blendshape that collapses nearby body vertices under the clothing.");
            using (new EditorGUI.DisabledScope(!_createHide.boolValue)) {
                DrawSlider(_hideRadius, "Hide spread", "How close body vertices must be to the clothing to be hidden.", 0.001f, 0.12f);
                DrawSlider(_hideDepth, "Hide depth", "How far selected body vertices move inward.", 0.001f, 0.2f);
                DrawProp(_hideMode, "Hide direction", "How the body vertices under the clothing should move.");
            }

            // ---- Advanced ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            DrawProp(_selectionMode, "Clothing mask", "Optional vertex color channel used to limit which clothing vertices tighten.");
            DrawProp(_garmentName, "Tighten shape name", "Blendshape name to write on the clothing mesh.");
            DrawProp(_bodyName, "Body hide shape name", "Blendshape name to write on the body mesh.");
            DrawProp(_tightenTo100, "Preview clothing at 100", "");
            DrawProp(_hideTo100, "Preview body hide at 100", "");
            DrawProp(_processInPlay, "Run in play mode", "");
            DrawProp(_processOnUpload, "Run on upload", "");
            DrawProp(_verbose, "Verbose console log", "");

            // ---- Actions ----
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("Open Auto Mesh Fixes",
                        "Open the window that lists every mesh fix on this avatar and runs Preview / Validate."),
                        GUILayout.Height(24))) {
                    MeshFixWindow.Open(target as AutoTightenToBody);
                }
                using (new EditorGUI.DisabledScope(targets.Length != 1 || target == null)) {
                    if (GUILayout.Button(new GUIContent("Preview this avatar",
                            "Start the Avatar QoL preview against this component's avatar. Runs every enabled mesh fix on the avatar so you can see the combined result."),
                            GUILayout.Height(24))) {
                        var avatarRoot = ResolveAvatarRoot(target as AutoTightenToBody);
                        if (avatarRoot != null) MeshFixPreviewController.StartPreview(avatarRoot);
                    }
                }
            }

            if (MeshFixSessionState.IsPreviewActive()) {
                EditorGUILayout.Space(2);
                EditorGUILayout.HelpBox(
                    "An Auto Mesh Fixes preview is active. Stop it from the Auto Mesh Fixes window before running Weight Sanity Check or other destructive tools on the same renderers.",
                    MessageType.Info);
            }

            if (serializedObject.ApplyModifiedProperties()) {
                RecordPrefabOverrideIfNeeded();
            }
        }

        private void DrawProp(SerializedProperty prop, string label, string tooltip) {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip));
            EditorGUI.showMixedValue = false;
        }

        private void DrawSlider(SerializedProperty prop, string label, string tooltip, float min, float max) {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            float next = EditorGUILayout.Slider(new GUIContent(label, tooltip), prop.floatValue, min, max);
            EditorGUI.showMixedValue = false;
            if (!Mathf.Approximately(next, prop.floatValue)) prop.floatValue = next;
        }

        private void DrawReadWriteRemediation(SkinnedMeshRenderer renderer, string role) {
            if (renderer == null || renderer.sharedMesh == null || renderer.sharedMesh.isReadable) return;
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.HelpBox($"{role} mesh '{renderer.sharedMesh.name}' is not Read/Write enabled. The pipeline will refuse to run.", MessageType.Warning);
                if (GUILayout.Button("Fix Read/Write", GUILayout.Width(120), GUILayout.Height(38))) {
                    EnableReadWrite(renderer.sharedMesh);
                }
            }
        }

        private void RecordPrefabOverrideIfNeeded() {
            foreach (var t in targets) {
                if (t == null) continue;
                if (PrefabUtility.IsPartOfPrefabInstance(t)) {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                }
            }
        }

        private static GameObject ResolveAvatarRoot(AutoTightenToBody setup) {
            if (setup == null) return null;
            var animator = setup.GetComponentInParent<Animator>(true);
            return animator != null ? animator.gameObject : setup.gameObject;
        }

        private static void EnableReadWrite(Mesh mesh) {
            if (mesh == null) return;
            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) return;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) {
                Debug.LogWarning($"[Avatar QoL] {path} is not a ModelImporter asset; cannot toggle Read/Write here.");
                return;
            }
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }
}
