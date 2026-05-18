using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.Tools.AutoMeshFixes {

    internal sealed class AutoMeshFixWindow : EditorWindow {

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#auto-mesh-fixes";

        [SerializeField] private Animator _avatar;
        [SerializeField] private SkinnedMeshRenderer _newGarment;
        [SerializeField] private SkinnedMeshRenderer _newBody;
        [SerializeField] private bool _showAdvanced;

        private Vector2 _scroll;
        private string _lastMessage = "";
        private AutoTightenToBody _focusedSetup;

        internal static void Open(AutoTightenToBody focus = null) {
            var w = GetWindow<AutoMeshFixWindow>(false, "Auto Mesh Fixes", true);
            w.titleContent = new GUIContent("Avatar QoL - Auto Mesh Fixes");
            w.minSize = new Vector2(640, 520);
            if (focus != null) {
                w._focusedSetup = focus;
                w._avatar = focus.GetComponentInParent<Animator>(true);
                w._newGarment = focus.garmentRenderer;
                w._newBody = focus.bodyRenderer;
            } else {
                w.PrefillFromSelection();
            }
            w.Show();
            w.Focus();
        }

        private void OnGUI() {
            DrawTitleBar();
            AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Info,
                "Create the setup here. Avatar QoL stores it on small editor-only components so Blender re-exports do not wipe your fixes.");

            DrawAvatarPicker();
            EditorGUILayout.Space(2);
            DrawPreviewBar();
            EditorGUILayout.Space(2);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawCreateSetup();
            EditorGUILayout.Space(2);
            DrawExistingSetups();
            EditorGUILayout.EndScrollView();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Auto Mesh Fixes",
                        "Nondestructive garment tighten and body hide blendshapes generated during preview, play mode, and upload."),
                    AvatarQolStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawAvatarPicker() {
            using (AvatarQolStyles.Section("1. Avatar")) {
                AvatarQolStyles.LabeledField(
                    new GUIContent("Avatar", "The avatar that owns the clothing and body meshes."),
                    () => {
                        var next = (Animator)EditorGUILayout.ObjectField(_avatar, typeof(Animator), true);
                        if (next != _avatar) {
                            _avatar = next;
                            _newBody = GuessBodyRenderer(_avatar);
                            _lastMessage = "";
                        }
                    });

                if (_avatar == null) {
                    AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                        "Choose the avatar Animator first. Then add one setup per clothing mesh.");
                }
            }
        }

        private void DrawPreviewBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(_avatar == null)) {
                    if (AutoMeshFixPreviewController.IsPreviewing) {
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.85f, 0.25f, 0.20f);
                        if (GUILayout.Button(
                                new GUIContent("Stop Previewing",
                                    "Restore the original avatar visibility and remove the generated preview copy."),
                                AvatarQolStyles.PrimaryButton,
                                GUILayout.Width(150))) {
                            AutoMeshFixPreviewController.StopPreview();
                            _lastMessage = "Preview stopped.";
                        }
                        GUI.backgroundColor = prev;
                    } else if (AvatarQolStyles.PrimaryButtonInline(
                                   new GUIContent("Preview",
                                       "Hide the current avatar and show a temporary processed copy. This does not move your Scene view."),
                                   GUILayout.Width(120))) {
                        var result = AutoMeshFixPreviewController.StartPreview(_avatar.gameObject);
                        _lastMessage = BuildMessage(result);
                    }
                }

                using (new EditorGUI.DisabledScope(_avatar == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Validate",
                                "Check the setups and report missing meshes or unreadable imports without keeping a preview copy."),
                            GUILayout.Height(28), GUILayout.Width(90))) {
                        var result = AutoMeshFixProcessor.ProcessAvatar(_avatar.gameObject, new AutoMeshFixProcessor.Options {
                            Preview = true,
                            Verbose = HasVerboseSetup(_avatar.gameObject),
                        });
                        result.Session.Restore();
                        _lastMessage = BuildMessage(result);
                    }
                }

                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_lastMessage)) {
                    EditorGUILayout.LabelField(_lastMessage, AvatarQolStyles.Muted);
                }
            }
        }

        private void DrawCreateSetup() {
            using (AvatarQolStyles.Section("2. Add clothing fix",
                    "Pick one clothing mesh and the body mesh it should fit against. The setup is stored on the clothing object.")) {
                using (new EditorGUI.DisabledScope(_avatar == null)) {
                    AvatarQolStyles.LabeledField(
                        new GUIContent("Clothing mesh", "The clothing, accessory, or garment mesh to tighten."),
                        () => _newGarment = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_newGarment, typeof(SkinnedMeshRenderer), true));
                    AvatarQolStyles.LabeledField(
                        new GUIContent("Body mesh", "The body mesh to project toward and optionally hide under the clothing."),
                        () => _newBody = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_newBody, typeof(SkinnedMeshRenderer), true));

                    using (new EditorGUI.DisabledScope(_newGarment == null || _newBody == null)) {
                        if (GUILayout.Button(
                                new GUIContent("Add clothing fix",
                                    "Create a stored setup on the clothing object. You can edit it in the list below."),
                                GUILayout.Height(28), GUILayout.Width(140))) {
                            AddSetup();
                        }
                    }

                    if (_newBody == null && _avatar != null) {
                        EditorGUILayout.LabelField("Tip: drag the avatar body mesh into Body mesh. It is usually named Body, BodyMesh, or BaseBody.", AvatarQolStyles.Muted);
                    }
                }
            }
        }

        private void DrawExistingSetups() {
            using (AvatarQolStyles.Section("3. Stored fixes",
                    "These are the editor-only components saved on your avatar. They generate temporary blendshapes during preview, play mode, and upload.")) {
                var setups = GetSetups();
                if (setups.Count == 0) {
                    EditorGUILayout.LabelField("No stored mesh fixes yet.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }

                foreach (var setup in setups) {
                    DrawSetupCard(setup);
                    AvatarQolStyles.Divider();
                }
            }
        }

        private void DrawSetupCard(AutoTightenToBody setup) {
            if (setup == null) return;
            var so = new SerializedObject(setup);
            so.Update();

            var garment = so.FindProperty("garmentRenderer");
            var body = so.FindProperty("bodyRenderer");
            var title = $"{NameOf(garment.objectReferenceValue)} -> {NameOf(body.objectReferenceValue)}";

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(title, AvatarQolStyles.SubsectionTitle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Select", "Select the stored setup component."), EditorStyles.miniButton, GUILayout.Width(52))) {
                        Selection.activeObject = setup.gameObject;
                        EditorGUIUtility.PingObject(setup.gameObject);
                    }
                    if (GUILayout.Button(new GUIContent("Remove", "Delete this stored setup."), EditorStyles.miniButton, GUILayout.Width(62))) {
                        Undo.DestroyObjectImmediate(setup);
                        _lastMessage = "Removed stored setup.";
                        return;
                    }
                }

                EditorGUILayout.PropertyField(garment, new GUIContent("Clothing mesh", "The mesh to tighten."));
                EditorGUILayout.PropertyField(body, new GUIContent("Body mesh", "The body mesh used as the fit target."));

                EditorGUILayout.Space(2);
                EditorGUILayout.PropertyField(so.FindProperty("createGarmentTightenShape"),
                    new GUIContent("Tighten clothing", "Generate a blendshape that pulls the clothing toward the body surface."));
                using (new EditorGUI.DisabledScope(!so.FindProperty("createGarmentTightenShape").boolValue)) {
                    Slider(so.FindProperty("garmentSurfaceOffset"), "Surface gap", "Small gap left between body and clothing.", 0f, 0.02f);
                    Slider(so.FindProperty("maxProjectionDistance"), "Search distance", "How far a clothing vertex can look for body surface.", 0.005f, 0.2f);
                }

                EditorGUILayout.PropertyField(so.FindProperty("createBodyHideShape"),
                    new GUIContent("Hide body underneath", "Generate a body blendshape that collapses nearby body vertices under the clothing."));
                using (new EditorGUI.DisabledScope(!so.FindProperty("createBodyHideShape").boolValue)) {
                    Slider(so.FindProperty("bodyHideRadius"), "Hide spread", "How close body vertices must be to the clothing to be hidden.", 0.001f, 0.12f);
                    Slider(so.FindProperty("bodyHideDepth"), "Hide depth", "How far selected body vertices move inward.", 0.001f, 0.2f);
                    BodyHideModeField(so.FindProperty("bodyHideMode"));
                }

                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true, AvatarQolStyles.FoldoutHeader);
                if (_showAdvanced) {
                    SelectionModeField(so.FindProperty("selectionMode"));
                    EditorGUILayout.PropertyField(so.FindProperty("garmentTightenBlendShapeName"),
                        new GUIContent("Clothing shape name"));
                    EditorGUILayout.PropertyField(so.FindProperty("bodyHideBlendShapeName"),
                        new GUIContent("Body hide shape name"));
                    EditorGUILayout.PropertyField(so.FindProperty("setGarmentTightenWeightTo100"),
                        new GUIContent("Preview clothing at 100"));
                    EditorGUILayout.PropertyField(so.FindProperty("setBodyHideWeightTo100"),
                        new GUIContent("Preview body hide at 100"));
                    EditorGUILayout.PropertyField(so.FindProperty("processInPlayMode"),
                        new GUIContent("Run in play mode"));
                    EditorGUILayout.PropertyField(so.FindProperty("processOnUpload"),
                        new GUIContent("Run on upload"));
                    EditorGUILayout.PropertyField(so.FindProperty("verboseLog"),
                        new GUIContent("Verbose console log"));
                }
            }

            so.ApplyModifiedProperties();
        }

        private void Slider(SerializedProperty prop, string label, string tooltip, float min, float max) {
            prop.floatValue = EditorGUILayout.Slider(new GUIContent(label, tooltip), prop.floatValue, min, max);
        }

        private void BodyHideModeField(SerializedProperty prop) {
            var labels = new[] {
                new GUIContent("Push inward", "Move selected body vertices inward along the body normal."),
                new GUIContent("Collapse toward body root", "Move selected body vertices toward the renderer root."),
                new GUIContent("Collapse toward nearest bone", "Move selected body vertices toward the bone that already controls them most."),
            };
            prop.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Hide direction", "How the body vertices under the clothing should move."),
                prop.enumValueIndex,
                labels);
        }

        private void SelectionModeField(SerializedProperty prop) {
            var labels = new[] {
                new GUIContent("Use whole clothing mesh", "Every clothing vertex can be tightened."),
                new GUIContent("Use red vertex color", "Only vertices with red vertex color are tightened."),
                new GUIContent("Use green vertex color", "Only vertices with green vertex color are tightened."),
                new GUIContent("Use blue vertex color", "Only vertices with blue vertex color are tightened."),
                new GUIContent("Use alpha vertex color", "Only vertices with alpha vertex color are tightened."),
            };
            prop.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Clothing mask", "Optional vertex color channel used to limit which clothing vertices tighten."),
                prop.enumValueIndex,
                labels);
        }

        private void AddSetup() {
            if (_newGarment == null || _newBody == null) return;
            if (_avatar != null && !_newGarment.transform.IsChildOf(_avatar.transform)) {
                _lastMessage = "Clothing mesh is not under the selected avatar.";
                return;
            }

            var setup = _newGarment.GetComponent<AutoTightenToBody>();
            if (setup == null) {
                setup = Undo.AddComponent<AutoTightenToBody>(_newGarment.gameObject);
            } else {
                Undo.RecordObject(setup, "Update Avatar QoL mesh fix");
            }
            setup.garmentRenderer = _newGarment;
            setup.bodyRenderer = _newBody;
            setup.garmentTightenBlendShapeName = $"AUTO_Tighten_{_newGarment.name}";
            setup.bodyHideBlendShapeName = $"AUTO_HideBody_{_newGarment.name}";
            EditorUtility.SetDirty(setup);
            _focusedSetup = setup;
            Selection.activeObject = setup.gameObject;
            EditorGUIUtility.PingObject(setup.gameObject);
            _lastMessage =
                $"Stored Auto Tighten To Body on {AvatarQol.GetGameObjectPath(setup.gameObject)}. " +
                "Select that clothing mesh to see the component, or use this window to edit it.";
        }

        private List<AutoTightenToBody> GetSetups() {
            if (_avatar == null) return new List<AutoTightenToBody>();
            return _avatar.GetComponentsInChildren<AutoTightenToBody>(true)
                .Where(s => s != null)
                .OrderByDescending(s => s == _focusedSetup)
                .ThenBy(s => s.garmentRenderer != null ? s.garmentRenderer.name : s.name)
                .ToList();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;

            _avatar = go.GetComponent<Animator>() ??
                      go.GetComponentInParent<Animator>(true) ??
                      go.GetComponentInChildren<Animator>(true);
            var renderer = go.GetComponent<SkinnedMeshRenderer>() ??
                           go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (renderer != null) _newGarment = renderer;
            if (_avatar != null) _newBody = GuessBodyRenderer(_avatar, _newGarment);
        }

        private static SkinnedMeshRenderer GuessBodyRenderer(Animator avatar, SkinnedMeshRenderer exclude = null) {
            if (avatar == null) return null;
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r != exclude && r.sharedMesh != null)
                .ToList();
            var body = renderers.FirstOrDefault(r => r.name.ToLowerInvariant().Contains("body"));
            if (body != null) return body;
            return renderers.OrderByDescending(r => r.sharedMesh != null ? r.sharedMesh.vertexCount : 0).FirstOrDefault();
        }

        private static string NameOf(Object obj) {
            return obj != null ? obj.name : "(missing)";
        }

        private static bool HasVerboseSetup(GameObject avatarRoot) {
            if (avatarRoot == null) return false;
            foreach (var setup in avatarRoot.GetComponentsInChildren<AutoTightenToBody>(true)) {
                if (setup != null && setup.verboseLog) return true;
            }
            return false;
        }

        private static string BuildMessage(AutoMeshFixProcessor.Result result) {
            if (result == null) return "";
            if (result.Errors.Count > 0) return result.Errors[0];
            if (result.Warnings.Count > 0) return result.Warnings[0];
            return result.Summary;
        }
    }
}
