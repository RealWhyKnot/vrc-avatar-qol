# Installation

`vrc-avatar-qol` ships as a VPM package (`dev.whyknot.avatar-qol`), Editor-only,
hard-depending on `com.vrchat.avatars` (>= 3.5.0). Unity compiles it into a
dedicated `dev.whyknot.avatar-qol.Editor` assembly and the `Editor/` folder
never leaks into runtime builds.

## VCC (recommended)

Add the WhyKnot VPM listing to the [VRChat Creator Companion](https://creators.vrchat.com/),
then this package shows up under **Manage Project -> Add Package**.

1. Click <https://vpm.whyknot.dev/>. The page redirects to a `vcc://` handler
   URL and VCC opens with the listing pre-filled. Click **I Understand, Add
   Repository**.
2. If that does not work, in VCC go to **Settings -> Packages -> Add Repository**,
   paste `https://vpm.whyknot.dev/index.json`, click **I Understand, Add
   Repository**.
3. Open any project, click **Manage Project**, find **Avatar QoL** in the package
   list, hit **Add**. VCC refuses to install without the VRChat Avatars SDK
   present.

## Manual install (non-VCC projects)

Download `dev.whyknot.avatar-qol-X.Y.Z.zip` from the
[latest release](https://github.com/RealWhyKnot/vrc-avatar-qol/releases/latest),
unzip into `Packages/dev.whyknot.avatar-qol/` so `Packages/dev.whyknot.avatar-qol/package.json`
exists, and Unity's Package Manager picks it up on next refresh.

## Beta / pre-release builds

Pre-release tags follow the form `vX.Y.Z-betaN` and ship as GitHub Releases
marked **Pre-release**. They are NOT picked up by the VPM listing automatically.

To pull a beta:
- Download the `dev.whyknot.avatar-qol-X.Y.Z-betaN.zip` from the
  [pre-releases page](https://github.com/RealWhyKnot/vrc-avatar-qol/releases?q=prerelease%3Atrue)
  and unzip into `Packages/dev.whyknot.avatar-qol/` as in Manual install above.
- Or, in VCC, use **Settings -> Packages -> Show Pre-release Packages**, then
  the version dropdown for Avatar QoL surfaces the pre-release builds.

Drop back to a release version by re-Adding from VCC's package list and
choosing a non-pre-release version.

## Per-clone development setup

For working on the package itself: clone the repo somewhere outside any Unity
project, then symlink (or junction) the package root into your test project's
`Packages/` folder so edits in the clone apply immediately to Unity.

**Windows (PowerShell, run as administrator):**
```powershell
New-Item -ItemType Junction `
  -Path "C:\Path\To\YourProject\Packages\dev.whyknot.avatar-qol" `
  -Target "C:\Path\To\vrc-avatar-qol"
```

**Linux / macOS:**
```sh
ln -s /path/to/vrc-avatar-qol \
      /path/to/YourProject/Packages/dev.whyknot.avatar-qol
```

Unity treats it as a local-disk package and recompiles on every script change.

## Compatibility

- **Unity 2022.3.x** -- primary target.
- **VRChat Avatars SDK 3.5+** -- hard dependency.
- **Humanoid avatars** -- the Weight Sanity Check needs a Humanoid Animator.
  Generic / non-Humanoid rigs are not supported by that specific tool;
  other tools have their own requirements (see [[Tools-Overview]]).

## Uninstalling

In VCC: **Manage Project** -> remove **Avatar QoL**. Manual installs: delete
the `Packages/dev.whyknot.avatar-qol/` folder. There are no persistent
EditorPrefs keys to clean up.
