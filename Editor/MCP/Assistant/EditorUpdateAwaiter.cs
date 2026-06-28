using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Awaitable continuations pumped by <see cref="EditorApplication.update"/> — which keeps firing while
    /// Unity Play mode is paused, unlike <c>Awaitable.NextFrameAsync</c> / the player loop. The assistant
    /// uses this to poll background work (HTTP completion) so a turn can progress and answer while the user
    /// has paused Play mode (Sprint 65).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/MCP/Assistant/</c>. Editor-only; resumes on the main
    /// thread. All waits honor the supplied <see cref="CancellationToken"/> (surfaced as
    /// <see cref="OperationCanceledException"/>), so Stop/cancel never hangs.
    /// </remarks>
    public static class EditorUpdateAwaiter
    {
        /// <summary>Completes on the next <see cref="EditorApplication.update"/> tick (fires while paused).</summary>
        /// <param name="cancellationToken">Cancels the wait; the returned awaitable then throws.</param>
        public static Awaitable NextAsync(CancellationToken cancellationToken)
        {
            var source = new AwaitableCompletionSource();
            if (cancellationToken.IsCancellationRequested)
            {
                source.TrySetException(new OperationCanceledException(cancellationToken));
                return source.Awaitable;
            }

            EditorApplication.CallbackFunction tick = null;
            CancellationTokenRegistration registration = default;

            tick = () =>
            {
                EditorApplication.update -= tick;
                registration.Dispose();
                source.TrySetResult();
            };
            EditorApplication.update += tick;

            registration = cancellationToken.Register(() =>
            {
                EditorApplication.update -= tick;
                source.TrySetException(new OperationCanceledException(cancellationToken));
            });

            return source.Awaitable;
        }

        /// <summary>
        /// Waits until <paramref name="condition"/> returns <c>true</c>, ticking via <paramref name="tick"/>
        /// (defaults to <see cref="NextAsync"/>). The <paramref name="tick"/> seam is injectable so the loop
        /// is unit-testable without a live editor-update loop.
        /// </summary>
        /// <param name="condition">Polled before each wait; the loop exits as soon as it is <c>true</c>.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <param name="tick">Awaited once per poll while the condition is false.</param>
        public static async Awaitable WaitUntilAsync(
            Func<bool> condition, CancellationToken cancellationToken, Func<CancellationToken, Awaitable> tick = null)
        {
            tick ??= NextAsync;
            while (!condition())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await tick(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
