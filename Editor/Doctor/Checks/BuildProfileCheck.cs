using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.Settings;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates every <see cref="BuildSettings"/> asset's build profiles: profile names
    /// must be unique and non-empty, the output path must be set, an Android/iOS
    /// application-identifier override must be a valid reverse-DNS identifier, and scripting
    /// define symbols must be valid identifiers.
    /// </summary>
    /// <remarks>
    /// The optional <c>runtimeManager</c> / <c>globalSettings</c> profile references are not
    /// flagged when unset — an empty value intentionally falls back to the project defaults.
    /// </remarks>
    public class BuildProfileCheck : IDoctorCheck
    {
        public string Id => "build-profile-valid";
        public string Description => "Build profiles have unique names, output paths, and valid identifiers/define symbols";

        // Reverse-DNS application id: at least two letter-led segments (Android requires a dot).
        private static readonly Regex AndroidId = new Regex(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$");
        private static readonly Regex IosId = new Regex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$");
        private static readonly Regex DefineSymbol = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            foreach (var guid in AssetDatabase.FindAssets("t:BuildSettings"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<BuildSettings>(path);
                if (settings == null)
                    continue;

                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var profile in settings.Profiles)
                {
                    if (profile == null)
                        continue;

                    var label = string.IsNullOrWhiteSpace(profile.name) ? "(unnamed)" : profile.name;

                    if (string.IsNullOrWhiteSpace(profile.name))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            "A build profile has an empty name; GetProfile cannot select it.", path));
                    }
                    // Names are matched case-insensitively by GetProfile, so collisions there are ambiguous.
                    else if (!seenNames.Add(profile.name))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            $"Duplicate build profile name \"{profile.name}\" — GetProfile would always return the first match.", path));
                    }

                    if (string.IsNullOrWhiteSpace(profile.outputPath))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"Build profile \"{label}\" has an empty output path.", path));
                    }

                    var appId = profile.applicationIdentifierOverride;
                    if (!string.IsNullOrEmpty(appId))
                    {
                        // The override is applied only for Android and iOS targets.
                        if (profile.target == BuildTarget.Android && !AndroidId.IsMatch(appId))
                        {
                            issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                                $"Build profile \"{label}\" Android application id \"{appId}\" is not a valid reverse-DNS package name (e.g. com.company.app).", path));
                        }
                        else if (profile.target == BuildTarget.iOS && !IosId.IsMatch(appId))
                        {
                            issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                                $"Build profile \"{label}\" iOS bundle id \"{appId}\" is not a valid identifier (e.g. com.company.app).", path));
                        }
                    }

                    if (!string.IsNullOrEmpty(profile.defineSymbols))
                    {
                        foreach (var symbol in profile.defineSymbols.Split(';'))
                        {
                            var trimmed = symbol.Trim();
                            if (trimmed.Length > 0 && !DefineSymbol.IsMatch(trimmed))
                            {
                                issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                                    $"Build profile \"{label}\" define symbol \"{trimmed}\" is not a valid C# identifier.", path));
                            }
                        }
                    }
                }
            }

            return issues;
        }
    }
}
