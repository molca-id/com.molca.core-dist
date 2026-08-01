using System;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Networking.Security
{
    /// <summary>
    /// Reads a credential from a process environment variable named by the profile's
    /// <see cref="NetworkCredentialProfile.ProviderKey"/>.
    /// </summary>
    /// <remarks>
    /// The one provider Core can implement without owning anything else: the others need a live auth
    /// session, editor secure storage, or a platform key store, so the SDK, the project, or the editor
    /// layer registers those.
    /// <para>
    /// Intended for CI and headless runs, where a secret is injected into the environment rather than
    /// committed. The value is read on demand and never cached to disk, serialized, or logged.
    /// </para>
    /// </remarks>
    public sealed class EnvironmentVariableCredentialProvider : INetworkCredentialProvider
    {
        /// <inheritdoc />
        public NetworkCredentialProviderKind Kind => NetworkCredentialProviderKind.EnvironmentVariable;

        /// <inheritdoc />
        public Awaitable<NetworkCredential> AcquireAsync(
            NetworkCredentialProfile profile,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new AwaitableCompletionSource<NetworkCredential>();

            string variable = profile?.ProviderKey;
            if (string.IsNullOrEmpty(variable))
            {
                Debug.LogWarning(
                    $"[Network] Credential profile '{profile?.Id}' uses the environment-variable provider " +
                    "but names no variable in its provider key.");

                completion.SetResult(NetworkCredential.None);
                return completion.Awaitable;
            }

            string value = null;
            try
            {
                value = Environment.GetEnvironmentVariable(variable);
            }
            catch (Exception e)
            {
                // Some platforms restrict environment access. Degrading to anonymous is better than
                // failing the send, and the message names the variable, never a value.
                Debug.LogWarning($"[Network] Could not read environment variable '{variable}': {e.Message}");
            }

            completion.SetResult(
                string.IsNullOrEmpty(value) ? NetworkCredential.None : new NetworkCredential(value));

            return completion.Awaitable;
        }
    }
}
