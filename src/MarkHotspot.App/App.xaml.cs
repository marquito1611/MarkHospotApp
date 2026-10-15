using System;
using System.Threading;
using System.Windows;

namespace MarkHotspot;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\MarkHotspot.SingleInstance";
    private const string ActivationEventName = @"Local\MarkHotspot.Activate";
    private static Mutex? _instanceMutex;
    public static EventWaitHandle? ActivationSignal { get; private set; }
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        ActivationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _instanceMutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out _ownsMutex);

        if (!_ownsMutex)
        {
            try { ActivationSignal.Set(); } catch (ObjectDisposedException) { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { ActivationSignal?.Dispose(); } catch { }
        ActivationSignal = null;

        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }
}
