namespace Molca.Editor.ContentPackage
{
    /// <summary>
    /// Builds the Addressables content a player ships, immediately before the player itself, so the
    /// two can never be out of sync. Opt-in per build profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/ContentPackage/</c>.
    /// <b>Registration:</b> discovered by <see cref="MolcaBuildStepRegistry"/> as an
    /// <see cref="IMolcaBuildStep"/>.
    /// </para>
    /// <para>
    /// <b>Why this is a step and not a branch in BuildManager.</b> The build manager used to call into
    /// this system by name, which made the build core depend on the content system and gave the next
    /// system to need pre-build work nowhere to put itself. It also had to remember to tell the
    /// localization gate what it had done, through a static latch on that gate's own class. Both of
    /// those are now the seam's job: the step lives with the system it belongs to, and the fact it
    /// records travels on <see cref="MolcaBuildContext"/>, which expires with the build.
    /// </para>
    /// </remarks>
    public sealed class AddressablesContentBuildStep : IMolcaBuildStep
    {
        /// <summary>
        /// Recorded on the build context when this step has rebuilt the content the player ships.
        /// </summary>
        /// <remarks>
        /// Declared here, by the system that sets it, rather than in the build core — which
        /// deliberately does not know what Addressables content is. Read by the localization build
        /// gate, whose production freshness policy this fact satisfies.
        /// </remarks>
        public const string ContentBuiltFact = "content.addressables-built";

        /// <inheritdoc/>
        public string Id => "addressables-content";

        /// <inheritdoc/>
        public string DisplayName => "Addressables content";

        /// <summary>
        /// Runs early: the content must exist before anything inspects or ships it, and a content
        /// failure should abort before later steps do expensive work.
        /// </summary>
        public int Order => 100;

        /// <inheritdoc/>
        public bool ShouldRun(MolcaBuildContext context) => context?.Profile?.buildAddressablesFirst == true;

        /// <inheritdoc/>
        public MolcaBuildStepResult Run(MolcaBuildContext context)
        {
            var result = AddressablesBuildUtility.BuildAllContent(
                new AddressablesBuildUtility.BuildOptions
                {
                    ProfileName = AddressablesBuildUtility.GetActiveProfileName(),
                    CleanBuild = context.Profile.cleanBuildCache,
                });

            if (result == null || !result.Success)
            {
                return MolcaBuildStepResult.Fail(
                    "Addressables content build failed. " +
                    (result?.ErrorMessage ?? result?.Message ?? "Unknown error."));
            }

            // Recorded only on success, and only after the build returned — a fact claimed before the
            // work completes is the stale-latch failure this seam exists to remove.
            context.SetFact(ContentBuiltFact);

            return MolcaBuildStepResult.Ok($"built {result.BuiltGroups.Count} group(s).");
        }
    }
}
