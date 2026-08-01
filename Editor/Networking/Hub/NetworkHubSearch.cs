using System;
using System.Collections.Generic;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>One structured search hit, and where selecting it navigates.</summary>
    internal sealed class NetworkHubSearchMatch
    {
        /// <summary>Primary text, usually the entity's ID.</summary>
        public string Title { get; }

        /// <summary>Secondary text explaining what matched — a display name, a path, an origin.</summary>
        public string Subtitle { get; }

        /// <summary>Which kind of entity this is, shown as a trailing badge.</summary>
        public string Kind { get; }

        /// <summary>Where selecting this hit navigates.</summary>
        public NetworkHubNavigationTarget Target { get; }

        /// <summary>How well this matched; lower sorts first.</summary>
        public int Rank { get; }

        /// <summary>Creates a match.</summary>
        /// <param name="title">Primary text.</param>
        /// <param name="subtitle">Secondary text.</param>
        /// <param name="kind">Entity kind label.</param>
        /// <param name="target">Navigation target.</param>
        /// <param name="rank">Match quality; lower sorts first.</param>
        public NetworkHubSearchMatch(
            string title, string subtitle, string kind, NetworkHubNavigationTarget target, int rank)
        {
            Title = title ?? string.Empty;
            Subtitle = subtitle ?? string.Empty;
            Kind = kind ?? string.Empty;
            Target = target;
            Rank = rank;
        }
    }

    /// <summary>
    /// Structured search across a catalog: environments, services, endpoints, policies, and credentials.
    /// </summary>
    /// <remarks>
    /// Deliberately not a text filter over whatever the current view is showing. A search for a service ID
    /// should reach that service's detail from anywhere in the workspace, including from a view that does
    /// not list services at all.
    /// <para>
    /// Pure and Unity-free, so it is directly testable without building a view.
    /// </para>
    /// </remarks>
    internal static class NetworkHubSearch
    {
        /// <summary>Most matches returned. Beyond this the list stops being scannable.</summary>
        internal const int MaxResults = 40;

        // Rank bands. An exact ID match is what a user typing a full ID means; a prefix match is what they
        // mean while still typing; a substring match anywhere else is a guess.
        private const int RankExactId = 0;
        private const int RankPrefixId = 1;
        private const int RankOtherField = 2;

        /// <summary>
        /// Finds catalog entities matching a query.
        /// </summary>
        /// <param name="catalog">The catalog to search; <c>null</c> yields no results.</param>
        /// <param name="query">The query; empty yields no results.</param>
        /// <returns>Matches ordered by rank then title. Never <c>null</c>.</returns>
        internal static List<NetworkHubSearchMatch> Find(NetworkCatalog catalog, string query)
        {
            var results = new List<NetworkHubSearchMatch>();
            if (catalog == null || string.IsNullOrWhiteSpace(query))
                return results;

            string needle = query.Trim();

            AddEnvironments(catalog, needle, results);
            AddServices(catalog, needle, results);
            AddEndpoints(catalog, needle, results);
            AddPolicies(catalog, needle, results);
            AddCredentials(catalog, needle, results);

            results.Sort((left, right) =>
            {
                int byRank = left.Rank.CompareTo(right.Rank);
                return byRank != 0
                    ? byRank
                    : string.Compare(left.Title, right.Title, StringComparison.Ordinal);
            });

            if (results.Count > MaxResults)
                results.RemoveRange(MaxResults, results.Count - MaxResults);

            return results;
        }

        private static void AddEnvironments(
            NetworkCatalog catalog, string needle, List<NetworkHubSearchMatch> results)
        {
            foreach (var environment in catalog.Environments)
            {
                if (environment == null) continue;

                if (!TryRank(needle, environment.Id, out int rank, environment.DisplayName))
                    continue;

                results.Add(new NetworkHubSearchMatch(
                    environment.Id,
                    $"{environment.DisplayName} · {environment.Classification}",
                    "Environment",
                    NetworkHubNavigationTarget.Environment(environment.Id),
                    rank));
            }
        }

        private static void AddServices(
            NetworkCatalog catalog, string needle, List<NetworkHubSearchMatch> results)
        {
            foreach (var service in catalog.Services)
            {
                if (service == null) continue;

                // A host is how people remember a service they did not name, so bound origins match too.
                string origins = DescribeOrigins(service);

                if (!TryRank(needle, service.Id, out int rank, service.DisplayName, origins))
                    continue;

                results.Add(new NetworkHubSearchMatch(
                    service.Id,
                    string.IsNullOrEmpty(origins) ? service.DisplayName : origins,
                    "Service",
                    NetworkHubNavigationTarget.Service(service.Id),
                    rank));
            }
        }

        private static void AddEndpoints(
            NetworkCatalog catalog, string needle, List<NetworkHubSearchMatch> results)
        {
            foreach (var collection in catalog.EndpointCollections)
            {
                if (collection?.Endpoints == null) continue;

                foreach (var endpoint in collection.Endpoints)
                {
                    if (endpoint == null) continue;

                    if (!TryRank(needle, endpoint.Id, out int rank, endpoint.DisplayName, endpoint.RelativePath))
                        continue;

                    results.Add(new NetworkHubSearchMatch(
                        endpoint.Id,
                        $"{endpoint.Method} {endpoint.RelativePath}",
                        "Endpoint",
                        NetworkHubNavigationTarget.Endpoint(endpoint.Id),
                        rank));
                }
            }
        }

        private static void AddPolicies(
            NetworkCatalog catalog, string needle, List<NetworkHubSearchMatch> results)
        {
            foreach (var profile in catalog.PolicyProfiles)
            {
                if (profile == null) continue;

                if (!TryRank(needle, profile.Id, out int rank, profile.DisplayName))
                    continue;

                results.Add(new NetworkHubSearchMatch(
                    profile.Id,
                    profile.DisplayName,
                    "Policy",
                    new NetworkHubNavigationTarget(NetworkHubViews.Policies, profile.Id),
                    rank));
            }
        }

        private static void AddCredentials(
            NetworkCatalog catalog, string needle, List<NetworkHubSearchMatch> results)
        {
            foreach (var profile in catalog.CredentialProfiles)
            {
                if (profile == null) continue;

                if (!TryRank(needle, profile.Id, out int rank, profile.DisplayName))
                    continue;

                results.Add(new NetworkHubSearchMatch(
                    profile.Id,
                    // Never the value, and there is none to show: a profile holds provider metadata only.
                    $"{profile.DisplayName} · {profile.ProviderKind}",
                    "Credential",
                    new NetworkHubNavigationTarget(NetworkHubViews.Credentials, profile.Id),
                    rank));
            }
        }

        private static string DescribeOrigins(NetworkServiceDefinition service)
        {
            if (service.Bindings == null) return string.Empty;

            var seen = new List<string>();
            foreach (var binding in service.Bindings)
            {
                if (binding == null || string.IsNullOrEmpty(binding.HttpOrigin)) continue;
                if (!seen.Contains(binding.HttpOrigin)) seen.Add(binding.HttpOrigin);
            }
            return string.Join(", ", seen);
        }

        /// <summary>
        /// Ranks a query against an entity's ID and its other searchable text.
        /// </summary>
        /// <param name="needle">The query.</param>
        /// <param name="id">The entity's stable ID, which carries the strongest ranks.</param>
        /// <param name="rank">The match band on success; lower sorts first.</param>
        /// <param name="otherFields">Display name, path, origin — anything else worth matching.</param>
        /// <returns><c>false</c> when nothing matched.</returns>
        private static bool TryRank(string needle, string id, out int rank, params string[] otherFields)
        {
            rank = RankOtherField;

            if (!string.IsNullOrEmpty(id))
            {
                if (string.Equals(id, needle, StringComparison.OrdinalIgnoreCase))
                {
                    rank = RankExactId;
                    return true;
                }

                if (id.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    rank = RankPrefixId;
                    return true;
                }

                if (id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            if (otherFields != null)
            {
                foreach (string field in otherFields)
                {
                    if (!string.IsNullOrEmpty(field) &&
                        field.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
