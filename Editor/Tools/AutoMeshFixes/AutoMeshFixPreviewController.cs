using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools.AutoMeshFixes {

    [InitializeOnLoad]
    internal static class AutoMeshFixPreviewController {

        private const string PreviewSuffix = " (Avatar QoL Preview)";
        private const string SessionPreviewId = "WhyKnot.AvatarQol.AutoMeshFix.PreviewId";
        private const string SessionSourceId = "WhyKnot.AvatarQol.AutoMeshFix.SourceId";
        private const string SessionSourceWasHidden = "WhyKnot.AvatarQol.AutoMeshFix.SourceWasHidden";
        private const string PrefsSourceGlobalId = "WhyKnot.AvatarQol.AutoMeshFix.SourceGlobalId";
        private const string PrefsSourceWasHidden = "WhyKnot.AvatarQol.AutoMeshFix.SourceWasHidden";

        private static GameObject _sourceAvatar;
        private static GameObject _previewAvatar;
        private static bool _sourceWasHidden;
        private static AutoMeshFixProcessor.Session _previewSession;

        static AutoMeshFixPreviewController() {
            EditorApplication.delayCall += RestoreAfterDomainReload;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneOpened += (_, __) => RestoreAfterDomainReload();
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
            EditorApplication.quitting += StopPreview;
        }

        internal static bool IsPreviewing => _previewAvatar != null;
        internal static GameObject SourceAvatar => _sourceAvatar;
        internal static GameObject PreviewAvatar => _previewAvatar;

        internal static AutoMeshFixProcessor.Result StartPreview(GameObject avatarRoot) {
            StopPreview();
            var result = new AutoMeshFixProcessor.Result();
            if (avatarRoot == null) {
                result.Errors.Add("Choose an avatar before previewing.");
                return result;
            }

            _sourceAvatar = avatarRoot;
            _sourceWasHidden = IsSceneHidden(avatarRoot);
            _previewAvatar = Object.Instantiate(avatarRoot, avatarRoot.transform.parent);
            _previewAvatar.name = avatarRoot.name + PreviewSuffix;
            _previewAvatar.transform.SetSiblingIndex(avatarRoot.transform.GetSiblingIndex() + 1);
            _previewAvatar.SetActive(true);
            RememberPreview();

            result = AutoMeshFixProcessor.ProcessAvatar(_previewAvatar, new AutoMeshFixProcessor.Options {
                Preview = true,
                Verbose = HasVerboseSetup(_previewAvatar),
            });
            _previewSession = result.Session;
            if (!result.Success || result.ComponentsProcessed == 0) {
                StopPreview();
                return result;
            }

            HideSourceAvatar(avatarRoot);
            return result;
        }

        internal static void StopPreview() {
            if (_previewSession != null) {
                _previewSession.Restore();
                _previewSession = null;
            }

            if (_previewAvatar != null) {
                Object.DestroyImmediate(_previewAvatar);
                _previewAvatar = null;
            }

            if (_sourceAvatar != null) {
                RestoreSourceVisibility(_sourceAvatar, _sourceWasHidden);
                _sourceAvatar = null;
            }
            ForgetPreview();
        }

        [MenuItem("Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/Stop Previewing", false, 2102)]
        private static void StopPreviewMenu() {
            StopPreview();
        }

        [MenuItem("Tools/WhyKnot/vrc-avatar-qol/Auto Mesh Fixes/Stop Previewing", true)]
        private static bool StopPreviewMenuValidate() {
            return IsPreviewing;
        }

        private static bool HasVerboseSetup(GameObject avatarRoot) {
            if (avatarRoot == null) return false;
            foreach (var setup in avatarRoot.GetComponentsInChildren<Components.AutoTightenToBody>(true)) {
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

        private static void RestoreAfterDomainReload() {
            var hasSourceState = SessionState.GetInt(SessionSourceId, 0) != 0 ||
                EditorPrefs.HasKey(PrefsSourceGlobalId);
            var source = ResolveRememberedSource();
            var preview = EditorUtility.InstanceIDToObject(SessionState.GetInt(SessionPreviewId, 0)) as GameObject;
            if (source == null && hasSourceState && EditorPrefs.HasKey(PrefsSourceGlobalId)) {
                CleanupAbandonedPreviewClones();
                return;
            }

            var sourceWasHidden = SessionState.GetBool(
                SessionSourceWasHidden,
                EditorPrefs.GetBool(PrefsSourceWasHidden, false));
            RestoreSourceVisibility(source, sourceWasHidden);
            if (preview != null) Object.DestroyImmediate(preview);
            CleanupAbandonedPreviewClones();

            _sourceAvatar = null;
            _previewAvatar = null;
            _previewSession = null;
            ForgetPreview();
        }

        private static void Tick() {
            if (_previewAvatar != null) return;
            if (_sourceAvatar != null) {
                RestoreSourceVisibility(_sourceAvatar, _sourceWasHidden);
                _sourceAvatar = null;
                _previewSession = null;
                ForgetPreview();
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
                if (!go.name.EndsWith(PreviewSuffix, System.StringComparison.Ordinal)) continue;
                if (!EditorUtility.IsPersistent(go)) Object.DestroyImmediate(go);
            }
        }
    }
}
