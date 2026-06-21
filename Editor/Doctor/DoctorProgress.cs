namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Progress snapshot reported before each check runs during
    /// <see cref="MolcaDoctor.RunAllAsync"/>.
    /// </summary>
    public readonly struct DoctorProgress
    {
        /// <summary>Number of checks that have already finished running.</summary>
        public readonly int CompletedCount;

        /// <summary>Total number of enabled checks in this run.</summary>
        public readonly int TotalCount;

        /// <summary>The check about to execute. Never null.</summary>
        public readonly IDoctorCheck CurrentCheck;

        /// <summary>Creates a progress snapshot.</summary>
        /// <param name="completedCount">Checks already completed.</param>
        /// <param name="totalCount">Total enabled checks.</param>
        /// <param name="currentCheck">The check about to run.</param>
        public DoctorProgress(int completedCount, int totalCount, IDoctorCheck currentCheck)
        {
            CompletedCount = completedCount;
            TotalCount = totalCount;
            CurrentCheck = currentCheck;
        }

        /// <summary>Fraction complete in the range [0, 1], suitable for a progress bar.</summary>
        public float Fraction => TotalCount == 0 ? 1f : (float)CompletedCount / TotalCount;
    }
}
