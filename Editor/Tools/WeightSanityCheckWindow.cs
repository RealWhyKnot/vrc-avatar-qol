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
// Preview / debug:
//   - Per-issue *Preview* button: rotates the offending bone back and forth
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
            DrawHeader();
            EditorGUILayout.Space(2);
            DrawTunables();
            EditorGUILayout.Space(2);
            DrawExclusions();
            EditorGUILayout.Space(4);
            DrawScanBar();
            DrawDivider();
            DrawIssues();
            DrawDivider();
            DrawDebugBar();
        }

        private void DrawHeader() {
            EditorGUILayout.LabelField("Avatar (Humanoid Animator)", EditorStyles.boldLabel);
            var newAnim = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
            if (newAnim != _animator) { _animator = newAnim; _issues.Clear(); _scanSummary = ""; }
            if (_animator != null && !_animator.isHuman) {
                EditorGUILayout.HelpBox(
                    "Animator is not Humanoid. The symmetry check needs Humanoid bone bindings (LeftUpperLeg, RightUpperLeg, Hips).",
                    MessageType.Warning);
            }
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Limit scan to",
                        "Optional. When set, Scan only walks this single SkinnedMeshRenderer instead of every renderer under the avatar. Useful when debugging one outfit / mesh without touching the exclusion list."),
                    GUILayout.Width(100));
                var newLimit = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_limitToRenderer, typeof(SkinnedMeshRenderer), true);
                if (newLimit != _limitToRenderer) {
                    _limitToRenderer = newLimit;
                    // Save the user a click: when they pick a renderer to
                    // limit the scan to, also seed the Inspect Vertex
                    // renderer with the same value so "Why?" / Inspect
                    // queries default to the same focused renderer.
                    if (newLimit != null && _inspectRenderer == null) _inspectRenderer = newLimit;
                }
            }
            if (_animator != null && _limitToRenderer != null
                    && !_limitToRenderer.transform.IsChildOf(_animator.transform)) {
                EditorGUILayout.HelpBox(
                    "The 'Limit scan to' renderer is not a descendant of the picked Animator. The scan will still run on it, but side classification uses the Animator's Hips so it may misbehave for renderers parented elsewhere.",
                    MessageType.Warning);
            }
        }

        private void DrawTunables() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Weight floor",
                        "Per-vertex weights below this are treated as noise and ignored. 0.005 = 0.5% influence."),
                    GUILayout.Width(100));
                _weightFloor = EditorGUILayout.Slider(_weightFloor, 0f, 0.5f);
            }
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Center margin",
                        "Vertices closer to the avatar's centerline than this aren't classified as left or right. Avoids spurious flags on the spine."),
                    GUILayout.Width(100));
                _centerMargin = EditorGUILayout.Slider(_centerMargin, 0f, 0.2f);
            }
            using (new EditorGUILayout.HorizontalScope()) {
                _showGizmos = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show gizmos in Scene view",
                        "Draw a red marker in the Scene view at every flagged vertex's bind-pose world position."),
                    _showGizmos, GUILayout.Width(220));
                _verboseLog = EditorGUILayout.ToggleLeft(
                    new GUIContent("Verbose log",
                        "On scan, dump per-renderer stats and per-skipped-weight reasons to the Unity console. Useful for understanding why a weight you expected to be flagged isn't."),
                    _verboseLog);
            }
            using (new EditorGUILayout.HorizontalScope()) {
                _scanCenterBand = EditorGUILayout.ToggleLeft(
                    new GUIContent("Scan centre-band vertices",
                        "When on, vertices in the centre stripe (between -centerMargin and +centerMargin in Hips local space) are also scanned for cross-side weights. Off by default — centre-band bleed (spine ↔ clavicle, hip ↔ pelvis) is usually legitimate and floods the issue list. Turn on if you suspect spine-area weight contamination."),
                    _scanCenterBand, GUILayout.Width(220));
                if (_scanCenterBand) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Centre threshold",
                            "Minimum weight a centre-stripe vertex must have to a Left or Right bone before it's flagged. Higher than the regular floor because small bleed near the spine is usually fine."),
                        GUILayout.Width(120));
                    _centerCrossSideFloor = EditorGUILayout.Slider(_centerCrossSideFloor, 0f, 0.5f);
                }
            }
        }

        private void DrawExclusions() {
            EditorGUILayout.LabelField(
                new GUIContent("Exclude renderers (legit cross-side)",
                    "Add any SkinnedMeshRenderer that bridges left/right by design (capes, dresses, tails). They won't be scanned."),
                EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.MinHeight(40))) {
                if (_excludedRenderers.Count == 0) {
                    EditorGUILayout.LabelField("(none)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    for (int i = 0; i < _excludedRenderers.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            _excludedRenderers[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                                _excludedRenderers[i], typeof(SkinnedMeshRenderer), true);
                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                        }
                    }
                    if (removeIndex >= 0) _excludedRenderers.RemoveAt(removeIndex);
                }
                if (GUILayout.Button("Add row", EditorStyles.miniButton, GUILayout.Width(80))) {
                    _excludedRenderers.Add(null);
                }
            }
        }

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canScan = _animator != null && _animator.isHuman;
                using (new EditorGUI.DisabledScope(!canScan)) {
                    if (GUILayout.Button("Scan", GUILayout.Height(24), GUILayout.MinWidth(120))) Scan();
                }
                using (new EditorGUI.DisabledScope(_issues.Count == 0)) {
                    if (GUILayout.Button("Clear results", GUILayout.Height(24), GUILayout.Width(110))) {
                        _issues.Clear(); _scanSummary = ""; SceneView.RepaintAll();
                    }
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button("Stop preview", GUILayout.Height(24), GUILayout.Width(110))) {
                        StopPreview();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_scanSummary)) {
                    EditorGUILayout.LabelField(_scanSummary, EditorStyles.miniLabel);
                }
            }
        }

        private void DrawIssues() {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick an Animator and click Scan." : "No issues found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    // Pre-bucket counts once per draw rather than the previous
                    // O(n²) Count() on every renderer header — meaningful at
                    // multi-thousand-issue scales.
                    var perRendererCount = new Dictionary<SkinnedMeshRenderer, int>();
                    foreach (var i in _issues) {
                        if (i.Renderer == null) continue;
                        if (perRendererCount.ContainsKey(i.Renderer)) perRendererCount[i.Renderer]++;
                        else perRendererCount[i.Renderer] = 1;
                    }
                    SkinnedMeshRenderer lastRenderer = null;
                    foreach (var i in _issues) {
                        if (i.Renderer != lastRenderer) {
                            int count = i.Renderer != null && perRendererCount.TryGetValue(i.Renderer, out var n) ? n : 0;
                            EditorGUILayout.LabelField(
                                $"{i.RendererPath}  —  {count} issue(s)" + (i.Renderer == null ? "  (renderer destroyed)" : ""),
                                EditorStyles.boldLabel);
                            lastRenderer = i.Renderer;
                        }
                        DrawIssueRow(i);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawIssueRow(Issue i) {
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope()) {
                    string categoryTag;
                    switch (i.Category) {
                        case IssueCategory.HumanoidCrossSide:    categoryTag = "[humanoid]"; break;
                        case IssueCategory.SpatialCrossSide:     categoryTag = "[spatial]";  break;
                        case IssueCategory.CenterBandSideBleed:  categoryTag = "[center]";   break;
                        default:                                  categoryTag = "[?]";        break;
                    }
                    string boneName = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
                    EditorGUILayout.LabelField(
                        $"{categoryTag} vertex #{i.VertexIndex}  on {i.VertexSide}  weighted to {boneName} ({i.BoneSide})  weight={i.Weight:F3}",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"world pos: ({i.WorldPosition.x:F3}, {i.WorldPosition.y:F3}, {i.WorldPosition.z:F3})",
                        EditorStyles.miniLabel);
                }
                using (new EditorGUI.DisabledScope(i.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("Ping", "Highlight the renderer in the hierarchy."),
                            EditorStyles.miniButton, GUILayout.Width(40))) {
                        if (i.Renderer != null) {
                            Selection.activeObject = i.Renderer;
                            EditorGUIUtility.PingObject(i.Renderer);
                        }
                    }
                }
                if (GUILayout.Button(new GUIContent("Frame", "Move the Scene view camera to the vertex."),
                        EditorStyles.miniButton, GUILayout.Width(50))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(i.WorldPosition, sv.rotation, 0.3f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(i.OffendingBone == null)) {
                    bool isPreviewing = _previewBone == i.OffendingBone && i.OffendingBone != null;
                    if (GUILayout.Button(
                            new GUIContent(isPreviewing ? "Stop" : "Preview",
                                "Wobble the offending bone in the Scene view so you can see how the bad weights deform the mesh. Click again to stop."),
                            EditorStyles.miniButton, GUILayout.Width(60))) {
                        if (isPreviewing) StopPreview();
                        else if (i.OffendingBone != null) StartPreview(i.OffendingBone);
                    }
                }
                using (new EditorGUI.DisabledScope(i.Renderer == null || i.OffendingBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Why?",
                                "Send this vertex to the Inspect Vertex panel and run a per-weight verdict — useful for understanding why a related weight didn't flag."),
                            EditorStyles.miniButton, GUILayout.Width(40))) {
                        _inspectRenderer = i.Renderer;
                        _inspectVertexIndex = i.VertexIndex;
                        InspectVertex();
                    }
                }
            }
            EditorGUILayout.Space(2);
        }

        private void DrawDebugBar() {
            DrawNonReadableBanner();
            DrawVertexInspector();
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(
                        new GUIContent("Dump weights for selection",
                            "Print every vertex's bone weights for the currently selected SkinnedMeshRenderer to the Unity console. Useful when an issue you expect isn't being flagged."),
                        GUILayout.Height(22))) {
                    DumpSelectedRendererWeights();
                }
            }
        }

        private void DrawNonReadableBanner() {
            if (_nonReadableRenderers.Count == 0) return;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField(
                    $"{_nonReadableRenderers.Count} renderer(s) skipped — mesh has Read/Write disabled in importer.",
                    EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button(
                        new GUIContent($"Enable Read/Write & rescan",
                            "For every skipped renderer, find its source asset, set Read/Write Enabled in the model importer, reimport, then re-run the scan."),
                        GUILayout.Width(200), GUILayout.Height(34))) {
                    EnableReadWriteOnSkippedAndRescan();
                }
            }
        }

        private void DrawVertexInspector() {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField(
                    new GUIContent("Inspect specific vertex",
                        "When an issue you expect isn't flagged, drop the renderer here and type the vertex index. The console gets a per-weight verdict explaining exactly which gate every weight passed or failed against the current thresholds."),
                    EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField("Renderer", GUILayout.Width(64));
                    _inspectRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_inspectRenderer, typeof(SkinnedMeshRenderer), true);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField("Vertex #", GUILayout.Width(64));
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

        // ------ Scene view gizmos -----------------------------------------

        private void OnSceneGui(SceneView sceneView) {
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
