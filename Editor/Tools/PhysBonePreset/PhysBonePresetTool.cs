// PhysBonePresetTool.cs
//
// Registers the entry points for the PhysBone preset window. The window
// itself lives in PhysBonePresetWindow.cs.
//
// Two ways to open it:
//   1. Tools/Avatar QoL/Apply PhysBone Preset...     (top-level menu)
//   2. Right-click bones in the hierarchy → Avatar QoL/Apply PhysBone preset...
//      (pre-fills the window with the selected Transforms)

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class PhysBonePresetTool {

        private const string ToolsMenuPath      = "Tools/Avatar QoL/Apply PhysBone Preset...";
        private const string GameObjectMenuPath = "GameObject/Avatar QoL/Apply PhysBone preset...";

        static PhysBonePresetTool() { /* registration via [MenuItem] below */ }

        [MenuItem(ToolsMenuPath, false, 2010)]
        private static void OpenFromToolsMenu() {
            PhysBonePresetWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 49)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Hierarchy menu callbacks fire once per selected GameObject;
            // bail for all but the first so we don't open N windows.
            if (command.context != Selection.activeGameObject) return;
            PhysBonePresetWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            // Enable when at least one GameObject is selected. We don't
            // require a Humanoid Animator here because some presets work on
            // any rig; the window itself will warn if SDK is missing or
            // the rig is unsuitable for the chosen preset.
            return Selection.gameObjects != null && Selection.gameObjects.Length > 0;
        }
    }
}
