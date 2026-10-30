using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace StickyNotes.Tests;

/// <summary>
/// WPF allows one Application per process and windows must stay on the thread that created them, so a single
/// STA thread owns the Application and every window the tests create. Tests hand their work to it with
/// <see cref="Run(Action)"/>; assertion failures are rethrown on the calling test thread.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    /// <summary>Exceptions that escaped a timer or event handler on the UI thread.</summary>
    public static ConcurrentQueue<Exception> Unhandled { get; } = new();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Unhandled.Enqueue(e.Exception);
                e.Handled = true;
            };
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "StickyNotes.Tests UI" };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    public static T Run<T>(Func<T> function)
    {
        Exception? error = null;
        T result = default!;

        Host.Value.Invoke(() =>
        {
            try { result = function(); }
            catch (Exception ex) { error = ex; }
        });

        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    /// <summary>Lets queued layout and input work run. Call from inside <see cref="Run(Action)"/>.</summary>
    public static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

    public static void WaitUntil(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        if (condition()) return;
        long started = Environment.TickCount64;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (condition() || Environment.TickCount64 - started >= timeoutMilliseconds)
                frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new TimeoutException("The UI did not reach the expected state.");
    }

    /// <summary>Keeps the UI thread processing messages (timers fire) for a while. Call from inside <see cref="Run(Action)"/>.</summary>
    public static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
