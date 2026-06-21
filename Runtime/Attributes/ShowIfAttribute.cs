using UnityEngine;

namespace Molca.Attributes
{
    /// <summary>
    /// Attribute to show a property in the Inspector based on a boolean field's value
    /// </summary>
    public class ShowIfAttribute : PropertyAttribute
    {
        public readonly string boolFieldName;

        public ShowIfAttribute(string boolFieldName)
        {
            this.boolFieldName = boolFieldName;
        }
    }
} 