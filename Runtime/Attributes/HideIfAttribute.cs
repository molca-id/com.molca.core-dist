using UnityEngine;

namespace Molca.Attributes
{
    /// <summary>
    /// Attribute to hide a property in the Inspector based on a boolean field's value
    /// </summary>
    public class HideIfAttribute : PropertyAttribute
    {
        public readonly string boolFieldName;

        public HideIfAttribute(string boolFieldName)
        {
            this.boolFieldName = boolFieldName;
        }
    }
} 