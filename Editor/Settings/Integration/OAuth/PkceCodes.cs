using System;
using System.Security.Cryptography;
using System.Text;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// A PKCE (RFC 7636) verifier/challenge pair plus an anti-forgery <c>state</c> value for an
    /// authorization-code flow.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Pure and network-free, so it is directly unit-testable (S256 correctness, verifier length,
    /// base64url alphabet). Reuses the cryptographic-RNG + URL-safe-base64 pattern from
    /// <c>McpAuth</c> (Sprint 14.2): PKCE is what lets a distributable editor tool run an
    /// authorization-code flow with <b>no</b> embedded <c>client_secret</c> — the verifier proves the
    /// token request came from the same client that started the flow.
    /// </remarks>
    public sealed class PkceCodes
    {
        private PkceCodes(string verifier, string challenge, string state)
        {
            CodeVerifier = verifier;
            CodeChallenge = challenge;
            State = state;
        }

        /// <summary>The high-entropy <c>code_verifier</c> (43–128 unreserved chars per RFC 7636).</summary>
        public string CodeVerifier { get; }

        /// <summary>The S256 <c>code_challenge</c>: base64url(SHA-256(<see cref="CodeVerifier"/>)).</summary>
        public string CodeChallenge { get; }

        /// <summary>The challenge method literal sent to the authorize endpoint.</summary>
        public string CodeChallengeMethod => "S256";

        /// <summary>A cryptographically-random anti-forgery value echoed back on the redirect.</summary>
        public string State { get; }

        /// <summary>
        /// Generates a fresh verifier, its S256 challenge, and a random state.
        /// </summary>
        /// <returns>A new <see cref="PkceCodes"/>.</returns>
        public static PkceCodes Generate()
        {
            // 32 random bytes → 43-char base64url verifier (within the 43–128 RFC range).
            var verifier = RandomUrlSafe(32);
            var challenge = Challenge(verifier);
            var state = RandomUrlSafe(16);
            return new PkceCodes(verifier, challenge, state);
        }

        /// <summary>
        /// Computes the S256 <c>code_challenge</c> for a given verifier: base64url(SHA-256(ASCII(verifier))).
        /// </summary>
        /// <param name="verifier">The <c>code_verifier</c>.</param>
        /// <returns>The base64url-encoded challenge.</returns>
        public static string Challenge(string verifier)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(hash);
        }

        /// <summary>Generates a cryptographically-random URL-safe base64 string from <paramref name="byteCount"/> bytes.</summary>
        /// <param name="byteCount">The number of random bytes to draw.</param>
        /// <returns>The base64url-encoded random value.</returns>
        public static string RandomUrlSafe(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        // URL-safe base64 without padding — the same transform McpAuth uses for its token.
        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
