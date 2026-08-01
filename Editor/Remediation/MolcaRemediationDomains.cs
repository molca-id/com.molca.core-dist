using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// One project-wide audit domain a remediation pass can be run against.
    /// </summary>
    /// <remarks>
    /// A domain is registered only when a single project-wide sweep is meaningful for it. Per-object
    /// domains are deliberately absent: sequence remediation targets one <c>SequenceController</c>, so it
    /// belongs on that controller's own surface rather than in a "fix the project" button that would have to
    /// invent which controllers to touch.
    /// </remarks>
    public sealed class MolcaRemediationDomain
    {
        /// <summary>Creates a domain descriptor.</summary>
        /// <param name="id">Stable key, matching the request's domain (e.g. <c>network</c>).</param>
        /// <param name="label">Human-facing name for the row.</param>
        /// <param name="createRequest">Builds a request for the given policy. Must re-audit on each call.</param>
        /// <param name="order">Sort order in the panel; ties broken by <paramref name="id"/>.</param>
        /// <param name="isAvailable">Optional gate; a domain with nothing configured can hide itself.</param>
        public MolcaRemediationDomain(
            string id,
            string label,
            Func<RemediationPolicy, MolcaRemediationRequest> createRequest,
            int order = 100,
            Func<bool> isAvailable = null)
        {
            Id = id;
            Label = label;
            CreateRequest = createRequest;
            Order = order;
            IsAvailable = isAvailable ?? (() => true);
        }

        /// <summary>Stable key, matching the request's domain.</summary>
        public string Id { get; }

        /// <summary>Human-facing name.</summary>
        public string Label { get; }

        /// <summary>Sort order within the panel.</summary>
        public int Order { get; }

        /// <summary>Builds a request for a policy.</summary>
        public Func<RemediationPolicy, MolcaRemediationRequest> CreateRequest { get; }

        /// <summary>Whether this domain is worth showing at all.</summary>
        public Func<bool> IsAvailable { get; }
    }

    /// <summary>
    /// Contributes project-wide remediation domains. Discovered by <c>TypeCache</c>; needs a public
    /// parameterless constructor.
    /// </summary>
    /// <remarks>
    /// The seam a fork or add-on uses to put its own audit behind the same button, without Core knowing it
    /// exists.
    /// </remarks>
    public interface IMolcaRemediationDomainProvider
    {
        /// <summary>Returns the domains this provider contributes; never <c>null</c>.</summary>
        /// <returns>Domain descriptors.</returns>
        IEnumerable<MolcaRemediationDomain> GetDomains();
    }

    /// <summary>
    /// Discovers every registered <see cref="MolcaRemediationDomain"/>.
    /// </summary>
    /// <remarks>Editor-only, main thread. Cached until <see cref="Reset"/>.</remarks>
    public static class MolcaRemediationDomains
    {
        private static List<MolcaRemediationDomain> _domains;

        /// <summary>Every registered domain, ordered, excluding those whose gate says otherwise.</summary>
        public static IReadOnlyList<MolcaRemediationDomain> All
        {
            get
            {
                EnsureBuilt();
                return _domains.Where(d => Available(d)).ToList();
            }
        }

        /// <summary>Clears the discovery cache. Intended for tests.</summary>
        public static void Reset() => _domains = null;

        /// <summary>Returns the domain with the given id, or <c>null</c>.</summary>
        /// <param name="id">A domain id.</param>
        /// <returns>The domain, or <c>null</c>.</returns>
        public static MolcaRemediationDomain ById(string id)
        {
            EnsureBuilt();
            return _domains.FirstOrDefault(d => d.Id == id);
        }

        private static bool Available(MolcaRemediationDomain domain)
        {
            try { return domain.IsAvailable(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MolcaRemediationDomains] '{domain.Id}' availability gate threw: {ex.Message}");
                return false;
            }
        }

        private static void EnsureBuilt()
        {
            if (_domains != null) return;

            var found = new List<MolcaRemediationDomain>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaRemediationDomainProvider>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    var provider = (IMolcaRemediationDomainProvider)Activator.CreateInstance(type);
                    foreach (var domain in provider.GetDomains() ?? Enumerable.Empty<MolcaRemediationDomain>())
                    {
                        if (domain == null || string.IsNullOrWhiteSpace(domain.Id)) continue;
                        if (!seen.Add(domain.Id))
                        {
                            Debug.LogWarning(
                                $"[MolcaRemediationDomains] Duplicate domain id '{domain.Id}' from "
                                + $"'{type.FullName}'; skipped.");
                            continue;
                        }
                        found.Add(domain);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MolcaRemediationDomains] Provider '{type.FullName}' failed: {ex.Message}");
                }
            }

            _domains = found
                .OrderBy(d => d.Order)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
        }
    }
}
