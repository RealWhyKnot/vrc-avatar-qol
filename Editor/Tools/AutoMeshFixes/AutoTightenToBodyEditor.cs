using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.Tools.AutoMeshFixes {

    [CustomEditor(typeof(AutoTightenToBody))]
    internal sealed class AutoTightenToBodyEditor : Editor {

        public override void OnInspectorGUI() {
            var setup = (AutoTightenToBody)target;
            EditorGUILayout.LabelField("Avatar QoL stored mesh fix", AvatarQolStyles.SectionTitle);
            EditorGUILayout.LabelField(
                "Edit this through the Auto Mesh Fixes window. The component stores the setup so model re-exports do not erase it.",
                AvatarQolStyles.Body);

            EditorGUILayout.Space(4);
            DrawStatus(setup);
            EditorGUILayout.Space(6);

            if (AvatarQolStyles.PrimaryButtonInline(
                    new GUIContent("Open Auto Mesh Fixes", "Open the UI for editing, previewing, and validating this setup."),
                    GUILayout.ExpandWidth(true))) {
                AutoMeshFixWindow.Open(setup);
            }
        }

        private static void DrawStatus(AutoTightenToBody setup) {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField("Clothing", setup.garmentRenderer != null ? setup.garmentRenderer.name : "(not chosen)");
                EditorGUILayout.LabelField("Body", setup.bodyRenderer != null ? setup.bodyRenderer.name : "(not chosen)");
                EditorGUILayout.LabelField("Runs", $"{(setup.processInPlayMode ? "play mode" : "no play mode")} / {(setup.processOnUpload ? "upload" : "no upload")}");
            }
        }
    }
}
