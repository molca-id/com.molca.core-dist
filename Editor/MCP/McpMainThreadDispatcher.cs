using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Marshals work from the bridge's background listener thread onto the Unity main thread. The
    /// <see cref="HttpListener"/> callbacks run on a thread-pool thread, but every Unity editor and
    /// runtime API must be touched from the main thread (the same cross-thread hazard as the content
    /// package deploy output, Sprint 12.9). The listener enqueues a unit of work here and blocks until
    /// <see cref="EditorApplication.update"/> drains the queue and signals completion.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpMainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        static McpMainThreadDispatcher()
        {
            EditorApplication.update += Drain;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and returns its result, blocking the calling
        /// (background) thread until the work completes or <paramref name="timeoutMs"/> elapses.
        /// </summary>
        /// <typeparam name="T">The work result type.</typeparam>
        /// <param name="work">The delegate to run on the main thread.</param>
        /// <param name="timeoutMs">Maximum time to wait for the main thread to drain the work.</param>
        /// <returns>The result of <paramref name="work"/>.</returns>
        /// <exception cref="TimeoutException">Thrown if the main thread does not run the work in time.</exception>
        public static T Invoke<T>(Func<T> work, int timeoutMs = 10000)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            using var done = new ManualResetEventSlim(false);
            T result = default;
            Exception error = null;

            Queue.Enqueue(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            if (!done.Wait(timeoutMs))
                throw new TimeoutException($"Main-thread work did not complete within {timeoutMs} ms.");

            if (error != null)
                throw error;

            return result;
        }

        /// <summary>
        /// Runs an <see cref="UnityEngine.Awaitable"/>-returning <paramref name="work"/> on the main
        /// thread and returns its result, blocking the calling (background) thread until it completes or
        /// <paramref name="timeoutMs"/> elapses. The work is awaited on the main thread without blocking
        /// it, so tools that themselves await Unity APIs (e.g. the Doctor run) don't deadlock.
        /// </summary>
        /// <typeparam name="T">The work result type.</typeparam>
        /// <param name="work">The async delegate to run on the main thread.</param>
        /// <param name="timeoutMs">Maximum time to wait for completion. Longer than the sync default
        /// because async tools (file scans, multi-check runs) can take seconds.</param>
        /// <returns>The result of <paramref name="work"/>.</returns>
        /// <exception cref="TimeoutException">Thrown if the work does not complete in time.</exception>
        public static T InvokeAsync<T>(Func<UnityEngine.Awaitable<T>> work, int timeoutMs = 60000)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            using var done = new ManualResetEventSlim(false);
            T result = default;
            Exception error = null;

            // async void lambda matches Action; it starts on the main thread when drained and its
            // continuations resume on the main thread (Unity Awaitable default), then signals done.
            Queue.Enqueue(async () =>
            {
                try { result = await work(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            if (!done.Wait(timeoutMs))
                throw new TimeoutException($"Async main-thread work did not complete within {timeoutMs} ms.");

            if (error != null)
                throw error;

            return result;
        }

        private static void Drain()
        {
            // Cap per-frame work so a flood of requests can't stall the editor; remaining items run
            // on the next update tick.
            const int maxPerFrame = 16;
            var processed = 0;
            while (processed++ < maxPerFrame && Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception)
                {
                    // The action itself signals its own completion/exception; never let a stray
                    // throw kill the editor update loop.
                }
            }
        }
    }
}
