namespace Molca.Editor
{
    /// <summary>
    /// The stable identifiers for why a build attempt did not ship, and the one place they are defined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>.
    /// </para>
    /// <para>
    /// <b>Why a code rather than the message.</b> A build record is reported to the control plane so a
    /// project can see what is failing, and the reason is the useful half of that. But a build failure
    /// message is a developer's private working state — it names source paths, scene names, line numbers,
    /// and sometimes a stack trace. So the report carries an identifier and the message stays on the
    /// machine. The server enforces the same split with a pattern, not a length limit: a length limit would
    /// have admitted all three.
    /// </para>
    /// <para>
    /// <b>Shape.</b> Lowercase, digits, and hyphens; up to 64 characters. Anything else is replaced with
    /// <c>unspecified</c> server-side rather than cleaned up into a code — slugifying a message looks
    /// tidier and is the actual hazard, because
    /// <c>Assets/Game/Boss.cs(42): error CS1002</c> would become a valid-looking code that has leaked the
    /// path and the line.
    /// </para>
    /// <para>
    /// <b>Adding one.</b> Name the thing that refused, not the symptom: a reader who sees
    /// <c>localization-gate</c> knows where to look, and one who sees <c>missing-keys</c> has to guess
    /// which system decided that. Every value here is a constant, because a literal at the throw site is a
    /// string nobody can find the other end of.
    /// </para>
    /// </remarks>
    public static class MolcaBuildReasonCode
    {
        // ---- Refused before the build pipeline started -------------------------------------------------
        // None of these can reach the control plane: they happen before LicenseBuildGate mints the build
        // token a record hangs off. They are recorded locally, which is where the person who hit them is.

        /// <summary>The named profile does not exist in the build settings asset.</summary>
        public const string ProfileNotFound = "profile-not-found";

        /// <summary>The profile cannot produce a build — unsupported target, or Android with no keystore.</summary>
        public const string ProfileInvalid = "profile-invalid";

        /// <summary>The profile's scene set could not be resolved to paths.</summary>
        public const string SceneSetUnresolvable = "scene-set-unresolvable";

        /// <summary>The scene reference audit found unresolvable or ambiguous references.</summary>
        public const string SceneReferences = "scene-references";

        /// <summary>A registered pre-build step refused.</summary>
        public const string PreBuildStep = "pre-build-step";

        /// <summary>The pre-build Molca Doctor gate reported an Error-severity finding.</summary>
        public const string DoctorGate = "doctor-gate";

        /// <summary>The active build target could not be switched to the profile's target.</summary>
        public const string TargetSwitchFailed = "target-switch-failed";

        /// <summary>The build was deferred while the editor switches target, and resumes after the reload.</summary>
        public const string TargetSwitchDeferred = "target-switch-deferred";

        // ---- Refused by a gate inside the build pipeline -----------------------------------------------
        // These run after the license gate, so a build token exists and the refusal is reportable.

        /// <summary>No developer entitlement, or no connected project. Recorded locally only.</summary>
        /// <remarks>
        /// Unreportable by construction: this gate is what mints the build token, so a build it refuses has
        /// no id to be recorded against. The one blind spot the ledger keeps, and it is named here rather
        /// than left as an absence somebody has to notice.
        /// </remarks>
        public const string LicenseGate = "license-gate";

        /// <summary>A development-only bridge or affordance would have shipped in the player.</summary>
        public const string DevBridgePresent = "dev-bridge-present";

        /// <summary>Localization completeness or catalog freshness refused the build.</summary>
        public const string LocalizationGate = "localization-gate";

        /// <summary>The scene reference gate refused the build from inside the pipeline.</summary>
        public const string ReferenceGate = "reference-gate";

        /// <summary>Network catalog validation refused the build.</summary>
        public const string NetworkCatalogGate = "network-catalog-gate";

        /// <summary>Colour theme resolution, contrast, or generated UI freshness refused the build.</summary>
        public const string ColorThemeGate = "color-theme-gate";

        // ---- The build itself ------------------------------------------------------------------------

        /// <summary>
        /// The player build ran and did not succeed, and no gate claimed the refusal.
        /// </summary>
        /// <remarks>
        /// Deliberately one code covering every compile error, link failure, and cancellation. Splitting it
        /// finer would mean classifying Unity's build report messages, and the only honest way to do that
        /// is to read them — which is exactly the text this design keeps on the machine.
        /// </remarks>
        public const string BuildFailed = "build-failed";
    }
}
