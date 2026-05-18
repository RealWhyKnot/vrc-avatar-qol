// PhysBoneClippingRiskWindow.cs
//
// Standalone UI for the conservative PhysBone clipping-risk estimate. It is
// scoped to one SkinnedMeshRenderer at a time so the regular Weight Sanity
// Check stays fast and this heavier scan is explicit.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.UI;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class PhysBoneClippingRiskWindow : EditorWindow {

        [SerializeField] private Animator _animator;
        [SerializeField] private SkinnedMeshRenderer _targetRenderer;
        [SerializeField] private List<SkinnedMeshRenderer> _comparisonRenderers =
            new List<SkinnedMeshRenderer>();
        [SerializeField] private bool _showGizmos = true;
        [SerializeField] private bool _verboseLog;
        [SerializeField] private float _weightFloor = 0.03f;
        [SerializeField] private float _clearanceMargin = 0.025f;
        [SerializeField] private int _maxIssuesPerPhysBone = 8;

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#physbone-clipping-risks";

        private readonly List<PhysBoneClippingAnalyzer.Issue> _issues =
            new List<PhysBoneClippingAnalyzer.Issue>();
        private Vector2 _scroll;
        private string _scanSummary = "";
        private int _lastSurfaceRendererCount;

        private Transform _previewBone;
        private Quaternion _previewRestRotation;
        private double _previewStart;

        private Vector3 _flashPos;
        private double _flashUntil;

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<PhysBoneClippingRiskWindow>(false, "PhysBone Clipping Risks", true);
            w.titleContent = new GUIContent("Avatar QoL - PhysBone Clipping Risks");
            w.minSize = new Vector2(620, 460);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

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

        private void OnGUI() {
            DrawTitleBar();
            AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Info,
                "This is a heavier physics-risk scan, so it checks one mesh at a time. Pick the mesh that actually moves from PhysBones, then scan.");
            DrawSetup();
            EditorGUILayout.Space(2);
            DrawTuning();
            EditorGUILayout.Space(2);
            DrawScanBar();
            EditorGUILayout.Space(2);
            DrawIssues();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("PhysBone Clipping Risks",
                        "Estimate whether one PhysBone-driven mesh has enough motion range to clip into itself or nearby surfaces."),
                    AvatarQolStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawSetup() {
            using (AvatarQolStyles.Section("1. Moving mesh",
                    "Pick the mesh that moves from PhysBones. The scan checks that mesh against itself and against any comparison meshes you add below.")) {
                AvatarQolStyles.LabeledField(
                    new GUIContent("Animator",
                        "The avatar Animator. The scan looks under this object for live VRCPhysBones and supported generated/custom PhysBone setup components."),
                    () => {
                        var next = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (next != _animator) {
                            _animator = next;
                            ClearResults();
                        }
                    });
                AvatarQolStyles.LabeledField(
                    new GUIContent("Mesh to check",
                        "The one SkinnedMeshRenderer to scan for PhysBone-driven vertices. Pick hair, tail, skirt, sleeves, or another mesh that moves from PhysBones."),
                    () => {
                        var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_targetRenderer, typeof(SkinnedMeshRenderer), true);
                        if (next != _targetRenderer) {
                            _targetRenderer = next;
                            if (_targetRenderer != null && _animator == null) {
                                _animator = _targetRenderer.GetComponentInParent<Animator>(true);
                            }
                            ClearResults();
                        }
                    });

                if (!PhysBoneClippingAnalyzer.SdkAvailable) {
                    AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                        "VRChat SDK 3 PhysBone types are not available in this project, so this scan cannot run.");
                } else if (_targetRenderer != null && (_targetRenderer.sharedMesh == null || !_targetRenderer.sharedMesh.isReadable)) {
                    AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                        "The target mesh is not readable. Enable Read/Write on its model importer before scanning.");
                } else if (_animator != null && _targetRenderer != null && !_targetRenderer.transform.IsChildOf(_animator.transform)) {
                    AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                        "The target mesh is not under the selected Animator. The scan can still run, but PhysBone ownership may not match what you expect.");
                }
            }
        }

        private void DrawTuning() {
            using (AvatarQolStyles.Section("2. Comparison meshes",
                    "Add body, clothing, accessories, or any other readable SkinnedMeshRenderer that the moving mesh should not pass through.")) {
                EditorGUILayout.LabelField(
                    "The moving mesh is always included, so self-clipping is checked even when this list is empty.",
                    AvatarQolStyles.Muted);
                DrawComparisonRendererList();
            }

            EditorGUILayout.Space(2);
            using (AvatarQolStyles.Section("3. Scan options",
                    "Defaults are tuned for a quick first pass. Raise the margin or lower the weight floor if you want a more sensitive scan.")) {
                _verboseLog = EditorGUILayout.ToggleLeft(
                    new GUIContent("Verbose log",
                        "Print scan counts and timing to the Console. Useful when performance is still too slow on a large mesh."),
                    _verboseLog);
                _showGizmos = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show gizmos in Scene view",
                        "Draw orange markers on risky vertices and a line to the nearest surface sample."),
                    _showGizmos);

                AvatarQolStyles.LabeledField(
                    new GUIContent("Driven weight floor",
                        "A vertex must have at least this much weight to a PhysBone-driven transform before it is considered part of the moving surface."),
                    () => _weightFloor = EditorGUILayout.Slider(_weightFloor, 0.001f, 0.5f));
                AvatarQolStyles.LabeledField(
                    new GUIContent("Clearance margin",
                        "How much empty space nearby mesh should have before the motion envelope is considered risky. 0.025 m is 2.5 cm."),
                    () => _clearanceMargin = EditorGUILayout.Slider(_clearanceMargin, 0.005f, 0.15f));
                AvatarQolStyles.LabeledField(
                    new GUIContent("Max rows per PhysBone",
                        "Caps repeated warnings from one PhysBone so one skirt or hair chain does not flood the list."),
                    () => _maxIssuesPerPhysBone = EditorGUILayout.IntSlider(_maxIssuesPerPhysBone, 1, 25));
            }
        }

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanScan())) {
                    if (AvatarQolStyles.PrimaryButtonInline(
                            new GUIContent("Scan for clipping",
                                "Run the PhysBone clipping-risk estimate for the moving mesh and the comparison meshes listed above."),
                            GUILayout.MinWidth(140))) {
                        Scan();
                    }
                }
                using (new EditorGUI.DisabledScope(!CanCreateMeshFixAny())) {
                    if (GUILayout.Button(
                            new GUIContent("Create mesh fixes",
                                "Create or update stored Auto Mesh Fix components on the affected moving mesh objects, then open Auto Mesh Fixes so you can review them."),
                            GUILayout.Height(28), GUILayout.Width(132))) {
                        CreateMeshFixSetups(_issues);
                    }
                }
                using (new EditorGUI.DisabledScope(!CanReduceMotionAny())) {
                    if (GUILayout.Button(
                            new GUIContent("Reduce motion",
                                "Immediate fallback: tighten the PhysBone or supported authoring component settings. This is not the component-backed mesh fix."),
                            GUILayout.Height(28), GUILayout.Width(108))) {
                        ReduceMotion(_issues);
                    }
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop wobble",
                                "Restore the currently-wobbled bone to its rest rotation."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        StopPreview();
                    }
                }
                using (new EditorGUI.DisabledScope(_issues.Count == 0 && string.IsNullOrEmpty(_scanSummary))) {
                    if (GUILayout.Button(
                            new GUIContent("Clear",
                                "Drop the current risk list and clear Scene view markers."),
                            GUILayout.Height(28), GUILayout.Width(70))) {
                        ClearResults();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_scanSummary)) {
                    EditorGUILayout.LabelField(_scanSummary, AvatarQolStyles.Muted);
                }
            }
        }

        private void DrawIssues() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent(_issues.Count > 0 ? $"Risks ({_issues.Count})" : "Risks",
                        "Rows where the estimated PhysBone motion envelope reaches nearby mesh surface."),
                    AvatarQolStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                if (_lastSurfaceRendererCount > 1) {
                    EditorGUILayout.LabelField($"{_lastSurfaceRendererCount} surface meshes sampled", AvatarQolStyles.Muted, GUILayout.Width(170));
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick one moving mesh, add any comparison meshes, then scan." : "No likely PhysBone clipping risks found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    foreach (var issue in _issues) {
                        DrawIssueRow(issue);
                        AvatarQolStyles.Divider();
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawIssueRow(PhysBoneClippingAnalyzer.Issue issue) {
            var severityColor = issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                ? AvatarQolStyles.CategoryHumanoid
                : AvatarQolStyles.ColorWarning;
            var severityText = issue.Severity == PhysBoneClippingAnalyzer.Severity.High ? "high" : "medium";
            string boneName = issue.DrivenBone != null ? issue.DrivenBone.name : "(destroyed)";
            using (new EditorGUILayout.HorizontalScope()) {
                AvatarQolStyles.BadgePill(severityText, severityColor,
                    issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                        ? "No effective collider coverage or already-small clearance. This deserves attention."
                        : "Collider coverage exists or the estimated overlap is smaller, but the area is still worth checking.");
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"v#{issue.VertexIndex}  {boneName}  move~{issue.EstimatedMotion * 100f:0.0}cm  clearance {issue.Clearance * 100f:0.0}cm",
                        issue.Reason),
                    AvatarQolStyles.Mono);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!CanCreateMeshFix(issue))) {
                    if (GUILayout.Button(
                            new GUIContent("Mesh fix",
                                "Create or update a stored Auto Mesh Fix component on the moving mesh object for this risk."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(66))) {
                        CreateMeshFixSetups(new[] { issue });
                    }
                }
                using (new EditorGUI.DisabledScope(!PhysBoneClippingAnalyzer.CanReduceMotion(issue))) {
                    if (GUILayout.Button(
                            new GUIContent("Motion",
                                "Immediate fallback: tighten this PhysBone source's motion settings. This does not create the stored mesh-fix component."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(58))) {
                        ReduceMotion(new[] { issue });
                    }
                }
                using (new EditorGUI.DisabledScope(issue.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("Ping", "Ping the target renderer in the hierarchy."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(42))) {
                        Selection.activeObject = issue.Renderer;
                        EditorGUIUtility.PingObject(issue.Renderer);
                    }
                }
                if (GUILayout.Button(new GUIContent("Frame", "Frame the risky vertex in Scene view."),
                        AvatarQolStyles.MiniRowButton, GUILayout.Width(48))) {
                    Frame(issue.WorldPosition, 0.18f);
                }
                using (new EditorGUI.DisabledScope(issue.DrivenBone == null)) {
                    if (GUILayout.Button(new GUIContent("Reveal", "Select and ping the PhysBone-driven transform."),
                            AvatarQolStyles.MiniRowButton, GUILayout.Width(52))) {
                        Selection.activeObject = issue.DrivenBone;
                        EditorGUIUtility.PingObject(issue.DrivenBone);
                        FlashHighlight(issue.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == issue.DrivenBone && issue.DrivenBone != null;
                    if (GUILayout.Button(
                            new GUIContent(isPreviewing ? "Stop" : "Wobble",
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

        private bool CanScan() {
            return PhysBoneClippingAnalyzer.SdkAvailable &&
                   _animator != null &&
                   _targetRenderer != null &&
                   _targetRenderer.sharedMesh != null &&
                   _targetRenderer.sharedMesh.isReadable;
        }

        private bool CanReduceMotionAny() {
            return _issues.Any(PhysBoneClippingAnalyzer.CanReduceMotion);
        }

        private bool CanCreateMeshFixAny() {
            return _issues.Any(CanCreateMeshFix);
        }

        private static bool CanCreateMeshFix(PhysBoneClippingAnalyzer.Issue issue) {
            return issue != null &&
                   issue.Renderer != null &&
                   issue.NearestSurfaceRenderer != null &&
                   issue.NearestSurfaceRenderer != issue.Renderer;
        }

        private void Scan() {
            _issues.Clear();
            _scanSummary = "";
            if (!CanScan()) return;

            var surfaces = BuildSurfaceList();
            _lastSurfaceRendererCount = surfaces.Count;
            var log = _verboseLog ? new StringBuilder() : null;
            log?.AppendLine("[Avatar QoL] PhysBone Clipping Risks verbose log");
            log?.AppendLine($"  target={AvatarQol.GetGameObjectPath(_targetRenderer.gameObject)}");
            log?.AppendLine($"  comparisonRenderers={surfaces.Count - 1}, surfaceRenderers={surfaces.Count}");
            log?.AppendLine($"  weightFloor={_weightFloor:F4}, clearanceMargin={_clearanceMargin:F3}, maxIssuesPerPhysBone={_maxIssuesPerPhysBone}");

            double start = EditorApplication.timeSinceStartup;
            var settings = new PhysBoneClippingAnalyzer.Settings {
                WeightFloor = _weightFloor,
                ClearanceMargin = _clearanceMargin,
                MaxIssuesPerPhysBone = _maxIssuesPerPhysBone,
            };
            _issues.AddRange(PhysBoneClippingAnalyzer.ScanOneMesh(
                _animator,
                _targetRenderer,
                surfaces,
                settings,
                log));
            double elapsed = EditorApplication.timeSinceStartup - start;

            int totalSources = settings.NativePhysBoneCount + settings.CustomPhysBoneCount;
            string sourceText = settings.CustomPhysBoneCount > 0
                ? $"{totalSources} PhysBone sources ({settings.CustomPhysBoneCount} generated/custom)"
                : $"{totalSources} PhysBone sources";
            string surfaceText = surfaces.Count == 1 ? "self only" : $"{surfaces.Count} surface meshes";
            _scanSummary = $"Scanned 1 mesh vs {surfaceText} in {elapsed:0.0}s; {sourceText}; found {_issues.Count} risk(s).";
            if (log != null) {
                log.AppendLine($"  elapsed={elapsed:0.000}s");
                log.AppendLine($"  livePhysBones={settings.NativePhysBoneCount}");
                log.AppendLine($"  generatedOrCustomPhysBones={settings.CustomPhysBoneCount}");
                log.AppendLine($"  drivenVertexSamples={settings.DrivenVertexSampleCount}");
                log.AppendLine($"  surfaceSamples={settings.SurfaceSampleCount}");
                log.AppendLine($"  risks={_issues.Count}");
                Debug.Log(log.ToString());
            }
            SceneView.RepaintAll();
        }

        private List<SkinnedMeshRenderer> BuildSurfaceList() {
            var list = new List<SkinnedMeshRenderer> { _targetRenderer };
            foreach (var renderer in _comparisonRenderers) {
                if (renderer == null || renderer.sharedMesh == null || !renderer.sharedMesh.isReadable) continue;
                if (!list.Contains(renderer)) list.Add(renderer);
            }
            return list;
        }

        private void DrawComparisonRendererList() {
            if (_comparisonRenderers == null) _comparisonRenderers = new List<SkinnedMeshRenderer>();

            int unreadableCount = 0;
            for (int i = 0; i < _comparisonRenderers.Count; i++) {
                using (new EditorGUILayout.HorizontalScope()) {
                    var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                        new GUIContent($"Mesh {i + 1}",
                            "A mesh to use as nearby surface, such as body, clothing, hair, accessories, or another area you want checked."),
                        _comparisonRenderers[i],
                        typeof(SkinnedMeshRenderer),
                        true);
                    if (next != _comparisonRenderers[i]) {
                        _comparisonRenderers[i] = next;
                        ClearResults();
                    }
                    if (GUILayout.Button(
                            new GUIContent("Remove", "Remove this comparison mesh from the scan."),
                            EditorStyles.miniButton, GUILayout.Width(62))) {
                        _comparisonRenderers.RemoveAt(i);
                        ClearResults();
                        i--;
                        continue;
                    }
                }

                var renderer = i >= 0 && i < _comparisonRenderers.Count ? _comparisonRenderers[i] : null;
                if (renderer != null && (renderer.sharedMesh == null || !renderer.sharedMesh.isReadable)) {
                    unreadableCount++;
                }
            }

            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(
                        new GUIContent("Add comparison mesh",
                            "Add another empty slot where you can drag a body, clothing, accessory, or any other SkinnedMeshRenderer."),
                        GUILayout.Width(160))) {
                    _comparisonRenderers.Add(null);
                    ClearResults();
                }
                using (new EditorGUI.DisabledScope(_comparisonRenderers.Count == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Clear list", "Remove every comparison mesh."),
                            GUILayout.Width(80))) {
                        _comparisonRenderers.Clear();
                        ClearResults();
                    }
                }
                GUILayout.FlexibleSpace();
            }

            if (_comparisonRenderers.Count == 0) {
                EditorGUILayout.LabelField("No extra comparison meshes. The scan will check the moving mesh against itself.", AvatarQolStyles.Muted);
            }
            if (unreadableCount > 0) {
                AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Warning,
                    $"{unreadableCount} comparison mesh(es) are not readable and will be skipped. Enable Read/Write on those model imports to include them.");
            }
        }

        private void CreateMeshFixSetups(IEnumerable<PhysBoneClippingAnalyzer.Issue> issues) {
            var list = issues == null ? new List<PhysBoneClippingAnalyzer.Issue>() : issues.Where(i => i != null).ToList();
            if (list.Count == 0) return;

            var candidates = list.Where(CanCreateMeshFix)
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.Score)
                .ToList();
            if (candidates.Count == 0) {
                EditorUtility.DisplayDialog(
                    "Create Mesh Fix",
                    "These risks are self-clipping or do not have a separate comparison mesh target. Add a body/clothing/accessory comparison mesh, scan again, then use Create mesh fixes to create a stored mesh-fix setup.",
                    "OK");
                return;
            }

            // Detect renderers that already have a setup so we can ask the
            // user once what to do (Keep / Merge / Overwrite) instead of
            // silently mutating their manually-tuned values.
            var perRendererBest = new List<PhysBoneClippingAnalyzer.Issue>();
            foreach (var rendererGroup in candidates.GroupBy(i => i.Renderer)) {
                var best = rendererGroup
                    .OrderByDescending(i => i.Severity)
                    .ThenByDescending(i => i.Score)
                    .FirstOrDefault();
                if (best == null || best.Renderer == null || best.NearestSurfaceRenderer == null) continue;
                perRendererBest.Add(best);
            }

            int existingCount = perRendererBest.Count(b => b.Renderer.GetComponent<AutoTightenToBody>() != null);
            ExistingSetupAction existingAction = ExistingSetupAction.Merge;
            if (existingCount > 0) {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Avatar QoL - Existing Auto Mesh Fix",
                    $"{existingCount} renderer(s) already have an Auto Tighten To Body setup. What should I do for those?\n\n" +
                    "Merge (recommended): widen each numeric parameter to the larger of (current, suggested). Keeps your manual tuning unless the clipping analysis needs more room.\n\n" +
                    "Overwrite: replace every numeric parameter with the suggested value. Your manual tuning is lost.\n\n" +
                    "Keep: leave existing setups untouched; only fresh setups are created where there is none yet.",
                    "Merge",     // 0
                    "Keep",      // 1 (cancel)
                    "Overwrite"  // 2 (alt)
                );
                switch (choice) {
                    case 0: existingAction = ExistingSetupAction.Merge; break;
                    case 1: existingAction = ExistingSetupAction.Keep; break;
                    case 2: existingAction = ExistingSetupAction.Overwrite; break;
                }
            }

            Undo.SetCurrentGroupName("Avatar QoL: create PhysBone clipping mesh fix");
            int undoGroup = Undo.GetCurrentGroup();

            int created = 0;
            int updated = 0;
            int kept = 0;
            int skipped = list.Count - candidates.Count;
            AutoTightenToBody lastSetup = null;
            foreach (var best in perRendererBest) {
                var setup = best.Renderer.GetComponent<AutoTightenToBody>();
                if (setup == null) {
                    setup = Undo.AddComponent<AutoTightenToBody>(best.Renderer.gameObject);
                    ConfigureMeshFixSetup(setup, best, ExistingSetupAction.Overwrite);
                    EditorUtility.SetDirty(setup);
                    created++;
                    lastSetup = setup;
                } else if (existingAction == ExistingSetupAction.Keep) {
                    kept++;
                    if (lastSetup == null) lastSetup = setup;
                } else {
                    Undo.RecordObject(setup, "Update Avatar QoL clipping mesh fix");
                    ConfigureMeshFixSetup(setup, best, existingAction);
                    EditorUtility.SetDirty(setup);
                    updated++;
                    lastSetup = setup;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            if (lastSetup != null) {
                Selection.activeObject = lastSetup.gameObject;
                EditorGUIUtility.PingObject(lastSetup.gameObject);
                MeshFixWindow.Open(lastSetup);
                _scanSummary =
                    $"Stored mesh fix setup(s): {created} created, {updated} updated, {kept} kept. " +
                    $"Selected {AvatarQol.GetGameObjectPath(lastSetup.gameObject)}.";
                if (skipped > 0) _scanSummary += $" Skipped {skipped} self-clipping row(s).";
            } else {
                _scanSummary = "No Auto Mesh Fix setup could be created from the selected risk rows.";
            }

            Repaint();
        }

        private enum ExistingSetupAction { Keep, Merge, Overwrite }

        private static void ConfigureMeshFixSetup(
            AutoTightenToBody setup,
            PhysBoneClippingAnalyzer.Issue issue,
            ExistingSetupAction mode) {
            // On Overwrite the renderer refs / names / toggles are always
            // rewritten. On Merge they are only set if currently null/empty
            // so the user's manual choices survive.
            bool overwrite = mode == ExistingSetupAction.Overwrite;

            if (overwrite || setup.garmentRenderer == null) setup.garmentRenderer = issue.Renderer;
            if (overwrite || setup.bodyRenderer == null) setup.bodyRenderer = issue.NearestSurfaceRenderer;
            if (overwrite || string.IsNullOrWhiteSpace(setup.garmentTightenBlendShapeName)) {
                setup.garmentTightenBlendShapeName = $"AUTO_Tighten_{issue.Renderer.name}";
            }
            if (overwrite || string.IsNullOrWhiteSpace(setup.bodyHideBlendShapeName)) {
                setup.bodyHideBlendShapeName = $"AUTO_HideBody_{issue.Renderer.name}";
            }
            if (overwrite) {
                setup.createGarmentTightenShape = true;
                setup.setGarmentTightenWeightTo100 = true;
                bool bodyLikeTarget = LooksLikeBodyRenderer(issue.NearestSurfaceRenderer);
                setup.createBodyHideShape = bodyLikeTarget;
                setup.setBodyHideWeightTo100 = bodyLikeTarget;
            }

            float projectedRange = issue.EstimatedMotion + issue.Clearance + 0.015f;
            if (overwrite) {
                setup.maxProjectionDistance = Mathf.Clamp(projectedRange, 0.01f, 0.2f);
                setup.bodyHideRadius = Mathf.Clamp(issue.Clearance + 0.01f, 0.005f, 0.12f);
                setup.bodyHideDepth = Mathf.Clamp(issue.EstimatedMotion + 0.02f, 0.01f, 0.2f);
            } else {
                // Merge: widen to cover what the analysis says is needed, but
                // never shrink the user's existing dial. This was the previous
                // behavior; preserved here so default-button-press matches it.
                setup.maxProjectionDistance = Mathf.Clamp(Mathf.Max(setup.maxProjectionDistance, projectedRange), 0.01f, 0.2f);
                setup.bodyHideRadius = Mathf.Clamp(Mathf.Max(setup.bodyHideRadius, issue.Clearance + 0.01f), 0.005f, 0.12f);
                setup.bodyHideDepth = Mathf.Clamp(Mathf.Max(setup.bodyHideDepth, issue.EstimatedMotion + 0.02f), 0.01f, 0.2f);
            }
        }

        private static bool LooksLikeBodyRenderer(SkinnedMeshRenderer renderer) {
            if (renderer == null) return false;
            var text = (AvatarQol.GetGameObjectPath(renderer.gameObject) + " " +
                        renderer.name + " " +
                        (renderer.sharedMesh != null ? renderer.sharedMesh.name : string.Empty)).ToLowerInvariant();
            return text.Contains("body") ||
                   text.Contains("basebody") ||
                   text.Contains("base body") ||
                   text.Contains("skin") ||
                   text.Contains("torso");
        }

        private void ReduceMotion(IEnumerable<PhysBoneClippingAnalyzer.Issue> issues) {
            var list = issues == null ? new List<PhysBoneClippingAnalyzer.Issue>() : issues.Where(i => i != null).ToList();
            if (list.Count == 0) return;

            var log = _verboseLog ? new StringBuilder() : null;
            log?.AppendLine("[Avatar QoL] PhysBone Clipping Risks motion reduction");
            var result = PhysBoneClippingAnalyzer.ReduceMotionIssues(list, log);
            _scanSummary = result.SourcesChanged > 0
                ? $"{result.Summary} Scan again to verify."
                : result.Summary;
            if (result.UnsupportedSources > 0) {
                _scanSummary += $" {result.UnsupportedSources} source(s) were not supported.";
            }
            if (log != null) {
                log.AppendLine($"  sourcesChanged={result.SourcesChanged}");
                log.AppendLine($"  issuesCovered={result.IssuesCovered}");
                log.AppendLine($"  unsupportedSources={result.UnsupportedSources}");
                Debug.Log(log.ToString());
            }
            SceneView.RepaintAll();
            Repaint();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var renderer = go.GetComponent<SkinnedMeshRenderer>() ?? go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var animator = go.GetComponent<Animator>() ??
                           go.GetComponentInParent<Animator>(true) ??
                           go.GetComponentInChildren<Animator>(true);
            if (renderer != null) _targetRenderer = renderer;
            if (animator == null && renderer != null) animator = renderer.GetComponentInParent<Animator>(true);
            if (animator != null) _animator = animator;
            ClearResults();
        }

        private void ClearResults() {
            _issues.Clear();
            _scanSummary = "";
            _lastSurfaceRendererCount = 0;
            SceneView.RepaintAll();
        }

        private static void Frame(Vector3 worldPosition, float size) {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.LookAt(worldPosition, sv.rotation, size);
            sv.Repaint();
        }

        private void FlashHighlight(Vector3 worldPos) {
            _flashPos = worldPos;
            _flashUntil = EditorApplication.timeSinceStartup + 2.0;
            SceneView.RepaintAll();
        }

        private void StartPreview(Transform bone) {
            if (bone == null) return;
            if (_previewBone == bone) return;
            StopPreview();
            _previewBone = bone;
            _previewRestRotation = bone.localRotation;
            _previewStart = EditorApplication.timeSinceStartup;
            Undo.RegisterCompleteObjectUndo(bone, "Avatar QoL PhysBone preview");
        }

        private void StopPreview() {
            if (_previewBone == null) {
                _previewBone = null;
                return;
            }
            _previewBone.localRotation = _previewRestRotation;
            _previewBone = null;
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate() {
            if (_previewBone == null) {
                _previewBone = null;
                return;
            }
            float t = (float)(EditorApplication.timeSinceStartup - _previewStart);
            float angle = Mathf.Sin(t * Mathf.PI) * 30f;
            float zAngle = Mathf.Cos(t * Mathf.PI) * 18f;
            _previewBone.localRotation = _previewRestRotation * Quaternion.Euler(angle, 0f, zAngle);
            SceneView.RepaintAll();
        }

        private void OnSceneGui(SceneView sceneView) {
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
            Handles.color = new Color(1f, 0.62f, 0.15f, 0.95f);
            foreach (var issue in _issues) {
                if (issue.Renderer == null) continue;
                var size = HandleUtility.GetHandleSize(issue.WorldPosition) * 0.055f;
                Handles.SphereHandleCap(0, issue.WorldPosition, Quaternion.identity, size, EventType.Repaint);
                Handles.DrawLine(issue.WorldPosition, issue.NearestSurfacePosition);
            }
            Handles.color = prevColor;
        }
    }
}
