namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Shared scope helper for the Sprint-36 consumer-facing networking checks.
    /// They surface footguns a project/SDK author can hit; Core's own networking
    /// internals are validated by tests, not Doctor (locked Sprint-36 decision), so
    /// these checks skip Core package sources.
    /// </summary>
    internal static class NetworkingCheckScope
    {
        private const string CorePackagePrefix = "Packages/com.molca.core/";

        /// <summary>True if the source lives inside the Molca Core package.</summary>
        public static bool IsCoreInternal(DoctorSourceFile source) =>
            source.Path != null && source.Path.StartsWith(CorePackagePrefix, System.StringComparison.Ordinal);
    }
}
