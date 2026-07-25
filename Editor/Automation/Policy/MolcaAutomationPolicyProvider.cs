using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The extension seam for replacing the automation authorization policy (§9.3). By default the kernel
    /// uses the built-in profile/allowlist policy (<see cref="MolcaAutomationPolicy.FromSettings"/>); a fork
    /// or consumer that needs its own authorization logic — role-based, an external approval service, a
    /// stricter CI gate — ships a provider instead of editing the kernel. Providers are discovered by
    /// <c>TypeCache</c>; the highest-<see cref="Priority"/> enabled one wins, else the built-in default.
    /// </summary>
    /// <remarks>
    /// Mirrors the <see cref="MolcaCommandProvider"/> discovery pattern (public parameterless constructor
    /// required). A provider may compose on top of the default by calling
    /// <see cref="MolcaAutomationPolicy.FromSettings"/> inside <see cref="CreatePolicy"/> and wrapping it.
    /// A provider that throws from <see cref="IsEnabled"/> or <see cref="CreatePolicy"/> is skipped and the
    /// resolver falls back — a broken policy provider must never leave the kernel with no policy.
    /// </remarks>
    public abstract class MolcaAutomationPolicyProvider
    {
        /// <summary>Selection priority; the highest enabled provider wins. Use distinct values — ties are unspecified.</summary>
        public abstract int Priority { get; }

        /// <summary>Whether this provider should supply the policy right now. Defaults to true.</summary>
        /// <returns>True to participate in resolution.</returns>
        public virtual bool IsEnabled() => true;

        /// <summary>Creates the authorization policy the kernel should enforce.</summary>
        /// <returns>The policy; a null return causes the resolver to fall back to the default.</returns>
        public abstract IMolcaAutomationPolicy CreatePolicy();

        /// <summary>Short label naming this policy source, for status/audit surfaces.</summary>
        /// <returns>A human-readable source label. Defaults to the type name.</returns>
        public virtual string Describe() => GetType().Name;
    }

    /// <summary>
    /// Resolves the active <see cref="IMolcaAutomationPolicy"/> from discovered
    /// <see cref="MolcaAutomationPolicyProvider"/>s, falling back to the built-in settings policy. The
    /// pure <see cref="Resolve"/> overload is unit-testable; the kernel calls <see cref="ResolveDiscovered"/>.
    /// </summary>
    public static class MolcaAutomationPolicyResolver
    {
        /// <summary>The source label used when the built-in settings policy is in force.</summary>
        public const string SettingsSource = "settings";

        /// <summary>
        /// Picks the highest-priority enabled provider's policy, or the built-in settings policy when no
        /// provider supplies one. Never returns null.
        /// </summary>
        /// <param name="providers">Candidate providers (null/empty → the settings default).</param>
        /// <param name="source">The label naming which policy source was chosen.</param>
        /// <returns>The resolved policy.</returns>
        public static IMolcaAutomationPolicy Resolve(
            IEnumerable<MolcaAutomationPolicyProvider> providers, out string source)
        {
            MolcaAutomationPolicyProvider chosen = null;
            if (providers != null)
            {
                foreach (var provider in providers)
                {
                    if (provider == null) continue;
                    bool enabled;
                    try { enabled = provider.IsEnabled(); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Molca Automation] Policy provider '{Describe(provider)}' threw from IsEnabled (skipped): {ex.Message}");
                        continue;
                    }
                    if (!enabled) continue;
                    if (chosen == null || provider.Priority > chosen.Priority) chosen = provider;
                }
            }

            if (chosen != null)
            {
                try
                {
                    var policy = chosen.CreatePolicy();
                    if (policy != null)
                    {
                        source = Describe(chosen);
                        return policy;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Automation] Policy provider '{Describe(chosen)}' failed to create a policy; using the default: {ex.Message}");
                }
            }

            source = SettingsSource;
            return MolcaAutomationPolicy.FromSettings();
        }

        /// <summary>Discovers policy providers via <c>TypeCache</c> and resolves the active policy.</summary>
        /// <param name="source">The label naming which policy source was chosen.</param>
        /// <returns>The resolved policy.</returns>
        public static IMolcaAutomationPolicy ResolveDiscovered(out string source) => Resolve(Discover(), out source);

        private static IEnumerable<MolcaAutomationPolicyProvider> Discover()
        {
            var providers = new List<MolcaAutomationPolicyProvider>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaAutomationPolicyProvider>())
            {
                // Only auto-discover providers with a public parameterless constructor (same rule as
                // command providers); anything needing arguments is not meant for discovery.
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null) continue;
                try { providers.Add((MolcaAutomationPolicyProvider)Activator.CreateInstance(type)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Automation] Policy provider '{type.FullName}' could not be instantiated (skipped): {ex.Message}");
                }
            }
            return providers;
        }

        private static string Describe(MolcaAutomationPolicyProvider provider)
        {
            try { return provider.Describe(); }
            catch { return provider.GetType().Name; }
        }
    }
}
