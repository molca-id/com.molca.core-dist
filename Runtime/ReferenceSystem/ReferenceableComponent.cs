using System;
using Molca.Attributes;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// A general-purpose MonoBehaviour that can be added to any GameObject to make it
    /// referenceable through the Reference System. Use this when you need to reference
    /// GameObjects by ID (e.g. spawn points, checkpoints, triggers) without creating a
    /// dedicated component type.
    /// </summary>
    [AddComponentMenu("Molca/Reference System/Referenceable")]
    public class ReferenceableComponent : MonoBehaviour, IReferenceable
    {
        [SerializeField, ReadOnly]
        [Tooltip("Unique ID for this referenceable. Auto-generated if empty.")]
        private string refId;

        [SerializeField]
        [Tooltip("Type/category for grouping (e.g. Referenceable, SpawnPoint, Checkpoint). Used by ReferenceManager for lookups.")]
        private string refType = "Referenceable";

        [SerializeField]
        [Tooltip("Optional display name. If empty, the GameObject name is used.")]
        private string displayNameOverride;

        [SerializeField]
        [Tooltip("Which space this id must be unique in. Leave as Legacy Global unless this object lives inside a Reference Scope Root.")]
        private ReferenceScopeKind scopeMode = ReferenceScopeKind.LegacyGlobal;

        public string RefId
        {
            get => refId;
            set => refId = value;
        }

        public string RefType => string.IsNullOrEmpty(refType) ? "Referenceable" : refType;

        public string DisplayName => string.IsNullOrEmpty(displayNameOverride) ? gameObject.name : displayNameOverride;

        /// <summary>The space this component's id is required to be unique in.</summary>
        public ReferenceScopeKind ScopeMode => scopeMode;

        /// <summary>
        /// True when this component's id belongs to the prefab instance around it rather than to the
        /// project, so it must survive duplication unchanged.
        /// </summary>
        private bool IsScopeLocal =>
            scopeMode == ReferenceScopeKind.PrefabLocal && ReferenceScopeRoot.FindNearest(this) != null;

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(refId))
            {
                refId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#if UNITY_EDITOR
            // A prefab instance inherits the asset's id. Without a scope that is a project-wide
            // collision, so each placement gets a fresh one — which is exactly what broke the
            // prefab's internal wiring, since the references inside it still name the authored id.
            //
            // Inside a scope root the inheritance is the point: two instances may hold the same
            // local id because their scope instance ids differ, so the id is left alone and the
            // internal references keep working.
            else if (!IsScopeLocal && ReferenceGenerator.IsInheritedPrefabId(this, refId))
            {
                refId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#endif
        }

        /// <summary>
        /// The scoped key this component registers under, and the scope root backing it.
        /// </summary>
        /// <remarks>
        /// Falls back to <see cref="ReferenceScopeKind.LegacyGlobal"/> when a prefab-local component
        /// has no enclosing scope root: registering it as prefab-local would be refused outright,
        /// and silently dropping the registration is worse than behaving the way it did before the
        /// scope was configured. The editor audit reports the missing root.
        /// </remarks>
        private ReferenceRuntimeKey BuildKey(out ReferenceScopeRoot root)
        {
            root = null;

            switch (scopeMode)
            {
                case ReferenceScopeKind.Global:
                    return ReferenceRuntimeKey.Global(RefType, refId);

                case ReferenceScopeKind.Scene:
                    string scenePath = gameObject.scene.path;
                    return string.IsNullOrEmpty(scenePath)
                        ? ReferenceRuntimeKey.Legacy(RefType, refId)
                        : ReferenceRuntimeKey.Scene(scenePath, RefType, refId);

                case ReferenceScopeKind.PrefabLocal:
                    root = ReferenceScopeRoot.FindNearest(this);
                    return root == null
                        ? ReferenceRuntimeKey.Legacy(RefType, refId)
                        : root.KeyFor(RefType, refId);

                default:
                    return ReferenceRuntimeKey.Legacy(RefType, refId);
            }
        }

        /// <summary>
        /// The registration this component owns, so <c>OnDisable</c> releases exactly what
        /// <c>OnEnable</c> took — and nothing else, even if the id changed in between.
        /// </summary>
        private ReferenceRegistrationHandle _registration;

        private async void OnEnable() // doctor:ignore async-void is intentional: Unity message entry point wrapped in try/catch
        {
            // async-void entry point: try/catch shim per the async contract, so a bootstrap failure
            // surfaces here instead of as an unobserved exception in Unity's sync context.
            try
            {
                // The token means a component destroyed mid-bootstrap stops waiting rather than
                // resuming into a fake-null state.
                await RuntimeManager.WaitForInitialization(destroyCancellationToken);

                // Destroyed or disabled while waiting — OnDisable has already run (or
                // will never run for this activation), so registering now would leave
                // a dead entry in the ReferenceManager.
                if (this == null || !isActiveAndEnabled) return;

                var manager = RuntimeManager.GetSubsystem<ReferenceManager>();
                if (manager == null || string.IsNullOrEmpty(refId) || string.IsNullOrEmpty(RefType))
                    return;

                var key = BuildKey(out var root);

                // Open the scope before registering. A child whose OnEnable resumes ahead of its
                // root's would otherwise be refused for an ordering race it cannot control.
                root?.EnsureOpen(manager);

                var result = manager.Register(this, key, out _registration);
                if (!result.IsRegistered)
                {
                    Debug.LogError(
                        $"[ReferenceableComponent] '{name}' could not register: {result.Describe()}.", this);
                }
            }
            catch (OperationCanceledException)
            {
                // Destroyed while waiting — exit quietly.
            }
            catch (Exception e)
            {
                Debug.LogError($"[ReferenceableComponent] OnEnable failed on '{name}': {e}", this);
            }
        }

        private void OnDisable()
        {
            // Guarded so a component disabled before its registration completed does not produce a
            // misleading "object not registered" warning from the manager. Releasing through the
            // handle rather than by object means a component whose RefId changed while registered
            // still drops the entry it actually holds.
            _registration?.Dispose();
            _registration = null;
        }
    }
}
