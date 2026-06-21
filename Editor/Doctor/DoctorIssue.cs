using System;

namespace Molca.Editor.Doctor
{
    /// <summary>Severity of a Molca Doctor finding.</summary>
    public enum DoctorSeverity
    {
        /// <summary>Informational; never fails CI.</summary>
        Info = 0,
        /// <summary>Likely violation found by a heuristic; does not fail CI.</summary>
        Warning = 1,
        /// <summary>Definite convention violation; fails CI.</summary>
        Error = 2,
    }

    /// <summary>A single finding reported by an <see cref="IDoctorCheck"/>.</summary>
    [Serializable]
    public class DoctorIssue
    {
        /// <summary>Id of the check that produced this issue.</summary>
        public string CheckId;
        public DoctorSeverity Severity;
        /// <summary>Human-readable description including the suggested fix.</summary>
        public string Message;
        /// <summary>Project-relative file or asset path, if applicable.</summary>
        public string Path;
        /// <summary>1-based line number for source findings; 0 when not applicable.</summary>
        public int Line;

        public DoctorIssue(string checkId, DoctorSeverity severity, string message, string path = null, int line = 0)
        {
            CheckId = checkId;
            Severity = severity;
            Message = message;
            Path = path;
            Line = line;
        }

        public override string ToString() =>
            $"[{Severity}] {CheckId}: {Message}" + (string.IsNullOrEmpty(Path) ? "" : $" ({Path}{(Line > 0 ? $":{Line}" : "")})");
    }
}
