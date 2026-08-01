#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.ColorID.Editor
{
    /// <summary>
    /// The revamp plan's §16.6 migration batches, named so a run is attributable to one of them.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Transactions/</c>.
    /// <para/>
    /// A batch is nothing but a path filter plus whether scenes are in scope. That is deliberately thin:
    /// the value of batching is that a visual regression is attributable to one reviewable change, not that
    /// each batch needs its own logic.
    /// <para/>
    /// Batch 4 (the UI Token Catalog) is absent because it is data rather than components and was migrated
    /// in Phase 5 by <c>MolcaUiTokenCatalogMigration</c>. Batches 8 and 9 (Figma configuration, samples and
    /// documentation) are likewise not component migrations.
    /// </remarks>
    public static class ColorMigrationBatches
    {
        /// <summary>One named batch.</summary>
        public sealed class Batch
        {
            /// <summary>Batch number as the plan orders them.</summary>
            public int Number { get; }

            /// <summary>Short name.</summary>
            public string Name { get; }

            /// <summary>What the batch may touch.</summary>
            public ColorContentMigrationOptions Options { get; }

            /// <summary>Creates a batch.</summary>
            public Batch(int number, string name, ColorContentMigrationOptions options)
            {
                Number = number;
                Name = name;
                Options = options;
            }

            /// <inheritdoc/>
            public override string ToString() => $"Batch {Number} — {Name}";
        }

        /// <summary>Every defined batch, in the plan's order.</summary>
        public static readonly IReadOnlyList<Batch> All = new[]
        {
            new Batch(1, "Core prefabs and fixtures", new ColorContentMigrationOptions(
                new[] { "Packages/com.molca.core/" })),

            new Batch(2, "SDK controls", new ColorContentMigrationOptions(
                new[] { "Assets/_MolcaSDK/Prefabs/Controls/" })),

            new Batch(3, "SDK panels and modals", new ColorContentMigrationOptions(
                new[] { "Packages/com.molca.sdk/" })),

            // Scenes are in scope only from batch 5 onward: everything above is prefab content, and
            // opening scenes is the more disruptive operation of the two.
            new Batch(5, "Project prefabs and scenes", new ColorContentMigrationOptions(
                new[] { "Assets/" }, includeScenes: true)),

            new Batch(6, "Renderer and material bindings", new ColorContentMigrationOptions(
                null, includeScenes: true)),

            new Batch(7, "Runtime UI Toolkit panels", new ColorContentMigrationOptions(
                null, includeScenes: true))
        };

        /// <summary>Finds a batch by number.</summary>
        /// <param name="number">The batch number.</param>
        /// <returns>The batch, or <c>null</c>.</returns>
        public static Batch Find(int number) => All.FirstOrDefault(b => b.Number == number);
    }

    /// <summary>
    /// Menu and CLI entry points for <see cref="ColorContentMigration"/>.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Editor/ColorID/Transactions/</c>.
    /// <b>Registration:</b> <c>[MenuItem]</c>, plus static entry points for <c>-executeMethod</c>.
    /// <para/>
    /// Preview and apply are separate menu items, and apply asks for confirmation with the counts in the
    /// prompt. There is no combined "migrate everything" command on purpose: the point of a previewed
    /// transaction is that somebody read the preview.
    /// </remarks>
    public static class ColorContentMigrationMenu
    {
        /// <summary>Previews a migration of every prefab in the project.</summary>
        [MenuItem("Molca/ColorID/Preview Content Migration", priority = 100)]
        public static void PreviewAll() => Preview(ColorContentMigrationOptions.AllPrefabs);

        /// <summary>Previews and, on confirmation, applies a migration of every prefab.</summary>
        [MenuItem("Molca/ColorID/Migrate Content…", priority = 101)]
        public static void MigrateAll() => Migrate(ColorContentMigrationOptions.AllPrefabs);

        private static void Preview(ColorContentMigrationOptions options)
        {
            var plan = BuildPlan(options);
            Debug.Log(plan.ToPreview());
        }

        private static void Migrate(ColorContentMigrationOptions options)
        {
            var plan = BuildPlan(options);
            Debug.Log(plan.ToPreview());

            if (!plan.IsValid)
            {
                EditorUtility.DisplayDialog("Colour content migration",
                    "The plan is not valid:\n\n" + string.Join("\n", plan.Errors), "Close");
                return;
            }

            int count = plan.Migratable.Count();
            if (count == 0)
            {
                EditorUtility.DisplayDialog("Colour content migration",
                    "Nothing to migrate. See the Console for what was blocked and why.", "Close");
                return;
            }

            bool proceed = EditorUtility.DisplayDialog("Colour content migration",
                $"Rewrite {count} ColorID component(s) across {plan.AffectedAssets.Count()} asset(s)?\n\n"
                + (plan.Options.RemoveLegacyComponent
                    ? "The legacy ColorID components will be removed."
                    : "The legacy ColorID components will be kept.")
                + "\n\nThe full plan is in the Console. This edits content assets — commit or stash first.",
                "Migrate", "Cancel");

            if (!proceed) return;

            var result = ColorContentMigration.Apply(plan);
            Debug.Log($"[ColorTheme] {result}");
            foreach (string failure in result.Failures) Debug.LogError($"[ColorTheme] {failure}");
        }

        private static ColorContentMigrationPlan BuildPlan(ColorContentMigrationOptions options)
        {
            var snapshot = ColorThemeAuditService.Run(ColorThemeAuditRequest.Default);
            return ColorContentMigration.Plan(snapshot, options);
        }

        #region CLI

        /// <summary>
        /// Headless preview. Reads <c>-molcaMigrationBatch &lt;n&gt;</c>; previews all prefabs without it.
        /// </summary>
        /// <remarks>
        /// Always exits 0 on a valid plan, whatever it contains. Blocked sites are the expected steady
        /// state during the compatibility window, not a build failure.
        /// </remarks>
        public static void PreviewFromCli()
        {
            var options = OptionsFromCommandLine(out string description);
            var plan = BuildPlan(options);

            Debug.Log($"[ColorTheme] {description}\n{plan.ToPreview()}");
            EditorApplication.Exit(plan.IsValid ? 0 : 1);
        }

        /// <summary>Headless apply. Same arguments as <see cref="PreviewFromCli"/>.</summary>
        public static void ApplyFromCli()
        {
            var options = OptionsFromCommandLine(out string description);
            var plan = BuildPlan(options);

            Debug.Log($"[ColorTheme] {description}\n{plan.ToPreview()}");

            if (!plan.IsValid)
            {
                Debug.LogError("[ColorTheme] The migration plan is not valid; nothing was written.");
                EditorApplication.Exit(1);
                return;
            }

            var result = ColorContentMigration.Apply(plan);
            Debug.Log($"[ColorTheme] {result}");

            foreach (string failure in result.Failures) Debug.LogError($"[ColorTheme] {failure}");

            EditorApplication.Exit(result.Applied && result.Failures.Count == 0 ? 0 : 1);
        }

        private static ColorContentMigrationOptions OptionsFromCommandLine(out string description)
        {
            var args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "-molcaMigrationBatch") continue;
                if (!int.TryParse(args[i + 1], out int number)) break;

                var batch = ColorMigrationBatches.Find(number);
                if (batch == null) break;

                description = batch.ToString();
                return batch.Options;
            }

            description = "All prefabs";
            return ColorContentMigrationOptions.AllPrefabs;
        }

        #endregion
    }
}
#endif
