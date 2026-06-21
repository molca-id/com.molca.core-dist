using System;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// What a pinned <see cref="AssistantContextItem"/> represents (Sprint 24.3). Each kind resolves to a
    /// section of the prompt's context block; <see cref="AssistantEditorContext"/> owns the rendering.
    /// </summary>
    public enum AssistantContextKind
    {
        /// <summary>The current editor selection.</summary>
        Selection,
        /// <summary>The active scene (name, path, play/edit mode).</summary>
        ActiveScene,
        /// <summary>A specific asset or script, pinned by object reference.</summary>
        Asset,
        /// <summary>A compact snapshot of the live Framework Graph (subsystems/services/references).</summary>
        FrameworkGraph,
        /// <summary>The knowledge-graph build status (graphify installed / graph built).</summary>
        KgStatus
    }

    /// <summary>
    /// One piece of context the user has explicitly pinned for a turn (Sprint 24.3). Replaces the old
    /// always-on auto-injection: nothing reaches the model unless it is pinned. Serializable so the pinned
    /// set survives a domain reload alongside the transcript (Sprint 24.5).
    /// </summary>
    /// <remarks>
    /// A <see cref="AssistantContextKind.Selection"/> item may be <see cref="Live"/> (re-resolved at send)
    /// or a snapshot captured at pin time (<see cref="Snapshot"/>). <see cref="AssistantContextKind.Asset"/>
    /// items reference the asset by <see cref="AssetGuid"/> so the reference is stable across reloads.
    /// </remarks>
    [Serializable]
    public sealed class AssistantContextItem
    {
        /// <summary>What this item represents.</summary>
        public AssistantContextKind Kind;

        /// <summary>For <see cref="AssistantContextKind.Selection"/>: re-resolve at send vs. use <see cref="Snapshot"/>.</summary>
        public bool Live;

        /// <summary>For <see cref="AssistantContextKind.Asset"/>: the pinned asset's GUID.</summary>
        public string AssetGuid;

        /// <summary>Captured text for snapshot items (a non-live Selection). Empty for live/resolved kinds.</summary>
        public string Snapshot;

        /// <summary>Cached chip label captured at pin time (e.g. the asset or scene name).</summary>
        public string DisplayLabel;

        /// <summary>Pins the current selection, either live (re-resolved at send) or as a one-time snapshot.</summary>
        public static AssistantContextItem ForSelection(bool live, string snapshot, string label)
            => new AssistantContextItem
            {
                Kind = AssistantContextKind.Selection,
                Live = live,
                Snapshot = live ? string.Empty : (snapshot ?? string.Empty),
                DisplayLabel = label
            };

        /// <summary>Pins the active scene.</summary>
        public static AssistantContextItem ForActiveScene(string label)
            => new AssistantContextItem { Kind = AssistantContextKind.ActiveScene, DisplayLabel = label };

        /// <summary>Pins a specific asset by GUID.</summary>
        public static AssistantContextItem ForAsset(string assetGuid, string label)
            => new AssistantContextItem { Kind = AssistantContextKind.Asset, AssetGuid = assetGuid, DisplayLabel = label };

        /// <summary>Pins a compact Framework Graph snapshot.</summary>
        public static AssistantContextItem ForFrameworkGraph()
            => new AssistantContextItem { Kind = AssistantContextKind.FrameworkGraph, DisplayLabel = "Framework Graph" };

        /// <summary>Pins the knowledge-graph status.</summary>
        public static AssistantContextItem ForKgStatus()
            => new AssistantContextItem { Kind = AssistantContextKind.KgStatus, DisplayLabel = "KG status" };

        /// <summary>A short, human-readable chip label for the context bar.</summary>
        public string ChipLabel => Kind switch
        {
            AssistantContextKind.Selection => Live ? "Selection (live)" : (string.IsNullOrEmpty(DisplayLabel) ? "Selection" : DisplayLabel),
            AssistantContextKind.ActiveScene => string.IsNullOrEmpty(DisplayLabel) ? "Active Scene" : DisplayLabel,
            AssistantContextKind.Asset => string.IsNullOrEmpty(DisplayLabel) ? "Asset" : DisplayLabel,
            AssistantContextKind.FrameworkGraph => "Framework Graph",
            AssistantContextKind.KgStatus => "KG status",
            _ => Kind.ToString()
        };
    }
}
