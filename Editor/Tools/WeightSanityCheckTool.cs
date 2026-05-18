// WeightSanityCheckTool.cs
//
// Registers the entry points for the Weight Sanity Check window. The
// window itself lives in WeightSanityCheckWindow.cs.
//
// Two ways to open it:
//   1. Tools/WhyKnot/vrc-avatar-qol/Weight Sanity Check...
//   2. Right-click a GameObject with a Humanoid Animator in the hierarchy →
//      "WhyKnot/vrc-avatar-qol/Check weights..." — pre-fills the window
//      with that Animator.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class WeightSanityCheckTool {

        private const string ToolsMenuPath      = "Tools/WhyKnot/vrc-avatar-qol/Weight Sanity Check...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Check weights...";

        static WeightSanityCheckTool() { /* registration happens via [MenuItem] below */ }

        [MenuItem(ToolsMenuPath, false, 2000)]
        private static void OpenFromToolsMenu() {
            WeightSanityCheckWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 49)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Hierarchy menu callbacks fire once per selected GameObject;
            // bail for all but the first so we don't open N windows.
            if (command.context != Selection.activeGameObject) return;
            WeightSanityCheckWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            // Enabled when the selection (or one of its descendants) has a
            // Humanoid Animator. That's the contract the tool needs.
            var animator = go.GetComponentInChildren<Animator>(true);
            return animator != null && animator.isHuman;
        }
    }
}
