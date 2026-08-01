using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Molca.Licensing;
using UnityEngine;
using UnityEngine.Networking;

namespace Molca.Localization
{
    [Serializable]
    internal sealed class LocalizationManifestEnvelope
    {
        public int schemaVersion;
        public string manifest;
    }

    /// <summary>Bounded, authenticated remote manifest and bundle fetcher.</summary>
    internal sealed class LocalizationRemoteCatalogClient
    {
        private readonly LocalizationRemoteCatalogSettings _settings;
        private readonly LocalizationOverlayManager _manager;
        private string _etag;

        internal LocalizationRemoteCatalogClient(
            LocalizationRemoteCatalogSettings settings,
            LocalizationOverlayManager manager)
        {
            _settings = settings;
            _manager = manager;
        }

        internal async Awaitable<LocalizationOverlayActivationResult> RefreshAsync(
            CancellationToken cancellationToken)
        {
            var stamp = LicenseStamp.Current;
            if (stamp == null || string.IsNullOrWhiteSpace(stamp.buildToken))
                return Failure("localization-overlay-build-token-missing",
                    "This build has no project-scoped runtime token.");
            var manifestUrl = ResolveManifestUrl(stamp);
            if (!IsTransportAllowed(manifestUrl, null))
                return Failure("localization-overlay-manifest-url-invalid",
                    "Manifest URL must use HTTPS or loopback HTTP.");

            using var manifestRequest = UnityWebRequest.Get(manifestUrl);
            manifestRequest.SetRequestHeader("Authorization", $"Bearer {stamp.buildToken}");
            if (!string.IsNullOrEmpty(_etag))
                manifestRequest.SetRequestHeader("If-None-Match", _etag);
            await SendAsync(manifestRequest, cancellationToken);
            if (manifestRequest.responseCode == 304)
                return new LocalizationOverlayActivationResult(
                    true, string.Empty, "Remote catalog is unchanged.", _manager.Active?.Version);
            if (manifestRequest.result != UnityWebRequest.Result.Success)
                return Failure("localization-overlay-manifest-download-failed",
                    $"{manifestRequest.responseCode}: {manifestRequest.error}");
            var envelope = JsonUtility.FromJson<LocalizationManifestEnvelope>(
                manifestRequest.downloadHandler.text);
            if (envelope == null || envelope.schemaVersion != 1 ||
                string.IsNullOrWhiteSpace(envelope.manifest))
                return Failure("localization-overlay-manifest-response-invalid",
                    "Manifest response is invalid.");
            var projectId = string.IsNullOrWhiteSpace(_settings.ProjectId)
                ? BuildInfo.ProjectId
                : _settings.ProjectId;
            var manifestValidation = _manager.ValidateManifestForDownload(
                envelope.manifest,
                projectId,
                Application.version,
                out var bundleUrl);
            if (!manifestValidation.Success)
                return manifestValidation;
            if (!IsTransportAllowed(bundleUrl, new Uri(manifestUrl)))
                return Failure("localization-overlay-bundle-url-invalid",
                    "Bundle URL host is not allowed.");

            var boundedDownload = new BoundedDownloadHandler(
                LocalizationOverlayManager.MaximumBytes);
            using var bundleRequest = new UnityWebRequest(
                bundleUrl,
                UnityWebRequest.kHttpVerbGET,
                boundedDownload,
                null);
            var manifestOrigin = new Uri(manifestUrl);
            var bundleOrigin = new Uri(bundleUrl);
            if (string.Equals(
                    manifestOrigin.Host,
                    bundleOrigin.Host,
                    StringComparison.OrdinalIgnoreCase))
                bundleRequest.SetRequestHeader("Authorization", $"Bearer {stamp.buildToken}");
            await SendAsync(bundleRequest, cancellationToken);
            if (boundedDownload.ExceededLimit)
                return Failure("localization-overlay-size-invalid",
                    "Downloaded bundle exceeds the client limit.");
            if (bundleRequest.result != UnityWebRequest.Result.Success)
                return Failure("localization-overlay-bundle-download-failed",
                    $"{bundleRequest.responseCode}: {bundleRequest.error}");
            var contentLength = bundleRequest.downloadedBytes;
            if (contentLength > LocalizationOverlayManager.MaximumBytes)
                return Failure("localization-overlay-size-invalid",
                    "Downloaded bundle exceeds the client limit.");

            var result = _manager.TryActivate(
                envelope.manifest,
                bundleRequest.downloadHandler.text,
                projectId,
                Application.version);
            if (result.Success)
            {
                _etag = manifestRequest.GetResponseHeader("ETag");
                _manager.TrySaveLastKnownGood(CacheDirectory, out _);
            }
            return result;
        }

        internal string CacheDirectory
        {
            get
            {
                var projectId = string.IsNullOrWhiteSpace(_settings.ProjectId)
                    ? BuildInfo.ProjectId
                    : _settings.ProjectId;
                return Path.Combine(
                    Application.persistentDataPath,
                    "Molca",
                    "Localization",
                    SafePathSegment(projectId),
                    SafePathSegment(_settings.Channel));
            }
        }

        private string ResolveManifestUrl(LicenseStampData stamp)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ManifestUrl))
                return AppendChannel(_settings.ManifestUrl);
            return AppendChannel(
                $"{(stamp.serverBaseUrl ?? string.Empty).TrimEnd('/')}/localization/catalog/manifest");
        }

        private string AppendChannel(string url)
        {
            var separator = url.Contains("?") ? "&" : "?";
            return $"{url}{separator}channel={Uri.EscapeDataString(_settings.Channel)}";
        }

        private static string SafePathSegment(string value)
        {
            var safe = new string((value ?? string.Empty)
                .Where(character =>
                    char.IsLetterOrDigit(character) || character is '-' or '_')
                .Take(128)
                .ToArray());
            return string.IsNullOrEmpty(safe) ? "default" : safe;
        }

        private bool IsTransportAllowed(string value, Uri manifestOrigin)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;
            var secure = uri.Scheme == Uri.UriSchemeHttps ||
                         uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
            if (!secure)
                return false;
            if (manifestOrigin == null ||
                string.Equals(uri.Host, manifestOrigin.Host, StringComparison.OrdinalIgnoreCase))
                return true;
            return _settings.AllowedDownloadHosts.Any(host =>
                string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase));
        }

        private static async Awaitable SendAsync(
            UnityWebRequest request,
            CancellationToken cancellationToken)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    request.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Awaitable.NextFrameAsync(cancellationToken);
            }
        }

        private static LocalizationOverlayActivationResult Failure(string code, string message) =>
            new(false, code, message, string.Empty);

        private sealed class BoundedDownloadHandler : DownloadHandlerScript
        {
            private readonly int _maximumBytes;
            private readonly MemoryStream _bytes = new();

            internal BoundedDownloadHandler(int maximumBytes) : base(new byte[64 * 1024])
            {
                _maximumBytes = maximumBytes;
            }

            internal bool ExceededLimit { get; private set; }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0)
                    return true;
                if (_bytes.Length + dataLength > _maximumBytes)
                {
                    ExceededLimit = true;
                    return false;
                }
                _bytes.Write(data, 0, dataLength);
                return true;
            }

            protected override byte[] GetData() => _bytes.ToArray();

            protected override string GetText() =>
                Encoding.UTF8.GetString(_bytes.GetBuffer(), 0, checked((int)_bytes.Length));

            public override void Dispose()
            {
                _bytes.Dispose();
                base.Dispose();
            }
        }
    }
}
