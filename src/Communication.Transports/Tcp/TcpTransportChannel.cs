using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Communication.Abstractions;
using Communication.Abstractions.Exceptions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Transports.Internal;
using Communication.Transports.Options;

namespace Communication.Transports.Tcp;

/// <summary>Implements a reusable asynchronous TCP client byte channel.</summary>
public sealed class TcpTransportChannel : ITransportChannel
{
    private readonly TcpTransportOptions _options;
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _disposed;

    /// <summary>Initializes a TCP client channel.</summary>
    public TcpTransportChannel(TcpTransportOptions options, string? channelId = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        ChannelId = channelId ?? $"tcp://{options.Host}:{options.Port}";
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
                return InvalidState("connect");
            }

            bool isReconnect = State == ConnectionState.Faulted;
            if (isReconnect)
            {
                CloseResources();
            }

            _stateMachine.TransitionTo(
                isReconnect ? ConnectionState.Reconnecting : ConnectionState.Connecting);
            TcpClient client = new()
            {
                NoDelay = _options.NoDelay,
                ReceiveBufferSize = _options.ReceiveBufferSize,
                SendBufferSize = _options.SendBufferSize,
            };

            try
            {
                await TaskCompatibility.WaitAsync(
                    client.ConnectAsync(_options.Host, _options.Port),
                    _options.ConnectTimeout,
                    cancellationToken).ConfigureAwait(false);
                _client = client;
                _stream = client.GetStream();
                _stateMachine.TransitionTo(ConnectionState.Connected);
                return CommunicationResult.Success();
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                _stateMachine.TransitionTo(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception exception)
            {
                client.Dispose();
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

            CloseResources();
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
        NetworkStream? stream = _stream;
        if (State != ConnectionState.Connected || stream is null)
        {
            return CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "TCP channel is not connected."));
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
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
            TryFault(error);
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
        NetworkStream stream = _stream ?? throw new InvalidOperationException("TCP channel is not connected.");
        byte[] buffer = new byte[_options.ReceiveBufferSize];

        while (State == ConnectionState.Connected)
        {
            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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
                TryFault(error);
                throw new ConnectionException(exception.Message, exception);
            }

            if (bytesRead == 0)
            {
                CommunicationError error = new(
                    CommunicationErrorCode.ConnectionFailure,
                    "The remote TCP endpoint closed the connection.");
                TryFault(error);
                yield break;
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

    private static void ValidateOptions(TcpTransportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("A host is required.", nameof(options));
        }

        if (options.Port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ReceiveBufferSize <= 0 || options.SendBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        TaskCompatibility.ValidateTimeout(options.ConnectTimeout, nameof(options.ConnectTimeout));
    }

    private CommunicationResult InvalidState(string operation) =>
        CommunicationResult.Failure(new CommunicationError(
            CommunicationErrorCode.InvalidState,
            $"Cannot {operation} a TCP channel while it is {State}."));

    private void TryFault(CommunicationError error)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting or ConnectionState.Reconnecting)
        {
            _stateMachine.TryTransition(ConnectionState.Faulted, error);
        }
    }

    private void CloseResources()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TcpTransportChannel));
        }
    }
}
