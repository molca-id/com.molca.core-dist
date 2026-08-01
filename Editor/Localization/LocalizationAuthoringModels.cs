using System;
using System.Collections.Generic;

namespace Molca.Editor
{
    /// <summary>Immutable preview of an add-or-repair locale transaction.</summary>
    public sealed class LocalizationLocaleAuthoringPlan
    {
        private readonly List<string> _changes = new();
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        internal LocalizationLocaleAuthoringPlan(
            string code,
            string displayName,
            string modulePath,
            string sourceFingerprint)
        {
            PlanId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            Code = code;
            DisplayName = displayName;
            ModulePath = modulePath;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
        }

        /// <summary>Opaque identifier accepted by the execution endpoint.</summary>
        public string PlanId { get; }

        /// <summary>UTC time at which the preview was produced.</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>Canonical locale code.</summary>
        public string Code { get; }

        /// <summary>Display name written to the Molca localization module.</summary>
        public string DisplayName { get; }

        /// <summary>Selected Molca localization module asset path.</summary>
        public string ModulePath { get; }

        /// <summary>Audit fingerprint that must still match at execution time.</summary>
        public string SourceFingerprint { get; }

        /// <summary>Planned mutations in execution order.</summary>
        public IReadOnlyList<string> Changes => _changes;

        /// <summary>Non-blocking preview guidance.</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>Blocking precondition failures.</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>Whether this preview may be executed.</summary>
        public bool IsExecutable => _errors.Count == 0 && _changes.Count > 0;

        internal bool AddModuleEntry { get; set; }
        internal bool CreateLocaleAsset { get; set; }
        internal bool RegisterLocale { get; set; }
        internal string LocaleAssetPath { get; set; }
        internal UnityEngine.Localization.Locale LocaleAsset { get; set; }
        internal Molca.Localization.LocalizationModule Module { get; set; }
        internal List<UnityEditor.Localization.LocalizationTableCollection> MissingTableCollections { get; } = new();

        internal void AddChange(string change) => _changes.Add(change);
        internal void AddWarning(string warning) => _warnings.Add(warning);
        internal void AddError(string error) => _errors.Add(error);
    }

    /// <summary>Outcome and verification evidence for one locale authoring transaction.</summary>
    public sealed class LocalizationLocaleAuthoringResult
    {
        private readonly List<string> _createdAssetPaths = new();

        internal LocalizationLocaleAuthoringResult(LocalizationLocaleAuthoringPlan plan)
        {
            Plan = plan;
        }

        /// <summary>Preview that authorized the transaction.</summary>
        public LocalizationLocaleAuthoringPlan Plan { get; }

        /// <summary>Whether all mutations and postconditions succeeded.</summary>
        public bool Succeeded { get; internal set; }

        /// <summary>Whether execution was refused because the source fingerprint changed.</summary>
        public bool WasStale { get; internal set; }

        /// <summary>Error or refusal reason when <see cref="Succeeded"/> is false.</summary>
        public string Error { get; internal set; }

        /// <summary>Assets and asset folders created by the successful transaction.</summary>
        public IReadOnlyList<string> CreatedAssetPaths => _createdAssetPaths;

        /// <summary>Fresh shared audit snapshot captured after a successful transaction.</summary>
        public LocalizationAuditSnapshot PostAudit { get; internal set; }

        internal void AddCreatedAssetPath(string path) => _createdAssetPaths.Add(path);
    }

    /// <summary>Immutable preview of a non-destructive locale archive transaction.</summary>
    public sealed class LocalizationLocaleArchivePlan
    {
        private readonly List<string> _changes = new();
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        internal LocalizationLocaleArchivePlan(
            string code,
            string modulePath,
            string sourceFingerprint)
        {
            PlanId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            Code = code;
            ModulePath = modulePath;
            SourceFingerprint = sourceFingerprint ?? string.Empty;
        }

        /// <summary>Opaque identifier accepted by the archive execution endpoint.</summary>
        public string PlanId { get; }

        /// <summary>UTC time at which the preview was produced.</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>Canonical locale code to disable.</summary>
        public string Code { get; }

        /// <summary>Selected Molca localization module asset path.</summary>
        public string ModulePath { get; }

        /// <summary>Audit fingerprint that must still match at execution time.</summary>
        public string SourceFingerprint { get; }

        /// <summary>Planned mutations in execution order.</summary>
        public IReadOnlyList<string> Changes => _changes;

        /// <summary>Preservation and follow-up guidance.</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>Blocking precondition failures.</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>Whether this preview may be executed.</summary>
        public bool IsExecutable => _errors.Count == 0 && _changes.Count > 0;

        internal bool UnregisterLocale { get; set; }
        internal UnityEngine.Localization.Locale LocaleAsset { get; set; }
        internal Molca.Localization.LocalizationModule Module { get; set; }
        internal List<(UnityEditor.Localization.LocalizationTableCollection collection,
            UnityEngine.Localization.Tables.LocalizationTable table)> Tables { get; } = new();

        internal void AddChange(string change) => _changes.Add(change);
        internal void AddWarning(string warning) => _warnings.Add(warning);
        internal void AddError(string error) => _errors.Add(error);
    }

    /// <summary>Outcome and audit evidence for one archive-locale transaction.</summary>
    public sealed class LocalizationLocaleArchiveResult
    {
        internal LocalizationLocaleArchiveResult(LocalizationLocaleArchivePlan plan)
        {
            Plan = plan;
        }

        /// <summary>Preview that authorized the transaction.</summary>
        public LocalizationLocaleArchivePlan Plan { get; }

        /// <summary>Whether all archive mutations and postconditions succeeded.</summary>
        public bool Succeeded { get; internal set; }

        /// <summary>Whether execution was refused because the source fingerprint changed.</summary>
        public bool WasStale { get; internal set; }

        /// <summary>Error or refusal reason when the transaction did not succeed.</summary>
        public string Error { get; internal set; }

        /// <summary>Fresh shared audit snapshot captured after a successful archive.</summary>
        public LocalizationAuditSnapshot PostAudit { get; internal set; }
    }
}
