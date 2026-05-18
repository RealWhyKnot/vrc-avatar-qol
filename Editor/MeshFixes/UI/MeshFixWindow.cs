// MeshFixWindow.cs
//
// Replacement for the old AutoMeshFixWindow. Surfaces every mesh fix
// operation on an avatar grouped by the renderer it writes to, plus a
// plan-view that runs Validate (no mutation) and flags shape-name
// collisions, plus Preview / Stop Preview controls.
//
// Per-card foldout state is keyed by GlobalObjectId.ToString() (NOT
// InstanceID -- not stable across domain reload) and persisted in
// SessionState (survives domain reload, cleared on editor exit) so
// expanding "Advanced" on one setup does not expand it on every other
// card (F7 in the redesign plan).

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Lifecycle;
using WhyKnot.AvatarQol.MeshFixes.Pipeline;

namespace WhyKnot.AvatarQol.MeshFixes.UI {

    internal sealed class MeshFixWindow : EditorWindow {

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#auto-mesh-fixes";
        private const string FoldoutSessionPrefix = "WhyKnot.AvatarQol.MeshFix.UI.Foldout.";

        [SerializeField] private Animator _avatar;
        [SerializeField] private SkinnedMeshRenderer _newGarment;
        [SerializeField] private SkinnedMeshRenderer _newBody;

        private Vector2 _scroll;
        private string _lastMessage = "";
        private MeshFixPipeline.RunResult _lastValidation;
        private AutoTightenToBody _focusedSetup;

        internal static void Open(AutoTightenToBody focus = null) {
            var w = GetWindow<MeshFixWindow>(false, "Auto Mesh Fixes", true);
            w.titleContent = new GUIContent("Avatar QoL - Auto Mesh Fixes");
            w.minSize = new Vector2(680, 540);
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
                "Setups are stored on small editor-only components on each clothing object so Blender re-exports do not wipe them.");

            DrawAvatarPicker();
            EditorGUILayout.Space(2);
            DrawPreviewBar();
            EditorGUILayout.Space(2);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawCreateSetup();
            EditorGUILayout.Space(2);
            DrawStoredSetups();
            EditorGUILayout.Space(2);
            DrawPlanView();
            EditorGUILayout.EndScrollView();
        }

        // ---- Top sections ------------------------------------------------

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Auto Mesh Fixes",
                        "Nondestructive garment-tighten and body-hide blendshapes. Generated during preview, play mode, and upload."),
                    AvatarQolStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
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
                            _lastValidation = null;
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
                    if (MeshFixPreviewController.IsPreviewing) {
                        var prev = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.85f, 0.25f, 0.20f);
                        if (GUILayout.Button(
                                new GUIContent("Stop Previewing",
                                    "Restore the original avatar visibility and remove the generated preview copy."),
                                AvatarQolStyles.PrimaryButton,
                                GUILayout.Width(150))) {
                            MeshFixPreviewController.StopPreview();
                            _lastMessage = "Preview stopped.";
                        }
                        GUI.backgroundColor = prev;
                    } else if (AvatarQolStyles.PrimaryButtonInline(
                                   new GUIContent("Preview",
                                       "Hide the current avatar and show a temporary processed copy. This does not move your Scene view."),
                                   GUILayout.Width(120))) {
                        var preview = MeshFixPreviewController.StartPreview(_avatar.gameObject);
                        _lastMessage = BuildMessage(preview);
                    }
                }

                using (new EditorGUI.DisabledScope(_avatar == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Validate",
                                "Run the pipeline in plan-only mode. Reports missing meshes, unreadable imports, and shape-name collisions without mutating anything."),
                            GUILayout.Height(28), GUILayout.Width(90))) {
                        _lastValidation = MeshFixPipeline.Run(_avatar.gameObject, new MeshFixPipeline.RunOptions {
                            Mode = MeshFixMode.Validate,
                        });
                        _lastValidation.Session.Dispose();
                        _lastMessage = _lastValidation.Summary;
                    }
                }

                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_lastMessage)) {
                    EditorGUILayout.LabelField(_lastMessage, AvatarQolStyles.Muted);
                }
            }
        }

        // ---- Add new setup ----------------------------------------------

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
                                    "Create a stored setup on the clothing object. You can edit it in the list below or in the Inspector."),
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

        // ---- Stored setups list -----------------------------------------

        private void DrawStoredSetups() {
            using (AvatarQolStyles.Section("3. Stored fixes",
                    "Editor-only components that generate temporary blendshapes during preview, play mode, and upload.")) {
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
            string foldoutKey = FoldoutSessionPrefix + GlobalObjectId.GetGlobalObjectIdSlow(setup);
            bool isAdvancedOpen = SessionState.GetBool(foldoutKey, false);

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
                        // Clear the foldout key so the next setup at this slot doesn't inherit state.
                        SessionState.EraseBool(foldoutKey);
                        return;
                    }
                }

                EditorGUILayout.PropertyField(garment, new GUIContent("Clothing mesh", "The mesh to tighten."));
                EditorGUILayout.PropertyField(body, new GUIContent("Body mesh", "The body mesh used as the fit target."));

                // Inline validation status for the common Read/Write fail.
                MaybeDrawReadWriteRemediation(setup.garmentRenderer, "Clothing");
                MaybeDrawReadWriteRemediation(setup.bodyRenderer, "Body");

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

                bool nextAdvancedOpen = EditorGUILayout.Foldout(isAdvancedOpen, "Advanced", true, AvatarQolStyles.FoldoutHeader);
                if (nextAdvancedOpen != isAdvancedOpen) SessionState.SetBool(foldoutKey, nextAdvancedOpen);
                if (nextAdvancedOpen) {
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

        private void MaybeDrawReadWriteRemediation(SkinnedMeshRenderer renderer, string role) {
            if (renderer == null || renderer.sharedMesh == null || renderer.sharedMesh.isReadable) return;
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent($"{role} mesh is not readable.",
                        "Auto Mesh Fixes needs Read/Write enabled on the source model import. Click Fix to flip it and reimport."),
                    AvatarQolStyles.Muted);
                if (GUILayout.Button("Fix Read/Write", EditorStyles.miniButton, GUILayout.Width(110))) {
                    EnableReadWrite(renderer.sharedMesh);
                }
            }
        }

        // ---- Plan view ---------------------------------------------------

        private void DrawPlanView() {
            using (AvatarQolStyles.Section("4. Plan",
                    "Click Validate above to see exactly which operations would run, grouped by the renderer they write to. Shape-name conflicts are highlighted.")) {
                if (_lastValidation == null) {
                    EditorGUILayout.LabelField("Run Validate to populate the plan view.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }

                if (_lastValidation.Plan.Count == 0 && _lastValidation.Errors.Count == 0) {
                    EditorGUILayout.LabelField("No operations on this avatar yet.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }

                if (_lastValidation.Errors.Count > 0) {
                    foreach (var err in _lastValidation.Errors) {
                        AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning, err);
                    }
                }
                if (_lastValidation.Warnings.Count > 0) {
                    foreach (var w in _lastValidation.Warnings) {
                        AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning, w);
                    }
                }

                var byRenderer = _lastValidation.Plan
                    .Where(r => r.Target != null)
                    .GroupBy(r => r.Target)
                    .OrderBy(g => g.Key != null ? g.Key.name : "");
                foreach (var group in byRenderer) {
                    EditorGUILayout.LabelField($"{group.Key.name}", AvatarQolStyles.SubsectionTitle);
                    foreach (var row in group) {
                        DrawPlanRow(row);
                    }
                }

                var unrendered = _lastValidation.Plan.Where(r => r.Target == null).ToList();
                if (unrendered.Count > 0) {
                    EditorGUILayout.LabelField("Operations without a resolved target:", AvatarQolStyles.SubsectionTitle);
                    foreach (var row in unrendered) DrawPlanRow(row);
                }
            }
        }

        private void DrawPlanRow(MeshFixPipeline.PlanRow row) {
            Color pillColor;
            string pillText;
            switch (row.Status) {
                case MeshFixPipeline.PlanStatus.Ok:       pillColor = AvatarQolStyles.ColorSuccess; pillText = "OK"; break;
                case MeshFixPipeline.PlanStatus.Warning:  pillColor = AvatarQolStyles.ColorWarning; pillText = "warn"; break;
                case MeshFixPipeline.PlanStatus.Conflict: pillColor = AvatarQolStyles.CategoryHumanoid; pillText = "conflict"; break;
                case MeshFixPipeline.PlanStatus.Error:    pillColor = AvatarQolStyles.CategoryHumanoid; pillText = "error"; break;
                default:                                  pillColor = AvatarQolStyles.ColorInfo; pillText = "skipped"; break;
            }

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(10);
                AvatarQolStyles.BadgePill(pillText, pillColor, row.Note);
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"[{row.Operation.DisplayName}] from {OwnerName(row.Operation)}  -> {(row.Target != null ? row.Target.name : "(no target)")}   shape: {row.ShapeName}",
                        row.Note),
                    AvatarQolStyles.Mono);
                GUILayout.FlexibleSpace();
                if (row.Operation.Owner is Component c && c != null && c.gameObject != null) {
                    if (GUILayout.Button(new GUIContent("Ping", "Ping the owning setup in the hierarchy."),
                            EditorStyles.miniButton, GUILayout.Width(44))) {
                        Selection.activeObject = c.gameObject;
                        EditorGUIUtility.PingObject(c.gameObject);
                    }
                }
            }
        }

        // ---- Add / discover ---------------------------------------------

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
            // Per-garment shape names so two manually-added setups against the
            // same body do not silently fight over the default constant.
            setup.garmentTightenBlendShapeName = $"AUTO_Tighten_{_newGarment.name}";
            setup.bodyHideBlendShapeName = $"AUTO_HideBody_{_newGarment.name}";
            EditorUtility.SetDirty(setup);
            if (PrefabUtility.IsPartOfPrefabInstance(setup)) {
                PrefabUtility.RecordPrefabInstancePropertyModifications(setup);
            }
            EnsureControllerOnAvatar();
            _focusedSetup = setup;
            Selection.activeObject = setup.gameObject;
            EditorGUIUtility.PingObject(setup.gameObject);
            _lastMessage =
                $"Stored Auto Tighten To Body on {AvatarQol.GetGameObjectPath(setup.gameObject)}. " +
                "Select that clothing mesh to see the component, or use this window to edit it.";
            _lastValidation = null;
        }

        private void EnsureControllerOnAvatar() {
            if (_avatar == null) return;
            var existing = _avatar.GetComponentInChildren<WhyKnotMeshFixController>(true);
            if (existing != null) return;
            var controller = Undo.AddComponent<WhyKnotMeshFixController>(_avatar.gameObject);
            EditorUtility.SetDirty(controller);
            if (PrefabUtility.IsPartOfPrefabInstance(controller)) {
                PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
            }
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

        // ---- Field helpers ----------------------------------------------

        private void Slider(SerializedProperty prop, string label, string tooltip, float min, float max) {
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            float next = EditorGUILayout.Slider(new GUIContent(label, tooltip), prop.floatValue, min, max);
            EditorGUI.showMixedValue = false;
            if (!Mathf.Approximately(next, prop.floatValue)) prop.floatValue = next;
        }

        private void BodyHideModeField(SerializedProperty prop) {
            var labels = new[] {
                new GUIContent("Push inward", "Move selected body vertices inward along the body normal."),
                new GUIContent("Collapse toward body root", "Move selected body vertices toward the renderer root."),
                new GUIContent("Collapse toward nearest bone", "Move selected body vertices toward the bone that already controls them most."),
            };
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            prop.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Hide direction", "How the body vertices under the clothing should move."),
                prop.enumValueIndex,
                labels);
            EditorGUI.showMixedValue = false;
        }

        private void SelectionModeField(SerializedProperty prop) {
            var labels = new[] {
                new GUIContent("Use whole clothing mesh", "Every clothing vertex can be tightened."),
                new GUIContent("Use red vertex color", "Only vertices with red vertex color are tightened."),
                new GUIContent("Use green vertex color", "Only vertices with green vertex color are tightened."),
                new GUIContent("Use blue vertex color", "Only vertices with blue vertex color are tightened."),
                new GUIContent("Use alpha vertex color", "Only vertices with alpha vertex color are tightened."),
            };
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            prop.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent("Clothing mask", "Optional vertex color channel used to limit which clothing vertices tighten."),
                prop.enumValueIndex,
                labels);
            EditorGUI.showMixedValue = false;
        }

        // ---- Utilities ---------------------------------------------------

        private static SkinnedMeshRenderer GuessBodyRenderer(Animator avatar, SkinnedMeshRenderer exclude = null) {
            if (avatar == null) return null;
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null && r != exclude && r.sharedMesh != null)
                .ToList();
            var body = renderers.FirstOrDefault(r => r.name.ToLowerInvariant().Contains("body"));
            if (body != null) return body;
            return renderers.OrderByDescending(r => r.sharedMesh != null ? r.sharedMesh.vertexCount : 0).FirstOrDefault();
        }

        private static string NameOf(Object obj) => obj != null ? obj.name : "(missing)";

        private static string OwnerName(IMeshOperation op) {
            if (op == null || op.Owner == null) return "(none)";
            return op.Owner is Component c && c != null && c.gameObject != null ? c.gameObject.name : op.Owner.name;
        }

        private static string BuildMessage(MeshFixPreviewController.PreviewResult result) {
            if (result == null) return "";
            if (result.Errors.Count > 0) return result.Errors[0];
            if (result.PipelineResult == null) return "";
            if (result.PipelineResult.Errors.Count > 0) return result.PipelineResult.Errors[0];
            if (result.PipelineResult.Warnings.Count > 0) return result.PipelineResult.Warnings[0];
            return result.PipelineResult.Summary;
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
