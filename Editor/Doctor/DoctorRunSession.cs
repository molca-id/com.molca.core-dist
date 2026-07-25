using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Molca.Editor.Doctor
{
    /// <summary>
    /// Editor-session-scoped owner of a Molca Doctor run. It drives <see cref="MolcaDoctor.RunAllAsync"/>,
    /// accumulates the findings and per-check trace, and re-broadcasts progress to whatever
    /// <see cref="MolcaDoctorView"/> is currently rendering it.
    /// </summary>
    /// <remarks>
    /// Because the run lives here rather than inside a view, it survives the view being detached and
    /// rebuilt — e.g. switching Molca Hub tabs away from the Doctor and back, which clears the hosted view
    /// — instead of being cancelled. A view <see cref="Subscribe"/>s on attach (rebuilding its display from
    /// the current state) and <see cref="Unsubscribe"/>s on detach; it no longer owns the run lifetime.
    /// A single static <see cref="Instance"/> suffices: an in-flight <c>Awaitable</c> run cannot outlive a
    /// domain reload anyway, and only one Doctor run should exist at a time regardless of how many views
    /// display it. Not thread-safe; every member is touched on the main thread (the runner returns to it
    /// before every callback).
    /// </remarks>
    internal sealed class DoctorRunSession
    {
        /// <summary>The shared session every Doctor view renders and drives.</summary>
        public static DoctorRunSession Instance { get; } = new DoctorRunSession();

        private readonly List<DoctorIssue> _issues = new List<DoctorIssue>();
        private readonly List<DoctorCheckReport> _reports = new List<DoctorCheckReport>();
        private readonly HashSet<string> _disabledChecks = new HashSet<string>();
        private readonly Stopwatch _currentCheckStopwatch = new Stopwatch();
        private CancellationTokenSource _cts;

        private DoctorRunSession() { }

        /// <summary>Findings from the most recent (or in-progress) run, in production order.</summary>
        public IReadOnlyList<DoctorIssue> Issues => _issues;

        /// <summary>Per-check completion records for the run's trace log, in run order.</summary>
        public IReadOnlyList<DoctorCheckReport> Reports => _reports;

        /// <summary>Check ids the user has turned off for the next run (persisted across view rebuilds).</summary>
        public ISet<string> DisabledChecks => _disabledChecks;

        /// <summary>True while a run is executing.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>True once a run has completed at least once this domain session.</summary>
        public bool HasRun { get; private set; }

        /// <summary>True if the most recent completed run ended via cancellation.</summary>
        public bool WasCanceled { get; private set; }

        /// <summary>Total enabled checks in the current/last run.</summary>
        public int TotalCount { get; private set; }

        /// <summary>Progress of the check currently executing, or null when between checks / idle.</summary>
        public DoctorProgress? CurrentProgress { get; private set; }

        /// <summary>Latest sub-check status detail for the running check (null when idle).</summary>
        public string CurrentStatus { get; private set; }

        /// <summary>Elapsed time of the check currently executing, in milliseconds (0 when idle).</summary>
        public double CurrentCheckElapsedMs =>
            _currentCheckStopwatch.IsRunning ? _currentCheckStopwatch.Elapsed.TotalMilliseconds : 0;

        // Events re-broadcast to subscribed views. All raised on the main thread.

        /// <summary>Raised when a run begins (after state is reset).</summary>
        public event Action RunStarted;

        /// <summary>Raised immediately before each check runs.</summary>
        public event Action<DoctorProgress> ProgressReported;

        /// <summary>Raised for each sub-check status detail (never with null/empty).</summary>
        public event Action<string> StatusReported;

        /// <summary>Raised once per check, immediately after it finishes.</summary>
        public event Action<DoctorCheckReport> CheckCompleted;

        /// <summary>Raised when the run ends; the argument is true when it was cancelled.</summary>
        public event Action<bool> RunFinished;

        /// <summary>
        /// Starts a run over the currently-enabled checks (all checks minus <see cref="DisabledChecks"/>).
        /// No-op if a run is already in flight.
        /// </summary>
        public void Run()
        {
            if (IsRunning)
                return;
            // Fire-and-forget: the run owns its lifetime (cancellation via Cancel()); the awaitable faults
            // are handled inside RunAsync, so an explicit discard is the visible opt-in per the async contract.
            _ = RunAsync();
        }

        /// <summary>Requests cancellation of the in-flight run. Safe to call when idle.</summary>
        public void Cancel() => _cts?.Cancel();

        private async Awaitable RunAsync()
        {
            var enabled = new HashSet<string>(
                MolcaDoctor.Checks.Select(c => c.Id).Except(_disabledChecks));

            IsRunning = true;
            WasCanceled = false;
            TotalCount = enabled.Count;
            _issues.Clear();
            _reports.Clear();
            CurrentProgress = null;
            CurrentStatus = null;
            _currentCheckStopwatch.Reset();
            _cts = new CancellationTokenSource();

            RunStarted?.Invoke();

            bool canceled = false;
            try
            {
                // Findings are accumulated per check in OnCheckCompleted (so a re-attached view sees partial
                // results mid-run); the returned aggregate is therefore already reflected in _issues.
                await MolcaDoctor.RunAllAsync(enabled, OnProgress, _cts.Token, OnStatus, OnCheckCompleted);
                HasRun = true;
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MolcaDoctor] Run failed: {e}");
            }
            finally
            {
                canceled = canceled || (_cts != null && _cts.IsCancellationRequested);
                _cts?.Dispose();
                _cts = null;
                _currentCheckStopwatch.Stop();
                CurrentProgress = null;
                CurrentStatus = null;
                IsRunning = false;
                WasCanceled = canceled;
                RunFinished?.Invoke(canceled);
            }
        }

        private void OnProgress(DoctorProgress p)
        {
            CurrentProgress = p;
            CurrentStatus = p.CurrentCheck?.Description;
            _currentCheckStopwatch.Restart();
            ProgressReported?.Invoke(p);
        }

        private void OnStatus(string detail)
        {
            if (string.IsNullOrEmpty(detail))
                return;
            CurrentStatus = detail;
            StatusReported?.Invoke(detail);
        }

        private void OnCheckCompleted(DoctorCheckReport report)
        {
            _reports.Add(report);
            _issues.AddRange(report.Findings);
            _currentCheckStopwatch.Stop();
            CurrentProgress = null;
            CheckCompleted?.Invoke(report);
        }
    }
}
