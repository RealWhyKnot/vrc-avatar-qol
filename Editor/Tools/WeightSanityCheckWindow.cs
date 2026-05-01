// WeightSanityCheckWindow.cs
//
// Detects the most common kind of weight contamination introduced by Blender's
// Data Transfer / robust weight transfer: vertices on one side of the avatar
// (say, a garter on the LEFT leg) getting non-trivial weight from a bone on
// the OTHER side (Right leg). When the avatar moves, those stray weights
// stretch or follow the wrong limb.
//
// What we check:
//   For each SkinnedMeshRenderer under a Humanoid Animator, every vertex's
//   bind-pose world position is classified as Left/Right/Center using the
//   Animator's actual Hips & LeftUpperLeg positions (so we don't depend on
//   any specific Unity coordinate convention). Each weight on that vertex is
//   classified by walking up its bone's parent chain to the nearest Humanoid
//   ancestor. If a Left-side vertex has weight > threshold from a Right-side
//   bone (or vice versa), it's flagged.
//
// Tunables: weight floor (skip noise weights) and center margin (vertices in
// a band around the spine aren't classified as either side).
//
// What we don't check (yet):
//   - Vertices with zero ancestor info on the bone (loose/prop bones).
//   - Bone-graph distance between weighted bones.
//   - Per-island variance.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class WeightSanityCheckWindow : EditorWindow {

        // Persisted across domain reloads.
        [SerializeField] private Animator _animator;
        [SerializeField] private float _weightFloor   = 0.01f;  // skip stray weights below this
        [SerializeField] private float _centerMargin  = 0.02f;  // metres in Hips local space
        [SerializeField] private bool  _showGizmos    = true;

        // Renderer opt-out list — by default scan everything; let the user
        // exclude meshes that legitimately bridge sides (capes, dresses).
        [SerializeField] private List<SkinnedMeshRenderer> _excludedRenderers = new List<SkinnedMeshRenderer>();

        // Scan output. Not serialized: rebuilt by Scan().
        private readonly List<Issue> _issues = new List<Issue>();
        private string _scanSummary = "";
        private Vector2 _scroll;

        // ------ Public entry points ----------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<WeightSanityCheckWindow>(false, "Weight Sanity Check", true);
            w.titleContent = new GUIContent("Avatar QoL — Weight Sanity Check");
            w.minSize = new Vector2(560, 420);
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
        }

        private void OnDisable() {
            SceneView.duringSceneGui -= OnSceneGui;
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
                        "Per-vertex weights below this are treated as noise and ignored. 0.01 = 1% influence."),
                    GUILayout.Width(100));
                _weightFloor = EditorGUILayout.Slider(_weightFloor, 0f, 0.5f);
            }
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Center margin",
                        "Vertices closer to the avatar's centerline (in Hips local space) than this aren't classified as left or right. Avoids spurious flags on the spine."),
                    GUILayout.Width(100));
                _centerMargin = EditorGUILayout.Slider(_centerMargin, 0f, 0.2f);
            }
            using (new EditorGUILayout.HorizontalScope()) {
                _showGizmos = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show gizmos in Scene view",
                        "Draw a red marker in the Scene view at every flagged vertex's bind-pose world position."),
                    _showGizmos);
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
                    EditorGUILayout.LabelField(
                        $"vertex #{i.VertexIndex}  on {i.VertexSide}  weighted to {i.OffendingBone.name} ({i.BoneSide})  weight={i.Weight:F3}",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"world pos: ({i.WorldPosition.x:F3}, {i.WorldPosition.y:F3}, {i.WorldPosition.z:F3})",
                        EditorStyles.miniLabel);
                }
                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(44))) {
                    if (i.Renderer != null) {
                        Selection.activeObject = i.Renderer;
                        EditorGUIUtility.PingObject(i.Renderer);
                    }
                }
                if (GUILayout.Button("Frame", EditorStyles.miniButton, GUILayout.Width(50))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(i.WorldPosition, sv.rotation, 0.3f);
                        sv.Repaint();
                    }
                }
            }
            EditorGUILayout.Space(2);
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
            foreach (var r in renderers) {
                if (r == null || r.sharedMesh == null) continue;
                if (_excludedRenderers.Contains(r)) continue;
                verticesScanned += ScanRenderer(r, sideMap);
                renderersScanned++;
            }

            // Group by renderer for the UI.
            _issues.Sort((a, b) => {
                int r = string.Compare(
                    AvatarQol.GetGameObjectPath(a.Renderer.gameObject),
                    AvatarQol.GetGameObjectPath(b.Renderer.gameObject),
                    System.StringComparison.Ordinal);
                if (r != 0) return r;
                return a.VertexIndex.CompareTo(b.VertexIndex);
            });

            _scanSummary = $"Scanned {verticesScanned} vertices across {renderersScanned} renderer(s); flagged {_issues.Count}.";
            SceneView.RepaintAll();
        }

        private int ScanRenderer(SkinnedMeshRenderer renderer, HumanoidSideMap sideMap) {
            var mesh = renderer.sharedMesh;
            if (mesh == null) return 0;
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return 0;

            // Tag each bone the renderer references — caches inside sideMap.
            // Skip the iteration entirely if no Left/Right bones exist on this
            // rig (e.g. a non-Humanoid head-only mesh).
            var boneSides = new BoneSide[bones.Length];
            bool anyLeft = false, anyRight = false;
            for (int i = 0; i < bones.Length; i++) {
                boneSides[i] = sideMap.GetSide(bones[i]);
                if (boneSides[i] == BoneSide.Left) anyLeft = true;
                if (boneSides[i] == BoneSide.Right) anyRight = true;
            }
            if (!anyLeft || !anyRight) return mesh.vertexCount;

            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            int weightCursor = 0;
            var rendererTransform = renderer.transform;

            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];
                var worldPos = rendererTransform.TransformPoint(verts[v]);
                var vertexSide = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);

                // Only flag vertices that are clearly on a side; center-band
                // vertices are by design ambiguous.
                if (vertexSide == BoneSide.Left || vertexSide == BoneSide.Right) {
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[weightCursor + w];
                        if (bw.weight < _weightFloor) continue;
                        var bSide = boneSides[bw.boneIndex];
                        if (bSide == BoneSide.Unknown || bSide == BoneSide.Center) continue;
                        if (bSide == vertexSide) continue;
                        // Mismatch: vertex on one side, weight on the other.
                        _issues.Add(new Issue {
                            Renderer       = renderer,
                            VertexIndex    = v,
                            WorldPosition  = worldPos,
                            VertexSide     = vertexSide,
                            OffendingBone  = bones[bw.boneIndex],
                            BoneSide       = bSide,
                            Weight         = bw.weight,
                        });
                    }
                }

                weightCursor += wCount;
            }
            return mesh.vertexCount;
        }

        // ------ Scene view gizmos -----------------------------------------

        private void OnSceneGui(SceneView sceneView) {
            if (!_showGizmos || _issues.Count == 0) return;
            var prevColor = Handles.color;
            Handles.color = new Color(1f, 0.25f, 0.25f, 0.95f);
            // A tiny solid disc at each issue position. Using HandleUtility
            // GetHandleSize for camera-relative scaling so issues stay
            // visible whether you're up close or zoomed out.
            foreach (var i in _issues) {
                if (i.Renderer == null) continue;
                var size = HandleUtility.GetHandleSize(i.WorldPosition) * 0.04f;
                Handles.SphereHandleCap(0, i.WorldPosition, Quaternion.identity, size, EventType.Repaint);
            }
            Handles.color = prevColor;
        }

        // ------ Records ----------------------------------------------------

        private sealed class Issue {
            public SkinnedMeshRenderer Renderer;
            public int VertexIndex;
            public Vector3 WorldPosition;
            public BoneSide VertexSide;
            public Transform OffendingBone;
            public BoneSide BoneSide;
            public float Weight;
        }
    }
}
