// PhysBoneClippingRiskTool.cs
//
// Entry points for the standalone PhysBone clipping risk window. This scan is
// intentionally separate from Weight Sanity Check because it can be much more
// expensive and most users only need it for one mesh at a time.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class PhysBoneClippingRiskTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/vrc-avatar-qol/PhysBone Clipping Risks...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Check PhysBone clipping...";

        static PhysBoneClippingRiskTool() { }

        [MenuItem(ToolsMenuPath, false, 2001)]
        private static void OpenFromToolsMenu() {
            PhysBoneClippingRiskWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 50)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            PhysBoneClippingRiskWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            if (go.GetComponentInParent<Animator>(true) != null) return true;
            return go.GetComponentInChildren<Animator>(true) != null ||
                   go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }
    }
}
