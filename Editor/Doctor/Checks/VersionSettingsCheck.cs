using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.Settings;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates every <see cref="VersionSettings"/> asset: the version components and
    /// build number must be in range, the SemVer pre-release / build-metadata identifiers
    /// must be well-formed, and a changelog path must be present (and its folder exist)
    /// when auto-append is enabled.
    /// </summary>
    public class VersionSettingsCheck : IDoctorCheck
    {
        public string Id => "version-settings-valid";
        public string Description => "VersionSettings has a valid version/build number and well-formed SemVer identifiers";

        // SemVer dot-separated identifier set: alphanumerics and hyphens per segment.
        private static readonly Regex SemverIdentifier = new Regex(@"^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            foreach (var guid in AssetDatabase.FindAssets("t:VersionSettings"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<VersionSettings>(path);
                if (settings == null)
                    continue;

                if (!settings.IsValidVersion())
                {
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                        "Invalid version: all components must be >= 0 and the build number must be >= 1.", path));
                }

                var pre = settings.GetPreReleaseIdentifier();
                if (!string.IsNullOrEmpty(pre) && !SemverIdentifier.IsMatch(pre))
                {
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"Pre-release identifier \"{pre}\" is not a valid SemVer identifier (alphanumerics and hyphens, dot-separated).", path));
                }

                var meta = settings.GetBuildMetadata();
                if (!string.IsNullOrEmpty(meta) && !SemverIdentifier.IsMatch(meta))
                {
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                        $"Build metadata \"{meta}\" is not a valid SemVer identifier (alphanumerics and hyphens, dot-separated).", path));
                }

                if (settings.AutoAppendChangelogOnBuild)
                {
                    var changelog = settings.ChangelogPath;
                    if (string.IsNullOrWhiteSpace(changelog))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            "Auto-append changelog is enabled but the Changelog Path is empty.", path));
                    }
                    else
                    {
                        // The file itself may be created on first build; its parent folder must exist.
                        var dir = Path.GetDirectoryName(changelog);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                                $"Changelog Path \"{changelog}\" points into a folder that does not exist.", path));
                        }
                    }
                }
            }

            return issues;
        }
    }
}
