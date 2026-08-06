using System;
using System.Collections.Generic;
using UnityEditor;

namespace Molca.Settings.Integration.ClickUp
{
    /// <summary>
    /// Per-project, per-machine ClickUp task preferences: the single <em>focused</em> task the developer is
    /// working on, and the set of <em>pinned</em> tasks that float to the top of the Hub Tasks list.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/ClickUp/</c>.
    /// Registration: static utility; not an asset.
    /// <para>
    /// <b>Focus vs. pinning.</b> Focus is <em>semantic</em> and singular: it names the one task this working
    /// copy is currently about, so build/release activity can comment on it (see
    /// <see cref="ClickUpIntegrationProvider.PushTarget"/>) instead of creating a new task per build. Pinning
    /// is <em>presentational</em> and plural: it only affects row ordering and survives filter changes. They
    /// are deliberately separate so "what does a build comment on?" always has exactly one answer.
    /// </para>
    /// <para>
    /// <b>Why not <see cref="IntegrationCredentialStore"/>.</b> That gateway exists to keep <em>secrets</em> in
    /// one place. None of the state here is a secret — it is developer preference — so it is stored directly
    /// through <see cref="EditorUserSettings"/> (which is already per-project and git-ignored) rather than
    /// widening the credential API with non-credential keys. It is never serialized onto the provider asset,
    /// because focus is personal and a committed field would cause churn and merge conflicts across a team.
    /// </para>
    /// <para>
    /// Values are cached in memory after the first read so the Hub can query <see cref="IsPinned"/> per row on
    /// the render path without hitting <see cref="EditorUserSettings"/> each time. The cache is static and
    /// therefore cleared by a domain reload, which re-reads from disk. Main thread only.
    /// </para>
    /// </remarks>
    public static class ClickUpTaskFocus
    {
        private const string KeyPrefix = "Molca.Integration.ClickUp.";
        private const string FocusIdKey = KeyPrefix + "FocusTaskId";
        private const string FocusNameKey = KeyPrefix + "FocusTaskName";
        private const string FocusUrlKey = KeyPrefix + "FocusTaskUrl";
        private const string PinnedIdsKey = KeyPrefix + "PinnedTaskIds";

        // ClickUp task ids are alphanumeric, so '|' is safe as a separator and needs no escaping.
        private const char PinSeparator = '|';

        private static bool _loaded;
        private static string _focusId;
        private static string _focusName;
        private static string _focusUrl;
        private static HashSet<string> _pinned;

        /// <summary>
        /// Raised whenever the focused task or the pinned set changes, so open UI (the Hub Tasks section and
        /// the provider inspector) can refresh without polling.
        /// </summary>
        /// <remarks>
        /// Handlers run synchronously on the main thread inside the mutating call. Subscribers must unsubscribe
        /// when they are destroyed or detached — this is a static event and will otherwise keep them alive.
        /// </remarks>
        public static event Action Changed;

        /// <summary>The focused task's id, or <c>null</c>/empty when no task is focused.</summary>
        public static string FocusedTaskId
        {
            get
            {
                EnsureLoaded();
                return _focusId;
            }
        }

        /// <summary>The focused task's display name, cached so the UI can label it without a fetch.</summary>
        public static string FocusedTaskName
        {
            get
            {
                EnsureLoaded();
                return _focusName;
            }
        }

        /// <summary>The focused task's ClickUp URL, cached so the UI can offer an "open" affordance.</summary>
        public static string FocusedTaskUrl
        {
            get
            {
                EnsureLoaded();
                return _focusUrl;
            }
        }

        /// <summary>Whether a task is currently focused.</summary>
        public static bool HasFocus => !string.IsNullOrEmpty(FocusedTaskId);

        /// <summary>The number of pinned tasks.</summary>
        public static int PinnedCount
        {
            get
            {
                EnsureLoaded();
                return _pinned.Count;
            }
        }

        /// <summary>
        /// Sets the focused task. The name and url are cached alongside the id so the focus can be rendered
        /// (and commented on) without re-fetching the task.
        /// </summary>
        /// <param name="taskId">The task id to focus; null or empty clears the focus.</param>
        /// <param name="taskName">The task's display name, for labelling.</param>
        /// <param name="taskUrl">The task's ClickUp URL, for the "open in ClickUp" affordance.</param>
        public static void SetFocus(string taskId, string taskName, string taskUrl)
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(taskId))
            {
                ClearFocus();
                return;
            }

            if (_focusId == taskId && _focusName == taskName && _focusUrl == taskUrl)
                return;

            _focusId = taskId;
            _focusName = taskName;
            _focusUrl = taskUrl;

            EditorUserSettings.SetConfigValue(FocusIdKey, _focusId);
            EditorUserSettings.SetConfigValue(FocusNameKey, _focusName);
            EditorUserSettings.SetConfigValue(FocusUrlKey, _focusUrl);
            Changed?.Invoke();
        }

        /// <summary>Clears the focused task. No-op when nothing is focused.</summary>
        public static void ClearFocus()
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(_focusId)) return;

            _focusId = null;
            _focusName = null;
            _focusUrl = null;

            EditorUserSettings.SetConfigValue(FocusIdKey, null);
            EditorUserSettings.SetConfigValue(FocusNameKey, null);
            EditorUserSettings.SetConfigValue(FocusUrlKey, null);
            Changed?.Invoke();
        }

        /// <summary>Whether the given task id is the focused one.</summary>
        /// <param name="taskId">The task id to test.</param>
        public static bool IsFocused(string taskId)
            => !string.IsNullOrEmpty(taskId) && taskId == FocusedTaskId;

        /// <summary>Whether the given task id is pinned.</summary>
        /// <param name="taskId">The task id to test.</param>
        /// <remarks>Cheap enough for the render path — backed by the in-memory cache, not a settings read.</remarks>
        public static bool IsPinned(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return false;
            EnsureLoaded();
            return _pinned.Contains(taskId);
        }

        /// <summary>Pins or unpins a task.</summary>
        /// <param name="taskId">The task id to pin or unpin.</param>
        /// <param name="pinned">Whether the task should be pinned.</param>
        public static void SetPinned(string taskId, bool pinned)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            EnsureLoaded();

            bool mutated = pinned ? _pinned.Add(taskId) : _pinned.Remove(taskId);
            if (!mutated) return;

            PersistPinned();
            Changed?.Invoke();
        }

        /// <summary>Flips the pinned state of a task and returns the new state.</summary>
        /// <param name="taskId">The task id to toggle.</param>
        /// <returns><c>true</c> if the task is pinned after the call.</returns>
        public static bool TogglePin(string taskId)
        {
            bool next = !IsPinned(taskId);
            SetPinned(taskId, next);
            return next;
        }

        /// <summary>Removes every pin. No-op when nothing is pinned.</summary>
        public static void ClearPins()
        {
            EnsureLoaded();
            if (_pinned.Count == 0) return;

            _pinned.Clear();
            PersistPinned();
            Changed?.Invoke();
        }

        /// <summary>A snapshot of the pinned task ids.</summary>
        /// <returns>A fresh array; mutating it does not affect the stored set.</returns>
        public static string[] GetPinnedIds()
        {
            EnsureLoaded();
            var copy = new string[_pinned.Count];
            _pinned.CopyTo(copy);
            return copy;
        }

        // Reads the persisted state once per domain, then serves from memory.
        private static void EnsureLoaded()
        {
            if (_loaded) return;

            _focusId = EditorUserSettings.GetConfigValue(FocusIdKey);
            _focusName = EditorUserSettings.GetConfigValue(FocusNameKey);
            _focusUrl = EditorUserSettings.GetConfigValue(FocusUrlKey);

            _pinned = new HashSet<string>(StringComparer.Ordinal);
            string raw = EditorUserSettings.GetConfigValue(PinnedIdsKey);
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var id in raw.Split(PinSeparator))
                {
                    if (!string.IsNullOrEmpty(id)) _pinned.Add(id);
                }
            }

            _loaded = true;
        }

        private static void PersistPinned()
        {
            EditorUserSettings.SetConfigValue(
                PinnedIdsKey,
                _pinned.Count == 0 ? null : string.Join(PinSeparator.ToString(), _pinned));
        }
    }
}
