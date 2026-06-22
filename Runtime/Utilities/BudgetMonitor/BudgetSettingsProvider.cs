using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Molca.Utilities
{
    /// <summary>
    /// Runtime resolver that picks the authored <see cref="BudgetSettings"/> matching the current
    /// platform (Sprint 54). It is the runtime complement to the editor-side
    /// <c>Molca.Editor.Doctor.BudgetSettingsResolver</c> used by the scene-performance audit: both share
    /// this resolution algorithm and the same platform token vocabulary so a static audit and the live
    /// <see cref="BudgetMonitor"/> grade against the <b>same</b> budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the editor resolver, this one cannot enumerate project assets (there is no
    /// <c>AssetDatabase</c> at runtime), so candidate budgets are supplied by the caller — e.g. the
    /// platform budgets assigned on the <see cref="BudgetMonitor"/> component. With no candidates it
    /// returns a fresh default instance, so resolution never returns null.
    /// </para>
    /// <para>Pure and main-thread agnostic; no Unity object is mutated. Safe to unit-test in isolation.</para>
    /// </remarks>
    public static class BudgetSettingsProvider
    {
        /// <summary>The outcome of a resolve: the chosen settings and where it came from.</summary>
        public readonly struct Resolution
        {
            /// <summary>The settings to grade against. Never null.</summary>
            public readonly BudgetSettings Settings;

            /// <summary>Human-readable provenance, e.g. <c>"platform:Quest"</c>, <c>"fallback:PC BudgetSettings"</c>, <c>"default"</c>.</summary>
            public readonly string Source;

            /// <summary>True when no authored asset was supplied and a default instance was created.</summary>
            public readonly bool IsDefault;

            /// <summary>True when authored assets exist but none matched the platform tokens.</summary>
            public readonly bool IsPlatformMismatch;

            internal Resolution(BudgetSettings settings, string source, bool isDefault, bool isPlatformMismatch)
            {
                Settings = settings;
                Source = source;
                IsDefault = isDefault;
                IsPlatformMismatch = isPlatformMismatch;
            }
        }

        /// <summary>Resolves the budget for the current <see cref="Application.platform"/> among <paramref name="candidates"/>.</summary>
        public static Resolution Resolve(IReadOnlyList<BudgetSettings> candidates)
            => ResolveFrom(candidates, TokensFor(Application.platform));

        /// <summary>
        /// Pure resolution core (shared with the editor audit): pick the first candidate whose name contains
        /// a platform token, in token order; else the first authored candidate (flagged as a mismatch); else
        /// a fresh default instance.
        /// </summary>
        /// <param name="candidates">Authored budget assets (any order); nulls are ignored.</param>
        /// <param name="priorityTokens">Platform name tokens, highest priority first.</param>
        public static Resolution ResolveFrom(
            IReadOnlyList<BudgetSettings> candidates, IReadOnlyList<string> priorityTokens)
        {
            var authored = (candidates ?? Array.Empty<BudgetSettings>())
                .Where(c => c != null).ToList();

            if (authored.Count == 0)
                return new Resolution(DefaultSettings(), "default", isDefault: true, isPlatformMismatch: false);

            foreach (var token in priorityTokens ?? Array.Empty<string>())
            {
                var match = authored.FirstOrDefault(
                    a => a.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                    return new Resolution(match, $"platform:{token}", isDefault: false, isPlatformMismatch: false);
            }

            // Authored budgets exist but none matched this platform — grade against one rather than silently
            // passing, and flag the mismatch so callers can warn.
            var fallback = authored[0];
            return new Resolution(fallback, $"fallback:{fallback.name}", isDefault: false, isPlatformMismatch: true);
        }

        /// <summary>
        /// Ordered platform name tokens for a runtime platform, highest priority first (Sprint 54). The
        /// vocabulary mirrors the editor resolver's build-target mapping so audit and monitor agree; Android
        /// is intentionally ambiguous (Quest is also Android), so it prefers a general <c>Mobile</c> budget,
        /// then <c>Quest</c>, then <c>Android</c>.
        /// </summary>
        public static IReadOnlyList<string> TokensFor(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.Android:
                    return new[] { "Mobile", "Quest", "Android" };
                case RuntimePlatform.IPhonePlayer:
                    return new[] { "Mobile", "iOS" };
                case RuntimePlatform.WSAPlayerX86:
                case RuntimePlatform.WSAPlayerX64:
                case RuntimePlatform.WSAPlayerARM:
                    return new[] { "Mobile", "UWP", "WSA" };
                case RuntimePlatform.WebGLPlayer:
                    return new[] { "Mobile", "WebGL", "PC" };
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.LinuxEditor:
                    return new[] { "PC", "Desktop", "Standalone" };
                default:
                    return new[] { "PC", "Desktop", "Standalone" };
            }
        }

        private static BudgetSettings DefaultSettings() => ScriptableObject.CreateInstance<BudgetSettings>();
    }
}
