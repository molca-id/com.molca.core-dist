using System;
using System.Linq;
using System.Text;
using Molca.ColorID;
using Molca.ColorID.Editor.Upgrade;
using UnityEditor;
using UnityEngine;

namespace Molca.App.UI.Editor
{
    /// <summary>
    /// Menu and headless entry points for <see cref="ColorTokenReferenceMigration"/>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/AppUI/Migration/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>, plus static entry points for <c>-executeMethod</c>.
    /// <para/>
    /// The preview exits non-zero only when the plan is <i>inconclusive</i>. Refused sites are a reportable
    /// content problem for a human to decide, not a build failure — the same policy the deprecation report
    /// uses, and for the same reason: remaining legacy usage must not fail a build during the migration
    /// window.
    /// </remarks>
    public static class ColorTokenReferenceMigrationMenu
    {
        /// <summary>Reports what the migration would do, writing nothing.</summary>
        [MenuItem("Molca/ColorID/Preview ColorTokenReference Migration", priority = 110)]
        public static void Preview()
        {
            if (!TryPlan(out var plan, out string error))
            {
                Debug.LogError(error);
                return;
            }

            Debug.Log(Describe(plan));
        }

        /// <summary>Applies the migration.</summary>
        [MenuItem("Molca/ColorID/Migrate ColorTokenReference…", priority = 111)]
        public static void Migrate()
        {
            if (!TryPlan(out var plan, out string error))
            {
                Debug.LogError(error);
                return;
            }

            if (!EditorUtility.DisplayDialog("Migrate ColorTokenReference",
                    Describe(plan) + "\n\nApply?", "Apply", "Cancel"))
            {
                return;
            }

            var result = ColorTokenReferenceMigration.Apply(plan);
            Debug.Log(Describe(result));
        }

        /// <summary>Headless preview for <c>-executeMethod</c>.</summary>
        /// <remarks>Exits non-zero when the plan is inconclusive.</remarks>
        public static void PreviewFromCli()
        {
            if (!TryPlan(out var plan, out string error))
            {
                Debug.LogError(error);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log(Describe(plan));
            EditorApplication.Exit(plan.IsConclusive ? 0 : 1);
        }

        /// <summary>Headless apply for <c>-executeMethod</c>.</summary>
        /// <remarks>Exits non-zero when the plan is inconclusive or any write failed.</remarks>
        public static void ApplyFromCli()
        {
            if (!TryPlan(out var plan, out string error))
            {
                Debug.LogError(error);
                EditorApplication.Exit(1);
                return;
            }

            if (!plan.IsConclusive)
            {
                Debug.LogError(Describe(plan));
                EditorApplication.Exit(1);
                return;
            }

            ColorTokenReferenceResult result;
            try
            {
                result = ColorTokenReferenceMigration.Apply(plan);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log(Describe(plan) + "\n\n" + Describe(result));
            EditorApplication.Exit(result.Failures.Count == 0 ? 0 : 1);
        }

        private static bool TryPlan(out ColorTokenReferencePlan plan, out string error)
        {
            plan = null;
            error = null;

            var readiness = ColorThemeUpgradeReadiness.Evaluate();
            if (!readiness.IsReady)
            {
                error = "[ColorTokenReferenceMigration] " + readiness.Message
                        + " Review the unified 1.x → 2.x readiness report before retrying.";
                return false;
            }

            plan = ColorTokenReferenceMigration.Plan(readiness.ThemeSet);
            return true;
        }

        private static string Describe(ColorTokenReferencePlan plan)
        {
            var text = new StringBuilder();
            text.AppendLine("[ColorTokenReferenceMigration] plan");
            text.AppendLine($"  migrate:       {plan.Migrated.Count()}");
            text.AppendLine($"  refuse:        {plan.Refused.Count()}");
            text.AppendLine($"  nothing to do: {plan.NothingToDo.Count()}");
            text.AppendLine($"  conclusive:    {plan.IsConclusive}");

            foreach (string unreadable in plan.UnreadableAssets)
                text.AppendLine($"  UNREADABLE {unreadable}");

            foreach (var field in plan.Migrated.OrderBy(f => f.ContainingAssetPath, StringComparer.Ordinal))
            {
                text.AppendLine($"  migrate {field.ContainingAssetPath} "
                                + $"{(field.IsInstanceOverride ? "[override] " : string.Empty)}"
                                + $"{field.FieldName}: {field.LegacyKey.SwatchName}.{field.LegacyKey.ColorId}"
                                + $" -> {field.CanonicalTokenId}");
            }

            foreach (var field in plan.Refused.OrderBy(f => f.ContainingAssetPath, StringComparer.Ordinal))
            {
                text.AppendLine($"  REFUSE  {field.ContainingAssetPath} "
                                + $"{(field.IsInstanceOverride ? "[override] " : string.Empty)}"
                                + $"{field.FieldName}: {field.LegacyKey.SwatchName}.{field.LegacyKey.ColorId}"
                                + $" — {field.Reason}");
            }

            return text.ToString();
        }

        private static string Describe(ColorTokenReferenceResult result)
        {
            var text = new StringBuilder();
            text.AppendLine("[ColorTokenReferenceMigration] result");
            text.AppendLine($"  source fields written:   {result.SourceFieldsWritten}");
            text.AppendLine($"  override fields written: {result.OverrideFieldsWritten}");
            text.AppendLine($"  assets written:          {result.WrittenAssets.Count}");

            foreach (string asset in result.WrittenAssets) text.AppendLine($"    {asset}");
            foreach (string failure in result.Failures) text.AppendLine($"  FAILED {failure}");

            return text.ToString();
        }
    }
}
