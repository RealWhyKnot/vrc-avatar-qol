// MeshFixBuildHook.cs
//
// Runs the MeshFixPipeline at three lifecycle points:
//
//   - IVRCSDKPreprocessAvatarCallback (Build & Publish upload)
//   - EditorApplication.playModeStateChanged.ExitingEditMode (Play-mode entry)
//   - IVRCSDKPostprocessAvatarCallback (post-upload session restore)
//
// callbackOrder = -5000 places us well before NDMF's Optimizing phase at
// -1025 and before VRCFury's PreuploadHook (< -1025, exact integer not
// publicly indexed). VRCSDK strips IEditorOnly components at
// RemoveAvatarEditorOnly which runs just after -1025; we MUST run earlier.
//
// Why ExitingEditMode and NOT EnteredPlayMode for play-mode mesh swaps:
// Unity docs: "Because this event is synchronized with the editor
// application's update loop, [EnteredPlayMode] may occur after the game's
// update loop has already executed one or more times." Mesh swaps that
// must be visible to Animator/PhysBones/cloth from frame 1 cannot land
// any later than ExitingEditMode.
//
// Per-avatar session isolation: a separate MeshFixSession per processed
// avatar root, kept in a Dictionary keyed by GameObject. A partial
// failure on one avatar only unwinds that avatar; other avatars in the
// same scene remain processed.
//
// Idempotent event subscription: static ctor re-runs on every domain
// reload; unsubscribe-then-subscribe prevents handler accumulation when
// Domain Reload is disabled.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Pipeline;

namespace WhyKnot.AvatarQol.MeshFixes.Lifecycle {

    [InitializeOnLoad]
    internal sealed class MeshFixBuildHook :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        public int callbackOrder => -5000;

        private static readonly Dictionary<GameObject, MeshFixSession> _uploadSessions =
            new Dictionary<GameObject, MeshFixSession>();
        private static readonly Dictionary<GameObject, MeshFixSession> _playModeSessions =
            new Dictionary<GameObject, MeshFixSession>();
        private static GameObject _activeUploadAvatar;

        static MeshFixBuildHook() {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // ---- Upload path -------------------------------------------------

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (avatarGameObject == null) return true;

            // Defensive: if a prior upload exception bypassed
            // OnPostprocessAvatar, leftover sessions still hold the avatar's
            // original meshes. Unwind anything matching this root before we
            // start a fresh pipeline against it.
            DisposeSession(_uploadSessions, avatarGameObject);

            MeshFixSessionState.SetUploadActive(true);
            _activeUploadAvatar = avatarGameObject;

            var controller = avatarGameObject.GetComponentInChildren<WhyKnotMeshFixController>(true);
            if (controller != null && !controller.enableAll) return true;

            bool verbose = ResolveVerbose(avatarGameObject, controller);
            var result = MeshFixPipeline.Run(avatarGameObject, new MeshFixPipeline.RunOptions {
                Mode = MeshFixMode.Upload,
                Verbose = verbose,
                OwnerGate = obj => OwnerGateForUpload(obj),
            });

            if (result.OpsApplied == 0 && result.OpsSkipped == 0) {
                result.Session.Dispose();
                MeshFixSessionState.SetUploadActive(false);
                _activeUploadAvatar = null;
                return true;
            }

            if (result.BuildShouldAbort || !result.Success) {
                result.Session.Dispose();
                MeshFixSessionState.SetUploadActive(false);
                _activeUploadAvatar = null;
                LogResult("upload", result, LogType.Error);
                return false;
            }

            _uploadSessions[avatarGameObject] = result.Session;
            LogResult("upload", result, LogType.Log);
            return true;
        }

        public void OnPostprocessAvatar() {
            // No avatar handle on this side; clean up whatever we held during
            // the upload that just finished. _activeUploadAvatar was set in
            // OnPreprocessAvatar; if it is null, OnPreprocessAvatar found
            // nothing to do for this build.
            if (_activeUploadAvatar != null) {
                DisposeSession(_uploadSessions, _activeUploadAvatar);
                _activeUploadAvatar = null;
            } else {
                // Belt-and-braces: dispose every retained upload session in
                // case the SDK invoked OnPostprocessAvatar without us having
                // recorded an active avatar.
                DisposeAllSessions(_uploadSessions);
            }
            MeshFixSessionState.SetUploadActive(_uploadSessions.Count > 0);
        }

        private static bool OwnerGateForUpload(Object owner) {
            return owner is AutoTightenToBody setup && setup.processOnUpload;
        }

        // ---- Play-mode path ----------------------------------------------

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode) {
                ProcessForPlayMode();
            } else if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.ExitingPlayMode) {
                DisposeAllSessions(_playModeSessions);
                MeshFixSessionState.SetPlayModeActive(false);
                // Also defensively unwind any leaked upload sessions on
                // play-mode exit -- an aborted Build & Publish that never
                // reached OnPostprocessAvatar would otherwise leave clones
                // pinned in memory.
                DisposeAllSessions(_uploadSessions);
                _activeUploadAvatar = null;
                MeshFixSessionState.SetUploadActive(false);
            }
        }

        private static void ProcessForPlayMode() {
            DisposeAllSessions(_playModeSessions);

            var avatarRoots = FindPlayModeAvatarRoots();
            if (avatarRoots.Count == 0) return;

            MeshFixSessionState.SetPlayModeActive(true);

            foreach (var root in avatarRoots) {
                var controller = root.GetComponentInChildren<WhyKnotMeshFixController>(true);
                if (controller != null && !controller.enableAll) continue;

                bool verbose = ResolveVerbose(root, controller);
                var result = MeshFixPipeline.Run(root, new MeshFixPipeline.RunOptions {
                    Mode = MeshFixMode.PlayMode,
                    Verbose = verbose,
                    OwnerGate = obj => OwnerGateForPlayMode(obj),
                });

                if (result.OpsApplied == 0 && result.OpsSkipped == 0) {
                    result.Session.Dispose();
                    continue;
                }

                if (!result.Success) {
                    result.Session.Dispose();
                    LogResult($"play mode ({root.name})", result, LogType.Error);
                    continue;
                }

                _playModeSessions[root] = result.Session;
                PhysBoneReinitHook.RequestReinit(root);
                LogResult($"play mode ({root.name})", result, LogType.Log);
            }

            if (_playModeSessions.Count == 0) MeshFixSessionState.SetPlayModeActive(false);
        }

        private static bool OwnerGateForPlayMode(Object owner) {
            return owner is AutoTightenToBody setup && setup.processInPlayMode;
        }

        private static List<GameObject> FindPlayModeAvatarRoots() {
            var roots = new HashSet<GameObject>();
            foreach (var setup in Resources.FindObjectsOfTypeAll<AutoTightenToBody>()) {
                if (setup == null || !setup.enabled || !setup.processInPlayMode) continue;
                if (EditorUtility.IsPersistent(setup)) continue;
                var go = setup.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if (MeshFixPreviewController.IsAvatarInsidePreview(go)) continue;

                var animator = setup.GetComponentInParent<Animator>(true);
                roots.Add(animator != null ? animator.gameObject : TopLevel(setup.transform).gameObject);
            }
            return roots.ToList();
        }

        private static Transform TopLevel(Transform transform) {
            while (transform != null && transform.parent != null) transform = transform.parent;
            return transform;
        }

        // ---- Shared helpers ----------------------------------------------

        private static bool ResolveVerbose(GameObject avatarRoot, WhyKnotMeshFixController controller) {
            if (controller != null && controller.forceVerboseLog) return true;
            if (avatarRoot == null) return false;
            foreach (var setup in avatarRoot.GetComponentsInChildren<AutoTightenToBody>(true)) {
                if (setup != null && setup.verboseLog) return true;
            }
            return false;
        }

        private static void DisposeSession(Dictionary<GameObject, MeshFixSession> map, GameObject avatarRoot) {
            if (avatarRoot == null || !map.TryGetValue(avatarRoot, out var session)) return;
            session?.Dispose();
            map.Remove(avatarRoot);
        }

        private static void DisposeAllSessions(Dictionary<GameObject, MeshFixSession> map) {
            foreach (var session in map.Values) session?.Dispose();
            map.Clear();
        }

        private static void LogResult(string context, MeshFixPipeline.RunResult result, LogType type) {
            if (result == null || (result.OpsApplied == 0 && result.OpsSkipped == 0 && result.Errors.Count == 0)) return;

            var lines = new List<string> {
                $"[Avatar QoL] Mesh fix {context}: {result.Summary}"
            };
            foreach (var w in result.Warnings) lines.Add("  Warning: " + w);
            foreach (var e in result.Errors) lines.Add("  Error: " + e);
            var text = string.Join("\n", lines);

            switch (type) {
                case LogType.Error: Debug.LogError(text); break;
                case LogType.Warning: Debug.LogWarning(text); break;
                default: Debug.Log(text); break;
            }
        }
    }
}
