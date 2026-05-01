# vrc-avatar-qol

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Unity](https://img.shields.io/badge/Unity-2022.3-000000.svg?logo=unity)](https://unity.com/)

Editor tools for VRChat avatars — catches subtle issues that don't surface until they're already in your scene. Sibling repo to [vrcfury-qol](https://github.com/RealWhyKnot/vrcfury-qol): where vrcfury-qol lives next to VRCFury components, this repo is for general avatar QoL — meshes, weights, bones, materials.

The framework is small on purpose. Everything is built on Unity's public APIs (Animator/Humanoid, SkinnedMeshRenderer, Mesh) — no third-party reflection — so adding a tool is usually one self-contained `[InitializeOnLoad]` static class.

## What's in the box

- **Weight Sanity Check.** Detects the most common Blender-weight-transfer mistake on humanoid avatars: vertices on one side of the avatar (a left-leg garter, say) picking up non-trivial weight from a bone on the other side (right thigh). When you spread the legs the bad vertices stretch or follow the wrong limb. The tool walks every SkinnedMeshRenderer under a Humanoid Animator, classifies each vertex's bind-pose position as Left / Right / Center using the avatar's actual bone geometry (so it doesn't depend on Unity coordinate convention), classifies each weighted bone by walking up to its nearest Humanoid ancestor *or* — for custom rig bones with no Humanoid parent — by their pivot's spatial position. Per-issue Ping + Frame buttons, a *Preview* button that wobbles the offending bone in the Scene view so you can see exactly how the mesh deforms, an optional Scene-view gizmo overlay, a *Verbose log* mode that dumps per-renderer scan stats to the console (so it's possible to tell *why* a weight wasn't flagged), and a *Dump weights for selection* debug button for surgical inspection. *(Tools → Avatar QoL → Weight Sanity Check…, or right-click an avatar in the hierarchy → Avatar QoL → Check weights…)*
- **PhysBone Preset (early).** A library of smart presets that set up VRChat PhysBones on a selection of bones. Built-in presets: Tail, Ears, Hair, Dress/Skirt, plus a Generic fallback. Each preset reads a structural analysis of the selection (chain count, bone count per chain, average bone size, dominant orientation in avatar local space, nearest Humanoid ancestor) and adapts its parameters — gravity scales with verticality, spring loosens on longer chains, hair adds a head sphere collider sized to the avatar, dresses auto-add capsule colliders along the legs. Auto-suggests the best-fit preset on selection; preview-then-apply UI shows exactly what will be created before any mutation. *(Tools → Avatar QoL → Apply PhysBone Preset…, or right-click bones in the hierarchy → Avatar QoL → Apply PhysBone preset…)*

More tools to come — see the [wiki](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki) for the long-form docs and roadmap.

## Installation

Drop the `Editor/` folder into your Unity project under any path that ends in (or contains) `Editor/`. Unity compiles it as an editor-only assembly automatically.

```
Assets/
  YourFolder/
    Editor/
      AvatarQol.cs
      HumanoidSideMap.cs
      Tools/
        WeightSanityCheckTool.cs
        WeightSanityCheckWindow.cs
        PhysBonePreset/
          IPhysBonePreset.cs
          BoneSelectionAnalysis.cs
          PhysBonePlanApplier.cs
          PhysBonePresetTool.cs
          PhysBonePresetWindow.cs
          Presets/
            GenericPreset.cs
            EarsPreset.cs
            TailPreset.cs
            HairPreset.cs
            DressPreset.cs
```

No asmdef, no dependencies. Tested on Unity **2022.3**.

For per-clone setup (symlink-for-development, etc.) see the [Installation](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Installation) wiki page.

## Adding your own tool

A tool is a small `[InitializeOnLoad]` static class with one or more `[MenuItem]` entry points. The framework provides shared utilities (path formatting, Humanoid side mapping) but otherwise stays out of your way. The full developer guide lives at [Adding a Tool](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Adding-a-Tool).

## Documentation

- [Wiki home](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki) — long-form docs
- [Tools Overview](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview) — every shipping tool
- [Architecture](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Architecture) — framework design + heuristics
- [Adding a Tool](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Adding-a-Tool) — developer guide
- [Troubleshooting](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Troubleshooting) — common failure modes

## Heuristic, not exhaustive

Weight checks are heuristics on top of bone geometry. They catch the *common* failure modes (cross-side bleed) but won't catch every weighting mistake — and they can produce false positives on meshes that legitimately bridge sides (capes, dresses, tails). Use the per-renderer exclusion list to silence those. Always:

1. Treat results as "look here" hints, not "fix this exactly."
2. Commit your project to version control before doing anything destructive based on a scan.
3. Tune the weight floor + center margin to your avatar's geometry.

## Contributing

Bug reports, feature requests, and pull requests are all welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the dev loop and PR conventions.

## License

MIT — see [LICENSE](LICENSE).
