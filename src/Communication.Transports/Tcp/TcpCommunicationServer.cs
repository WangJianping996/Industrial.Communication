using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Transports.Options;

namespace Communication.Transports.Tcp;

/// <summary>Implements a bounded, multi-session asynchronous TCP server.</summary>
public sealed class TcpCommunicationServer : ICommunicationServer
{
    private readonly TcpServerOptions _options;
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetimeCancellation;
    private Channel<ServerRequestContext>? _requests;
    private Task? _acceptLoop;
    private int _disposed;

    /// <summary>Initializes a TCP communication server.</summary>
    public TcpCommunicationServer(TcpServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _stateMachine.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
    }

    /// <inheritdoc />
    public ConnectionState State => _stateMachine.State;

    /// <inheritdoc />
    public IReadOnlyCollection<CommunicationSession> Sessions =>
        _sessions.Values.Select(session => session.Description).ToArray();

    /// <summary>Gets the actual bound port, or zero before the server starts.</summary>
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> StartAsync(CancellationToken cancellationToken = default)
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
                    $"Cannot start a TCP server while it is {State}."));
            }

            _stateMachine.TransitionTo(ConnectionState.Connecting);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                IPAddress address = IPAddress.Parse(_options.ListenAddress);
                TcpListener listener = new(address, _options.Port);
                listener.Start(_options.MaxConnections);

                _requests = Channel.CreateBounded<ServerRequestContext>(new BoundedChannelOptions(
                    _options.RequestQueueCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false,
                });
                _lifetimeCancellation = new CancellationTokenSource();
                _listener = listener;
                _acceptLoop = AcceptLoopAsync(listener, _lifetimeCancellation.Token);
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
    public async ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default)
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

            CancellationTokenSource? lifetimeCancellation = _lifetimeCancellation;
            _lifetimeCancellation = null;
            lifetimeCancellation?.Cancel();
            _listener?.Stop();
            _listener = null;

            ClientSession[] sessions = _sessions.Values.ToArray();
            foreach (ClientSession session in sessions)
            {
                session.Client.Dispose();
            }

            Task[] tasks = sessions
                .Select(session => session.ReceiveTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .Append(_acceptLoop ?? Task.CompletedTask)
                .ToArray();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception) when (lifetimeCancellation?.IsCancellationRequested == true)
            {
                // Expected while sockets are being closed for an orderly stop.
            }

            _acceptLoop = null;
            _sessions.Clear();
            _requests?.Writer.TryComplete();
            lifetimeCancellation?.Dispose();
            _stateMachine.TransitionTo(ConnectionState.Disconnected);
            return CommunicationResult.Success();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ServerRequestContext> ReadRequestsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<ServerRequestContext> requests = _requests ??
            throw new InvalidOperationException("TCP server has not been started.");
        await foreach (ServerRequestContext request in requests.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return request;
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<int>> SendAsync(
        string sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_sessions.TryGetValue(sessionId, out ClientSession? session))
        {
            return CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.ConnectionFailure,
                $"TCP session '{sessionId}' is not active."));
        }

        await session.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            return CommunicationResult<int>.Success(payload.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RemoveSession(session);
            return CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.ConnectionFailure,
                exception.Message,
                Exception: exception));
        }
        finally
        {
            session.SendGate.Release();
        }
    }

    /// <summary>Closes one active client session without stopping the listener.</summary>
    public ValueTask<CommunicationResult> DisconnectSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryRemove(sessionId, out ClientSession? session))
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.ConnectionFailure,
                $"TCP session '{sessionId}' is not active.")));
        }

        session.Dispose();
        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
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
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!listener.Pending())
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                if (_sessions.Count >= _options.MaxConnections)
                {
                    client.Dispose();
                    continue;
                }

                client.NoDelay = _options.NoDelay;
                client.ReceiveBufferSize = _options.ReceiveBufferSize;
                string sessionId = Guid.NewGuid().ToString("N");
                string? endpoint = client.Client.RemoteEndPoint?.ToString();
                ClientSession session = new(
                    client,
                    new CommunicationSession(sessionId, DateTimeOffset.UtcNow, endpoint));
                if (!_sessions.TryAdd(sessionId, session))
                {
                    session.Dispose();
                    continue;
                }

                session.ReceiveTask = ReceiveSessionAsync(session, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                CommunicationError error = new(
                    CommunicationErrorCode.ConnectionFailure,
                    exception.Message,
                    Exception: exception);
                _stateMachine.TryTransition(ConnectionState.Faulted, error);
                _requests?.Writer.TryComplete(exception);
                break;
            }
        }
    }

    private async Task ReceiveSessionAsync(ClientSession session, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[_options.ReceiveBufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await session.Stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                byte[] payload = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, payload, 0, bytesRead);
                MessageEnvelope message = new(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    MessageDirection.Inbound,
                    "tcp-server",
                    payload,
                    session.Description.SessionId,
                    Summary: $"{bytesRead} bytes");
                Channel<ServerRequestContext>? requests = _requests;
                if (requests is null)
                {
                    break;
                }

                await requests.Writer.WriteAsync(
                    new ServerRequestContext(session.Description, message),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            RemoveSession(session);
        }
    }

    private void RemoveSession(ClientSession session)
    {
        _sessions.TryRemove(session.Description.SessionId, out _);
        session.Dispose();
    }

    private static void ValidateOptions(TcpServerOptions options)
    {
        if (!IPAddress.TryParse(options.ListenAddress, out _))
        {
            throw new ArgumentException("ListenAddress must be an IP address.", nameof(options));
        }

        if (options.Port is < 0 or > 65_535 ||
            options.MaxConnections <= 0 ||
            options.ReceiveBufferSize <= 0 ||
            options.RequestQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TcpCommunicationServer));
        }
    }

    private sealed class ClientSession : IDisposable
    {
        private int _disposed;

        public ClientSession(TcpClient client, CommunicationSession description)
        {
            Client = client;
            Description = description;
            Stream = client.GetStream();
        }

        public TcpClient Client { get; }

        public NetworkStream Stream { get; }

        public CommunicationSession Description { get; }

        public SemaphoreSlim SendGate { get; } = new(1, 1);

        public Task? ReceiveTask { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Stream.Dispose();
            Client.Dispose();
        }
    }
}
