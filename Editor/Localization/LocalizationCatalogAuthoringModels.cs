using System;
using System.Collections.Generic;

namespace Molca.Editor
{
    /// <summary>One stable catalog cell rendered by Hub, MCP, or export.</summary>
    public sealed class LocalizationCatalogCell
    {
        internal LocalizationCatalogCell(
            string collectionId,
            string collectionName,
            long entryId,
            string key,
            string localeCode,
            string value,
            bool isSmart,
            string tableAssetPath,
            bool isReadOnly)
        {
            CollectionId = collectionId;
            CollectionName = collectionName;
            EntryId = entryId;
            Key = key;
            LocaleCode = localeCode;
            Value = value ?? string.Empty;
            IsSmart = isSmart;
            TableAssetPath = tableAssetPath;
            IsReadOnly = isReadOnly;
        }

        public string CollectionId { get; }
        public string CollectionName { get; }
        public long EntryId { get; }
        public string Key { get; }
        public string LocaleCode { get; }
        public string Value { get; }
        public bool IsMissing => string.IsNullOrEmpty(Value);
        public bool IsSmart { get; }
        public string TableAssetPath { get; }
        public bool IsReadOnly { get; }
    }

    /// <summary>Deterministic read-only view of every Unity StringTable catalog cell.</summary>
    public sealed class LocalizationCatalogSnapshot
    {
        internal LocalizationCatalogSnapshot(
            string sourceFingerprint,
            IReadOnlyList<LocalizationCatalogCell> cells,
            IReadOnlyList<string> warnings)
        {
            SnapshotId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
            Cells = cells;
            Warnings = warnings;
        }

        public string SnapshotId { get; }
        public DateTime CreatedAtUtc { get; }
        public string SourceFingerprint { get; }
        public IReadOnlyList<LocalizationCatalogCell> Cells { get; }
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>Immutable preview of one stable catalog-cell edit.</summary>
    public sealed class LocalizationCatalogEditPlan
    {
        private readonly List<string> _changes = new();
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        internal LocalizationCatalogEditPlan(
            string sourceFingerprint,
            string collectionId,
            long entryId,
            string key,
            string localeCode,
            string value)
        {
            PlanId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
            CollectionId = collectionId ?? string.Empty;
            EntryId = entryId;
            Key = key ?? string.Empty;
            LocaleCode = localeCode ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string PlanId { get; }
        public DateTime CreatedAtUtc { get; }
        public string SourceFingerprint { get; }
        public string CollectionId { get; }
        public long EntryId { get; internal set; }
        public string Key { get; }
        public string LocaleCode { get; }
        public string Value { get; }
        public string PreviousValue { get; internal set; } = string.Empty;
        public bool CreatesEntry { get; internal set; }
        public bool CreatesLocaleCell { get; internal set; }
        public IReadOnlyList<string> Changes => _changes;
        public IReadOnlyList<string> Warnings => _warnings;
        public IReadOnlyList<string> Errors => _errors;
        public bool IsExecutable => _errors.Count == 0 && _changes.Count > 0;

        internal UnityEditor.Localization.StringTableCollection Collection { get; set; }
        internal UnityEngine.Localization.Tables.StringTable Table { get; set; }
        internal void AddChange(string value) => _changes.Add(value);
        internal void AddWarning(string value) => _warnings.Add(value);
        internal void AddError(string value) => _errors.Add(value);
    }

    /// <summary>Result of a verified catalog-cell transaction.</summary>
    public sealed class LocalizationCatalogEditResult
    {
        internal LocalizationCatalogEditResult(LocalizationCatalogEditPlan plan) => Plan = plan;

        public LocalizationCatalogEditPlan Plan { get; }
        public bool Succeeded { get; internal set; }
        public bool WasStale { get; internal set; }
        public string Error { get; internal set; }
        public LocalizationAuditSnapshot PostAudit { get; internal set; }
    }

    /// <summary>One mutation parsed from a stable Molca catalog CSV.</summary>
    public sealed class LocalizationCatalogImportChange
    {
        internal LocalizationCatalogImportChange(
            string collectionId,
            long entryId,
            string key,
            string localeCode,
            string previousValue,
            string value)
        {
            CollectionId = collectionId;
            EntryId = entryId;
            Key = key;
            LocaleCode = localeCode;
            PreviousValue = previousValue ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string CollectionId { get; }
        public long EntryId { get; }
        public string Key { get; }
        public string LocaleCode { get; }
        public string PreviousValue { get; }
        public string Value { get; }

        internal UnityEngine.Localization.Tables.StringTable Table { get; set; }
    }

    /// <summary>Immutable, all-or-nothing preview of a CSV catalog import.</summary>
    public sealed class LocalizationCatalogImportPlan
    {
        private readonly List<LocalizationCatalogImportChange> _changes = new();
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        internal LocalizationCatalogImportPlan(string sourceFingerprint)
        {
            PlanId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
        }

        public string PlanId { get; }
        public DateTime CreatedAtUtc { get; }
        public string SourceFingerprint { get; }
        public IReadOnlyList<LocalizationCatalogImportChange> Changes => _changes;
        public IReadOnlyList<string> Warnings => _warnings;
        public IReadOnlyList<string> Errors => _errors;
        public bool IsExecutable => _errors.Count == 0 && _changes.Count > 0;

        internal void AddChange(LocalizationCatalogImportChange value) => _changes.Add(value);
        internal void AddWarning(string value) => _warnings.Add(value);
        internal void AddError(string value) => _errors.Add(value);
    }

    /// <summary>Result of one verified all-or-nothing catalog import.</summary>
    public sealed class LocalizationCatalogImportResult
    {
        internal LocalizationCatalogImportResult(LocalizationCatalogImportPlan plan) => Plan = plan;

        public LocalizationCatalogImportPlan Plan { get; }
        public bool Succeeded { get; internal set; }
        public bool WasStale { get; internal set; }
        public string Error { get; internal set; }
        public LocalizationAuditSnapshot PostAudit { get; internal set; }
    }
}
