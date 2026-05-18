// WeightSanityCheckWindow.cs
//
// Detects the most common kind of weight contamination introduced by
// Blender's Data Transfer / robust weight transfer: vertices on one side of
// the avatar (say, a garter on the LEFT leg) getting non-trivial weight
// from a bone on the OTHER side (Right leg). When the avatar moves, those
// stray weights stretch or follow the wrong limb.
//
// Detection layers (in order of confidence):
//   1. Bone has a Humanoid ancestor on the OPPOSITE side of the vertex.
//      e.g. vertex on Left, bone descended from RightUpperLeg → flagged.
//   2. Bone has NO Humanoid ancestor (custom rig bone, prop bone, etc.) but
//      its OWN world position sits on the OPPOSITE side of the vertex.
//      e.g. vertex on Left, bone is a custom bone whose pivot is on the
//      avatar's right side → flagged with category "spatial".
//
// Center-band coverage: vertices in the centre stripe (between -centerMargin
// and +centerMargin in Hips local X) are also scanned, but only flagged when
// a single weight to a Left or Right bone exceeds a higher threshold
// (_centerCrossSideFloor). This catches stray spine/crotch weights without
// drowning the user in shoulder/clavicle bleed (which is normal).
//
// Vertex world-position derivation: we use proper bind-pose math —
//   bonesPerVertex[v]'s highest-weight bone is the "anchor"; multiply the
//   mesh-local vertex by `mesh.bindposes[boneIdx]` to get bone-local
//   coords, then `bone.TransformPoint(...)` to get world. This is rig-
//   independent and doesn't rely on the renderer's GameObject sitting at
//   any particular place. (An earlier version used renderer.transform or
//   rootBone directly; both produced wrong classifications on real-world
//   rigs where the mesh-local frame doesn't align with where the
//   GameObject sits — symptom: every vertex got bucketed as Center.)
// Caveat: the bone's CURRENT world transform is used, so if the avatar is
// being driven by an animator the result is the deformed position rather
// than the bind-pose position. Pause animator / scrub to T-pose before
// scanning when in doubt.
//
// Wobble / debug:
//   - Per-issue *Wobble* button: rotates the offending bone back and forth
//     so the user can watch the deformation. Click again to stop.
//   - Verbose log: dumps per-renderer scan stats to the console — exactly
//     why each weight was flagged or skipped, so it's possible to tell
//     "we didn't see this issue because the bone is Unknown" vs "the weight
//     was below the floor."
//   - "Dump weights for selection" button: takes the current SkinnedMeshRenderer
//     selection and prints every vertex's bone weights with side classifications.
//     Pinpoint debugging when an issue is missed.
//
// What we deliberately don't do (yet):
//   - Bone-graph distance violations (vertices weighted to bones very far
//     apart in the hierarchy).
//   - Per-island weight variance.
//   - Mutate weights ("fix" them). The tool is a checker — humans review
//     before changing skinning data.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class WeightSanityCheckWindow : EditorWindow {

        // Persisted across domain reloads.
        [SerializeField] private Animator _animator;
        // Optional: when set, Scan walks only this renderer instead of every
        // SkinnedMeshRenderer under the avatar. Lets the user focus on a
        // specific outfit / mesh while debugging without touching the
        // exclusion list.
        [SerializeField] private SkinnedMeshRenderer _limitToRenderer;
        // Lower default than the original 0.01: real bleed in the 0.001-0.005
        // range still causes visible stretching during animation. Tunable
        // upward if it's flagging too much.
        [SerializeField] private float _weightFloor   = 0.005f;
        [SerializeField] private float _centerMargin  = 0.02f;
        // Vertices in the centre stripe (|x| < centerMargin in Hips local
        // space) are scanned only when _scanCenterBand is on. Even then
        // their threshold for "this side weight is suspicious" is higher
        // than the regular floor: centre-band cross-side bleed is mostly
        // legitimate (spine vertices with a tiny shoulder weight, etc.).
        // 0.10 = 10% of total influence — a clear majority weight on one
        // side from what should be a centre-anchored vertex.
        [SerializeField] private bool  _scanCenterBand = false;
        [SerializeField] private float _centerCrossSideFloor = 0.10f;
        [SerializeField] private bool  _showGizmos    = true;
        [SerializeField] private bool  _verboseLog    = false;

        // Vertex inspector: the user types a vertex index (or picks one from
        // a dump) and the tool walks every weight on that vertex with full
        // verdict reasoning, so it's possible to tell *exactly* why a weight
        // wasn't flagged.
        [SerializeField] private SkinnedMeshRenderer _inspectRenderer;
        [SerializeField] private int _inspectVertexIndex = 0;

        [SerializeField] private List<SkinnedMeshRenderer> _excludedRenderers = new List<SkinnedMeshRenderer>();

        // UI state — persisted across reloads so a power user who opened
        // Advanced once doesn't have to re-open it every session.
        [SerializeField] private bool _advancedOpen;
        [SerializeField] private bool _showConsoleNoticeAfterInspect;
        [SerializeField] private bool _showConsoleNoticeAfterDump;
        // Per-renderer collapsed state in the issue list, keyed by RendererPath.
        [SerializeField] private List<string> _collapsedRenderers = new List<string>();
        // Per-issue expansion in the compact issue rows.
        private readonly HashSet<int> _expandedIssueRows = new HashSet<int>();

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#weight-sanity-check";

        private readonly List<Issue> _issues = new List<Issue>();
        // Tracked per scan so we can offer a "Enable Read/Write on these N
        // meshes" button below the scan output.
        private readonly List<SkinnedMeshRenderer> _nonReadableRenderers = new List<SkinnedMeshRenderer>();
        private string _scanSummary = "";
        private Vector2 _scroll;

        // Preview state — at most one bone is animated at a time. Bone is
        // wobbled around its rest rotation; on stop we restore.
        private Transform _previewBone;
        private Quaternion _previewRestRotation;
        private double _previewStart;

        // ------ Public entry points ----------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<WeightSanityCheckWindow>(false, "Weight Sanity Check", true);
            w.titleContent = new GUIContent("Avatar QoL — Weight Sanity Check");
            w.minSize = new Vector2(600, 460);
            if (prefillFromSelection) {
                var sel = Selection.activeGameObject;
                if (sel != null) {
                    var anim = sel.GetComponent<Animator>() ?? sel.GetComponentInChildren<Animator>(true);
                    if (anim != null && anim.isHuman) w._animator = anim;
                }
            }
            w.Show();
            w.Focus();
        }

        // ------ Lifecycle --------------------------------------------------

        private void OnEnable() {
            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable() {
            SceneView.duringSceneGui -= OnSceneGui;
            EditorApplication.update -= OnEditorUpdate;
            StopPreview();
        }

        private void OnDestroy() {
            StopPreview();
        }

        // ------ GUI --------------------------------------------------------

        private void OnGUI() {
            // Top-of-window banner only when applicable; otherwise it lives
            // inside Advanced. Hoisting it here keeps the user from missing
            // a half-skipped scan.
            if (_nonReadableRenderers.Count > 0) DrawNonReadableBanner();
            DrawTitleBar();
            AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Info,
                "Flow: pick the avatar Animator, scan, review weight rows, then fix or tune what matters. PhysBone clipping has its own window so this scan stays fast.");
            DrawHeader();
            EditorGUILayout.Space(2);
            DrawScanBar();
            EditorGUILayout.Space(2);
            DrawIssues();
            EditorGUILayout.Space(4);
            DrawAdvanced();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Weight Sanity Check",
                        "Find mesh weights that pull part of the avatar toward the wrong left/right side, then review or fix them."),
                    AvatarQolStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawHeader() {
            using (AvatarQolStyles.Section("1. Pick avatar",
                    "Choose the Humanoid avatar to scan. Optionally narrow the scan to one renderer while debugging an outfit or mesh.")) {
                AvatarQolStyles.LabeledField(
                    new GUIContent("Animator",
                        "The Humanoid Animator at the root of your avatar. The scan walks every SkinnedMeshRenderer underneath it and uses the Humanoid bone bindings (Hips, LeftUpperLeg, RightUpperLeg) to derive the avatar's left/right axis. Generic / non-Humanoid rigs aren't supported."),
                    () => {
                        var newAnim = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (newAnim != _animator) { _animator = newAnim; _issues.Clear(); _scanSummary = ""; }
                    });
                if (_animator != null && !_animator.isHuman) {
                    AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                        "Animator is not Humanoid. The symmetry check needs Humanoid bone bindings (LeftUpperLeg, RightUpperLeg, Hips).");
                }
                AvatarQolStyles.LabeledField(
                    new GUIContent("Only scan renderer",
                        "Optional. When set, Scan only walks this single SkinnedMeshRenderer instead of every renderer under the avatar. Useful when debugging one outfit / mesh without touching the exclusion list. Auto-fills the Inspect Vertex renderer below."),
                    () => {
                        var newLimit = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_limitToRenderer, typeof(SkinnedMeshRenderer), true);
                        if (newLimit != _limitToRenderer) {
                            _limitToRenderer = newLimit;
                            if (newLimit != null && _inspectRenderer == null) _inspectRenderer = newLimit;
                        }
                    });
                if (_animator != null && _limitToRenderer != null
                        && !_limitToRenderer.transform.IsChildOf(_animator.transform)) {
                    AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                        "The 'Limit scan to' renderer is not a descendant of the picked Animator. The scan will still run on it, but side classification uses the Animator's Hips so it may misbehave for renderers parented elsewhere.");
                }
            }
        }

        private void DrawAdvanced() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Detection thresholds, exclusion list, diagnostics (verbose log, gizmos), and the per-vertex inspector. Folded by default — most users only need Animator + Scan + Fix all."),
                true, AvatarQolStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            DrawTunables();
            EditorGUILayout.Space(2);
            DrawExclusions();
            EditorGUILayout.Space(2);
            DrawDiagnostics();
        }

        private void DrawTunables() {
            using (AvatarQolStyles.Section("Detection thresholds",
                    "Knobs that control how aggressive the scanner is. Sensible defaults; tune only when the issue list is too noisy or too quiet.")) {
                AvatarQolStyles.LabeledField(
                    new GUIContent("Weight floor",
                        "Weights below this are ignored as noise. Range 0–0.5; 0.005 ≈ 0.5% influence per vertex. Raise toward 0.02 if you see false positives; lower toward 0.001 if real bleed (≤ 0.005) is being missed."),
                    () => _weightFloor = EditorGUILayout.Slider(_weightFloor, 0f, 0.5f));
                AvatarQolStyles.LabeledField(
                    new GUIContent("Center margin",
                        "Half-width of the centre stripe in metres, in Hips local X. Vertices within ±this distance of the spine count as Center, not Left/Right. 0–0.2 m. 0.02 m ≈ 2 cm. Increase if shoulder/clavicle vertices keep getting flagged; decrease if real cross-side bleed near the spine is being missed."),
                    () => _centerMargin = EditorGUILayout.Slider(_centerMargin, 0f, 0.2f));
                using (new EditorGUILayout.HorizontalScope()) {
                    _scanCenterBand = EditorGUILayout.ToggleLeft(
                        new GUIContent("Scan centre-band vertices",
                            "When on, vertices in the centre stripe are also scanned for cross-side weights. Off by default — centre-band bleed (spine ↔ clavicle, hip ↔ pelvis) is usually legitimate and floods the issue list."),
                        _scanCenterBand, GUILayout.Width(220));
                }
                if (_scanCenterBand) {
                    AvatarQolStyles.LabeledField(
                        new GUIContent("Centre threshold",
                            "Minimum weight a centre-stripe vertex must have to a Left or Right bone before it's flagged. Higher than the regular floor because small bleed near the spine is usually fine."),
                        () => _centerCrossSideFloor = EditorGUILayout.Slider(_centerCrossSideFloor, 0f, 0.5f));
                }
            }
        }

        private void DrawDiagnostics() {
            using (AvatarQolStyles.Section("Diagnostics",
                    "Visualisation, logging, and the per-vertex Inspect tool. Helpful when the scan isn't flagging something you expected, or when triaging hundreds of issues.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    _showGizmos = EditorGUILayout.ToggleLeft(
                        new GUIContent("Show gizmos in Scene view",
                            "Draw a red marker at every flagged vertex's bind-pose world position. Helps you see where issues cluster on the avatar."),
                        _showGizmos, GUILayout.Width(220));
                    _verboseLog = EditorGUILayout.ToggleLeft(
                        new GUIContent("Verbose log",
                            "On scan, dump per-renderer stats and per-skipped-weight reasons to the Unity console. Useful for understanding why a weight you expected isn't being flagged."),
                        _verboseLog);
                }
                EditorGUILayout.Space(4);
                DrawVertexInspector();
                if (_showConsoleNoticeAfterInspect) {
                    if (AvatarQolStyles.ConsoleResultNotice("Inspect output")) {
                        EditorApplication.ExecuteMenuItem("Window/General/Console");
                        _showConsoleNoticeAfterInspect = false;
                    }
                }
                EditorGUILayout.Space(2);
                if (GUILayout.Button(
                        new GUIContent("Dump weights for selection",
                            "Print every vertex's bone weights for the currently selected SkinnedMeshRenderer to the Unity console. Useful when an issue you expect isn't being flagged."),
                        GUILayout.Height(22))) {
                    DumpSelectedRendererWeights();
                }
                if (_showConsoleNoticeAfterDump) {
                    if (AvatarQolStyles.ConsoleResultNotice("Weight dump")) {
                        EditorApplication.ExecuteMenuItem("Window/General/Console");
                        _showConsoleNoticeAfterDump = false;
                    }
                }
            }
        }

        private void DrawExclusions() {
            using (AvatarQolStyles.Section("Exclude renderers (legit cross-side)",
                    "Add any SkinnedMeshRenderer that bridges left/right by design (capes, dresses, tails). They won't be scanned.")) {
                if (_excludedRenderers.Count == 0) {
                    EditorGUILayout.LabelField("(none)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    for (int i = 0; i < _excludedRenderers.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            _excludedRenderers[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "A SkinnedMeshRenderer the scan should ignore (e.g. capes, dresses, tails that legitimately bridge left and right)."),
                                _excludedRenderers[i], typeof(SkinnedMeshRenderer), true);
                            if (GUILayout.Button(new GUIContent("×", "Remove this renderer from the exclusion list."),
                                    EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                        }
                    }
                    if (removeIndex >= 0) _excludedRenderers.RemoveAt(removeIndex);
                }
                if (GUILayout.Button(new GUIContent("Add row", "Append an empty slot for a new renderer to exclude. Drop a SkinnedMeshRenderer onto it after."),
                        EditorStyles.miniButton, GUILayout.Width(80))) {
                    _excludedRenderers.Add(null);
                }
            }
        }

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canScan = _animator != null && _animator.isHuman;
                using (new EditorGUI.DisabledScope(!canScan)) {
                    if (AvatarQolStyles.PrimaryButtonInline(
                            new GUIContent("Scan",
                                "Walk every SkinnedMeshRenderer under the Animator (or just the renderer selected above) and flag vertices weighted to a bone on the avatar's opposite side. Run again any time after a fix to refresh."),
                            GUILayout.MinWidth(140))) Scan();
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop wobble",
                                "Restore the currently-wobbled bone to its rest rotation."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        StopPreview();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_scanSummary)) {
                    EditorGUILayout.LabelField(_scanSummary, AvatarQolStyles.Muted);
                }
            }
        }

        private void DrawIssues() {
            // Header bar: "Issues (N)" + Fix all + Clear, attached to the
            // list so the action sits next to what it acts on.
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent(_issues.Count > 0 ? $"Issues ({_issues.Count})" : "Issues",
                        "Step 3. Each row is one suspicious bone weight on one vertex. The bracketed tag shows confidence: [humanoid] = bone is on the wrong Humanoid side, [spatial] = inferred from world position, [center] = mid-line bleed."),
                    AvatarQolStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_issues.Count == 0)) {
                    if (AvatarQolStyles.PrimaryButtonInline(
                            new GUIContent($"Fix all ({_issues.Count})",
                                "Apply fixes to every issue currently in the list. Each weight is redirected to its matching left/right Humanoid bone when one exists, otherwise it is removed and the remaining weights on that vertex are scaled up. FBX meshes are cloned first; the original FBX is never modified."),
                            GUILayout.Width(150))) {
                        FixIssues(new List<Issue>(_issues), $"{_issues.Count} issue(s)");
                    }
                    if (GUILayout.Button(
                            new GUIContent("Clear",
                                "Drop the current issue list and clear the gizmo overlay. Doesn't undo any fixes you've already applied."),
                            GUILayout.Height(28), GUILayout.Width(70))) {
                        _issues.Clear();
                        _scanSummary = "";
                        _expandedIssueRows.Clear();
                        SceneView.RepaintAll();
                    }
                }
            }

            // Inline legend so the bracket tags aren't mystery jargon.
            if (_issues.Count > 0) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField("Legend:", AvatarQolStyles.Muted, GUILayout.Width(54));
                    AvatarQolStyles.BadgePill("humanoid", AvatarQolStyles.CategoryHumanoid,
                        "Bone has a Humanoid ancestor on the avatar's opposite side from the vertex. Highest-confidence flag.");
                    AvatarQolStyles.BadgePill("spatial", AvatarQolStyles.CategorySpatial,
                        "Bone has no Humanoid ancestor; its world pivot sits on the opposite side of the vertex.");
                    AvatarQolStyles.BadgePill("center", AvatarQolStyles.CategoryCenter,
                        "Vertex is in the centre stripe; a Left or Right bone exceeded the higher centre threshold.");
                    GUILayout.FlexibleSpace();
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick an Animator, then click Scan." : "No issues found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    // Pre-bucket counts once per draw — was O(n²) before.
                    var perRendererCount = new Dictionary<SkinnedMeshRenderer, int>();
                    foreach (var i in _issues) {
                        if (i.Renderer == null) continue;
                        perRendererCount.TryGetValue(i.Renderer, out var n);
                        perRendererCount[i.Renderer] = n + 1;
                    }
                    SkinnedMeshRenderer lastRenderer = null;
                    bool currentCollapsed = false;
                    int issueIndex = 0;
                    foreach (var i in _issues) {
                        int captured = issueIndex++;
                        if (i.Renderer != lastRenderer) {
                            int count = i.Renderer != null && perRendererCount.TryGetValue(i.Renderer, out var n) ? n : 0;
                            currentCollapsed = _collapsedRenderers.Contains(i.RendererPath);
                            using (new EditorGUILayout.HorizontalScope()) {
                                bool now = EditorGUILayout.Foldout(!currentCollapsed,
                                    new GUIContent($"{i.RendererPath}  —  {count} issue(s)" + (i.Renderer == null ? "  (renderer destroyed)" : ""),
                                        "Click to collapse all issues from this renderer. Useful when one mesh has hundreds of issues you've already triaged."),
                                    true, AvatarQolStyles.FoldoutHeader);
                                bool nowCollapsed = !now;
                                if (nowCollapsed != currentCollapsed) {
                                    if (nowCollapsed) _collapsedRenderers.Add(i.RendererPath);
                                    else _collapsedRenderers.Remove(i.RendererPath);
                                    currentCollapsed = nowCollapsed;
                                }
                                GUILayout.FlexibleSpace();
                            }
                            lastRenderer = i.Renderer;
                        }
                        if (!currentCollapsed) DrawIssueRowCompact(i, captured);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

#if false
        private void DrawPhysBoneIssueRow(PhysBoneClippingAnalyzer.Issue issue) {
            var severityColor = issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                ? AvatarQolStyles.CategoryHumanoid
                : AvatarQolStyles.ColorWarning;
            var severityText = issue.Severity == PhysBoneClippingAnalyzer.Severity.High ? "high" : "medium";
            string boneName = issue.DrivenBone != null ? issue.DrivenBone.name : "(destroyed)";
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(6);
                AvatarQolStyles.BadgePill(severityText, severityColor,
                    issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                        ? "No effective collider coverage or already-small clearance. This deserves attention."
                        : "Collider coverage exists or the estimated overlap is smaller, but the area is still worth checking.");
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"{issue.RendererPath}  v#{issue.VertexIndex}  {boneName}  move~{issue.EstimatedMotion * 100f:0.0}cm  clearance {issue.Clearance * 100f:0.0}cm",
                        issue.Reason),
                    AvatarQolStyles.Mono);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(issue.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("P", "Ping the renderer in the hierarchy."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = issue.Renderer;
                        EditorGUIUtility.PingObject(issue.Renderer);
                    }
                }
                if (GUILayout.Button(new GUIContent("F", "Frame the risky vertex in the Scene view."),
                        AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(issue.WorldPosition, sv.rotation, 0.18f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(issue.DrivenBone == null)) {
                    if (GUILayout.Button(new GUIContent("R", "Reveal the PhysBone-driven transform."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = issue.DrivenBone;
                        EditorGUIUtility.PingObject(issue.DrivenBone);
                        FlashHighlight(issue.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == issue.DrivenBone && issue.DrivenBone != null;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
                            "Temporarily wobble the driven transform so you can inspect likely clipping. This does not move the Scene camera."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else StartPreview(issue.DrivenBone);
                    }
                }
            }
            EditorGUILayout.LabelField("   " + issue.Reason, AvatarQolStyles.Muted);
            EditorGUILayout.LabelField($"   nearest surface: {issue.NearestSurfacePath}", AvatarQolStyles.Muted);
        }

#endif
        private void DrawIssueRowCompact(Issue i, int issueIndex) {
            string boneName = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
            Color tag; string tagText; string tagTooltip;
            switch (i.Category) {
                case IssueCategory.HumanoidCrossSide:
                    tag = AvatarQolStyles.CategoryHumanoid; tagText = "humanoid";
                    tagTooltip = "Bone has a Humanoid ancestor on the avatar's opposite side from the vertex. Highest-confidence flag.";
                    break;
                case IssueCategory.SpatialCrossSide:
                    tag = AvatarQolStyles.CategorySpatial; tagText = "spatial";
                    tagTooltip = "Bone has no Humanoid ancestor; its world pivot sits on the opposite side of the vertex.";
                    break;
                case IssueCategory.CenterBandSideBleed:
                    tag = AvatarQolStyles.CategoryCenter; tagText = "center";
                    tagTooltip = "Vertex is in the centre stripe; a Left or Right bone exceeded the higher centre threshold.";
                    break;
                default:
                    tag = AvatarQolStyles.ColorInfo; tagText = "?"; tagTooltip = ""; break;
            }
            bool expanded = _expandedIssueRows.Contains(issueIndex);

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(6);
                // Foldout caret on the far left toggles the per-row details.
                bool now = EditorGUILayout.Foldout(expanded, GUIContent.none, true);
                if (now != expanded) {
                    if (now) _expandedIssueRows.Add(issueIndex);
                    else _expandedIssueRows.Remove(issueIndex);
                }
                AvatarQolStyles.BadgePill(tagText, tag, tagTooltip);
                EditorGUILayout.LabelField(
                    new GUIContent($"v#{i.VertexIndex}  {i.VertexSide} → {boneName} ({i.BoneSide})  w={i.Weight:F3}",
                        $"Vertex #{i.VertexIndex} on the avatar's {i.VertexSide} side has weight {i.Weight:F3} on {boneName}, which is classified {i.BoneSide}. Click ∨ for the world position; use the row buttons to investigate or fix."),
                    AvatarQolStyles.Mono);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(i.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("P", "Ping the renderer in the hierarchy."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                        if (i.Renderer != null) {
                            Selection.activeObject = i.Renderer;
                            EditorGUIUtility.PingObject(i.Renderer);
                        }
                    }
                }
                if (GUILayout.Button(new GUIContent("F", "Frame: move Scene camera to the vertex."),
                        AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(i.WorldPosition, sv.rotation, 0.15f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(i.OffendingBone == null)) {
                    if (GUILayout.Button(new GUIContent("R",
                            "Reveal: select the offending bone, frame the vertex, and flash a marker disc in the Scene view for two seconds."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = i.OffendingBone;
                        EditorGUIUtility.PingObject(i.OffendingBone);
                        var sv = SceneView.lastActiveSceneView;
                        if (sv != null) { sv.LookAt(i.WorldPosition, sv.rotation, 0.15f); sv.Repaint(); }
                        FlashHighlight(i.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == i.OffendingBone && i.OffendingBone != null;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
                            "Temporarily wobble the offending bone so you can see how the bad weights deform the mesh. This does not move the Scene camera."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else if (i.OffendingBone != null) StartPreview(i.OffendingBone);
                    }
                }
                using (new EditorGUI.DisabledScope(i.Renderer == null || i.OffendingBone == null)) {
                    if (GUILayout.Button(new GUIContent("?",
                            "Why? Send this vertex to the Inspect Vertex panel and run a per-weight verdict — useful for understanding why a related weight didn't flag. Result prints to the Unity console."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(22))) {
                        _inspectRenderer = i.Renderer;
                        _inspectVertexIndex = i.VertexIndex;
                        InspectVertex();
                    }
                    if (GUILayout.Button(new GUIContent("Fix",
                            "Redirect this offending weight to the bone's Humanoid mirror (e.g. RightUpperLeg → LeftUpperLeg). When no mirror is available, zero the weight and renormalise the rest. FBX-imported meshes are cloned to an editable .mesh in Assets/AvatarQol Generated/ before any change."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(34))) {
                        var name = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
                        FixIssues(new List<Issue> { i }, $"weight on {name}");
                    }
                }
            }
            if (expanded) {
                EditorGUILayout.LabelField(
                    $"   world pos:  ({i.WorldPosition.x:F4}, {i.WorldPosition.y:F4}, {i.WorldPosition.z:F4})",
                    AvatarQolStyles.Muted);
                EditorGUILayout.LabelField(
                    $"   bone path:  {(i.OffendingBone != null ? AvatarQol.GetGameObjectPath(i.OffendingBone.gameObject) : "(destroyed)")}",
                    AvatarQolStyles.Muted);
            }
        }

        // Scene-view fade-out marker. Stores a single hit; the gizmo
        // overlay polls _flashUntil and renders a disc until then.
        private Vector3 _flashPos;
        private double _flashUntil;

        private void FlashHighlight(Vector3 worldPos) {
            _flashPos = worldPos;
            _flashUntil = EditorApplication.timeSinceStartup + 2.0;
            SceneView.RepaintAll();
        }

        private void DrawNonReadableBanner() {
            if (_nonReadableRenderers.Count == 0) return;
            if (AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                    $"{_nonReadableRenderers.Count} renderer(s) skipped — mesh has Read/Write disabled in importer.",
                    "Enable Read/Write & rescan",
                    "For every skipped renderer, find its source asset, set Read/Write Enabled in the model importer, reimport, then re-run the scan.")) {
                EnableReadWriteOnSkippedAndRescan();
            }
        }

        private void DrawVertexInspector() {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField(
                    new GUIContent("Inspect specific vertex",
                        "When an issue you expect isn't flagged, drop the renderer here and type the vertex index. The console gets a per-weight verdict explaining exactly which gate every weight passed or failed against the current thresholds."),
                    EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Renderer",
                            "The SkinnedMeshRenderer whose vertex you want to inspect. Auto-fills when you change 'Limit scan to' or click 'From selection' below."),
                        GUILayout.Width(64));
                    _inspectRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                        new GUIContent(GUIContent.none.image, "Drop the SkinnedMeshRenderer to inspect."),
                        _inspectRenderer, typeof(SkinnedMeshRenderer), true);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Vertex #",
                            "The mesh vertex index to inspect. Find candidate indices via 'Dump weights for selection' below, or click 'Why?' on any flagged issue to auto-fill this field with that issue's vertex."),
                        GUILayout.Width(64));
                    _inspectVertexIndex = EditorGUILayout.IntField(_inspectVertexIndex);
                    using (new EditorGUI.DisabledScope(_inspectRenderer == null || _animator == null || !_animator.isHuman)) {
                        if (GUILayout.Button(
                                new GUIContent("Inspect",
                                    "Print the verdict for this vertex against current thresholds. Output goes to the Unity console."),
                                GUILayout.Width(80))) {
                            InspectVertex();
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent("From selection",
                                "Set the renderer to whatever's currently selected in the hierarchy."),
                            GUILayout.Width(110))) {
                        var go = Selection.activeGameObject;
                        if (go != null) _inspectRenderer = go.GetComponent<SkinnedMeshRenderer>();
                    }
                }
            }
        }

        // For each unique mesh referenced by the skipped renderers, find its
        // source asset's ModelImporter and flip Read/Write on. Reimport, then
        // re-run the scan automatically. We only touch ModelImporter assets —
        // procedurally-built or in-memory meshes (where there's no importer)
        // can't be fixed this way and are skipped with a warning.
        private void EnableReadWriteOnSkippedAndRescan() {
            var importersToReimport = new HashSet<string>();
            int unfixable = 0;
            foreach (var r in _nonReadableRenderers) {
                if (r == null || r.sharedMesh == null) continue;
                var path = AssetDatabase.GetAssetPath(r.sharedMesh);
                if (string.IsNullOrEmpty(path)) { unfixable++; continue; }
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) { unfixable++; continue; }
                if (!importer.isReadable) {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
                importersToReimport.Add(path);
            }
            if (unfixable > 0) {
                Debug.LogWarning(
                    $"[Avatar QoL] {unfixable} skipped mesh(es) had no ModelImporter " +
                    $"(procedurally generated, or imported by a different pipeline). " +
                    $"Read/Write couldn't be auto-enabled on those.");
            }
            if (importersToReimport.Count > 0) {
                Debug.Log($"[Avatar QoL] Enabled Read/Write on {importersToReimport.Count} model asset(s); rescanning.");
            }
            Scan();
        }

        // Walks every weight on a single vertex and prints the verdict each
        // weight got against current thresholds. The most direct answer to
        // "why didn't this get flagged?".
        private void InspectVertex() {
            var smr = _inspectRenderer;
            if (smr == null) {
                EditorUtility.DisplayDialog("Inspect vertex", "Drop a SkinnedMeshRenderer first.", "OK");
                return;
            }
            if (_animator == null || !_animator.isHuman) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    "Pick a Humanoid Animator at the top of the window first; we need it for side classification.", "OK");
                return;
            }
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    "The renderer's mesh is null or not readable. Use 'Enable Read/Write & rescan' above if needed.", "OK");
                return;
            }
            if (_inspectVertexIndex < 0 || _inspectVertexIndex >= mesh.vertexCount) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    $"Vertex index {_inspectVertexIndex} is out of range (mesh has {mesh.vertexCount} vertices).", "OK");
                return;
            }

            var sideMap = new HumanoidSideMap(_animator);
            var bones = smr.bones;
            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var bindposes = mesh.bindposes;

            // Walk to the weight-cursor for the requested vertex.
            int cursor = 0;
            for (int v = 0; v < _inspectVertexIndex; v++) cursor += bonesPerVertex[v];
            int wCount = bonesPerVertex[_inspectVertexIndex];

            // Same bindpose-based world position as Scan: highest-weight
            // bone is the anchor. Falling back to renderer.transform is only
            // hit when the vertex has no usable weights (rare).
            int primaryIdx = -1;
            float primaryWeight = 0f;
            for (int w = 0; w < wCount; w++) {
                var bw = weights[cursor + w];
                if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                if (bones[bw.boneIndex] == null) continue;
                if (bw.weight > primaryWeight) { primaryWeight = bw.weight; primaryIdx = bw.boneIndex; }
            }
            Vector3 worldPos;
            string anchorDesc;
            if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                var meshLocal = verts[_inspectVertexIndex];
                var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                anchorDesc = $"bindpose anchor={bones[primaryIdx].name} (weight {primaryWeight:F3})";
            } else {
                worldPos = smr.transform.TransformPoint(verts[_inspectVertexIndex]);
                anchorDesc = "fallback=renderer.transform (no usable bone weight)";
            }
            var vertexSide = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
            bool isCenter = vertexSide == BoneSide.Center;
            float floor = isCenter ? _centerCrossSideFloor : _weightFloor;

            var sb = new StringBuilder();
            sb.AppendLine($"[Avatar QoL] Inspect vertex #{_inspectVertexIndex} of {AvatarQol.GetGameObjectPath(smr.gameObject)}");
            sb.AppendLine($"  world pos: ({worldPos.x:F4}, {worldPos.y:F4}, {worldPos.z:F4})  {anchorDesc}");
            sb.AppendLine($"  vertex side: {vertexSide} (isCenter={isCenter}, applicable floor={floor:F4})");
            sb.AppendLine($"  weights ({wCount}):");
            for (int w = 0; w < wCount; w++) {
                var bw = weights[cursor + w];
                Transform bone = bw.boneIndex >= 0 && bw.boneIndex < bones.Length ? bones[bw.boneIndex] : null;
                string boneName = bone != null ? bone.name : $"(invalid index {bw.boneIndex})";
                BoneSide humanoidSide = bone != null ? sideMap.GetSide(bone) : BoneSide.Unknown;
                BoneSide spatialSide = bone != null ? sideMap.ClassifyWorldPosition(bone.position, _centerMargin) : BoneSide.Unknown;
                BoneSide effectiveSide = humanoidSide != BoneSide.Unknown ? humanoidSide : spatialSide;
                string verdict;
                if (bone == null) {
                    verdict = "SKIPPED (invalid bone index)";
                } else if (bw.weight < floor) {
                    verdict = $"SKIPPED (weight {bw.weight:F4} < floor {floor:F4})";
                } else if (effectiveSide == BoneSide.Unknown) {
                    verdict = "SKIPPED (bone has no Humanoid ancestor and pivot is in centre band — Unknown side)";
                } else if (effectiveSide == BoneSide.Center) {
                    verdict = "SKIPPED (bone classified Center — same as central avatar mass)";
                } else if (!isCenter && effectiveSide == vertexSide) {
                    verdict = "SKIPPED (bone same side as vertex)";
                } else {
                    string cat = isCenter ? "center-band" : (humanoidSide != BoneSide.Unknown ? "humanoid" : "spatial");
                    verdict = $"FLAGGED [{cat}]  vertex={vertexSide} bone={effectiveSide}";
                }
                sb.AppendLine($"    {boneName}  weight={bw.weight:F4}  humanoid={humanoidSide}  spatial={spatialSide}  →  {verdict}");
            }
            Debug.Log(sb.ToString());
            _showConsoleNoticeAfterInspect = true;
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }

        // ------ Scan -------------------------------------------------------

        private void Scan() {
            _issues.Clear();
            _nonReadableRenderers.Clear();
            _scanSummary = "";
            if (_animator == null || !_animator.isHuman) return;

            var sideMap = new HumanoidSideMap(_animator);
            if (!sideMap.IsValid) {
                _scanSummary = "Animator has no usable Humanoid bindings (Hips missing).";
                return;
            }

            // If the user dropped a renderer into "Limit scan to", honour that
            // and skip everything else under the avatar. Otherwise walk every
            // SkinnedMeshRenderer in the hierarchy.
            SkinnedMeshRenderer[] renderers;
            if (_limitToRenderer != null) {
                renderers = new[] { _limitToRenderer };
            } else {
                renderers = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            }
            int verticesScanned = 0;
            int renderersScanned = 0;
            var globalLog = _verboseLog ? new StringBuilder() : null;
            globalLog?.AppendLine($"[Avatar QoL] Weight Sanity Check verbose log");
            globalLog?.AppendLine($"  weightFloor={_weightFloor:F4}, centerMargin={_centerMargin:F3}, scanCenterBand={_scanCenterBand}, centerCrossSideFloor={_centerCrossSideFloor:F3}");
            globalLog?.AppendLine($"  avatar={_animator.gameObject.name}, leftSign={sideMap.LeftSignInHipsLocal}");
            if (_limitToRenderer != null) {
                globalLog?.AppendLine($"  filter: limit-to-renderer={AvatarQol.GetGameObjectPath(_limitToRenderer.gameObject)}");
            }

            foreach (var r in renderers) {
                if (r == null || r.sharedMesh == null) continue;
                if (_excludedRenderers.Contains(r)) {
                    globalLog?.AppendLine($"  SKIP renderer (excluded): {AvatarQol.GetGameObjectPath(r.gameObject)}");
                    continue;
                }
                verticesScanned += ScanRenderer(r, sideMap, globalLog);
                renderersScanned++;
            }

            _issues.Sort((a, b) => {
                // RendererPath is cached at scan time, so this survives a
                // renderer being destroyed mid-comparison.
                int rcmp = string.Compare(a.RendererPath, b.RendererPath, System.StringComparison.Ordinal);
                if (rcmp != 0) return rcmp;
                return a.VertexIndex.CompareTo(b.VertexIndex);
            });

            _scanSummary = $"Scanned {verticesScanned} vertices across {renderersScanned} renderer(s); flagged {_issues.Count}.";
            if (globalLog != null) {
                globalLog.AppendLine();
                globalLog.AppendLine($"  total issues flagged: {_issues.Count}");
                Debug.Log(globalLog.ToString());
            }
            SceneView.RepaintAll();
        }

        private int ScanRenderer(SkinnedMeshRenderer renderer, HumanoidSideMap sideMap, StringBuilder log) {
            if (renderer == null || renderer.gameObject == null) return 0;
            var mesh = renderer.sharedMesh;
            if (mesh == null) return 0;
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) {
                log?.AppendLine($"  SKIP renderer (no bones array): {AvatarQol.GetGameObjectPath(renderer.gameObject)}");
                return 0;
            }
            if (!mesh.isReadable) {
                // Surface this in the SCAN summary too so the user notices when
                // many renderers are silently being skipped. Actionable
                // remediation lives in the "Enable Read/Write" UX next to the
                // scan output.
                log?.AppendLine($"  SKIP renderer (mesh not readable; enable Read/Write in the model importer): {AvatarQol.GetGameObjectPath(renderer.gameObject)}");
                _nonReadableRenderers.Add(renderer);
                return 0;
            }

            // Tag every bone the renderer references. Layered:
            //   1. Humanoid-ancestor side (most reliable).
            //   2. If Unknown, check the bone's own world position against
            //      the avatar's center axis — this catches custom prop /
            //      skirt-rig bones that are spatially on a side but have no
            //      Humanoid ancestor.
            var boneSides = new BoneSide[bones.Length];
            int countLeft = 0, countRight = 0, countCenter = 0, countUnknown = 0, countSpatial = 0;
            for (int i = 0; i < bones.Length; i++) {
                if (bones[i] == null) { boneSides[i] = BoneSide.Unknown; countUnknown++; continue; }
                var humanoidSide = sideMap.GetSide(bones[i]);
                if (humanoidSide != BoneSide.Unknown) {
                    boneSides[i] = humanoidSide;
                } else {
                    var spatial = sideMap.ClassifyWorldPosition(bones[i].position, _centerMargin);
                    boneSides[i] = spatial;
                    if (spatial == BoneSide.Left || spatial == BoneSide.Right) countSpatial++;
                }
                switch (boneSides[i]) {
                    case BoneSide.Left:    countLeft++;    break;
                    case BoneSide.Right:   countRight++;   break;
                    case BoneSide.Center:  countCenter++;  break;
                    case BoneSide.Unknown: countUnknown++; break;
                }
            }
            log?.AppendLine($"  RENDER {AvatarQol.GetGameObjectPath(renderer.gameObject)}: " +
                            $"{bones.Length} bones (L={countLeft} R={countRight} C={countCenter} U={countUnknown}; " +
                            $"{countSpatial} of those by spatial fallback)");
            if (countLeft == 0 || countRight == 0) {
                log?.AppendLine($"    EARLY-EXIT: no cross-side mismatch possible (need both Left and Right bones in the renderer's bones array).");
                return mesh.vertexCount;
            }

            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            // bindposes[i] transforms a mesh-local point into bone-i-local
            // coordinates AT BIND POSE. We use this plus bone.TransformPoint
            // (current pose, assumed ≈ bind pose) to derive each vertex's
            // world position correctly regardless of where the renderer's
            // GameObject sits in the hierarchy.
            var bindposes = mesh.bindposes;
            // Defensive: corrupt / re-imported meshes can have mismatched
            // sub-arrays. Bail rather than walk off the end.
            if (verts.Length != mesh.vertexCount || bonesPerVertex.Length != mesh.vertexCount) {
                log?.AppendLine($"    SKIP: vertex/bonesPerVertex length mismatch ({verts.Length}/{bonesPerVertex.Length} vs vertexCount={mesh.vertexCount}).");
                return 0;
            }
            if (bindposes == null || bindposes.Length < bones.Length) {
                log?.AppendLine($"    WARN: bindposes incomplete ({bindposes?.Length ?? 0} for {bones.Length} bones); falling back to renderer.transform for affected vertices.");
            }

            int weightCursor = 0;
            int sLeftVerts = 0, sRightVerts = 0, sCenterVerts = 0;
            int wSkippedFloor = 0, wSkippedCenter = 0, wSkippedUnknown = 0, wSkippedSameSide = 0;
            int wFlaggedHumanoid = 0, wFlaggedSpatial = 0, wFlaggedCenterBand = 0;

            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];

                // Bind-pose world position via the highest-weight bone.
                // The vertex's "anchor" bone is whichever bone influences it
                // most; we transform mesh-local → bone-local (bindpose) →
                // world (current bone transform). Equivalent to evaluating
                // the skin at bind pose for a single dominant bone, which
                // is plenty for side classification.
                int primaryIdx = -1;
                float primaryWeight = 0f;
                for (int w = 0; w < wCount; w++) {
                    var bw = weights[weightCursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bones[bw.boneIndex] == null) continue;
                    if (bw.weight > primaryWeight) {
                        primaryWeight = bw.weight;
                        primaryIdx = bw.boneIndex;
                    }
                }
                Vector3 worldPos;
                if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                    var meshLocal = verts[v];
                    var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                    worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                } else {
                    worldPos = renderer.transform.TransformPoint(verts[v]);
                }

                var vertexSide = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
                if (vertexSide == BoneSide.Left)        sLeftVerts++;
                else if (vertexSide == BoneSide.Right)  sRightVerts++;
                else                                     sCenterVerts++;

                bool isCenterVertex = vertexSide == BoneSide.Center;
                // Skip centre-band vertices entirely unless the user opted
                // in. They produce noise (legitimate spine/clavicle bleed)
                // that drowns the real Left/Right cross-side issues.
                if (isCenterVertex && !_scanCenterBand) {
                    weightCursor += wCount;
                    continue;
                }
                float vertexFloor = isCenterVertex ? _centerCrossSideFloor : _weightFloor;

                for (int w = 0; w < wCount; w++) {
                    var bw = weights[weightCursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bw.weight < vertexFloor) { wSkippedFloor++; continue; }
                    var bSide = boneSides[bw.boneIndex];
                    if (bSide == BoneSide.Unknown) { wSkippedUnknown++; continue; }
                    if (bSide == BoneSide.Center)  { wSkippedCenter++;  continue; }
                    // For Left/Right vertices: flag iff bone is the OPPOSITE side.
                    // For Center vertices: flag iff bone is Left OR Right (any side
                    // weight on a centerline vertex is suspicious if it survived
                    // the higher floor).
                    if (!isCenterVertex && bSide == vertexSide) { wSkippedSameSide++; continue; }

                    var bone = bones[bw.boneIndex];
                    if (bone == null) continue;
                    bool spatialClassification = sideMap.GetSide(bone) == BoneSide.Unknown;
                    IssueCategory category;
                    if (isCenterVertex) {
                        category = IssueCategory.CenterBandSideBleed;
                        wFlaggedCenterBand++;
                    } else if (spatialClassification) {
                        category = IssueCategory.SpatialCrossSide;
                        wFlaggedSpatial++;
                    } else {
                        category = IssueCategory.HumanoidCrossSide;
                        wFlaggedHumanoid++;
                    }
                    _issues.Add(new Issue {
                        Renderer       = renderer,
                        RendererPath   = AvatarQol.GetGameObjectPath(renderer.gameObject),
                        VertexIndex    = v,
                        WorldPosition  = worldPos,
                        VertexSide     = vertexSide,
                        OffendingBone  = bone,
                        BoneSide       = bSide,
                        Weight         = bw.weight,
                        Category       = category,
                    });
                }
                weightCursor += wCount;
            }
            log?.AppendLine($"    verts L={sLeftVerts} R={sRightVerts} C={sCenterVerts}");
            log?.AppendLine($"    weights skipped: floor={wSkippedFloor} center-bone={wSkippedCenter} unknown-bone={wSkippedUnknown} same-side={wSkippedSameSide}");
            log?.AppendLine($"    weights flagged: humanoid={wFlaggedHumanoid} spatial={wFlaggedSpatial} center-band={wFlaggedCenterBand}");
            return mesh.vertexCount;
        }

        // ------ Debug dump for a single renderer ---------------------------

        private void DumpSelectedRendererWeights() {
            var go = Selection.activeGameObject;
            if (go == null) {
                EditorUtility.DisplayDialog("Dump weights", "Select a SkinnedMeshRenderer in the hierarchy first.", "OK");
                return;
            }
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) {
                EditorUtility.DisplayDialog("Dump weights",
                    $"'{go.name}' has no SkinnedMeshRenderer. Select the actual renderer GameObject.", "OK");
                return;
            }
            if (_animator == null || !_animator.isHuman) {
                EditorUtility.DisplayDialog("Dump weights",
                    "Pick a Humanoid Animator at the top of the window first; we need it for side classification.", "OK");
                return;
            }
            var sideMap = new HumanoidSideMap(_animator);
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) {
                EditorUtility.DisplayDialog("Dump weights",
                    "The renderer's mesh is null or not readable. Enable Read/Write in the model importer if needed.", "OK");
                return;
            }

            var bones = smr.bones;
            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var sb = new StringBuilder();
            sb.AppendLine($"[Avatar QoL] Weight dump for {AvatarQol.GetGameObjectPath(smr.gameObject)}");
            sb.AppendLine($"  vertices={mesh.vertexCount}, bones={bones.Length}");

            // First, list every bone with its classification — handy when
            // tracking down "why didn't a custom bone get flagged?"
            sb.AppendLine("  bones:");
            for (int b = 0; b < bones.Length; b++) {
                if (bones[b] == null) { sb.AppendLine($"    [{b}] (null)"); continue; }
                var humanoid = sideMap.GetSide(bones[b]);
                var spatial  = sideMap.ClassifyWorldPosition(bones[b].position, _centerMargin);
                sb.AppendLine($"    [{b}] {bones[b].name}  humanoid={humanoid}  spatial={spatial}");
            }

            // Then dump every vertex with its top weights. Limited to the
            // first 200 vertices per renderer to keep the log readable —
            // bump if needed for specific debugging.
            int limit = Mathf.Min(mesh.vertexCount, 200);
            sb.AppendLine($"  first {limit} vertices:");
            int cursor = 0;
            // Same bindpose-based world position as Scan: pick the highest-
            // weight bone, transform mesh-local → bone-local via bindpose,
            // bone-local → world via the bone's current transform.
            var bindposes = mesh.bindposes;
            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];
                if (v < limit) {
                    int primaryIdx = -1;
                    float primaryWeight = 0f;
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[cursor + w];
                        if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                        if (bones[bw.boneIndex] == null) continue;
                        if (bw.weight > primaryWeight) { primaryWeight = bw.weight; primaryIdx = bw.boneIndex; }
                    }
                    Vector3 worldPos;
                    if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                        var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(verts[v]);
                        worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                    } else {
                        worldPos = smr.transform.TransformPoint(verts[v]);
                    }
                    var side = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
                    sb.Append($"    v#{v} on {side} ({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3}): ");
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[cursor + w];
                        var name = bw.boneIndex < bones.Length && bones[bw.boneIndex] != null
                            ? bones[bw.boneIndex].name : "?";
                        sb.Append($"{name}={bw.weight:F3} ");
                    }
                    sb.AppendLine();
                }
                cursor += wCount;
            }
            Debug.Log(sb.ToString());
            _showConsoleNoticeAfterDump = true;
        }

        // ------ Preview ----------------------------------------------------

        private void StartPreview(Transform bone) {
            if (bone == null) return;
            if (_previewBone == bone) return;
            // Restore any prior preview before starting a new one.
            StopPreview();
            _previewBone = bone;
            _previewRestRotation = bone.localRotation;
            _previewStart = EditorApplication.timeSinceStartup;
            // Mark the bone undo-recorded so accidental scene save doesn't
            // immortalise the wobbled rotation. We also restore on stop.
            Undo.RegisterCompleteObjectUndo(bone, "Avatar QoL preview");
        }

        private void StopPreview() {
            // The Unity-fake-null check is the right read here: if the bone
            // was destroyed, we can't restore it, but we should still clear
            // our reference so we don't keep wobbling against a dead object
            // every editor update.
            if (_previewBone == null) { _previewBone = null; return; }
            _previewBone.localRotation = _previewRestRotation;
            _previewBone = null;
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate() {
            if (_previewBone == null) {
                // Bone may have been destroyed — drop our reference so the
                // next OnGUI doesn't try to draw a Stop button against it.
                _previewBone = null;
                return;
            }
            // Wobble around the bone's primary swing axes. Most rigs deform
            // legibly when rotated around their local X (forward bend) and
            // local Z (side splay). We combine the two so the mesh moves
            // visibly even when the rig's primary axis happens to be one or
            // the other.
            float t = (float)(EditorApplication.timeSinceStartup - _previewStart);
            float angle = Mathf.Sin(t * Mathf.PI) * 30f;
            float zAngle = Mathf.Cos(t * Mathf.PI) * 18f;
            _previewBone.localRotation = _previewRestRotation * Quaternion.Euler(angle, 0f, zAngle);
            SceneView.RepaintAll();
        }

        // ------ Fix --------------------------------------------------------

        private void FixIssues(List<Issue> issues, string description) {
            if (issues == null || issues.Count == 0) return;
            // Single confirmation up-front; the apply runs in a single Undo
            // group so Ctrl+Z reverts everything in one step.
            var rendererCount = new HashSet<SkinnedMeshRenderer>();
            foreach (var i in issues) if (i.Renderer != null) rendererCount.Add(i.Renderer);
            string msg = $"Fix {description} across {rendererCount.Count} renderer(s)?\n\n" +
                         "Each offending weight will be redirected to its Humanoid mirror bone " +
                         "(e.g. RightUpperLeg → LeftUpperLeg). Weights with no mirror are zeroed " +
                         "and the remaining weights on the same vertex are scaled up.\n\n" +
                         "FBX-imported meshes will be cloned to editable .mesh assets in " +
                         $"{WeightFixer.GeneratedFolder}/ — the original FBX is never modified.\n\n" +
                         "Ctrl+Z reverts the operation.";
            if (!EditorUtility.DisplayDialog("Fix weight contamination", msg, "Fix", "Cancel")) return;

            // Translate UI Issue → fixer IssueRef (avoids leaking UI types
            // into the fixer module).
            var refs = new List<WeightFixer.IssueRef>(issues.Count);
            foreach (var i in issues) {
                refs.Add(new WeightFixer.IssueRef {
                    Renderer       = i.Renderer,
                    VertexIndex    = i.VertexIndex,
                    OffendingBone  = i.OffendingBone,
                    Weight         = i.Weight,
                });
            }
            var result = WeightFixer.ApplyFixes(refs, _animator);

            // Drop the just-fixed issues from the visible list and refresh
            // the gizmo overlay. We don't auto-rescan: the user usually
            // wants to compare before/after themselves, and Ctrl+Z is more
            // useful when the issue list still shows what was done.
            var fixedSet = new HashSet<Issue>(issues);
            _issues.RemoveAll(fixedSet.Contains);
            SceneView.RepaintAll();

            string clonedNote = result.MeshesCloned > 0
                ? $" Cloned {result.MeshesCloned} mesh(es) to {WeightFixer.GeneratedFolder}/."
                : "";
            string skipNote = result.Skipped > 0
                ? $" Skipped {result.Skipped} (weight no longer present)."
                : "";
            Debug.Log(
                $"[Avatar QoL] Weight fix: {result.Fixed} weight(s) corrected — " +
                $"{result.Mirrored} mirrored, {result.Zeroed} zeroed + renormalised, " +
                $"across {result.RenderersTouched} renderer(s).{clonedNote}{skipNote}");
            AssetDatabase.SaveAssets();
        }

        // ------ Scene view gizmos -----------------------------------------

        private void OnSceneGui(SceneView sceneView) {
            // Reveal flash — fades out by alpha over the 2s window.
            if (_flashUntil > EditorApplication.timeSinceStartup) {
                float remaining = (float)(_flashUntil - EditorApplication.timeSinceStartup) / 2.0f;
                var prev = Handles.color;
                Handles.color = new Color(1f, 0.85f, 0.20f, Mathf.Clamp01(remaining));
                var size = HandleUtility.GetHandleSize(_flashPos) * 0.18f;
                Handles.DrawWireDisc(_flashPos, sceneView.camera.transform.forward, size);
                Handles.DrawWireDisc(_flashPos, sceneView.camera.transform.forward, size * 0.6f);
                Handles.color = prev;
                sceneView.Repaint();
            }
            if (!_showGizmos || _issues.Count == 0) return;
            var prevColor = Handles.color;
            Handles.color = new Color(1f, 0.25f, 0.25f, 0.95f);
            foreach (var i in _issues) {
                if (i.Renderer == null) continue;
                var size = HandleUtility.GetHandleSize(i.WorldPosition) * 0.04f;
                Handles.SphereHandleCap(0, i.WorldPosition, Quaternion.identity, size, EventType.Repaint);
            }
            Handles.color = prevColor;
        }

        // ------ Records ----------------------------------------------------

        private enum IssueCategory {
            HumanoidCrossSide,    // bone has Humanoid ancestor on opposite side
            SpatialCrossSide,     // bone has no Humanoid ancestor; pivot on opposite side
            CenterBandSideBleed,  // vertex is in the centre stripe; bone is Left or Right above the higher centre floor
        }

        private sealed class Issue {
            public SkinnedMeshRenderer Renderer;
            // Cached at scan-time so OnGUI doesn't have to re-walk Transforms
            // (and so a destroyed-after-scan renderer doesn't NRE the header).
            public string RendererPath;
            public int VertexIndex;
            public Vector3 WorldPosition;
            public BoneSide VertexSide;
            public Transform OffendingBone;
            public BoneSide BoneSide;
            public float Weight;
            public IssueCategory Category;
        }
    }
}
