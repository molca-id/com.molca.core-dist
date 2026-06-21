using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Molca.Editor
{
    /// <summary>
    /// Generates new <c>Step</c> / <c>StepAuxiliary</c> C# subclass stubs into the project from a
    /// convention-following template, so an assistant can introduce a brand-new step/auxiliary type that
    /// does not yet exist (the one thing the runtime-instantiation authoring tools cannot do — they can
    /// only place types that already compile).
    /// </summary>
    /// <remarks>
    /// Source generation is GUI- and AssetDatabase-free here (pure string building + file write) so it is
    /// unit-testable; the MCP tool layer is responsible for importing the asset and warning that the new
    /// type only becomes instantiable after the ensuing domain reload completes.
    /// </remarks>
    public static class StepScriptGenerationService
    {
        /// <summary>Default folder for generated <c>Step</c> subclasses (per the framework folder model).</summary>
        public const string DefaultStepFolder = "Assets/YourProject/Scripts/Steps";

        /// <summary>Default folder for generated <c>StepAuxiliary</c> subclasses.</summary>
        public const string DefaultAuxiliaryFolder = "Assets/YourProject/Scripts/Auxiliaries";

        /// <summary>Outcome of a generation request.</summary>
        public readonly struct GenerationResult
        {
            /// <summary>Project-relative path of the written file, or null on failure.</summary>
            public string Path { get; }

            /// <summary>Failure reason, or null on success.</summary>
            public string Error { get; }

            private GenerationResult(string path, string error) { Path = path; Error = error; }

            internal static GenerationResult Ok(string path) => new GenerationResult(path, null);
            internal static GenerationResult Fail(string error) => new GenerationResult(null, error);
        }

        /// <summary>
        /// Writes a new <c>Step</c> subclass named <paramref name="typeName"/> into <paramref name="folder"/>.
        /// </summary>
        /// <param name="typeName">Desired class name; must end in "Step" by convention and be a valid identifier.</param>
        /// <param name="folder">Target project folder; defaults to <see cref="DefaultStepFolder"/>.</param>
        /// <param name="namespaceName">Optional namespace to wrap the class in.</param>
        /// <param name="typeExists">Predicate reporting whether a type of this simple name already exists.</param>
        public static GenerationResult CreateStepScript(
            string typeName, string folder, string namespaceName, Func<string, bool> typeExists)
            => Create(typeName, "Step", folder ?? DefaultStepFolder, namespaceName,
                      BuildStepScript, typeExists);

        /// <summary>
        /// Writes a new <c>StepAuxiliary</c> subclass named <paramref name="typeName"/> into
        /// <paramref name="folder"/>.
        /// </summary>
        /// <param name="typeName">Desired class name; must end in "Auxiliary" by convention.</param>
        /// <param name="folder">Target project folder; defaults to <see cref="DefaultAuxiliaryFolder"/>.</param>
        /// <param name="namespaceName">Optional namespace to wrap the class in.</param>
        /// <param name="typeExists">Predicate reporting whether a type of this simple name already exists.</param>
        public static GenerationResult CreateAuxiliaryScript(
            string typeName, string folder, string namespaceName, Func<string, bool> typeExists)
            => Create(typeName, "Auxiliary", folder ?? DefaultAuxiliaryFolder, namespaceName,
                      BuildAuxiliaryScript, typeExists);

        private static GenerationResult Create(
            string typeName, string suffix, string folder, string namespaceName,
            Func<string, string, string> build, Func<string, bool> typeExists)
        {
            if (!IsValidIdentifier(typeName))
                return GenerationResult.Fail($"'{typeName}' is not a valid C# type name.");
            if (!typeName.EndsWith(suffix, StringComparison.Ordinal))
                return GenerationResult.Fail($"By convention the name must end in '{suffix}' (e.g. 'OpenValve{suffix}').");
            if (typeExists != null && typeExists(typeName))
                return GenerationResult.Fail($"A type named '{typeName}' already exists.");

            var path = $"{folder.TrimEnd('/')}/{typeName}.cs";
            if (File.Exists(path))
                return GenerationResult.Fail($"A file already exists at '{path}'.");

            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, build(typeName, namespaceName));
            }
            catch (Exception e)
            {
                return GenerationResult.Fail($"Could not write '{path}': {e.Message}");
            }
            return GenerationResult.Ok(path);
        }

        /// <summary>Builds the source for a new <c>Step</c> subclass.</summary>
        public static string BuildStepScript(string typeName, string namespaceName)
        {
            var body =
                "/// <summary>\n" +
                $"/// TODO: describe what {typeName} does and when it completes.\n" +
                "/// </summary>\n" +
                $"public class {typeName} : Step\n" +
                "{\n" +
                "    // Add [SerializeField] configuration fields here, then set them with\n" +
                "    // molca_sequence_set_step_fields. Override the Step lifecycle hooks you need.\n" +
                "}\n";
            return Wrap("using Molca.Sequence;\nusing UnityEngine;\n", namespaceName, body);
        }

        /// <summary>Builds the source for a new <c>StepAuxiliary</c> subclass.</summary>
        public static string BuildAuxiliaryScript(string typeName, string namespaceName)
        {
            var body =
                "/// <summary>\n" +
                $"/// TODO: describe the side effect {typeName} performs around its owning step.\n" +
                "/// </summary>\n" +
                "[System.Serializable]\n" +
                $"public class {typeName} : StepAuxiliary\n" +
                "{\n" +
                "    // Add [SerializeField] configuration fields here, then set them with\n" +
                "    // molca_sequence_set_auxiliary_fields.\n\n" +
                "    /// <inheritdoc/>\n" +
                "    public override void OnStepBegin() { }\n\n" +
                "    /// <inheritdoc/>\n" +
                "    public override void OnStepCompleted() { }\n" +
                "}\n";
            return Wrap("using Molca.Sequence.Auxiliary;\nusing UnityEngine;\n", namespaceName, body);
        }

        private static string Wrap(string usings, string namespaceName, string body)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
                return usings + "\n" + body;

            var indented = "    " + body.TrimEnd('\n').Replace("\n", "\n    ");
            return usings + "\n" + $"namespace {namespaceName}\n{{\n" + indented + "\n}\n";
        }

        private static bool IsValidIdentifier(string name) =>
            !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");
    }
}
