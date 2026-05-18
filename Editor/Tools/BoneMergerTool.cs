// BoneMergerTool.cs
//
// Entry points for the Bone Merger window. The window itself lives in
// BoneMergerWindow.cs. Use this tool to fold a stray duplicate bone
// (typically a Blender ".001" sub-bone) into its kept counterpart: skin
// weights transfer onto the kept bone, the merged-away bone is removed.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class BoneMergerTool {

        private const string ToolsMenuPath      = "Tools/WhyKnot/vrc-avatar-qol/Bone Merger...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Merge bones...";

        static BoneMergerTool() { /* registration via [MenuItem] below */ }

        [MenuItem(ToolsMenuPath, false, 2002)]
        private static void OpenFromToolsMenu() {
            BoneMergerWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 51)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Hierarchy menu callbacks fire once per selected GameObject;
            // bail for all but the first so we don't open N windows.
            if (command.context != Selection.activeGameObject) return;
            BoneMergerWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            // Enabled when the selection sits under, or contains, an Animator
            // or any SkinnedMeshRenderer. The window can prefill the Animator
            // from either direction.
            if (go.GetComponentInParent<Animator>(true) != null) return true;
            return go.GetComponentInChildren<Animator>(true) != null ||
                   go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }
    }
}
