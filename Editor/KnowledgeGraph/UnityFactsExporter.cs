using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Molca.Settings;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.KnowledgeGraph
{
    /// <summary>
    /// Exports Unity-specific project facts that graphify cannot derive from raw source files — the
    /// <see cref="AssetDatabase"/> dependency graph (prefab → ScriptableObject → scene → material …) and
    /// the <see cref="TypeCache"/> type graph of framework extension points (subsystems, settings modules,
    /// steps, auxiliaries) with their <c>[DependsOn]</c> wiring — as plain markdown into
    /// <see cref="GraphifyCli.CorpusDir"/>. graphify then ingests that corpus alongside the code and docs,
    /// so the knowledge graph understands Unity wiring, not just C# text.
    /// </summary>
    /// <remarks>
    /// Output is deliberately markdown with explicit relationship prose ("inherits from", "depends on",
    /// "references") so graphify's extractor turns each fact into a typed edge. Read-only: this only reads
    /// the project and writes into the corpus folder; it never touches assets.
    /// </remarks>
    public static class UnityFactsExporter
    {
        /// <summary>Summary of an export run, for surfacing in tool results / the settings UI.</summary>
        public struct ExportSummary
        {
            /// <summary>Corpus directory written to.</summary>
            public string CorpusDir;
            /// <summary>Number of types described in the type graph.</summary>
            public int TypeCount;
            /// <summary>Number of assets described in the dependency graph.</summary>
            public int AssetCount;
            /// <summary>Markdown files written.</summary>
            public string[] Files;
        }

        /// <summary>
        /// Writes the full Unity facts corpus (type graph + asset dependency graph) and returns a summary.
        /// </summary>
        public static ExportSummary ExportAll()
        {
            var dir = GraphifyCli.CorpusDir;
            Directory.CreateDirectory(dir);

            var typesFile = Path.Combine(dir, "molca-types.md");
            var assetsFile = Path.Combine(dir, "molca-assets.md");

            int typeCount = WriteTypeGraph(typesFile);
            int assetCount = WriteAssetGraph(assetsFile);

            return new ExportSummary
            {
                CorpusDir = dir,
                TypeCount = typeCount,
                AssetCount = assetCount,
                Files = new[] { typesFile, assetsFile }
            };
        }

        // --- type graph ------------------------------------------------------------------------------

        private static int WriteTypeGraph(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Molca Framework Type Graph");
            sb.AppendLine();
            sb.AppendLine("Framework extension points discovered in this project and how they relate. "
                        + "Generated from TypeCache; do not edit by hand.");
            sb.AppendLine();

            int count = 0;
            count += WriteTypeSection(sb, "RuntimeSubsystems", TypeCache.GetTypesDerivedFrom<RuntimeSubsystem>(),
                describeSubsystem: true);
            count += WriteTypeSection(sb, "Settings Modules", TypeCache.GetTypesDerivedFrom<SettingModule>());
            count += InvokeContributors(sb);

            File.WriteAllText(path, sb.ToString());
            return count;
        }

        /// <summary>
        /// Discovers every <see cref="IKnowledgeGraphContributor"/> implementor via <c>TypeCache</c> and
        /// lets each append its own type sections (fork/add-on extension point, mirrors
        /// <see cref="FrameworkGraph.IFrameworkGraphContributor"/>). Test assemblies are skipped so test
        /// fixtures can't pollute the corpus; each contributor is wrapped in try/catch so one faulting
        /// contributor cannot break the export.
        /// </summary>
        private static int InvokeContributors(StringBuilder sb)
        {
            int count = 0;
            foreach (var type in TypeCache.GetTypesDerivedFrom<IKnowledgeGraphContributor>())
            {
                if (type == null || type.IsAbstract || type.IsInterface) continue;
                if (IsTestAssembly(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    var contributor = (IKnowledgeGraphContributor)Activator.CreateInstance(type);
                    count += contributor.Contribute(sb);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca KnowledgeGraph] Contributor '{type.Name}' failed: {ex.Message}");
                }
            }
            return count;
        }

        /// <summary>
        /// Appends a markdown section listing <paramref name="types"/> under <paramref name="heading"/> —
        /// one entry per concrete, non-test type with its full name, defining assembly, base type,
        /// <c>[DependsOn]</c> edges (when <paramref name="describeSubsystem"/>), and asset-menu wiring.
        /// Exposed so an <see cref="IKnowledgeGraphContributor"/> can format its own sections identically
        /// to Core's built-in ones.
        /// </summary>
        public static int WriteTypeSection(StringBuilder sb, string heading,
            IEnumerable<Type> types, bool describeSubsystem = false)
        {
            // Concrete, project-relevant types only; skip abstracts and test scaffolding (test fixtures
            // are typically nested helper types in *.Tests assemblies — they aren't real framework
            // extension points and would pollute the graph).
            var list = types
                .Where(t => t != null && !t.IsAbstract && !t.IsNested && !IsTestAssembly(t))
                .OrderBy(t => t.FullName)
                .ToList();
            sb.AppendLine($"## {heading}");
            sb.AppendLine();
            if (list.Count == 0)
            {
                sb.AppendLine("_None in this project._");
                sb.AppendLine();
                return 0;
            }

            foreach (var t in list)
            {
                sb.AppendLine($"### {t.Name}");
                sb.AppendLine();
                sb.AppendLine($"- Full type: `{t.FullName}`");
                sb.AppendLine($"- Defined in assembly: `{t.Assembly.GetName().Name}`");
                if (t.BaseType != null && t.BaseType != typeof(object))
                    sb.AppendLine($"- `{t.Name}` inherits from `{t.BaseType.Name}`.");

                // [DependsOn] edges — the real subsystem wiring, invisible to a raw-text scan.
                if (describeSubsystem)
                {
                    foreach (var attr in t.GetCustomAttributes<DependsOnAttribute>(inherit: true))
                        foreach (var dep in attr.Dependencies)
                            if (dep != null)
                                sb.AppendLine($"- `{t.Name}` depends on subsystem `{dep.Name}`.");
                }

                // [CreateAssetMenu] tells authors how the asset is created — useful "where configured" prose.
                var menu = t.GetCustomAttribute<CreateAssetMenuAttribute>();
                if (menu != null)
                    sb.AppendLine($"- Authored as an asset via menu: `{menu.menuName}`.");

                sb.AppendLine();
            }

            return list.Count;
        }

        /// <summary>True if the type lives in a test assembly (name ends with <c>.Tests</c>/<c>Tests</c>).</summary>
        private static bool IsTestAssembly(Type t)
        {
            var asm = t.Assembly.GetName().Name;
            return asm != null && (asm.EndsWith(".Tests", StringComparison.Ordinal)
                                   || asm.EndsWith("Tests", StringComparison.Ordinal));
        }

        // --- asset dependency graph ------------------------------------------------------------------

        private static int WriteAssetGraph(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Molca Project Asset Dependency Graph");
            sb.AppendLine();
            sb.AppendLine("Prefabs, ScriptableObjects, and scenes under `Assets/` and the assets each one "
                        + "references. Generated from AssetDatabase; do not edit by hand.");
            sb.AppendLine();

            // Prefabs, scenes, and ScriptableObjects are the meaningful wiring carriers; raw textures/audio
            // are excluded as leaves to keep the corpus focused.
            var guids = AssetDatabase.FindAssets("t:Prefab t:Scene t:ScriptableObject", new[] { "Assets" });
            var paths = guids.Select(AssetDatabase.GUIDToAssetPath)
                             .Where(p => !string.IsNullOrEmpty(p))
                             .Distinct()
                             .OrderBy(p => p)
                             .ToList();

            int count = 0;
            foreach (var assetPath in paths)
            {
                var name = Path.GetFileNameWithoutExtension(assetPath);
                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

                sb.AppendLine($"### {name}");
                sb.AppendLine();
                sb.AppendLine($"- Asset path: `{assetPath}`");
                if (type != null) sb.AppendLine($"- Asset type: `{type.Name}`");

                // Direct (non-recursive) dependencies, restricted to project assets, self excluded.
                var deps = AssetDatabase.GetDependencies(assetPath, recursive: false)
                    .Where(d => d != assetPath && d.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d)
                    .ToList();

                foreach (var dep in deps)
                    sb.AppendLine($"- `{name}` references `{Path.GetFileNameWithoutExtension(dep)}` (`{dep}`).");

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(path, sb.ToString());
            return count;
        }
    }
}
