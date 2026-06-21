using System;

namespace Molca
{
    /// <summary>
    /// Marks a field, property, or constructor for dependency injection by RuntimeManager.
    /// Dependencies will be automatically resolved and injected after RuntimeManager initialization.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Constructor, AllowMultiple = false)]
    public class InjectAttribute : Attribute
    {
        /// <summary>
        /// If true, the injection will fail with an error if the dependency cannot be resolved.
        /// If false, the field/property will remain null if the dependency is not found.
        /// </summary>
        public bool Required { get; set; } = true;
        
        /// <summary>
        /// If true, will inject even if the field/property already has a non-null value.
        /// </summary>
        public bool ForceInject { get; set; } = false;
        
        public InjectAttribute() { }
        
        public InjectAttribute(bool required)
        {
            Required = required;
        }
    }
}
