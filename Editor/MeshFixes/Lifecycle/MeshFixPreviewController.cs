// MeshFixPreviewController.cs
//
// Edit-mode "Preview" path. Instantiates a temporary clone of the avatar
// in place, runs the MeshFixPipeline against the clone, hides the source
// via SceneVisibilityManager (Scene view only -- Game view is unaffected,
// which is expected for an edit-mode preview). On StopPreview the clone
// is destroyed and source visibility restored.
//
// Domain-reload recovery: source GameObject identity is persisted via
// GlobalObjectId.ToString() in EditorPrefs (project-scoped, survives
// editor restarts) so a crashed preview can still un-hide the source
// the next time the project opens. Preview clones are tagged with a
// name suffix; abandoned clones are cleaned up on recovery.
//
// SessionState marker: while a preview is active, the
// WhyKnot.AvatarQol.MeshFix.Preview.Active flag is set so WeightFixer
// refuses to write durable changes to the in-memory clone that is
// currently assigned to the renderer.

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.MeshFixes.Pipeline;

namespace WhyKnot.AvatarQol.MeshFixes.Lifecycle {

    [InitializeOnLoad]
    internal static class MeshFixPreviewController {

        private const string PreviewSuffix = " (Avatar QoL Preview)";
        private const string SessionPreviewId = "WhyKnot.AvatarQol.MeshFix.PreviewId";
        private const string SessionSourceId = "WhyKnot.AvatarQol.MeshFix.SourceId";
        private const string SessionSourceWasHidden = "WhyKnot.AvatarQol.MeshFix.SourceWasHidden";
        private const string PrefsSourceGlobalId = "WhyKnot.AvatarQol.MeshFix.SourceGlobalId";
        private const string PrefsSourceWasHidden = "WhyKnot.AvatarQol.MeshFix.SourceWasHidden";

        private static GameObject _sourceAvatar;
        private static GameObject _previewAvatar;
        private static bool _sourceWasHidden;
        private static MeshFixSession _previewSession;

        static MeshFixPreviewController() {
            EditorApplication.delayCall -= RestoreAfterDomainReload;
            EditorApplication.delayCall += RestoreAfterDomainReload;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
            EditorApplication.quitting -= StopPreview;
            EditorApplication.quitting += StopPreview;
        }

        internal static bool IsPreviewing => _previewAvatar != null;
        internal static GameObject SourceAvatar => _sourceAvatar;
        internal static GameObject PreviewAvatar => _previewAvatar;

        /// <summary>
        /// Returns true when the candidate GameObject is the active preview
        /// clone or a descendant of it. Defensive null-check on the cached
        /// preview reference; F10 (the previous codebase NRE-d here when the
        /// preview was destroyed under us by a domain reload).
        /// </summary>
        internal static bool IsAvatarInsidePreview(GameObject candidate) {
            if (candidate == null) return false;
            if (_previewAvatar == null) return false;
            var previewTransform = _previewAvatar.transform;
            if (previewTransform == null) return false;
            return candidate.transform == previewTransform || candidate.transform.IsChildOf(previewTransform);
        }

        internal static PreviewResult StartPreview(GameObject avatarRoot) {
            StopPreview();
            var result = new PreviewResult();
            if (avatarRoot == null) {
                result.Errors.Add("Choose an avatar before previewing.");
                return result;
            }

            _sourceAvatar = avatarRoot;
            _sourceWasHidden = IsSceneHidden(avatarRoot);
            _previewAvatar = UnityEngine.Object.Instantiate(avatarRoot, avatarRoot.transform.parent);
            _previewAvatar.name = avatarRoot.name + PreviewSuffix;
            _previewAvatar.transform.SetSiblingIndex(avatarRoot.transform.GetSiblingIndex() + 1);
            _previewAvatar.SetActive(true);
            RememberPreview();

            MeshFixSessionState.SetPreviewActive(true);

            var pipelineResult = MeshFixPipeline.Run(_previewAvatar, new MeshFixPipeline.RunOptions {
                Mode = MeshFixMode.Preview,
                Verbose = ResolveVerbose(_previewAvatar),
            });
            _previewSession = pipelineResult.Session;
            result.PipelineResult = pipelineResult;

            if (!pipelineResult.Success || pipelineResult.OpsApplied == 0) {
                StopPreview();
                return result;
            }

            HideSourceAvatar(avatarRoot);
            return result;
        }

        internal static void StopPreview() {
            if (_previewSession != null) {
                _previewSession.Dispose();
                _previewSession = null;
            }

            if (_previewAvatar != null) {
                UnityEngine.Object.DestroyImmediate(_previewAvatar);
                _previewAvatar = null;
            }

            if (_sourceAvatar != null) {
                RestoreSourceVisibility(_sourceAvatar, _sourceWasHidden);
                _sourceAvatar = null;
            }
            ForgetPreview();
            MeshFixSessionState.SetPreviewActive(false);
        }

        [MenuItem("Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/Stop Previewing", false, 2102)]
        private static void StopPreviewMenu() => StopPreview();

        [MenuItem("Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/Stop Previewing", true)]
        private static bool StopPreviewMenuValidate() => IsPreviewing;

        private static bool ResolveVerbose(GameObject avatarRoot) {
            if (avatarRoot == null) return false;
            var controller = avatarRoot.GetComponentInChildren<WhyKnotMeshFixController>(true);
            if (controller != null && controller.forceVerboseLog) return true;
            foreach (var setup in avatarRoot.GetComponentsInChildren<AutoTightenToBody>(true)) {
                if (setup != null && setup.verboseLog) return true;
            }
            return false;
        }

        private static void RememberPreview() {
            SessionState.SetInt(SessionPreviewId, _previewAvatar != null ? _previewAvatar.GetInstanceID() : 0);
            SessionState.SetInt(SessionSourceId, _sourceAvatar != null ? _sourceAvatar.GetInstanceID() : 0);
            SessionState.SetBool(SessionSourceWasHidden, _sourceWasHidden);
            StoreSourceForCrashRecovery(_sourceAvatar, _sourceWasHidden);
        }

        private static void ForgetPreview() {
            SessionState.EraseInt(SessionPreviewId);
            SessionState.EraseInt(SessionSourceId);
            SessionState.EraseBool(SessionSourceWasHidden);
            EditorPrefs.DeleteKey(PrefsSourceGlobalId);
            EditorPrefs.DeleteKey(PrefsSourceWasHidden);
        }

        private static void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode) {
            RestoreAfterDomainReload();
        }

        private static void RestoreAfterDomainReload() {
            try {
                var hasSourceState = SessionState.GetInt(SessionSourceId, 0) != 0 ||
                    EditorPrefs.HasKey(PrefsSourceGlobalId);
                var source = ResolveRememberedSource();
                var preview = EditorUtility.InstanceIDToObject(SessionState.GetInt(SessionPreviewId, 0)) as GameObject;
                if (source == null && hasSourceState && EditorPrefs.HasKey(PrefsSourceGlobalId)) {
                    CleanupAbandonedPreviewClones();
                    MeshFixSessionState.SetPreviewActive(false);
                    return;
                }

                var sourceWasHidden = SessionState.GetBool(
                    SessionSourceWasHidden,
                    EditorPrefs.GetBool(PrefsSourceWasHidden, false));
                RestoreSourceVisibility(source, sourceWasHidden);
                if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
                CleanupAbandonedPreviewClones();

                _sourceAvatar = null;
                _previewAvatar = null;
                _previewSession = null;
                ForgetPreview();
                MeshFixSessionState.SetPreviewActive(false);
            } catch (Exception ex) {
                Debug.LogWarning($"[Avatar QoL] Preview recovery hit an unexpected error: {ex.Message}");
            }
        }

        private static void Tick() {
            if (_previewAvatar != null) return;
            if (_sourceAvatar != null) {
                RestoreSourceVisibility(_sourceAvatar, _sourceWasHidden);
                _sourceAvatar = null;
                // Mesh clones live behind HideFlags.DontSave -- dropping the
                // session reference without Dispose would leak them until
                // domain reload.
                _previewSession?.Dispose();
                _previewSession = null;
                ForgetPreview();
                MeshFixSessionState.SetPreviewActive(false);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode) {
                StopPreview();
            }
        }

        private static void HideSourceAvatar(GameObject source) {
            if (source == null) return;
            SceneVisibilityManager.instance.Hide(source, true);
        }

        private static void RestoreSourceVisibility(GameObject source, bool sourceWasHidden) {
            if (source == null || sourceWasHidden) return;
            SceneVisibilityManager.instance.Show(source, true);
        }

        private static bool IsSceneHidden(GameObject source) {
            return source != null && SceneVisibilityManager.instance.IsHidden(source);
        }

        private static void StoreSourceForCrashRecovery(GameObject source, bool sourceWasHidden) {
            if (source == null) return;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(source).ToString();
            if (string.IsNullOrEmpty(id)) return;
            EditorPrefs.SetString(PrefsSourceGlobalId, id);
            EditorPrefs.SetBool(PrefsSourceWasHidden, sourceWasHidden);
        }

        private static GameObject ResolveRememberedSource() {
            var source = EditorUtility.InstanceIDToObject(SessionState.GetInt(SessionSourceId, 0)) as GameObject;
            if (source != null) return source;

            var idText = EditorPrefs.GetString(PrefsSourceGlobalId, string.Empty);
            if (string.IsNullOrEmpty(idText)) return null;
            if (!GlobalObjectId.TryParse(idText, out var id)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
        }

        private static void CleanupAbandonedPreviewClones() {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>()) {
                if (go == null || string.IsNullOrEmpty(go.name)) continue;
                if (!go.name.EndsWith(PreviewSuffix, StringComparison.Ordinal)) continue;
                if (!EditorUtility.IsPersistent(go)) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        internal sealed class PreviewResult {
            public System.Collections.Generic.List<string> Errors { get; } = new System.Collections.Generic.List<string>();
            public MeshFixPipeline.RunResult PipelineResult { get; set; }
        }
    }
}
