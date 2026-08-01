using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;

namespace Molca.Editor.Networking.RequestConsole
{
    /// <summary>
    /// What the request console is about to send: a route, either an endpoint template or a relative
    /// path, the parameters filled in, and the safe per-send policy options.
    /// </summary>
    /// <remarks>
    /// A plain model with no UI and no I/O, so preflight and execution are testable without building a
    /// view. It is also the reason the console cannot address an arbitrary URL: a draft names an
    /// <em>environment and service</em>, and the origin comes from the catalog binding. There is
    /// deliberately no "full URL" field — an editor control that sends wherever you type, carrying
    /// whatever headers are in the panel, is the shape of the leak this whole subsystem exists to close
    /// (plan §7.11 safeguards).
    /// <para>
    /// <see cref="Headers"/> and <see cref="Body"/> live in memory for the lifetime of the workspace and
    /// are never written to preferences, an asset, or an export. Only the non-secret selection — route,
    /// endpoint, method, path — is persisted, by the view.
    /// </para>
    /// </remarks>
    internal sealed class NetworkConsoleRequest
    {
        /// <summary>One editable key/value pair.</summary>
        internal sealed class Entry
        {
            /// <summary>The key.</summary>
            public string Key = string.Empty;

            /// <summary>The value.</summary>
            public string Value = string.Empty;

            /// <summary>Whether this entry is sent.</summary>
            public bool Enabled = true;

            /// <summary>Creates an entry.</summary>
            /// <param name="key">The key.</param>
            /// <param name="value">The value.</param>
            public Entry(string key = null, string value = null)
            {
                Key = key ?? string.Empty;
                Value = value ?? string.Empty;
            }
        }

        /// <summary>The environment to send under.</summary>
        public string EnvironmentId = string.Empty;

        /// <summary>The service to send to.</summary>
        public string ServiceId = string.Empty;

        /// <summary>The endpoint template to apply, or empty for an ad-hoc relative path.</summary>
        public string EndpointId = string.Empty;

        /// <summary>
        /// The path relative to the service origin. Used when <see cref="EndpointId"/> is empty, and as
        /// the substituted path when it is not.
        /// </summary>
        public string RelativePath = string.Empty;

        /// <summary>The HTTP method.</summary>
        public HttpMethod Method = HttpMethod.GET;

        /// <summary>Path parameter values, keyed by parameter name.</summary>
        public readonly List<Entry> PathParameters = new List<Entry>();

        /// <summary>Query parameters appended to the path.</summary>
        public readonly List<Entry> QueryParameters = new List<Entry>();

        /// <summary>Request headers. Never persisted; see the type remarks.</summary>
        public readonly List<Entry> Headers = new List<Entry>();

        /// <summary>The body kind.</summary>
        public BodyType BodyType = BodyType.None;

        /// <summary>The JSON or text body. Never persisted.</summary>
        public string Body = string.Empty;

        /// <summary>Form fields, for <see cref="BodyType.Form"/>. Never persisted.</summary>
        public readonly List<Entry> FormFields = new List<Entry>();

        /// <summary>An idempotency key to send, or empty.</summary>
        public string IdempotencyKey = string.Empty;

        /// <summary>Per-send overall timeout, or <c>null</c> to inherit the effective policy.</summary>
        public float? TimeoutSecondsOverride;

        /// <summary>Per-send retry switch, or <c>null</c> to inherit.</summary>
        public bool? RetryEnabledOverride;

        /// <summary>
        /// Whether this send may record a redacted response body preview in history. Defaults off.
        /// </summary>
        /// <remarks>
        /// Off by default because a body preview is the one part of a diagnostic that carries whatever the
        /// server chose to return. <c>LogRedaction.RedactJsonBody</c> masks credential-shaped fields, but
        /// opting in should still be a decision rather than a default.
        /// </remarks>
        public bool CaptureBody;

        /// <summary>The route this draft targets.</summary>
        public NetworkRouteKey Route => new NetworkRouteKey(EnvironmentId, ServiceId);

        /// <summary>Whether the draft names both an environment and a service.</summary>
        public bool HasRoute => !string.IsNullOrEmpty(EnvironmentId) && !string.IsNullOrEmpty(ServiceId);

        /// <summary>Whether the draft applies an authored endpoint template.</summary>
        public bool UsesEndpoint => !string.IsNullOrEmpty(EndpointId);

        /// <summary>
        /// Fills the draft in from an endpoint template: method, path, and one entry per declared
        /// parameter.
        /// </summary>
        /// <param name="endpoint">The endpoint to adopt, or <c>null</c> to clear back to an ad-hoc path.</param>
        /// <remarks>
        /// Sensitive parameters are seeded empty even when the template carries a default. A default that
        /// looks like a token belongs to whoever authored the catalog, not to a panel that will render it.
        /// </remarks>
        public void AdoptEndpoint(NetworkEndpointDefinition endpoint)
        {
            PathParameters.Clear();
            QueryParameters.Clear();
            Headers.Clear();

            if (endpoint == null)
            {
                EndpointId = string.Empty;
                return;
            }

            EndpointId = endpoint.Id;
            Method = endpoint.Method;
            RelativePath = endpoint.RelativePath ?? string.Empty;
            BodyType = endpoint.BodyType;
            Body = endpoint.BodyType == BodyType.None ? string.Empty : endpoint.RequestBodyExample ?? string.Empty;

            Seed(PathParameters, endpoint.PathParameters);
            Seed(QueryParameters, endpoint.QueryParameters);

            // Header parameters are seeded too. Without this an endpoint that declares a required header
            // gave the panel no field for it, so the only way to satisfy its own declaration was to know
            // to add the header by hand.
            Seed(Headers, endpoint.HeaderParameters);
        }

        private static void Seed(List<Entry> target, IReadOnlyList<NetworkParameterDefinition> declared)
        {
            if (declared == null) return;

            foreach (var parameter in declared)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.Name)) continue;
                target.Add(new Entry(parameter.Name, parameter.Sensitive ? string.Empty : parameter.DefaultValue));
            }
        }

        /// <summary>
        /// The relative path with <c>{name}</c> path parameters substituted.
        /// </summary>
        /// <returns>The substituted path; unfilled placeholders are left in place so preflight can report them.</returns>
        public string ResolvePath()
        {
            string path = RelativePath ?? string.Empty;

            foreach (var entry in PathParameters)
            {
                if (entry == null || !entry.Enabled || string.IsNullOrEmpty(entry.Key)) continue;
                path = path.Replace("{" + entry.Key + "}", System.Uri.EscapeDataString(entry.Value ?? string.Empty));
            }

            return path;
        }

        /// <summary>Path parameter names still unsubstituted in <see cref="ResolvePath"/>.</summary>
        /// <returns>The placeholder names, without braces.</returns>
        public List<string> UnfilledPathParameters()
        {
            var missing = new List<string>();
            string path = ResolvePath();

            int index = 0;
            while (index < path.Length)
            {
                int open = path.IndexOf('{', index);
                if (open < 0) break;

                int close = path.IndexOf('}', open + 1);
                if (close < 0) break;

                missing.Add(path.Substring(open + 1, close - open - 1));
                index = close + 1;
            }

            return missing;
        }

        /// <summary>
        /// Builds the transport request.
        /// </summary>
        /// <returns>A fresh <see cref="HttpRequest"/>; the draft is not mutated.</returns>
        /// <remarks>
        /// <c>useFullUrl</c> is left <c>false</c> and <c>url</c> carries only the relative path, so the
        /// routed pipeline supplies the origin. That is what makes the console's destination a property of
        /// the catalog rather than of this panel.
        /// </remarks>
        public HttpRequest BuildHttpRequest()
        {
            var request = new HttpRequest
            {
                name = "Molca request console",
                method = Method,
                url = ResolvePath(),
                useFullUrl = false,
                bodyType = BodyType,
                expectedResponseType = ResponseType.Text,
            };

            foreach (var header in Headers)
            {
                if (header != null && header.Enabled && !string.IsNullOrEmpty(header.Key))
                    request.AddHeader(header.Key, header.Value ?? string.Empty);
            }

            foreach (var parameter in QueryParameters)
            {
                if (parameter != null && parameter.Enabled && !string.IsNullOrEmpty(parameter.Key))
                    request.AddParam(parameter.Key, parameter.Value ?? string.Empty);
            }

            switch (BodyType)
            {
                case BodyType.Json:
                    request.jsonBody = Body ?? string.Empty;
                    break;

                case BodyType.Form:
                    foreach (var field in FormFields)
                    {
                        if (field != null && field.Enabled && !string.IsNullOrEmpty(field.Key))
                            request.AddFormField(field.Key, field.Value ?? string.Empty);
                    }
                    break;
            }

            return request;
        }

        /// <summary>
        /// Builds the routed query, including the per-send policy override.
        /// </summary>
        /// <returns>The query the routed client should resolve with.</returns>
        public NetworkRouteQuery BuildQuery()
        {
            var over = new NetworkSendPolicyOverride
            {
                OverallTimeoutSeconds = TimeoutSecondsOverride,
                RetryEnabled = RetryEnabledOverride,
                CaptureBodies = CaptureBody,
                IdempotencyKey = string.IsNullOrEmpty(IdempotencyKey) ? null : IdempotencyKey,
            };

            return new NetworkRouteQuery(
                NetworkProtocols.Http,
                UsesEndpoint ? EndpointId : null,
                // The substituted path is passed even with an endpoint selected, because the template's
                // own path still carries the {placeholders}.
                ResolvePath(),
                over);
        }
    }
}
