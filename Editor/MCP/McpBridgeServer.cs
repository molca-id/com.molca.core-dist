using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// The editor-side MCP transport. Listens on a loopback HTTP port on a background thread and
    /// proxies tool listing/invocation to the Unity main thread via
    /// <see cref="McpMainThreadDispatcher"/>. The TypeScript MCP server (and later the in-editor
    /// assistant) talk to this bridge; this class knows nothing about MCP itself — it speaks a small
    /// authenticated HTTP/JSON protocol.
    /// </summary>
    /// <remarks>
    /// <para>Protocol (all requests require the <see cref="McpAuth.TokenHeader"/> header):</para>
    /// <list type="bullet">
    /// <item><c>GET /ping</c> → <c>{"ok":true}</c> — liveness.</item>
    /// <item><c>GET /tools</c> → <c>{"tools":[{name,description,inputSchema,mode,kind}]}</c>.</item>
    /// <item><c>POST /invoke</c> with <c>{"tool":"name","arguments":{...}}</c> →
    /// <c>{"ok":true,"result":...}</c> or <c>{"ok":false,"error":"..."}</c>.</item>
    /// </list>
    /// <para>
    /// Security: the listener binds <c>127.0.0.1</c> only, and every request must carry a matching
    /// session token, so stray local processes cannot drive the editor (Sprint 14.2).
    /// </para>
    /// </remarks>
    public sealed class McpBridgeServer : IDisposable
    {
        private readonly Func<McpToolRegistry> _registryFactory;
        private readonly Func<string, bool> _isActionAllowed;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>The loopback port the server is bound to, or 0 if not running.</summary>
        public int Port { get; private set; }

        /// <summary>True while the listener thread is accepting requests.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Creates a bridge server.
        /// </summary>
        /// <param name="registryFactory">
        /// Builds the current tool registry. Invoked on the main thread for every <c>/tools</c> and
        /// <c>/invoke</c> request so provider <c>GetTools()</c> calls can touch Unity APIs safely.
        /// </param>
        /// <param name="isActionAllowed">
        /// Returns whether an Action-kind tool (by name) is on the action allowlist. Defaults to denying
        /// all actions when null (Sprint 17 guardrail).
        /// </param>
        public McpBridgeServer(Func<McpToolRegistry> registryFactory, Func<string, bool> isActionAllowed = null)
        {
            _registryFactory = registryFactory ?? throw new ArgumentNullException(nameof(registryFactory));
            _isActionAllowed = isActionAllowed ?? (_ => false);
        }

        /// <summary>
        /// Starts the listener on <paramref name="port"/> (loopback only). No-op if already running.
        /// </summary>
        /// <param name="port">The loopback TCP port to bind.</param>
        /// <exception cref="HttpListenerException">Thrown if the port cannot be bound.</exception>
        public void Start(int port)
        {
            if (_running) return;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            Port = port;
            _running = true;
            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "MolcaMcpBridge" };
            _thread.Start();
        }

        /// <summary>Stops the listener and joins the background thread. Safe to call repeatedly.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _listener?.Stop(); } catch { /* already stopped */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;

            // The listener thread unblocks from GetContext when the listener closes; give it a moment.
            try { _thread?.Join(1000); } catch { /* ignore */ }
            _thread = null;
            Port = 0;
        }

        /// <inheritdoc/>
        public void Dispose() => Stop();

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch
                {
                    // Listener stopped/closed — exit cleanly.
                    break;
                }

                try { HandleRequest(context); }
                catch (Exception ex)
                {
                    TryWrite(context, 500, ErrorBody($"Internal bridge error: {ex.Message}"));
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;

            // 1) Loopback only — never serve a non-local caller even if the OS routed one here.
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
            {
                TryWrite(context, 403, ErrorBody("Non-loopback callers are rejected."));
                return;
            }

            // 2) Per-session token.
            var token = request.Headers[McpAuth.TokenHeader];
            if (!McpAuth.Verify(token))
            {
                TryWrite(context, 401, ErrorBody("Missing or invalid auth token."));
                return;
            }

            var path = request.Url.AbsolutePath.TrimEnd('/');
            switch (path)
            {
                case "/ping":
                case "":
                    TryWrite(context, 200, "{\"ok\":true}");
                    break;

                case "/tools":
                    TryWrite(context, 200, BuildToolsResponse());
                    break;

                case "/invoke":
                    if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        TryWrite(context, 405, ErrorBody("/invoke requires POST."));
                        return;
                    }
                    string body;
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                        body = reader.ReadToEnd();
                    TryWrite(context, 200, HandleInvoke(body));
                    break;

                default:
                    TryWrite(context, 404, ErrorBody($"Unknown endpoint '{path}'."));
                    break;
            }
        }

        private string BuildToolsResponse()
        {
            // Registry build touches provider SOs → main thread.
            var registry = McpMainThreadDispatcher.Invoke(() => _registryFactory());

            var arr = new JArray();
            foreach (var tool in registry.Tools)
            {
                arr.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = JToken.Parse(tool.InputSchemaJson),
                    ["mode"] = tool.Mode.ToString(),
                    ["kind"] = tool.Kind.ToString()
                });
            }

            return new JObject { ["ok"] = true, ["tools"] = arr }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private string HandleInvoke(string body)
        {
            string toolName;
            string argumentsJson;
            string confirmationToken;
            try
            {
                var envelope = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                toolName = envelope.Value<string>("tool");
                var args = envelope["arguments"];
                argumentsJson = args != null && args.Type != JTokenType.Null
                    ? args.ToString(Newtonsoft.Json.Formatting.None)
                    : "{}";
                confirmationToken = envelope.Value<string>("confirmationToken");
            }
            catch (Exception ex)
            {
                return ErrorBody($"Malformed request body: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(toolName))
                return ErrorBody("Request is missing the 'tool' field.");

            // Resolve + mode-gate on the main thread first (registry build touches SOs).
            var resolved = McpMainThreadDispatcher.Invoke<(McpToolDefinition tool, string error)>(() =>
            {
                var registry = _registryFactory();
                if (!registry.TryGet(toolName, out var t))
                    return (null, $"Unknown tool '{toolName}'.");

                var modeError = CheckMode(t.Mode);
                return modeError != null ? (null, modeError) : (t, null);
            });

            if (resolved.error != null)
                return ErrorBody(resolved.error);

            var tool = resolved.tool;

            // Action-tool guardrail (Sprint 17.1): allowlist + one-time confirmation.
            if (tool.Kind == McpToolKind.Action)
            {
                var gate = McpActionGuard.Evaluate(tool, _isActionAllowed(tool.Name), confirmationToken, argumentsJson);
                if (gate.Decision == McpActionDecision.Refused)
                {
                    McpActionAuditLog.Record(tool.Name, argumentsJson, "bridge", "refused", gate.Message);
                    return ErrorBody(gate.Message);
                }
                if (gate.Decision == McpActionDecision.NeedsConfirmation)
                {
                    // The client must confirm with the user and re-invoke with this token.
                    return new JObject
                    {
                        ["ok"] = false,
                        ["requiresConfirmation"] = true,
                        ["confirmationToken"] = gate.ConfirmationToken,
                        ["summary"] = gate.Message
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }
            }

            try
            {
                // Async tools are awaited on the main thread without blocking it; sync tools run inline.
                var result = tool.IsAsync
                    ? McpMainThreadDispatcher.InvokeAsync(() => tool.ExecuteAsync(argumentsJson))
                    : McpMainThreadDispatcher.Invoke(() => tool.Execute(argumentsJson));

                if (tool.Kind == McpToolKind.Action)
                    McpActionAuditLog.Record(tool.Name, argumentsJson, "bridge", "executed");

                return new JObject
                {
                    ["ok"] = true,
                    ["result"] = JToken.Parse(result ?? "null")
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                if (tool.Kind == McpToolKind.Action)
                    McpActionAuditLog.Record(tool.Name, argumentsJson, "bridge", "failed", ex.Message);
                return ErrorBody($"Tool '{toolName}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns a user-facing error string when the editor is in the wrong state for
        /// <paramref name="mode"/>, or null when the tool may run (Sprint 14.8 / 15.7 mode-gating UX).
        /// </summary>
        private static string CheckMode(McpToolMode mode)
            => McpModeGate.Check(mode, EditorApplication.isPlaying);

        private static string ErrorBody(string message)
            => new JObject { ["ok"] = false, ["error"] = message }.ToString(Newtonsoft.Json.Formatting.None);

        private static void TryWrite(HttpListenerContext context, int statusCode, string json)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch
            {
                // Client may have disconnected; nothing actionable.
            }
        }
    }
}
