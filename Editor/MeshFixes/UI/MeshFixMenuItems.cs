// MeshFixMenuItems.cs
//
// Menu-bar and right-click entry points for the mesh fix pipeline.
// Kept in a dedicated file so the menu paths are easy to find and the
// window class stays focused on UI.
//
// Menu strings are stable across the rewrite -- they were already
// "Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/..." before this
// pipeline landed, so muscle memory and external docs keep working.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.UI {

    internal static class MeshFixMenuItems {

        private const string ToolsMenuPath = "Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/Open...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes...";

        [MenuItem(ToolsMenuPath, false, 2100)]
        private static void OpenFromToolsMenu() => MeshFixWindow.Open();

        [MenuItem(GameObjectMenuPath, false, 51)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Hierarchy menu callbacks fire once per selected GameObject;
            // bail for all but the first so we do not open N windows.
            if (command.context != Selection.activeGameObject) return;
            MeshFixWindow.Open();
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            return go.GetComponentInParent<Animator>(true) != null ||
                   go.GetComponentInChildren<Animator>(true) != null ||
                   go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }
    }
}
