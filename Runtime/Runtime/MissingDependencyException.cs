using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca
{
    /// <summary>
    /// Thrown by <see cref="RuntimeManager.InjectDependencies"/> when one or more
    /// fields or properties marked <see cref="InjectAttribute"/> with
    /// <see cref="InjectAttribute.Required"/> = true could not be resolved.
    /// </summary>
    /// <remarks>
    /// Failing fast at injection time produces a stack trace pointing at the
    /// injection call site, rather than a downstream <see cref="NullReferenceException"/>
    /// at the eventual point of first use, which is much harder to trace.
    /// Optional dependencies (<c>[Inject(false)]</c>) do not trigger this exception.
    /// </remarks>
    public class MissingDependencyException : Exception
    {
        /// <summary>The type whose dependencies could not be fully resolved.</summary>
        public Type TargetType { get; }

        /// <summary>Names of the unresolved required members on <see cref="TargetType"/>.</summary>
        public IReadOnlyList<string> MissingMembers { get; }

        /// <summary>
        /// Creates a new exception describing one or more unresolved required dependencies.
        /// </summary>
        /// <param name="targetType">The type whose dependencies could not be fully resolved.</param>
        /// <param name="missingMembers">Names of the unresolved required members.</param>
        public MissingDependencyException(Type targetType, IEnumerable<string> missingMembers)
            : base(BuildMessage(targetType, missingMembers))
        {
            TargetType = targetType;
            MissingMembers = missingMembers?.ToArray() ?? Array.Empty<string>();
        }

        private static string BuildMessage(Type targetType, IEnumerable<string> missingMembers)
        {
            var typeName = targetType?.FullName ?? "<unknown>";
            var members = missingMembers != null ? string.Join(", ", missingMembers) : "<none>";
            return $"[RuntimeManager] Required [Inject] dependencies could not be resolved on {typeName}: {members}";
        }
    }
}
