using System;
using System.Collections.Generic;
using System.Text;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// Stable identifiers for the Network workspace's views.
    /// </summary>
    /// <remarks>
    /// Strings rather than an enum, deliberately. A deep link is a value another surface constructs —
    /// a settings leaf, a Doctor finding, an MCP result — and none of those should have to reference an
    /// editor enum to say where they want to go. Adding a view here never changes an existing caller.
    /// <para>
    /// These ids are API: they appear in navigation targets that other tools build. Add, never rename.
    /// </para>
    /// </remarks>
    public static class NetworkHubViews
    {
        /// <summary>Project readiness, the binding matrix, and the prioritized action list.</summary>
        public const string Overview = "overview";

        /// <summary>Environment profiles.</summary>
        public const string Environments = "environments";

        /// <summary>Service definitions and their per-environment binding grid.</summary>
        public const string Services = "services";

        /// <summary>Endpoint collections and endpoint templates.</summary>
        public const string Endpoints = "endpoints";

        /// <summary>Policy profiles and the effective-policy inspector.</summary>
        public const string Policies = "policies";

        /// <summary>Credential profile metadata and provider readiness.</summary>
        public const string Credentials = "credentials";

        /// <summary>Streaming provider assets and the routes they connect through.</summary>
        public const string Providers = "providers";

        /// <summary>The request console: preflight, send, and the redacted result.</summary>
        public const string Console = "console";

        /// <summary>Live pipeline telemetry: in-flight requests, route state, and streaming sessions.</summary>
        public const string Live = "live";

        /// <summary>Catalog validation findings, grouped by severity.</summary>
        public const string Diagnostics = "diagnostics";

        /// <summary>Every view id the workspace currently renders, in rail order.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Overview, Environments, Services, Endpoints, Policies, Credentials, Providers, Console, Live,
            Diagnostics,
        };

        /// <summary>The rail label for a view id.</summary>
        /// <param name="viewId">A view id; an unknown id is title-cased so a fork's view is still readable.</param>
        public static string Label(string viewId)
        {
            switch (viewId)
            {
                case Overview: return "Overview";
                case Environments: return "Environments";
                case Services: return "Services";
                case Endpoints: return "Endpoints";
                case Policies: return "Policies";
                case Credentials: return "Credentials";
                case Providers: return "Providers";
                case Console: return "Console";
                case Live: return "Live";
                case Diagnostics: return "Diagnostics";
                default:
                    return string.IsNullOrEmpty(viewId)
                        ? Label(Overview)
                        : char.ToUpperInvariant(viewId[0]) + viewId.Substring(1);
            }
        }

        /// <summary>Whether <paramref name="viewId"/> is a view this workspace renders.</summary>
        /// <param name="viewId">The id to test.</param>
        public static bool IsKnown(string viewId)
        {
            if (string.IsNullOrEmpty(viewId)) return false;
            for (int i = 0; i < All.Count; i++)
            {
                if (string.Equals(All[i], viewId, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Where in the Network workspace a caller wants to land: a view, optionally an entity within it, and
    /// optionally the environment to preview it under.
    /// </summary>
    /// <remarks>
    /// A value object rather than a set of static pending fields (plan §7.3). Static fields would make
    /// two near-simultaneous navigations silently interleave, and would give every future navigation
    /// source a new field to remember to clear.
    /// <para>
    /// The serialized form is <c>workspace=network&amp;view=services&amp;entity=identity&amp;environment=staging</c>.
    /// It round-trips through <see cref="TryParse"/> so a finding or an MCP result can carry a link as a
    /// plain string.
    /// </para>
    /// </remarks>
    public readonly struct NetworkHubNavigationTarget : IEquatable<NetworkHubNavigationTarget>
    {
        /// <summary>The workspace key used in the serialized form.</summary>
        public const string WorkspaceKey = "network";

        /// <summary>The view to select. Empty means the workspace's remembered view.</summary>
        public readonly string ViewId;

        /// <summary>The entity to select within the view, or empty.</summary>
        public readonly string EntityId;

        /// <summary>The environment to preview under, or empty to keep the current preview.</summary>
        public readonly string EnvironmentId;

        /// <summary>Whether this target names anything at all.</summary>
        public bool IsEmpty =>
            string.IsNullOrEmpty(ViewId) &&
            string.IsNullOrEmpty(EntityId) &&
            string.IsNullOrEmpty(EnvironmentId);

        /// <summary>Creates a target.</summary>
        /// <param name="viewId">The view to select; see <see cref="NetworkHubViews"/>.</param>
        /// <param name="entityId">The entity to select, or <c>null</c>.</param>
        /// <param name="environmentId">The environment to preview under, or <c>null</c>.</param>
        public NetworkHubNavigationTarget(string viewId, string entityId = null, string environmentId = null)
        {
            ViewId = viewId ?? string.Empty;
            EntityId = entityId ?? string.Empty;
            EnvironmentId = environmentId ?? string.Empty;
        }

        /// <summary>A target naming a service, optionally in one environment.</summary>
        /// <param name="serviceId">The service to select.</param>
        /// <param name="environmentId">The environment to preview under, or <c>null</c>.</param>
        public static NetworkHubNavigationTarget Service(string serviceId, string environmentId = null) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Services, serviceId, environmentId);

        /// <summary>A target naming an environment.</summary>
        /// <param name="environmentId">The environment to select and preview under.</param>
        public static NetworkHubNavigationTarget Environment(string environmentId) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Environments, environmentId, environmentId);

        /// <summary>A target naming an endpoint.</summary>
        /// <param name="endpointId">The endpoint to select.</param>
        public static NetworkHubNavigationTarget Endpoint(string endpointId) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Endpoints, endpointId);

        /// <summary>A target naming a streaming provider asset.</summary>
        /// <param name="providerId">The provider to select, or <c>null</c>.</param>
        public static NetworkHubNavigationTarget Provider(string providerId = null) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Providers, providerId);

        /// <summary>A target naming the request console, optionally preloaded with an endpoint.</summary>
        /// <param name="endpointId">The endpoint to load into the draft, or <c>null</c>.</param>
        /// <param name="environmentId">The environment to send under, or <c>null</c>.</param>
        public static NetworkHubNavigationTarget Console(string endpointId = null, string environmentId = null) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Console, endpointId, environmentId);

        /// <summary>A target naming the live telemetry view.</summary>
        /// <param name="serviceId">A service to filter to, or <c>null</c>.</param>
        public static NetworkHubNavigationTarget Live(string serviceId = null) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Live, serviceId);

        /// <summary>A target naming the diagnostics view.</summary>
        /// <param name="findingCode">A finding code to scroll to, or <c>null</c>.</param>
        public static NetworkHubNavigationTarget Diagnostics(string findingCode = null) =>
            new NetworkHubNavigationTarget(NetworkHubViews.Diagnostics, findingCode);

        /// <summary>
        /// Parses the serialized form.
        /// </summary>
        /// <param name="text">A string like <c>workspace=network&amp;view=services&amp;entity=identity</c>.</param>
        /// <param name="target">The parsed target on success.</param>
        /// <returns><c>false</c> when the text is empty or names a different workspace.</returns>
        /// <remarks>
        /// Unknown keys are ignored rather than rejected, so a link written by a newer version still
        /// navigates to the part this version understands instead of failing outright.
        /// </remarks>
        public static bool TryParse(string text, out NetworkHubNavigationTarget target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string workspace = null, view = null, entity = null, environment = null;

            foreach (string pair in text.Split('&'))
            {
                int split = pair.IndexOf('=');
                if (split <= 0) continue;

                string key = pair.Substring(0, split).Trim();
                string value = Uri.UnescapeDataString(pair.Substring(split + 1).Trim());

                switch (key)
                {
                    case "workspace": workspace = value; break;
                    case "view": view = value; break;
                    case "entity": entity = value; break;
                    case "environment": environment = value; break;
                }
            }

            if (!string.Equals(workspace, WorkspaceKey, StringComparison.Ordinal))
                return false;

            target = new NetworkHubNavigationTarget(view, entity, environment);
            return true;
        }

        /// <summary>Renders the serialized form.</summary>
        public override string ToString()
        {
            var text = new StringBuilder("workspace=").Append(WorkspaceKey);
            Append(text, "view", ViewId);
            Append(text, "entity", EntityId);
            Append(text, "environment", EnvironmentId);
            return text.ToString();
        }

        private static void Append(StringBuilder text, string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
                text.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        /// <inheritdoc />
        public bool Equals(NetworkHubNavigationTarget other) =>
            string.Equals(ViewId, other.ViewId, StringComparison.Ordinal) &&
            string.Equals(EntityId, other.EntityId, StringComparison.Ordinal) &&
            string.Equals(EnvironmentId, other.EnvironmentId, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is NetworkHubNavigationTarget other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = ViewId.GetHashCode();
                hash = (hash * 397) ^ EntityId.GetHashCode();
                return (hash * 397) ^ EnvironmentId.GetHashCode();
            }
        }
    }
}
