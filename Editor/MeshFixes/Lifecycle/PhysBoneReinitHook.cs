// PhysBoneReinitHook.cs
//
// Mid-play `renderer.sharedMesh = clone` is invisible to already-initialized
// VRCPhysBone components -- they snapshot their bone-transform list at
// Awake/Start. Without an explicit reinit pass, PhysBone motion on the
// affected renderers is undefined until the next domain reload.
//
// callbackOrder = 10000 places this hook well after every mesh-mutating
// pipeline (NDMF / VRCFury / Modular Avatar / d4rkAvatarOptimizer / us).
// We do NOT use int.MaxValue because Unity's IOrderedCallback sort has
// a documented bug with int.MaxValue / int.MinValue (Unity Issue Tracker,
// reproducible through 2021.1; status in 2022.3 unverified).
//
// Reflection is used to call InitTransforms because the VRCPhysBone type
// is defined in VRC.SDK3.Dynamics.PhysBone which we depend on, and the
// reflection target is a public method on a public type -- the only risk
// is the method being renamed across SDK versions, in which case we fail
// silent and PhysBones stay in their pre-swap state (no crash).

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace WhyKnot.AvatarQol.MeshFixes.Lifecycle {

    [InitializeOnLoad]
    internal sealed class PhysBoneReinitHook : IVRCSDKPreprocessAvatarCallback {

        public int callbackOrder => 10000;

        private static readonly HashSet<GameObject> _pendingRoots = new HashSet<GameObject>();
        private static MethodInfo _initTransformsMethod;
        private static bool _initTransformsProbed;

        static PhysBoneReinitHook() {
            EditorApplication.delayCall += FlushPlayModeReinits;
        }

        /// <summary>
        /// Mark an avatar root as needing a PhysBone reinit after a mesh swap.
        /// Called by MeshFixBuildHook on play-mode entry. Upload-time reinit
        /// runs via OnPreprocessAvatar in this same class.
        /// </summary>
        public static void RequestReinit(GameObject avatarRoot) {
            if (avatarRoot == null) return;
            _pendingRoots.Add(avatarRoot);
            EditorApplication.delayCall -= FlushPlayModeReinits;
            EditorApplication.delayCall += FlushPlayModeReinits;
        }

        private static void FlushPlayModeReinits() {
            if (_pendingRoots.Count == 0) return;
            foreach (var root in _pendingRoots) ReinitUnderRoot(root);
            _pendingRoots.Clear();
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            ReinitUnderRoot(avatarGameObject);
            return true;
        }

        private static void ReinitUnderRoot(GameObject root) {
            if (root == null) return;
            EnsureInitTransformsProbed();
            if (_initTransformsMethod == null) return;

            // PhysBone type lookup via name; tolerate SDK refactors.
            var components = root.GetComponentsInChildren(typeof(MonoBehaviour), true);
            foreach (var c in components) {
                if (c == null) continue;
                var t = c.GetType();
                if (!IsVrcPhysBoneType(t)) continue;
                try {
                    _initTransformsMethod.Invoke(c, null);
                } catch (Exception ex) {
                    Debug.LogWarning($"[Avatar QoL] PhysBone reinit on {c.name} failed: {ex.Message}", c);
                }
            }
        }

        private static bool IsVrcPhysBoneType(Type t) {
            if (t == null) return false;
            // Walk up the hierarchy so both VRCPhysBone and any subclasses are
            // matched. Match by simple-name to avoid a hard reference to the
            // PhysBone assembly's exact type identity (the assembly IS a
            // dependency, but the type name is the stable contract).
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType) {
                if (cur.Name == "VRCPhysBoneBase" || cur.Name == "VRCPhysBone") return true;
            }
            return false;
        }

        private static void EnsureInitTransformsProbed() {
            if (_initTransformsProbed) return;
            _initTransformsProbed = true;

            // Try the documented method name on the base type first.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type baseType;
                try { baseType = asm.GetType("VRC.Dynamics.VRCPhysBoneBase", false); }
                catch { continue; }
                if (baseType == null) continue;

                _initTransformsMethod = baseType.GetMethod("InitTransforms",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_initTransformsMethod != null) return;
            }
        }
    }
}
