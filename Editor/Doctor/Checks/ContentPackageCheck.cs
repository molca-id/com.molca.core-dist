using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Molca.ContentPackage;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Validates every <see cref="ContentPackageSettings"/> asset's package configs: package
    /// ids must be present and unique, display names present, and dependencies must reference
    /// existing packages without forming a cycle.
    /// </summary>
    /// <remarks>
    /// <see cref="ContentPackageSettings.ValidateConfigurations"/> covers missing ids/names and
    /// empty label lists; this check focuses on the relational errors it does not — duplicate
    /// ids and broken/circular dependency graphs — which silently break install resolution.
    /// </remarks>
    public class ContentPackageCheck : IDoctorCheck
    {
        public string Id => "content-package-valid";
        public string Description => "Content package ids are unique and dependencies resolve without cycles";

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.MainThreadAsync();
            var issues = new List<DoctorIssue>();

            foreach (var guid in AssetDatabase.FindAssets("t:ContentPackageSettings"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<ContentPackageSettings>(path);
                if (settings == null)
                    continue;

                var configs = settings.packageConfigs;
                if (configs == null)
                    continue;

                // Build the id set first so dependency resolution and cycle detection can use it.
                var ids = new HashSet<string>();
                var seen = new HashSet<string>();
                foreach (var config in configs)
                {
                    if (config == null)
                        continue;

                    if (string.IsNullOrEmpty(config.packageId))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            "A content package config has an empty packageId — it cannot be resolved by GetPackageConfig.", path));
                        continue;
                    }

                    if (!seen.Add(config.packageId))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                            $"Duplicate content package id \"{config.packageId}\" — GetPackageConfig would always return the first match.", path));
                    }

                    ids.Add(config.packageId);

                    if (string.IsNullOrEmpty(config.displayName))
                    {
                        issues.Add(new DoctorIssue(Id, DoctorSeverity.Warning,
                            $"Content package \"{config.packageId}\" has no display name.", path));
                    }
                }

                // Dependency resolution: every referenced id must exist.
                foreach (var config in configs)
                {
                    if (config?.dependencies == null || string.IsNullOrEmpty(config.packageId))
                        continue;

                    foreach (var dep in config.dependencies)
                    {
                        if (dep == null || string.IsNullOrEmpty(dep.packageId))
                        {
                            issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                                $"Content package \"{config.packageId}\" has a dependency with an empty packageId.", path));
                        }
                        else if (!ids.Contains(dep.packageId))
                        {
                            issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                                $"Content package \"{config.packageId}\" depends on \"{dep.packageId}\", which is not defined in this asset.", path));
                        }
                    }
                }

                foreach (var cycle in FindCycles(configs))
                {
                    issues.Add(new DoctorIssue(Id, DoctorSeverity.Error,
                        $"Circular content-package dependency: {cycle}.", path));
                }
            }

            return issues;
        }

        // Reports each distinct dependency cycle as "a → b → a". Only edges to defined
        // packages are followed, so this complements the unresolved-dependency findings.
        private static IEnumerable<string> FindCycles(List<ContentPackageSettings.PackageConfig> configs)
        {
            var graph = new Dictionary<string, List<string>>();
            foreach (var config in configs)
            {
                if (config == null || string.IsNullOrEmpty(config.packageId))
                    continue;
                var edges = new List<string>();
                if (config.dependencies != null)
                {
                    foreach (var dep in config.dependencies)
                    {
                        if (dep != null && !string.IsNullOrEmpty(dep.packageId))
                            edges.Add(dep.packageId);
                    }
                }
                graph[config.packageId] = edges;
            }

            var reported = new HashSet<string>();
            var results = new List<string>();
            var state = new Dictionary<string, int>(); // 0 = unvisited, 1 = on stack, 2 = done
            var stack = new List<string>();

            void Visit(string node)
            {
                state[node] = 1;
                stack.Add(node);
                if (graph.TryGetValue(node, out var edges))
                {
                    foreach (var next in edges)
                    {
                        if (!graph.ContainsKey(next))
                            continue; // unresolved edge — reported separately
                        state.TryGetValue(next, out var s);
                        if (s == 0)
                        {
                            Visit(next);
                        }
                        else if (s == 1)
                        {
                            // Found a back-edge: extract the cycle from the stack.
                            int start = stack.IndexOf(next);
                            var path = stack.GetRange(start, stack.Count - start);
                            path.Add(next);
                            var key = string.Join("→", path);
                            // Normalise so the same cycle isn't reported once per entry node.
                            var normalised = NormaliseCycle(path);
                            if (reported.Add(normalised))
                                results.Add(key);
                        }
                    }
                }
                stack.RemoveAt(stack.Count - 1);
                state[node] = 2;
            }

            foreach (var node in graph.Keys)
            {
                state.TryGetValue(node, out var s);
                if (s == 0)
                    Visit(node);
            }

            return results;
        }

        // Rotation-invariant key for a cycle so it is reported once regardless of entry point.
        private static string NormaliseCycle(List<string> path)
        {
            var nodes = path.GetRange(0, path.Count - 1); // drop the repeated closing node
            int min = 0;
            for (int i = 1; i < nodes.Count; i++)
            {
                if (string.CompareOrdinal(nodes[i], nodes[min]) < 0)
                    min = i;
            }
            var rotated = new List<string>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
                rotated.Add(nodes[(min + i) % nodes.Count]);
            return string.Join("→", rotated);
        }
    }
}
