using System;
using System.Threading;
using UnityEngine;

namespace Molca
{
    public abstract class RuntimeSubsystem : MonoBehaviour, IRuntimeSubsystem
    {
        [Flags]
        public enum RuntimeMode
        {
            Nothing = 0,
            Editor = 1 << 0,
            Runtime = 1 << 1
        }

        [SerializeField]
        private RuntimeMode _runtimeMode;

        [SerializeField]
        private int _initializationPriority = 0;

        protected bool isActive;

        private CancellationTokenSource _shutdownCts;

        public int InitializationPriority => _initializationPriority;
        public bool IsActive => isActive && IsRuntimeValid;

        /// <summary>
        /// The execution contexts (Editor / Runtime) in which this subsystem is active, as authored
        /// in the Inspector. Exposed for tooling/introspection (e.g. the MCP <c>molca_subsystems</c>
        /// tool); does not affect lifecycle.
        /// </summary>
        public RuntimeMode Mode => _runtimeMode;

        /// <summary>
        /// Lifetime token for this subsystem. Cancelled by <see cref="Shutdown"/> before
        /// <see cref="Teardown"/> runs. Key any background loop or pending await started by
        /// this subsystem on this token so teardown unwinds in-flight work.
        /// </summary>
        /// <remarks>Stays cancelled after shutdown; it is never reset.</remarks>
        public CancellationToken ShutdownToken => (_shutdownCts ??= new CancellationTokenSource()).Token;

        internal bool IsRuntimeValid
        {
            get
            {
                if (Application.isEditor && _runtimeMode.HasFlag(RuntimeMode.Editor))
                    return true;
                if (!Application.isEditor && _runtimeMode.HasFlag(RuntimeMode.Runtime))
                    return true;
                return false;
            }
        }

        /// <summary>
        /// Legacy initialization path (compat shim). Prefer overriding
        /// <see cref="InitializeAsync(CancellationToken)"/> in new code.
        /// Always invoke <paramref name="finishCallback"/>; bootstrap blocks until it is called.
        /// </summary>
        /// <remarks>
        /// The default implementation completes immediately. Override exactly one of
        /// <see cref="Initialize"/> or <see cref="InitializeAsync(CancellationToken)"/> —
        /// <see cref="RuntimeManager"/> drives initialization only through
        /// <see cref="InitializeAsync(CancellationToken)"/>, whose base implementation
        /// bridges to this method.
        /// </remarks>
        public virtual void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            finishCallback?.Invoke(this);
        }

        /// <summary>
        /// Async initialization path. <see cref="RuntimeManager"/> awaits this during bootstrap.
        /// The base implementation bridges to the legacy
        /// <see cref="Initialize(Action{IRuntimeSubsystem})"/> callback form, so existing
        /// subsystems behave unchanged.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancelled when bootstrap is torn down or the per-subsystem init timeout elapses.
        /// Thread it through all awaited work.
        /// </param>
        public virtual async Awaitable InitializeAsync(CancellationToken cancellationToken)
        {
            var completion = new AwaitableCompletionSource<bool>();
            // Bridge: a cancelled token resolves the await so bootstrap can move on;
            // a late finishCallback after that is harmless (TrySet no-ops).
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled());
            Initialize(_ => completion.TrySetResult(true));
            await completion.Awaitable;
        }

        public virtual void Shutdown()
        {
            // Cancel in-flight work keyed on ShutdownToken before cleanup runs.
            _shutdownCts?.Cancel();
            Teardown();
        }

        /// <summary>
        /// Called by <see cref="RuntimeManager"/> once all subsystems have finished
        /// <see cref="Initialize"/>. Sets <see cref="isActive"/> to <c>true</c>.
        /// Not virtual — subsystems that need post-init work should do it in
        /// <see cref="Initialize"/> after invoking <c>finishCallback</c>.
        /// </summary>
        internal void MarkActive()
        {
            isActive = true;
        }

        /// <summary>
        /// Override to unregister event listeners and release resources on shutdown.
        /// Always call <c>base.Teardown()</c> to clear <see cref="isActive"/>.
        /// </summary>
        public virtual void Teardown()
        {
            isActive = false;
        }
    }
}
