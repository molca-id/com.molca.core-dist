using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Editor.Networking.Authoring;
using Object = UnityEngine.Object;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>
    /// Finds the project's legacy networking configuration — the global base URL, request assets, and
    /// data providers — and describes it in catalog terms.
    /// </summary>
    /// <remarks>
    /// Strictly read-only, and the report it produces is the only input the migration plan needs. That
    /// separation is what makes the dry run trustworthy: the preview is computed from the same report the
    /// apply step consumes, so it cannot describe something different from what would happen.
    /// <para>
    /// Provider URLs are read through <see cref="SerializedObject"/> rather than public properties,
    /// because they deliberately have none — the fields are authoring state, not runtime API. Reading the
    /// serialized form also means migration sees exactly what is on disk, including values written by an
    /// older version of the provider.
    /// </para>
    /// <para>
    /// Assets are located by type through <see cref="AssetDatabase"/>, never by hardcoded path.
    /// </para>
    /// </remarks>
    public static class LegacyNetworkScanner
    {
        // Serialized field names on the legacy providers. Constants so a rename surfaces here as a
        // failing test rather than as a scan that silently reports nothing.
        private const string FieldUrl = "_url";
        private const string FieldServerUrl = "_serverUrl";
        private const string FieldRequest = "_request";
        private const string FieldUseSecureConnection = "_useSecureConnection";
        private const string FieldRequireAuthentication = "_requireAuthentication";
        private const string FieldSendAuthToken = "_sendAuthToken";
        private const string FieldSocketPath = "_socketPath";

        /// <summary>
        /// Scans the project.
        /// </summary>
        /// <returns>The report. Never <c>null</c>; may describe an empty project.</returns>
        public static LegacyNetworkScanReport Scan()
        {
            var module = FindHttpModule(out string moduleGuid);
            string baseUrl = module != null ? module.BaseUrl : string.Empty;

            var items = new List<LegacyNetworkItem>();

            if (module != null)
            {
                items.Add(new LegacyNetworkItem(
                    LegacyNetworkItemKind.GlobalBaseUrl,
                    module,
                    moduleGuid,
                    module.name,
                    baseUrl,
                    baseUrl,
                    NetworkProtocols.Http,
                    notes: DescribeModule(module)));
            }

            CollectRequestAssets(items);
            CollectProviders(items);

            return new LegacyNetworkScanReport(
                baseUrl, module != null, moduleGuid, NetworkCatalogLocator.FindCatalog(), items);
        }

        private static IReadOnlyList<string> DescribeModule(HttpModule module)
        {
            var notes = new List<string>();

            if (string.IsNullOrWhiteSpace(module.BaseUrl))
            {
                notes.Add(
                    "No base URL is set, so relative request URLs resolve to nothing. Migration will " +
                    "still create the environment and policy scaffolding.");
            }

            foreach (var header in module.GetDefaultHeaders())
            {
                // A credential-shaped default header is the mechanism behind the leak the routed model
                // fixes: it is applied to every request, including full URLs to third-party hosts.
                if (IsCredentialHeaderName(header.Key))
                {
                    notes.Add(
                        $"Default header '{header.Key}' is applied to every request, including full URLs " +
                        "to unrelated hosts. Migration scopes it to a credential profile instead.");
                }
            }

            if (!module.ValidateSSL)
                notes.Add("SSL validation is disabled globally. The migrated policy profile keeps TLS validation on.");

            return notes;
        }

        private static void CollectRequestAssets(List<LegacyNetworkItem> items)
        {
            foreach (var asset in LoadAll<HttpRequestAsset>())
            {
                var request = asset.request;
                if (request == null) continue;

                var notes = new List<string>();
                bool declaresCredential = false;

                foreach (var header in request.headers)
                {
                    if (header == null || !IsCredentialHeaderName(header.key)) continue;
                    declaresCredential = true;
                    notes.Add($"Declares credential header '{header.key}'.");
                }

                if (request.useFullUrl && declaresCredential)
                {
                    notes.Add(
                        "A full URL that also opts into authentication. Confirm the host should receive " +
                        "the credential before authoring it as a service.");
                }

                if (!request.validateSSL)
                    notes.Add("SSL validation is disabled on this request; the migrated route keeps it on.");

                items.Add(new LegacyNetworkItem(
                    LegacyNetworkItemKind.RequestAsset,
                    asset,
                    GuidOf(asset),
                    asset.name,
                    request.url,
                    request.useFullUrl ? request.url : null,
                    NetworkProtocols.Http,
                    request.method,
                    declaresCredential,
                    notes));
            }
        }

        private static void CollectProviders(List<LegacyNetworkItem> items)
        {
            foreach (var provider in LoadAll<Molca.Networking.Data.DataProvider>())
            {
                var serialized = new SerializedObject(provider);
                string typeName = provider.GetType().Name;

                switch (typeName)
                {
                    case "HttpDataProvider":
                        items.Add(DescribeHttpProvider(provider, serialized));
                        break;

                    case "SSEProvider":
                        items.Add(DescribeStreamProvider(
                            provider, serialized, LegacyNetworkItemKind.SseProvider,
                            NetworkProtocols.ServerSentEvents, FieldUrl, null, FieldSendAuthToken));
                        break;

                    case "WebSocketDataProvider":
                        items.Add(DescribeStreamProvider(
                            provider, serialized, LegacyNetworkItemKind.WebSocketProvider,
                            NetworkProtocols.WebSocket, FieldUrl, "wss://", FieldRequireAuthentication));
                        break;

                    case "SocketIODataProvider":
                        items.Add(DescribeStreamProvider(
                            provider, serialized, LegacyNetworkItemKind.SocketIoProvider,
                            NetworkProtocols.SocketIO, FieldServerUrl, "https://", FieldRequireAuthentication));
                        break;
                }
            }
        }

        private static LegacyNetworkItem DescribeHttpProvider(Object provider, SerializedObject serialized)
        {
            var requestProperty = serialized.FindProperty(FieldRequest);
            var requestAsset = requestProperty?.objectReferenceValue as HttpRequestAsset;

            var notes = new List<string>();
            string authoredUrl = string.Empty;
            string effectiveUrl = null;
            var method = HttpMethod.GET;

            if (requestAsset?.request == null)
            {
                notes.Add(
                    "No request asset is assigned, so there is nothing to route. Assign one, or point " +
                    "this provider at a migrated endpoint.");
            }
            else
            {
                authoredUrl = requestAsset.request.url;
                effectiveUrl = requestAsset.request.useFullUrl ? requestAsset.request.url : null;
                method = requestAsset.request.method;
                notes.Add($"Sends through request asset '{requestAsset.name}', which migrates to its own endpoint.");
            }

            if (ReadBool(serialized, FieldRequireAuthentication))
                notes.Add("Requires authentication, so the migrated service needs a credential profile.");

            return new LegacyNetworkItem(
                LegacyNetworkItemKind.HttpProvider,
                provider,
                GuidOf(provider),
                provider.name,
                authoredUrl,
                effectiveUrl,
                NetworkProtocols.Http,
                method,
                ReadBool(serialized, FieldRequireAuthentication),
                notes);
        }

        /// <summary>
        /// Describes one streaming provider, reproducing the scheme it prepends at connect time.
        /// </summary>
        /// <param name="provider">The provider asset.</param>
        /// <param name="serialized">Its serialized view.</param>
        /// <param name="kind">Which item kind to record.</param>
        /// <param name="protocol">The protocol it speaks.</param>
        /// <param name="urlField">Serialized field holding the URL.</param>
        /// <param name="secureScheme">
        /// The scheme prepended when the authored URL has none and secure connections are on, or
        /// <c>null</c> when the provider requires an absolute URL already.
        /// </param>
        /// <param name="authField">Serialized bool field indicating the provider opts into authentication.</param>
        private static LegacyNetworkItem DescribeStreamProvider(
            Object provider,
            SerializedObject serialized,
            LegacyNetworkItemKind kind,
            NetworkProtocols protocol,
            string urlField,
            string secureScheme,
            string authField)
        {
            string authored = serialized.FindProperty(urlField)?.stringValue ?? string.Empty;
            var notes = new List<string>();

            string effective = ResolveProviderUrl(authored, serialized, secureScheme, notes);

            if (ReadBool(serialized, authField))
                notes.Add("Opts into authentication, so the migrated service needs a credential profile.");

            if (kind == LegacyNetworkItemKind.SocketIoProvider)
            {
                string path = serialized.FindProperty(FieldSocketPath)?.stringValue;
                if (!string.IsNullOrEmpty(path))
                    notes.Add($"Socket.IO handshake path '{path}' migrates onto the service binding.");
            }

            return new LegacyNetworkItem(
                kind,
                provider,
                GuidOf(provider),
                provider.name,
                authored,
                effective,
                protocol,
                declaresCredential: ReadBool(serialized, authField),
                notes: notes);
        }

        /// <summary>
        /// Reproduces the absolute URL a streaming provider connects to.
        /// </summary>
        /// <remarks>
        /// The providers store a schemeless host and prepend <c>wss://</c>/<c>ws://</c> or
        /// <c>https://</c>/<c>http://</c> from their <c>_useSecureConnection</c> flag. Migration has to
        /// reproduce that, or the origin it writes onto the binding would not be the origin the provider
        /// was actually reaching.
        /// </remarks>
        private static string ResolveProviderUrl(
            string authored,
            SerializedObject serialized,
            string secureScheme,
            List<string> notes)
        {
            if (string.IsNullOrWhiteSpace(authored))
            {
                notes.Add("No URL is authored, so this provider cannot be bound to a service yet.");
                return null;
            }

            string trimmed = authored.Trim();

            if (trimmed.Contains("://"))
                return trimmed;

            if (secureScheme == null)
            {
                notes.Add($"'{trimmed}' has no scheme and this provider does not add one; it will not connect.");
                return null;
            }

            bool secure = ReadBool(serialized, FieldUseSecureConnection, defaultValue: true);
            string scheme = secure
                ? secureScheme
                : secureScheme.Replace("wss://", "ws://").Replace("https://", "http://");

            if (!secure)
            {
                notes.Add(
                    "Secure connections are off, so the effective origin is unencrypted. A production " +
                    "environment refuses that, and migration records the origin as authored.");
            }

            return scheme + trimmed;
        }

        private static bool ReadBool(SerializedObject serialized, string field, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(field))
                return defaultValue;

            var property = serialized.FindProperty(field);
            return property != null ? property.boolValue : defaultValue;
        }

        /// <summary>
        /// Whether a header name is one of the well-known credential headers.
        /// </summary>
        /// <remarks>
        /// Deliberately narrower than <c>LegacyRouteMapper.IsCredentialHeader</c>: that reads a catalog's
        /// credential profiles, and the scan runs before one exists. The scan only needs to flag the
        /// obvious cases for the author to review.
        /// </remarks>
        internal static bool IsCredentialHeaderName(string headerName) =>
            !string.IsNullOrEmpty(headerName) &&
            (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
             headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
             headerName.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
             headerName.StartsWith("X-Api-Key", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The project's <c>HttpModule</c>, preferring the one registered on <c>GlobalSettings</c>.
        /// </summary>
        private static HttpModule FindHttpModule(out string guid)
        {
            guid = string.Empty;

            var settings = MolcaProjectSettings.Instance;
            var globalSettings = settings != null ? settings.GlobalSettings : null;

            if (globalSettings?.modules != null)
            {
                foreach (var module in globalSettings.modules)
                {
                    if (module is HttpModule http)
                    {
                        guid = GuidOf(http);
                        return http;
                    }
                }
            }

            var found = LoadAll<HttpModule>();
            if (found.Count == 0)
                return null;

            guid = GuidOf(found[0]);
            return found[0];
        }

        /// <summary>
        /// Every asset of a type, ordered by path so scans are reproducible.
        /// </summary>
        private static List<T> LoadAll<T>() where T : Object
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets($"t:{typeof(T).Name}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }

            paths.Sort(StringComparer.Ordinal);

            var result = new List<T>();
            foreach (string path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null)
                    result.Add(asset);
            }
            return result;
        }

        private static string GuidOf(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }
    }
}
