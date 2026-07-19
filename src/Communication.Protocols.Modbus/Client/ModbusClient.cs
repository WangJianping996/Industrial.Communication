using System.Diagnostics;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Reliability;
using Communication.Protocols.Modbus.Codecs;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Client;

/// <summary>Provides typed Modbus TCP and RTU client operations using zero-based protocol addresses.</summary>
public sealed class ModbusClient : IAsyncDisposable
{
    private readonly ReliableCommunicationClient<ModbusRequest, ModbusResponse> _client;
    private readonly ModbusTransportMode _mode;
    private readonly TimeSpan _rtuInterFrameDelay;
    private readonly SemaphoreSlim _rtuGate = new(1, 1);
    private int _transactionId;
    private long _lastRtuActivity;
    private int _disposed;

    /// <summary>Initializes a Modbus client over an existing byte transport.</summary>
    public ModbusClient(
        ITransportChannel channel,
        ModbusTransportMode mode,
        ModbusClientOptions? options = null,
        IRetryPolicy? retryPolicy = null,
        IReconnectPolicy? reconnectPolicy = null,
        IMessageMonitor? monitor = null,
        IEnumerable<IConnectionRecoveryHandler>? recoveryHandlers = null)
    {
        if (channel is null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        options ??= new ModbusClientOptions();
        if (options.DefaultTimeout <= TimeSpan.Zero ||
            options.MaxTcpInFlight <= 0 ||
            options.MaxBufferedBytes < 260 ||
            options.RtuInterFrameDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _mode = mode;
        _rtuInterFrameDelay = options.RtuInterFrameDelay;
        IProtocolCodec<ModbusRequest, ModbusResponse> codec = mode switch
        {
            ModbusTransportMode.Tcp => new ModbusTcpCodec(),
            ModbusTransportMode.Rtu => new ModbusRtuCodec(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        IResponseCorrelator<ModbusRequest, ModbusResponse> correlator = mode == ModbusTransportMode.Tcp
            ? new DelegatingResponseCorrelator<ModbusRequest, ModbusResponse>(
                request => request.TransactionId.ToString(),
                response => response.TransactionId.ToString(),
                options.MaxTcpInFlight)
            : new SingleRequestCorrelator<ModbusRequest, ModbusResponse>();
        _client = new ReliableCommunicationClient<ModbusRequest, ModbusResponse>(
            channel,
            codec,
            correlator,
            new CommunicationClientOptions
            {
                DefaultTimeout = options.DefaultTimeout,
                MaxBufferedBytes = options.MaxBufferedBytes,
                ProtocolName = mode == ModbusTransportMode.Tcp ? "modbus-tcp" : "modbus-rtu",
            },
            retryPolicy,
            reconnectPolicy,
            monitor,
            recoveryHandlers);
    }

    /// <summary>Gets the current transport state.</summary>
    public ConnectionState State => _client.State;

    /// <summary>Raised when the underlying connection state changes.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged
    {
        add => _client.StateChanged += value;
        remove => _client.StateChanged -= value;
    }

    /// <summary>Connects the underlying channel.</summary>
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        _client.ConnectAsync(cancellationToken);

    /// <summary>Disconnects the underlying channel.</summary>
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default) =>
        _client.DisconnectAsync(cancellationToken);

    /// <summary>Executes an advanced raw Modbus request.</summary>
    public async ValueTask<CommunicationResult<ModbusResponse>> ExecuteAsync(
        ModbusRequest request,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        CommunicationError? validation = ValidateUnit(request.UnitId);
        validation ??= ModbusPduCodec.ValidateRequest(request);
        if (validation is not null)
        {
            return CommunicationResult<ModbusResponse>.Failure(validation);
        }

        if (_mode == ModbusTransportMode.Rtu)
        {
            await _rtuGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WaitForRtuSilenceAsync(cancellationToken).ConfigureAwait(false);
                return await ExecuteCoreAsync(request, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _lastRtuActivity, Stopwatch.GetTimestamp());
                _rtuGate.Release();
            }
        }

        return await ExecuteCoreAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommunicationResult<ModbusResponse>> ExecuteCoreAsync(
        ModbusRequest request,
        CommunicationRequestOptions? options,
        CancellationToken cancellationToken)
    {

        ModbusRequest correlated = request with
        {
            TransactionId = _mode == ModbusTransportMode.Tcp
                ? unchecked((ushort)Interlocked.Increment(ref _transactionId))
                : (ushort)0,
        };
        CommunicationResult<ModbusResponse> result = await _client
            .ExecuteAsync(correlated, options, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        ModbusResponse response = result.Value!;
        if (response.UnitId != correlated.UnitId || response.FunctionCode != correlated.FunctionCode)
        {
            return ProtocolFailure<ModbusResponse>("The Modbus response unit or function does not match the request.");
        }

        if (response.IsException)
        {
            return CommunicationResult<ModbusResponse>.Failure(response.ToCommunicationError());
        }

        return CommunicationResult<ModbusResponse>.Success(response);
    }

    /// <summary>Reads coils using function 01.</summary>
    public ValueTask<CommunicationResult<IReadOnlyList<bool>>> ReadCoilsAsync(
        byte unitId,
        ushort address,
        ushort quantity,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ReadBitsAsync(unitId, ModbusFunctionCode.ReadCoils, address, quantity, options, cancellationToken);

    /// <summary>Reads discrete inputs using function 02.</summary>
    public ValueTask<CommunicationResult<IReadOnlyList<bool>>> ReadDiscreteInputsAsync(
        byte unitId,
        ushort address,
        ushort quantity,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ReadBitsAsync(unitId, ModbusFunctionCode.ReadDiscreteInputs, address, quantity, options, cancellationToken);

    /// <summary>Reads holding registers using function 03.</summary>
    public ValueTask<CommunicationResult<IReadOnlyList<ushort>>> ReadHoldingRegistersAsync(
        byte unitId,
        ushort address,
        ushort quantity,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(unitId, ModbusFunctionCode.ReadHoldingRegisters, address, quantity, options, cancellationToken);

    /// <summary>Reads input registers using function 04.</summary>
    public ValueTask<CommunicationResult<IReadOnlyList<ushort>>> ReadInputRegistersAsync(
        byte unitId,
        ushort address,
        ushort quantity,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ReadRegistersAsync(unitId, ModbusFunctionCode.ReadInputRegisters, address, quantity, options, cancellationToken);

    /// <summary>Writes one coil using function 05.</summary>
    public async ValueTask<CommunicationResult> WriteSingleCoilAsync(
        byte unitId,
        ushort address,
        bool value,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync(ModbusRequest.WriteCoil(unitId, address, value), options, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Writes one holding register using function 06.</summary>
    public async ValueTask<CommunicationResult> WriteSingleRegisterAsync(
        byte unitId,
        ushort address,
        ushort value,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync(ModbusRequest.WriteRegister(unitId, address, value), options, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Writes coils using function 15.</summary>
    public async ValueTask<CommunicationResult> WriteMultipleCoilsAsync(
        byte unitId,
        ushort address,
        IReadOnlyList<bool> values,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync(ModbusRequest.WriteCoils(unitId, address, values), options, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Writes holding registers using function 16.</summary>
    public async ValueTask<CommunicationResult> WriteMultipleRegistersAsync(
        byte unitId,
        ushort address,
        IReadOnlyList<ushort> values,
        CommunicationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync(ModbusRequest.WriteRegisters(unitId, address, values), options, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _client.DisposeAsync().ConfigureAwait(false);
        await _rtuGate.WaitAsync().ConfigureAwait(false);
        _rtuGate.Release();
        _rtuGate.Dispose();
    }

    private async ValueTask WaitForRtuSilenceAsync(CancellationToken cancellationToken)
    {
        long lastActivity = Interlocked.Read(ref _lastRtuActivity);
        if (lastActivity == 0 || _rtuInterFrameDelay <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan elapsed = TimeSpan.FromSeconds(
            (Stopwatch.GetTimestamp() - lastActivity) / (double)Stopwatch.Frequency);
        TimeSpan remaining = _rtuInterFrameDelay - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<CommunicationResult<IReadOnlyList<bool>>> ReadBitsAsync(
        byte unitId,
        ModbusFunctionCode function,
        ushort address,
        ushort quantity,
        CommunicationRequestOptions? options,
        CancellationToken cancellationToken)
    {
        CommunicationResult<ModbusResponse> response = await ExecuteAsync(
            ModbusRequest.Read(unitId, function, address, quantity),
            options,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return CommunicationResult<IReadOnlyList<bool>>.Failure(response.Error!);
        }

        int expectedBytes = (quantity + 7) / 8;
        if (response.Value!.Data.Length != expectedBytes)
        {
            return ProtocolFailure<IReadOnlyList<bool>>("The Modbus bit response length does not match the requested quantity.");
        }

        bool[] values = new bool[quantity];
        for (int index = 0; index < quantity; index++)
        {
            values[index] = (response.Value.Data.Span[index / 8] & (1 << (index % 8))) != 0;
        }

        return CommunicationResult<IReadOnlyList<bool>>.Success(values);
    }

    private async ValueTask<CommunicationResult<IReadOnlyList<ushort>>> ReadRegistersAsync(
        byte unitId,
        ModbusFunctionCode function,
        ushort address,
        ushort quantity,
        CommunicationRequestOptions? options,
        CancellationToken cancellationToken)
    {
        CommunicationResult<ModbusResponse> response = await ExecuteAsync(
            ModbusRequest.Read(unitId, function, address, quantity),
            options,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return CommunicationResult<IReadOnlyList<ushort>>.Failure(response.Error!);
        }

        if (response.Value!.Data.Length != quantity * 2)
        {
            return ProtocolFailure<IReadOnlyList<ushort>>("The Modbus register response length does not match the requested quantity.");
        }

        ushort[] values = new ushort[quantity];
        for (int index = 0; index < quantity; index++)
        {
            values[index] = ModbusPduCodec.ReadUInt16(response.Value.Data.Span, index * 2);
        }

        return CommunicationResult<IReadOnlyList<ushort>>.Success(values);
    }

    private async ValueTask<CommunicationResult> ExecuteWriteAsync(
        ModbusRequest request,
        CommunicationRequestOptions? options,
        CancellationToken cancellationToken)
    {
        CommunicationResult<ModbusResponse> response = await ExecuteAsync(request, options, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return CommunicationResult.Failure(response.Error!);
        }

        byte[] echo = response.Value!.Data.ToArray();
        ushort responseAddress = ModbusPduCodec.ReadUInt16(echo, 0);
        ushort responseValue = ModbusPduCodec.ReadUInt16(echo, 2);
        ushort expectedValue = request.FunctionCode is
            ModbusFunctionCode.WriteMultipleCoils or ModbusFunctionCode.WriteMultipleRegisters
            ? request.Quantity
            : ModbusPduCodec.ReadUInt16(request.Data.Span, 0);
        return responseAddress == request.Address && responseValue == expectedValue
            ? CommunicationResult.Success()
            : CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.ProtocolError,
                "The Modbus write response echo does not match the request."));
    }

    private CommunicationError? ValidateUnit(byte unitId)
    {
        if (_mode == ModbusTransportMode.Rtu && unitId is 0 or > 247)
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidAddress,
                "An RTU client request requires a station in range 1..247; station 0 is broadcast-only.");
        }

        return null;
    }

    private static CommunicationResult<T> ProtocolFailure<T>(string message) =>
        CommunicationResult<T>.Failure(new CommunicationError(CommunicationErrorCode.ProtocolError, message));
}
