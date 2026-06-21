using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Molca.Editor.FrameworkGraph
{
    /// <summary>
    /// Editor-only persistence for Framework Graph node positions (Sprint 22.9). Positions are keyed by
    /// the stable <see cref="FrameworkGraphNode.Id"/> and stored as JSON under <c>Library/Molca/</c> — a
    /// project-local, non-asset, non-committed location, since the layout is incidental editor state, not
    /// project data. File I/O failures degrade gracefully to auto-layout; they never throw into the editor.
    /// </summary>
    public static class FrameworkGraphLayoutStore
    {
        [Serializable]
        private struct Entry { public string id; public float x; public float y; }

        [Serializable]
        private class Data { public List<Entry> nodes = new(); }

        private static string FilePath
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(root, "Library", "Molca", "FrameworkGraphLayout.json");
            }
        }

        /// <summary>Loads persisted positions keyed by node id; empty if nothing is stored.</summary>
        public static Dictionary<string, Vector2> Load()
        {
            var result = new Dictionary<string, Vector2>();
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return result;
                var data = JsonUtility.FromJson<Data>(File.ReadAllText(path));
                if (data?.nodes == null) return result;
                foreach (var e in data.nodes)
                    if (!string.IsNullOrEmpty(e.id)) result[e.id] = new Vector2(e.x, e.y);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca FrameworkGraph] Could not load layout: {ex.Message}");
            }
            return result;
        }

        /// <summary>Persists node positions, replacing any previously stored layout.</summary>
        public static void Save(IReadOnlyDictionary<string, Vector2> positionsById)
        {
            if (positionsById == null) return;
            try
            {
                var data = new Data();
                foreach (var kv in positionsById)
                    if (!string.IsNullOrEmpty(kv.Key))
                        data.nodes.Add(new Entry { id = kv.Key, x = kv.Value.x, y = kv.Value.y });

                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, JsonUtility.ToJson(data));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca FrameworkGraph] Could not save layout: {ex.Message}");
            }
        }
    }
}
