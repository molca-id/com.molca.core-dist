using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Networking.Routing;
using Molca.Networking.Utils;

namespace Molca.Editor.Networking.RequestConsole
{
    /// <summary>How strongly a preflight note bears on whether the send should happen.</summary>
    internal enum NetworkConsoleNoteLevel
    {
        /// <summary>Worth knowing before sending.</summary>
        Info = 0,

        /// <summary>The send will happen but something about it is surprising or risky.</summary>
        Warning,

        /// <summary>The send cannot happen, or must not.</summary>
        Blocking,
    }

    /// <summary>One thing preflight has to say about a draft.</summary>
    internal sealed class NetworkConsoleNote
    {
        /// <summary>Stable code, so a test and a bug report can name the same note.</summary>
        public string Code { get; }

        /// <summary>What it means, in a sentence.</summary>
        public string Message { get; }

        /// <summary>How strongly it bears on sending.</summary>
        public NetworkConsoleNoteLevel Level { get; }

        /// <summary>Creates a note.</summary>
        /// <param name="code">Stable code.</param>
        /// <param name="level">Severity.</param>
        /// <param name="message">Explanation.</param>
        public NetworkConsoleNote(string code, NetworkConsoleNoteLevel level, string message)
        {
            Code = code;
            Level = level;
            Message = message;
        }

        /// <inheritdoc />
        public override string ToString() => $"[{Level}] {Code}: {Message}";
    }

    /// <summary>
    /// Everything the console must show before a send, and the decision about whether the send is
    /// allowed at all.
    /// </summary>
    /// <remarks>
    /// A pure function of the catalog and the draft (see <see cref="Evaluate"/>), computed by the same
    /// <see cref="NetworkRouteResolver"/> the runtime uses. The console does not decide where a request
    /// goes or what policy applies — it reports what the catalog already decided, which is what makes
    /// "console behavior matches runtime resolution" (plan §5 exit criteria) a property rather than a
    /// hope.
    /// </remarks>
    internal sealed class NetworkConsolePreflight
    {
        /// <summary>Note codes, stable across versions.</summary>
        internal static class Codes
        {
            /// <summary>The draft names no environment or no service.</summary>
            public const string NoRoute = "network.console.no-route";

            /// <summary>The route does not resolve.</summary>
            public const string Unresolved = "network.console.unresolved";

            /// <summary>A <c>{placeholder}</c> in the path was never substituted.</summary>
            public const string UnfilledPathParameter = "network.console.unfilled-path-parameter";

            /// <summary>The target environment enforces production safety.</summary>
            public const string Production = "network.console.production";

            /// <summary>The send mutates state.</summary>
            public const string Mutation = "network.console.mutation";

            /// <summary>A production mutation, which the catalog forbids from the console.</summary>
            public const string ProductionMutationBlocked = "network.console.production-mutation-blocked";

            /// <summary>The endpoint asks for an idempotency key and none was supplied.</summary>
            public const string MissingIdempotencyKey = "network.console.missing-idempotency-key";

            /// <summary>A parameter the endpoint declares required has no value.</summary>
            public const string MissingRequiredParameter = "network.console.missing-required-parameter";

            /// <summary>The service names a credential the console may not use.</summary>
            public const string CredentialNotConsoleUsable = "network.console.credential-not-usable";

            /// <summary>The credential exists but is not authorized for the resolved host.</summary>
            public const string CredentialOutOfScope = "network.console.credential-out-of-scope";

            /// <summary>The effective policy disables request logging, so this send records nothing.</summary>
            public const string LoggingDisabled = "network.console.logging-disabled";

            /// <summary>The resolved origin is not encrypted.</summary>
            public const string InsecureScheme = "network.console.insecure-scheme";

            /// <summary>A security rule overruled a weaker authored or per-send value.</summary>
            public const string SecurityClamp = "network.console.security-clamp";

            /// <summary>A header typed into the panel looks like a credential.</summary>
            public const string CredentialShapedHeader = "network.console.credential-shaped-header";
        }

        /// <summary>The resolution the send will use. Never <c>null</c>.</summary>
        public NetworkRouteResolution Resolution { get; private set; }

        /// <summary>The destination with query values masked, for display.</summary>
        public string RedactedUri { get; private set; } = string.Empty;

        /// <summary>The effective policy, or <c>null</c> when nothing resolved.</summary>
        public NetworkEffectivePolicy Policy => Resolution?.Policy;

        /// <summary>The credential profile the service names, or empty for anonymous. Never a value.</summary>
        public string CredentialProfileId { get; private set; } = string.Empty;

        /// <summary>Whether that credential will actually be attached to this send.</summary>
        public bool CredentialWillBeSent { get; private set; }

        /// <summary>Everything preflight has to say, worst first.</summary>
        public IReadOnlyList<NetworkConsoleNote> Notes { get; private set; } = Array.Empty<NetworkConsoleNote>();

        /// <summary>Whether the send may proceed at all.</summary>
        public bool CanSend { get; private set; }

        /// <summary>
        /// Whether the user must confirm this specific send before it runs.
        /// </summary>
        /// <remarks>
        /// Per send, never remembered. A "don't ask again" on a production mutation would turn the one
        /// safeguard that stands between a test panel and real customer data into a checkbox somebody
        /// ticked last Tuesday.
        /// </remarks>
        public bool RequiresConfirmation { get; private set; }

        /// <summary>The confirmation prompt, or empty when none is required.</summary>
        public string ConfirmationMessage { get; private set; } = string.Empty;

        /// <summary>Whether this send mutates state.</summary>
        public bool IsMutation { get; private set; }

        /// <summary>Notes at or above a level.</summary>
        /// <param name="level">The minimum level.</param>
        public List<NetworkConsoleNote> NotesAtLeast(NetworkConsoleNoteLevel level)
        {
            var result = new List<NetworkConsoleNote>();
            foreach (var note in Notes)
            {
                if (note.Level >= level) result.Add(note);
            }
            return result;
        }

        /// <summary>Whether a note with this code was raised.</summary>
        /// <param name="code">The code to look for.</param>
        public bool Has(string code)
        {
            foreach (var note in Notes)
            {
                if (string.Equals(note.Code, code, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Evaluates a draft against a catalog.
        /// </summary>
        /// <param name="catalog">The catalog to resolve against.</param>
        /// <param name="draft">The draft to evaluate.</param>
        /// <returns>The preflight; never <c>null</c>.</returns>
        public static NetworkConsolePreflight Evaluate(NetworkCatalog catalog, NetworkConsoleRequest draft)
        {
            var preflight = new NetworkConsolePreflight();
            var notes = new List<NetworkConsoleNote>();

            if (catalog == null || draft == null || !draft.HasRoute)
            {
                notes.Add(new NetworkConsoleNote(Codes.NoRoute, NetworkConsoleNoteLevel.Blocking,
                    "Choose an environment and a service before sending."));
                return preflight.Finish(notes, null);
            }

            var resolver = new NetworkRouteResolver(NetworkCatalogSnapshot.Capture(catalog));
            var resolution = resolver.Resolve(draft.Route, draft.BuildQuery());
            preflight.Resolution = resolution;
            preflight.RedactedUri = LogRedaction.RedactUrl(resolution.ResolvedUri ?? string.Empty);

            var endpoint = resolution.Endpoint;
            preflight.IsMutation = ClassifyMutation(draft.Method, endpoint);
            preflight.CredentialProfileId = resolution.Credential?.Id ?? string.Empty;

            if (!resolution.Resolves)
            {
                notes.Add(new NetworkConsoleNote(Codes.Unresolved, NetworkConsoleNoteLevel.Blocking,
                    string.IsNullOrEmpty(resolution.FailureMessage)
                        ? $"Route {draft.Route} does not resolve."
                        : resolution.FailureMessage));
                return preflight.Finish(notes, catalog);
            }

            foreach (string missing in draft.UnfilledPathParameters())
            {
                notes.Add(new NetworkConsoleNote(Codes.UnfilledPathParameter, NetworkConsoleNoteLevel.Blocking,
                    $"Path parameter '{missing}' has no value, so the request would be sent with a literal " +
                    "'{" + missing + "}' in its path."));
            }

            AppendRequiredParameterNotes(draft, resolution, notes);
            AppendCredentialNotes(preflight, resolution, notes);
            AppendSafetyNotes(preflight, catalog, draft, resolution, notes);
            AppendPolicyNotes(draft, resolution, notes);
            AppendHeaderNotes(draft, notes);

            return preflight.Finish(notes, catalog);
        }

        /// <summary>
        /// Reports a parameter the endpoint declares required that carries no value.
        /// </summary>
        /// <remarks>
        /// A warning rather than a block, matching how a missing idempotency key is treated: both are the
        /// endpoint's own declaration rather than something the pipeline can check, and sending without one
        /// to see how the server answers is a legitimate thing to do from a console. An unsubstituted
        /// <c>{placeholder}</c> stays blocking, because that one puts a literal brace on the wire.
        /// <para>
        /// Sensitive parameters are named but their absence is described the same way — the note says which
        /// parameter is missing, never what its value should be.
        /// </para>
        /// </remarks>
        private static void AppendRequiredParameterNotes(
            NetworkConsoleRequest draft,
            NetworkRouteResolution resolution,
            List<NetworkConsoleNote> notes)
        {
            var endpoint = resolution.Endpoint;
            if (endpoint == null) return;

            AppendMissing(endpoint.QueryParameters, draft.QueryParameters, "query parameter", notes);
            AppendMissing(endpoint.HeaderParameters, draft.Headers, "header", notes);
        }

        private static void AppendMissing(
            IReadOnlyList<NetworkParameterDefinition> declared,
            List<NetworkConsoleRequest.Entry> supplied,
            string kind,
            List<NetworkConsoleNote> notes)
        {
            if (declared == null) return;

            foreach (var parameter in declared)
            {
                if (parameter == null || !parameter.Required || string.IsNullOrEmpty(parameter.Name))
                    continue;

                if (HasValue(supplied, parameter.Name)) continue;

                notes.Add(new NetworkConsoleNote(
                    Codes.MissingRequiredParameter, NetworkConsoleNoteLevel.Warning,
                    $"The endpoint declares {kind} '{parameter.Name}' as required and it has no value."));
            }
        }

        /// <summary>Whether an enabled entry supplies a non-empty value for a name.</summary>
        private static bool HasValue(List<NetworkConsoleRequest.Entry> entries, string name)
        {
            foreach (var entry in entries)
            {
                if (entry == null || !entry.Enabled) continue;

                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(entry.Value))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AppendCredentialNotes(
            NetworkConsolePreflight preflight,
            NetworkRouteResolution resolution,
            List<NetworkConsoleNote> notes)
        {
            var credential = resolution.Credential;
            if (credential == null || credential.IsAnonymous)
            {
                preflight.CredentialWillBeSent = false;
                return;
            }

            if (!credential.UsableFromRequestConsole)
            {
                // The gate is enforced in NetworkConsoleRunner, not here — this note only explains why
                // the response may be a 401 that the running game would not get.
                preflight.CredentialWillBeSent = false;
                notes.Add(new NetworkConsoleNote(Codes.CredentialNotConsoleUsable, NetworkConsoleNoteLevel.Warning,
                    $"Credential profile '{credential.Id}' is not marked usable from the request console, so " +
                    "this send goes out anonymous. A 401 here does not mean the route is broken at runtime."));
                return;
            }

            if (!resolution.CredentialAppliesToHost)
            {
                preflight.CredentialWillBeSent = false;
                notes.Add(new NetworkConsoleNote(Codes.CredentialOutOfScope, NetworkConsoleNoteLevel.Warning,
                    $"Credential profile '{credential.Id}' is not scoped to host '{resolution.Host}', so this " +
                    "send goes out anonymous."));
                return;
            }

            preflight.CredentialWillBeSent = true;
        }

        private static void AppendSafetyNotes(
            NetworkConsolePreflight preflight,
            NetworkCatalog catalog,
            NetworkConsoleRequest draft,
            NetworkRouteResolution resolution,
            List<NetworkConsoleNote> notes)
        {
            bool production = resolution.IsProduction;

            if (production)
            {
                notes.Add(new NetworkConsoleNote(Codes.Production, NetworkConsoleNoteLevel.Warning,
                    $"Environment '{resolution.Environment?.Id}' enforces production safety. This request " +
                    "reaches real infrastructure."));
            }

            if (preflight.IsMutation)
            {
                notes.Add(new NetworkConsoleNote(Codes.Mutation, NetworkConsoleNoteLevel.Warning,
                    $"{draft.Method} changes state on the server. It is not a read you can repeat freely."));
            }

            if (production && preflight.IsMutation && !catalog.AllowProductionConsoleMutations)
            {
                notes.Add(new NetworkConsoleNote(Codes.ProductionMutationBlocked, NetworkConsoleNoteLevel.Blocking,
                    "This catalog does not allow production mutations from the request console. Enable " +
                    "'Allow production console mutations' on the catalog if this is genuinely intended."));
            }

            var endpoint = resolution.Endpoint;
            if (endpoint != null && endpoint.RequiresIdempotencyKey && string.IsNullOrEmpty(draft.IdempotencyKey))
            {
                notes.Add(new NetworkConsoleNote(Codes.MissingIdempotencyKey, NetworkConsoleNoteLevel.Warning,
                    $"Endpoint '{endpoint.Id}' declares that it requires an idempotency key and none was " +
                    "supplied, so a retry could apply this change twice."));
            }

            if (!string.IsNullOrEmpty(resolution.Origin) &&
                Uri.TryCreate(resolution.Origin, UriKind.Absolute, out var origin) &&
                !NetworkOrigin.IsSecureScheme(origin.Scheme))
            {
                notes.Add(new NetworkConsoleNote(Codes.InsecureScheme, NetworkConsoleNoteLevel.Warning,
                    $"The resolved origin '{resolution.Origin}' is not encrypted, so headers and body travel " +
                    "in the clear."));
            }
        }

        private static void AppendPolicyNotes(
            NetworkConsoleRequest draft,
            NetworkRouteResolution resolution,
            List<NetworkConsoleNote> notes)
        {
            var policy = resolution.Policy;
            if (policy == null) return;

            if (!policy.LogRequests.Value)
            {
                notes.Add(new NetworkConsoleNote(Codes.LoggingDisabled, NetworkConsoleNoteLevel.Info,
                    "The effective policy disables request logging, so this send will not appear in history."));
            }

            if (policy.HasSecurityClamps)
            {
                foreach (string clamp in policy.SecurityClamps)
                {
                    notes.Add(new NetworkConsoleNote(Codes.SecurityClamp, NetworkConsoleNoteLevel.Info, clamp));
                }
            }
        }

        private static void AppendHeaderNotes(NetworkConsoleRequest draft, List<NetworkConsoleNote> notes)
        {
            foreach (var header in draft.Headers)
            {
                if (header == null || !header.Enabled || string.IsNullOrEmpty(header.Key)) continue;
                if (!LogRedaction.IsSensitiveHeader(header.Key)) continue;

                notes.Add(new NetworkConsoleNote(Codes.CredentialShapedHeader, NetworkConsoleNoteLevel.Warning,
                    $"Header '{header.Key}' looks like a credential. Its value is never persisted, logged, or " +
                    "exported, but a credential profile is the supported way to authenticate a route."));
            }
        }

        /// <summary>
        /// Whether a method and endpoint combination changes server state.
        /// </summary>
        /// <param name="method">The method being sent.</param>
        /// <param name="endpoint">The endpoint template, or <c>null</c>.</param>
        /// <remarks>
        /// The two signals are OR-ed rather than ranked, because
        /// <see cref="NetworkMutationClass.Safe"/> is the enum's zero value: an author who never touched
        /// the field on a <c>POST</c> endpoint is indistinguishable from one who deliberately marked it
        /// read-only. Letting the unset default downgrade a <c>POST</c> to "safe" would silently drop the
        /// production confirmation on exactly the endpoints that were never reviewed.
        /// </remarks>
        internal static bool ClassifyMutation(HttpMethod method, NetworkEndpointDefinition endpoint)
        {
            if (endpoint != null && endpoint.MutationClass != NetworkMutationClass.Safe)
                return true;

            switch (method)
            {
                case HttpMethod.POST:
                case HttpMethod.PUT:
                case HttpMethod.PATCH:
                case HttpMethod.DELETE:
                    return true;
                default:
                    return false;
            }
        }

        private NetworkConsolePreflight Finish(List<NetworkConsoleNote> notes, NetworkCatalog catalog)
        {
            notes.Sort((a, b) => b.Level.CompareTo(a.Level));
            Notes = notes;

            CanSend = true;
            foreach (var note in notes)
            {
                if (note.Level == NetworkConsoleNoteLevel.Blocking) CanSend = false;
            }

            bool production = Resolution != null && Resolution.IsProduction;
            RequiresConfirmation = CanSend && production && IsMutation;
            ConfirmationMessage = RequiresConfirmation
                ? $"Send {Resolution.Route} — a {(Resolution.Endpoint != null ? Resolution.Endpoint.Id : "request")} " +
                  $"that changes state — to production?\n\n{RedactedUri}\n\n" +
                  "This reaches real infrastructure and cannot be undone from here."
                : string.Empty;

            return this;
        }
    }
}
