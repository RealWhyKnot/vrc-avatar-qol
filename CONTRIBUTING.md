# Contributing

Welcome, and thanks for taking an interest. Bug reports, feature requests, and pull requests are all welcome — open an issue or PR against this repo.

## Before you start

- Skim the [Architecture](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Architecture) wiki page for the framework conventions.
- For a bug report, check the [Troubleshooting](https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Troubleshooting) page first.

## Setting up the dev loop

There's no build system here — `vrc-avatar-qol` is a flat folder of `.cs` files compiled by Unity itself.

**Prerequisites:**

- Unity 2022.3.x
- A Unity project with a Humanoid avatar to test against
- git

**Recommended layout:** clone the repo somewhere outside your Unity project, then symlink the `Editor/` folder into the project under any `Assets/...Editor/` path. Edits to the repo apply to the live project without copy-pasting.

Windows (PowerShell, run as admin):
```powershell
New-Item -ItemType Junction -Path "C:\Path\To\YourProject\Assets\AvatarQol" -Target "C:\Path\To\vrc-avatar-qol\Editor"
```

Linux / macOS:
```sh
ln -s /path/to/vrc-avatar-qol/Editor /path/to/YourProject/Assets/AvatarQol
```

Focus Unity once after the first install so it compiles the scripts.

## Editing the wiki

The wiki is **source-controlled at `wiki/`** in this repo. That means:

- Edits go through normal PR review, same as code.
- **Do not edit on the github.com Wiki UI.** Web edits get overwritten the next time the sync workflow runs.
- On every push to `main` that touches `wiki/**`, the [wiki-sync workflow](.github/workflows/wiki-sync.yml) mirrors the changes to the GitHub Wiki repo.

**One-time wiki bootstrap.** GitHub doesn't create the wiki repo until a maintainer creates the first page through the web UI. If you see a `wiki repo doesn't exist yet` warning in the wiki-sync workflow, visit `https://github.com/RealWhyKnot/vrc-avatar-qol/wiki` and click **Create the first page** with any content (it'll be overwritten on the next sync). After that, every push that touches `wiki/**` syncs automatically.

## Submitting a PR

- Branch from `main`. Open the PR against `main`.
- The [PR template](.github/PULL_REQUEST_TEMPLATE.md) auto-populates the description. Fill the checklist honestly — particularly the "compiles in Unity 2022.3.x with no console errors" item.
- **Touched a tool?** Update or add the corresponding section in [`wiki/Tools-Overview.md`](wiki/Tools-Overview.md). UI changes deserve a screenshot.
- **Heuristic changes?** Note any tradeoffs in the PR description (false-positive vs false-negative shifts, runtime cost on big meshes).
- Keep PRs focused. Mixing unrelated changes makes review harder for everyone.

## Code review expectations

- Be ready to iterate. Expect at least one review pass on anything non-trivial.
- Tools that mutate scene/asset state must wrap operations in a single Undo group (`Undo.SetCurrentGroupName` + `Undo.CollapseUndoOperations`).

## Commit message style

- Conventional-ish prefixes are appreciated but not enforced: `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `ci:`.
- Keep the subject ≤72 characters.
- The body is for the *why*. The diff already shows the *what*.

## Reporting security issues

Please don't file a public issue for a security vulnerability. Use GitHub's **Security tab → Report a vulnerability** for a private disclosure. See [SECURITY.md](.github/SECURITY.md) for details.
