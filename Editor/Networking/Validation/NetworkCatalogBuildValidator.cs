using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Editor.Networking.Authoring;

namespace Molca.Editor.Networking.Validation
{
    /// <summary>
    /// Validates the network catalog when a build starts.
    /// </summary>
    /// <remarks>
    /// Warning-only unless the catalog sets <see cref="NetworkCatalog.FailBuildOnValidationError"/>.
    /// Phase 1 ships the hook in warning mode on purpose: turning a project's build red on the
    /// framework's schedule rather than the project's would be the wrong default while the routed
    /// pipeline is still rolling out.
    /// <para>
    /// A project with no catalog at all is silent. Nothing depends on the catalog until a route is
    /// used, so absence is not yet a build problem.
    /// </para>
    /// </remarks>
    public sealed class NetworkCatalogBuildValidator : IPreprocessBuildWithReport
    {
        /// <summary>
        /// Runs after most other preprocessors so its message lands near the end of the build log,
        /// where a reader looks first.
        /// </summary>
        public int callbackOrder => 100;

        /// <summary>
        /// Validates the project's catalog and reports the result to the build log.
        /// </summary>
        /// <param name="report">The build report supplied by Unity.</param>
        /// <exception cref="BuildFailedException">
        /// The catalog has validation errors and opts into failing the build.
        /// </exception>
        public void OnPreprocessBuild(BuildReport report)
        {
            var catalog = NetworkCatalogLocator.FindCatalog();
            if (catalog == null)
                return;

            var validation = NetworkCatalogValidator.Validate(catalog);
            if (validation.IsValid)
            {
                if (validation.WarningCount > 0)
                    Debug.Log($"[Network] Catalog validation passed with {validation.WarningCount} warning(s).");
                return;
            }

            string details = Format(validation);

            if (catalog.FailBuildOnValidationError)
                throw new BuildFailedException(
                    $"[Network] Catalog validation failed: {validation.Summarize()}.\n{details}");

            Debug.LogWarning(
                $"[Network] Catalog validation failed: {validation.Summarize()}. " +
                "The build continues because the catalog does not enable 'Fail Build On Validation Error'.\n" +
                details);
        }

        /// <summary>
        /// Renders errors and warnings for the build log, one finding per line.
        /// </summary>
        /// <param name="validation">The report to format.</param>
        /// <returns>A multi-line summary.</returns>
        private static string Format(NetworkValidationReport validation)
        {
            var builder = new StringBuilder();
            foreach (var finding in validation.AtLeast(NetworkValidationSeverity.Warning))
            {
                builder.Append("  ").Append(finding).AppendLine();

                if (!string.IsNullOrEmpty(finding.Remedy))
                    builder.Append("    → ").Append(finding.Remedy).AppendLine();
            }
            return builder.ToString();
        }
    }
}
