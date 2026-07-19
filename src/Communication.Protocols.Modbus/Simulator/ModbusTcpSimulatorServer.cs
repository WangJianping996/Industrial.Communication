using System.Buffers;
using System.Collections.Concurrent;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Codecs;
using Communication.Protocols.Modbus.Models;
using Communication.Transports.Options;
using Communication.Transports.Tcp;

namespace Communication.Protocols.Modbus.Simulator;

/// <summary>Hosts a scriptable Modbus TCP slave on a real TCP listener.</summary>
public sealed class ModbusTcpSimulatorServer : IAsyncDisposable
{
    private readonly TcpCommunicationServer _server;
    private readonly ModbusSlave _slave;
    private readonly ModbusSimulatorOptions _options;
    private readonly ConcurrentDictionary<string, List<byte>> _buffers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private long _sequence;
    private int _disposed;

    /// <summary>Initializes a Modbus TCP simulator.</summary>
    public ModbusTcpSimulatorServer(
        TcpServerOptions transportOptions,
        ModbusSlave? slave = null,
        ModbusSimulatorOptions? options = null)
    {
        _server = new TcpCommunicationServer(transportOptions ?? throw new ArgumentNullException(nameof(transportOptions)));
        _slave = slave ?? new ModbusSlave();
        _options = options ?? new ModbusSimulatorOptions();
        if (_options.ResponseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>Gets the backing data store.</summary>
    public ModbusDataStore DataStore => _slave.DataStore;

    /// <summary>Gets the actual listener port, or zero before start.</summary>
    public int BoundPort => _server.BoundPort;

    /// <summary>Gets the server connection state.</summary>
    public ConnectionState State => _server.State;

    /// <summary>Starts the listener and request processor.</summary>
    public async ValueTask<CommunicationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CommunicationResult result = await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && _loop is null)
        {
            _cancellation = new CancellationTokenSource();
            _loop = RunAsync(_cancellation.Token);
        }

        return result;
    }

    /// <summary>Stops the listener and all active sessions.</summary>
    public async ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? loop = _loop;
        _cancellation = null;
        _loop = null;
        cancellation?.Cancel();
        CommunicationResult result = await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
            {
            }
        }

        cancellation?.Dispose();
        _buffers.Clear();
        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (ServerRequestContext context in _server.ReadRequestsAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            string sessionId = context.Session.SessionId;
            List<byte> buffer = _buffers.GetOrAdd(sessionId, _ => []);
            buffer.AddRange(context.Message.Payload.ToArray());
            while (buffer.Count > 0)
            {
                ProtocolDecodeResult<ModbusRequest> decoded = ModbusTcpFrameCodec.TryDecodeRequest(
                    new ReadOnlySequence<byte>(buffer.ToArray()));
                if (decoded.Status == DecodeStatus.NeedMoreData)
                {
                    break;
                }

                int consumed = checked((int)Math.Max(1, decoded.Consumed));
                consumed = Math.Min(consumed, buffer.Count);
                byte[] rawRequest = buffer.Take(consumed).ToArray();
                buffer.RemoveRange(0, consumed);
                if (decoded.Status == DecodeStatus.Done && decoded.Value is not null &&
                    (decoded.Value.UnitId == _slave.UnitId || decoded.Value.UnitId == 0))
                {
                    _ = HandleRequestAsync(context.Session, decoded.Value, rawRequest, cancellationToken);
                }
            }

            foreach (string stale in _buffers.Keys.Except(
                _server.Sessions.Select(session => session.SessionId),
                StringComparer.Ordinal))
            {
                _buffers.TryRemove(stale, out _);
            }
        }
    }

    private async Task HandleRequestAsync(
        CommunicationSession session,
        ModbusRequest request,
        byte[] rawRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            long sequence = Interlocked.Increment(ref _sequence);
            SimulationResponseDirective directive = _options.Script is null
                ? new SimulationResponseDirective()
                : await _options.Script.GetDirectiveAsync(new SimulationRequest(
                    "modbus-tcp",
                    "modbus-tcp-simulator",
                    session.SessionId,
                    sequence,
                    rawRequest), cancellationToken).ConfigureAwait(false);
            TimeSpan delay = _options.ResponseDelay + directive.Delay;
            if (delay < TimeSpan.Zero)
            {
                return;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (_options.DisconnectOnRequest || directive.Disconnect)
            {
                await _server.DisconnectSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_options.DropResponses || directive.DropResponse)
            {
                return;
            }

            if (request.UnitId == 0)
            {
                _slave.ProcessRequest(request);
                return;
            }

            ModbusResponse response = _options.ForcedException.HasValue
                ? new ModbusResponse(
                    request.TransactionId,
                    request.UnitId,
                    request.FunctionCode,
                    ReadOnlyMemory<byte>.Empty,
                    _options.ForcedException.Value)
                : _slave.ProcessRequest(request);
            byte[] rawResponse = directive.ResponseOverride?.ToArray() ??
                ModbusTcpFrameCodec.EncodeResponse(response).ToArray();
            if (directive.CorruptResponse && rawResponse.Length > 0)
            {
                rawResponse[rawResponse.Length - 1] ^= 0xFF;
            }

            await _server.SendAsync(session.SessionId, rawResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A scripted response is isolated to its request and must not stop the simulator loop.
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ModbusTcpSimulatorServer));
        }
    }
}
