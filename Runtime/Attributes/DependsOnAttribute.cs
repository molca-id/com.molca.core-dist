using System;

namespace Molca
{
    /// <summary>
    /// Declares that a <see cref="RuntimeSubsystem"/> requires other subsystems to be
    /// initialized first. The bootstrap pipeline performs a topological sort over these
    /// declarations before initializing subsystems.
    /// </summary>
    /// <remarks>
    /// Use this attribute when a subsystem accesses another subsystem inside its
    /// <see cref="RuntimeSubsystem.Initialize"/> override. Without it, ordering is governed
    /// only by <see cref="RuntimeSubsystem.InitializationPriority"/> — which is brittle
    /// since priorities are magic numbers with no compile-time relationship between them.
    /// <para>
    /// When <see cref="DependsOnAttribute"/> is present, dependencies always initialize
    /// first; <see cref="RuntimeSubsystem.InitializationPriority"/> is used only as a
    /// deterministic tiebreaker for unrelated subsystems. Multiple
    /// <see cref="DependsOnAttribute"/> attributes may be applied to one type.
    /// </para>
    /// <para>
    /// Cycles are detected at bootstrap time. On cycle, <see cref="RuntimeManager"/> logs
    /// a clear error identifying the participating types and falls back to priority-only
    /// ordering so the application still boots.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [DependsOn(typeof(LogManager))]
    /// public class TelemetrySubsystem : RuntimeSubsystem { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class DependsOnAttribute : Attribute
    {
        /// <summary>The subsystem types this subsystem requires to be initialized first.</summary>
        public Type[] Dependencies { get; }

        /// <summary>
        /// Declares that the decorated subsystem depends on the given types.
        /// </summary>
        /// <param name="dependencies">One or more <see cref="RuntimeSubsystem"/> types.</param>
        public DependsOnAttribute(params Type[] dependencies)
        {
            Dependencies = dependencies ?? Array.Empty<Type>();
        }
    }
}
