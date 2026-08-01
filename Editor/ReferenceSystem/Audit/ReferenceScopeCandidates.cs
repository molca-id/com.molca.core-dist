using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// Decides which providers a reference field may legally be pointed at, given the scope it is
    /// authored in.
    /// </summary>
    /// <remarks>
    /// <para>The picker's job used to be "offer everything and hope"; with scopes it has to offer only
    /// what can actually resolve. A prefab-local reference pointed at an object in a scene will always
    /// fail at runtime, and letting an author choose it means the mistake is only discovered later, by
    /// someone else.</para>
    ///
    /// <para>Pure and snapshot-driven, so the rule is testable without opening a prefab, and so the
    /// picker and the audit agree about what is legal by construction rather than by coincidence.</para>
    /// </remarks>
    public static class ReferenceScopeCandidates
    {
        /// <summary>
        /// Filters <paramref name="providers"/> down to those a reference in the given scope can reach.
        /// </summary>
        /// <param name="providers">The candidate providers.</param>
        /// <param name="scopeKind">The scope the reference field is authored in.</param>
        /// <param name="ownerAssetPath">Project-relative path of the asset holding the reference.</param>
        /// <returns>The legal candidates, in input order.</returns>
        public static IReadOnlyList<ReferenceProviderRecord> For(
            IEnumerable<ReferenceProviderRecord> providers,
            ReferenceScopeKind scopeKind,
            string ownerAssetPath)
        {
            var all = (providers ?? Array.Empty<ReferenceProviderRecord>()).Where(p => p != null).ToList();
            var predicate = Predicate(scopeKind, ownerAssetPath);
            return all.Where(predicate).ToList();
        }

        /// <summary>
        /// The legality test for one scope, for callers that filter their own collection.
        /// </summary>
        /// <param name="scopeKind">The scope the reference field is authored in.</param>
        /// <param name="ownerAssetPath">Project-relative path of the asset holding the reference.</param>
        public static Func<ReferenceProviderRecord, bool> Predicate(
            ReferenceScopeKind scopeKind, string ownerAssetPath)
        {
            switch (scopeKind)
            {
                case ReferenceScopeKind.PrefabLocal:
                    // Inside the prefab only. A prefab-local key is resolved relative to the live scope
                    // root, so nothing outside the asset can ever satisfy it.
                    return p => p.Kind == ReferenceProviderKind.PrefabComponent && SamePath(p, ownerAssetPath);

                case ReferenceScopeKind.Scene:
                    // Within the same scene. Cross-scene targets are legal in principle but need an
                    // explicit availability decision, so they are not offered as if they were routine.
                    return p => p.Kind == ReferenceProviderKind.SceneComponent && SamePath(p, ownerAssetPath);

                case ReferenceScopeKind.Global:
                case ReferenceScopeKind.LegacyGlobal:
                default:
                    // Anything the runtime can register. Prefab-asset and ScriptableObject providers are
                    // excluded because they are never registered, so offering one guarantees a reference
                    // that resolves in the Inspector and fails at runtime.
                    return p => p.IsRuntimeResolvable;
            }
        }

        /// <summary>
        /// Why a scope offers the candidates it does, for the picker's empty state.
        /// </summary>
        /// <param name="scopeKind">The scope the reference field is authored in.</param>
        /// <param name="ownerAssetPath">Project-relative path of the asset holding the reference.</param>
        public static string Describe(ReferenceScopeKind scopeKind, string ownerAssetPath)
        {
            string owner = string.IsNullOrEmpty(ownerAssetPath) ? "this asset" : Short(ownerAssetPath);

            return scopeKind switch
            {
                ReferenceScopeKind.PrefabLocal =>
                    $"Prefab-local: only targets inside '{owner}' can resolve, because the reference is "
                    + "looked up relative to the live prefab instance.",
                ReferenceScopeKind.Scene =>
                    $"Scene-scoped: only targets in '{owner}' are offered. Pointing outside the scene "
                    + "needs an explicit availability decision.",
                ReferenceScopeKind.Global =>
                    "Global: any runtime-registered provider, which must be unique across every loaded scene.",
                _ =>
                    "Legacy global: any runtime-registered provider. Migrate to an explicit scope to get "
                    + "uniqueness checked properly.",
            };
        }

        private static bool SamePath(ReferenceProviderRecord provider, string ownerAssetPath) =>
            !string.IsNullOrEmpty(ownerAssetPath) &&
            string.Equals(provider.Locator.AssetPath, ownerAssetPath, StringComparison.Ordinal);

        private static string Short(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }
    }
}
