using System;
using Molca.Attributes;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Marks a subtree — normally a prefab root — as its own reference scope. Ids on providers
    /// beneath it need only be unique within the subtree, so the same prefab can be placed and
    /// instantiated any number of times without its internal wiring colliding.
    /// </summary>
    /// <remarks>
    /// This is the structural answer to the problem v1 papered over by regenerating ids. V1 required
    /// every id to be unique project-wide, so the editor gave each new prefab placement a fresh id —
    /// which broke the prefab's <i>internal</i> references, because the reference still pointed at
    /// the id the asset was authored with.
    ///
    /// Here the authored ids never change. The template id is inherited by every instance on
    /// purpose; what distinguishes two live copies is <see cref="ScopeInstanceId"/>, which is
    /// assigned per instance at runtime and never serialized.
    /// </remarks>
    [AddComponentMenu("Molca/Reference System/Reference Scope Root")]
    [DisallowMultipleComponent]
    public class ReferenceScopeRoot : MonoBehaviour
    {
        [SerializeField, ReadOnly]
        [Tooltip("Stable id of this scope template, shared by every instance of the prefab. Auto-generated if empty.")]
        private string scopeTemplateId;

        [SerializeField]
        [Tooltip("Optional label for this scope in the References workspace. The GameObject name is used if empty.")]
        private string displayNameOverride;

        private static int _instanceCounter;

        private ReferenceManager _openedWith;

        /// <summary>
        /// The id shared by every instance of this prefab. Stable across placement and duplication:
        /// it identifies the <i>template</i>, not the copy.
        /// </summary>
        public string ScopeTemplateId => scopeTemplateId ?? string.Empty;

        private string _scopeInstanceId;

        /// <summary>
        /// The id of this particular live instance. Never serialized: it is what distinguishes two
        /// copies of one prefab, and the scope component of every
        /// <see cref="ReferenceScopeKind.PrefabLocal"/> key beneath this root.
        /// </summary>
        /// <remarks>
        /// Assigned on first access rather than in a lifecycle callback, so a child that asks before
        /// this root's <c>Awake</c> has run still gets the right id. Once assigned it never changes:
        /// a scope that took a new identity when toggled off and on would invalidate every reference
        /// into it.
        /// </remarks>
        public string ScopeInstanceId
        {
            get
            {
                if (string.IsNullOrEmpty(_scopeInstanceId))
                {
                    string template = string.IsNullOrEmpty(scopeTemplateId) ? "ReferenceScope" : scopeTemplateId;
                    _scopeInstanceId = $"{template}#{++_instanceCounter}";
                }

                return _scopeInstanceId;
            }
        }

        /// <summary>Label for this scope in diagnostics and editor surfaces.</summary>
        public string DisplayName =>
            string.IsNullOrEmpty(displayNameOverride) ? gameObject.name : displayNameOverride;

        /// <summary>True while the registry is accepting prefab-local registrations for this scope.</summary>
        public bool IsOpen => _openedWith != null && _openedWith.IsScopeOpen(ScopeInstanceId);

        /// <summary>
        /// The nearest enclosing scope root, or null when <paramref name="target"/> is not inside one.
        /// </summary>
        /// <param name="target">The transform to search up from; the search includes itself.</param>
        /// <remarks>
        /// Nearest, not outermost: nested scope roots each delimit their own subtree, so a provider
        /// belongs to the innermost one that contains it.
        /// </remarks>
        public static ReferenceScopeRoot FindNearest(Transform target) =>
            target == null ? null : target.GetComponentInParent<ReferenceScopeRoot>(true);

        /// <summary>
        /// The nearest enclosing scope root of a component, or null when it is not inside one.
        /// </summary>
        /// <param name="component">The component to search up from.</param>
        public static ReferenceScopeRoot FindNearest(Component component) =>
            component == null ? null : FindNearest(component.transform);

        private void OnValidate()
        {
            // Generated once and then left alone — deliberately unlike ReferenceableComponent, which
            // re-generates an id inherited by a prefab instance. Re-generating here would give every
            // placement a different scope template and defeat the point of having one.
            if (string.IsNullOrEmpty(scopeTemplateId))
                scopeTemplateId = ReferenceGenerator.GenerateUniqueId("ReferenceScope");
        }

        private void Awake()
        {
            // Pin the id early so it is stamped before any child looks for it, even though the
            // getter would assign one on demand anyway.
            _ = ScopeInstanceId;
        }

        private async void OnEnable() // doctor:ignore async-void is intentional: Unity message entry point wrapped in try/catch
        {
            try
            {
                await RuntimeManager.WaitForInitialization(destroyCancellationToken);

                if (this == null || !isActiveAndEnabled)
                    return;

                EnsureOpen(RuntimeManager.GetSubsystem<ReferenceManager>());
            }
            catch (OperationCanceledException)
            {
                // Destroyed while waiting — exit quietly.
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceScopeRoot] OnEnable failed on '{name}': {e}", this);
            }
        }

        private void OnDisable() => Close();

        /// <summary>
        /// Closes this scope, dropping every registration made inside it.
        /// </summary>
        /// <returns>How many registrations were dropped.</returns>
        /// <remarks>
        /// Runs on <c>OnDisable</c>, and is public because a pool returning an instance needs the
        /// same thing. Children beneath a destroyed or recycled instance may never get their own
        /// <c>OnDisable</c>, and a leftover local entry would then block the next instance that
        /// legitimately reuses the id.
        /// </remarks>
        public int Close()
        {
            int dropped = _openedWith?.CloseScope(ScopeInstanceId) ?? 0;
            _openedWith = null;
            return dropped;
        }

        /// <summary>
        /// Opens this scope if it is not open already, so registrations naming it are accepted.
        /// </summary>
        /// <param name="manager">The registry to open the scope in.</param>
        /// <returns>True when the scope is open once this returns.</returns>
        /// <remarks>
        /// Idempotent, and called from both <c>OnEnable</c> and <see cref="RegisterLocal"/>. That
        /// removes the ordering dependency: a child whose <c>OnEnable</c> resumes before its root's
        /// opens the scope itself rather than being refused for a race it cannot control.
        /// </remarks>
        public bool EnsureOpen(ReferenceManager manager)
        {
            if (manager == null || string.IsNullOrEmpty(ScopeInstanceId))
                return false;

            if (_openedWith == manager && manager.IsScopeOpen(ScopeInstanceId))
                return true;

            manager.OpenScope(ScopeInstanceId);
            _openedWith = manager;
            return true;
        }

        /// <summary>
        /// The prefab-local key for an id authored inside this scope.
        /// </summary>
        /// <param name="refType">The provider's type category.</param>
        /// <param name="refId">The provider's local id within the prefab.</param>
        public ReferenceRuntimeKey KeyFor(string refType, string refId) =>
            ReferenceRuntimeKey.PrefabLocal(ScopeInstanceId, refType, refId);

        /// <summary>
        /// Register a provider into this scope, opening the scope first if necessary.
        /// </summary>
        /// <param name="manager">The registry to register with.</param>
        /// <param name="provider">The provider to register.</param>
        /// <param name="handle">The handle owning the registration, or null when refused.</param>
        /// <returns>The registration outcome.</returns>
        public ReferenceRegistrationResult RegisterLocal(
            ReferenceManager manager,
            IReferenceable provider,
            out ReferenceRegistrationHandle handle)
        {
            handle = null;

            if (manager == null || provider == null)
                return new ReferenceRegistrationResult(ReferenceRegistrationOutcome.InvalidProvider, default);

            EnsureOpen(manager);
            return manager.Register(provider, KeyFor(provider.RefType, provider.RefId), out handle);
        }

        /// <summary>Resolve a local id within this scope.</summary>
        /// <param name="manager">The registry to look in.</param>
        /// <param name="refType">The target's type category.</param>
        /// <param name="refId">The target's local id.</param>
        /// <param name="provider">The found provider, or null.</param>
        /// <returns>True when a live provider holds that local id in this scope.</returns>
        public bool TryResolveLocal(
            ReferenceManager manager, string refType, string refId, out IReferenceable provider)
        {
            provider = null;
            return manager != null && manager.TryGet(KeyFor(refType, refId), out provider);
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"ReferenceScopeRoot '{DisplayName}' ({ScopeInstanceId ?? ScopeTemplateId})";
    }
}
