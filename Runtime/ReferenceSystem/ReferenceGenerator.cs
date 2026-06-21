using System;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// Static class responsible for generating unique reference IDs.
    /// Provides various ID generation strategies for the reference system.
    /// </summary>
    /// <remarks>
    /// IDs are <see cref="Guid"/>-based: each id embeds fresh GUID entropy, so they are
    /// collision-safe across editor sessions and process restarts. (The previous scheme
    /// combined a near-constant high-order <c>Ticks</c> timestamp with a per-session
    /// counter that reset to zero each session, opening a collision window.) The change is
    /// data-safe: existing string ids keep resolving — only newly generated ids change shape.
    /// </remarks>
    public static class ReferenceGenerator
    {
        private const string ID_PREFIX = "ref_";
        private const int MAX_ID_LENGTH = 32;

        #region ID Generation

        /// <summary>
        /// Generate a unique reference ID for the specified type.
        /// </summary>
        /// <param name="referenceType">The type identifier for the reference.</param>
        /// <param name="customPrefix">Optional custom prefix to override the default.</param>
        /// <returns>A unique reference ID.</returns>
        public static string GenerateUniqueId(string referenceType, string customPrefix = null)
        {
            if (string.IsNullOrEmpty(referenceType))
                throw new ArgumentException("Reference type cannot be null or empty", nameof(referenceType));

            string prefix = customPrefix ?? GetDefaultPrefix(referenceType);
            return GenerateId(prefix);
        }

        /// <summary>
        /// Generate a unique reference ID with a specific prefix.
        /// </summary>
        /// <param name="prefix">The prefix to use for the ID.</param>
        /// <param name="referenceType">The type identifier for the reference.</param>
        /// <returns>A unique reference ID with the specified prefix.</returns>
        public static string GenerateUniqueIdWithPrefix(string prefix, string referenceType)
        {
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentException("Prefix cannot be null or empty", nameof(prefix));

            if (string.IsNullOrEmpty(referenceType))
                throw new ArgumentException("Reference type cannot be null or empty", nameof(referenceType));

            return GenerateId(prefix);
        }

        /// <summary>
        /// Generate a short unique reference ID for the specified type.
        /// </summary>
        /// <param name="referenceType">The type identifier for the reference.</param>
        /// <returns>A short unique reference ID.</returns>
        public static string GenerateShortUniqueId(string referenceType)
        {
            if (string.IsNullOrEmpty(referenceType))
                throw new ArgumentException("Reference type cannot be null or empty", nameof(referenceType));

            string prefix = GetShortPrefix(referenceType);
            return GenerateShortId(prefix);
        }

        /// <summary>
        /// Generate a ReferenceId struct for the specified type.
        /// </summary>
        /// <param name="referenceType">The type identifier for the reference.</param>
        /// <returns>A ReferenceId struct with a unique ID.</returns>
        public static ReferenceId GenerateReferenceId(string referenceType)
        {
            string id = GenerateUniqueId(referenceType);
            return new ReferenceId(id, referenceType);
        }

        #endregion

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only: true when <paramref name="instance"/> is a prefab instance still
        /// carrying its source asset's id (an inherited duplicate). Placing a referenceable
        /// prefab N times would otherwise share one id; <c>OnValidate</c> uses this to force
        /// a fresh id per placement.
        /// </summary>
        /// <param name="instance">The component being validated.</param>
        /// <param name="currentId">The component's current <see cref="IReferenceable.RefId"/>.</param>
        public static bool IsInheritedPrefabId(UnityEngine.Component instance, string currentId)
        {
            if (instance == null || string.IsNullOrEmpty(currentId))
                return false;

            var source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(instance);
            return source is IReferenceable referenceable && referenceable.RefId == currentId;
        }
#endif



        #region Private Methods

        private static string GenerateId(string prefix)
        {
            // 32 hex chars of GUID entropy, dashless. Collision-safe across sessions.
            string unique = Guid.NewGuid().ToString("N");
            string combined = $"{prefix}{unique}";

            if (combined.Length > MAX_ID_LENGTH)
            {
                // Cap the whole id at MAX_ID_LENGTH while always keeping at least
                // MinGuidChars of GUID entropy — truncating the prefix first if it is
                // long enough to crowd the GUID out.
                const int MinGuidChars = 8;
                int maxPrefix = MAX_ID_LENGTH - MinGuidChars;
                string p = prefix.Length > maxPrefix ? prefix.Substring(0, maxPrefix) : prefix;
                int guidRoom = MAX_ID_LENGTH - p.Length;
                combined = p + unique.Substring(0, Math.Min(unique.Length, guidRoom));
            }

            return combined;
        }

        private static string GenerateShortId(string prefix)
        {
            // Short by design: 8 hex chars / 32 bits of GUID entropy.
            string unique = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{prefix}{unique}";
        }

        private static string GetDefaultPrefix(string referenceType)
        {
            return $"{ID_PREFIX}{referenceType.ToLower()}_";
        }

        private static string GetShortPrefix(string referenceType)
        {
            return $"{ID_PREFIX}{referenceType.Substring(0, Math.Min(3, referenceType.Length)).ToLower()}_";
        }

        #endregion
    }
}
