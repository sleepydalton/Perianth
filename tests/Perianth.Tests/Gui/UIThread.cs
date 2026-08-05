using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Avalonia.Threading;

namespace Perianth.Tests.Gui;

/// <summary>
/// The one thread Avalonia's dispatcher belongs to, for tests that use it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dispatcher.UIThread"/> is created by whichever thread first asks
/// for it and refuses every other thread afterwards. xUnit runs test bodies on
/// pool threads and does not promise the same one twice, so a view model that
/// posts to the dispatcher in one test and a <c>RunJobs</c> in the next are
/// only ever on the same thread by luck. In a window there is a real UI thread
/// and the question does not arise; in a test there is not one unless the test
/// makes one.
/// </para>
/// <para>
/// So this makes one, and every test that goes near the dispatcher runs its
/// body on it. That is also what the window does, so the tests exercise the
/// ordering the pane actually relies on rather than one an ad-hoc thread
/// happened to produce.
/// </para>
/// <para>
/// Started on first use rather than from a module initializer, which is where
/// this was first put and where it deadlocks: a module initializer runs holding
/// the loader lock, and the thread it starts wants that same lock to load the
/// Avalonia types it immediately touches. The whole run hangs with no output.
/// Being late is safe here because nothing else in the tests reaches the
/// dispatcher without coming through this class first.
/// </para>
/// <para>
/// This failed only on a hosted runner, where the pool hands out threads
/// differently under a cold start — passing locally is not evidence.
/// </para>
/// </remarks>
internal static class UiThread
{
    private static readonly BlockingCollection<Action> Queue = [];

    private static readonly Lazy<Thread> Owner = new(Claim, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Thread Claim()
    {
        using ManualResetEventSlim bound = new();

        Thread thread = new(() =>
        {
            // Asking for it is what creates it, on this thread.
            _ = Dispatcher.UIThread;
            bound.Set();

            foreach (Action work in Queue.GetConsumingEnumerable())
            {
                work();
            }
        })
        {
            IsBackground = true,
            Name = "perianth-tests-ui",
        };

        thread.Start();
        bound.Wait();
        return thread;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the dispatcher's thread and waits for it.
    /// </summary>
    /// <remarks>
    /// A failure is rethrown on the calling thread rather than reported there,
    /// so an assertion still fails the test that made it instead of ending the
    /// run somewhere with no name attached.
    /// </remarks>
    internal static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _ = Owner.Value;

        ExceptionDispatchInfo? failure = null;
        using ManualResetEventSlim finished = new();

        Queue.Add(() =>
        {
            try
            {
                body();
            }
            catch (Exception fault)
            {
                failure = ExceptionDispatchInfo.Capture(fault);
            }
            finally
            {
                finished.Set();
            }
        });

        finished.Wait();
        failure?.Throw();
    }
}
