using System;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Networking.Security
{
    /// <summary>
    /// A credential obtained from a provider: the value, when it expires, and nothing else.
    /// </summary>
    /// <remarks>
    /// <see cref="Value"/> is the only place in the networking stack a secret exists, and it lives only
    /// as long as the attempt that uses it. It is never serialized, never logged, and never copied into
    /// a diagnostic — <see cref="ToString"/> is overridden to make an accidental interpolation harmless
    /// rather than a leak.
    /// </remarks>
    public readonly struct NetworkCredential
    {
        /// <summary>An absent credential. Requests using it are sent anonymously.</summary>
        public static readonly NetworkCredential None = default;

        /// <summary>The secret value, without the profile's scheme prefix.</summary>
        public readonly string Value;

        /// <summary>When the credential expires, or <see cref="DateTime.MaxValue"/> when it does not.</summary>
        public readonly DateTime ExpiresUtc;

        /// <summary>Creates a credential.</summary>
        /// <param name="value">The secret value.</param>
        /// <param name="expiresUtc">Expiry, or <c>null</c> for none.</param>
        public NetworkCredential(string value, DateTime? expiresUtc = null)
        {
            Value = value;
            ExpiresUtc = expiresUtc ?? DateTime.MaxValue;
        }

        /// <summary>Whether a value is present.</summary>
        public bool HasValue => !string.IsNullOrEmpty(Value);

        /// <summary>Whether the credential has expired at <paramref name="nowUtc"/>.</summary>
        /// <param name="nowUtc">The current UTC time.</param>
        public bool IsExpired(DateTime nowUtc) => ExpiresUtc <= nowUtc;

        /// <summary>
        /// Never renders the value. Returns a presence marker so an accidental interpolation into a log
        /// line cannot leak the secret.
        /// </summary>
        public override string ToString() => HasValue ? "[credential]" : "[no credential]";
    }

    /// <summary>
    /// Supplies secret material for a <see cref="NetworkCredentialProfile"/>.
    /// </summary>
    /// <remarks>
    /// The catalog stores which provider to ask; implementations answer with the value. Register
    /// implementations with the network subsystem — a project can add its own for
    /// <see cref="NetworkCredentialProviderKind.Custom"/> without touching Core.
    /// <para>
    /// A provider must never write a credential to a <see cref="ScriptableObject"/>,
    /// <c>PlayerPrefs</c>, <c>EditorPrefs</c>, or a log.
    /// </para>
    /// </remarks>
    public interface INetworkCredentialProvider
    {
        /// <summary>Which profile kind this provider answers for.</summary>
        NetworkCredentialProviderKind Kind { get; }

        /// <summary>
        /// Acquires a credential for a profile.
        /// </summary>
        /// <param name="profile">The profile describing what to fetch. Metadata only.</param>
        /// <param name="forceRefresh">
        /// When <c>true</c>, bypass any cached value — used after a 401 when the profile's refresh mode
        /// permits it.
        /// </param>
        /// <param name="cancellationToken">Cancels acquisition.</param>
        /// <returns>
        /// The credential, or <see cref="NetworkCredential.None"/> when none is available. Returning
        /// <c>None</c> sends the request anonymously rather than failing it, so a missing optional
        /// credential does not break an endpoint that tolerates anonymous access.
        /// </returns>
        Awaitable<NetworkCredential> AcquireAsync(
            NetworkCredentialProfile profile,
            bool forceRefresh,
            CancellationToken cancellationToken);
    }
}
