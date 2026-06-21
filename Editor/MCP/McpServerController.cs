using System;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Owns the lifetime of the single <see cref="McpBridgeServer"/> instance: starts it on domain
    /// load when the bridge is enabled in <see cref="McpSettings"/>, stops it on editor quit, and
    /// exposes start/stop/restart for the settings UI. The registry is rebuilt per request from the
    /// currently-configured providers, so adding a provider asset takes effect without a restart.
    /// </summary>
    [InitializeOnLoad]
    public static class McpServerController
    {
        private static McpBridgeServer _server;

        static McpServerController()
        {
            // Defer until the AssetDatabase is ready post-reload (same pattern as BootstrapAssetValidator).
            EditorApplication.delayCall += StartIfEnabled;
            EditorApplication.quitting += Stop;

            // Free the port before the domain reloads on recompile. Without this the previous listener
            // lingers and the next domain fails to bind (port-in-use spam on every recompile).
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }

        /// <summary>True if the bridge listener is currently running.</summary>
        public static bool IsRunning => _server != null && _server.IsRunning;

        /// <summary>The port the bridge is bound to, or 0 if not running.</summary>
        public static int Port => _server?.Port ?? 0;

        /// <summary>Resolves the configured MCP settings, or null if none is assigned.</summary>
        public static McpSettings Settings => MolcaEditorSettings.Instance.McpSettings;

        /// <summary>Starts the bridge if enabled in settings. No-op if already running or disabled.</summary>
        public static void StartIfEnabled()
        {
            var settings = Settings;
            if (settings == null || !settings.Enabled)
                return;

            Start();
        }

        /// <summary>
        /// Starts the bridge on the configured port. Logs and aborts if no settings asset is assigned
        /// or the port cannot be bound (e.g. a stale listener from another editor instance).
        /// </summary>
        public static void Start()
        {
            var settings = Settings;
            if (settings == null)
            {
                Debug.LogWarning("[Molca MCP] No MCP Settings asset assigned; bridge not started.");
                return;
            }

            Stop();

            // Warm the auth-token snapshot on the main thread; the listener thread verifies against it
            // (EditorPrefs cannot be read off the main thread).
            _ = McpAuth.Token;

            _server = new McpBridgeServer(() => settings.BuildRegistry(), settings.IsActionAllowed);
            try
            {
                _server.Start(settings.Port);
                Debug.Log($"[Molca MCP] Bridge listening on http://127.0.0.1:{settings.Port}/");
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                Debug.LogWarning(
                    $"[Molca MCP] Bridge port {settings.Port} is already in use. " +
                    "If another Unity editor or MCP bridge is running, close it or choose a different MCP port in Project Settings > Molca.");
                _server = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Molca MCP] Failed to start bridge on port {settings.Port}: {ex.Message}");
                _server = null;
            }
        }

        /// <summary>Stops the bridge if running. Safe to call when not running.</summary>
        public static void Stop()
        {
            _server?.Stop();
            _server = null;
        }

        /// <summary>Stops and restarts the bridge — call after changing the port or enable flag.</summary>
        public static void Restart()
        {
            Stop();
            StartIfEnabled();
        }

        // Address-in-use surfaces as an HttpListenerException on .NET but as a SocketException under
        // Unity's Mono HttpListener — and either may be wrapped — so check the whole exception chain
        // by native error code (32/183/WSAEADDRINUSE 10048), SocketError, or message.
        private static bool IsAddressInUse(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                switch (e)
                {
                    case HttpListenerException hle when hle.ErrorCode == 32 || hle.ErrorCode == 183 || hle.ErrorCode == 10048:
                        return true;
                    case SocketException se when se.SocketErrorCode == SocketError.AddressAlreadyInUse || se.ErrorCode == 10048:
                        return true;
                }
                if (e.Message.IndexOf("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0
                    || e.Message.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
