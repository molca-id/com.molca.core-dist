using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Molca.ContentPackage.Release
{
    /// <summary>Outcome of verifying a signed release manifest.</summary>
    public sealed class ReleaseVerificationResult
    {
        /// <summary>True when the manifest may influence local state.</summary>
        public bool Success { get; private set; }

        /// <summary>A <see cref="ContentReleaseReason"/> when <see cref="Success"/> is false.</summary>
        public string Reason { get; private set; }

        /// <summary>Operator-facing detail. Never contains key material or a token.</summary>
        public string Detail { get; private set; }

        /// <summary>The accepted manifest, or null on failure.</summary>
        public ContentReleaseManifest Manifest { get; private set; }

        /// <summary>The verified signing envelope, or null on failure.</summary>
        public ReleaseSignatureEnvelope Envelope { get; private set; }

        /// <summary>Builds a success carrying the accepted documents.</summary>
        public static ReleaseVerificationResult Ok(ContentReleaseManifest manifest, ReleaseSignatureEnvelope envelope) =>
            new ReleaseVerificationResult { Success = true, Manifest = manifest, Envelope = envelope, Reason = "", Detail = "" };

        /// <summary>Builds a failure carrying a contract reason and a human-readable detail.</summary>
        public static ReleaseVerificationResult Fail(string reason, string detail) =>
            new ReleaseVerificationResult { Success = false, Reason = reason, Detail = detail ?? "" };
    }

    /// <summary>The signed envelope payload — see <c>contracts/content-release-v1.md</c> §3.</summary>
    [Serializable]
    public class ReleaseSignatureEnvelope
    {
        /// <summary>Envelope schema version.</summary>
        public int schemaVersion;

        /// <summary>Always <c>molca.content.release</c>.</summary>
        public string kind;

        /// <summary>Wire protocol major of the manifest this envelope covers.</summary>
        public int protocolVersion;

        /// <summary>Release identity the signature binds.</summary>
        public string releaseId;

        /// <summary>Project identity the signature binds.</summary>
        public string projectId;

        /// <summary>Channel the signature binds.</summary>
        public string channel;

        /// <summary>Platform the signature binds.</summary>
        public string platform;

        /// <summary>Content version the signature binds.</summary>
        public string contentVersion;

        /// <summary>Lowercase hex SHA-256 of the canonical manifest bytes.</summary>
        public string manifestSha256;

        /// <summary>Signing instant, ISO 8601.</summary>
        public string issuedAt;
    }

    /// <summary>Verifies a signed release manifest before it may influence local state.</summary>
    /// <remarks>
    /// The order in <see cref="Verify"/> is the contract's, and it is mandatory rather than stylistic
    /// (§3). Each step is only meaningful once the previous one has passed:
    ///
    /// <list type="number">
    /// <item>the signature is worthless until the key id is one we trust;</item>
    /// <item>the envelope's claims are worthless until the signature verifies;</item>
    /// <item>parsing a document of an unknown protocol major risks acting on fields whose meaning
    /// has changed underneath the same name;</item>
    /// <item>the digest is what actually binds the manifest to the signature — everything the
    /// signature protects, it protects <em>through</em> this one field;</item>
    /// <item>and the cross-field comparison closes the substitution gap: without it a validly signed
    /// manifest for the <em>wrong project, channel, or platform</em> passes every earlier step.</item>
    /// </list>
    ///
    /// The digest is computed over the bytes as received and the document is never re-serialized to
    /// compare. Re-canonicalizing would drift on the first additive field this client does not model,
    /// which is exactly the change the contract permits a server to make without warning.
    /// </remarks>
    public sealed class ReleaseManifestVerifier
    {
        private readonly IReleaseKeyring _keyring;

        /// <summary>Builds a verifier over a keyring.</summary>
        /// <param name="keyring">Resolves signing key ids. Required.</param>
        /// <exception cref="ArgumentNullException">The keyring is null.</exception>
        public ReleaseManifestVerifier(IReleaseKeyring keyring)
        {
            _keyring = keyring ?? throw new ArgumentNullException(nameof(keyring));
        }

        /// <summary>
        /// Verifies a manifest against its signature, in the contract's mandatory order.
        /// </summary>
        /// <param name="manifestBytes">The manifest exactly as received. Never re-encoded.</param>
        /// <param name="signatureToken">The compact <c>kid.payload.signature</c> token.</param>
        /// <param name="expectedProjectId">
        /// The project this build is bound to, when known. Supplied, a manifest for another project
        /// is refused even if the server signed it; omitted, that check is skipped and the server
        /// remains the only guard.
        /// </param>
        /// <param name="expectedPlatform">The platform this player is, when known.</param>
        /// <returns>The outcome; never null.</returns>
        public ReleaseVerificationResult Verify(
            byte[] manifestBytes,
            string signatureToken,
            string expectedProjectId = null,
            string expectedPlatform = null)
        {
            if (manifestBytes == null || manifestBytes.Length == 0)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, "Manifest body is empty.");

            // 1. Parse the token and resolve the key id. An unknown kid is untrusted, full stop:
            //    there is no fallback key and no "try them all".
            var parts = (signatureToken ?? string.Empty).Trim().Split('.');
            if (parts.Length != 3)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, "Signature is not a compact token.");
            if (!_keyring.TryGetKey(parts[0], out var key))
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, "Signing key id is not trusted.");

            ReleaseSignatureEnvelope envelope;
            try
            {
                // 2. Verify the signature over `kid.payload` as ASCII, before trusting any claim.
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
                    return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, "Signature verification failed.");

                envelope = JsonUtility.FromJson<ReleaseSignatureEnvelope>(
                    Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            }
            catch (Exception exception)
            {
                // A malformed key, segment, or payload is untrusted rather than an error to surface
                // as a crash: from the player's side these are indistinguishable from tampering.
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted,
                    $"Signature could not be evaluated: {exception.Message}");
            }

            if (envelope == null)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, "Signed payload is not an envelope.");
            if (!string.Equals(envelope.kind, ContentReleaseManifest.ExpectedKind, StringComparison.Ordinal))
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted,
                    $"Envelope kind '{envelope.kind}' is not a content release.");

            // 3. Refuse an unknown protocol major. Unknown *fields* degrade gracefully (§9); an
            //    unknown *protocol* does not, because a field name may have been redefined.
            if (envelope.protocolVersion > ContentReleaseManifest.SupportedProtocolVersion)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ProtocolUnsupported,
                    $"Release protocol {envelope.protocolVersion} is newer than supported " +
                    $"({ContentReleaseManifest.SupportedProtocolVersion}). Update the app.");

            // 4. The digest binds everything else. Computed over the received bytes, compared without
            //    early exit so the comparison does not leak where the first difference is.
            string digest = Sha256Hex(manifestBytes);
            if (!FixedTimeEquals(digest, envelope.manifestSha256))
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted,
                    "Manifest digest does not match the signed envelope.");

            ContentReleaseManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<ContentReleaseManifest>(Encoding.UTF8.GetString(manifestBytes));
            }
            catch (Exception exception)
            {
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted,
                    $"Manifest is not readable: {exception.Message}");
            }
            if (manifest == null)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, "Manifest is empty.");

            // 5. Cross-field agreement. Without this a correctly signed manifest from another
            //    project, channel, or platform verifies perfectly and installs the wrong content.
            string mismatch = FirstMismatch(envelope, manifest);
            if (mismatch != null)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted,
                    $"Envelope and manifest disagree on {mismatch}.");

            if (!string.IsNullOrEmpty(expectedProjectId) &&
                !string.Equals(expectedProjectId, manifest.projectId, StringComparison.Ordinal))
                return ReleaseVerificationResult.Fail(ContentReleaseReason.Unauthorized,
                    "Release belongs to a different project than this build is bound to.");

            if (!string.IsNullOrEmpty(expectedPlatform) &&
                !string.Equals(expectedPlatform, manifest.platform, StringComparison.Ordinal))
                return ReleaseVerificationResult.Fail(ContentReleaseReason.PlatformUnsupported,
                    $"Release targets {manifest.platform}, this player is {expectedPlatform}.");

            // Structural checks the contract makes both sides enforce (§4.1-§4.3). These are not
            // security boundaries -- the signature already is -- but a signed manifest can still be
            // internally inconsistent, and finding that out mid-download strands a half-activation.
            string structural = ReleaseStructureValidator.FirstProblem(manifest);
            if (structural != null)
                return ReleaseVerificationResult.Fail(ContentReleaseReason.ManifestUntrusted, structural);

            return ReleaseVerificationResult.Ok(manifest, envelope);
        }

        private static string FirstMismatch(ReleaseSignatureEnvelope envelope, ContentReleaseManifest manifest)
        {
            if (!string.Equals(envelope.releaseId, manifest.releaseId, StringComparison.Ordinal)) return "releaseId";
            if (!string.Equals(envelope.projectId, manifest.projectId, StringComparison.Ordinal)) return "projectId";
            if (!string.Equals(envelope.channel, manifest.channel, StringComparison.Ordinal)) return "channel";
            if (!string.Equals(envelope.platform, manifest.platform, StringComparison.Ordinal)) return "platform";
            if (!string.Equals(envelope.contentVersion, manifest.contentVersion, StringComparison.Ordinal)) return "contentVersion";
            return null;
        }

        /// <summary>Lowercase hex SHA-256 of the given bytes.</summary>
        /// <param name="bytes">The bytes to digest.</param>
        public static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        /// <summary>
        /// Compares two hex digests without exiting early on the first difference.
        /// </summary>
        /// <remarks>
        /// <c>CryptographicOperations.FixedTimeEquals</c> is not available on every player scripting
        /// backend this SDK targets, so the comparison is written out. Length is compared first and
        /// separately: it is not secret, and there is nothing useful to hide about it.
        /// </remarks>
        internal static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static byte[] Base64UrlDecode(string value)
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }
    }

    /// <summary>
    /// Structural checks a signed manifest must still satisfy — see contract §4.1 to §4.3.
    /// </summary>
    /// <remarks>
    /// Separate from signature verification because these answer a different question. The signature
    /// asks "did Molca produce this?"; these ask "is this internally consistent enough to act on?".
    /// A signed manifest whose package references an object it does not declare is authentic and
    /// still unusable, and discovering that partway through a download is what leaves a device with
    /// content it cannot finish activating.
    /// </remarks>
    internal static class ReleaseStructureValidator
    {
        // Contract §4.3.
        private const int MaxObjects = 50_000;
        private const int MaxPackages = 512;
        private const int MaxDependencies = 64;
        private const long MaxObjectBytes = 8L * 1024 * 1024 * 1024;
        private const long MaxTotalBytes = 256L * 1024 * 1024 * 1024;

        /// <summary>Returns the first structural problem, or null when the manifest is consistent.</summary>
        /// <param name="manifest">The parsed manifest.</param>
        internal static string FirstProblem(ContentReleaseManifest manifest)
        {
            var objects = manifest.objects ?? Array.Empty<ContentReleaseManifest.ObjectEntry>();
            var packages = manifest.packages ?? Array.Empty<ContentReleaseManifest.PackageEntry>();

            if (objects.Length > MaxObjects) return $"Release declares {objects.Length} objects, over the {MaxObjects} bound.";
            if (packages.Length > MaxPackages) return $"Release declares {packages.Length} packages, over the {MaxPackages} bound.";

            var objectIds = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in objects)
            {
                if (entry == null || string.IsNullOrEmpty(entry.objectId)) return "An object has no objectId.";
                if (!objectIds.Add(entry.objectId)) return $"Object '{entry.objectId}' is declared more than once.";
                if (entry.sizeBytes < 0) return $"Object '{entry.objectId}' has a negative size.";
                if (entry.sizeBytes > MaxObjectBytes) return $"Object '{entry.objectId}' exceeds the 8 GiB object bound.";
                total += entry.sizeBytes;
            }
            if (total > MaxTotalBytes) return "Release exceeds the 256 GiB total bound.";

            var packageIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in packages)
            {
                if (package == null || string.IsNullOrEmpty(package.packageId)) return "A package has no packageId.";
                if (!packageIds.Add(package.packageId)) return $"Package '{package.packageId}' is declared more than once.";

                var dependencies = package.dependencies ?? Array.Empty<string>();
                if (dependencies.Length > MaxDependencies)
                    return $"Package '{package.packageId}' declares more than {MaxDependencies} dependencies.";

                foreach (var reference in package.objects ?? Array.Empty<ContentReleaseManifest.PackageObjectRef>())
                {
                    if (reference == null || string.IsNullOrEmpty(reference.objectId))
                        return $"Package '{package.packageId}' has an object reference with no objectId.";
                    if (!objectIds.Contains(reference.objectId))
                        return $"Package '{package.packageId}' references undeclared object '{reference.objectId}'.";
                }
            }

            foreach (var package in packages)
            {
                foreach (var dependency in package.dependencies ?? Array.Empty<string>())
                {
                    if (string.Equals(dependency, package.packageId, StringComparison.Ordinal))
                        return $"Package '{package.packageId}' depends on itself.";
                    if (!packageIds.Contains(dependency))
                        return $"Package '{package.packageId}' depends on '{dependency}', which is not in this release.";
                }
            }

            string cycle = FirstCycle(packages);
            if (cycle != null) return cycle;

            if (manifest.catalog == null || string.IsNullOrEmpty(manifest.catalog.catalogObjectId))
                return "Release declares no catalog object.";
            if (!objectIds.Contains(manifest.catalog.catalogObjectId))
                return $"Catalog object '{manifest.catalog.catalogObjectId}' is not declared in objects.";

            return null;
        }

        private static string FirstCycle(IReadOnlyList<ContentReleaseManifest.PackageEntry> packages)
        {
            var byId = new Dictionary<string, ContentReleaseManifest.PackageEntry>(StringComparer.Ordinal);
            foreach (var package in packages)
                if (package != null && !string.IsNullOrEmpty(package.packageId)) byId[package.packageId] = package;

            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done

            string Visit(string id)
            {
                if (state.TryGetValue(id, out int mark))
                    return mark == 1 ? $"Package dependencies form a cycle through '{id}'." : null;

                state[id] = 1;
                if (byId.TryGetValue(id, out var package))
                {
                    foreach (var dependency in package.dependencies ?? Array.Empty<string>())
                    {
                        if (string.IsNullOrEmpty(dependency)) continue;
                        string found = Visit(dependency);
                        if (found != null) return found;
                    }
                }
                state[id] = 2;
                return null;
            }

            foreach (var package in packages)
            {
                if (package == null || string.IsNullOrEmpty(package.packageId)) continue;
                string found = Visit(package.packageId);
                if (found != null) return found;
            }
            return null;
        }
    }
}
