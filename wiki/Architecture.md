# Architecture

The framework is small on purpose. There's no third-party reflection (unlike sister project [vrcfury-qol](https://github.com/RealWhyKnot/vrcfury-qol)), so the framework is just a place to keep cross-tool helpers and a consistent menu structure. Each tool is otherwise self-contained.

## Directory layout

```
Editor/
  AvatarQol.cs                         shared utilities (path formatting, etc.)
  HumanoidSideMap.cs                   Humanoid Left/Right/Center bone tagging
  Tools/
    WeightSanityCheckTool.cs           menu entry points
    WeightSanityCheckWindow.cs         the window + scan logic
```

## Tool registration

Tools register entry points via Unity's standard `[MenuItem]` attributes. The framework doesn't impose a registration API — it would be over-engineered for the current scope.

Convention: every tool's top-level menu entry lives under `Tools/Avatar QoL/<Name>...`. If the tool has a sensible right-click trigger, it also registers `GameObject/Avatar QoL/<Action>...` (visible in the hierarchy right-click menu, fires once per multi-select).

## HumanoidSideMap

The most reusable bit of the framework. Given an `Animator` with `isHuman == true`, it builds:

- A `Transform → HumanBodyBones` reverse map (one entry per bound Humanoid bone).
- A `Transform → BoneSide` cache (memoised on first query).
- The avatar's "left" sign in Hips local space, derived from the actual position of `LeftUpperLeg` relative to `Hips`. This means we don't have to assume any specific Unity coordinate convention — a vertex is on the avatar's left iff `sign(hipsLocalPos.x) == LeftSignInHipsLocal`.

Side resolution walks the parent chain of any queried Transform until it hits a Humanoid bone, then reads the side off the bone's name (`Left*` / `Right*` → side; everything else, including Hips/Spine/Chest/Neck/Head/etc. → Center). The result is cached, so repeated lookups during a vertex-walk are O(1) after the first hit.

`ClassifyWorldPosition(worldPos, centerMargin)` extends the same logic to a free-floating world position: transform into Hips local space, project onto the left axis, return Left/Right/Center based on a configurable margin around the centerline.

## Weight Sanity Check heuristic

Three steps for each renderer:

1. **Bone tagging.** For every entry in `SkinnedMeshRenderer.bones`, query `HumanoidSideMap.GetSide(bone)`. Cache into a `BoneSide[]` parallel to the bones array. Skip the renderer entirely if it has no Left or no Right bones (e.g. a head-only mesh — nothing to cross-contaminate).

2. **Vertex classification.** Iterate `Mesh.vertices` (bind-pose, mesh local space). For each vertex, transform to world via the renderer's transform, then to Hips local space. Classify as Left/Right/Center.

3. **Cross-side detection.** Iterate weights via `Mesh.GetAllBoneWeights()` + `Mesh.GetBonesPerVertex()` (the modern many-bone-per-vertex API, supports >4 weights). For each weight above the floor, look up the bone's pre-computed side. If vertex side is Left/Right and the bone's side is the opposite, flag the issue.

Center-banded vertices are deliberately not flagged: it's normal for spine vertices to have small bleed from arm bones, and we'd produce too many false positives.

## Why bind-pose, not the live skinned mesh?

Skinning is what we're checking — using the deformed mesh would be circular. The bind-pose vertex position is also the only stable spatial input we have when a scene first opens (no animation has played). It's accurate enough to classify which side of the avatar a vertex belongs to, which is all we need.

## Undo and SetDirty

The Weight Sanity Check is read-only — it never mutates project state. If you add a tool that does (e.g. a "zero out cross-side weights" fixer), wrap operations in:

```csharp
var group = Undo.GetCurrentGroup();
Undo.SetCurrentGroupName("Avatar QoL: ...");
try {
    Undo.RegisterCompleteObjectUndo(target, "...");
    // ... mutate ...
    EditorUtility.SetDirty(target);
    Undo.CollapseUndoOperations(group);
} catch {
    Undo.RevertAllInCurrentGroup();
    throw;
}
```

Same pattern as vrcfury-qol uses.
