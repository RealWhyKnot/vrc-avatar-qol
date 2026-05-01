# Tools Overview

Every shipping tool, where to find it, what it does.

## Weight Sanity Check

**Where:** *Tools → Avatar QoL → Weight Sanity Check…*, or right-click an avatar in the hierarchy → *Avatar QoL → Check weights…* (pre-fills the picker with the selected Animator).

**What it does:** Detects the most common Blender-weight-transfer mistake on humanoid avatars: vertices on one side of the avatar (a left-leg garter, say) picking up non-trivial weight from a bone on the other side (right thigh). When the avatar moves, the bad vertices stretch or follow the wrong limb.

**How it works:**

1. Walks every `SkinnedMeshRenderer` under the picked `Humanoid Animator`.
2. For each vertex, transforms its bind-pose mesh-local position to world, then to Hips local space, and classifies it as **Left / Right / Center** along an axis derived from the actual position of `LeftUpperLeg` relative to `Hips`. (Inferring the axis from the rig means the tool doesn't care about Unity coordinate conventions — it works on any rig orientation.)
3. For each bone the renderer references, walks up its parent chain to the nearest Humanoid ancestor (LeftUpperLeg, RightUpperArm, etc.). The bone inherits the side of that ancestor — so custom bones added under a Humanoid bone (e.g. a skirt rig parented to LeftUpperLeg) are correctly tagged.
4. For each vertex, checks every bone weight above the threshold. If a Left-side vertex has weight from a Right-side bone (or vice versa), it's flagged.

**Tunables:**

- **Weight floor** *(default 0.01)* — weights below this are noise and skipped.
- **Center margin** *(default 0.02 m in Hips local space)* — vertices closer to the avatar's centerline than this aren't classified as either side. Avoids false flags on the spine.
- **Show gizmos in Scene view** — draws a red sphere at every flagged vertex's world position.
- **Exclude renderers** — opt-out list for meshes that legitimately bridge sides (capes, dresses, tails).

**Per-issue UI:** *Ping* selects the affected renderer; *Frame* moves the Scene view camera to the vertex.

**What it doesn't catch (yet):**

- Bone-graph distance violations (vertices weighted to bones very far apart in the hierarchy).
- Per-island weight variance.
- Path-only bone references (loose bones with no Humanoid ancestor).
- Static-mesh weighting issues — only `SkinnedMeshRenderer` is scanned.

> _Screenshot: TODO_
