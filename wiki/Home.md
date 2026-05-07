# vrc-avatar-qol Wiki

Editor tools for VRChat avatars -- catches subtle issues that don't surface until they're already in your scene. Sibling repo to [vrcfury-qol](https://github.com/RealWhyKnot/vrcfury-qol): where vrcfury-qol lives next to VRCFury components, this repo is for general avatar QoL -- meshes, weights, bones, materials.

The [README](https://github.com/RealWhyKnot/vrc-avatar-qol/blob/main/README.md) is the quick-start; this wiki goes deeper.

## How it works (60 seconds)

The framework is small on purpose. Everything is built on Unity's public APIs (Animator/Humanoid, SkinnedMeshRenderer, Mesh) -- no third-party reflection -- so adding a tool is usually one self-contained `[InitializeOnLoad]` static class with one or more `[MenuItem]` entry points.

Two shared helpers in `Editor/`:

1. **`AvatarQol.cs`** -- tiny grab-bag of cross-tool utilities (e.g. hierarchy path formatting).
2. **`HumanoidSideMap.cs`** -- given a Humanoid Animator, classifies every Transform in its bone tree as Left / Right / Center / Unknown. Used by tools that reason about avatar symmetry. Walks up the parent chain to the nearest Humanoid bone, so custom bones (skirt panels, prop chains) inherit the side of their nearest Humanoid ancestor.

Each tool then picks what it needs from those helpers and otherwise stays independent.

## Read these first

- **[[Installation]]** -- drop-in instructions
- **[[Tools-Overview]]** -- every shipping tool, what it does, where to find it
- **[[Architecture]]** -- framework design + how each heuristic works
- **[[Adding-a-Tool]]** -- developer guide
- **[[Troubleshooting]]** -- common failure modes and false-positive scenarios

## What's in the box

- **Weight Sanity Check** -- flags vertices on one side of a humanoid avatar that have non-trivial weight from a bone on the other side. Catches the most common Blender weight-transfer mistake.

More tools to come.
