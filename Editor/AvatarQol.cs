// AvatarQol.cs
// Shared utilities for the vrc-avatar-qol Editor tools.
//
// This namespace is intentionally small — unlike vrcfury-qol's sister
// framework, none of these tools need to reflect into a third-party
// package's internals. Everything here is built on Unity's public APIs
// (Animator/Humanoid, SkinnedMeshRenderer, Mesh) so the framework is just
// a tidy place to keep cross-tool helpers.
//
// Each tool lives in Editor/Tools/<Something>Tool.cs (entry points) and,
// when it has its own UI, Editor/Tools/<Something>Window.cs.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol {

    internal static class AvatarQol {

        /// <summary>
        /// "Root/Parent/Child"-style hierarchy path for a GameObject.
        /// </summary>
        internal static string GetGameObjectPath(GameObject go) {
            if (go == null) return "(null)";
            var parts = new List<string>();
            var t = go.transform;
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
