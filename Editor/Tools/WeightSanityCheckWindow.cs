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
        // Lower default than the original 0.01: real bleed in the 0.001-0.005
        // range still causes visible stretching during animation. Tunable
        // upward if it's flagging too much.
        [SerializeField] private float _weightFloor   = 0.005f;
        [SerializeField] private float _centerMargin  = 0.02f;
        [SerializeField] private bool  _showGizmos    = true;
        [SerializeField] private bool  _verboseLog    = false;

        [SerializeField] private List<SkinnedMeshRenderer> _excludedRenderers = new List<SkinnedMeshRenderer>();

        private readonly List<Issue> _issues = new List<Issue>();
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
                    SkinnedMeshRenderer lastRenderer = null;
                    int rendererIssueCount = 0;
                    foreach (var i in _issues) {
                        if (i.Renderer != lastRenderer) {
                            rendererIssueCount = _issues.Count(x => x.Renderer == i.Renderer);
                            EditorGUILayout.LabelField(
                                $"{AvatarQol.GetGameObjectPath(i.Renderer.gameObject)}  —  {rendererIssueCount} issue(s)",
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
                    string categoryTag = i.Category == IssueCategory.HumanoidCrossSide
                        ? "[humanoid]"
                        : "[spatial]";
                    EditorGUILayout.LabelField(
                        $"{categoryTag} vertex #{i.VertexIndex}  on {i.VertexSide}  weighted to {i.OffendingBone.name} ({i.BoneSide})  weight={i.Weight:F3}",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"world pos: ({i.WorldPosition.x:F3}, {i.WorldPosition.y:F3}, {i.WorldPosition.z:F3})",
                        EditorStyles.miniLabel);
                }
                if (GUILayout.Button(new GUIContent("Ping", "Highlight the renderer in the hierarchy."),
                        EditorStyles.miniButton, GUILayout.Width(40))) {
                    if (i.Renderer != null) {
                        Selection.activeObject = i.Renderer;
                        EditorGUIUtility.PingObject(i.Renderer);
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
                bool isPreviewing = _previewBone == i.OffendingBone;
                if (GUILayout.Button(
                        new GUIContent(isPreviewing ? "Stop" : "Preview",
                            "Wobble the offending bone in the Scene view so you can see how the bad weights deform the mesh. Click again to stop."),
                        EditorStyles.miniButton, GUILayout.Width(60))) {
                    if (isPreviewing) StopPreview();
                    else StartPreview(i.OffendingBone);
                }
            }
            EditorGUILayout.Space(2);
        }

        private void DrawDebugBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(
                        new GUIContent("Dump weights for selection",
                            "Print every vertex's bone weights for the currently selected SkinnedMeshRenderer to the Unity console. Useful when an issue you expect isn't being flagged."),
                        GUILayout.Height(22))) {
                    DumpSelectedRendererWeights();
                }
            }
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }

        // ------ Scan -------------------------------------------------------

        private void Scan() {
            _issues.Clear();
            _scanSummary = "";
            if (_animator == null || !_animator.isHuman) return;

            var sideMap = new HumanoidSideMap(_animator);
            if (!sideMap.IsValid) {
                _scanSummary = "Animator has no usable Humanoid bindings (Hips missing).";
                return;
            }

            var renderers = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int verticesScanned = 0;
            int renderersScanned = 0;
            var globalLog = _verboseLog ? new StringBuilder() : null;
            globalLog?.AppendLine($"[Avatar QoL] Weight Sanity Check verbose log");
            globalLog?.AppendLine($"  weightFloor={_weightFloor:F4}, centerMargin={_centerMargin:F3}");
            globalLog?.AppendLine($"  avatar={_animator.gameObject.name}, leftSign={sideMap.LeftSignInHipsLocal}");

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
                int rcmp = string.Compare(
                    AvatarQol.GetGameObjectPath(a.Renderer.gameObject),
                    AvatarQol.GetGameObjectPath(b.Renderer.gameObject),
                    System.StringComparison.Ordinal);
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
            var mesh = renderer.sharedMesh;
            if (mesh == null) return 0;
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) {
                log?.AppendLine($"  SKIP renderer (no bones array): {AvatarQol.GetGameObjectPath(renderer.gameObject)}");
                return 0;
            }
            if (!mesh.isReadable) {
                log?.AppendLine($"  SKIP renderer (mesh not readable; enable Read/Write in importer): {AvatarQol.GetGameObjectPath(renderer.gameObject)}");
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
                    // Fall back to spatial classification.
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
            int weightCursor = 0;
            var rendererTransform = renderer.transform;

            int sLeftVerts = 0, sRightVerts = 0, sCenterVerts = 0;
            int wSkippedFloor = 0, wSkippedCenter = 0, wSkippedUnknown = 0, wSkippedSameSide = 0;
            int wFlaggedHumanoid = 0, wFlaggedSpatial = 0;

            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];
                var worldPos = rendererTransform.TransformPoint(verts[v]);
                var vertexSide = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
                if (vertexSide == BoneSide.Left)   sLeftVerts++;
                else if (vertexSide == BoneSide.Right) sRightVerts++;
                else sCenterVerts++;

                if (vertexSide == BoneSide.Left || vertexSide == BoneSide.Right) {
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[weightCursor + w];
                        if (bw.weight < _weightFloor) { wSkippedFloor++; continue; }
                        var bSide = boneSides[bw.boneIndex];
                        if (bSide == BoneSide.Unknown) { wSkippedUnknown++; continue; }
                        if (bSide == BoneSide.Center)  { wSkippedCenter++;  continue; }
                        if (bSide == vertexSide)        { wSkippedSameSide++; continue; }
                        // Flagged. Distinguish humanoid-derived vs spatial-derived
                        // bone classification so the user can weigh how confident
                        // we should be in the call.
                        var bone = bones[bw.boneIndex];
                        bool spatialClassification = sideMap.GetSide(bone) == BoneSide.Unknown;
                        var category = spatialClassification ? IssueCategory.SpatialCrossSide : IssueCategory.HumanoidCrossSide;
                        if (spatialClassification) wFlaggedSpatial++; else wFlaggedHumanoid++;
                        _issues.Add(new Issue {
                            Renderer       = renderer,
                            VertexIndex    = v,
                            WorldPosition  = worldPos,
                            VertexSide     = vertexSide,
                            OffendingBone  = bone,
                            BoneSide       = bSide,
                            Weight         = bw.weight,
                            Category       = category,
                        });
                    }
                }
                weightCursor += wCount;
            }
            log?.AppendLine($"    verts L={sLeftVerts} R={sRightVerts} C={sCenterVerts}");
            log?.AppendLine($"    weights skipped: floor={wSkippedFloor} center-bone={wSkippedCenter} unknown-bone={wSkippedUnknown} same-side={wSkippedSameSide}");
            log?.AppendLine($"    weights flagged: humanoid={wFlaggedHumanoid} spatial={wFlaggedSpatial}");
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
            var rendererTransform = smr.transform;
            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];
                if (v < limit) {
                    var worldPos = rendererTransform.TransformPoint(verts[v]);
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
            if (_previewBone == null) return;
            _previewBone.localRotation = _previewRestRotation;
            _previewBone = null;
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate() {
            if (_previewBone == null) return;
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
            HumanoidCrossSide,   // bone has Humanoid ancestor on opposite side
            SpatialCrossSide,    // bone has no Humanoid ancestor; pivot on opposite side
        }

        private sealed class Issue {
            public SkinnedMeshRenderer Renderer;
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
