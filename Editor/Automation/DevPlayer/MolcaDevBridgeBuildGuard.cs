using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Molca.Editor.Automation.DevPlayer
{
    /// <summary>
    /// Enforces the Phase 5 rejection criterion — "no runtime-dev component can enter a production build"
    /// (§17). The <c>MolcaDevPlayerBridge</c> is already excluded from a production Player by its
    /// <c>DEVELOPMENT_BUILD || UNITY_EDITOR</c> compilation guard; this preprocessor is the belt-and-braces
    /// check that fails a non-development build if <c>DEVELOPMENT_BUILD</c> has been forced into the
    /// scripting define symbols (which would smuggle the bridge in). Discovered by Unity as an
    /// <see cref="IPreprocessBuildWithReport"/>, so it runs for the Build Manager, File &gt; Build, and CI.
    /// </summary>
    public sealed class MolcaDevBridgeBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>Runs in the gate band, after licensing. See <see cref="MolcaBuildCallbackOrder"/>.</summary>
        public int callbackOrder => MolcaBuildCallbackOrder.EnvironmentGuard;

        /// <summary>The scripting define that gates the dev bridge into a build.</summary>
        public const string DevBuildDefine = "DEVELOPMENT_BUILD";

        /// <summary>Fails a non-development build that would ship the dev bridge via a forced define.</summary>
        /// <param name="report">The build report for the build about to run.</param>
        /// <exception cref="BuildFailedException">Thrown when the bridge would enter a production build.</exception>
        public void OnPreprocessBuild(BuildReport report)
        {
            bool developmentBuild = (report.summary.options & BuildOptions.Development) != 0;

            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(report.summary.platform));
            var defines = PlayerSettings.GetScriptingDefineSymbols(namedTarget)
                .Split(';', ',')
                .Select(d => d.Trim())
                .Where(d => d.Length > 0);

            if (WouldBridgeShip(developmentBuild, defines))
            {
                Molca.Editor.MolcaBuildRefusal.Record(
                    Molca.Editor.MolcaBuildReasonCode.DevBridgePresent);
                throw new BuildFailedException(
                    $"[Molca] '{DevBuildDefine}' is in the scripting define symbols for a non-development build — " +
                    "the development-player bridge would ship in a production Player. Remove the forced define " +
                    "or check the Development Build option.");
            }
        }

        /// <summary>
        /// Pure test seam: whether the dev bridge could enter a build. It ships only when this is a
        /// development build (expected and allowed) or when <see cref="DevBuildDefine"/> is force-defined
        /// on an otherwise non-development build (the unsafe case this guard rejects).
        /// </summary>
        /// <param name="developmentBuild">Whether the build has the Development option set.</param>
        /// <param name="defines">The scripting define symbols for the target.</param>
        /// <returns>True only for the unsafe case: a non-development build that force-defines the dev flag.</returns>
        public static bool WouldBridgeShip(bool developmentBuild, IEnumerable<string> defines)
        {
            if (developmentBuild) return false; // allowed: the bridge belongs in a dev build
            return defines != null && defines.Any(d => d == DevBuildDefine);
        }
    }
}
