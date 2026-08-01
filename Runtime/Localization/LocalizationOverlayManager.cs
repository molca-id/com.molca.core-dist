using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Molca.Localization
{
    /// <summary>Current lifecycle state of the optional remote localization overlay.</summary>
    public enum LocalizationOverlayStatus
    {
        Disabled,
        None,
        Active,
        Rejected,
        RolledBack,
    }

    /// <summary>Typed outcome from manifest validation, activation, cache load, or rollback.</summary>
    public readonly struct LocalizationOverlayActivationResult
    {
        public LocalizationOverlayActivationResult(
            bool success,
            string diagnosticCode,
            string message,
            string version)
        {
            Success = success;
            DiagnosticCode = diagnosticCode ?? string.Empty;
            Message = message ?? string.Empty;
            Version = version ?? string.Empty;
        }

        /// <summary>Whether the requested operation completed successfully.</summary>
        public bool Success { get; }
        /// <summary>Stable machine-readable failure code, or empty on success.</summary>
        public string DiagnosticCode { get; }
        /// <summary>Human-readable outcome detail.</summary>
        public string Message { get; }
        /// <summary>Overlay version involved in the outcome, when available.</summary>
        public string Version { get; }
    }

    [Serializable]
    internal sealed class LocalizationOverlayManifest
    {
        public int schemaVersion;
        public string kind;
        public string projectId;
        public string channel;
        public string version;
        public string baseCatalogVersion;
        public string createdAt;
        public string minAppVersion;
        public string maxAppVersion;
        public string sha256;
        public long sizeBytes;
        public int entryCount;
        public int localeCount;
        public string bundleUrl;
    }

    [Serializable]
    internal sealed class LocalizationOverlayBundle
    {
        public int schemaVersion;
        public string kind;
        public string projectId;
        public string channel;
        public string version;
        public string baseCatalogVersion;
        public string createdAt;
        public string minAppVersion;
        public string maxAppVersion;
        public LocalizationOverlayEntry[] entries;
    }

    [Serializable]
    internal sealed class LocalizationOverlayEntry
    {
        public string collectionId;
        public string entryId;
        public string locale;
        public string value;
        public string[] placeholders;
    }

    /// <summary>Immutable, thread-safe active overlay view.</summary>
    public sealed class LocalizationOverlaySnapshot
    {
        private readonly Dictionary<string, string> _values;

        internal LocalizationOverlaySnapshot(
            LocalizationOverlayManifest manifest,
            Dictionary<string, string> values)
        {
            Version = manifest.version ?? string.Empty;
            ProjectId = manifest.projectId ?? string.Empty;
            Channel = manifest.channel ?? string.Empty;
            ContentHash = manifest.sha256 ?? string.Empty;
            _values = values;
        }

        /// <summary>Immutable published version.</summary>
        public string Version { get; }
        /// <summary>Project bound by the signed manifest.</summary>
        public string ProjectId { get; }
        /// <summary>Publication channel bound by the signed manifest.</summary>
        public string Channel { get; }
        /// <summary>SHA-256 of the exact canonical bundle bytes.</summary>
        public string ContentHash { get; }
        /// <summary>Number of localized values in the snapshot.</summary>
        public int EntryCount => _values.Count;

        /// <summary>Builds a normalized collection-and-entry allowlist identity.</summary>
        public static string Identity(string collectionId, long entryId) =>
            $"{NormalizeCollectionId(collectionId)}:{entryId}";

        internal static string ValueIdentity(
            string collectionId,
            long entryId,
            string localeCode) =>
            $"{Identity(collectionId, entryId)}:{CanonicalLocale(localeCode)}";

        /// <summary>Resolves a value using the supplied ordered locale fallback chain.</summary>
        public bool TryGet(
            string collectionId,
            long entryId,
            IEnumerable<string> localeChain,
            out string value,
            out string resolvedLocale)
        {
            foreach (var locale in localeChain ?? Array.Empty<string>())
                if (_values.TryGetValue(
                        ValueIdentity(collectionId, entryId, locale),
                        out value))
                {
                    resolvedLocale = CanonicalLocale(locale);
                    return true;
                }
            value = string.Empty;
            resolvedLocale = string.Empty;
            return false;
        }

        internal static string NormalizeCollectionId(string value) =>
            (value ?? string.Empty).Replace("-", string.Empty).ToLowerInvariant();

        internal static string CanonicalLocale(string value) =>
            LocalizationModule.CanonicalizeLocaleCode(value).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies, validates, caches, activates, and rolls back remote catalog snapshots atomically.
    /// </summary>
    public sealed class LocalizationOverlayManager : IDisposable
    {
        /// <summary>Supported localization catalog protocol major.</summary>
        public const int SchemaVersion = 1;
        /// <summary>Maximum exact UTF-8 bundle bytes accepted by the player.</summary>
        public const int MaximumBytes = 4 * 1024 * 1024;
        /// <summary>Maximum localized values accepted in one overlay.</summary>
        public const int MaximumEntries = 50_000;
        /// <summary>Maximum distinct locales accepted in one overlay.</summary>
        public const int MaximumLocales = 32;
        /// <summary>Maximum UTF-16 characters accepted in one localized value.</summary>
        public const int MaximumValueCharacters = 16_384;
        /// <summary>Maximum placeholders accepted in one localized value.</summary>
        public const int MaximumPlaceholders = 64;

        private readonly LocalizationRemoteCatalogSettings _settings;
        private readonly object _gate = new();
        private LocalizationOverlaySnapshot _active;
        private LocalizationOverlaySnapshot _previous;
        private string _activeManifestToken;
        private string _activeBundleJson;
        private string _previousManifestToken;
        private string _previousBundleJson;

        /// <summary>Creates a verifier and atomic snapshot owner for one shipped trust policy.</summary>
        public LocalizationOverlayManager(LocalizationRemoteCatalogSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Status = settings.Enabled ? LocalizationOverlayStatus.None : LocalizationOverlayStatus.Disabled;
        }

        /// <summary>Raised after a snapshot activation or rollback changes resolved values.</summary>
        public event Action Changed;
        /// <summary>Current overlay lifecycle state.</summary>
        public LocalizationOverlayStatus Status { get; private set; }
        /// <summary>Most recent stable failure code.</summary>
        public string LastDiagnosticCode { get; private set; } = string.Empty;
        /// <summary>Most recent failure detail.</summary>
        public string LastMessage { get; private set; } = string.Empty;
        /// <summary>Current immutable overlay snapshot, or null when none is active.</summary>
        public LocalizationOverlaySnapshot Active
        {
            get
            {
                lock (_gate)
                    return _active;
            }
        }

        /// <summary>Verifies and atomically activates one signed candidate bundle.</summary>
        public LocalizationOverlayActivationResult TryActivate(
            string manifestToken,
            string bundleJson,
            string expectedProjectId,
            string appVersion)
        {
            try
            {
                var validation = ValidateManifest(
                    manifestToken,
                    expectedProjectId,
                    appVersion,
                    out var manifest);
                if (!validation.Success)
                    return validation;

                var bytes = Encoding.UTF8.GetBytes(bundleJson ?? string.Empty);
                if (bytes.Length != manifest.sizeBytes ||
                    !string.Equals(Sha256(bytes), manifest.sha256, StringComparison.OrdinalIgnoreCase))
                    return Reject("localization-overlay-hash-invalid", "Bundle bytes do not match the signed hash and size.");

                var bundle = JsonUtility.FromJson<LocalizationOverlayBundle>(bundleJson);
                if (bundle == null || bundle.schemaVersion != SchemaVersion ||
                    bundle.kind != "molca.localization.bundle")
                    return Reject("localization-overlay-schema-invalid", "Bundle schema is unsupported.");
                if (!string.Equals(bundle.projectId, manifest.projectId, StringComparison.Ordinal) ||
                    !string.Equals(bundle.channel, manifest.channel, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(bundle.version, manifest.version, StringComparison.Ordinal))
                    return Reject("localization-overlay-manifest-mismatch", "Bundle identity does not match its signed manifest.");

                var entries = bundle.entries ?? Array.Empty<LocalizationOverlayEntry>();
                if (entries.Length != manifest.entryCount || entries.Length > MaximumEntries)
                    return Reject("localization-overlay-entry-limit", "Bundle entry count is invalid.");
                var allowlist = _settings.BuildAllowlist();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var locales = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!IsCollectionId(entry.collectionId))
                        return Reject(
                            "localization-overlay-entry-invalid",
                            "Bundle contains an invalid collection id.");
                    if (!long.TryParse(entry.entryId, out var entryId) || entryId <= 0)
                        return Reject("localization-overlay-entry-invalid", "Bundle contains an invalid entry id.");
                    if (!IsLocale(entry.locale))
                        return Reject(
                            "localization-overlay-locale-invalid",
                            "Bundle contains an invalid locale.");
                    if ((entry.value ?? string.Empty).Length > MaximumValueCharacters)
                        return Reject(
                            "localization-overlay-value-limit",
                            "Bundle contains a value above the character limit.");
                    if ((entry.placeholders?.Length ?? 0) > MaximumPlaceholders)
                        return Reject(
                            "localization-overlay-placeholder-limit",
                            "Bundle contains too many placeholders for one value.");
                    var identity = LocalizationOverlaySnapshot.Identity(entry.collectionId, entryId);
                    if (!allowlist.TryGetValue(identity, out var allowed))
                        return Reject("localization-overlay-identity-unsupported", $"Entry '{identity}' is not shipped in the allowlist.");
                    var claimed = new HashSet<string>(
                        entry.placeholders ?? Array.Empty<string>(),
                        StringComparer.Ordinal);
                    var actual = ExtractPlaceholders(entry.value);
                    var expected = new HashSet<string>(
                        allowed.Placeholders ?? Array.Empty<string>(),
                        StringComparer.Ordinal);
                    if (!claimed.SetEquals(actual) || !expected.SetEquals(actual))
                        return Reject("localization-overlay-placeholder-mismatch", $"Entry '{identity}' changes its placeholder contract.");
                    var locale = LocalizationOverlaySnapshot.CanonicalLocale(entry.locale);
                    if (string.IsNullOrEmpty(locale))
                        return Reject("localization-overlay-locale-invalid", "Bundle contains an invalid locale.");
                    locales.Add(locale);
                    if (locales.Count > MaximumLocales)
                        return Reject("localization-overlay-locale-limit", "Bundle exceeds the locale limit.");
                    var valueIdentity = LocalizationOverlaySnapshot.ValueIdentity(
                        entry.collectionId, entryId, locale);
                    if (!values.TryAdd(valueIdentity, entry.value ?? string.Empty))
                        return Reject("localization-overlay-entry-duplicate", $"Bundle duplicates '{valueIdentity}'.");
                }
                if (locales.Count != manifest.localeCount)
                    return Reject("localization-overlay-locale-count", "Bundle locale count does not match its manifest.");

                var candidate = new LocalizationOverlaySnapshot(manifest, values);
                lock (_gate)
                {
                    _previous = _active;
                    _previousManifestToken = _activeManifestToken;
                    _previousBundleJson = _activeBundleJson;
                    _active = candidate;
                    _activeManifestToken = manifestToken;
                    _activeBundleJson = bundleJson;
                    Status = LocalizationOverlayStatus.Active;
                    LastDiagnosticCode = string.Empty;
                    LastMessage = string.Empty;
                }
                Changed?.Invoke();
                return new LocalizationOverlayActivationResult(
                    true, string.Empty, "Overlay activated.", candidate.Version);
            }
            catch (Exception exception)
            {
                return Reject("localization-overlay-invalid", exception.Message);
            }
        }

        internal LocalizationOverlayActivationResult ValidateManifestForDownload(
            string manifestToken,
            string expectedProjectId,
            string appVersion,
            out string bundleUrl)
        {
            var result = ValidateManifest(
                manifestToken,
                expectedProjectId,
                appVersion,
                out var manifest);
            bundleUrl = result.Success ? manifest.bundleUrl ?? string.Empty : string.Empty;
            if (result.Success && string.IsNullOrWhiteSpace(bundleUrl))
                return Reject(
                    "localization-overlay-bundle-url-invalid",
                    "Signed manifest has no bundle URL.");
            return result;
        }

        /// <summary>Swaps back to the prior verified in-memory snapshot.</summary>
        public LocalizationOverlayActivationResult Rollback()
        {
            lock (_gate)
            {
                if (_previous == null)
                    return Reject("localization-overlay-rollback-unavailable", "No prior in-memory overlay is available.");
                (_active, _previous) = (_previous, _active);
                (_activeManifestToken, _previousManifestToken) =
                    (_previousManifestToken, _activeManifestToken);
                (_activeBundleJson, _previousBundleJson) =
                    (_previousBundleJson, _activeBundleJson);
                Status = LocalizationOverlayStatus.RolledBack;
            }
            Changed?.Invoke();
            return new LocalizationOverlayActivationResult(
                true, string.Empty, "Overlay rolled back.", Active.Version);
        }

        /// <summary>Atomically persists the active snapshot as a bounded last-known-good cache.</summary>
        public bool TrySaveLastKnownGood(string directory, out string error)
        {
            error = string.Empty;
            string manifest;
            string bundle;
            string version;
            lock (_gate)
            {
                if (_active == null)
                {
                    error = "No active overlay.";
                    return false;
                }
                manifest = _activeManifestToken;
                bundle = _activeBundleJson;
                version = _active.Version;
            }
            try
            {
                Directory.CreateDirectory(directory);
                var prefix = Path.Combine(directory, version);
                WriteAtomically(prefix + ".manifest", manifest);
                WriteAtomically(prefix + ".bundle", bundle);
                foreach (var file in new DirectoryInfo(directory)
                             .GetFiles("*.manifest")
                             .OrderByDescending(file => file.LastWriteTimeUtc)
                             .Skip(_settings.RetainedVersions))
                {
                    File.Delete(file.FullName);
                    var bundlePath = Path.ChangeExtension(file.FullName, ".bundle");
                    if (File.Exists(bundlePath))
                        File.Delete(bundlePath);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>Reverifies and activates the newest valid cached snapshot.</summary>
        public LocalizationOverlayActivationResult TryLoadLastKnownGood(
            string directory,
            string expectedProjectId,
            string appVersion)
        {
            if (!Directory.Exists(directory))
                return new LocalizationOverlayActivationResult(
                    false, "localization-overlay-cache-empty", "No cache exists.", string.Empty);
            foreach (var manifestFile in new DirectoryInfo(directory)
                         .GetFiles("*.manifest")
                         .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                var bundlePath = Path.ChangeExtension(manifestFile.FullName, ".bundle");
                if (!File.Exists(bundlePath))
                    continue;
                var result = TryActivate(
                    File.ReadAllText(manifestFile.FullName),
                    File.ReadAllText(bundlePath),
                    expectedProjectId,
                    appVersion);
                if (result.Success)
                    return result;
            }
            return new LocalizationOverlayActivationResult(
                false, LastDiagnosticCode, LastMessage, string.Empty);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _active = null;
                _previous = null;
                _activeManifestToken = null;
                _activeBundleJson = null;
                _previousManifestToken = null;
                _previousBundleJson = null;
                Status = _settings.Enabled
                    ? LocalizationOverlayStatus.None
                    : LocalizationOverlayStatus.Disabled;
            }
        }

        private bool TryVerifyManifest(
            string token,
            out LocalizationOverlayManifest manifest,
            out string error)
        {
            manifest = null;
            error = string.Empty;
            var parts = (token ?? string.Empty).Trim().Split('.');
            if (parts.Length != 3 || !_settings.TryGetKey(parts[0], out var key))
            {
                error = "Manifest key is not trusted.";
                return false;
            }
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Convert.FromBase64String(key.ModulusBase64),
                    Exponent = Convert.FromBase64String(key.ExponentBase64),
                });
                if (!rsa.VerifyData(
                        Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                        Base64UrlDecode(parts[2]),
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1))
                {
                    error = "Manifest signature verification failed.";
                    return false;
                }
                manifest = JsonUtility.FromJson<LocalizationOverlayManifest>(
                    Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
                if (manifest == null || manifest.schemaVersion != SchemaVersion ||
                    manifest.kind != "molca.localization.manifest")
                {
                    error = "Manifest schema is unsupported.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private LocalizationOverlayActivationResult ValidateManifest(
            string manifestToken,
            string expectedProjectId,
            string appVersion,
            out LocalizationOverlayManifest manifest)
        {
            manifest = null;
            if (!_settings.Enabled)
                return Reject("localization-overlay-disabled", "Remote localization is disabled.");
            if (!TryVerifyManifest(manifestToken, out manifest, out var verificationError))
                return Reject("localization-overlay-signature-invalid", verificationError);
            expectedProjectId = string.IsNullOrWhiteSpace(expectedProjectId)
                ? _settings.ProjectId
                : expectedProjectId;
            if (!string.Equals(manifest.projectId, expectedProjectId, StringComparison.Ordinal))
                return Reject(
                    "localization-overlay-project-mismatch",
                    "Manifest project does not match this build.");
            if (!string.Equals(manifest.channel, _settings.Channel, StringComparison.OrdinalIgnoreCase))
                return Reject(
                    "localization-overlay-channel-mismatch",
                    "Manifest channel does not match configured policy.");
            if (!VersionWithinRange(appVersion, manifest.minAppVersion, manifest.maxAppVersion))
                return Reject(
                    "localization-overlay-app-incompatible",
                    "Manifest is incompatible with this app version.");
            if (manifest.sizeBytes < 0 || manifest.sizeBytes > MaximumBytes)
                return Reject(
                    "localization-overlay-size-invalid",
                    "Manifest exceeds the bundle size limit.");
            return new LocalizationOverlayActivationResult(
                true,
                string.Empty,
                "Manifest verified.",
                manifest.version);
        }

        private LocalizationOverlayActivationResult Reject(string code, string message)
        {
            Status = LocalizationOverlayStatus.Rejected;
            LastDiagnosticCode = code;
            LastMessage = message ?? string.Empty;
            return new LocalizationOverlayActivationResult(
                false, code, message, Active?.Version);
        }

        private static HashSet<string> ExtractPlaceholders(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            value ??= string.Empty;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '{' || index + 1 < value.Length && value[index + 1] == '{')
                    continue;
                var closing = value.IndexOfAny(new[] { '}', ':', ',' }, index + 1);
                if (closing <= index + 1)
                    continue;
                var token = value.Substring(index + 1, closing - index - 1).Trim();
                if (token.Length > 0 && token.All(character =>
                        char.IsLetterOrDigit(character) || character is '_' or '.'))
                    result.Add(token);
            }
            return result;
        }

        private static bool IsCollectionId(string value) =>
            value?.Length == 32 && value.All(character =>
                character is >= '0' and <= '9' ||
                character is >= 'a' and <= 'f' ||
                character is >= 'A' and <= 'F');

        private static bool IsLocale(string value)
        {
            var segments = (value ?? string.Empty).Replace('_', '-').Split('-');
            return segments.Length > 0 && segments.All(segment =>
                segment.Length is >= 1 and <= 8 &&
                segment.All(char.IsLetterOrDigit));
        }

        private static bool VersionWithinRange(string version, string minimum, string maximum)
        {
            if (!string.IsNullOrWhiteSpace(minimum) && CompareVersion(version, minimum) < 0)
                return false;
            return string.IsNullOrWhiteSpace(maximum) || CompareVersion(version, maximum) <= 0;
        }

        private static int CompareVersion(string left, string right)
        {
            static int[] Parts(string value)
            {
                var source = (value ?? string.Empty).Split('.');
                return source.Take(Math.Min(3, source.Length)).Select(part =>
                {
                    var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                    return int.TryParse(digits, out var number) ? number : 0;
                })
                .Concat(new[] { 0, 0, 0 })
                .Take(3)
                .ToArray();
            }
            var a = Parts(left);
            var b = Parts(right);
            for (var index = 0; index < 3; index++)
                if (a[index] != b[index])
                    return a[index].CompareTo(b[index]);
            return 0;
        }

        private static string Sha256(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }

        private static byte[] Base64UrlDecode(string value)
        {
            var normalized = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            return Convert.FromBase64String(normalized);
        }

        private static void WriteAtomically(string destination, string content)
        {
            var temporary = destination + ".tmp";
            File.WriteAllText(temporary, content ?? string.Empty, new UTF8Encoding(false));
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(temporary, destination);
        }
    }
}
