using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Molca.ContentPackage
{
    /// <summary>
    /// Storage provider that deploys Addressables content to a Google Cloud Storage bucket
    /// using the <c>gcloud storage cp</c> command (Google Cloud SDK).
    /// </summary>
    /// <remarks>
    /// Requires the Google Cloud SDK installed and authenticated via <c>gcloud auth login</c>
    /// or a service account key set in <c>GOOGLE_APPLICATION_CREDENTIALS</c>.
    /// Install: https://cloud.google.com/sdk/docs/install
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-storage.png")]
    [CreateAssetMenu(fileName = "GCSStorageProvider",
                     menuName  = "Molca/Content Package/Storage/Google Cloud Storage", order = 50)]
    public class GCSStorageProvider : ContentPackageStorageProvider
    {
        [Tooltip("GCS bucket name (without gs:// prefix).")]
        public string gcsBucket = "";

        /// <summary>
        /// Folder prefix inside the bucket.
        /// Example: <c>content/</c> → files land at <c>gs://bucket/content/StandaloneWindows64/</c>.
        /// </summary>
        [Tooltip("Folder prefix inside the bucket. Leave empty to deploy to the bucket root.")]
        public string gcsKeyPrefix = "content/";

        [Tooltip("Named gcloud configuration to use. Leave empty to use the currently active configuration.")]
        public string gcloudConfiguration = "";

        [Tooltip("If set, impersonate this service account for the upload (requires impersonation permissions).")]
        public string impersonateServiceAccount = "";

        [Tooltip("Show what would be copied without actually transferring files (--dry-run).")]
        public bool dryRun = false;

        [Tooltip("Delete remote files that no longer exist locally (-d flag on rsync).")]
        public bool deleteRemoved = false;

        [Tooltip("Extra arguments appended verbatim to the gcloud storage rsync command.")]
        public string extraArgs = "";

        // ── ContentPackageStorageProvider ────────────────────────────────────

        /// <inheritdoc/>
        public override string DisplayName => "Google Cloud Storage";

        /// <inheritdoc/>
        public override string ExecutableName => "gcloud";

        /// <inheritdoc/>
        public override string BuildDeployArguments(string localPath, string buildTarget)
        {
            var sb = new StringBuilder("storage rsync");
            sb.Append($" \"{localPath}\"");
            sb.Append($" {GCSDestination(buildTarget)}");
            sb.Append(" -r"); // recursive
            if (deleteRemoved)                               sb.Append(" -d");
            if (dryRun)                                      sb.Append(" --dry-run");
            if (!string.IsNullOrEmpty(gcloudConfiguration)) sb.Append($" --configuration={gcloudConfiguration}");
            if (!string.IsNullOrEmpty(impersonateServiceAccount))
                sb.Append($" --impersonate-service-account={impersonateServiceAccount}");
            if (!string.IsNullOrEmpty(extraArgs))            sb.Append($" {extraArgs}");
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string GetDestinationDescription(string buildTarget)
            => GCSDestination(buildTarget);

        /// <inheritdoc/>
        public override bool CheckAvailability(out string errorMessage)
        {
            if (_gcloudAvailable.HasValue)
            {
                errorMessage = _gcloudAvailable.Value ? null : "gcloud CLI not found in PATH.";
                return _gcloudAvailable.Value;
            }

            try
            {
                var p = Process.Start(new ProcessStartInfo("gcloud", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                });
                p?.WaitForExit(2000);
                _gcloudAvailable = p?.ExitCode == 0;
            }
            catch
            {
                _gcloudAvailable = false;
            }

            errorMessage = _gcloudAvailable.Value
                ? null
                : "gcloud CLI not found in PATH. Install the Google Cloud SDK and restart the Unity Editor.";
            return _gcloudAvailable.Value;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Constructs the full GCS destination URI for the given build target.</summary>
        public string GCSDestination(string buildTarget)
        {
            var prefix = gcsKeyPrefix.TrimEnd('/');
            return $"gs://{gcsBucket}/{(string.IsNullOrEmpty(prefix) ? "" : prefix + "/")}{buildTarget}/";
        }

        [System.NonSerialized] private bool? _gcloudAvailable;
    }
}
