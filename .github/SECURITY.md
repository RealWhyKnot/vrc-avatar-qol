# Security Policy

## Reporting a vulnerability

If you find a security issue in this project, please **don't** file a public GitHub issue. Use GitHub's [private vulnerability reporting](https://github.com/RealWhyKnot/vrc-avatar-qol/security/advisories/new) form.

I'll acknowledge within a week and aim to release a fix or workaround within 30 days.

## Threat model summary

`vrc-avatar-qol` is a Unity Editor-only tool. It runs at user privilege inside the Unity Editor process and:

- **Does not make network requests.**
- **Does not load native code or shell out to external binaries.**
- **Does not require any elevated privileges** beyond what Unity already has on the user's machine.
- **Operates only on assets in the open Unity project** (scenes, prefabs, meshes, EditorPrefs).

In scope:
- Code execution paths in Editor scripts that could be triggered by data crafted into a scene or prefab (e.g. malicious mesh data on a third-party prefab).
- Any path that writes outside the open project.

Out of scope (won't be treated as security issues):
- Bugs that require an attacker to already have write access to your project files.
- Bugs in upstream dependencies (Unity). Report those upstream.
- "The tool flagged something that wasn't actually wrong" — file a normal issue.
