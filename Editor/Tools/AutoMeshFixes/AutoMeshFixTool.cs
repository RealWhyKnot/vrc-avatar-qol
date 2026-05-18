using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.AutoMeshFixes {

    [InitializeOnLoad]
    internal static class AutoMeshFixTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/Open...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes...";

        static AutoMeshFixTool() { }

        [MenuItem(ToolsMenuPath, false, 2100)]
        private static void OpenFromToolsMenu() {
            AutoMeshFixWindow.Open();
        }

        [MenuItem(GameObjectMenuPath, false, 51)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            AutoMeshFixWindow.Open();
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
