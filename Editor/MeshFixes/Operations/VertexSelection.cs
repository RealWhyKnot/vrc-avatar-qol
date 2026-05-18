// VertexSelection.cs
//
// Helpers shared by ops that honor AutoTightenToBody.selectionMode.
// Lives in the Operations namespace alongside its only callers; the
// runtime VertexSelectionMode enum is in the Components namespace.

using UnityEngine;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.MeshFixes.Operations {

    internal static class VertexSelection {

        public static bool IsSelected(Mesh mesh, VertexSelectionMode mode, int vertexIndex) {
            if (mode == VertexSelectionMode.AllVertices) return true;
            if (mesh == null) return false;
            var colors = mesh.colors32;
            if (colors == null || colors.Length != mesh.vertexCount || vertexIndex < 0 || vertexIndex >= colors.Length) return false;
            var c = colors[vertexIndex];
            switch (mode) {
                case VertexSelectionMode.VertexColorRed:   return c.r > 16;
                case VertexSelectionMode.VertexColorGreen: return c.g > 16;
                case VertexSelectionMode.VertexColorBlue:  return c.b > 16;
                case VertexSelectionMode.VertexColorAlpha: return c.a > 16;
                default: return true;
            }
        }
    }
}
