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

        public string RefId
        {
            get => refId;
            set => refId = value;
        }

        public string RefType => string.IsNullOrEmpty(refType) ? "Referenceable" : refType;

        public string DisplayName => string.IsNullOrEmpty(displayNameOverride) ? gameObject.name : displayNameOverride;

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(refId))
            {
                refId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#if UNITY_EDITOR
            // A prefab instance inherits the asset's id; give each placement a fresh one.
            else if (ReferenceGenerator.IsInheritedPrefabId(this, refId))
            {
                refId = ReferenceGenerator.GenerateUniqueId(RefType);
            }
#endif
        }

        private async void OnEnable()
        {
            await RuntimeManager.WaitForInitialization();

            // Destroyed or disabled while waiting — OnDisable has already run (or
            // will never run for this activation), so registering now would leave
            // a dead entry in the ReferenceManager.
            if (this == null || !isActiveAndEnabled) return;

            var manager = ReferenceManager.Instance;
            if (manager != null && !string.IsNullOrEmpty(refId) && !string.IsNullOrEmpty(RefType))
            {
                manager.Register(this);
            }
        }

        private void OnDisable()
        {
            var manager = ReferenceManager.Instance;
            if (manager != null)
            {
                manager.Unregister(this);
            }
        }
    }
}
