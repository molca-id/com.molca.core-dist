using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage.Core;
using Molca.ContentPackage.Services;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// A read-only window onto the live <see cref="PackageService"/> while the project is playing.
    /// </summary>
    /// <remarks>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/Hub/Workspaces/Content/</c>.
    /// <b>Registration:</b> constructed and disposed by <see cref="ContentWorkspaceView"/>, and reached
    /// by the pages through <see cref="ContentWorkspaceContext.Runtime"/>. An instance owned by the
    /// view, never a static: a controller that outlives its view is how a Hub tool ends up with two of
    /// itself running behind one panel.
    /// <para>
    /// <b>Strictly observational.</b> It reads installed state and cloud status and offers no way to
    /// install, uninstall, or retry. Authoring changes what a release contains; driving a running
    /// player's downloads is a different job with different consequences, and the MCP operation tools
    /// already own it.
    /// </para>
    /// <para>
    /// <b>Sampled, not subscribed, and deliberately slow.</b> A download raises progress every frame,
    /// and reporting each one would rebuild the workspace at frame rate. So this polls once a second
    /// and raises <see cref="Changed"/> only when a signature of what it exposes actually differs —
    /// which during a download means roughly one rebuild per percent, and none at all when nothing is
    /// happening.
    /// </para>
    /// </remarks>
    internal sealed class ContentRuntimeProbe : IDisposable
    {
        /// <summary>Seconds between samples of the live service.</summary>
        private const double SampleInterval = 1.0;

        private readonly Dictionary<string, PackageState> _states =
            new Dictionary<string, PackageState>(StringComparer.Ordinal);

        private readonly Func<IEnumerable<string>> _knownPackageIds;

        private PackageService _service;
        private double _nextSample;
        private string _signature = "";
        private bool _disposed;

        /// <summary>Raised when the observed state changed in a way a page would render differently.</summary>
        public event Action Changed;

        /// <summary>The live cloud status, or null when nothing is playing.</summary>
        public PackageCloudStatus CloudStatus { get; private set; }

        /// <summary>Whether a live <see cref="PackageService"/> is attached.</summary>
        public bool IsLive => _service != null;

        /// <summary>Starts watching for a live service.</summary>
        /// <param name="knownPackageIds">
        /// The packages the project defines. Needed because <see cref="PackageService"/> exposes only
        /// its <em>installed</em> set, and the states worth looking at during a session are the ones
        /// that are not installed: a failed download and one in flight both read as absent otherwise.
        /// </param>
        public ContentRuntimeProbe(Func<IEnumerable<string>> knownPackageIds)
        {
            _knownPackageIds = knownPackageIds;
            EditorApplication.update += Tick;
        }

        /// <summary>The live state of one package, or null.</summary>
        /// <param name="packageId">The package.</param>
        /// <returns>Its state, or null when not playing or not known to the service.</returns>
        public PackageState StateOf(string packageId) =>
            packageId != null && _states.TryGetValue(packageId, out var state) ? state : null;

        /// <summary>Stops watching and drops the reference to the live service.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            EditorApplication.update -= Tick;
            Detach();
        }

        private void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextSample) return;
            _nextSample = EditorApplication.timeSinceStartup + SampleInterval;

            if (!Application.isPlaying)
            {
                if (_service == null) return;

                Detach();
                Raise();
                return;
            }

            if (_service == null)
            {
                // Polled rather than hooked to a play-mode state change, because the subsystem is
                // registered some way into bootstrap — a hook on EnteredPlayMode fires before the
                // service exists and would attach to nothing.
                var subsystem = RuntimeManager.GetSubsystem<PackageSubsystem>();
                if (subsystem?.PackageService == null) return;

                _service = subsystem.PackageService;
                CloudStatus = _service.CloudStatus;
            }

            Refresh();
        }

        private void Refresh()
        {
            _states.Clear();

            foreach (var state in _service.GetInstalledPackages())
            {
                if (state?.packageId != null) _states[state.packageId] = state;
            }

            foreach (string packageId in _knownPackageIds?.Invoke() ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(packageId) || _states.ContainsKey(packageId)) continue;

                var state = _service.GetPackageState(packageId);
                if (state != null) _states[packageId] = state;
            }

            CloudStatus = _service.CloudStatus;
            Raise();
        }

        private void Raise()
        {
            string next = Signature();
            if (string.Equals(next, _signature, StringComparison.Ordinal)) return;

            _signature = next;
            Changed?.Invoke();
        }

        /// <summary>
        /// A cheap description of everything the pages render, used to suppress no-op rebuilds.
        /// </summary>
        /// <remarks>
        /// Download progress is quantised to whole percent on purpose. At full precision it changes
        /// every sample of an active download, so the signature would never match and the suppression
        /// would do nothing at all.
        /// </remarks>
        private string Signature()
        {
            if (_service == null) return "";

            var builder = new StringBuilder();
            builder.Append(CloudStatus?.State).Append('|').Append(CloudStatus?.RemotePackageCount);

            foreach (var pair in _states)
            {
                builder.Append(';').Append(pair.Key).Append(':')
                    .Append(pair.Value.status).Append(':')
                    .Append(Mathf.RoundToInt(pair.Value.downloadProgress * 100f)).Append(':')
                    .Append(pair.Value.installedVersion);
            }

            return builder.ToString();
        }

        private void Detach()
        {
            _service = null;
            CloudStatus = null;
            _states.Clear();
        }
    }
}
