using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Molca.Settings.Integration.OAuth
{
    /// <summary>
    /// A one-shot loopback HTTP listener that catches the OAuth authorization-code redirect.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/Integration/OAuth/</c>.
    /// Registration: instantiated by <see cref="OAuthAuthorizationCodeClient"/>; not an asset.
    /// <para>
    /// Binds <c>http://127.0.0.1:&lt;ephemeral&gt;/</c> only (the same loopback-only stance as
    /// <c>McpBridgeServer</c>, Sprint 14.2), so no off-machine caller can reach it. The provider's
    /// authorize URL is configured with this listener's <see cref="RedirectUri"/>; after the user
    /// approves in the browser, the provider redirects here with <c>code</c> + <c>state</c>, which the
    /// listener captures, validates against the expected <c>state</c>, and hands back. It serves a small
    /// "you can close this tab" page and shuts down. Honors a timeout and a
    /// <see cref="CancellationToken"/>; cancellation/timeout unblocks the accept loop by stopping the
    /// listener.
    /// </para>
    /// </remarks>
    public sealed class OAuthLoopbackListener : IDisposable
    {
        private readonly string _callbackPath;
        private HttpListener _listener;

        /// <summary>The loopback redirect URI the authorize request must use; valid after <see cref="Start"/>.</summary>
        public string RedirectUri { get; private set; }

        /// <summary>The bound loopback port, or 0 before <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        /// <summary>Creates a listener.</summary>
        /// <param name="callbackPath">The redirect path the provider sends the code to (default <c>/callback</c>).</param>
        public OAuthLoopbackListener(string callbackPath = "/callback")
        {
            _callbackPath = string.IsNullOrEmpty(callbackPath) ? "/callback" : callbackPath;
        }

        /// <summary>
        /// Binds an ephemeral loopback port and starts the listener. Sets <see cref="RedirectUri"/>.
        /// </summary>
        /// <exception cref="HttpListenerException">Thrown if the chosen port cannot be bound.</exception>
        public void Start()
        {
            Port = FindFreeLoopbackPort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            RedirectUri = $"http://127.0.0.1:{Port}{_callbackPath}";
        }

        /// <summary>
        /// Waits for the authorization redirect and returns the captured code.
        /// </summary>
        /// <param name="expectedState">The <c>state</c> value to match; a mismatch is rejected (CSRF guard).</param>
        /// <param name="timeout">Maximum time to wait for the browser redirect.</param>
        /// <param name="cancellationToken">Cancels the wait; cancellation surfaces as <see cref="OperationCanceledException"/>.</param>
        /// <returns>The captured code, or an error description.</returns>
        public async Awaitable<LoopbackResult> WaitForCodeAsync(
            string expectedState, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_listener == null)
                throw new InvalidOperationException("Start() must be called before WaitForCodeAsync().");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            // Stopping the listener unblocks the blocking GetContext call below.
            using var registration = linked.Token.Register(() =>
            {
                try { _listener?.Stop(); } catch { /* already stopped */ }
            });

            await Awaitable.BackgroundThreadAsync();
            try
            {
                while (true)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = _listener.GetContext();
                    }
                    catch
                    {
                        // Listener was stopped by caller-cancellation or the timeout, or faulted.
                        await Awaitable.MainThreadAsync();
                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(cancellationToken);
                        return LoopbackResult.Fail("Timed out waiting for the authorization redirect.");
                    }

                    var request = context.Request;

                    // Ignore stray requests (e.g. /favicon.ico) so the real redirect still lands.
                    if (!string.Equals(request.Url.AbsolutePath.TrimEnd('/'),
                            _callbackPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(context, 404, "Not found.");
                        continue;
                    }

                    var error = request.QueryString["error"];
                    var code = request.QueryString["code"];
                    var state = request.QueryString["state"];

                    LoopbackResult result;
                    string page;
                    if (!string.IsNullOrEmpty(error))
                    {
                        result = LoopbackResult.Fail($"Authorization was denied or failed: {error}.");
                        page = "Authorization failed. You can close this tab and return to Unity.";
                    }
                    else if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                    {
                        // CSRF guard: the redirected state must match the one we generated.
                        result = LoopbackResult.Fail("State mismatch — the authorization response was rejected.");
                        page = "Authorization could not be verified. You can close this tab.";
                    }
                    else if (string.IsNullOrEmpty(code))
                    {
                        result = LoopbackResult.Fail("Authorization redirect carried no code.");
                        page = "Authorization incomplete. You can close this tab.";
                    }
                    else
                    {
                        result = LoopbackResult.Ok(code);
                        page = "Connected. You can close this tab and return to Unity.";
                    }

                    Respond(context, string.IsNullOrEmpty(error) && result.Success ? 200 : 400, page);
                    await Awaitable.MainThreadAsync();
                    return result;
                }
            }
            finally
            {
                await Awaitable.MainThreadAsync();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;
            Port = 0;
        }

        private static void Respond(HttpListenerContext context, int status, string message)
        {
            try
            {
                var html = $"<!doctype html><html><body style=\"font-family:sans-serif;padding:2rem\"><p>{message}</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/html";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch
            {
                // Browser may have disconnected; nothing actionable.
            }
        }

        // Bind a TcpListener on port 0 to let the OS pick a free loopback port, then release it for HttpListener.
        private static int FindFreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }

    /// <summary>The result of awaiting the loopback redirect: a captured code or an error.</summary>
    public readonly struct LoopbackResult
    {
        private LoopbackResult(bool success, string code, string error)
        {
            Success = success;
            Code = code;
            Error = error;
        }

        /// <summary>True when an authorization code was captured and the state validated.</summary>
        public bool Success { get; }

        /// <summary>The captured authorization code when <see cref="Success"/> is true.</summary>
        public string Code { get; }

        /// <summary>The failure description when <see cref="Success"/> is false.</summary>
        public string Error { get; }

        /// <summary>Creates a success result.</summary>
        public static LoopbackResult Ok(string code) => new LoopbackResult(true, code, null);

        /// <summary>Creates a failure result.</summary>
        public static LoopbackResult Fail(string error) => new LoopbackResult(false, null, error);
    }
}
