# Installation

`vrc-avatar-qol` ships as a flat folder of `.cs` files compiled by Unity itself. There's no asmdef, no package manifest, no native binaries.

## Option A -- Drop into your project (simplest)

Copy the `Editor/` folder into your Unity project under any path that ends in (or contains) `Editor/`.

```
Assets/
  YourFolder/
    Editor/
      AvatarQol.cs
      HumanoidSideMap.cs
      Tools/
        WeightSanityCheckTool.cs
        WeightSanityCheckWindow.cs
```

## Option B -- Symlink for live development

Clone the repo somewhere outside your Unity project, then symlink the `Editor/` folder into the project. Edits in the repo apply to the live project without copy-pasting.

**Windows (PowerShell, run as admin):**
```powershell
New-Item -ItemType Junction -Path "C:\Path\To\YourProject\Assets\AvatarQol" -Target "C:\Path\To\vrc-avatar-qol\Editor"
```

**Linux / macOS:**
```sh
ln -s /path/to/vrc-avatar-qol/Editor /path/to/YourProject/Assets/AvatarQol
```

## After installing

Focus Unity once so it compiles the new scripts. The Tools menu and the GameObject right-click menu both pick up the new entries automatically.

## Compatibility

- **Unity 2022.3.x** -- primary target. Older versions may work but aren't tested.
- **Humanoid avatars** -- the Weight Sanity Check needs a Humanoid Animator. Generic / non-Humanoid rigs aren't supported by that tool (other tools may have different requirements; see [[Tools-Overview]]).

## Uninstalling

Delete the folder you installed into. There are no persistent settings or `EditorPrefs` keys to clean up.
