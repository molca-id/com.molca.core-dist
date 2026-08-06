using System;
using Molca.Settings;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Work a system contributes <em>after</em> a Molca player build has succeeded — uploading debug
    /// symbols, publishing an artifact, recording a release row, notifying a channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>.
    /// <b>Registration:</b> implement this interface with a public parameterless constructor in any
    /// Editor assembly; <see cref="MolcaBuildStepRegistry"/> discovers it. The pre-build twin is
    /// <see cref="IMolcaBuildStep"/>.
    /// </para>
    /// <para>
    /// <b>Why this exists.</b> <see cref="IMolcaBuildStep"/> gave pre-build work a home, and everything
    /// that happens once a player exists had none — so the next system needing it would have added
    /// another <c>IPostprocessBuildWithReport</c> with a hand-picked <c>callbackOrder</c>, which is the
    /// pattern <see cref="MolcaBuildCallbackOrder"/> exists to stop. A post step also gets what a raw
    /// Unity postprocessor cannot be given: the profile, the facts the pre-build steps recorded, and the
    /// resolved output path.
    /// </para>
    /// <para>
    /// <b>Only for builds that produced an artifact.</b> Post steps do not run for a failed, cancelled or
    /// refused build. Work that must happen whichever way a build ends belongs in a Unity postprocessor in
    /// the <see cref="MolcaBuildCallbackOrder.PostGeneratedCleanup"/> band.
    /// </para>
    /// <para>
    /// <b>A failing post step does not fail the build</b> — the player already exists and cannot be
    /// un-built, and reporting the build as failed because an upload was rejected sends the reader looking
    /// in the wrong place. Every post step runs even when an earlier one failed (unlike the pre-build
    /// steps, which stop at the first failure because later ones may depend on earlier ones), and the
    /// failures are surfaced together and recorded on the build record.
    /// </para>
    /// </remarks>
    public interface IMolcaPostBuildStep
    {
        /// <summary>Stable kebab-case identifier, unique across all registered post-build steps.</summary>
        string Id { get; }

        /// <summary>Human-facing name for build logs.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Relative execution order; lower runs first. Ties break on <see cref="Id"/> (ordinal).
        /// </summary>
        /// <remarks>Core's own steps use multiples of 100 to leave room between them.</remarks>
        int Order { get; }

        /// <summary>Whether this step applies to <paramref name="context"/>.</summary>
        /// <param name="context">The build that just succeeded.</param>
        /// <returns>False to skip silently — an off-by-configuration step is not a failure.</returns>
        bool ShouldRun(MolcaPostBuildContext context);

        /// <summary>Performs the step's work.</summary>
        /// <param name="context">The build that just succeeded.</param>
        /// <returns>
        /// The outcome. A failure is reported and recorded but does not turn the build into a failure —
        /// a step must still not report success for work it did not complete.
        /// </returns>
        MolcaBuildStepResult Run(MolcaPostBuildContext context);
    }

    /// <summary>
    /// Everything an <see cref="IMolcaPostBuildStep"/> may know about the build it is running after.
    /// </summary>
    public sealed class MolcaPostBuildContext
    {
        /// <summary>The profile that was built.</summary>
        public BuildSettings.BuildProfile Profile { get; }

        /// <summary>The target that was built for.</summary>
        public BuildTarget Target { get; }

        /// <summary>Where the artifact was written.</summary>
        public string OutputPath { get; }

        /// <summary>
        /// The record for this build, before it is persisted.
        /// </summary>
        /// <remarks>
        /// Carries the version, build number, git provenance, size and duration a step would otherwise
        /// re-derive — and re-derive slightly differently, which is how two records of one build end up
        /// disagreeing. Mutating it is not the intended use; read it.
        /// </remarks>
        public MolcaBuildRecord Record { get; }

        /// <summary>
        /// The pre-build context, so a post step can read the facts the pre-build steps recorded.
        /// </summary>
        /// <remarks>
        /// A publisher that must not upload a player whose content bundles were not rebuilt asks the
        /// context, rather than guessing from the profile's configuration.
        /// </remarks>
        public MolcaBuildContext BuildContext { get; }

        /// <summary>Initializes a post-build context.</summary>
        /// <param name="profile">The profile that was built. Required.</param>
        /// <param name="outputPath">Where the artifact was written.</param>
        /// <param name="record">The record for this build.</param>
        /// <param name="buildContext">The pre-build context for the same build.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is null.</exception>
        public MolcaPostBuildContext(
            BuildSettings.BuildProfile profile,
            string outputPath,
            MolcaBuildRecord record,
            MolcaBuildContext buildContext)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Target = profile.target;
            OutputPath = outputPath ?? string.Empty;
            Record = record;
            BuildContext = buildContext;
        }
    }
}
