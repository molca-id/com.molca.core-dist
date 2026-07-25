using System;
using System.Collections.Generic;
using Molca.Editor.Licensing;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Slim, full-width strip pinned to the bottom of the Molca Hub shell that renders live "activity"
    /// chips — ongoing processes (e.g. a running Doctor scan) and context — contributed through the
    /// <see cref="MolcaHubActivityProvider"/> seam. The rail hides itself entirely when no provider has
    /// anything to show, so an idle Hub reclaims the space.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Base class: <see cref="VisualElement"/>.
    /// Hosted by <see cref="MolcaHubWindow"/> as the last child of its root (a flex column), so it sits
    /// below both the Settings body and the workspace host and spans the full window width. It owns the
    /// provider instances for its lifetime: it subscribes to each provider's <c>Changed</c> event on
    /// construction and disposes them on <see cref="DetachFromPanelEvent"/> (window close / rebuild), so
    /// providers observing a static source do not leak. Styling lives in <c>MolcaHubWindow.uss</c>.
    /// Editor-only; main thread.
    /// </remarks>
    public sealed class MolcaHubActivityRail : VisualElement
    {
        private readonly Action<string> _activateWorkspace;
        private readonly IReadOnlyList<MolcaHubActivityProvider> _providers;
        private readonly VisualElement _chips;
        private readonly VisualElement _license;
        private bool _refreshQueued;

        /// <summary>Creates the rail.</summary>
        /// <param name="activateWorkspace">
        /// Callback that focuses a Hub workspace tab by id — invoked when a chip with a
        /// <see cref="MolcaHubActivity.WorkspaceId"/> (and no custom click) is clicked.
        /// </param>
        public MolcaHubActivityRail(Action<string> activateWorkspace)
        {
            _activateWorkspace = activateWorkspace;
            AddToClassList("molca-hub-activity-rail");
            style.display = DisplayStyle.None; // hidden until a provider has something to show

            _chips = new VisualElement();
            _chips.AddToClassList("molca-hub-activity-rail__chips");
            Add(_chips);

            // Right-anchored developer-license status pill; clicking it opens the sign-in window.
            // The manipulator is attached once here (the pill's contents are rebuilt in Refresh).
            _license = new VisualElement();
            _license.AddToClassList("molca-hub-activity-rail__license");
            _license.tooltip = "Developer license — click to manage";
            _license.AddManipulator(new Clickable(() => DevLicenseWindow.Open()));
            Add(_license);

            _providers = MolcaHubActivityRegistry.CreateProviders();
            foreach (var provider in _providers)
                provider.Changed += OnProviderChanged;

            RegisterCallback<DetachFromPanelEvent>(_ => Teardown());
            Refresh();

            // Sign-in/out happens in a separate window, so poll the license pill to keep it current
            // without a full Hub rebuild. Cheap: an offline signature check plus two tiny elements.
            // The scheduled item is bound to this element's panel, so it stops when the window closes.
            schedule.Execute(() => BuildLicenseStatus()).Every(4000);
        }

        // Coalesce bursty provider notifications (a run reports repeatedly) into one rebuild on the next
        // frame so the rail never rebuilds mid-layout or several times per frame.
        private void OnProviderChanged()
        {
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            schedule.Execute(() =>
            {
                _refreshQueued = false;
                Refresh();
            });
        }

        private void Teardown()
        {
            foreach (var provider in _providers)
            {
                provider.Changed -= OnProviderChanged;
                try { provider.Dispose(); }
                catch { /* a misbehaving provider must not break teardown */ }
            }
        }

        /// <summary>Rebuilds the chip row from the providers' current activities; hides the rail when empty.</summary>
        private void Refresh()
        {
            var activities = MolcaHubActivityRegistry.Collect(_providers);
            _chips.Clear();
            foreach (var activity in activities)
                _chips.Add(BuildChip(activity));

            bool licenseShown = BuildLicenseStatus();

            // The rail shows when there is activity to display or a license status to surface; an idle,
            // unconfigured Hub still reclaims the space.
            style.display = (activities.Count > 0 || licenseShown) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Rebuilds the right-anchored developer-license pill from the current entitlement. Returns
        /// <c>false</c> and hides the pill when licensing is not configured for this distribution.
        /// </summary>
        private bool BuildLicenseStatus()
        {
            _license.Clear();

            if (!DevLicenseConfig.IsConfigured)
            {
                _license.style.display = DisplayStyle.None;
                return false;
            }

            string token = DevEntitlementStore.LoadEffective();
            var status = DevEntitlementVerifier.Evaluate(
                token, UnityEngine.SystemInfo.deviceUniqueIdentifier, out var payload);

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-activity-rail__license-dot");
            dot.AddToClassList(LicenseDotClass(status));
            _license.Add(dot);

            var label = new Label(LicenseLabel(status, payload));
            label.AddToClassList("molca-hub-activity-rail__license-label");
            _license.Add(label);

            _license.style.display = DisplayStyle.Flex;
            return true;
        }

        private static string LicenseDotClass(DevLicenseStatus status) => status switch
        {
            DevLicenseStatus.Valid => "molca-hub-activity-rail__license-dot--ok",
            DevLicenseStatus.Expired => "molca-hub-activity-rail__license-dot--warn",
            DevLicenseStatus.WrongMachine => "molca-hub-activity-rail__license-dot--warn",
            DevLicenseStatus.Invalid => "molca-hub-activity-rail__license-dot--error",
            _ => "molca-hub-activity-rail__license-dot--idle",
        };

        private static string LicenseLabel(DevLicenseStatus status, DevEntitlementPayload payload) => status switch
        {
            DevLicenseStatus.Valid => $"Licensed · {payload.licenseeId}",
            DevLicenseStatus.Expired => "License expired",
            DevLicenseStatus.WrongMachine => "License · other machine",
            DevLicenseStatus.Invalid => "License invalid",
            _ => "Not signed in",
        };

        private VisualElement BuildChip(MolcaHubActivity activity)
        {
            var chip = new VisualElement();
            chip.AddToClassList("molca-hub-activity-chip");

            // The body (dot + label + status) is the click target; a separate dismiss button avoids the
            // nested-clickable ambiguity of a button inside a button.
            var body = new VisualElement();
            body.AddToClassList("molca-hub-activity-chip__body");
            chip.Add(body);

            var dot = new VisualElement();
            dot.AddToClassList("molca-hub-activity-chip__dot");
            dot.AddToClassList(DotClass(activity.State));
            body.Add(dot);

            var label = new Label(activity.Label);
            label.AddToClassList("molca-hub-activity-chip__label");
            body.Add(label);

            if (!string.IsNullOrEmpty(activity.Status))
            {
                var status = new Label(activity.Status);
                status.AddToClassList("molca-hub-activity-chip__status");
                body.Add(status);
            }

            var onClick = ResolveClick(activity);
            if (onClick != null)
            {
                body.AddToClassList("molca-hub-activity-chip__body--clickable");
                body.AddManipulator(new Clickable(onClick));
            }

            if (activity.OnDismiss != null)
            {
                var dismiss = new Button(activity.OnDismiss) { text = "✕", tooltip = "Dismiss" };
                dismiss.AddToClassList("molca-hub-activity-chip__dismiss");
                chip.Add(dismiss);
            }

            // A thin determinate fill along the bottom of the chip (absolute-positioned track + fill).
            if (activity.Progress.HasValue)
            {
                var track = new VisualElement();
                track.AddToClassList("molca-hub-activity-chip__track");
                var fill = new VisualElement();
                fill.AddToClassList("molca-hub-activity-chip__fill");
                fill.style.width = Length.Percent(UnityEngine.Mathf.Clamp01(activity.Progress.Value) * 100f);
                track.Add(fill);
                chip.Add(track);
            }

            return chip;
        }

        private Action ResolveClick(MolcaHubActivity activity)
        {
            if (activity.OnClick != null)
                return activity.OnClick;
            if (!string.IsNullOrEmpty(activity.WorkspaceId) && _activateWorkspace != null)
                return () => _activateWorkspace(activity.WorkspaceId);
            return null;
        }

        private static string DotClass(MolcaHubActivityState state) => state switch
        {
            MolcaHubActivityState.Running => "molca-hub-activity-chip__dot--running",
            MolcaHubActivityState.Ok => "molca-status-dot--ok",
            MolcaHubActivityState.Warning => "molca-status-dot--warn",
            MolcaHubActivityState.Error => "molca-status-dot--error",
            _ => "molca-status-dot--idle",
        };
    }
}
