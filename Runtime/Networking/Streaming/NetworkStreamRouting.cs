using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Networking.Streaming
{
    /// <summary>
    /// Resolves a streaming route against the active network subsystem.
    /// </summary>
    /// <remarks>
    /// The entry point protocol assemblies use. <c>Molca.Networking.WebSocket</c> and
    /// <c>Molca.Networking.SocketIO</c> compile only when their own dependency is present, so Core cannot
    /// name their types — but they can name this, and through it they get the same origin, allowed-host,
    /// production-scheme, and credential-scope answers an HTTP request gets.
    /// <para>
    /// Separate from <see cref="NetworkStreamBinding.Resolve"/>, which takes a resolver and is therefore
    /// pure and testable. This adds only the step of locating the running subsystem, which is the part a
    /// test does not want.
    /// </para>
    /// </remarks>
    public static class NetworkStreamRouting
    {
        /// <summary>
        /// Resolves a route for a protocol.
        /// </summary>
        /// <param name="route">The route to resolve.</param>
        /// <param name="protocol">The protocol whose origin to resolve.</param>
        /// <param name="binding">The binding on success, or <c>null</c>.</param>
        /// <param name="failure">Why resolution failed, or <c>null</c>.</param>
        /// <returns><c>true</c> when <paramref name="binding"/> carries a usable destination.</returns>
        /// <remarks>
        /// A route that does not resolve is a failure, never a fallback to an authored URL. Falling back
        /// would mean a provider whose catalog binding was deleted quietly resumes connecting to whatever
        /// URL was left in the asset — which is the drift the catalog exists to remove.
        /// </remarks>
        public static bool TryResolve(
            NetworkStreamRoute route,
            NetworkProtocols protocol,
            out NetworkStreamBinding binding,
            out string failure)
        {
            binding = null;
            failure = null;

            if (!route.IsConfigured)
            {
                failure = "No catalog service is set on this route.";
                return false;
            }

            var network = RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>();
            if (network == null || network.Resolver == null)
            {
                failure =
                    $"No NetworkRuntimeSubsystem is active, so catalog service '{route.ServiceId}' cannot " +
                    "be resolved.";
                return false;
            }

            binding = NetworkStreamBinding.Resolve(network.Resolver, route, protocol);

            if (!binding.Resolves)
            {
                failure = binding.FailureMessage;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a credential may be attached to a resolved stream destination.
        /// </summary>
        /// <param name="binding">The resolved binding.</param>
        /// <param name="reason">Why it may not, or <c>null</c>.</param>
        /// <returns><c>true</c> when the service's credential covers the resolved host.</returns>
        /// <remarks>
        /// Used by providers that acquire their own token synchronously — a Socket.IO or WebSocket
        /// handshake builds its headers before it connects. It is the same question
        /// <c>NetworkCredentialRegistry.AcquireForHostAsync</c> answers, asked without acquiring, so a
        /// provider can refuse to attach a token to a host the catalog scoped it away from.
        /// <para>
        /// A service that names no credential profile returns <c>true</c>: the catalog has expressed no
        /// opinion, so the provider's own authentication setting stands. Scoping only overrules a
        /// provider when there is a scope to overrule it with.
        /// </para>
        /// </remarks>
        public static bool AllowsCredential(NetworkStreamBinding binding, out string reason)
        {
            reason = null;

            if (binding?.Credential == null || binding.Credential.IsAnonymous)
                return true;

            if (binding.CredentialAppliesToHost)
                return true;

            reason =
                $"Credential profile '{binding.Credential.Id}' is not scoped to host '{binding.Host}', so " +
                "this stream connects anonymously.";
            return false;
        }

        /// <summary>Logs a resolution failure once, in the shape every provider should use.</summary>
        /// <param name="providerName">The asset name, for the log prefix.</param>
        /// <param name="protocol">The protocol that failed to resolve.</param>
        /// <param name="failure">The failure text.</param>
        public static void LogResolutionFailure(string providerName, NetworkProtocols protocol, string failure)
        {
            Debug.LogError($"[Network] {providerName}: {protocol} route did not resolve. {failure}");
        }
    }
}
