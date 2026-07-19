using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Communication.Abstractions;
using Communication.Abstractions.Exceptions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Transports.Internal;
using Communication.Transports.Options;

namespace Communication.Transports.Udp;

/// <summary>Implements a bound UDP datagram channel with an optional default remote endpoint.</summary>
public sealed class UdpTransportChannel : ITransportChannel
{
    private readonly UdpTransportOptions _options;
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private UdpClient? _client;
    private int _disposed;

    /// <summary>Initializes a UDP channel.</summary>
    public UdpTransportChannel(UdpTransportOptions options, string? channelId = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        ChannelId = channelId ?? $"udp://{options.LocalAddress}:{options.LocalPort}";
        _stateMachine.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
    }

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <inheritdoc />
    public ConnectionState State => _stateMachine.State;

    /// <summary>Gets the actual bound local port, or zero before connection.</summary>
    public int BoundPort => (_client?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;

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
                    $"Cannot bind a UDP channel while it is {State}."));
            }

            bool isReconnect = State == ConnectionState.Faulted;
            if (isReconnect)
            {
                _client?.Dispose();
                _client = null;
            }

            _stateMachine.TransitionTo(
                isReconnect ? ConnectionState.Reconnecting : ConnectionState.Connecting);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                IPAddress localAddress = ResolveAddress(_options.LocalAddress);
                UdpClient client = new(new IPEndPoint(localAddress, _options.LocalPort));
                client.EnableBroadcast = _options.EnableBroadcast;
                client.Client.ReceiveBufferSize = _options.ReceiveBufferSize;
                if (_options.RemoteHost is not null && _options.RemotePort.HasValue)
                {
                    client.Connect(_options.RemoteHost, _options.RemotePort.Value);
                }

                _client = client;
                _stateMachine.TransitionTo(ConnectionState.Connected);
                return CommunicationResult.Success();
            }
            catch (OperationCanceledException)
            {
                _stateMachine.TransitionTo(ConnectionState.Disconnected);
                throw;
            }
            catch (Exception exception)
            {
                CommunicationError error = new(
                    CommunicationErrorCode.ConnectionFailure,
                    exception.Message,
                    Exception: exception);
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

            _client?.Dispose();
            _client = null;
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
        UdpClient? client = _client;
        if (State != ConnectionState.Connected || client is null)
        {
            return CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "UDP channel is not bound."));
        }

        if (_options.RemoteHost is null || !_options.RemotePort.HasValue)
        {
            return CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "UDP channel has no default remote endpoint."));
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = payload.ToArray();
            Task<int> operation = client.SendAsync(bytes, bytes.Length);
            int sent = await TaskCompatibility.WaitAsync(operation, cancellationToken).ConfigureAwait(false);
            return CommunicationResult<int>.Success(sent);
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
        UdpClient client = _client ?? throw new InvalidOperationException("UDP channel is not bound.");

        while (State == ConnectionState.Connected)
        {
            UdpReceiveResult result;
            using CancellationTokenRegistration registration = cancellationToken.Register(client.Dispose);
            try
            {
                result = await TaskCompatibility.WaitAsync(client.ReceiveAsync(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _client = null;
                _stateMachine.TryTransition(ConnectionState.Faulted, new CommunicationError(
                    CommunicationErrorCode.Canceled,
                    "UDP receive was canceled and its socket was closed."));
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

            yield return result.Buffer;
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

    private static void ValidateOptions(UdpTransportOptions options)
    {
        _ = ResolveAddress(options.LocalAddress);
        if (options.LocalPort is < 0 or > 65_535 || options.ReceiveBufferSize is < 1 or > 65_507)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if ((options.RemoteHost is null) != !options.RemotePort.HasValue)
        {
            throw new ArgumentException("RemoteHost and RemotePort must be supplied together.", nameof(options));
        }

        if (options.RemotePort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static IPAddress ResolveAddress(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress? parsed))
        {
            throw new ArgumentException($"'{address}' is not an IP address.", nameof(address));
        }

        return parsed;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(UdpTransportChannel));
        }
    }
}
