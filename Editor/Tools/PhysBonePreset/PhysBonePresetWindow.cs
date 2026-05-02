// PhysBonePresetWindow.cs
//
// Three-panel window:
//   1. Bone selection (drop zone + list).
//   2. Preset picker — preset cards. The auto-suggested preset has a
//      ribbon and accent border; the selected preset gets a fill tint.
//      Each card shows the preset name, suggestion strength, description,
//      and a tooltip on the score bar that explains *why* the preset
//      matched.
//   3. Plan preview — chain-grouped foldouts. Each PhysBone is rendered
//      with a parameter table; values that differ from the VRChat SDK
//      defaults are bold-labelled and marked with `**`, so the user can
//      see at a glance what the preset is actually doing.
//
// After Apply, the bottom of the window briefly hosts a tweak strip:
// multiplicative sliders for Pull / Spring / Stiffness / Gravity / Radius
// that scale every PhysBone the apply just created. Disappears when the
// selection or preset changes; Ctrl+Z reverts the apply itself.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class PhysBonePresetWindow : EditorWindow {

        [SerializeField] private List<Transform> _selection = new List<Transform>();
        [SerializeField] private string _selectedPresetId;
        [SerializeField] private bool _advancedOpen;

        // Per-chain foldout state — stable across reloads via a parallel list of chain-root paths.
        [SerializeField] private List<string> _collapsedChainRoots = new List<string>();

        private List<IPhysBonePreset> _presets = new List<IPhysBonePreset>();
        private BoneSelectionAnalysis _analysis;
        private PhysBonePlan _plan;
        private Dictionary<string, float> _suggestionScores = new Dictionary<string, float>();
        private Dictionary<string, List<ScoringSignal>> _suggestionExplanations = new Dictionary<string, List<ScoringSignal>>();
        private Vector2 _selectionScroll;
        private Vector2 _planScroll;

        // Post-apply tweak state. After Apply, we cache the just-created
        // components and a snapshot of their original parameters so the
        // tweak sliders can scale relative to a stable baseline (dragging
        // back to 1.0 fully restores).
        private List<TweakSnapshot> _tweakSnapshots;
        private float _tweakPull = 1f, _tweakSpring = 1f, _tweakStiff = 1f, _tweakGravity = 1f, _tweakRadius = 1f;

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#physbone-preset";

        // ------ Public entry -----------------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<PhysBonePresetWindow>(false, "PhysBone Preset", true);
            w.titleContent = new GUIContent("Avatar QoL — PhysBone Preset");
            w.minSize = new Vector2(640, 580);
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

        // ------ Lifecycle --------------------------------------------------

        private void OnEnable() {
            DiscoverPresets();
            if (_analysis == null) RebuildAnalysis();
        }

        private void DiscoverPresets() {
            _presets = new List<IPhysBonePreset>();
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
            _presets.Sort((a, b) => {
                if (a.Id == "generic") return 1;
                if (b.Id == "generic") return -1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            });
        }

        // ------ GUI --------------------------------------------------------

        private void OnGUI() {
            DrawSdkBanner();
            DrawTitleBar();
            DrawSelection();
            EditorGUILayout.Space(2);
            DrawAnalysisSummary();
            EditorGUILayout.Space(2);
            DrawPresetPicker();
            EditorGUILayout.Space(2);
            DrawPlanPreview();
            EditorGUILayout.Space(2);
            if (_tweakSnapshots != null && _tweakSnapshots.Count > 0) {
                DrawTweakStrip();
            } else {
                DrawApplyBar();
            }
            DrawAdvanced();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("PhysBone Preset",
                        "Apply a smart preset to a selection of bones to set up VRChat PhysBones with sensible parameters and colliders."),
                    AvatarQolStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawSdkBanner() {
            if (PhysBonePlanApplier.SdkAvailable) return;
            AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                "VRChat SDK 3 (PhysBone) is not installed in this project. The window will analyse selections and preview plans, but Apply is disabled.");
        }

        // -------- Selection panel --------

        private void DrawSelection() {
            using (AvatarQolStyles.Section($"Bones ({_selection.Count})",
                    "Drop the bones the preset will set up. Each top-level bone you drop in becomes a chain root; descendants are walked automatically. Pick the ear roots, the tail base, the skirt panel anchors, etc.")) {
                using (new EditorGUILayout.VerticalScope(GUILayout.MinHeight(60), GUILayout.MaxHeight(150))) {
                    _selectionScroll = EditorGUILayout.BeginScrollView(_selectionScroll);
                    if (_selection.Count == 0) {
                        EditorGUILayout.LabelField(
                            new GUIContent("(empty — pick bones in the Hierarchy and click 'Use selection')",
                                "Drag any number of bone Transforms here, or select them in the Hierarchy and click Use selection."),
                            EditorStyles.centeredGreyMiniLabel);
                    } else {
                        int removeIndex = -1;
                        bool dirty = false;
                        for (int i = 0; i < _selection.Count; i++) {
                            using (new EditorGUILayout.HorizontalScope()) {
                                var newT = (Transform)EditorGUILayout.ObjectField(
                                    new GUIContent(GUIContent.none.image, "A bone Transform that will receive a PhysBone (or be folded into a chain that gets one)."),
                                    _selection[i], typeof(Transform), allowSceneObjects: true);
                                if (newT != _selection[i]) { _selection[i] = newT; dirty = true; }
                                if (GUILayout.Button(new GUIContent("×", "Remove this bone from the list."),
                                        EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                            }
                        }
                        if (removeIndex >= 0) { _selection.RemoveAt(removeIndex); dirty = true; }
                        if (dirty) RebuildAnalysis();
                    }
                    EditorGUILayout.EndScrollView();
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(new GUIContent("Use selection",
                            "Replace the bone list with the currently selected GameObjects."))) {
                        _selection = Selection.gameObjects.Where(g => g != null).Select(g => g.transform).Distinct().ToList();
                        RebuildAnalysis();
                    }
                    if (GUILayout.Button(new GUIContent("Add selection",
                            "Append the currently selected GameObjects to the bone list."))) {
                        foreach (var g in Selection.gameObjects) {
                            if (g == null) continue;
                            var t = g.transform;
                            if (!_selection.Contains(t)) _selection.Add(t);
                        }
                        RebuildAnalysis();
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Clear",
                            "Empty the bone list."), GUILayout.Width(70))) {
                        _selection.Clear();
                        RebuildAnalysis();
                    }
                }
            }
        }

        // -------- Analysis summary --------

        private void DrawAnalysisSummary() {
            if (_analysis == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                if (_analysis.Chains.Count == 0) {
                    EditorGUILayout.LabelField(
                        new GUIContent("No detectable chains.",
                            "A 'chain' is a Transform plus its straight descendants. Drop hair/ear/tail roots here, not the avatar root."),
                        AvatarQolStyles.Muted);
                    return;
                }
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"{_analysis.Chains.Count} chain(s)  •  avg {_analysis.AverageChainBoneCount} bones  •  avg length {_analysis.AverageChainLengthMetres:F3} m  •  avg bone size {_analysis.AverageBoneSize:F3} m",
                        "Auto-measured from your selection. Used to score which preset best fits."),
                    AvatarQolStyles.Muted);
                if (_analysis.HostAnimator != null) {
                    var bone = _analysis.NearestHumanoidBoneType?.ToString() ?? "n/a";
                    var msg = _analysis.HostAnimator.isHuman
                        ? $"Host: {_analysis.HostAnimator.gameObject.name} (Humanoid; nearest bone: {bone}; dominant side: {_analysis.DominantSide})"
                        : $"Host: {_analysis.HostAnimator.gameObject.name} (NOT Humanoid — ear / tail / hair / dress presets need Humanoid)";
                    EditorGUILayout.LabelField(
                        new GUIContent(msg, "Host avatar's Animator. Side classification and humanoid-mirror lookups depend on a Humanoid rig."),
                        AvatarQolStyles.Muted);
                } else {
                    EditorGUILayout.LabelField(
                        new GUIContent("No Animator found in the parent chain — limited adaptation possible.",
                            "Without an Animator the analysis can't classify side, find Hips, or auto-add leg colliders."),
                        AvatarQolStyles.Muted);
                }
            }
        }

        // -------- Preset picker — cards --------

        private void DrawPresetPicker() {
            EditorGUILayout.LabelField(
                new GUIContent("Preset",
                    "A preset writes parameter defaults tuned for a specific use case (ears, tail, hair, dress). Pick one to see its plan."),
                AvatarQolStyles.SubsectionTitle);

            if (_presets.Count == 0) {
                EditorGUILayout.LabelField("No presets discovered.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // Determine the suggestion winner.
            IPhysBonePreset best = null;
            float bestScore = -1f;
            if (_analysis != null && _analysis.Chains.Count > 0) {
                foreach (var p in _presets) {
                    if (!_suggestionScores.TryGetValue(p.Id, out var s)) s = 0;
                    if (s > bestScore) { best = p; bestScore = s; }
                }
            }

            // Flow cards into rows that fit the window width.
            const float cardWidth = 200f;
            const float cardHeight = 110f;
            const float gutter = 6f;
            float available = position.width - 24f;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((available + gutter) / (cardWidth + gutter)));
            for (int i = 0; i < _presets.Count; i += perRow) {
                using (new EditorGUILayout.HorizontalScope()) {
                    for (int j = 0; j < perRow && i + j < _presets.Count; j++) {
                        var preset = _presets[i + j];
                        DrawPresetCard(preset, isSuggested: preset == best, cardWidth, cardHeight);
                        if (j + 1 < perRow && i + j + 1 < _presets.Count) GUILayout.Space(gutter);
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Space(gutter);
            }
        }

        private void DrawPresetCard(IPhysBonePreset preset, bool isSuggested, float width, float height) {
            float score = _suggestionScores.TryGetValue(preset.Id, out var s) ? s : 0f;
            bool isSelected = preset.Id == _selectedPresetId;

            // Reserve the card rect + draw background fills + border + content.
            var rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));

            // Selection tint (under the helpBox so border still reads).
            if (isSelected) {
                var accent = AvatarQolStyles.ColorAccent;
                EditorGUI.DrawRect(rect, new Color(accent.r, accent.g, accent.b, 0.10f));
            }

            // Suggested ribbon at top of card.
            if (isSuggested) {
                var ribbon = new Rect(rect.x, rect.y, rect.width, 14);
                var accent = AvatarQolStyles.ColorAccent;
                EditorGUI.DrawRect(ribbon, new Color(accent.r, accent.g, accent.b, 0.55f));
                GUI.Label(ribbon, "SUGGESTED", new GUIStyle(EditorStyles.miniLabel) {
                    alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.white },
                });
            }

            // Border via four thin slabs (simpler than custom GUIStyle).
            float thick = isSuggested ? 2f : 1f;
            var border = isSuggested ? AvatarQolStyles.ColorAccent : AvatarQolStyles.ColorDivider;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thick), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thick, rect.width, thick), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thick, rect.height), border);
            EditorGUI.DrawRect(new Rect(rect.xMax - thick, rect.y, thick, rect.height), border);

            // Inner padding-aware content area.
            var inner = new Rect(rect.x + 8, rect.y + (isSuggested ? 18 : 8), rect.width - 16, rect.height - (isSuggested ? 26 : 16));

            // Title + percentage row.
            var titleRect = new Rect(inner.x, inner.y, inner.width - 40, 16);
            var pctRect   = new Rect(inner.x + inner.width - 40, inner.y, 40, 16);
            GUI.Label(titleRect, new GUIContent(preset.DisplayName, preset.Description), EditorStyles.boldLabel);
            GUI.Label(pctRect, $"{Mathf.RoundToInt(score * 100)}%", new GUIStyle(EditorStyles.miniLabel) {
                alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold,
            });

            // Score bar with breakdown tooltip.
            var barBg = new Rect(inner.x, inner.y + 18, inner.width, 6);
            EditorGUI.DrawRect(barBg, new Color(0, 0, 0, 0.18f));
            var barFill = new Rect(barBg.x, barBg.y, barBg.width * Mathf.Clamp01(score), barBg.height);
            var fillColor = isSuggested ? AvatarQolStyles.ColorAccent : new Color(0.3f, 0.6f, 0.9f, 0.7f);
            EditorGUI.DrawRect(barFill, fillColor);
            // Hover-over-tooltip on the score area.
            string scoreTooltip = BuildScoreTooltip(preset);
            GUI.Label(barBg, new GUIContent("", scoreTooltip));

            // Description (capped to remaining space).
            var descRect = new Rect(inner.x, inner.y + 28, inner.width, inner.height - 28);
            GUI.Label(descRect, preset.Description, AvatarQolStyles.Muted);

            // Whole-card click handler (drawn last so it's on top of visuals
            // but transparent — visual layers underneath remain readable).
            if (GUI.Button(rect, new GUIContent("", $"Pick this preset to use for the plan below.\n\n{preset.Description}"), GUIStyle.none)) {
                if (preset.Id != _selectedPresetId) {
                    _selectedPresetId = preset.Id;
                    RebuildPlan();
                }
            }
        }

        private string BuildScoreTooltip(IPhysBonePreset preset) {
            if (_analysis == null || _analysis.Chains.Count == 0) return "Selection has no detectable chains.";
            if (!_suggestionExplanations.TryGetValue(preset.Id, out var signals) || signals.Count == 0) {
                return $"Score: {Mathf.RoundToInt(_suggestionScores[preset.Id] * 100)}%. (No breakdown available for this preset.)";
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Why {Mathf.RoundToInt(_suggestionScores[preset.Id] * 100)}%:");
            foreach (var s in signals) {
                sb.AppendLine($"  {(s.Contribution >= 0 ? "+" : "")}{s.Contribution:F2}  {s.Name}");
            }
            return sb.ToString().TrimEnd();
        }

        // -------- Plan preview — chain-grouped --------

        private static readonly SdkDefaults Defaults = new SdkDefaults();

        private void DrawPlanPreview() {
            EditorGUILayout.LabelField(
                new GUIContent("Plan",
                    "What will be created if you click Apply. Nothing is written to the scene until then."),
                AvatarQolStyles.SubsectionTitle);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _planScroll = EditorGUILayout.BeginScrollView(_planScroll);
                if (_plan == null || (_plan.PhysBones.Count == 0 && _plan.Colliders.Count == 0)) {
                    EditorGUILayout.LabelField(
                        SelectedPreset() == null ? "Pick a preset above." : "Selected preset produced no plan for this selection.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    EditorGUILayout.LabelField(
                        new GUIContent($"{_plan.PhysBones.Count} PhysBone(s)  •  {_plan.Colliders.Count} collider(s)",
                            "Summary of the entire plan. Each Chain section below shows its PhysBone parameters and the colliders it references."),
                        AvatarQolStyles.Muted);

                    if (_plan.Notes.Count > 0) {
                        foreach (var n in _plan.Notes)
                            EditorGUILayout.LabelField("• " + n, AvatarQolStyles.Muted);
                        EditorGUILayout.Space(2);
                    }

                    // Group plan PhysBones by chain (root Transform).
                    var pbByRoot = new Dictionary<Transform, PhysBoneSpec>();
                    foreach (var pb in _plan.PhysBones) if (pb.Root != null) pbByRoot[pb.Root] = pb;
                    foreach (var chain in _analysis.Chains) {
                        if (!pbByRoot.TryGetValue(chain.Root, out var pb)) continue;
                        DrawChainBlock(chain, pb);
                    }

                    // Orphan colliders (not referenced by any PhysBone in the plan).
                    var refdIndices = new HashSet<int>();
                    foreach (var pb in _plan.PhysBones) foreach (var idx in pb.ColliderRefs) refdIndices.Add(idx);
                    var orphans = new List<int>();
                    for (int i = 0; i < _plan.Colliders.Count; i++) if (!refdIndices.Contains(i)) orphans.Add(i);
                    if (orphans.Count > 0) {
                        EditorGUILayout.LabelField(
                            new GUIContent($"Orphan colliders ({orphans.Count})",
                                "Colliders the plan creates but doesn't attach to any PhysBone. Usually a preset bug; the colliders will exist in the scene but not collide with anything."),
                            EditorStyles.boldLabel);
                        foreach (var idx in orphans) {
                            var c = _plan.Colliders[idx];
                            EditorGUILayout.LabelField($"  [{idx}] {c.Name} on {AvatarQol.GetGameObjectPath(c.AttachTo?.gameObject)}",
                                AvatarQolStyles.Mono);
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawChainBlock(BoneChain chain, PhysBoneSpec pb) {
            string rootPath = AvatarQol.GetGameObjectPath(chain.Root?.gameObject);
            bool collapsed = _collapsedChainRoots.Contains(rootPath);
            string title = $"Chain — {chain.Root?.name} → {chain.Tip?.name}  ({chain.Bones.Count} bones, {chain.LengthMetres:F3} m)";
            bool now = EditorGUILayout.Foldout(!collapsed,
                new GUIContent(title, "Click to collapse / expand this chain's PhysBone details."),
                true, AvatarQolStyles.FoldoutHeader);
            if (now == collapsed) {
                if (now) _collapsedChainRoots.Remove(rootPath);
                else _collapsedChainRoots.Add(rootPath);
            }
            if (collapsed) return;

            using (new EditorGUILayout.VerticalScope()) {
                GUILayout.Space(2);
                EditorGUILayout.LabelField(
                    new GUIContent($"   PhysBone on {AvatarQol.GetGameObjectPath(pb.Root?.gameObject)}",
                        "The GameObject that will receive a VRCPhysBone component."),
                    AvatarQolStyles.Mono);

                // Parameter table — value + bold "**" mark when not SDK default.
                DrawParamRow("pull",       pb.Pull,           Defaults.Pull,       PullHint(pb.Pull));
                DrawParamRow("spring",     pb.Spring,         Defaults.Spring,     SpringHint(pb.Spring));
                DrawParamRow("stiffness",  pb.Stiffness,      Defaults.Stiffness,  StiffHint(pb.Stiffness));
                DrawParamRow("gravity",    pb.Gravity,        Defaults.Gravity,    GravityHint(pb.Gravity));
                DrawParamRow("gravityFalloff", pb.GravityFalloff, Defaults.GravityFalloff,
                    "0–1; how concentrated gravity is at the chain tip vs the root. Higher = tip droops more, root stays put.");
                DrawParamRowEnum("immobileType", pb.ImmobileType.ToString(), "None",
                    "None / AllMotion / WorldRotation. WorldRotation makes the bone resist motion only when the avatar rotates — typical for ears so head turns don't whip them.");
                DrawParamRow("immobile",   pb.Immobile,       0f,
                    "0–1; strength of the immobile constraint above. 0.5 ≈ half-resist.");
                DrawParamRow("radius",     pb.Radius,         0f,
                    $"Capsule radius in metres (current: {pb.Radius:F3} m). Wider = more solid feel, more clipping with body.");
                DrawParamRowEnum("allowCollision", pb.AllowCollision.ToString(), "True",
                    "True / False / Other. Whether this PhysBone responds to PhysBoneColliders.");
                DrawParamRowEnum("allowGrabbing", pb.AllowGrabbing.ToString(), "True",
                    "True / False / Other. Whether VRChat users in-world can grab this bone.");
                DrawParamRowEnum("allowPosing", pb.AllowPosing.ToString(), "True",
                    "True / False / Other. Whether grabbed bones stay where you leave them when released.");

                if (pb.ColliderRefs.Count > 0) {
                    EditorGUILayout.LabelField(
                        new GUIContent("   Colliders attached:",
                            "Colliders this PhysBone will reference. Each row matches an entry from the plan's collider list."),
                        AvatarQolStyles.Mono);
                    foreach (var idx in pb.ColliderRefs) {
                        if (idx < 0 || idx >= _plan.Colliders.Count) continue;
                        var c = _plan.Colliders[idx];
                        EditorGUILayout.LabelField(
                            $"     [{idx}] {c.Name} on {AvatarQol.GetGameObjectPath(c.AttachTo?.gameObject)}  ({c.Shape}, r={c.Radius:F3} h={c.Height:F3})",
                            AvatarQolStyles.Mono);
                    }
                }
                if (!string.IsNullOrEmpty(pb.Note)) {
                    EditorGUILayout.LabelField("   • " + pb.Note, AvatarQolStyles.Muted);
                }
                GUILayout.Space(4);
            }
        }

        private static void DrawParamRow(string name, float value, float sdkDefault, string hint) {
            bool diverges = !Mathf.Approximately(value, sdkDefault);
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"     {name}", AvatarQolStyles.Mono, GUILayout.Width(140));
                var style = diverges ? new GUIStyle(AvatarQolStyles.Mono) { fontStyle = FontStyle.Bold } : AvatarQolStyles.Mono;
                EditorGUILayout.LabelField(
                    new GUIContent($"{value:F3}{(diverges ? " **" : "")}",
                        diverges
                            ? $"Preset overrides the SDK default ({sdkDefault:F3}) → {value:F3}.\n\n{hint}"
                            : $"Matches the SDK default ({sdkDefault:F3}).\n\n{hint}"),
                    style, GUILayout.Width(80));
                EditorGUILayout.LabelField(new GUIContent(hint, hint), AvatarQolStyles.Muted);
            }
        }

        private static void DrawParamRowEnum(string name, string value, string sdkDefault, string hint) {
            bool diverges = value != sdkDefault;
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"     {name}", AvatarQolStyles.Mono, GUILayout.Width(140));
                var style = diverges ? new GUIStyle(AvatarQolStyles.Mono) { fontStyle = FontStyle.Bold } : AvatarQolStyles.Mono;
                EditorGUILayout.LabelField(
                    new GUIContent($"{value}{(diverges ? " **" : "")}",
                        diverges
                            ? $"Preset overrides the SDK default ({sdkDefault}) → {value}.\n\n{hint}"
                            : $"Matches the SDK default ({sdkDefault}).\n\n{hint}"),
                    style, GUILayout.Width(120));
                EditorGUILayout.LabelField(new GUIContent(hint, hint), AvatarQolStyles.Muted);
            }
        }

        // SDK default values — referenced by DrawParamRow for the bold-vs-default mark.
        private sealed class SdkDefaults {
            public readonly float Pull            = 0.2f;
            public readonly float Spring          = 0.5f;
            public readonly float Stiffness       = 0.4f;
            public readonly float Gravity         = 0f;
            public readonly float GravityFalloff  = 0f;
        }

        // Per-value hint text — translated to plain language for tooltips.
        private static string PullHint(float v) =>
            v < 0.15f ? $"pull = {v:F2}: low — chain swings freely"
            : v < 0.35f ? $"pull = {v:F2}: moderate — gentle return to rest"
            : $"pull = {v:F2}: stiff — snaps back quickly";
        private static string SpringHint(float v) =>
            v < 0.25f ? $"spring = {v:F2}: low oscillation"
            : v < 0.55f ? $"spring = {v:F2}: moderate bounce"
            : $"spring = {v:F2}: high bounce, springy feel";
        private static string StiffHint(float v) =>
            v < 0.25f ? $"stiffness = {v:F2}: floppy mid-chain"
            : v < 0.55f ? $"stiffness = {v:F2}: balanced"
            : $"stiffness = {v:F2}: rigid mid-chain";
        private static string GravityHint(float v) =>
            v < 0.05f ? $"gravity = {v:F2}: nearly weightless"
            : v < 0.20f ? $"gravity = {v:F2}: light droop"
            : $"gravity = {v:F2}: heavy droop";

        // -------- Apply / tweak / advanced --------

        private void DrawApplyBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canApply = PhysBonePlanApplier.SdkAvailable
                                && _plan != null
                                && _plan.PhysBones.Count > 0;
                using (new EditorGUI.DisabledScope(!canApply)) {
                    string label = canApply
                        ? $"Apply ({_plan.PhysBones.Count} PhysBone(s), {_plan.Colliders.Count} collider(s))"
                        : "Apply";
                    if (AvatarQolStyles.PrimaryButtonInline(
                            new GUIContent(label,
                                "Create the listed components on the listed bones in one Undo group. Ctrl+Z reverts. VRC SDK 3 must be installed."),
                            GUILayout.MinWidth(260))) {
                        ApplyPlan();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Close", "Close this window. Plan is discarded; nothing is written."),
                        GUILayout.Height(28), GUILayout.Width(80))) Close();
            }
        }

        private void DrawTweakStrip() {
            using (AvatarQolStyles.Section($"Just applied ({_tweakSnapshots.Count} PhysBone(s)) — tweak",
                    "Multiplicative scalars applied on top of the original preset values. Drag back to 1.0× to restore exactly. Disappears when you change the selection or pick a different preset.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(new GUIContent("Reset all to 1×",
                            "Restore every just-applied PhysBone to its original preset values."),
                            GUILayout.Width(140))) {
                        _tweakPull = _tweakSpring = _tweakStiff = _tweakGravity = _tweakRadius = 1f;
                        ApplyTweaks();
                    }
                    if (GUILayout.Button(new GUIContent("Dismiss",
                            "Hide the tweak strip. The applied values stay; Ctrl+Z still reverts the original Apply."),
                            GUILayout.Width(80))) {
                        _tweakSnapshots = null;
                    }
                }
                AvatarQolStyles.LabeledField(new GUIContent("Spring ×",  "Scale the spring parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakSpring, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakSpring)) { _tweakSpring = v; ApplyTweaks(); } });
                AvatarQolStyles.LabeledField(new GUIContent("Pull ×",    "Scale the pull parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakPull, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakPull)) { _tweakPull = v; ApplyTweaks(); } });
                AvatarQolStyles.LabeledField(new GUIContent("Stiffness ×", "Scale the stiffness parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakStiff, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakStiff)) { _tweakStiff = v; ApplyTweaks(); } });
                AvatarQolStyles.LabeledField(new GUIContent("Gravity ×", "Scale the gravity parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakGravity, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakGravity)) { _tweakGravity = v; ApplyTweaks(); } });
                AvatarQolStyles.LabeledField(new GUIContent("Radius ×",  "Scale the radius parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakRadius, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakRadius)) { _tweakRadius = v; ApplyTweaks(); } });
            }
        }

        private void DrawAdvanced() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Re-run analysis manually, plus the raw plan dump for debugging."),
                true, AvatarQolStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(_selection.Count == 0)) {
                    if (GUILayout.Button(new GUIContent("Refresh analysis",
                            "Re-walk the selection and rebuild the suggestion scores + plan."))) RebuildAnalysis();
                }
            }
        }

        // ------ Logic --------------------------------------------------------

        private void RebuildAnalysis() {
            _analysis = BoneSelectionAnalysis.Build(_selection);
            _suggestionScores.Clear();
            _suggestionExplanations.Clear();
            foreach (var p in _presets) {
                try { _suggestionScores[p.Id] = p.SuggestionScore(_analysis); }
                catch { _suggestionScores[p.Id] = 0f; }
                try { _suggestionExplanations[p.Id] = new List<ScoringSignal>(p.ExplainScore(_analysis) ?? Array.Empty<ScoringSignal>()); }
                catch { _suggestionExplanations[p.Id] = new List<ScoringSignal>(); }
            }
            var suggestion = SuggestedPreset();
            if (string.IsNullOrEmpty(_selectedPresetId) || SelectedPreset() == null) {
                _selectedPresetId = suggestion?.Id;
            }
            // Selection changed → drop any stale tweak snapshot.
            _tweakSnapshots = null;
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
            // Capture the just-created components for the tweak strip.
            CaptureTweakSnapshots();
            if (_plan.PhysBones.Count > 0 && _plan.PhysBones[0].Root != null) {
                Selection.activeGameObject = _plan.PhysBones[0].Root.gameObject;
            }
            // Reset slider scalars; we're at 1.0× of the original values.
            _tweakPull = _tweakSpring = _tweakStiff = _tweakGravity = _tweakRadius = 1f;
            RebuildAnalysis();
        }

        private void CaptureTweakSnapshots() {
            _tweakSnapshots = new List<TweakSnapshot>();
#if VRC_SDK_VRCSDK3
            foreach (var spec in _plan.PhysBones) {
                if (spec.Root == null) continue;
                var components = spec.Root.GetComponents<VRCPhysBone>();
                if (components.Length == 0) continue;
                // Take the most recently added — last in the array.
                var pb = components[components.Length - 1];
                _tweakSnapshots.Add(new TweakSnapshot {
                    PhysBone = pb,
                    OriginalPull = pb.pull,
                    OriginalSpring = pb.spring,
                    OriginalStiffness = pb.stiffness,
                    OriginalGravity = pb.gravity,
                    OriginalRadius = pb.radius,
                });
            }
#endif
        }

        private void ApplyTweaks() {
#if VRC_SDK_VRCSDK3
            if (_tweakSnapshots == null) return;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: tweak just-applied PhysBones");
            foreach (var snap in _tweakSnapshots) {
                if (snap.PhysBone == null) continue;
                Undo.RegisterCompleteObjectUndo(snap.PhysBone, "Tweak PhysBone");
                snap.PhysBone.pull      = snap.OriginalPull      * _tweakPull;
                snap.PhysBone.spring    = snap.OriginalSpring    * _tweakSpring;
                snap.PhysBone.stiffness = snap.OriginalStiffness * _tweakStiff;
                snap.PhysBone.gravity   = snap.OriginalGravity   * _tweakGravity;
                snap.PhysBone.radius    = snap.OriginalRadius    * _tweakRadius;
                EditorUtility.SetDirty(snap.PhysBone);
            }
            Undo.CollapseUndoOperations(undoGroup);
#endif
        }

        private sealed class TweakSnapshot {
#if VRC_SDK_VRCSDK3
            public VRCPhysBone PhysBone;
#else
            public UnityEngine.Object PhysBone;
#endif
            public float OriginalPull;
            public float OriginalSpring;
            public float OriginalStiffness;
            public float OriginalGravity;
            public float OriginalRadius;
        }
    }
}
