using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Discovers <see cref="MolcaHubActivityProvider"/>s via <c>TypeCache</c> and gathers the ordered,
    /// deduplicated set of activity chips they currently expose for the Hub's bottom rail.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Providers are stateful observers, so the
    /// caller (the rail) instantiates them once via <see cref="CreateProviders"/>, subscribes to each
    /// provider's <c>Changed</c> event, and disposes them on teardown. Discovery requires a public
    /// parameterless constructor; a subclass without one is owned by its own caller and is skipped
    /// silently. A discovered provider that throws while constructing, or while listing, is skipped
    /// (logged) rather than breaking the rail. Editor-only; main thread.
    /// </remarks>
    public static class MolcaHubActivityRegistry
    {
        /// <summary>
        /// Instantiates every discovered concrete, default-constructible <see cref="MolcaHubActivityProvider"/>.
        /// The caller owns the returned instances and must <see cref="IDisposable.Dispose"/> them when done.
        /// </summary>
        /// <returns>The live provider instances (empty if none / all failed).</returns>
        public static IReadOnlyList<MolcaHubActivityProvider> CreateProviders()
        {
            var providers = new List<MolcaHubActivityProvider>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaHubActivityProvider>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;

                // A provider the registry can own must be default-constructible. A subclass that exposes
                // only parameterised constructors is owned by its own caller instead (test doubles, and
                // providers composed by the system they observe), so it is not a discovery failure and must
                // not be reported as one — TypeCache sees test assemblies too. The warning below stays for
                // the case that is a genuine fault: a default-constructible provider that throws.
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    providers.Add((MolcaHubActivityProvider)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Activity provider '{type.FullName}' could not be instantiated (skipped): {ex.Message}");
                }
            }
            return providers;
        }

        /// <summary>
        /// Gathers the current activities from <paramref name="providers"/>, drops null/id-less and duplicate
        /// ids (first wins), and orders them by <see cref="MolcaHubActivity.Order"/> then id. Exposed for
        /// testing; a provider that throws while listing is skipped.
        /// </summary>
        /// <param name="providers">The provider instances to poll.</param>
        /// <returns>The ordered, deduplicated activity set.</returns>
        public static IReadOnlyList<MolcaHubActivity> Collect(IEnumerable<MolcaHubActivityProvider> providers)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<MolcaHubActivity>();

            foreach (var provider in providers ?? Enumerable.Empty<MolcaHubActivityProvider>())
            {
                if (provider == null) continue;

                IEnumerable<MolcaHubActivity> activities;
                try
                {
                    activities = provider.GetActivities();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Activity provider '{provider.GetType().FullName}' threw while listing (skipped): {ex.Message}");
                    continue;
                }

                if (activities == null) continue;
                foreach (var activity in activities)
                {
                    if (activity == null || string.IsNullOrEmpty(activity.Id)) continue;
                    if (!seen.Add(activity.Id)) continue; // first registration of a duplicate id wins
                    result.Add(activity);
                }
            }

            result.Sort((a, b) =>
                a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
            return result;
        }
    }
}
