# Changelog

All notable changes to this project will be documented in this file. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-05-03

First release as a VRChat Package Manager (VPM) package, installable via the Creator Companion at `https://vpm.whyknot.dev/index.json`.

### Added
- VPM package metadata (`package.json`) declaring `dev.whyknot.avatar-qol` with a hard `vpmDependencies` on `com.vrchat.avatars` (≥ 3.5.0).
- Editor assembly definition (`Editor/dev.whyknot.avatar-qol.Editor.asmdef`) scoping the tools to the Editor platform and gating the SDK-conditional code via `versionDefines` for `VRC_SDK_VRCSDK3`.

### Changed
- **Breaking for loose-script users.** Prior to 1.0.0 the recommended install was to drop the `Editor/` folder anywhere under your `Assets/` tree; Unity compiled the scripts into the project's default editor assembly. With the new asmdef, code now compiles into a dedicated `dev.whyknot.avatar-qol.Editor` assembly. If you were previously importing as loose scripts and you upgrade by adding the asmdef in place, *internal* type references inside this package keep working, but any **external** code in your project that referenced these tools' types (e.g. `WhyKnot.AvatarQol.AvatarQol` from your own scripts) will need its asmdef to add `dev.whyknot.avatar-qol.Editor` to its `references`.
- Recommended migration: remove the old loose-script copy from `Assets/` and reinstall via VCC. Unity asset GUIDs are regenerated on import; nothing inside this package references its own files by GUID, so no project-side cleanup is required beyond removing the duplicate.

### Notes
- The `#if VRC_SDK_VRCSDK3` blocks in `PhysBonePlanApplier` and `PhysBonePresetWindow` are preserved verbatim. They are now effectively always-on (the package hard-depends on `com.vrchat.avatars`, which sets the define via `versionDefines`), but kept for future flexibility if the dependency is later relaxed.
