using System;
using System.Collections.Generic;
using Molca.Settings;
using UnityEditor;

namespace Molca.Editor
{
    /// <summary>
    /// Work a system contributes to a Molca player build, ahead of <c>BuildPipeline.BuildPlayer</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>.
    /// <b>Registration:</b> implement this interface with a public parameterless constructor in any
    /// Editor assembly; <see cref="MolcaBuildStepRegistry"/> discovers it. No Core edit is required,
    /// which is the point — this is the extension seam Doctor has in <c>IDoctorCheck</c> and the Hub
    /// has in its workspace registry.
    /// </para>
    /// <para>
    /// <b>Why this exists.</b> <see cref="BuildManager"/> used to name one specific system —
    /// Addressables content — in its own body, and the next system to need pre-build work would have
    /// added a second such branch. Systems that could not edit <see cref="BuildManager"/> instead
    /// registered a global <c>IPreprocessBuildWithReport</c> with a hand-picked
    /// <c>callbackOrder</c> and hand-rolled a static latch to tell the next callback what they had
    /// already done. Three systems independently invented that latch. A step declares its
    /// <see cref="Order"/> against the other steps and communicates through
    /// <see cref="MolcaBuildContext"/>, whose lifetime is one build.
    /// </para>
    /// <para>
    /// <b>Steps run inside the Molca build path only.</b> <c>File &gt; Build</c> and a raw
    /// <c>BuildPipeline.BuildPlayer</c> call do not run them — those entry points cannot be given a
    /// profile, and a step that silently did nothing for half the ways a project builds would be
    /// worse than one that is documented not to run. Work that must gate <em>every</em> build stays
    /// an <c>IPreprocessBuildWithReport</c> (see <see cref="ReferenceBuildGate"/>).
    /// </para>
    /// <para>Steps are synchronous: they run on the main thread inside the build call, and the build
    /// cannot proceed past them.</para>
    /// </remarks>
    public interface IMolcaBuildStep
    {
        /// <summary>Stable kebab-case identifier, unique across all registered steps.</summary>
        string Id { get; }

        /// <summary>Human-facing name for build logs.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Relative execution order; lower runs first. Ties break on <see cref="Id"/> (ordinal), so
        /// two steps that do not declare an order between them still run deterministically.
        /// </summary>
        /// <remarks>Core's own steps use multiples of 100 to leave room between them.</remarks>
        int Order { get; }

        /// <summary>Whether this step applies to <paramref name="context"/>.</summary>
        /// <param name="context">The build about to run.</param>
        /// <returns>False to skip silently — an off-by-configuration step is not a failure.</returns>
        bool ShouldRun(MolcaBuildContext context);

        /// <summary>Performs the step's work.</summary>
        /// <param name="context">The build about to run; record facts other steps or gates need here.</param>
        /// <returns>
        /// The outcome. A failed result aborts the build before any PlayerSettings mutation, so a step
        /// must not report success for work it did not complete.
        /// </returns>
        MolcaBuildStepResult Run(MolcaBuildContext context);
    }

    /// <summary>The outcome of one <see cref="IMolcaBuildStep"/>.</summary>
    public readonly struct MolcaBuildStepResult
    {
        /// <summary>True when the step completed its work.</summary>
        public bool Succeeded { get; }

        /// <summary>Detail for the build log, or the abort reason when <see cref="Succeeded"/> is false.</summary>
        public string Message { get; }

        private MolcaBuildStepResult(bool succeeded, string message)
        {
            Succeeded = succeeded;
            Message = message ?? string.Empty;
        }

        /// <summary>A successful outcome.</summary>
        /// <param name="message">Optional detail for the build log.</param>
        public static MolcaBuildStepResult Ok(string message = null) => new MolcaBuildStepResult(true, message);

        /// <summary>A failing outcome; aborts the build.</summary>
        /// <param name="message">Why the build cannot continue. Shown to the person who started it.</param>
        public static MolcaBuildStepResult Fail(string message) => new MolcaBuildStepResult(false, message);
    }

    /// <summary>
    /// Everything a <see cref="IMolcaBuildStep"/> may know about the build it is running inside, plus
    /// the facts it records for later steps and for build gates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Facts replace the static latches.</b> A gate that needs to know what an earlier phase did —
    /// "was this player's Addressables content just rebuilt?" — used to read a <c>static bool</c> that
    /// one other file wrote and every reader had to remember to clear. Those latches carry comments
    /// warning that a stale one silently weakens the gate, which is exactly the failure a gate must not
    /// have. A fact lives on the context, and the context lives for one build
    /// (<see cref="MolcaBuildSession"/>), so it cannot go stale by construction.
    /// </para>
    /// <para>
    /// Fact keys are declared by the system that <em>sets</em> them, not by this assembly. The build
    /// core deliberately does not know what "Addressables content" is.
    /// </para>
    /// </remarks>
    public sealed class MolcaBuildContext
    {
        private readonly HashSet<string> _facts = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The profile being built.</summary>
        public BuildSettings.BuildProfile Profile { get; }

        /// <summary>The target being built for.</summary>
        public BuildTarget Target { get; }

        /// <summary>Whether this is a development build.</summary>
        public bool IsDevelopmentBuild { get; }

        /// <summary>Initializes a build context.</summary>
        /// <param name="profile">The profile being built. Required.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is null.</exception>
        public MolcaBuildContext(BuildSettings.BuildProfile profile)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Target = profile.target;
            IsDevelopmentBuild = profile.developmentBuild;
        }

        /// <summary>Records a fact about this build.</summary>
        /// <param name="key">The fact key, declared by the system that sets it.</param>
        public void SetFact(string key)
        {
            if (!string.IsNullOrEmpty(key))
                _facts.Add(key);
        }

        /// <summary>Whether a fact was recorded for this build.</summary>
        /// <param name="key">The fact key.</param>
        /// <returns>True when some step recorded it.</returns>
        public bool HasFact(string key) => !string.IsNullOrEmpty(key) && _facts.Contains(key);
    }

    /// <summary>
    /// The <see cref="MolcaBuildContext"/> for the build currently running, for callbacks Unity invokes
    /// with only a <c>BuildReport</c> and which therefore cannot be handed one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A build gate implemented as <c>IPreprocessBuildWithReport</c> is discovered by Unity, not called
    /// by <see cref="BuildManager"/>, so there is no parameter to pass a context through. This is the
    /// one static, with one lifetime: <see cref="Begin"/> opens it and disposing closes it, from a
    /// <c>finally</c> in <see cref="BuildManager"/>. Outside a Molca build — <c>File &gt; Build</c>, a
    /// content build, a raw <c>BuildPipeline.BuildPlayer</c> — <see cref="Current"/> is null and a
    /// reader must treat that as "nothing is known", never as a default.
    /// </para>
    /// <para>Not re-entrant: nested builds are not a thing Unity supports, and a nested
    /// <see cref="Begin"/> logs rather than silently replacing the outer context.</para>
    /// </remarks>
    public static class MolcaBuildSession
    {
        /// <summary>The context of the running Molca build, or null when no Molca build is running.</summary>
        public static MolcaBuildContext Current { get; private set; }

        /// <summary>Opens a session for <paramref name="context"/>.</summary>
        /// <param name="context">The build about to run.</param>
        /// <returns>A scope that closes the session when disposed. Always dispose from a <c>finally</c>.</returns>
        public static IDisposable Begin(MolcaBuildContext context)
        {
            if (Current != null)
            {
                UnityEngine.Debug.LogWarning(
                    "[MolcaBuildSession] A build session was already open; the previous one is being replaced. " +
                    "This means a build started inside another build, or a session was not disposed.");
            }

            Current = context;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose() => Current = null;
        }
    }
}
