using System;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Thrown when a required <see cref="SceneObjectReference"/> resolve fails — the
    /// referenced object was never assigned, is not registered (scene not loaded, or
    /// destroyed), or is the wrong type. Carries the synchronous call site so the
    /// failure points at who initiated the resolve rather than at a downstream
    /// <see cref="NullReferenceException"/> at first use.
    /// </summary>
    /// <remarks>
    /// Mirrors the <c>[Inject(Required = true)]</c> → <see cref="MissingDependencyException"/>
    /// contract: an unmet required dependency fails loudly and traceably instead of
    /// silently returning <c>null</c>.
    /// </remarks>
    public class ReferenceResolutionException : Exception
    {
        /// <summary>The reference id that could not be resolved.</summary>
        public string RefId { get; }

        /// <summary>The serialized reference type, if any.</summary>
        public string RefType { get; }

        /// <summary>The calling member captured at the synchronous resolve call site.</summary>
        public string CallerMember { get; }

        /// <summary>The calling file path captured at the synchronous resolve call site.</summary>
        public string CallerFilePath { get; }

        /// <summary>The calling line number captured at the synchronous resolve call site.</summary>
        public int CallerLine { get; }

        /// <summary>
        /// Create a resolution exception with the reference identity and captured call site.
        /// </summary>
        public ReferenceResolutionException(
            string message,
            string refId,
            string refType,
            string callerMember,
            string callerFilePath,
            int callerLine)
            : base(message)
        {
            RefId = refId;
            RefType = refType;
            CallerMember = callerMember;
            CallerFilePath = callerFilePath;
            CallerLine = callerLine;
        }
    }
}
