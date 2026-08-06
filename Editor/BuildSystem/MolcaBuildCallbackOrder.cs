namespace Molca.Editor
{
    /// <summary>
    /// The <c>callbackOrder</c> band every Molca build callback belongs to, and the values inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>.
    /// </para>
    /// <para>
    /// <b>Why this exists.</b> Unity discovers <c>IPreprocessBuildWithReport</c> by type, so the only
    /// thing coordinating a dozen independent callbacks is a number each one picks for itself. Core had
    /// nine of them spanning <c>int.MinValue</c> to <c>+100</c> with no stated convention, and two gates
    /// that abort the build sat at <c>+100</c> — above a callback that writes a generated file into
    /// <c>Assets/</c>. Because a <c>BuildFailedException</c> from a preprocessor skips every
    /// postprocessor, those two aborts leaked that file into the project. Nobody chose that; it fell out
    /// of two files picking numbers years apart, each for a locally sensible reason.
    /// </para>
    /// <para>
    /// <b>Use a constant from here rather than a literal.</b> A literal is a decision with no argument
    /// attached, and the next reader cannot tell which band it meant to be in.
    /// </para>
    ///
    /// <para><b>The bands, in execution order:</b></para>
    /// <list type="table">
    ///   <listheader><term>Band</term><description>What belongs in it</description></listheader>
    ///   <item>
    ///     <term><see cref="VersionSync"/> (<c>int.MinValue</c>)</term>
    ///     <description>
    ///     Idempotent state every later callback may read. Runs before the gates, so nothing here may
    ///     have an effect that outlives an aborted build.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Gates (<see cref="LicenseGate"/> … <see cref="LastGate"/>)</term>
    ///     <description>
    ///     Anything that can throw <c>BuildFailedException</c>. Ordered cheapest-to-refuse first, and
    ///     always before player settings are mutated, so an abort needs no restore.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term><see cref="Observer"/> (<c>0</c>)</term>
    ///     <description>Callbacks that only read and report — notifications, activity routing. Never abort.</description>
    ///   </item>
    ///   <item>
    ///     <term><see cref="GeneratedArtifacts"/></term>
    ///     <description>
    ///     Callbacks that create files for the player to carry and rely on a postprocessor to remove
    ///     them. These must run after <em>every</em> gate, because an abort means their cleanup never runs.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    public static class MolcaBuildCallbackOrder
    {
        /// <summary>
        /// Version data synced into <c>PlayerSettings</c> for every later callback to read.
        /// </summary>
        /// <remarks>
        /// First, deliberately: a notification or gate that reports the version must see the built one.
        /// Only idempotent work belongs this early — it runs before any gate can refuse the build.
        /// </remarks>
        public const int VersionSync = int.MinValue;

        /// <summary>May this project be built at all — entitlement and terms.</summary>
        /// <remarks>Cheapest possible refusal, and the one that makes every later check pointless.</remarks>
        public const int LicenseGate = -10000;

        /// <summary>Guards against shipping an editor-only or development affordance.</summary>
        public const int EnvironmentGuard = -9000;

        /// <summary>Scene reference resolution across the build scene set.</summary>
        public const int ReferenceGate = -1000;

        /// <summary>Localization completeness and content freshness.</summary>
        public const int LocalizationGate = -900;

        /// <summary>Network catalog validation — routes, hosts, credentials, policies.</summary>
        public const int NetworkCatalogGate = -800;

        /// <summary>Colour theme resolution, contrast, and generated UI Toolkit freshness.</summary>
        public const int ColorThemeGate = -700;

        /// <summary>
        /// The highest order a gate may use. A callback that must run after every gate orders above this.
        /// </summary>
        /// <remarks>
        /// Deliberately leaves room between <see cref="ColorThemeGate"/> and here: a new gate slots in
        /// without renumbering its neighbours, and stays inside the band the test enforces.
        /// </remarks>
        public const int LastGate = -100;

        /// <summary>Read-only observers: notifications, activity routing, telemetry.</summary>
        public const int Observer = 0;

        /// <summary>
        /// Generated files the player carries, removed by a postprocessor once the build ends.
        /// </summary>
        /// <remarks>
        /// Above every gate on purpose. A gate that aborts by throwing skips every postprocessor, so a
        /// generator ordered below a gate leaves its output behind in the project. Still far ahead of
        /// Unity collecting <c>Resources</c>, which happens once the player build itself starts.
        /// </remarks>
        public const int GeneratedArtifacts = 1000;

        // -------------------------------------------------------------------
        // Postprocessor bands
        // -------------------------------------------------------------------
        // Postprocessors are ordered by the same ascending callbackOrder, and had no vocabulary at all:
        // two Core callbacks both sat at int.MaxValue, each documented as running "after every other
        // post-process callback". Only one of them can, and which one is whatever order Unity happens to
        // discover them in. A callback implementing both halves is ordered by its *gate* band — Unity
        // reads one callbackOrder property for both interfaces — so these apply to postprocess-only
        // callbacks.

        /// <summary>Read-only post-build observers: notifications, activity routing, telemetry.</summary>
        /// <remarks>Same value and same reasoning as <see cref="Observer"/>, on the way out.</remarks>
        public const int PostObserver = 0;

        /// <summary>
        /// Removal of the files written by a <see cref="GeneratedArtifacts"/> preprocessor.
        /// </summary>
        /// <remarks>
        /// After the observers, so a callback that reports on the build can still read a generated stamp
        /// while it exists. Mirrors <see cref="GeneratedArtifacts"/> deliberately: the two halves of one
        /// generated file are easier to keep honest when their orders are named as a pair.
        /// </remarks>
        public const int PostGeneratedCleanup = 1000;

        /// <summary>
        /// Advancing recorded build state — the build number and the changelog entry — once everything
        /// else has read this build.
        /// </summary>
        /// <remarks>
        /// Last, and exclusively Core's: a "build completed" reader must report the version that was just
        /// built rather than the next build's number, and that guarantee only holds if exactly one
        /// callback occupies the final position. A project callback that needs to run late belongs just
        /// below this, not alongside it.
        /// </remarks>
        public const int PostVersionAdvance = int.MaxValue;
    }
}
