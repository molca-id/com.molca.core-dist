using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Discovers and indexes the registered <see cref="ISceneFix"/> implementations (Sprint 55), mirroring
    /// the Sprints 38/41 <c>SequenceFixRegistry</c>: every parameterless <see cref="ISceneFix"/> found by
    /// <see cref="TypeCache"/> is instantiated once and indexed by <see cref="ISceneFix.Id"/> and by the
    /// <see cref="ISceneFix.HandledCheckId"/> it remediates. Duplicate ids are rejected with a warning.
    /// </summary>
    public static class SceneFixRegistry
    {
        private static Dictionary<string, ISceneFix> _byId;
        private static Dictionary<string, List<ISceneFix>> _byCheckId;

        /// <summary>All registered scene fixes, ordered by id.</summary>
        public static IReadOnlyList<ISceneFix> All
        {
            get { EnsureBuilt(); return _byId.Values.OrderBy(f => f.Id, StringComparer.Ordinal).ToList(); }
        }

        /// <summary>Returns the fix with the given id, or <c>null</c>.</summary>
        public static ISceneFix ById(string id)
        {
            EnsureBuilt();
            return !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var fix) ? fix : null;
        }

        /// <summary>Returns the fixes that remediate findings from the given scene check id (never null).</summary>
        public static IReadOnlyList<ISceneFix> ForCheck(string checkId)
        {
            EnsureBuilt();
            return !string.IsNullOrEmpty(checkId) && _byCheckId.TryGetValue(checkId, out var list)
                ? list
                : Array.Empty<ISceneFix>();
        }

        private static void EnsureBuilt()
        {
            if (_byId != null) return;
            _byId = new Dictionary<string, ISceneFix>(StringComparer.Ordinal);
            _byCheckId = new Dictionary<string, List<ISceneFix>>(StringComparer.Ordinal);

            foreach (var type in TypeCache.GetTypesDerivedFrom<ISceneFix>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                ISceneFix fix;
                try { fix = (ISceneFix)Activator.CreateInstance(type); }
                catch (Exception e) { Debug.LogWarning($"[SceneFixRegistry] Could not instantiate {type.Name}: {e.Message}"); continue; }

                if (string.IsNullOrEmpty(fix.Id) || _byId.ContainsKey(fix.Id))
                {
                    Debug.LogWarning($"[SceneFixRegistry] Skipping fix with missing/duplicate id '{fix.Id}' ({type.Name}).");
                    continue;
                }

                _byId[fix.Id] = fix;
                if (!_byCheckId.TryGetValue(fix.HandledCheckId, out var list))
                    _byCheckId[fix.HandledCheckId] = list = new List<ISceneFix>();
                list.Add(fix);
            }
        }
    }
}
