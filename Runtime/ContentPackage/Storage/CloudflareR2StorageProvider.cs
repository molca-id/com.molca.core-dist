using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Molca.ContentPackage
{
    /// <summary>
    /// Storage provider that deploys Addressables content to a Cloudflare R2 bucket
    /// using the AWS CLI (R2 is S3-compatible).
    /// </summary>
    /// <remarks>
    /// Requires AWS CLI v2 installed. Authentication uses an R2 API token configured as
    /// an AWS CLI profile — R2 does not use standard AWS credentials.
    ///
    /// One-time setup:
    /// <code>
    /// aws configure --profile cloudflare-r2
    ///   AWS Access Key ID:     &lt;R2 Access Key ID&gt;
    ///   AWS Secret Access Key: &lt;R2 Secret Access Key&gt;
    ///   Default region:        auto
    ///   Default output format: json
    /// </code>
    ///
    /// Get your Account ID and API tokens from the Cloudflare dashboard:
    /// Cloudflare Dashboard → R2 → Manage R2 API Tokens.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-storage.png")]
    [CreateAssetMenu(fileName = "CloudflareR2StorageProvider",
                     menuName  = "Molca/Content Package/Storage/Cloudflare R2", order = 50)]
    public class CloudflareR2StorageProvider : ContentPackageStorageProvider
    {
        [Tooltip("Cloudflare Account ID. Found in the Cloudflare dashboard right sidebar.")]
        public string accountId = "";

        [Tooltip("R2 bucket name.")]
        public string bucketName = "";

        /// <summary>
        /// Folder prefix inside the bucket.
        /// Example: <c>content/</c> → files land at <c>s3://bucket/content/StandaloneWindows64/</c>.
        /// </summary>
        [Tooltip("Folder prefix inside the bucket. Leave empty to deploy to the bucket root.")]
        public string keyPrefix = "content/";

        /// <summary>
        /// AWS CLI profile configured with R2 API token credentials.
        /// Create via <c>aws configure --profile &lt;name&gt;</c> using your R2 Access Key ID and Secret.
        /// </summary>
        [Tooltip("AWS CLI profile holding R2 API token credentials.")]
        public string awsProfile = "cloudflare-r2";

        [Tooltip("Show what would be uploaded without actually transferring files (--dryrun).")]
        public bool dryRun = false;

        [Tooltip("Delete remote files that no longer exist locally (--delete).")]
        public bool deleteRemoved = false;

        [Tooltip("Extra arguments appended verbatim to the aws s3 sync command.")]
        public string extraArgs = "";

        // ── ContentPackageStorageProvider ────────────────────────────────────

        /// <inheritdoc/>
        public override string DisplayName => "Cloudflare R2";

        /// <inheritdoc/>
        public override string ExecutableName => "aws";

        /// <inheritdoc/>
        public override string BuildDeployArguments(string localPath, string buildTarget)
        {
            var sb = new StringBuilder("s3 sync");
            sb.Append($" \"{localPath}\"");
            sb.Append($" {R2Destination(buildTarget)}");
            sb.Append($" --endpoint-url {EndpointUrl}");
            sb.Append(" --region auto");
            if (!string.IsNullOrEmpty(awsProfile)) sb.Append($" --profile {awsProfile}");
            if (dryRun)                            sb.Append(" --dryrun");
            if (deleteRemoved)                     sb.Append(" --delete");
            if (!string.IsNullOrEmpty(extraArgs))  sb.Append($" {extraArgs}");
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string GetDestinationDescription(string buildTarget)
            => R2Destination(buildTarget);

        /// <inheritdoc/>
        public override bool CheckAvailability(out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                errorMessage = "Cloudflare Account ID is not set.";
                return false;
            }

            if (_awsAvailable.HasValue)
            {
                errorMessage = _awsAvailable.Value ? null : "AWS CLI not found in PATH.";
                return _awsAvailable.Value;
            }

            try
            {
                var p = Process.Start(new ProcessStartInfo("aws", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                });
                p?.WaitForExit(2000);
                _awsAvailable = p?.ExitCode == 0;
            }
            catch
            {
                _awsAvailable = false;
            }

            errorMessage = _awsAvailable.Value
                ? null
                : "AWS CLI not found in PATH. Install AWS CLI v2 and restart the Unity Editor.";
            return _awsAvailable.Value;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>R2 S3-compatible endpoint URL derived from the account ID.</summary>
        public string EndpointUrl => $"https://{accountId}.r2.cloudflarestorage.com";

        /// <summary>Constructs the S3-style destination URI for the given build target.</summary>
        public string R2Destination(string buildTarget)
        {
            var prefix = keyPrefix.TrimEnd('/');
            return $"s3://{bucketName}/{(string.IsNullOrEmpty(prefix) ? "" : prefix + "/")}{buildTarget}/";
        }

        [System.NonSerialized] private bool? _awsAvailable;
    }
}
