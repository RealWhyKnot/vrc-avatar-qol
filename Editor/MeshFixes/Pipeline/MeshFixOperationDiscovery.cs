// MeshFixOperationDiscovery.cs
//
// Walks the avatar tree once and expands each storage component into one
// or more IMeshOperation instances. Single dispatch point so adding a new
// op kind is a one-place change: extend the AutoTightenToBody expansion
// here (or add a new component type) and the pipeline picks it up.

using System.Collections.Generic;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Operations;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal static class MeshFixOperationDiscovery {

        public static IEnumerable<IMeshOperation> DiscoverOperations(GameObject avatarRoot) {
            if (avatarRoot == null) yield break;

            foreach (var setup in avatarRoot.GetComponentsInChildren<AutoTightenToBody>(true)) {
                if (setup == null || !setup.enabled) continue;
                if (setup.createGarmentTightenShape) {
                    yield return new GarmentTightenOp(setup);
                }
                if (setup.createBodyHideShape) {
                    yield return new BodyHideOp(setup);
                }
            }
        }
    }
}
