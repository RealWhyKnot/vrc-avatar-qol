# Changelog

All notable changes to this project will be documented in this file. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [Semantic Versioning](https://semver.org/).

<!-- Entries under "## Unreleased" are appended automatically by the changelog-append GitHub
     workflow on every push to main, then promoted to the versioned section by release.yml when
     a tag is cut. Don't hand-edit Unreleased -- your edits will be overwritten on the next push.
     To override an entry, amend the commit subject before merge. -->

## Unreleased

### Changed
- **deps:** Bump actions/checkout from 4 to 6 (#1) (0d8ab2b)
- Mesh Fix pipeline: fix native-array leak, missing using, idempotent delayCall, preview leak; only clone write-target meshes (31d1746)
- Editor asmdef: gate WHYKNOT_NDMF on nadena.dev.ndmf >=1.0.0; qualify Object disambiguation (5785f9d)
- Surrounding tools: WeightFixer handshake; Clipping Keep/Merge/Overwrite (3221b6d)
- Mesh Fix pipeline: redesign Auto Mesh Fixes around plan/apply with shape registry (e3b5885)
- Add Auto Mesh Fixes, PhysBone Clipping Risks, Bone Merger; namespace tools under WhyKnot/vrc-avatar-qol (1471d75)

---

## [1.0.1](https://github.com/RealWhyKnot/vrc-avatar-qol/releases/tag/v1.0.1) -- 2026-05-07

### Changed
- License: switched from MIT to GPL-3.0-or-later. Same set of users can use, modify, and redistribute; downstream forks now propagate the GPL terms instead of MIT's permissive ones.
- Repo infra: auto-maintained `CHANGELOG.md` via verified bot commits on every push to `main` (conventional-commit subjects bucket into Added/Changed/Fixed). Branch protection ruleset on `main` now requires signed commits.

---

## [1.0.0](https://github.com/RealWhyKnot/vrc-avatar-qol/releases/tag/v1.0.0) -- 2026-05-03

First release as a VRChat Package Manager (VPM) package, installable via the Creator Companion at `https://vpm.whyknot.dev/index.json`.

### Added
- VPM package metadata (`package.json`) declaring `dev.whyknot.avatar-qol` with a hard `vpmDependencies` on `com.vrchat.avatars` (>= 3.5.0).
- Editor assembly definition (`Editor/dev.whyknot.avatar-qol.Editor.asmdef`) scoping the tools to the Editor platform and gating the SDK-conditional code via `versionDefines` for `VRC_SDK_VRCSDK3`.

### Changed
- **Breaking for loose-script users.** Prior to 1.0.0 the recommended install was to drop the `Editor/` folder anywhere under your `Assets/` tree; Unity compiled the scripts into the project's default editor assembly. With the new asmdef, code now compiles into a dedicated `dev.whyknot.avatar-qol.Editor` assembly. If you were previously importing as loose scripts and you upgrade by adding the asmdef in place, *internal* type references inside this package keep working, but any **external** code in your project that referenced these tools' types (e.g. `WhyKnot.AvatarQol.AvatarQol` from your own scripts) will need its asmdef to add `dev.whyknot.avatar-qol.Editor` to its `references`.
- Recommended migration: remove the old loose-script copy from `Assets/` and reinstall via VCC. Unity asset GUIDs are regenerated on import; nothing inside this package references its own files by GUID, so no project-side cleanup is required beyond removing the duplicate.

### Notes
- The `#if VRC_SDK_VRCSDK3` blocks in `PhysBonePlanApplier` and `PhysBonePresetWindow` are preserved verbatim. They are now effectively always-on (the package hard-depends on `com.vrchat.avatars`, which sets the define via `versionDefines`), but kept for future flexibility if the dependency is later relaxed.
