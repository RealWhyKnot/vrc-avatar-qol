// WhyKnotMeshFixControllerEditor.cs
//
// Inspector for the root coordinator. Shows the avatar-wide toggles and
// surfaces a read-only count of mesh-fix components discovered under
// the avatar so the user can see at-a-glance whether the pipeline
// "found" their setups.

using System.Linq;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Lifecycle;

namespace WhyKnot.AvatarQol.MeshFixes.UI {

    [CustomEditor(typeof(WhyKnotMeshFixController))]
    internal sealed class WhyKnotMeshFixControllerEditor : Editor {

        private SerializedProperty _enableAll;
        private SerializedProperty _verbose;

        private void OnEnable() {
            _enableAll = serializedObject.FindProperty("enableAll");
            _verbose = serializedObject.FindProperty("forceVerboseLog");
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();

            var controller = (WhyKnotMeshFixController)target;
            EditorGUILayout.LabelField("Avatar QoL - Mesh Fix Controller", AvatarQolStyles.SubsectionTitle);
            EditorGUILayout.HelpBox(
                "Optional coordinator for the mesh-fix pipeline. The pipeline still works without it; this component just lets you flip avatar-wide options in one place and exposes a count of stored fixes.",
                MessageType.Info);

            EditorGUI.showMixedValue = _enableAll.hasMultipleDifferentValues;
            EditorGUILayout.PropertyField(_enableAll, new GUIContent("Enable all mesh fixes",
                "Master switch. When off, preview / play-mode / upload all skip every Auto Mesh Fix setup on this avatar."));
            EditorGUI.showMixedValue = false;

            EditorGUI.showMixedValue = _verbose.hasMultipleDifferentValues;
            EditorGUILayout.PropertyField(_verbose, new GUIContent("Force verbose log",
                "Force the pipeline to log per-renderer details regardless of per-setup verbose flags."));
            EditorGUI.showMixedValue = false;

            EditorGUILayout.Space(6);
            DrawDiscovered(controller);

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("Open Auto Mesh Fixes",
                        "Open the window that lists every mesh fix on this avatar and runs Preview / Validate."),
                        GUILayout.Height(24))) {
                    MeshFixWindow.Open();
                }
            }

            if (serializedObject.ApplyModifiedProperties()) {
                if (PrefabUtility.IsPartOfPrefabInstance(controller)) {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
                }
            }
        }

        private static void DrawDiscovered(WhyKnotMeshFixController controller) {
            if (controller == null || controller.gameObject == null) return;
            var setups = controller.gameObject.GetComponentsInChildren<AutoTightenToBody>(true)
                .Where(s => s != null)
                .ToList();
            EditorGUILayout.LabelField($"Stored Auto Tighten To Body setups: {setups.Count}", AvatarQolStyles.Body);
            foreach (var setup in setups) {
                using (new EditorGUILayout.HorizontalScope()) {
                    GUILayout.Space(8);
                    var label = setup.garmentRenderer != null && setup.bodyRenderer != null
                        ? $"{setup.garmentRenderer.name} -> {setup.bodyRenderer.name}"
                        : $"{setup.name} (incomplete)";
                    EditorGUILayout.LabelField(label, AvatarQolStyles.Muted);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(44))) {
                        Selection.activeObject = setup.gameObject;
                        EditorGUIUtility.PingObject(setup.gameObject);
                    }
                }
            }
        }
    }
}
