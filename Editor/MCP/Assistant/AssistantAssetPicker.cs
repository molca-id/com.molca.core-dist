using System;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Drives Unity's modal object picker and reports the chosen asset through a callback (Sprint 25.2).
    /// Encapsulates the <see cref="EditorApplication.update"/> polling the picker requires — it exposes no
    /// completion event — so the chat window no longer owns that state machine. One pick is tracked at a
    /// time; starting a new pick supersedes any in-flight one.
    /// </summary>
    public sealed class AssistantAssetPicker
    {
        // The picker control id is non-zero while our picker is open and returns to 0 once it closes.
        private static readonly int ControlId = "MolcaAssistantAssetPicker".GetHashCode();

        private Action<UnityEngine.Object> _onPicked;
        private UnityEngine.Object _lastPicked;
        private bool _opened;
        private bool _polling;

        /// <summary>
        /// Opens the object picker. <paramref name="onPicked"/> fires once with the chosen object, or not
        /// at all if the picker is dismissed without a selection.
        /// </summary>
        public void Pick(Action<UnityEngine.Object> onPicked)
        {
            _onPicked = onPicked;
            _lastPicked = null;
            _opened = false;
            EditorGUIUtility.ShowObjectPicker<UnityEngine.Object>(CurrentProjectSelection(), false, string.Empty, ControlId);
            if (!_polling)
            {
                EditorApplication.update += Poll;
                _polling = true;
            }
        }

        /// <summary>Stops polling and drops any pending callback. Call from the window's <c>OnDisable</c>.</summary>
        public void Dispose()
        {
            if (_polling)
            {
                EditorApplication.update -= Poll;
                _polling = false;
            }
            _onPicked = null;
            _lastPicked = null;
        }

        private void Poll()
        {
            if (EditorGUIUtility.GetObjectPickerControlID() == ControlId)
            {
                _opened = true;
                var current = EditorGUIUtility.GetObjectPickerObject();
                if (current != null && IsProjectAsset(current))
                    _lastPicked = current;
                return;
            }
            if (!_opened) return;

            EditorApplication.update -= Poll;
            _polling = false;
            _opened = false;

            var picked = EditorGUIUtility.GetObjectPickerObject();
            if (picked == null || !IsProjectAsset(picked))
                picked = _lastPicked;
            var callback = _onPicked;
            _onPicked = null;
            _lastPicked = null;
            if (picked != null) callback?.Invoke(picked);
        }

        private static UnityEngine.Object CurrentProjectSelection()
        {
            var active = Selection.activeObject;
            if (active == null) return null;

            var path = AssetDatabase.GetAssetPath(active);
            return string.IsNullOrEmpty(path) ? null : active;
        }

        private static bool IsProjectAsset(UnityEngine.Object obj)
            => obj != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj));
    }
}
