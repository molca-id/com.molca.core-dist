using UnityEngine;

namespace Molca.Attributes
{
    /// <summary>
    /// Attribute to make a property read-only in the Inspector while still allowing serialization
    /// </summary>
    public class ReadOnlyAttribute : PropertyAttribute
    {
        public ReadOnlyAttribute() { }
    }
} 