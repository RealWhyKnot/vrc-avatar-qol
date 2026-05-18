# Tools Overview

Every shipping tool, where to find it, what it does.

## Weight Sanity Check

**Where:** *Tools -> WhyKnot -> vrc-avatar-qol -> Weight Sanity Check...*, or right-click an avatar in the hierarchy -> *WhyKnot -> vrc-avatar-qol -> Check weights...* (pre-fills the picker with the selected Animator).

**What it does:** Detects the most common Blender-weight-transfer mistake on humanoid avatars: vertices on one side of the avatar (a left-leg garter, say) picking up non-trivial weight from a bone on the other side (right thigh). When the avatar moves, the bad vertices stretch or follow the wrong limb.

**How it works:**

1. Walks every `SkinnedMeshRenderer` under the picked `Humanoid Animator`.
2. For each vertex, transforms its bind-pose mesh-local position to world, then to Hips local space, and classifies it as **Left / Right / Center** along an axis derived from the actual position of `LeftUpperLeg` relative to `Hips`. (Inferring the axis from the rig means the tool doesn't care about Unity coordinate conventions -- it works on any rig orientation.)
3. For each bone the renderer references, walks up its parent chain to the nearest Humanoid ancestor (LeftUpperLeg, RightUpperArm, etc.). The bone inherits the side of that ancestor -- so custom bones added under a Humanoid bone (e.g. a skirt rig parented to LeftUpperLeg) are correctly tagged.
4. For each vertex, checks every bone weight above the threshold. If a Left-side vertex has weight from a Right-side bone (or vice versa), it's flagged.

**Tunables:**

- **Weight floor** *(default 0.01)* -- weights below this are noise and skipped.
- **Center margin** *(default 0.02 m in Hips local space)* -- vertices closer to the avatar's centerline than this aren't classified as either side. Avoids false flags on the spine.
- **Show gizmos in Scene view** -- draws a red sphere at every flagged vertex's world position.
- **Exclude renderers** -- opt-out list for meshes that legitimately bridge sides (capes, dresses, tails).

**Per-issue UI:** *Ping* selects the affected renderer; *Frame* moves the Scene view camera to the vertex.

**Detection layers** (issues are tagged by which one fired):

- **`[humanoid]`** -- bone has a Humanoid ancestor (LeftUpperLeg, RightShoulder, ...) on the side opposite the vertex. High confidence.
- **`[spatial]`** -- bone has no Humanoid ancestor (custom rig bones, prop bones, etc.) but its pivot's world position sits on the side opposite the vertex. Lower confidence -- useful for catching weight bleed onto custom skirt-rig bones that don't follow the Humanoid convention.

**Wobble button.** Each issue has a *Wobble* button that picks up the offending bone and wobbles it back and forth around its rest pose (+/-30 deg on local X, +/-18 deg on local Z, sinusoidal). The Scene view repaints continuously so you can watch the mesh deform, but the tool does not move or reframe the Scene camera. Click *Stop* on the same row (or *Stop wobble* in the scan bar) to restore.

**Verbose log.** Tick *Verbose log* and click *Scan*; the console gets a per-renderer breakdown of how every weight was treated:

```
RENDER Avatar/Body/SkirtMesh: 47 bones (L=12 R=12 C=23 U=0; 0 of those by spatial fallback)
  verts L=2412 R=2389 C=199
  weights skipped: floor=84 center-bone=2102 unknown-bone=0 same-side=14201
  weights flagged: humanoid=12 spatial=0
```

That tells you exactly why a weight didn't make it into the issue list: below floor, on a Center bone, on an Unknown bone, or already same-side. Lets you tune *Weight floor* / *Center margin* with intent rather than guessing.

**Dump weights for selection.** Surgical debugging: select a SkinnedMeshRenderer in the hierarchy, click the button, and the console gets every bone's classification plus the first 200 vertices' full weight breakdown. Useful when an issue you *know* is bad isn't being flagged.

**What it doesn't catch (yet):**

- Bone-graph distance violations (vertices weighted to bones very far apart in the hierarchy).
- Per-island weight variance.
- Static-mesh weighting issues -- only `SkinnedMeshRenderer` is scanned.

> _Screenshot: TODO_

## PhysBone Clipping Risks

**Where:** *Tools -> WhyKnot -> vrc-avatar-qol -> PhysBone Clipping Risks...*, or right-click a mesh/avatar in the hierarchy -> *WhyKnot -> vrc-avatar-qol -> Check PhysBone clipping...*.

**What it does:** Runs the heavier PhysBone clipping-risk estimate as its own explicit tool. It checks one `SkinnedMeshRenderer` at a time so normal Weight Sanity Check scans stay fast.

**How it works:**

1. Finds active `VRCPhysBone` components under the selected Animator.
2. Builds candidate vertices only from the chosen target mesh.
3. Estimates motion from actual PhysBone settings (`pull`, `spring`, `stiffness`, `gravity`, `radius`, `maxStretch`, collider presence, and reflected limits where available).
4. Compares the target mesh's moving vertices against surface samples. The moving mesh is always included for self-clipping, and you can add as many readable comparison meshes as needed for body, clothing, accessory, or other proximity checks.

**Per-risk UI:** **Create mesh fixes** creates or updates stored **Auto Tighten To Body** setups on the moving mesh object when rows have separate comparison mesh targets, then opens Auto Mesh Fixes for review. **Reduce motion** is the explicit fallback that edits supported PhysBone or authoring settings. *Frame* moves the Scene view camera to the risky vertex, *Reveal* selects the driven transform, and *Wobble* moves that transform until you click Stop.

**Performance rule:** start with one moving mesh and only the comparison meshes you care about. If the scan still takes too long, use a smaller mesh or raise **Driven weight floor** so fewer vertices become candidates.

**Limits:** This is a conservative static estimate, not VRChat's full runtime PhysBone solver. Treat rows as "look here" hints for collider, radius, pull, and stiffness tuning.

## Auto Mesh Fixes

**Where:** *Tools -> WhyKnot -> vrc-avatar-qol -> Auto Mesh Fixes -> Open...*, or right-click an avatar/mesh in the hierarchy -> *WhyKnot -> vrc-avatar-qol -> Auto Mesh Fixes...*.

**What it does:** Stores nondestructive clothing/body mesh fix intent on editor-only components, then generates temporary mesh clones with blendshapes during preview, play mode, and upload. The UI is the main workflow; the component inspector only shows status and an **Open Auto Mesh Fixes** button.

**Preview behavior:** Click **Preview** to hide the current avatar and show a generated copy with the selected blendshape weights enabled. Click the red **Stop Previewing** button to delete the generated copy and restore the original avatar. Preview does not frame, move, or otherwise force the Scene view.

**Current generated fixes:**

- **Tighten clothing** creates a garment blendshape that projects selected clothing vertices toward the body surface, leaving a configurable surface gap.
- **Hide body underneath** creates a body blendshape that pushes or collapses nearby body vertices under the clothing. This hides/collapses geometry by blendshape; it does not delete triangles.

**Nondestructive rule:** Imported FBX/model meshes are never edited. Generated meshes are temporary `Object.Instantiate` clones assigned during the processing session and restored afterward. Re-exporting from Blender keeps the setup because the setup lives on Unity components, not in the imported mesh.

## PhysBone Preset (early)

**Where:** *Tools -> WhyKnot -> vrc-avatar-qol -> Apply PhysBone Preset...*, or right-click a selection of bones in the hierarchy -> *WhyKnot -> vrc-avatar-qol -> Apply PhysBone preset...* (pre-fills the bone list).

**What it does:** Sets up VRChat PhysBones on the selected bones using a *preset* -- a smart template that reads a structural analysis of the selection and adapts. Built-in presets:

- **Tail** -- single long chain near Hips. Spring loosens as the chain gets longer; gravity scales with how vertically the chain hangs (a dangling tail gets full gravity, a tail that points back gets less so it doesn't visibly sink at rest).
- **Ears** -- short chains under Head. Stiff, with `ImmobileType = WorldRotation` so head turns don't whip the ears around.
- **Hair** -- many medium chains under Head. Light gravity, auto-adds a head sphere collider sized from avatar height (Hips Y).
- **Dress / Skirt** -- many vertical chains under Hips. Heavier gravity, low pull, auto-adds capsule colliders along `LeftUpperLeg/LeftLowerLeg` and the Right counterparts so the cloth doesn't clip through the legs.
- **Generic** -- neutral defaults. Always available; auto-suggested only when no other preset matches.

**Selection analysis.** Bones are walked into chains: a bone whose parent is *not* in the selection becomes a chain root, and the chain extends through its first selected child until the chain ends. Each chain is summarised (root, tip, length in metres, dominant axis in avatar local space, side via [HumanoidSideMap](Architecture)). Aggregated across the selection: dominant side, average bone size, average chain length, nearest Humanoid bone the selection sits beneath. Presets read from this rather than the raw selection -- so adding a new preset is mostly "score the analysis, return a plan."

**Plan-then-apply.** Picking a preset generates a *plan* listing every PhysBone that would be added (with its parameter values) and every collider that would be created (with its shape, size, and host bone). Nothing mutates the project until *Apply* is clicked. Apply runs in a single Undo group -- `Ctrl+Z` reverts the entire setup.

**Auto-suggest.** Each preset returns a 0-1 score for the current selection ("under Head + 2 short chains = high score for Ears"). The highest-scoring preset is starred (*) in the picker and auto-selected on first scan, with a percentage bar so you can see how close the runner-up is.

**Adding presets.** Drop a new `IPhysBonePreset` implementation under `Editor/Tools/PhysBonePreset/Presets/` with a parameterless constructor -- the window auto-discovers it via reflection.

**Limitations:**

- Branching chains (a bone with multiple selected children) currently follow the first selected child only; siblings get treated as their own chains only if their parent isn't in the selection. Wider rigs (some skirt rigs) under-count chain branching.
- Requires VRChat SDK 3 (PhysBone). The window opens and previews plans without it, but *Apply* is disabled.

> _Screenshot: TODO_

## Bone Merger

**Where:** *Tools -> WhyKnot -> vrc-avatar-qol -> Bone Merger...*, or right-click an avatar in the hierarchy -> *WhyKnot -> vrc-avatar-qol -> Merge bones...*.

**What it does:** Collapses a stray duplicate bone (typical case: Blender / FBX export leaves a `Boob_L.001` sitting under `Boob_L`) onto the bone it should have been part of. Every skin weight on every `SkinnedMeshRenderer` under the avatar is redirected from the duplicate onto the keeper, then the duplicate bone is deleted from the rig.

**How it works:**

1. Pick the `Animator` at the avatar root.
2. Add one row per bone you want gone. Each row reads left to right: **merge this bone** -> **into this bone**.
3. *Apply merge.* For each `SkinnedMeshRenderer` under the Animator, weights on the LEFT bone are redirected to the RIGHT bone; duplicate slots on the same vertex collapse and re-sort by weight. The LEFT bone's children re-parent under the RIGHT bone (world transforms preserved), then the LEFT bone GameObject is destroyed.

**FBX safety.** Meshes that live as sub-assets of a model importer (`.fbx`, `.obj`, `.dae`, `.gltf`, `.glb`) are cloned to a fresh `.mesh` asset under `Assets/AvatarQol Generated/` before any write. The original model file is never touched -- re-importing the FBX brings back the unmodified mesh, and the cloned mesh on the renderer keeps the merged weights.

**Options:**

- **Delete the merged-away bone after applying** *(default on)* -- destroys the LEFT bone's GameObject once weights are transferred. Turn off if you just want to re-weight without changing the rig.
- **Re-parent its children onto the kept bone** *(default on, requires the above)* -- any GameObjects nested under the LEFT bone get moved onto the RIGHT bone with world transforms preserved, so PhysBones / colliders / accessories sitting under it stay in place. Without this, those children would be destroyed along with the bone.

**Validation.** The tool refuses to apply when:

- The same bone is listed as the "merge this bone" in two rows with **different** "into this bone" destinations -- ambiguous, pick one.
- The pair list contains a cycle (e.g. row 1: A -> B, row 2: B -> A) -- can't be sequenced without leaving orphan weights.

Exact duplicate rows (same LEFT and same RIGHT) are tolerated as redundant.

**Math caveat.** Weight redirection is visually identical to the original skinning only when the LEFT and RIGHT bones share the same rest pose -- which is the case for the typical "sub-bone authored at local zero under its parent" workflow. When the two bones have different rest positions or rotations, vertices that were skinned to the LEFT bone will snap to the RIGHT bone's frame at the rig's rest pose. That's inherent to linear blend skinning, not a tool bug.

**Undo.** The whole apply (every weight write, every re-parent, every bone deletion, plus the newly-created `.mesh` asset) is one Undo step. `Ctrl+Z` reverts everything including the cloned mesh files on disk.

**Limits:** The merge-into bone must already exist in each affected renderer's `bones[]` array. If it isn't (e.g. you're trying to merge into a bone the mesh has never been skinned to), the row is skipped for that mesh with a console-style warning in the result panel.

> _Screenshot: TODO_
