using System.IO.Pipes;
using System.Text;

namespace ReVueVRO.Shell;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\ReVue-VRO-SingleInstance";
    private const string PipeName = "ReVue-VRO-Activation";

    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Mutex? _mutex;
    private Task? _listenerTask;
    private Action? _activationAction;
    private bool _pendingActivation;
    private bool _ownsMutex;
    private bool _disposed;

    public bool TryBecomePrimaryInstance()
    {
        ThrowIfDisposed();

        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
        }

        return createdNew;
    }

    public void StartActivationListener()
    {
        ThrowIfDisposed();

        if (_listenerTask != null)
            return;

        _listenerTask = Task.Run(ListenForActivationRequestsAsync);
    }

    public void SetActivationAction(Action activationAction)
    {
        ThrowIfDisposed();

        bool shouldRun = false;
        lock (_gate)
        {
            _activationAction = activationAction;
            if (_pendingActivation)
            {
                _pendingActivation = false;
                shouldRun = true;
            }
        }

        if (shouldRun)
            activationAction();
    }

    public bool RequestActivatePrimaryInstance()
    {
        ThrowIfDisposed();

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: PipeName,
                    direction: PipeDirection.Out,
                    options: PipeOptions.None);

                var remaining = deadline - DateTime.UtcNow;
                var timeoutMs = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 500);
                client.Connect(timeoutMs);

                using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                writer.WriteLine("activate");
                return true;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private async Task ListenForActivationRequestsAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var command = (await reader.ReadLineAsync().ConfigureAwait(false))?.Trim();

                if (string.Equals(command, "activate", StringComparison.OrdinalIgnoreCase))
                    RequestActivation();
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (_shutdown.IsCancellationRequested)
                    break;

                await Task.Delay(100, _shutdown.Token).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    private void RequestActivation()
    {
        Action? activationAction;

        lock (_gate)
        {
            activationAction = _activationAction;
            if (activationAction == null)
            {
                _pendingActivation = true;
                return;
            }
        }

        activationAction();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SingleInstanceCoordinator));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try { _shutdown.Cancel(); } catch { }

        try { _listenerTask?.Wait(250); } catch { }
        try { _shutdown.Dispose(); } catch { }

        if (_ownsMutex && _mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }

        try { _mutex?.Dispose(); } catch { }
    }
}
