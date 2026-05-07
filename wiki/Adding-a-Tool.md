# Adding a Tool

A tool is a small `[InitializeOnLoad]` static class with one or more `[MenuItem]` entry points. The framework provides shared utilities (path formatting, Humanoid side mapping) but otherwise stays out of your way.

The existing tools in `Editor/Tools/` are short -- borrow freely. This page is the worked-example reference for the conventions.

## Skeleton

```csharp
// Editor/Tools/MyTool.cs
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class MyTool {

        private const string ToolsMenuPath      = "Tools/Avatar QoL/My Tool...";
        private const string GameObjectMenuPath = "GameObject/Avatar QoL/Run my tool...";

        static MyTool() { /* registration via [MenuItem] below */ }

        [MenuItem(ToolsMenuPath, false, 2000)]
        private static void OpenFromToolsMenu() {
            MyToolWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 49)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Hierarchy menu callbacks fire once per selected GameObject;
            // bail for all but the first so we don't open N windows.
            if (command.context != Selection.activeGameObject) return;
            MyToolWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            // Enable only when the selection is meaningful for this tool.
            // ...
            return true;
        }
    }
}
```

## Companion window

Most tools want their own UI:

```csharp
// Editor/Tools/MyToolWindow.cs
internal sealed class MyToolWindow : EditorWindow {
    [SerializeField] private Animator _animator;

    internal static void Open(bool prefillFromSelection) {
        var w = GetWindow<MyToolWindow>(false, "My Tool", true);
        w.titleContent = new GUIContent("Avatar QoL -- My Tool");
        if (prefillFromSelection) {
            // populate from Selection
        }
        w.Show();
        w.Focus();
    }

    private void OnGUI() {
        // IMGUI in here.
    }
}
```

`[SerializeField]` fields survive domain reloads -- use them for any user input you want to persist while the window is open.

## Conventions

- **Menu paths.** Top-level: `Tools/Avatar QoL/<Name>...`. Hierarchy right-click: `GameObject/Avatar QoL/<Action>...` with priority `49` (places the entry just above Unity's "Center On Children" group).
- **Validators.** For hierarchy menu items, always pair with a `[MenuItem(..., true)]` validator that disables the entry when the selection isn't a fit. Saves users from clicking through to an error dialog.
- **Read-only by default.** Tools that only inspect / report don't need Undo. Tools that mutate state must wrap operations in `Undo.SetCurrentGroupName` + `Undo.CollapseUndoOperations` so `Ctrl+Z` reverts the whole operation.
- **No `[CustomEditor]` overrides.** Stay out of Unity's component drawers -- tools should open a window or print to console, not hijack the inspector. (vrcfury-qol uses an inspector overlay because it has to coexist with VRCFury's own drawer; this repo doesn't have that constraint.)
- **Defensive scans.** Tools that walk a lot of geometry should use the modern `Mesh.GetAllBoneWeights()` / `GetBonesPerVertex()` APIs (support >4 bones per vertex) and check `mesh.isReadable` before reading vertices on imported assets.

## Using HumanoidSideMap

If your tool reasons about avatar symmetry, `HumanoidSideMap` already does the bone classification:

```csharp
var sideMap = new HumanoidSideMap(animator);
if (!sideMap.IsValid) { /* not Humanoid or Hips missing */ return; }

// Classify a Transform (bone or descendant)
var side = sideMap.GetSide(someBone);   // BoneSide.Left / Right / Center / Unknown

// Classify a world position (e.g. a vertex)
var vertSide = sideMap.ClassifyWorldPosition(worldPos, centerMargin: 0.02f);
```

Side resolution walks the parent chain to the nearest Humanoid bone and caches the result, so repeated calls during a vertex walk are O(1).

## Performance tips

- Cache anything per-renderer outside the per-vertex loop (bone-side array, transform, etc).
- For meshes with hundreds of thousands of vertices, the per-vertex `Transform.TransformPoint` is the dominant cost. If it ever shows up as a real bottleneck, switch to a precomputed `localToWorldMatrix` and `MultiplyPoint3x4`.
- The Humanoid side cache is per-`HumanoidSideMap` instance -- create one per scan, not per call.
