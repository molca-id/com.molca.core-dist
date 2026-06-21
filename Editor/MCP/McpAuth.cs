using System;
using System.Security.Cryptography;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Owns the per-project MCP bridge auth token. The token gates every bridge request: the MCP
    /// server (or any front-end) must present it in the <c>X-Molca-Token</c> header, and the bridge
    /// rejects mismatches alongside the loopback-only check. This keeps the editor undriveable by
    /// stray local processes that happen to find the port.
    /// </summary>
    /// <remarks>
    /// The token is a secret, so — per the framework credential rule (Sprints 4.5 / 16.2) — it is
    /// never written to a ScriptableObject. It lives in project-scoped <c>EditorPrefs</c> via
    /// <see cref="MolcaEditorPrefs"/>, so it is isolated per project and not committed with assets.
    /// </remarks>
    public static class McpAuth
    {
        private const string TokenKey = "Mcp.AuthToken";

        /// <summary>The header the bridge expects the auth token in.</summary>
        public const string TokenHeader = "X-Molca-Token";

        // In-memory snapshot of the token so the bridge can verify on its background listener thread.
        // EditorPrefs is main-thread-only, so persistence is read/written only from the property and
        // Regenerate (both called on the main thread), and the snapshot is what Verify compares.
        private static volatile string _cachedToken;

        /// <summary>
        /// The current token, generating and persisting one on first access so the bridge is never
        /// unauthenticated. Must be accessed from the main thread (reads <c>EditorPrefs</c>); reading it
        /// also refreshes the snapshot used by <see cref="Verify"/>.
        /// </summary>
        public static string Token
        {
            get
            {
                var token = MolcaEditorPrefs.GetString(TokenKey, string.Empty);
                if (string.IsNullOrEmpty(token))
                    token = Regenerate();
                _cachedToken = token;
                return token;
            }
        }

        /// <summary>
        /// Generates a fresh cryptographically-random token, persists it, and returns it. Any running
        /// front-end must be reconfigured with the new value after a regenerate. Main thread only.
        /// </summary>
        /// <returns>The new token (URL-safe base64, 256 bits of entropy).</returns>
        public static string Regenerate()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            // URL-safe base64 so the token can be passed verbatim in a header or env var.
            var token = Convert.ToBase64String(bytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            MolcaEditorPrefs.SetString(TokenKey, token);
            _cachedToken = token;
            return token;
        }

        /// <summary>
        /// Constant-time comparison of a presented token against the cached token snapshot. Safe to call
        /// from any thread — it never touches <c>EditorPrefs</c>. Call <see cref="Token"/> once on the
        /// main thread (the server does this on start) to populate the snapshot first.
        /// </summary>
        /// <param name="presented">The token supplied by the caller (may be null).</param>
        /// <returns>True if the presented token matches the cached token.</returns>
        public static bool Verify(string presented)
        {
            if (string.IsNullOrEmpty(presented))
                return false;

            var expected = _cachedToken;
            if (string.IsNullOrEmpty(expected) || presented.Length != expected.Length)
                return false;

            // Length-equal constant-time compare to avoid leaking match progress via timing.
            var diff = 0;
            for (var i = 0; i < expected.Length; i++)
                diff |= presented[i] ^ expected[i];
            return diff == 0;
        }
    }
}
