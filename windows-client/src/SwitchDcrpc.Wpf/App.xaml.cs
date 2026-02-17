using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SwitchDcrpc.Wpf;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\RichNX_Wpf_SingleInstance";
    private const string ActivatePipeName = "RichNX_Wpf_Activate";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _activationListenerCts;
    private Task? _activationListenerTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        var createdNew = false;
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);

        if (!createdNew)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _activationListenerCts = new CancellationTokenSource();
        _activationListenerTask = Task.Run(() => RunActivationListenerAsync(_activationListenerCts.Token));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activationListenerCts?.Cancel();
            _activationListenerTask?.Wait(750);
        }
        catch
        {
            // Ignore shutdown listener errors.
        }

        _activationListenerCts?.Dispose();
        _activationListenerCts = null;
        _activationListenerTask = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Ignore mutex release errors.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivatePipeName, PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
        }
        catch
        {
            // If signaling fails, simply exit the second process.
        }
    }

    private async Task RunActivationListenerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    ActivatePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server);
                _ = await reader.ReadLineAsync(cancellationToken);

                await Dispatcher.InvokeAsync(BringMainWindowToFront);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep listener alive on transient IPC errors.
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private void BringMainWindowToFront()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.ShowInTaskbar = true;

        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }
}
