// MeshFixSessionState.cs
//
// SessionState-backed flags used to coordinate between independent tools.
//
// SessionState lifetime: across domain reload, cleared on editor exit.
// EditorPrefs is wrong here (persists across editor restarts; would survive
// a crashed preview into the next day's launch). Static fields are wrong
// here (wiped on every recompile, so the next domain reload would race).
//
// The preview-active marker lets WeightFixer refuse to mutate
// renderer.sharedMesh while an Auto Mesh Fixes preview holds an in-memory
// clone on it; otherwise the WeightFixer would write durable changes to a
// clone that gets DestroyImmediate-d when the preview ends.

using UnityEditor;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal static class MeshFixSessionState {

        public const string PreviewActiveKey = "WhyKnot.AvatarQol.MeshFix.Preview.Active";
        public const string UploadActiveKey = "WhyKnot.AvatarQol.MeshFix.Upload.Active";
        public const string PlayModeActiveKey = "WhyKnot.AvatarQol.MeshFix.PlayMode.Active";

        public static bool IsAnyMeshSessionActive() {
            return SessionState.GetBool(PreviewActiveKey, false)
                || SessionState.GetBool(UploadActiveKey, false)
                || SessionState.GetBool(PlayModeActiveKey, false);
        }

        public static bool IsPreviewActive() => SessionState.GetBool(PreviewActiveKey, false);

        public static void SetPreviewActive(bool active) {
            if (active) SessionState.SetBool(PreviewActiveKey, true);
            else SessionState.EraseBool(PreviewActiveKey);
        }

        public static void SetUploadActive(bool active) {
            if (active) SessionState.SetBool(UploadActiveKey, true);
            else SessionState.EraseBool(UploadActiveKey);
        }

        public static void SetPlayModeActive(bool active) {
            if (active) SessionState.SetBool(PlayModeActiveKey, true);
            else SessionState.EraseBool(PlayModeActiveKey);
        }

        public static void ClearAll() {
            SessionState.EraseBool(PreviewActiveKey);
            SessionState.EraseBool(UploadActiveKey);
            SessionState.EraseBool(PlayModeActiveKey);
        }
    }
}
