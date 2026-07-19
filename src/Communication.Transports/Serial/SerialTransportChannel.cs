using System.IO.Ports;
using System.Runtime.CompilerServices;
using Communication.Abstractions;
using Communication.Abstractions.Exceptions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Transports.Internal;
using Communication.Transports.Options;

namespace Communication.Transports.Serial;

/// <summary>Implements an asynchronous serial-port byte channel.</summary>
public sealed class SerialTransportChannel : ITransportChannel
{
    private readonly SerialTransportOptions _options;
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private SerialPort? _serialPort;
    private int _disposed;

    /// <summary>Initializes a serial channel.</summary>
    public SerialTransportChannel(SerialTransportOptions options, string? channelId = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        ChannelId = channelId ?? $"serial://{options.PortName}";
        _stateMachine.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
    }

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <inheritdoc />
    public ConnectionState State => _stateMachine.State;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                return CommunicationResult.Success();
            }

            if (State is not (ConnectionState.Disconnected or ConnectionState.Faulted))
            {
                return CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.InvalidState,
                    $"Cannot open a serial channel while it is {State}."));
            }

            bool isReconnect = State == ConnectionState.Faulted;
            if (isReconnect)
            {
                _serialPort?.Dispose();
                _serialPort = null;
            }

            _stateMachine.TransitionTo(
                isReconnect ? ConnectionState.Reconnecting : ConnectionState.Connecting);
            SerialPort serialPort = CreateSerialPort();
            try
            {
                Task openTask = Task.Run(serialPort.Open, CancellationToken.None);
                await TaskCompatibility.WaitAsync(openTask, _options.OpenTimeout, cancellationToken)
                    .ConfigureAwait(false);
                _serialPort = serialPort;
                _stateMachine.TransitionTo(ConnectionState.Connected);
                return CommunicationResult.Success();
            }
            catch (OperationCanceledException)
            {
                serialPort.Dispose();
                _stateMachine.TransitionTo(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception exception)
            {
                serialPort.Dispose();
                CommunicationError error = CommunicationError.FromException(
                    exception is TimeoutException ? exception : new ConnectionException(exception.Message, exception));
                _stateMachine.TransitionTo(ConnectionState.Faulted, error);
                return CommunicationResult.Failure(error);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Disconnected)
            {
                return CommunicationResult.Success();
            }

            if (State != ConnectionState.Disconnecting)
            {
                _stateMachine.TransitionTo(ConnectionState.Disconnecting);
            }

            SerialPort? serialPort = _serialPort;
            _serialPort = null;
            if (serialPort is not null)
            {
                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                }

                serialPort.Dispose();
            }

            _stateMachine.TransitionTo(ConnectionState.Disconnected);
            return CommunicationResult.Success();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<int>> SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SerialPort? serialPort = _serialPort;
        if (State != ConnectionState.Connected || serialPort is null || !serialPort.IsOpen)
        {
            return CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "Serial channel is not connected."));
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await serialPort.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await serialPort.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return CommunicationResult<int>.Success(payload.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CommunicationError error = new(
                CommunicationErrorCode.ConnectionFailure,
                exception.Message,
                Exception: exception);
            _stateMachine.TryTransition(ConnectionState.Faulted, error);
            return CommunicationResult<int>.Failure(error);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SerialPort serialPort = _serialPort ?? throw new InvalidOperationException("Serial channel is not connected.");
        byte[] buffer = new byte[_options.ReceiveBufferSize];

        while (State == ConnectionState.Connected && serialPort.IsOpen)
        {
            int bytesRead;
            try
            {
                bytesRead = await serialPort.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                CommunicationError error = new(
                    CommunicationErrorCode.ConnectionFailure,
                    exception.Message,
                    Exception: exception);
                _stateMachine.TryTransition(ConnectionState.Faulted, error);
                throw new ConnectionException(exception.Message, exception);
            }

            if (bytesRead == 0)
            {
                continue;
            }

            byte[] chunk = new byte[bytesRead];
            Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Dispose();
            _sendGate.Dispose();
        }
    }

    private SerialPort CreateSerialPort() => new(_options.PortName, _options.BaudRate, _options.Parity, _options.DataBits, _options.StopBits)
    {
        Handshake = _options.Handshake,
        ReadTimeout = checked((int)_options.ReadTimeout.TotalMilliseconds),
        WriteTimeout = checked((int)_options.WriteTimeout.TotalMilliseconds),
        ReadBufferSize = _options.ReceiveBufferSize,
    };

    private static void ValidateOptions(SerialTransportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PortName))
        {
            throw new ArgumentException("A port name is required.", nameof(options));
        }

        if (options.BaudRate <= 0 || options.DataBits is < 5 or > 8 || options.ReceiveBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.StopBits == StopBits.None)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        TaskCompatibility.ValidateTimeout(options.OpenTimeout, nameof(options.OpenTimeout));
        TaskCompatibility.ValidateTimeout(options.ReadTimeout, nameof(options.ReadTimeout));
        TaskCompatibility.ValidateTimeout(options.WriteTimeout, nameof(options.WriteTimeout));
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(SerialTransportChannel));
        }
    }
}
