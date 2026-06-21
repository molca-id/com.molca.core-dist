using UnityEngine;

namespace Molca.Attributes
{
    /// <summary>
    /// Marks a serialized string field as the backing store for <see cref="Molca.ReferenceSystem.IReferenceable.RefId"/>.
    /// Enables the Inspector to render a refresh button and context-menu actions for the field.
    /// The reference type is resolved at edit-time from the host's <see cref="Molca.ReferenceSystem.IReferenceable.RefType"/>.
    /// </summary>
    /// <remarks>
    /// After regenerating, a dialog offers to redirect any <see cref="Molca.ReferenceSystem.SceneObjectReference"/>
    /// fields in loaded scenes that pointed to the old ID. Unloaded scenes must be updated manually via a project scan.
    /// </remarks>
    public class RefIdAttribute : PropertyAttribute { }
}
