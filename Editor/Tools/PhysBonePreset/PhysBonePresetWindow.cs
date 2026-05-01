// PhysBonePresetWindow.cs
//
// Three-panel window:
//   1. Bone selection (drop zone, "Use selection" / "Add selection" buttons,
//      one row per Transform).
//   2. Preset picker. Each preset gets a button row with a suggestion score
//      bar; the highest-scoring preset is highlighted on first scan. Below
//      the buttons: the preset's Description, plus an analysis summary so
//      the user can sanity-check that what we think we're seeing matches
//      what they have.
//   3. Plan preview. Lists the PhysBones and Colliders that would be added
//      with their key parameters; a Notes section for anything the preset
//      wanted to flag. Apply button lives at the bottom.
//
// The window auto-discovers presets from the assembly via reflection — drop
// a new IPhysBonePreset implementation into Editor/Tools/PhysBonePreset/Presets/
// with a parameterless constructor and it appears in the picker on next
// reload.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class PhysBonePresetWindow : EditorWindow {

        [SerializeField] private List<Transform> _selection = new List<Transform>();
        [SerializeField] private string _selectedPresetId;

        private List<IPhysBonePreset> _presets = new List<IPhysBonePreset>();
        private BoneSelectionAnalysis _analysis;
        private PhysBonePlan _plan;
        private Dictionary<string, float> _suggestionScores = new Dictionary<string, float>();
        private Vector2 _selectionScroll;
        private Vector2 _planScroll;

        // ------ Public entry --------------------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<PhysBonePresetWindow>(false, "PhysBone Preset", true);
            w.titleContent = new GUIContent("Avatar QoL — PhysBone Preset");
            w.minSize = new Vector2(620, 540);
            if (prefillFromSelection) {
                w._selection = Selection.gameObjects
                    .Where(g => g != null)
                    .Select(g => g.transform)
                    .Distinct()
                    .ToList();
            }
            w.RebuildAnalysis();
            w.Show();
            w.Focus();
        }

        // ------ Lifecycle -----------------------------------------------------

        private void OnEnable() {
            DiscoverPresets();
            if (_analysis == null) RebuildAnalysis();
        }

        private void DiscoverPresets() {
            _presets = new List<IPhysBonePreset>();
            // Scan our own assembly first — that's where the shipping
            // presets live. If a user drops a preset into a different
            // assembly (e.g. their project's Assembly-CSharp-Editor), we
            // pick those up too.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types) {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(IPhysBonePreset).IsAssignableFrom(t)) continue;
                    var ctor = t.GetConstructor(Type.EmptyTypes);
                    if (ctor == null) continue;
                    try { _presets.Add((IPhysBonePreset)ctor.Invoke(null)); }
                    catch { /* skip: constructor threw */ }
                }
            }
            // Stable order: alphabetic, with Generic last because it's the
            // fallback.
            _presets.Sort((a, b) => {
                if (a.Id == "generic") return 1;
                if (b.Id == "generic") return -1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            });
        }

        // ------ GUI ---------------------------------------------------------

        private void OnGUI() {
            DrawSdkBanner();
            EditorGUILayout.Space(2);
            DrawSelection();
            EditorGUILayout.Space(2);
            DrawAnalysisSummary();
            EditorGUILayout.Space(2);
            DrawPresetPicker();
            EditorGUILayout.Space(2);
            DrawPlanPreview();
            EditorGUILayout.Space(2);
            DrawApplyBar();
        }

        private void DrawSdkBanner() {
            if (PhysBonePlanApplier.SdkAvailable) return;
            EditorGUILayout.HelpBox(
                "VRChat SDK 3 (PhysBone) is not installed in this project. The window will analyse selections and preview plans, but Apply is disabled.",
                MessageType.Warning);
        }

        // -------- Selection panel --------

        private void DrawSelection() {
            EditorGUILayout.LabelField("Bones", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinHeight(70), GUILayout.MaxHeight(180))) {
                _selectionScroll = EditorGUILayout.BeginScrollView(_selectionScroll);
                if (_selection.Count == 0) {
                    EditorGUILayout.LabelField("(empty — select bones in the hierarchy and click 'Use selection')",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    bool dirty = false;
                    for (int i = 0; i < _selection.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            var newT = (Transform)EditorGUILayout.ObjectField(GUIContent.none, _selection[i], typeof(Transform), allowSceneObjects: true);
                            if (newT != _selection[i]) { _selection[i] = newT; dirty = true; }
                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                        }
                    }
                    if (removeIndex >= 0) { _selection.RemoveAt(removeIndex); dirty = true; }
                    if (dirty) RebuildAnalysis();
                }
                EditorGUILayout.EndScrollView();
            }
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("Use selection", "Replace the bone list with the currently selected GameObjects."))) {
                    _selection = Selection.gameObjects.Where(g => g != null).Select(g => g.transform).Distinct().ToList();
                    RebuildAnalysis();
                }
                if (GUILayout.Button(new GUIContent("Add selection", "Add the currently selected GameObjects to the bone list."))) {
                    foreach (var g in Selection.gameObjects) {
                        if (g == null) continue;
                        var t = g.transform;
                        if (!_selection.Contains(t)) _selection.Add(t);
                    }
                    RebuildAnalysis();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Clear", "Empty the bone list."), GUILayout.Width(70))) {
                    _selection.Clear();
                    RebuildAnalysis();
                }
            }
        }

        // -------- Analysis summary --------

        private void DrawAnalysisSummary() {
            if (_analysis == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                if (_analysis.Chains.Count == 0) {
                    EditorGUILayout.LabelField("Selection has no detectable chains.", EditorStyles.miniLabel);
                    return;
                }
                EditorGUILayout.LabelField(
                    $"Detected: {_analysis.Chains.Count} chain(s), avg {_analysis.AverageChainBoneCount} bones, " +
                    $"avg length {_analysis.AverageChainLengthMetres:F3} m, " +
                    $"avg bone size {_analysis.AverageBoneSize:F3} m.",
                    EditorStyles.miniLabel);
                if (_analysis.HostAnimator != null) {
                    EditorGUILayout.LabelField(
                        $"Host avatar: {_analysis.HostAnimator.gameObject.name}" +
                        (_analysis.HostAnimator.isHuman
                            ? $" (Humanoid; nearest bone {(_analysis.NearestHumanoidBoneType?.ToString() ?? "n/a")}; dominant side {_analysis.DominantSide})"
                            : " (NOT Humanoid — ear/tail/hair/dress presets need Humanoid)"),
                        EditorStyles.miniLabel);
                } else {
                    EditorGUILayout.LabelField(
                        "No Animator found in the parent chain — limited adaptation possible.",
                        EditorStyles.miniLabel);
                }
            }
        }

        // -------- Preset picker --------

        private void DrawPresetPicker() {
            EditorGUILayout.LabelField("Preset", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                if (_presets.Count == 0) {
                    EditorGUILayout.LabelField("No presets discovered.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                IPhysBonePreset best = null;
                float bestScore = -1f;
                if (_analysis != null && _analysis.Chains.Count > 0) {
                    foreach (var p in _presets) {
                        if (!_suggestionScores.TryGetValue(p.Id, out var s)) s = 0;
                        if (s > bestScore) { best = p; bestScore = s; }
                    }
                }
                foreach (var preset in _presets) {
                    DrawPresetRow(preset, isSuggested: preset == best);
                }
                var selected = SelectedPreset();
                if (selected != null) {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(selected.Description, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void DrawPresetRow(IPhysBonePreset preset, bool isSuggested) {
            float score = _suggestionScores.TryGetValue(preset.Id, out var s) ? s : 0f;
            bool isSelected = preset.Id == _selectedPresetId;
            using (new EditorGUILayout.HorizontalScope()) {
                var label = preset.DisplayName + (isSuggested ? "  ★" : "");
                if (GUILayout.Toggle(isSelected, label, "Button", GUILayout.Width(180), GUILayout.Height(22))) {
                    if (!isSelected) {
                        _selectedPresetId = preset.Id;
                        RebuildPlan();
                    }
                }
                // Suggestion bar.
                var rect = GUILayoutUtility.GetRect(80, 14, GUILayout.ExpandWidth(false));
                EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.15f));
                var fill = rect; fill.width = rect.width * score;
                EditorGUI.DrawRect(fill, new Color(0.2f, 0.6f, 0.95f, 0.85f));
                EditorGUILayout.LabelField($"{Mathf.RoundToInt(score * 100)}%", EditorStyles.miniLabel, GUILayout.Width(36));
                EditorGUILayout.LabelField(preset.Description, EditorStyles.miniLabel);
            }
        }

        // -------- Plan preview --------

        private void DrawPlanPreview() {
            EditorGUILayout.LabelField("Plan", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _planScroll = EditorGUILayout.BeginScrollView(_planScroll);
                if (_plan == null || (_plan.PhysBones.Count == 0 && _plan.Colliders.Count == 0)) {
                    EditorGUILayout.LabelField(
                        SelectedPreset() == null ? "Pick a preset above." : "Selected preset produced no plan for this selection.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    EditorGUILayout.LabelField(
                        $"{_plan.PhysBones.Count} PhysBone(s), {_plan.Colliders.Count} collider(s).",
                        EditorStyles.miniLabel);
                    if (_plan.Notes.Count > 0) {
                        EditorGUILayout.LabelField("Notes", EditorStyles.boldLabel);
                        foreach (var n in _plan.Notes) EditorGUILayout.LabelField("• " + n, EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.Space(4);
                    }
                    if (_plan.Colliders.Count > 0) {
                        EditorGUILayout.LabelField("Colliders", EditorStyles.boldLabel);
                        for (int i = 0; i < _plan.Colliders.Count; i++) {
                            var c = _plan.Colliders[i];
                            EditorGUILayout.LabelField(
                                $"  [{i}] {c.Name} on {AvatarQol.GetGameObjectPath(c.AttachTo?.gameObject)} — {c.Shape}, r={c.Radius:F3} h={c.Height:F3}",
                                EditorStyles.miniLabel);
                        }
                        EditorGUILayout.Space(2);
                    }
                    EditorGUILayout.LabelField("PhysBones", EditorStyles.boldLabel);
                    foreach (var pb in _plan.PhysBones) {
                        EditorGUILayout.LabelField(
                            $"  on {AvatarQol.GetGameObjectPath(pb.Root?.gameObject)}",
                            EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(
                            $"     pull={pb.Pull:F2}  spring={pb.Spring:F2}  stiff={pb.Stiffness:F2}  gravity={pb.Gravity:F2}  immobile={pb.ImmobileType}/{pb.Immobile:F2}  radius={pb.Radius:F3}",
                            EditorStyles.miniLabel);
                        if (pb.ColliderRefs != null && pb.ColliderRefs.Count > 0) {
                            EditorGUILayout.LabelField("     colliders: [" + string.Join(", ", pb.ColliderRefs) + "]", EditorStyles.miniLabel);
                        }
                        if (!string.IsNullOrEmpty(pb.Note)) {
                            EditorGUILayout.LabelField("     " + pb.Note, EditorStyles.miniLabel);
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        // -------- Apply --------

        private void DrawApplyBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canApply = PhysBonePlanApplier.SdkAvailable
                                && _plan != null
                                && _plan.PhysBones.Count > 0;
                using (new EditorGUI.DisabledScope(!canApply)) {
                    string label = canApply
                        ? $"Apply ({_plan.PhysBones.Count} PhysBone(s), {_plan.Colliders.Count} collider(s))"
                        : "Apply";
                    if (GUILayout.Button(label, GUILayout.Height(26), GUILayout.MinWidth(260))) {
                        ApplyPlan();
                    }
                }
                if (GUILayout.Button(new GUIContent("Refresh", "Re-analyse the selection and rebuild the plan."),
                        GUILayout.Height(26), GUILayout.Width(80))) {
                    RebuildAnalysis();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Height(26), GUILayout.Width(80))) Close();
            }
        }

        // ------ Logic --------------------------------------------------------

        private void RebuildAnalysis() {
            _analysis = BoneSelectionAnalysis.Build(_selection);
            _suggestionScores.Clear();
            foreach (var p in _presets) {
                try { _suggestionScores[p.Id] = p.SuggestionScore(_analysis); }
                catch { _suggestionScores[p.Id] = 0f; }
            }
            // Auto-pick the suggestion if no preset is selected, or if the
            // current one has a noticeably worse score than the suggestion.
            var suggestion = SuggestedPreset();
            if (string.IsNullOrEmpty(_selectedPresetId) || SelectedPreset() == null) {
                _selectedPresetId = suggestion?.Id;
            }
            RebuildPlan();
        }

        private void RebuildPlan() {
            var preset = SelectedPreset();
            if (preset == null || _analysis == null) { _plan = null; return; }
            try { _plan = preset.BuildPlan(_analysis); }
            catch (Exception ex) {
                Debug.LogException(ex);
                _plan = null;
            }
        }

        private IPhysBonePreset SelectedPreset() {
            foreach (var p in _presets) if (p.Id == _selectedPresetId) return p;
            return null;
        }

        private IPhysBonePreset SuggestedPreset() {
            IPhysBonePreset best = null;
            float bestScore = -1f;
            foreach (var p in _presets) {
                if (!_suggestionScores.TryGetValue(p.Id, out var s)) s = 0;
                if (s > bestScore) { best = p; bestScore = s; }
            }
            return best;
        }

        private void ApplyPlan() {
            if (_plan == null) return;
            int created = PhysBonePlanApplier.Apply(_plan, out var error);
            if (created < 0) {
                EditorUtility.DisplayDialog("Apply PhysBone Preset",
                    "Apply failed; changes reverted.\n\n" + (error ?? "Unknown error."),
                    "OK");
                return;
            }
            Debug.Log($"[Avatar QoL] Applied {_plan.PresetDisplayName} — {created} PhysBone(s), {_plan.Colliders.Count} collider(s).");
            // Refresh the analysis so the plan reflects the new state of the
            // scene; selecting one of the newly-created PhysBones helps the
            // user immediately tune from the inspector.
            if (_plan.PhysBones.Count > 0 && _plan.PhysBones[0].Root != null) {
                Selection.activeGameObject = _plan.PhysBones[0].Root.gameObject;
            }
            RebuildAnalysis();
        }
    }
}
