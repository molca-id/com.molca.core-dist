using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Discovers every <see cref="IDoctorCheck"/> in the project — Core's built-in checks plus any
    /// authored by an SDK layer or consumer project — and exposes them as one ordered, de-duplicated
    /// set. Mirrors <see cref="Molca.Editor.Validation.SequenceValidatorRegistry"/> and the MCP tool
    /// registry: implementations are found by <c>TypeCache</c>, instantiated once via their public
    /// parameterless constructor, and a duplicate <see cref="IDoctorCheck.Id"/> is rejected loudly
    /// rather than silently shadowing another check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the extension point for Doctor: a fork or project adds a check simply by implementing
    /// <see cref="IDoctorCheck"/> (with a public parameterless constructor and a unique, kebab-case
    /// <see cref="IDoctorCheck.Id"/>) in an Editor assembly. No Core edit is required — the check is
    /// discovered automatically and appears in the Doctor window, <c>RunCI</c>, and the MCP tools.
    /// </para>
    /// <para>
    /// Ordering is deterministic: Core's built-in checks keep their curated order (see
    /// <see cref="BuiltInOrder"/>); every other discovered check follows, sorted by <c>Id</c>
    /// (ordinal). Checks are side-effect free and independent, so order only affects report/UI
    /// grouping, never results.
    /// </para>
    /// Results are cached after first discovery. Call <see cref="Reset"/> in a test teardown when a
    /// test-only check type should not leak into subsequent runs.
    /// </remarks>
    public static class DoctorCheckRegistry
    {
        /// <summary>
        /// Curated execution order for Core's built-in checks, by <see cref="IDoctorCheck.Id"/>.
        /// Discovered checks not listed here follow, sorted by id. Kept as ids (not types) so the
        /// ordering survives a check being moved between assemblies.
        /// </summary>
        public static IReadOnlyList<string> BuiltInOrder { get; } = new[]
        {
            "static-singleton-usage",
            "runtime-so-write",
            "missing-finish-callback",
            "inject-unresolvable",
            "unresolvable-scene-reference",
            "build-scenes-valid",
            "color-id-reference-invalid",
            "color-id-reference-early-access",
            "dynamic-localization-locale-invalid",
            "dynamic-localization-init-contract",
            "version-settings-valid",
            "build-profile-valid",
            "content-package-valid",
            "design-language",
            "http-hardcoded-url",
            "http-response-success-misuse",
            "http-unredacted-logging",
            "dataprovider-lifetime-token",
            "sequence-validation",
            // Scene-performance audit (Sprint 50) — ids prefixed "scene-".
            "scene-structure",
            "scene-polygon-budget",
            "scene-texture-budget",
            "scene-instancing-budget",
            "scene-lighting-budget",
            "scene-subsystem-placement",
            // Convention-enforcement tooling (Sprint 86) — turns the naming/async/SO
            // rules from the docs into checks so this bug class can't ship again.
            "unity-lifecycle-wrong-type",
            "async-void-non-entrypoint",
            "awaitable-missing-cancellation-token",
            "runtime-so-collection-write",
            "task-returning-public-api",
        };

        private static List<IDoctorCheck> _checks;
        private static readonly List<string> _errors = new();

        /// <summary>The discovered checks, in execution order (see the class remarks).</summary>
        public static IReadOnlyList<IDoctorCheck> Checks
        {
            get
            {
                EnsureDiscovered();
                return _checks;
            }
        }

        /// <summary>Discovery issues (duplicate ids, uninstantiable types, empty ids); empty when clean.</summary>
        public static IReadOnlyList<string> Errors
        {
            get
            {
                EnsureDiscovered();
                return _errors;
            }
        }

        /// <summary>Clears the discovery cache so the next access re-scans. Intended for tests.</summary>
        public static void Reset()
        {
            _checks = null;
            _errors.Clear();
        }

        private static void EnsureDiscovered()
        {
            if (_checks != null) return;

            var instances = new List<IDoctorCheck>();
            var errors = new List<string>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IDoctorCheck>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"Check '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }

                try
                {
                    instances.Add((IDoctorCheck)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    errors.Add($"Check '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }

            _checks = BuildChecks(instances, errors);
            _errors.Clear();
            _errors.AddRange(errors);
            if (_errors.Count > 0)
                Debug.LogWarning($"[DoctorCheckRegistry] discovery issues:\n - {string.Join("\n - ", _errors)}");
        }

        /// <summary>
        /// De-duplicates check instances by <see cref="IDoctorCheck.Id"/> (first wins; the rest recorded
        /// in <paramref name="errors"/>), drops empty ids, and orders the survivors: built-in checks in
        /// <see cref="BuiltInOrder"/> first, then the remainder by id (ordinal). Exposed for tests so the
        /// dedup/ordering contract can be exercised without <c>TypeCache</c>.
        /// </summary>
        /// <param name="candidates">Candidate check instances.</param>
        /// <param name="errors">Accumulates skip reasons (duplicate/empty id); may be pre-populated.</param>
        /// <returns>The accepted checks, in execution order.</returns>
        internal static List<IDoctorCheck> BuildChecks(
            IEnumerable<IDoctorCheck> candidates, List<string> errors)
        {
            var accepted = new List<IDoctorCheck>();
            var seenIds = new Dictionary<string, IDoctorCheck>();

            foreach (var instance in candidates)
            {
                if (instance == null) continue;
                if (string.IsNullOrWhiteSpace(instance.Id))
                {
                    errors.Add($"Check '{instance.GetType().FullName}' has an empty Id; skipped.");
                    continue;
                }
                if (seenIds.TryGetValue(instance.Id, out var existing))
                {
                    errors.Add($"Duplicate check Id '{instance.Id}' on '{instance.GetType().FullName}' "
                               + $"(already used by '{existing.GetType().FullName}'); skipped.");
                    continue;
                }

                seenIds[instance.Id] = instance;
                accepted.Add(instance);
            }

            // Built-in checks keep their curated order; everything else follows, sorted by id. A large
            // sentinel keeps unlisted checks after the curated block regardless of BuiltInOrder length.
            var order = new Dictionary<string, int>();
            for (int i = 0; i < BuiltInOrder.Count; i++)
                order[BuiltInOrder[i]] = i;

            return accepted
                .OrderBy(c => order.TryGetValue(c.Id, out var idx) ? idx : int.MaxValue)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
        }
    }
}
