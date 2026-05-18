// BoneMergerWindow.cs
//
// Folds a stray duplicate bone into the bone it should have been part of.
// Typical case: Blender / FBX export produced a ".001" sub-bone (Boob_L.001
// under Boob_L, Hair_R.001 under Hair_R) and the user wants those gone --
// the sub-bone's skin weights should belong to the parent, and the extra
// transform shouldn't exist in the rig.
//
// What "merge" means here:
//   - For every SkinnedMeshRenderer under the picked Animator, any vertex
//     weight on the merge-from bone is redirected to the merge-into bone.
//     Duplicate slots collapse, slots sort by weight descending, slots that
//     hit zero get dropped.
//   - If Delete is on, the merge-from GameObject is destroyed. Its children
//     re-parent onto the merge-into bone first so any rig sitting underneath
//     it stays in place (world transforms preserved).
//
// Mesh handling mirrors WeightFixer: FBX/OBJ/DAE/glTF sub-asset meshes
// can't be modified in place (the importer overwrites them on every
// reimport), so we clone to a fresh `.mesh` asset under
// "Assets/AvatarQol Generated/", assign the clone to renderer.sharedMesh,
// and write the merged weights to the clone.
//
// Mathematical caveat: weight-merging is visually identical to the
// original skinning only when the two bones share the same rest pose
// (i.e. the sub-bone sits at local zero under the kept bone). When the
// rest poses differ, vertices that were weighted to the sub-bone will
// snap to the kept bone's frame at the rig's rest pose. That's inherent
// to linear blend skinning, not a tool bug.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class BoneMergerWindow : EditorWindow {

        [System.Serializable]
        private sealed class BonePair {
            // The bone whose weights move OFF (and that gets deleted, if the
            // option is on). Named explicitly to avoid the "which is source,
            // which is target?" confusion.
            public Transform mergeFrom;
            // The bone that picks up the weights and survives.
            public Transform mergeInto;
        }

        [SerializeField] private Animator _animator;
        [SerializeField] private List<BonePair> _pairs = new List<BonePair>();
        [SerializeField] private bool _deleteMergedBones = true;
        [SerializeField] private bool _reparentChildren  = true;

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#bone-merger";
        private const string GeneratedFolder = "Assets/AvatarQol Generated";

        private string _resultSummary = "";
        private readonly List<string> _resultDetail = new List<string>();
        private Vector2 _scroll;

        // ------ Public entry points ----------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<BoneMergerWindow>(false, "Bone Merger", true);
            w.titleContent = new GUIContent("Avatar QoL -- Bone Merger");
            w.minSize = new Vector2(560, 440);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var animator = go.GetComponent<Animator>() ??
                           go.GetComponentInParent<Animator>(true) ??
                           go.GetComponentInChildren<Animator>(true);
            if (animator != null) _animator = animator;
            ClearResults();
        }

        // ------ GUI --------------------------------------------------------

        private void OnGUI() {
            DrawTitleBar();
            AvatarQolStyles.Notice(AvatarQolStyles.NoticeKind.Info,
                "Folds a stray duplicate bone (e.g. Blender's Boob_L.001) into its kept counterpart. Skin weights transfer across every SkinnedMeshRenderer under the avatar, then the merged-away bone is removed.");
            DrawAvatar();
            EditorGUILayout.Space(2);
            DrawPairs();
            EditorGUILayout.Space(2);
            DrawOptions();
            EditorGUILayout.Space(2);
            DrawApplyBar();
            EditorGUILayout.Space(2);
            DrawResults();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Bone Merger",
                        "Transfer skin weights from one bone onto another, then optionally delete the now-empty bone. Useful for collapsing Blender ''.001'' duplicate bones back into their parent."),
                    AvatarQolStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawAvatar() {
            using (AvatarQolStyles.Section("1. Pick avatar",
                    "The Animator at the root of the avatar. Every SkinnedMeshRenderer under it gets scanned for weights on the bones you list below.")) {
                AvatarQolStyles.LabeledField(
                    new GUIContent("Animator",
                        "Animator at the avatar root. Doesn't need to be Humanoid -- any rig works."),
                    () => {
                        var next = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (next != _animator) { _animator = next; ClearResults(); }
                    });
                if (_animator != null) {
                    int smrCount = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                    EditorGUILayout.LabelField($"{smrCount} SkinnedMeshRenderer(s) under this Animator.", AvatarQolStyles.Muted);
                }
            }
        }

        private void DrawPairs() {
            using (AvatarQolStyles.Section("2. Bones to merge",
                    "Drag two bones onto each row. The LEFT bone's weights move onto the RIGHT bone, and the LEFT bone is then removed.")) {
                EditorGUILayout.LabelField(
                    "Read each row left to right: \"merge THIS bone into THIS one\". Drag the duplicate / extra bone on the left, the keeper on the right.",
                    AvatarQolStyles.Muted);

                EditorGUILayout.Space(2);

                // Column header so the direction is unmistakable even without
                // hovering tooltips.
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Merge this bone",
                            "The bone you want to get rid of. Its weights are transferred away, and (if Delete is on) its GameObject is destroyed."),
                        EditorStyles.miniBoldLabel);
                    GUILayout.Space(20);
                    EditorGUILayout.LabelField(
                        new GUIContent("into this bone",
                            "The bone you want to keep. It receives the weights from the merged-away bone."),
                        EditorStyles.miniBoldLabel);
                    GUILayout.Space(24);
                }

                if (_pairs.Count == 0) {
                    EditorGUILayout.LabelField("(no pairs yet -- click Add row to start)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeAt = -1;
                    for (int i = 0; i < _pairs.Count; i++) {
                        var pair = _pairs[i] ?? (_pairs[i] = new BonePair());
                        using (new EditorGUILayout.HorizontalScope()) {
                            var nextFrom = (Transform)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "The bone whose weights are being transferred away. This is the bone that disappears."),
                                pair.mergeFrom, typeof(Transform), true);
                            EditorGUILayout.LabelField(
                                new GUIContent("into", "Weights move from the LEFT bone onto the RIGHT bone."),
                                GUILayout.Width(28));
                            var nextInto = (Transform)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "The bone that picks up the weights. This bone stays."),
                                pair.mergeInto, typeof(Transform), true);
                            if (nextFrom != pair.mergeFrom || nextInto != pair.mergeInto) {
                                pair.mergeFrom = nextFrom;
                                pair.mergeInto = nextInto;
                                ClearResults();
                            }
                            if (GUILayout.Button(
                                    new GUIContent("X", "Remove this row."),
                                    EditorStyles.miniButton, GUILayout.Width(22))) {
                                removeAt = i;
                            }
                        }
                        // Surface inline problems so the user sees them before
                        // hitting Apply.
                        if (pair.mergeFrom != null && pair.mergeInto != null && pair.mergeFrom == pair.mergeInto) {
                            EditorGUILayout.LabelField("   Both fields point at the same bone -- nothing to merge.", AvatarQolStyles.Muted);
                        }
                    }
                    if (removeAt >= 0) { _pairs.RemoveAt(removeAt); ClearResults(); }
                }

                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(
                            new GUIContent("Add row", "Append an empty pair slot. Drag two bones onto it after."),
                            GUILayout.Width(80))) {
                        _pairs.Add(new BonePair());
                        ClearResults();
                    }
                    using (new EditorGUI.DisabledScope(_pairs.Count == 0)) {
                        if (GUILayout.Button(
                                new GUIContent("Clear list", "Remove every pair from the list."),
                                GUILayout.Width(80))) {
                            _pairs.Clear();
                            ClearResults();
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void DrawOptions() {
            using (AvatarQolStyles.Section("3. Options",
                    "Defaults match the typical Blender duplicate-bone cleanup workflow.")) {
                _deleteMergedBones = EditorGUILayout.ToggleLeft(
                    new GUIContent("Delete the merged-away bone after applying",
                        "When on, the LEFT bone in each row gets removed from the hierarchy after its weights are transferred. Turn off if you just want to re-weight without changing the rig."),
                    _deleteMergedBones);
                using (new EditorGUI.DisabledScope(!_deleteMergedBones)) {
                    _reparentChildren = EditorGUILayout.ToggleLeft(
                        new GUIContent("Re-parent its children onto the kept bone",
                            "When on, any GameObjects parented under the merged-away bone get moved onto the kept bone (world transform preserved) before deletion. Recommended -- otherwise the children get destroyed with the bone."),
                        _reparentChildren);
                }
            }
        }

        private void DrawApplyBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanApply())) {
                    if (AvatarQolStyles.PrimaryButtonInline(
                            new GUIContent("Apply merge",
                                "Transfer skin weights across every renderer under the Animator, then (if enabled) delete the merged-away bones. Wrapped in a single Undo step."),
                            GUILayout.MinWidth(140))) {
                        Apply();
                    }
                }
                using (new EditorGUI.DisabledScope(_resultSummary == "" && _resultDetail.Count == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Clear result",
                                "Drop the last result summary. Doesn't undo any merge you've already applied."),
                            GUILayout.Height(28), GUILayout.Width(94))) {
                        ClearResults();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_resultSummary)) {
                    EditorGUILayout.LabelField(_resultSummary, AvatarQolStyles.Muted);
                }
            }
        }

        private void DrawResults() {
            if (_resultDetail.Count == 0) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                EditorGUILayout.LabelField("Last run", AvatarQolStyles.SubsectionTitle);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var line in _resultDetail) {
                    EditorGUILayout.LabelField(line, AvatarQolStyles.Mono);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        // ------ Validation -------------------------------------------------

        private bool CanApply() {
            if (_animator == null) return false;
            foreach (var p in _pairs) {
                if (p != null && p.mergeFrom != null && p.mergeInto != null && p.mergeFrom != p.mergeInto) {
                    return true;
                }
            }
            return false;
        }

        // Reject pair lists that can't be sequenced cleanly:
        //   - Same bone listed as the merge-from in two pairs with DIFFERENT
        //     destinations -- ambiguous.
        //   - A cycle through mergeFrom -> mergeInto edges. Cycles cause the
        //     chain-collapse step to no-op every weight redirect AND the
        //     deletion phase would still remove the bones, leaving orphaned
        //     weights pointing at destroyed slots. Easier to refuse upfront.
        private static bool ValidatePairs(List<BonePair> pairs, out string message) {
            message = "";
            var fromToInto = new Dictionary<Transform, Transform>();
            foreach (var p in pairs) {
                if (fromToInto.TryGetValue(p.mergeFrom, out var existing)) {
                    if (existing != p.mergeInto) {
                        message = $"\"{p.mergeFrom.name}\" is set to merge into two different bones (\"{existing.name}\" and \"{p.mergeInto.name}\"). Pick one and try again.";
                        return false;
                    }
                    continue; // exact duplicate row is fine
                }
                fromToInto[p.mergeFrom] = p.mergeInto;
            }
            foreach (var start in fromToInto.Keys) {
                var visited = new HashSet<Transform>();
                var cur = start;
                while (fromToInto.TryGetValue(cur, out var next)) {
                    if (!visited.Add(cur)) {
                        message = $"Cycle in the pair list (e.g. \"{cur.name}\" is reachable from itself). Break the loop and try again.";
                        return false;
                    }
                    cur = next;
                }
            }
            return true;
        }

        // ------ Apply ------------------------------------------------------

        private void Apply() {
            ClearResults();
            if (_animator == null) {
                _resultSummary = "Pick an Animator first.";
                return;
            }
            var validPairs = _pairs
                .Where(p => p != null && p.mergeFrom != null && p.mergeInto != null && p.mergeFrom != p.mergeInto)
                .ToList();
            if (validPairs.Count == 0) {
                _resultSummary = "Add at least one pair (LEFT bone and RIGHT bone, both set, not the same bone).";
                return;
            }

            if (!ValidatePairs(validPairs, out string conflictMessage)) {
                _resultSummary = conflictMessage;
                return;
            }

            var renderers = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) {
                _resultSummary = "The picked Animator has no SkinnedMeshRenderers underneath.";
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Merge bones");

            int renderersTouched = 0;
            int meshesCloned = 0;
            int weightsRedirected = 0;
            int unreadableRenderers = 0;
            var clonedPaths = new List<string>();
            var perPairFlaggedRenderers = new Dictionary<BonePair, int>();

            try {
                foreach (var renderer in renderers) {
                    if (renderer == null || renderer.sharedMesh == null) continue;
                    if (!renderer.sharedMesh.isReadable) {
                        unreadableRenderers++;
                        _resultDetail.Add($"SKIP {RendererPath(renderer)} -- mesh not readable. Enable Read/Write on the model importer.");
                        continue;
                    }
                    var rendererResult = MergeOnRenderer(renderer, validPairs, ref meshesCloned, clonedPaths);
                    if (rendererResult.RedirectsApplied > 0) {
                        renderersTouched++;
                        weightsRedirected += rendererResult.RedirectsApplied;
                        foreach (var pair in rendererResult.PairsThatMatched) {
                            perPairFlaggedRenderers.TryGetValue(pair, out var n);
                            perPairFlaggedRenderers[pair] = n + 1;
                        }
                        _resultDetail.Add($"OK   {RendererPath(renderer)} -- redirected {rendererResult.RedirectsApplied} weight slot(s){(rendererResult.WasCloned ? " (cloned mesh)" : "")}.");
                    }
                }

                // Bone deletion / re-parenting. Done after all weight edits so
                // the bones[] arrays we just walked stay valid.
                int bonesDeleted = 0;
                int childrenReparented = 0;
                if (_deleteMergedBones) {
                    foreach (var pair in validPairs) {
                        if (pair.mergeFrom == null || pair.mergeInto == null) continue;
                        if (_reparentChildren) {
                            var kids = new List<Transform>();
                            foreach (Transform c in pair.mergeFrom) kids.Add(c);
                            foreach (var c in kids) {
                                Undo.SetTransformParent(c, pair.mergeInto, "Re-parent under kept bone");
                                childrenReparented++;
                            }
                        }
                        Undo.DestroyObjectImmediate(pair.mergeFrom.gameObject);
                        bonesDeleted++;
                    }
                }

                if (meshesCloned > 0) AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);

                // Warn about pairs that didn't apply to any renderer -- usually
                // means the user dragged in a bone that isn't in any
                // SkinnedMeshRenderer's bones array.
                var unusedPairs = new List<string>();
                foreach (var pair in validPairs) {
                    if (!perPairFlaggedRenderers.ContainsKey(pair)) {
                        unusedPairs.Add($"{NameOrDestroyed(pair.mergeFrom)} -> {NameOrDestroyed(pair.mergeInto)}");
                    }
                }
                if (unusedPairs.Count > 0) {
                    _resultDetail.Add($"WARN {unusedPairs.Count} pair(s) didn't match any renderer's bone list: {string.Join(", ", unusedPairs)}. The bones may not be skinned to any mesh under this Animator, or the merge-into bone may be absent from the renderer's bone array.");
                }

                _resultSummary = BuildSummary(
                    renderersTouched, weightsRedirected, meshesCloned,
                    bonesDeleted, childrenReparented, unreadableRenderers);
                if (meshesCloned > 0) {
                    _resultDetail.Add("");
                    _resultDetail.Add("Cloned meshes (your FBX was untouched):");
                    foreach (var p in clonedPaths) _resultDetail.Add($"  {p}");
                }
            } catch (System.Exception ex) {
                Undo.RevertAllInCurrentGroup();
                Debug.LogException(ex);
                _resultSummary = "Merge failed -- nothing was changed. See console for the exception.";
            }
        }

        private static string BuildSummary(int renderersTouched, int weightsRedirected, int meshesCloned,
                                           int bonesDeleted, int childrenReparented, int unreadable) {
            var parts = new List<string>();
            parts.Add($"{weightsRedirected} weight(s) on {renderersTouched} renderer(s)");
            if (meshesCloned > 0) parts.Add($"{meshesCloned} mesh(es) cloned");
            if (bonesDeleted > 0) parts.Add($"{bonesDeleted} bone(s) deleted");
            if (childrenReparented > 0) parts.Add($"{childrenReparented} child(ren) re-parented");
            if (unreadable > 0)   parts.Add($"{unreadable} renderer(s) skipped (mesh not readable)");
            return string.Join(", ", parts) + ".";
        }

        // ------ Per-renderer merge ----------------------------------------

        private struct RendererResult {
            public int RedirectsApplied;
            public bool WasCloned;
            public List<BonePair> PairsThatMatched;
        }

        private RendererResult MergeOnRenderer(SkinnedMeshRenderer renderer, List<BonePair> validPairs,
                                               ref int meshesCloned, List<string> clonedPaths) {
            var result = new RendererResult { PairsThatMatched = new List<BonePair>() };
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return result;

            // Build the (bone-index -> bone-index) redirect map for THIS
            // renderer. A pair only applies if the merge-from bone is in
            // bones[] for this renderer; the merge-into bone must also be in
            // bones[] (we don't currently grow the bones array).
            var redirect = new Dictionary<int, int>(validPairs.Count);
            var pairsForRenderer = new List<BonePair>();
            foreach (var pair in validPairs) {
                int srcIdx = IndexOf(bones, pair.mergeFrom);
                if (srcIdx < 0) continue;
                int dstIdx = IndexOf(bones, pair.mergeInto);
                if (dstIdx < 0) {
                    _resultDetail.Add($"WARN {RendererPath(renderer)} -- bone \"{NameOrDestroyed(pair.mergeInto)}\" is not in this renderer's bones[]. Skipping that pair for this mesh.");
                    continue;
                }
                redirect[srcIdx] = dstIdx;
                pairsForRenderer.Add(pair);
            }
            if (redirect.Count == 0) return result;

            // Collapse chains a -> b -> c so every key maps to its final
            // destination, with a cycle guard.
            foreach (var key in redirect.Keys.ToList()) {
                int v = redirect[key];
                var visited = new HashSet<int> { key };
                while (redirect.TryGetValue(v, out int next) && visited.Add(v)) {
                    v = next;
                }
                redirect[key] = v;
            }

            // Resolve to an editable mesh -- clone if the sharedMesh is owned
            // by a model importer (FBX/OBJ/etc).
            var sharedMesh = renderer.sharedMesh;
            var editableMesh = ResolveEditableMesh(renderer, sharedMesh, ref meshesCloned, clonedPaths, out bool wasCloned);
            if (editableMesh == null) return result;
            result.WasCloned = wasCloned;

            var srcBpv = editableMesh.GetBonesPerVertex();
            var srcWeights = editableMesh.GetAllBoneWeights();
            int vertCount = srcBpv.Length;
            int totalWeights = srcWeights.Length;

            // Build output buffers. The output may shrink if the redirect
            // causes two slots on the same vertex to collapse, so we build
            // a list and then materialise a NativeArray. try/finally so the
            // Temp allocation always releases, even on exception.
            var newBpv = new NativeArray<byte>(vertCount, Allocator.Temp);
            try {
                var newWeights = new List<BoneWeight1>(totalWeights);

                // Scratch buffers for per-vertex combine. 64 slots is way
                // over anything Unity actually ships.
                const int Scratch = 64;
                var scratchIdx = new int[Scratch];
                var scratchWt  = new float[Scratch];

                int cursor = 0;
                int redirectsApplied = 0;

                for (int v = 0; v < vertCount; v++) {
                    int count = srcBpv[v];
                    if (count == 0) { newBpv[v] = 0; continue; }

                    // Fast-path: nothing on this vertex maps through the
                    // redirect, pass through unchanged.
                    bool touched = false;
                    for (int k = 0; k < count; k++) {
                        if (redirect.ContainsKey(srcWeights[cursor + k].boneIndex)) { touched = true; break; }
                    }
                    if (!touched) {
                        for (int k = 0; k < count; k++) newWeights.Add(srcWeights[cursor + k]);
                        newBpv[v] = (byte)count;
                        cursor += count;
                        continue;
                    }

                    // Slow path: redirect + combine duplicates into scratch.
                    int n = 0;
                    for (int k = 0; k < count; k++) {
                        var bw = srcWeights[cursor + k];
                        int idx = bw.boneIndex;
                        if (redirect.TryGetValue(idx, out int newIdx)) {
                            idx = newIdx;
                            redirectsApplied++;
                        }
                        int existing = -1;
                        for (int s = 0; s < n; s++) {
                            if (scratchIdx[s] == idx) { existing = s; break; }
                        }
                        if (existing >= 0) {
                            scratchWt[existing] += bw.weight;
                        } else if (n < Scratch) {
                            scratchIdx[n] = idx;
                            scratchWt[n]  = bw.weight;
                            n++;
                        }
                    }
                    cursor += count;

                    // Sort by weight descending so the most-influential
                    // slots come first (matches how every other Unity
                    // weight pipeline orders them).
                    for (int a = 0; a < n - 1; a++) {
                        int maxK = a;
                        for (int b = a + 1; b < n; b++) if (scratchWt[b] > scratchWt[maxK]) maxK = b;
                        if (maxK != a) {
                            (scratchWt[a],  scratchWt[maxK])  = (scratchWt[maxK],  scratchWt[a]);
                            (scratchIdx[a], scratchIdx[maxK]) = (scratchIdx[maxK], scratchIdx[a]);
                        }
                    }

                    int emitted = 0;
                    for (int s = 0; s < n; s++) {
                        if (scratchWt[s] <= 0f) continue;
                        newWeights.Add(new BoneWeight1 { boneIndex = scratchIdx[s], weight = scratchWt[s] });
                        emitted++;
                    }
                    newBpv[v] = (byte)emitted;
                }

                if (redirectsApplied > 0) {
                    using (var newWeightsNative = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp)) {
                        Undo.RegisterCompleteObjectUndo(editableMesh, "Merge bone weights");
                        editableMesh.SetBoneWeights(newBpv, newWeightsNative);
                        EditorUtility.SetDirty(editableMesh);
                    }
                    result.RedirectsApplied = redirectsApplied;
                    result.PairsThatMatched = pairsForRenderer;
                }
                return result;
            } finally {
                newBpv.Dispose();
            }
        }

        // ------ Mesh / asset resolution ------------------------------------

        private static Mesh ResolveEditableMesh(SkinnedMeshRenderer renderer, Mesh sharedMesh,
                                                ref int meshesCloned, List<string> clonedPaths,
                                                out bool wasCloned) {
            wasCloned = false;
            var path = AssetDatabase.GetAssetPath(sharedMesh);
            bool isImporterSubAsset = !string.IsNullOrEmpty(path)
                && (path.EndsWith(".fbx",  System.StringComparison.OrdinalIgnoreCase)
                 || path.EndsWith(".obj",  System.StringComparison.OrdinalIgnoreCase)
                 || path.EndsWith(".dae",  System.StringComparison.OrdinalIgnoreCase)
                 || path.EndsWith(".gltf", System.StringComparison.OrdinalIgnoreCase)
                 || path.EndsWith(".glb",  System.StringComparison.OrdinalIgnoreCase));
            if (!isImporterSubAsset) return sharedMesh;

            EnsureGeneratedFolder();
            var clone = Object.Instantiate(sharedMesh);
            clone.name = sharedMesh.name + " (Merged)";
            string targetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedFolder}/{SanitizeFileName(sharedMesh.name)}_MergedBones.asset");
            AssetDatabase.CreateAsset(clone, targetPath);
            // Asset object undo so Ctrl+Z removes the file from disk too,
            // not just the renderer assignment.
            Undo.RegisterCreatedObjectUndo(clone, "Create merged mesh");

            Undo.RecordObject(renderer, "Reassign mesh after bone merge");
            renderer.sharedMesh = clone;
            EditorUtility.SetDirty(renderer);

            meshesCloned++;
            clonedPaths.Add(targetPath);
            wasCloned = true;
            return clone;
        }

        private static void EnsureGeneratedFolder() {
            if (!AssetDatabase.IsValidFolder(GeneratedFolder)) {
                AssetDatabase.CreateFolder("Assets", "AvatarQol Generated");
            }
        }

        private static string SanitizeFileName(string name) {
            if (string.IsNullOrEmpty(name)) return "mesh";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name;
        }

        // ------ Helpers ----------------------------------------------------

        private static int IndexOf(Transform[] arr, Transform value) {
            if (arr == null || value == null) return -1;
            for (int i = 0; i < arr.Length; i++) if (arr[i] == value) return i;
            return -1;
        }

        private static string NameOrDestroyed(Transform t) {
            return t == null ? "(destroyed)" : t.name;
        }

        private static string RendererPath(SkinnedMeshRenderer r) {
            return r == null ? "(null)" : AvatarQol.GetGameObjectPath(r.gameObject);
        }

        private void ClearResults() {
            _resultSummary = "";
            _resultDetail.Clear();
        }
    }
}
