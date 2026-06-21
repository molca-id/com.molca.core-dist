using System.Linq;
using Molca.Editor;
using Molca.Sequence;
using Molca.Sequence.Auxiliary;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Mcp.Providers
{
    /// <summary>
    /// Codegen Action tools that author brand-new <see cref="Step"/> / <see cref="StepAuxiliary"/> C#
    /// subclasses — the capability the runtime-instantiation tools lack (they can only place types that
    /// already compile). Each writes a convention-following stub via
    /// <see cref="StepScriptGenerationService"/>, then imports it. The new type is only instantiable
    /// after the ensuing domain reload, so the result flags <c>requiresDomainReload</c>: the caller must
    /// wait for recompilation before referencing the type in an add-step/add-auxiliary call.
    /// </summary>
    /// <remarks>
    /// Marked <see cref="McpToolReversibility.Irreversible"/> from the undo system's perspective — a new
    /// source file is not on Unity's Undo stack nor a scene snapshot; revert by deleting the file.
    /// </remarks>
    public partial class CoreMcpToolProvider
    {
        // ── molca_sequence_create_step_script ────────────────────────────────────────────────

        private static McpToolDefinition CreateStepScriptTool() => new McpToolDefinition(
            name: "molca_sequence_create_step_script",
            description: "Creates a new Step subclass C# script (name must end in 'Step') from a "
                       + "convention-following template, so a step type that does not yet exist can be "
                       + "introduced. Optional 'folder' (defaults to the project Steps folder) and "
                       + "'namespace'. The new type is only usable AFTER Unity recompiles "
                       + "(requiresDomainReload=true) — wait for the reload before referencing it in "
                       + "molca_sequence_add_steps. Revert by deleting the file.",
            inputSchemaJson: CodegenSchema,
            execute: a => ExecuteCreateScript(a, isStep: true),
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        // ── molca_sequence_create_auxiliary_script ───────────────────────────────────────────

        private static McpToolDefinition CreateAuxiliaryScriptTool() => new McpToolDefinition(
            name: "molca_sequence_create_auxiliary_script",
            description: "Creates a new StepAuxiliary subclass C# script (name must end in 'Auxiliary') "
                       + "from a template, with OnStepBegin/OnStepCompleted stubs. Optional 'folder' "
                       + "(defaults to the project Auxiliaries folder) and 'namespace'. The new type is "
                       + "only usable AFTER Unity recompiles (requiresDomainReload=true). Revert by "
                       + "deleting the file.",
            inputSchemaJson: CodegenSchema,
            execute: a => ExecuteCreateScript(a, isStep: false),
            mode: McpToolMode.Edit,
            kind: McpToolKind.Action,
            reversibility: McpToolReversibility.Irreversible);

        private const string CodegenSchema =
            "{\"type\":\"object\",\"properties\":{" +
            "\"typeName\":{\"type\":\"string\",\"description\":\"Class name; must end in 'Step' or 'Auxiliary'.\"}," +
            "\"folder\":{\"type\":\"string\",\"description\":\"Project-relative target folder; omit for the default.\"}," +
            "\"namespace\":{\"type\":\"string\",\"description\":\"Optional namespace to wrap the class in.\"}}," +
            "\"required\":[\"typeName\"],\"additionalProperties\":false}";

        private static string ExecuteCreateScript(string argumentsJson, bool isStep)
        {
            var args = ParseArgs(argumentsJson);
            var typeName = args.Value<string>("typeName");
            var folder = args.Value<string>("folder");
            var ns = args.Value<string>("namespace");

            var result = isStep
                ? StepScriptGenerationService.CreateStepScript(typeName, folder, ns, StepTypeExists)
                : StepScriptGenerationService.CreateAuxiliaryScript(typeName, folder, ns, AuxiliaryTypeExists);

            if (result.Error != null) return Error(result.Error);

            // Import so the new script enters the compile pipeline; the resulting domain reload is what
            // makes the type instantiable, hence the explicit caller warning.
            AssetDatabase.ImportAsset(result.Path, ImportAssetOptions.ForceSynchronousImport);

            return new JObject
            {
                ["path"] = result.Path,
                ["typeName"] = typeName,
                ["requiresDomainReload"] = true,
                ["note"] = "Script created. Wait for Unity to recompile before referencing this type in an "
                         + "add-step/add-auxiliary call."
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool StepTypeExists(string name) =>
            TypeCache.GetTypesDerivedFrom<Step>().Append(typeof(Step)).Any(t => t.Name == name);

        private static bool AuxiliaryTypeExists(string name) =>
            TypeCache.GetTypesDerivedFrom<StepAuxiliary>().Any(t => t.Name == name);
    }
}
