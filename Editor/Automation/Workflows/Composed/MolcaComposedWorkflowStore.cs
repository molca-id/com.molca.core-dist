using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// File-backed persistence for composed workflows (Sprint 93.5): one JSON file per workflow under the
    /// consumer project (<see cref="RelativeRoot"/>), editable by hand and versionable, mirroring the
    /// assistant memory store's placement pattern. Each file stores the composition together with the
    /// facets the kernel aggregated at save time, so the command registry can declare accurate policy
    /// metadata without re-resolving members during a registry build (which would be circular).
    /// </summary>
    public static class MolcaComposedWorkflowStore
    {
        /// <summary>Project-relative root of the saved-workflow files.</summary>
        public const string RelativeRoot = "Assets/_Molca/AutomationWorkflows";

        /// <summary>Cap on stored workflows; a save beyond this is refused with a clear error.</summary>
        public const int MaxEntries = 100;

        private static readonly Regex IdShape = new Regex("^[a-z0-9][a-z0-9-.]{1,63}$", RegexOptions.Compiled);

        private static string _rootOverride;

        /// <summary>
        /// Redirects the store to a temp directory for tests, so a test never creates or deletes workflow
        /// files in the consumer project. Pass <c>null</c> to restore the default consumer-space path.
        /// </summary>
        /// <param name="absoluteDirectory">Directory to hold workflow files, or null for the default.</param>
        public static void OverrideRootForTests(string absoluteDirectory) => _rootOverride = absoluteDirectory;

        /// <summary>Absolute directory of the store.</summary>
        public static string AbsoluteRoot => _rootOverride ?? Path.Combine(
            Path.GetDirectoryName(Application.dataPath) ?? ".", RelativeRoot.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>One saved entry: the composition plus its save-time facets.</summary>
        public sealed class Entry
        {
            /// <summary>The composition.</summary>
            public MolcaComposedWorkflow Workflow { get; internal set; }

            /// <summary>The facets aggregated when the workflow was saved.</summary>
            public JObject FacetsJson { get; internal set; }

            /// <summary>When the entry was written (UTC).</summary>
            public DateTime SavedAtUtc { get; internal set; }
        }

        /// <summary>Whether <paramref name="id"/> is acceptable as a saved-workflow id (and file name).</summary>
        public static bool IsValidId(string id) => !string.IsNullOrEmpty(id) && IdShape.IsMatch(id);

        /// <summary>
        /// Saves a validated composition with its aggregated facets. The caller must have validated the
        /// workflow (<see cref="MolcaComposedWorkflowCompiler.Validate"/>) — this only persists.
        /// </summary>
        /// <param name="workflow">The composition to persist; its <see cref="MolcaComposedWorkflow.Id"/> names the file.</param>
        /// <param name="facets">The facets aggregated at validation time.</param>
        /// <param name="error">Human-readable refusal reason, or null on success.</param>
        /// <returns>True when written.</returns>
        public static bool Save(MolcaComposedWorkflow workflow, MolcaComposedFacets facets, out string error)
        {
            error = null;
            if (workflow == null || !IsValidId(workflow.Id))
            {
                error = "A saved workflow needs a kebab-case id (a–z, 0–9, '-', '.', 2–64 chars).";
                return false;
            }

            Directory.CreateDirectory(AbsoluteRoot);
            var existing = Directory.GetFiles(AbsoluteRoot, "*.json");
            var path = PathFor(workflow.Id);
            if (existing.Length >= MaxEntries && !File.Exists(path))
            {
                error = $"The workflow store already holds {existing.Length} entries (cap {MaxEntries}). Delete one first.";
                return false;
            }

            var envelope = new JObject
            {
                ["workflow"] = workflow.ToJson(),
                ["facets"] = facets.ToJson(),
                ["savedAtUtc"] = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(path, envelope.ToString(Formatting.Indented));
            return true;
        }

        /// <summary>Deletes a saved workflow (and its .meta). Returns false when no such entry exists.</summary>
        public static bool Delete(string id) => Delete(id, out _);

        /// <summary>
        /// Deletes a saved workflow and, with it, any authorization it held.
        /// </summary>
        /// <remarks>
        /// The de-authorization is part of the delete, not a courtesy of one caller: a leftover allowlist
        /// entry is <em>invisible</em> — the Permissions rail enumerates registered commands, and a deleted
        /// workflow is no longer one — so a workflow later saved under the same id would inherit an
        /// authorization nobody granted it. Removing it narrows permissions, which is always the safe
        /// direction to fail in.
        /// </remarks>
        /// <param name="id">The saved workflow id.</param>
        /// <param name="authorizationRemoved">True when an action allowlist entry was dropped as well.</param>
        /// <returns>True when a workflow file was deleted.</returns>
        public static bool Delete(string id, out bool authorizationRemoved)
        {
            authorizationRemoved = false;
            if (!IsValidId(id)) return false;
            var path = PathFor(id);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            var meta = path + ".meta";
            if (File.Exists(meta)) File.Delete(meta);

            var settings = MolcaAutomationPolicySettings.GetOrCreateSettings();
            if (settings.ActionAllowlist.Contains(id))
            {
                settings.SetActionAllowed(id, false);
                authorizationRemoved = true;
            }
            return true;
        }

        /// <summary>Loads one saved workflow, or null when absent/malformed.</summary>
        public static Entry TryGet(string id)
        {
            if (!IsValidId(id)) return null;
            return ReadEntry(PathFor(id));
        }

        /// <summary>All saved workflows (malformed files skipped), ordered by id.</summary>
        public static IReadOnlyList<Entry> List()
        {
            if (!Directory.Exists(AbsoluteRoot)) return Array.Empty<Entry>();
            return Directory.GetFiles(AbsoluteRoot, "*.json")
                .Select(ReadEntry)
                .Where(e => e != null)
                .OrderBy(e => e.Workflow.Id, StringComparer.Ordinal)
                .ToList();
        }

        private static string PathFor(string id) => Path.Combine(AbsoluteRoot, id + ".json");

        private static Entry ReadEntry(string path)
        {
            try
            {
                var envelope = JObject.Parse(File.ReadAllText(path));
                var workflow = MolcaComposedWorkflow.FromJson(envelope["workflow"] as JObject);
                if (workflow == null || string.IsNullOrWhiteSpace(workflow.Id)) return null;
                return new Entry
                {
                    Workflow = workflow,
                    FacetsJson = envelope["facets"] as JObject ?? new JObject(),
                    SavedAtUtc = DateTime.TryParse(envelope["savedAtUtc"]?.ToString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Contributes every saved composed workflow to the kernel registry as a command (Sprint 93.5), so a
    /// saved workflow appears in Hub Automation, the CLI/Pipeline, MCP, and the Assistant with zero extra
    /// wiring — exactly like a built-in workflow. Discovered by <c>TypeCache</c>.
    /// </summary>
    /// <remarks>
    /// The command declares the facets stored at save time (policy must gate on metadata available at
    /// registry build). The body re-validates and compiles against the kernel's <i>current</i> registry at
    /// run time, so member drift since the save fails legibly (<c>compose.unknown_command</c>,
    /// <c>compose.args_invalid</c>) instead of running a stale plan. A recursion guard bounds composed
    /// workflows invoking each other.
    /// </remarks>
    public sealed class SavedWorkflowCommandProvider : MolcaCommandProvider
    {
        /// <summary>Nested-composition depth cap (A runs B runs C…); past this the step fails legibly.</summary>
        private const int MaxDepth = 8;

        [ThreadStatic] private static int _depth;

        /// <inheritdoc/>
        public override string Namespace => "molca.workflows";

        /// <inheritdoc/>
        public override string DisplayName => "Saved Composed Workflows";

        /// <inheritdoc/>
        public override IEnumerable<MolcaCommandDefinition> GetCommands()
        {
            var commands = new List<MolcaCommandDefinition>();
            foreach (var entry in MolcaComposedWorkflowStore.List())
            {
                var workflow = entry.Workflow;
                var facets = entry.FacetsJson;
                commands.Add(new MolcaCommandDefinition(
                    id: workflow.Id,
                    displayName: workflow.DisplayName ?? workflow.Id,
                    description: string.IsNullOrWhiteSpace(workflow.Description)
                        ? "Saved composed workflow."
                        : workflow.Description,
                    executeAsync: ctx => RunSavedAsync(workflow.Id, ctx),
                    category: MolcaWorkflowCommandAdapter.ComposedCategory,
                    mode: ParseEnum(facets?["mode"], MolcaCommandMode.Edit),
                    kind: ParseEnum(facets?["kind"], MolcaCommandKind.Action),
                    reversibility: ParseEnum(facets?["reversibility"], MolcaCommandReversibility.None),
                    resourceClaims: ParseClaims(facets?["resourceClaims"] as JArray),
                    supportsCancellation: true,
                    requiresConfirmation: facets?["requiresConfirmation"]?.Type == JTokenType.Boolean
                                          && (bool)facets["requiresConfirmation"]));
            }
            return commands;
        }

        private static async Awaitable<MolcaCommandResult> RunSavedAsync(string id, MolcaCommandContext context)
        {
            // Always re-read and re-validate: the saved file or the member commands may have changed
            // since the registry was built. Failing here is the honest outcome.
            var entry = MolcaComposedWorkflowStore.TryGet(id);
            if (entry == null)
                return MolcaCommandResult.Fail(id, "compose.not_found", $"Saved workflow '{id}' no longer exists.");

            var registry = MolcaAutomationKernel.Instance.Registry;
            var validation = MolcaComposedWorkflowCompiler.Validate(entry.Workflow, registry);
            if (!validation.IsValid)
                return MolcaCommandResult.Failed(id, validation.Issues, validation.ToJson());

            if (_depth >= MaxDepth)
                return MolcaCommandResult.Fail(id, "compose.too_deep",
                    $"Composed workflows nest deeper than {MaxDepth} levels — check for a cycle.");

            _depth++;
            try
            {
                var definition = MolcaComposedWorkflowCompiler.Compile(entry.Workflow, registry);
                return await MolcaWorkflowRunner.RunAsync(definition, context);
            }
            finally
            {
                _depth--;
            }
        }

        private static T ParseEnum<T>(JToken token, T fallback) where T : struct
            => token != null && Enum.TryParse(token.ToString(), out T value) ? value : fallback;

        private static IReadOnlyList<MolcaResourceClaim> ParseClaims(JArray array)
        {
            if (array == null || array.Count == 0) return null;
            var claims = new List<MolcaResourceClaim>();
            foreach (var token in array)
                if (Enum.TryParse(token.ToString(), out MolcaResourceClaim claim))
                    claims.Add(claim);
            return claims.Count > 0 ? claims : null;
        }
    }
}
