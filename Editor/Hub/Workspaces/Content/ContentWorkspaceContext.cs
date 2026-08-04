using System;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage;
using Molca.ContentPackage.Editor;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// What every Content page is handed: the asset, the one service permitted to write it, the current
    /// findings, and the two ways to apply an edit.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> built once per rebuild by <see cref="ContentWorkspaceView"/> and passed to
    /// each page; pages never resolve the settings asset themselves.
    /// <para>
    /// There are two apply methods rather than one flag, because the difference is not cosmetic.
    /// <see cref="ApplyPackageEdit"/> throws away the staged build; <see cref="ApplySettingsEdit"/>
    /// keeps it. A release candidate is derived from the package definitions <em>and</em> the build
    /// graph together, so a definition edit made after a build produces a candidate describing content
    /// that was never built — sizes, bundle ownership and the manifest all for a configuration that no
    /// longer exists. Delivery and protocol settings do not feed the graph, and dropping a fifteen-minute
    /// Addressables build because someone changed the retry count would be its own kind of wrong.
    /// </para>
    /// </remarks>
    internal sealed class ContentWorkspaceContext
    {
        private readonly Action _reload;

        /// <summary>The project's content settings asset.</summary>
        public ContentPackageSettings Settings { get; }

        /// <summary>The one write path, shared with the MCP tools and the remediation fixes.</summary>
        public ContentPackageEditingService Editing { get; }

        /// <summary>Settings-level findings for the current definitions.</summary>
        public ContentValidationReport Report { get; }

        /// <summary>Why this asset cannot be edited, or null when it can.</summary>
        public string ReadOnlyReason { get; }

        /// <summary>Whether edits are refused.</summary>
        public bool IsReadOnly => ReadOnlyReason != null;

        /// <summary>
        /// The live package service while the project is playing, or null.
        /// </summary>
        /// <remarks>
        /// Owned by the workspace view, not by this context: the context is rebuilt on every edit and a
        /// probe rebuilt with it would re-attach to the running service several times a second.
        /// </remarks>
        public ContentRuntimeProbe Runtime { get; }

        /// <summary>Builds the context for one rebuild of the workspace.</summary>
        /// <param name="settings">The settings asset. Required.</param>
        /// <param name="reload">Rebuilds the workspace after an applied edit.</param>
        /// <param name="runtime">The view's live-service probe, or null.</param>
        /// <exception cref="ArgumentNullException">The asset is null.</exception>
        public ContentWorkspaceContext(
            ContentPackageSettings settings, Action reload, ContentRuntimeProbe runtime = null)
        {
            Settings = settings ? settings : throw new ArgumentNullException(nameof(settings));
            _reload = reload;
            Runtime = runtime;
            Editing = new ContentPackageEditingService(settings);
            ReadOnlyReason = Editing.ReadOnlyReason();
            Report = ContentValidation.ValidateSettings(settings.packageConfigs);
        }

        /// <summary>
        /// Re-renders the workspace without applying an edit.
        /// </summary>
        /// <remarks>
        /// For the things a page changes that are not settings-asset edits — which build config is
        /// selected, what a manifest import added. They still change what every page shows, and a page
        /// that refreshed only itself would leave the rail disagreeing with the form.
        /// </remarks>
        public void Reload() => _reload?.Invoke();

        /// <summary>
        /// Applies an edit to a package definition: saves, drops the staged build, and rebuilds.
        /// </summary>
        /// <param name="result">What the setter reported.</param>
        /// <returns>True when the asset changed.</returns>
        public bool ApplyPackageEdit(ContentEditResult result)
        {
            if (!Apply(result)) return false;

            ContentWorkspaceSession.InvalidateBuild();

            // Dropped wholesale rather than per package. Labels are the only input a scan reads, but a
            // rename changes the key it is filed under, so an edit-specific invalidation would have to
            // know which edit this was — and the cost of being wrong is a stale asset count presented
            // as current.
            ContentScanPreview.InvalidateAll();

            _reload?.Invoke();
            return true;
        }

        /// <summary>
        /// Applies an edit to a delivery or protocol setting: saves and rebuilds, keeping the staged build.
        /// </summary>
        /// <param name="result">What the setter reported.</param>
        /// <returns>True when the asset changed.</returns>
        public bool ApplySettingsEdit(ContentEditResult result)
        {
            if (!Apply(result)) return false;

            _reload?.Invoke();
            return true;
        }

        /// <summary>
        /// Persists a changed result, or surfaces the refusal.
        /// </summary>
        /// <remarks>
        /// Saved here rather than left dirty because a Hub edit has no other moment that would save it —
        /// an inspector at least has the asset selected. A refusal is logged rather than shown inline:
        /// the setters refuse only what the control should not have been able to ask for, so it is a
        /// developer-facing event, not something to explain in the form.
        /// </remarks>
        private bool Apply(ContentEditResult result)
        {
            if (result == null) return false;

            if (!result.Changed)
            {
                if (!result.Message.EndsWith("is already that.", StringComparison.Ordinal))
                    Debug.LogWarning($"[ContentPackage] {result.Message}");
                return false;
            }

            AssetDatabase.SaveAssets();
            return true;
        }
    }
}
