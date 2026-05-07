# Tools Overview

Every shipping tool, where to find it, what it does.

## Weight Sanity Check

**Where:** *Tools -> Avatar QoL -> Weight Sanity Check...*, or right-click an avatar in the hierarchy -> *Avatar QoL -> Check weights...* (pre-fills the picker with the selected Animator).

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

**Preview button.** Each issue has a *Preview* button that picks up the offending bone and wobbles it back and forth around its rest pose (+/-30 deg on local X, +/-18 deg on local Z, sinusoidal). The Scene view repaints continuously so you can watch the mesh deform -- exactly the motion that exposes the bad weight in animation. Click *Preview* again on the same bone (or *Stop preview* in the scan bar) to restore.

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

## PhysBone Preset (early)

**Where:** *Tools -> Avatar QoL -> Apply PhysBone Preset...*, or right-click a selection of bones in the hierarchy -> *Avatar QoL -> Apply PhysBone preset...* (pre-fills the bone list).

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
