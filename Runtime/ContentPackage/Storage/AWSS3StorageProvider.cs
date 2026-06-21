using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Molca.ContentPackage
{
    /// <summary>
    /// Storage provider that deploys Addressables content to an AWS S3 bucket using the AWS CLI.
    /// </summary>
    /// <remarks>
    /// Requires AWS CLI v2 installed and credentials configured via <c>aws configure</c>
    /// or environment variables (<c>AWS_ACCESS_KEY_ID</c>, <c>AWS_SECRET_ACCESS_KEY</c>,
    /// <c>AWS_DEFAULT_REGION</c>). Supports named profiles, dry-run, and delete-removed.
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-storage.png")]
    [CreateAssetMenu(fileName = "AWSS3StorageProvider",
                     menuName  = "Molca/Content Package/Storage/AWS S3", order = 50)]
    public class AWSS3StorageProvider : ContentPackageStorageProvider
    {
        [Tooltip("S3 bucket name (without s3:// prefix).")]
        public string s3Bucket = "";

        [Tooltip("AWS region of the bucket (e.g. ap-southeast-1).")]
        public string s3Region = "us-east-1";

        /// <summary>
        /// Key prefix inside the bucket. A trailing slash is added automatically if omitted.
        /// Example: <c>content/</c> → bundles land at <c>s3://bucket/content/StandaloneWindows64/</c>.
        /// </summary>
        [Tooltip("Folder prefix inside the bucket. Leave empty to deploy to the bucket root.")]
        public string s3KeyPrefix = "content/";

        /// <summary>
        /// Named AWS CLI profile. Leave empty to use the default profile or environment variables.
        /// </summary>
        [Tooltip("AWS CLI profile name. Empty = default profile / env vars.")]
        public string awsProfile = "";

        [Tooltip("Pass --dryrun to aws s3 sync — shows what would be uploaded without transferring.")]
        public bool dryRun = false;

        [Tooltip("Pass --delete to aws s3 sync — removes remote files that no longer exist locally.")]
        public bool deleteRemoved = false;

        [Tooltip("Extra arguments appended verbatim to the aws s3 sync command.")]
        public string extraArgs = "";

        // ── ContentPackageStorageProvider ────────────────────────────────────

        /// <inheritdoc/>
        public override string DisplayName => "AWS S3";

        /// <inheritdoc/>
        public override string ExecutableName => "aws";

        /// <inheritdoc/>
        public override string BuildDeployArguments(string localPath, string buildTarget)
        {
            var sb = new StringBuilder("s3 sync");
            sb.Append($" \"{localPath}\"");
            sb.Append($" {S3Destination(buildTarget)}");
            sb.Append($" --region {s3Region}");
            if (!string.IsNullOrEmpty(awsProfile))   sb.Append($" --profile {awsProfile}");
            if (dryRun)                               sb.Append(" --dryrun");
            if (deleteRemoved)                        sb.Append(" --delete");
            if (!string.IsNullOrEmpty(extraArgs))     sb.Append($" {extraArgs}");
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string GetDestinationDescription(string buildTarget)
            => S3Destination(buildTarget);

        /// <inheritdoc/>
        public override bool CheckAvailability(out string errorMessage)
        {
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

            errorMessage = _awsAvailable.Value ? null : "AWS CLI not found in PATH. Install AWS CLI v2 and restart the Unity Editor.";
            return _awsAvailable.Value;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Constructs the full S3 destination URI for the given build target.</summary>
        public string S3Destination(string buildTarget)
        {
            var prefix = s3KeyPrefix.TrimEnd('/');
            return $"s3://{s3Bucket}/{(string.IsNullOrEmpty(prefix) ? "" : prefix + "/")}{buildTarget}/";
        }

        // Cached per editor session — cleared automatically on domain reload.
        [System.NonSerialized] private bool? _awsAvailable;
    }
}
