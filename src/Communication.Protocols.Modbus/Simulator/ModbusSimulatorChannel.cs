using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Protocols.Modbus.Codecs;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Simulator;

/// <summary>Configures deterministic Modbus simulator faults.</summary>
public sealed record ModbusSimulatorOptions
{
    /// <summary>Gets the base response delay.</summary>
    public TimeSpan ResponseDelay { get; init; }

    /// <summary>Gets an exception forced for every valid request.</summary>
    public ModbusExceptionCode? ForcedException { get; init; }

    /// <summary>Gets whether every response is dropped to simulate timeout.</summary>
    public bool DropResponses { get; init; }

    /// <summary>Gets whether the connection is interrupted on each request.</summary>
    public bool DisconnectOnRequest { get; init; }

    /// <summary>Gets whether RTU CRC bytes are corrupted.</summary>
    public bool CorruptRtuCrc { get; init; }

    /// <summary>Gets an optional reusable raw response script.</summary>
    public ISimulationResponseScript? Script { get; init; }
}

/// <summary>Runs a Modbus TCP or RTU slave entirely in memory for deterministic CI tests.</summary>
public sealed class ModbusSimulatorChannel : ITransportChannel
{
    private readonly ModbusTransportMode _mode;
    private readonly ModbusSlave _slave;
    private readonly ModbusSimulatorOptions _options;
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly Channel<ReadOnlyMemory<byte>> _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _sequence;
    private int _disposed;

    /// <summary>Initializes an in-memory simulator channel.</summary>
    public ModbusSimulatorChannel(
        ModbusTransportMode mode,
        ModbusSlave? slave = null,
        ModbusSimulatorOptions? options = null,
        string? channelId = null)
    {
        _mode = mode;
        _slave = slave ?? new ModbusSlave();
        _options = options ?? new ModbusSimulatorOptions();
        if (_options.ResponseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        ChannelId = channelId ?? $"modbus-{mode.ToString().ToLowerInvariant()}-simulator";
        _stateMachine.StateChanged += (_, args) => StateChanged?.Invoke(this, args);
    }

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <inheritdoc />
    public ConnectionState State => _stateMachine.State;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ConnectionState.Connected)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Success());
        }

        _stateMachine.TransitionTo(State == ConnectionState.Faulted
            ? ConnectionState.Reconnecting
            : ConnectionState.Connecting);
        _stateMachine.TransitionTo(ConnectionState.Connected);
        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ConnectionState.Disconnected)
        {
            return new ValueTask<CommunicationResult>(CommunicationResult.Success());
        }

        _stateMachine.TransitionTo(ConnectionState.Disconnecting);
        _stateMachine.TransitionTo(ConnectionState.Disconnected);
        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult<int>> SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (State != ConnectionState.Connected)
        {
            return new ValueTask<CommunicationResult<int>>(CommunicationResult<int>.Failure(
                new CommunicationError(CommunicationErrorCode.InvalidState, "The simulator is not connected.")));
        }

        byte[] copy = payload.ToArray();
        _ = ProcessAsync(copy, _lifetimeCancellation.Token);
        return new ValueTask<CommunicationResult<int>>(CommunicationResult<int>.Success(copy.Length));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ReadOnlyMemory<byte> response in _responses.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return response;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        if (State != ConnectionState.Disconnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        _responses.Writer.TryComplete();
        _lifetimeCancellation.Dispose();
    }

    private async Task ProcessAsync(byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            ProtocolDecodeResult<ModbusRequest> decoded = _mode == ModbusTransportMode.Tcp
                ? ModbusTcpFrameCodec.TryDecodeRequest(new ReadOnlySequence<byte>(payload))
                : ModbusRtuFrameCodec.TryDecodeRequest(new ReadOnlySequence<byte>(payload));
            if (decoded.Status != DecodeStatus.Done || decoded.Value is null ||
                (decoded.Value.UnitId != _slave.UnitId && decoded.Value.UnitId != 0))
            {
                return;
            }

            long sequence = Interlocked.Increment(ref _sequence);
            SimulationResponseDirective directive = _options.Script is null
                ? new SimulationResponseDirective()
                : await _options.Script.GetDirectiveAsync(new SimulationRequest(
                    _mode == ModbusTransportMode.Tcp ? "modbus-tcp" : "modbus-rtu",
                    ChannelId,
                    null,
                    sequence,
                    payload), cancellationToken).ConfigureAwait(false);
            TimeSpan delay = _options.ResponseDelay + directive.Delay;
            if (delay < TimeSpan.Zero)
            {
                throw new InvalidOperationException("A simulator response delay cannot be negative.");
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (_options.DisconnectOnRequest || directive.Disconnect)
            {
                _stateMachine.TryTransition(
                    ConnectionState.Faulted,
                    new CommunicationError(CommunicationErrorCode.ConnectionFailure, "Scripted simulator disconnect."));
                return;
            }

            if (_options.DropResponses || directive.DropResponse || decoded.Value.UnitId == 0)
            {
                if (decoded.Value.UnitId == 0)
                {
                    _slave.ProcessRequest(decoded.Value);
                }

                return;
            }

            ModbusResponse response = _options.ForcedException.HasValue
                ? new ModbusResponse(
                    decoded.Value.TransactionId,
                    decoded.Value.UnitId,
                    decoded.Value.FunctionCode,
                    ReadOnlyMemory<byte>.Empty,
                    _options.ForcedException.Value)
                : _slave.ProcessRequest(decoded.Value);
            byte[] encoded = directive.ResponseOverride?.ToArray() ?? (_mode == ModbusTransportMode.Tcp
                ? ModbusTcpFrameCodec.EncodeResponse(response).ToArray()
                : ModbusRtuFrameCodec.EncodeResponse(response).ToArray());
            if ((_options.CorruptRtuCrc || directive.CorruptResponse) && encoded.Length > 0)
            {
                encoded[encoded.Length - 1] ^= 0xFF;
            }

            await _responses.Writer.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _stateMachine.TryTransition(
                ConnectionState.Faulted,
                new CommunicationError(CommunicationErrorCode.ConnectionFailure, exception.Message, Exception: exception));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ModbusSimulatorChannel));
        }
    }
}
