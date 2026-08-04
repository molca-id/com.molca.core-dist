using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Molca.Networking.Configuration;
using UnityEditor.Compilation;

namespace Molca.Editor.Networking.Hub
{
    /// <summary>
    /// Where the workspace's strict dropdowns get their options: the project's own types, the machine's
    /// environment, and the values the catalog already uses.
    /// </summary>
    /// <remarks>
    /// <para>These fields used to be free text, and free text is how a catalog acquires three spellings of
    /// one region and a response type that names a class nobody ever wrote. Detection replaces typing with
    /// choosing, so the only way to introduce a new value is the deliberate <i>create</i> action on
    /// <see cref="NetworkHubFields.EditChoice"/> — which is a decision the author can see themselves make.</para>
    ///
    /// <para>Two different kinds of source appear here and they fail differently, which is why each one is
    /// documented on its own. Types and environment variables are <b>discovered</b> — the answer depends on
    /// what is compiled and what this machine exports, so it legitimately differs between two developers.
    /// Catalog-derived options are <b>observed</b> — they are just the distinct values already authored, so
    /// they converge on whatever the project already agreed to call things.</para>
    /// </remarks>
    internal static class NetworkHubChoices
    {
        /// <summary>Header names a credential is conventionally written to.</summary>
        /// <remarks>
        /// Offered in addition to whatever the catalog already uses, so the first credential profile in an
        /// empty catalog still has something to choose. These are the four an HTTP client actually reads;
        /// anything else is a deliberate creation.
        /// </remarks>
        private static readonly string[] ConventionalHeaders =
        {
            "Authorization",
            "Proxy-Authorization",
            "X-Api-Key",
            "X-Auth-Token",
        };

        /// <summary>The Socket.IO handshake path clients default to.</summary>
        private const string DefaultSocketIoPath = "/socket.io/";

        /// <summary>Cached response-model candidates; cleared by the domain reload that would invalidate it.</summary>
        private static List<string> _responseTypes;

        /// <summary>
        /// Type names that could name a response model, as <c>Namespace.Type</c>.
        /// </summary>
        /// <remarks>
        /// Scoped to the player assemblies, without test assemblies. That is the honest boundary: a response
        /// model is deserialized at runtime, so a type that only exists in an editor or test assembly could
        /// never be one, and offering it would be offering a value that cannot work. Editor-only assemblies
        /// are also where most of the noise lives, so the scope keeps the list navigable as a side effect
        /// rather than as the goal.
        /// <para>
        /// Interfaces, abstracts, generics, enums and delegates are excluded for the same reason: none of
        /// them can be the concrete type a deserializer instantiates.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> ResponseTypes()
        {
            if (_responseTypes != null)
                return _responseTypes;

            var playerAssemblies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(
                         AssembliesType.PlayerWithoutTestAssemblies))
            {
                playerAssemblies.Add(assembly.name);
            }

            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!playerAssemblies.Contains(assembly.GetName().Name))
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // A partially loadable assembly still contributes the types that did load. Dropping the
                    // whole assembly would silently shorten the list with no way for the author to tell.
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    if (IsCandidateResponseType(type))
                        names.Add(type.FullName);
                }
            }

            _responseTypes = names.ToList();
            return _responseTypes;
        }

        private static bool IsCandidateResponseType(Type type) =>
            type != null
            && (type.IsPublic || type.IsNestedPublic)
            && !type.IsAbstract
            && !type.IsInterface
            && !type.IsEnum
            && !type.IsGenericTypeDefinition
            && !typeof(Delegate).IsAssignableFrom(type)
            // Closures, iterator state machines and anonymous types are public-ish compiler artifacts that
            // would otherwise outnumber the real models.
            && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            && !string.IsNullOrEmpty(type.FullName)
            && !type.FullName.Contains('<');

        /// <summary>
        /// Environment variable names visible to the editor process.
        /// </summary>
        /// <remarks>
        /// Machine-scoped by nature: a key that exists on a build agent will not appear here, and a key that
        /// exists here may not exist there. That is a reason to offer <i>create</i> alongside the list, not a
        /// reason to leave the field free text — an author who has the variable set locally gets the exact
        /// spelling, and one who does not has to state deliberately that they are naming something absent.
        /// </remarks>
        internal static IReadOnlyList<string> EnvironmentVariableNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = entry.Key as string;
                if (!string.IsNullOrWhiteSpace(key)) names.Add(key);
            }

            return names.ToList();
        }

        /// <summary>Header names already used by the catalog, plus the conventional ones.</summary>
        /// <param name="catalog">The catalog to read. Null contributes nothing.</param>
        internal static IReadOnlyList<string> HeaderNames(NetworkCatalog catalog)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string header in ConventionalHeaders) names.Add(header);

            if (catalog?.CredentialProfiles != null)
            {
                foreach (var profile in catalog.CredentialProfiles)
                {
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.HeaderName))
                        names.Add(profile.HeaderName.Trim());
                }
            }

            return names.ToList();
        }

        /// <summary>Region labels already used by the catalog's bindings.</summary>
        /// <param name="catalog">The catalog to read. Null contributes nothing.</param>
        /// <remarks>
        /// Purely observed — a region label affects nothing at runtime, so there is no authority to detect
        /// it from. Offering what is already in use is the whole value: it is what stops one project
        /// carrying <c>ap-southeast-1</c>, <c>AP-Southeast-1</c> and <c>Singapore</c> for one region.
        /// </remarks>
        internal static IReadOnlyList<string> RegionLabels(NetworkCatalog catalog) =>
            DistinctBindingValues(catalog, binding => binding.RegionLabel);

        /// <summary>Socket.IO handshake paths already used by the catalog, plus the client default.</summary>
        /// <param name="catalog">The catalog to read. Null contributes only the default.</param>
        internal static IReadOnlyList<string> SocketIoPaths(NetworkCatalog catalog)
        {
            var paths = new SortedSet<string>(
                DistinctBindingValues(catalog, binding => binding.AuthoredSocketIoPath),
                StringComparer.Ordinal)
            {
                DefaultSocketIoPath,
            };

            return paths.ToList();
        }

        private static List<string> DistinctBindingValues(
            NetworkCatalog catalog, Func<NetworkServiceBinding, string> read)
        {
            var values = new SortedSet<string>(StringComparer.Ordinal);
            if (catalog?.Services == null)
                return values.ToList();

            foreach (var service in catalog.Services)
            {
                if (service?.Bindings == null) continue;

                foreach (var binding in service.Bindings)
                {
                    if (binding == null) continue;

                    string value = read(binding);
                    if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
                }
            }

            return values.ToList();
        }
    }
}
