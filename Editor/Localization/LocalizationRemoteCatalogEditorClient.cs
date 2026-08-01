using System;
using System.Threading;
using Molca.Editor.Addons;
using Molca.Editor.Licensing;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Editor
{
    /// <summary>Authenticated, project-bound Editor publication client for remote catalogs.</summary>
    internal sealed class LocalizationRemoteCatalogEditorClient
    {
        private const int RequestTimeoutSeconds = 45;

        internal Awaitable<LocalizationRemoteCatalogEditorResult> PreviewAsync(
            string bundleJson,
            CancellationToken cancellationToken = default) =>
            PostAsync("preview", bundleJson, cancellationToken);

        internal Awaitable<LocalizationRemoteCatalogEditorResult> PublishAsync(
            string bundleJson,
            CancellationToken cancellationToken = default) =>
            PostAsync("publish", bundleJson, cancellationToken);

        private static async Awaitable<LocalizationRemoteCatalogEditorResult> PostAsync(
            string operation,
            string bundleJson,
            CancellationToken cancellationToken)
        {
            var token = DevEntitlementStore.LoadEffective();
            var licenseStatus = DevEntitlementVerifier.Evaluate(
                token,
                SystemInfo.deviceUniqueIdentifier,
                out _);
            if (licenseStatus != DevLicenseStatus.Valid)
                return LocalizationRemoteCatalogEditorResult.Failure(
                    "Sign in with a valid developer entitlement before publishing.");
            var project = MolcaProjectSettings.Instance;
            if (project == null || string.IsNullOrWhiteSpace(project.ProjectBinding))
                return LocalizationRemoteCatalogEditorResult.Failure(
                    "Connect this repository to a Molca project before publishing.");

            var url = $"{DevLicenseConfig.ServerBaseUrl.TrimEnd('/')}/localization/editor/{operation}";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !AddonDistributionConfig.IsTrustedDownloadHost(uri.Host))
                return LocalizationRemoteCatalogEditorResult.Failure(
                    "Localization publication endpoint is outside the pinned HTTPS host policy.");
            JObject bundle;
            try
            {
                bundle = JObject.Parse(bundleJson);
            }
            catch (Exception exception)
            {
                return LocalizationRemoteCatalogEditorResult.Failure(
                    $"Publication bundle is invalid JSON: {exception.Message}");
            }

            var body = new JObject
            {
                ["projectBinding"] = project.ProjectBinding,
                ["bundle"] = bundle,
            }.ToString(Newtonsoft.Json.Formatting.None);
            using var request = new UnityWebRequest(
                uri.AbsoluteUri,
                UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("X-Molca-Machine-Id", SystemInfo.deviceUniqueIdentifier);
            var pending = request.SendWebRequest();
            while (!pending.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Awaitable.NextFrameAsync(cancellationToken);
            }
            var responseText = request.downloadHandler?.text ?? string.Empty;
            if (request.result != UnityWebRequest.Result.Success)
            {
                var reason = string.Empty;
                try { reason = JObject.Parse(responseText).Value<string>("reason"); }
                catch { /* The transport detail below remains actionable. */ }
                return LocalizationRemoteCatalogEditorResult.Failure(
                    $"Publication {operation} failed: HTTP {request.responseCode} " +
                    $"{(string.IsNullOrEmpty(reason) ? request.error : reason)}");
            }
            try
            {
                var response = JObject.Parse(responseText);
                return LocalizationRemoteCatalogEditorResult.Success(
                    response.Value<string>("version"),
                    response.Value<string>("channel"),
                    response.Value<string>("sha256"),
                    response.Value<int?>("entryCount") ?? 0,
                    response.Value<int?>("localeCount") ?? 0,
                    response.Value<int?>("sizeBytes") ?? 0);
            }
            catch (Exception exception)
            {
                return LocalizationRemoteCatalogEditorResult.Failure(
                    $"Publication response is invalid: {exception.Message}");
            }
        }
    }

    internal sealed class LocalizationRemoteCatalogEditorResult
    {
        internal bool Succeeded { get; private set; }
        internal string Error { get; private set; }
        internal string Version { get; private set; }
        internal string Channel { get; private set; }
        internal string Sha256 { get; private set; }
        internal int EntryCount { get; private set; }
        internal int LocaleCount { get; private set; }
        internal int SizeBytes { get; private set; }

        internal static LocalizationRemoteCatalogEditorResult Success(
            string version,
            string channel,
            string sha256,
            int entryCount,
            int localeCount,
            int sizeBytes) =>
            new()
            {
                Succeeded = true,
                Version = version ?? string.Empty,
                Channel = channel ?? string.Empty,
                Sha256 = sha256 ?? string.Empty,
                EntryCount = entryCount,
                LocaleCount = localeCount,
                SizeBytes = sizeBytes,
            };

        internal static LocalizationRemoteCatalogEditorResult Failure(string error) =>
            new() { Error = error ?? "Localization publication failed." };
    }
}
