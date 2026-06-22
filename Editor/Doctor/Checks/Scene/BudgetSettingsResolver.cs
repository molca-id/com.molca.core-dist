using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Utilities;
using UnityEditor;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Resolves the <see cref="BudgetSettings"/> asset that the scene-performance audit
    /// should grade against, choosing the one that matches the active build target.
    /// </summary>
    /// <remarks>
    /// The framework ships several authored budgets (e.g. <c>Mobile</c>, <c>PC</c>,
    /// <c>Quest</c>). A naïve "first <c>t:BudgetSettings</c>" lookup would grade, say, a
    /// Quest scene against the PC budget — a correctness bug, not a refinement. This
    /// resolver matches an asset's <see cref="UnityEngine.Object.name"/> against an ordered
    /// list of platform tokens for the current <see cref="BuildTarget"/>, falling back to
    /// any authored asset and finally a fresh default instance.
    /// <para>
    /// Android is intentionally ambiguous (standalone Quest builds are also Android): the
    /// token order prefers a general <c>Mobile</c> budget, then <c>Quest</c>, then
    /// <c>Android</c>, so a project that authors only a Quest budget still resolves to it via
    /// fallback. A fork can adjust <see cref="TokensFor"/> by editing this file in its own
    /// layer, or pass an explicit <paramref name="target"/>.
    /// </para>
    /// This is the one piece shared with the runtime BudgetMonitor hardening work: it lives
    /// editor-side here and is promoted to a runtime <c>BudgetSettingsProvider</c> there.
    /// Editor-only; main thread only (uses <see cref="AssetDatabase"/>).
    /// </remarks>
    public static class BudgetSettingsResolver
    {
        /// <summary>The outcome of a resolve: the chosen settings and where it came from.</summary>
        public readonly struct Resolution
        {
            /// <summary>The settings to grade against. Never null.</summary>
            public readonly BudgetSettings Settings;

            /// <summary>
            /// Human-readable provenance, e.g. <c>"platform:Quest"</c>,
            /// <c>"fallback:PC BudgetSettings"</c>, or <c>"default"</c>.
            /// </summary>
            public readonly string Source;

            /// <summary>True when no authored asset was found and a default instance was created.</summary>
            public readonly bool IsDefault;

            /// <summary>True when an authored asset exists but none matched the platform tokens.</summary>
            public readonly bool IsPlatformMismatch;

            internal Resolution(BudgetSettings settings, string source, bool isDefault, bool isPlatformMismatch)
            {
                Settings = settings;
                Source = source;
                IsDefault = isDefault;
                IsPlatformMismatch = isPlatformMismatch;
            }
        }

        /// <summary>
        /// Resolves the budget for the given build target (defaults to the active target).
        /// </summary>
        /// <param name="target">Build target to resolve for; null uses
        /// <see cref="EditorUserBuildSettings.activeBuildTarget"/>.</param>
        public static Resolution Resolve(BuildTarget? target = null)
        {
            var t = target ?? EditorUserBuildSettings.activeBuildTarget;
            var assets = LoadAllBudgetSettings();
            return ResolveFrom(assets, TokensFor(t));
        }

        /// <summary>
        /// Pure resolution core (testable): pick the first asset whose name contains a
        /// platform token, in token order; else the first authored asset; else a default.
        /// </summary>
        /// <param name="candidates">Authored budget assets (any order).</param>
        /// <param name="priorityTokens">Platform name tokens, highest priority first.</param>
        internal static Resolution ResolveFrom(
            IReadOnlyList<BudgetSettings> candidates, IReadOnlyList<string> priorityTokens)
        {
            // Delegate to the runtime provider's shared algorithm (Sprint 54) so the static audit and the
            // live BudgetMonitor resolve identically, then adapt to this editor-public Resolution type.
            var r = BudgetSettingsProvider.ResolveFrom(candidates, priorityTokens);
            return new Resolution(r.Settings, r.Source, r.IsDefault, r.IsPlatformMismatch);
        }

        /// <summary>Ordered platform name tokens for a build target, highest priority first.</summary>
        internal static IReadOnlyList<string> TokensFor(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.Android:
                    return new[] { "Mobile", "Quest", "Android" };
                case BuildTarget.iOS:
                    return new[] { "Mobile", "iOS" };
                case BuildTarget.WSAPlayer:
                    return new[] { "Mobile", "UWP", "WSA" };
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                    return new[] { "PC", "Desktop", "Standalone" };
                case BuildTarget.WebGL:
                    return new[] { "Mobile", "WebGL", "PC" };
                default:
                    return new[] { "PC", "Desktop", "Standalone" };
            }
        }

        private static List<BudgetSettings> LoadAllBudgetSettings()
        {
            return AssetDatabase.FindAssets($"t:{nameof(BudgetSettings)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Select(AssetDatabase.LoadAssetAtPath<BudgetSettings>)
                .Where(a => a != null)
                .ToList();
        }
    }
}
