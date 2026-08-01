using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// A cheap, cached index of the reference providers in the currently loaded scenes, for surfaces that
    /// must answer "what does this id point at?" many times per second — chiefly the Inspector drawer.
    /// </summary>
    /// <remarks>
    /// This is a narrower <i>scope</i>, not a second set of rules: providers are described by
    /// <see cref="ReferenceSerializedScanner"/> and matched by <see cref="ReferenceResolutionAnalyzer"/>,
    /// exactly as the full audit does. That matters because the drawer previously matched on Ref Id alone
    /// and took <c>FirstOrDefault</c>, so it could display and select a different object than the runtime
    /// would resolve.
    ///
    /// The index is rebuilt when the hierarchy changes rather than on every repaint. The old drawer called
    /// <c>FindObjectsByType&lt;MonoBehaviour&gt;</c> inside <c>OnGUI</c>, once per drawn field per frame.
    /// </remarks>
    public static class ReferenceProviderLookup
    {
        private static ReferenceResolutionAnalyzer.ProviderIndex _index;
        private static List<ReferenceProviderRecord> _providers = new();
        private static bool _isStale = true;

        /// <summary>Every provider in the loaded scenes, rebuilt on demand.</summary>
        public static IReadOnlyList<ReferenceProviderRecord> Providers
        {
            get
            {
                EnsureBuilt();
                return _providers;
            }
        }

        /// <summary>The matching index, rebuilt on demand.</summary>
        public static ReferenceResolutionAnalyzer.ProviderIndex Index
        {
            get
            {
                EnsureBuilt();
                return _index;
            }
        }

        /// <summary>
        /// Rebuilds the index immediately and returns it.
        /// </summary>
        /// <returns>A freshly built index.</returns>
        /// <remarks>
        /// Use this from a validator, an MCP tool, or anything else whose answer must reflect authoring
        /// changes made in the same frame. The cached <see cref="Index"/> is invalidated by
        /// <c>hierarchyChanged</c>, which the editor raises on a later tick — fine for a drawer that
        /// repaints continuously, wrong for a one-shot programmatic check.
        /// </remarks>
        public static ReferenceResolutionAnalyzer.ProviderIndex Rebuild()
        {
            Invalidate();
            return Index;
        }

        /// <summary>
        /// Resolves a stored reference exactly as the runtime would, against the cached index.
        /// </summary>
        /// <param name="storedRefId">The serialized Ref Id.</param>
        /// <param name="storedRefType">The serialized Ref Type.</param>
        /// <param name="expectedType">
        /// The type the field promises, or null for the untyped <see cref="SceneObjectReference"/>.
        /// </param>
        /// <returns>The outcome and every candidate that produced it.</returns>
        public static ReferenceSiteResolution Resolve(
            string storedRefId, string storedRefType, Type expectedType = null) =>
            Resolve(Index, storedRefId, storedRefType, expectedType);

        /// <summary>
        /// Resolves a stored reference against an explicit index.
        /// </summary>
        /// <param name="index">The index to resolve against, typically from <see cref="Rebuild"/>.</param>
        /// <param name="storedRefId">The serialized Ref Id.</param>
        /// <param name="storedRefType">The serialized Ref Type.</param>
        /// <param name="expectedType">The type the field promises, or null when it promises nothing.</param>
        /// <returns>The outcome and every candidate that produced it.</returns>
        public static ReferenceSiteResolution Resolve(
            ReferenceResolutionAnalyzer.ProviderIndex index,
            string storedRefId,
            string storedRefType,
            Type expectedType = null)
        {
            // A synthetic site: the caller has the stored values but no serialized-scan locator, and the
            // analyzer only needs the stored identity and the expected type to decide.
            var site = new ReferenceSiteRecord(
                default, string.Empty, storedRefId, storedRefType, expectedType,
                ReferenceSiteSourceKind.Scene, isReadOnly: false);

            return ReferenceResolutionAnalyzer.Resolve(index, site);
        }

        /// <summary>
        /// Providers a picker may offer, optionally constrained to a type.
        /// </summary>
        /// <param name="expectedType">
        /// When non-null, only providers assignable to it are returned — so a typed field cannot be
        /// pointed at an object that will fail the cast at runtime.
        /// </param>
        /// <returns>Providers with an assigned Ref Id, grouped-friendly and stably ordered.</returns>
        public static IReadOnlyList<ReferenceProviderRecord> SelectableProviders(Type expectedType = null)
        {
            return Providers
                .Where(p => !string.IsNullOrEmpty(p.RefId))
                .Where(p => expectedType == null
                         || p.RuntimeType == null
                         || expectedType.IsAssignableFrom(p.RuntimeType))
                .OrderBy(p => p.RefType, StringComparer.Ordinal)
                .ThenBy(p => p.DisplayName, StringComparer.Ordinal)
                .ThenBy(p => p.RefId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The live object behind a provider record, or null when it is gone.</summary>
        /// <param name="provider">The provider to resolve. Null returns null.</param>
        public static UnityEngine.Object ResolveObject(ReferenceProviderRecord provider) =>
            provider?.Locator.TryResolve();

        /// <summary>Marks the index stale so the next access rebuilds it.</summary>
        public static void Invalidate() => _isStale = true;

        private static void EnsureBuilt()
        {
            if (!_isStale && _index != null)
                return;

            var providers = new List<ReferenceProviderRecord>();

            // Inactive objects are included: a disabled provider is still authored data the Inspector
            // must be able to show and pick, even though it is not registered at runtime while disabled.
            foreach (var behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (behaviour == null || behaviour is not IReferenceable)
                    continue;

                var scenePath = behaviour.gameObject.scene.path;
                var provider = ReferenceSerializedScanner.TryDescribeProvider(
                    behaviour, ReferenceProviderKind.SceneComponent, scenePath);
                if (provider != null)
                    providers.Add(provider);
            }

            _providers = providers;
            _index = ReferenceResolutionAnalyzer.BuildIndex(providers);
            _isStale = false;
        }

        [InitializeOnLoadMethod]
        private static void InstallInvalidationHooks()
        {
            EditorApplication.hierarchyChanged += Invalidate;
            EditorApplication.playModeStateChanged += _ => Invalidate();
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (_, _) => Invalidate();
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += _ => Invalidate();
        }
    }
}
