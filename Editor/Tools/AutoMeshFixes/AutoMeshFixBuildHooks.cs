using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.Tools.AutoMeshFixes {

    [InitializeOnLoad]
    internal sealed class AutoMeshFixBuildHooks :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        private static AutoMeshFixProcessor.Session _uploadSession;
        private static AutoMeshFixProcessor.Session _playModeSession;

        public int callbackOrder => -1026;

        static AutoMeshFixBuildHooks() {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            _uploadSession?.Restore();
            _uploadSession = null;

            var result = AutoMeshFixProcessor.ProcessAvatar(avatarGameObject, new AutoMeshFixProcessor.Options {
                Upload = true,
                Verbose = HasVerboseSetup(avatarGameObject),
            });
            if (result.ComponentsProcessed == 0) return true;

            if (!result.Success) {
                result.Session.Restore();
                LogResult("upload", result, LogType.Error);
                return false;
            }

            _uploadSession = result.Session;
            LogResult("upload", result, LogType.Log);
            return true;
        }

        public void OnPostprocessAvatar() {
            _uploadSession?.Restore();
            _uploadSession = null;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode) {
                ProcessForPlayMode();
            } else if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.ExitingPlayMode) {
                _playModeSession?.Restore();
                _playModeSession = null;
                _uploadSession?.Restore();
                _uploadSession = null;
            }
        }

        private static void ProcessForPlayMode() {
            _playModeSession?.Restore();
            _playModeSession = new AutoMeshFixProcessor.Session();

            var roots = FindPlayModeAvatarRoots();
            foreach (var root in roots) {
                var result = AutoMeshFixProcessor.ProcessAvatar(root, new AutoMeshFixProcessor.Options {
                    PlayMode = true,
                    Verbose = HasVerboseSetup(root),
                });
                if (result.ComponentsProcessed == 0) continue;

                if (!result.Success) {
                    result.Session.Restore();
                    LogResult("play mode", result, LogType.Error);
                    continue;
                }

                _playModeSession.Merge(result.Session);
                LogResult("play mode", result, LogType.Log);
            }
        }

        private static List<GameObject> FindPlayModeAvatarRoots() {
            var roots = new HashSet<GameObject>();
            foreach (var setup in Resources.FindObjectsOfTypeAll<AutoTightenToBody>()) {
                if (setup == null || !setup.enabled || !setup.processInPlayMode) continue;
                if (EditorUtility.IsPersistent(setup)) continue;
                if (!setup.gameObject.scene.IsValid() || !setup.gameObject.scene.isLoaded) continue;
                if (AutoMeshFixPreviewController.IsPreviewing &&
                    setup.transform.IsChildOf(AutoMeshFixPreviewController.PreviewAvatar.transform)) {
                    continue;
                }

                var animator = setup.GetComponentInParent<Animator>(true);
                roots.Add(animator != null ? animator.gameObject : TopLevel(setup.transform).gameObject);
            }
            return roots.ToList();
        }

        private static Transform TopLevel(Transform transform) {
            while (transform != null && transform.parent != null) transform = transform.parent;
            return transform;
        }

        private static bool HasVerboseSetup(GameObject avatarRoot) {
            if (avatarRoot == null) return false;
            foreach (var setup in avatarRoot.GetComponentsInChildren<AutoTightenToBody>(true)) {
                if (setup != null && setup.verboseLog) return true;
            }
            return false;
        }

        private static void LogResult(string context, AutoMeshFixProcessor.Result result, LogType type) {
            if (result == null || result.ComponentsProcessed == 0) return;

            var lines = new List<string> {
                $"[Avatar QoL] Auto Mesh Fixes {context}: {result.Summary}"
            };
            lines.AddRange(result.Warnings.Select(w => "  Warning: " + w));
            lines.AddRange(result.Errors.Select(e => "  Error: " + e));
            var text = string.Join("\n", lines);

            switch (type) {
                case LogType.Error:
                    Debug.LogError(text);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(text);
                    break;
                default:
                    Debug.Log(text);
                    break;
            }
        }
    }
}
