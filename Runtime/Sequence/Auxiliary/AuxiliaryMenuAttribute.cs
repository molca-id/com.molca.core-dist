using System;

namespace Molca.Sequence.Auxiliary
{
    /// <summary>
    /// Adds a StepAuxiliary class to a nested path in the "Add Auxiliary" menu.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class AuxiliaryMenuAttribute : Attribute
    {
        public string Path { get; }
        public bool AllowMultiple { get; }

        /// <summary>
        /// Defines the menu path for this auxiliary.
        /// </summary>
        /// <param name="path">The menu path, using "/" to separate levels (e.g., "Audio/Effects/Play Sound").</param>
        /// <param name="allowMultiple">Whether multiple instances of this auxiliary type can be added to the same step.</param>
        public AuxiliaryMenuAttribute(string path, bool allowMultiple = false)
        {
            Path = path;
            AllowMultiple = allowMultiple;
        }
    }
}