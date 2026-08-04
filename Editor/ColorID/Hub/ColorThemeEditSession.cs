#if UNITY_EDITOR
using System;
using Molca.ColorID;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// Applies colour-value edits to a <see cref="ColorThemeSet"/> at the pace an author edits, rather than
    /// at the pace an asset import can be committed.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Hub/</c>.
    /// <b>Shape:</b> a plain object owned by <see cref="ColorThemeWorkspaceView"/> for as long as that view
    /// is attached. Editor-only; main thread.
    /// <para/>
    /// <b>Why this exists.</b> A <c>ColorField</c> raises a change event on every frame the colour picker
    /// moves, so "write the asset, save it, and rebuild" per event means an <see cref="AssetDatabase"/> save
    /// and a full project audit per frame of a drag — and a rebuild destroys the very field being dragged.
    /// The three costs separate cleanly:
    /// <list type="bullet">
    /// <item>the write into the asset is cheap, and happens per event;</item>
    /// <item>the save is expensive and only has to be true once the author stops, so it is debounced;</item>
    /// <item>the project scan cannot be affected by a colour value at all, so it never happens here —
    /// see <see cref="ColorThemeWorkspaceModel.WithRefreshedValues"/>.</item>
    /// </list>
    /// <b>Undo.</b> One entry per burst, not one per frame. The group open when the burst starts is recorded
    /// and collapsed into on flush, so a drag undoes as the single act it felt like.
    /// </remarks>
    internal sealed class ColorThemeEditSession
    {
        /// <summary>
        /// Quiet period after the last edit before the asset is saved.
        /// </summary>
        /// <remarks>
        /// Long enough that no realistic drag or arrow-key repeat crosses it, short enough that an author who
        /// stops to look has a saved asset by the time they switch windows. The asset is already correct in
        /// memory throughout — this delays only the disk write.
        /// </remarks>
        private const long SaveDebounceMilliseconds = 600;

        private readonly Func<ColorThemeSet> _target;
        private readonly IVisualElementScheduledItem _save;

        private bool _bursting;
        private int _burstUndoGroup;

        /// <summary>Raised after each write, once the asset has been re-indexed.</summary>
        /// <remarks>
        /// The view uses this to re-resolve and repaint the parts that derive from a value — the preview and
        /// the contrast badges. It must not rebuild the control that raised the edit.
        /// </remarks>
        internal event Action Changed;

        /// <summary>Raised once a burst of edits has been committed to disk.</summary>
        internal event Action Committed;

        /// <summary>Creates a session that always writes to whichever set the workspace currently holds.</summary>
        /// <param name="target">
        /// Resolves the set to write to, evaluated per write. A provider rather than a captured reference so
        /// one session — and so one debounce timer — outlives the model rebuilds that replace the set: a
        /// scheduled item cannot be unregistered from an element, so a session per rebuild would accumulate
        /// timers for the life of the window.
        /// </param>
        /// <param name="host">Element whose scheduler drives the debounce; usually the workspace root.</param>
        internal ColorThemeEditSession(Func<ColorThemeSet> target, VisualElement host)
        {
            _target = target;

            // Registered paused. Each edit re-arms it, so the timer measures the gap since the last edit
            // rather than the time since the first one.
            _save = host.schedule.Execute(Flush);
            _save.Pause();
        }

        /// <summary>Whether anything has been written that is not yet on disk.</summary>
        internal bool HasPendingWrites => _bursting;

        /// <summary>Writes one variant's expression for one token.</summary>
        /// <param name="variantId">The variant to write into.</param>
        /// <param name="tokenId">The token to set.</param>
        /// <param name="expression">Literal, alias, or alias with alpha.</param>
        /// <param name="undoLabel">Undo entry name for the burst this write belongs to.</param>
        /// <returns><c>false</c> when the variant does not exist, in which case nothing was written.</returns>
        internal bool Write(string variantId, string tokenId, ColorExpression expression, string undoLabel)
        {
            var themeSet = _target?.Invoke();
            if (themeSet == null) return false;

            BeginBurst(themeSet, undoLabel);

            if (!ColorThemeSetEditing.SetTokenValue(themeSet, variantId, tokenId, expression))
            {
                Debug.LogWarning($"[ColorTheme] Variant '{variantId}' does not exist, so "
                                 + $"'{tokenId}' was not set.");
                return false;
            }

            // Before Changed: the view re-resolves in response, and the resolver reads these indexes.
            themeSet.InvalidateIndexes();
            EditorUtility.SetDirty(themeSet);

            Changed?.Invoke();
            _save.ExecuteLater(SaveDebounceMilliseconds);
            return true;
        }

        /// <summary>Commits any pending writes to disk now.</summary>
        /// <remarks>
        /// Called on the debounce, and directly before anything that reads the asset from disk or opens a
        /// transaction over the project — a planner that reads a stale file would preview the wrong change.
        /// Safe to call when nothing is pending.
        /// </remarks>
        internal void Flush()
        {
            _save.Pause();
            if (!_bursting) return;

            _bursting = false;
            AssetDatabase.SaveAssets();

            // Collapses every record since the burst began into one entry. Done after the save so the
            // collapsed entry describes an asset state that exists on disk.
            Undo.CollapseUndoOperations(_burstUndoGroup);

            Committed?.Invoke();
        }

        private void BeginBurst(ColorThemeSet themeSet, string undoLabel)
        {
            if (!_bursting)
            {
                _bursting = true;
                _burstUndoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(undoLabel ?? "Edit colour token");
            }

            Undo.RecordObject(themeSet, undoLabel ?? "Edit colour token");
        }
    }
}
#endif
