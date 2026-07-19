using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Plc;
using Communication.Protocols.Modbus.Models;

namespace Communication.Protocols.Modbus.Client;

/// <summary>Adapts Modbus tables to protocol-independent PLC variable definitions.</summary>
public sealed class ModbusPlcClient : IPlcClient
{
    private readonly ModbusClient _client;
    private readonly ModbusPlcClientOptions _options;
    private readonly ModbusPlcAddressParser _parser;
    private readonly IValueConverter _converter;

    /// <summary>Initializes a unified PLC adapter over an existing Modbus client.</summary>
    public ModbusPlcClient(
        ModbusClient client,
        ModbusPlcClientOptions? options = null,
        ModbusPlcAddressParser? parser = null,
        IValueConverter? converter = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new ModbusPlcClientOptions();
        if (_options.MaxReadBits is <= 0 or > 2000 || _options.MaxReadRegisters is <= 0 or > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _parser = parser ?? new ModbusPlcAddressParser();
        _converter = converter ?? new PlcValueConverter();
    }

    /// <inheritdoc />
    public ConnectionState State => _client.State;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        _client.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default) =>
        _client.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<VariableValue>> ReadAsync(
        VariableDefinition variable,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommunicationResult<VariableValue>> results = await ReadAsync(
            new[] { variable }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult<VariableValue>>> ReadAsync(
        IReadOnlyList<VariableDefinition> variables,
        CancellationToken cancellationToken = default)
    {
        if (variables is null)
        {
            throw new ArgumentNullException(nameof(variables));
        }

        var results = new CommunicationResult<VariableValue>?[variables.Count];
        List<ReadItem> items = [];
        for (int index = 0; index < variables.Count; index++)
        {
            CommunicationResult<ReadItem> parsed = ParseItem(variables[index], index);
            if (parsed.IsSuccess)
            {
                items.Add(parsed.Value!);
            }
            else
            {
                results[index] = CommunicationResult<VariableValue>.Failure(parsed.Error!);
            }
        }

        foreach (ReadGroup group in CreateGroups(items))
        {
            CommunicationResult<ReadOnlyMemory<byte>> read = await ReadGroupAsync(group, cancellationToken)
                .ConfigureAwait(false);
            foreach (ReadItem item in group.Items)
            {
                int unitOffset = item.Address.Offset - group.Start;
                int byteOffset = IsBitArea(group.Area) ? unitOffset : unitOffset * 2;
                CommunicationResult<ReadOnlyMemory<byte>> slice = read.IsSuccess
                    ? CommunicationResult<ReadOnlyMemory<byte>>.Success(
                        read.Value!.Slice(byteOffset, item.ByteCount))
                    : CommunicationResult<ReadOnlyMemory<byte>>.Failure(read.Error!);
                results[item.Index] = ConvertRead(item, slice);
            }
        }

        return results.Select((result, index) => result ?? CommunicationResult<VariableValue>.Failure(
            new CommunicationError(
                CommunicationErrorCode.ProtocolError,
                $"No Modbus batch result was produced for variable '{variables[index].Name}'.")))
            .ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> WriteAsync(
        PlcWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        VariableDefinition variable = request.Definition;
        if ((variable.Access & VariableAccess.Write) == 0)
        {
            return Failure(CommunicationErrorCode.InvalidState, $"Variable '{variable.Name}' is not writable.");
        }

        CommunicationResult<ModbusPlcAddress> address = _parser.ParseModbus(variable.Address);
        if (!address.IsSuccess)
        {
            return CommunicationResult.Failure(address.Error!);
        }

        CommunicationError? compatibility = ValidateCompatibility(variable, address.Value!, writing: true);
        if (compatibility is not null)
        {
            return CommunicationResult.Failure(compatibility);
        }

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes(request.Value, variable);
        if (!encoded.IsSuccess)
        {
            return CommunicationResult.Failure(encoded.Error!);
        }

        if (IsBitArea(address.Value!.Area))
        {
            bool[] bits = encoded.Value!.ToArray().Select(value => value != 0).ToArray();
            return await _client.WriteMultipleCoilsAsync(
                _options.UnitId, address.Value.Offset, bits, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        int registers = (encoded.Value!.Length + 1) / 2;
        ushort[] values = new ushort[registers];
        for (int index = 0; index < registers; index++)
        {
            int offset = index * 2;
            values[index] = (ushort)((encoded.Value.Span[offset] << 8) |
                (offset + 1 < encoded.Value.Length ? encoded.Value.Span[offset + 1] : 0));
        }

        return await _client.WriteMultipleRegistersAsync(
            _options.UnitId, address.Value.Offset, values, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
        IReadOnlyList<PlcWriteRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var results = new CommunicationResult[requests.Count];
        for (int index = 0; index < requests.Count; index++)
        {
            try
            {
                results[index] = await WriteAsync(requests[index], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                results[index] = CommunicationResult.Failure(new CommunicationError(
                    CommunicationErrorCode.Unknown,
                    $"Modbus write for '{requests[index].Definition.Name}' threw an exception.",
                    exception.Message,
                    exception));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private CommunicationResult<ReadItem> ParseItem(VariableDefinition variable, int index)
    {
        if ((variable.Access & VariableAccess.Read) == 0)
        {
            return ItemFailure($"Variable '{variable.Name}' is not readable.", CommunicationErrorCode.InvalidState);
        }

        CommunicationResult<ModbusPlcAddress> parsed = _parser.ParseModbus(variable.Address);
        if (!parsed.IsSuccess)
        {
            return CommunicationResult<ReadItem>.Failure(parsed.Error!);
        }

        CommunicationError? compatibility = ValidateCompatibility(variable, parsed.Value!, writing: false);
        if (compatibility is not null)
        {
            return CommunicationResult<ReadItem>.Failure(compatibility);
        }

        CommunicationResult<int> byteCount = PlcValueConverter.GetByteCount(variable);
        if (!byteCount.IsSuccess)
        {
            return CommunicationResult<ReadItem>.Failure(byteCount.Error!);
        }

        int units = IsBitArea(parsed.Value!.Area) ? variable.Length : (byteCount.Value + 1) / 2;
        int limit = IsBitArea(parsed.Value.Area) ? _options.MaxReadBits : _options.MaxReadRegisters;
        if (units > limit || (long)parsed.Value.Offset + units > ushort.MaxValue + 1L)
        {
            return ItemFailure("The Modbus variable range exceeds the configured protocol limit.",
                CommunicationErrorCode.InvalidAddress);
        }

        return CommunicationResult<ReadItem>.Success(new ReadItem(
            index, variable, parsed.Value, byteCount.Value, units));
    }

    private async ValueTask<CommunicationResult<ReadOnlyMemory<byte>>> ReadGroupAsync(
        ReadGroup group,
        CancellationToken cancellationToken)
    {
        if (IsBitArea(group.Area))
        {
            ValueTask<CommunicationResult<IReadOnlyList<bool>>> operation = group.Area == ModbusDataArea.Coils
                ? _client.ReadCoilsAsync(_options.UnitId, group.Start, checked((ushort)group.Units),
                    cancellationToken: cancellationToken)
                : _client.ReadDiscreteInputsAsync(_options.UnitId, group.Start, checked((ushort)group.Units),
                    cancellationToken: cancellationToken);
            CommunicationResult<IReadOnlyList<bool>> result = await operation.ConfigureAwait(false);
            return result.IsSuccess
                ? CommunicationResult<ReadOnlyMemory<byte>>.Success(
                    result.Value!.Select(value => value ? (byte)1 : (byte)0).ToArray())
                : CommunicationResult<ReadOnlyMemory<byte>>.Failure(result.Error!);
        }

        ValueTask<CommunicationResult<IReadOnlyList<ushort>>> registerOperation =
            group.Area == ModbusDataArea.HoldingRegisters
                ? _client.ReadHoldingRegistersAsync(_options.UnitId, group.Start, checked((ushort)group.Units),
                    cancellationToken: cancellationToken)
                : _client.ReadInputRegistersAsync(_options.UnitId, group.Start, checked((ushort)group.Units),
                    cancellationToken: cancellationToken);
        CommunicationResult<IReadOnlyList<ushort>> registers = await registerOperation.ConfigureAwait(false);
        if (!registers.IsSuccess)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(registers.Error!);
        }

        byte[] bytes = new byte[registers.Value!.Count * 2];
        for (int index = 0; index < registers.Value.Count; index++)
        {
            bytes[index * 2] = (byte)(registers.Value[index] >> 8);
            bytes[(index * 2) + 1] = (byte)registers.Value[index];
        }

        return CommunicationResult<ReadOnlyMemory<byte>>.Success(bytes);
    }

    private CommunicationResult<VariableValue> ConvertRead(
        ReadItem item,
        CommunicationResult<ReadOnlyMemory<byte>> read)
    {
        if (!read.IsSuccess)
        {
            return CommunicationResult<VariableValue>.Failure(read.Error!);
        }

        CommunicationResult<object?> converted = _converter.FromBytes(read.Value!.Span, item.Variable);
        return converted.IsSuccess
            ? CommunicationResult<VariableValue>.Success(new VariableValue(
                item.Variable, converted.Value, VariableQuality.Good, DateTimeOffset.UtcNow))
            : CommunicationResult<VariableValue>.Failure(converted.Error!);
    }

    private IEnumerable<ReadGroup> CreateGroups(List<ReadItem> items)
    {
        foreach (IGrouping<ModbusDataArea, ReadItem> area in items.GroupBy(item => item.Address.Area))
        {
            int limit = IsBitArea(area.Key) ? _options.MaxReadBits : _options.MaxReadRegisters;
            ReadGroup? current = null;
            foreach (ReadItem item in area.OrderBy(item => item.Address.Offset))
            {
                int end = item.Address.Offset + item.Units;
                if (current is null || end - current.Start > limit)
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = new ReadGroup(area.Key, item.Address.Offset, item.Units, [item]);
                }
                else
                {
                    current.Items.Add(item);
                    current.Units = Math.Max(current.Units, end - current.Start);
                }
            }

            if (current is not null)
            {
                yield return current;
            }
        }
    }

    private static CommunicationError? ValidateCompatibility(
        VariableDefinition variable,
        ModbusPlcAddress address,
        bool writing)
    {
        bool bits = IsBitArea(address.Area);
        if (bits != (variable.DataType == PlcDataType.Boolean))
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                bits ? "Modbus C/DI tables require Boolean variables." : "Modbus HR/IR tables do not accept Boolean variables.");
        }

        if (writing && address.Area is ModbusDataArea.DiscreteInputs or ModbusDataArea.InputRegisters)
        {
            return new CommunicationError(CommunicationErrorCode.InvalidState, "Modbus DI/IR tables are read-only.");
        }

        return null;
    }

    private static bool IsBitArea(ModbusDataArea area) =>
        area is ModbusDataArea.Coils or ModbusDataArea.DiscreteInputs;

    private static CommunicationResult<ReadItem> ItemFailure(string message, CommunicationErrorCode code) =>
        CommunicationResult<ReadItem>.Failure(new CommunicationError(code, message));

    private static CommunicationResult Failure(CommunicationErrorCode code, string message) =>
        CommunicationResult.Failure(new CommunicationError(code, message));

    private sealed record ReadItem(
        int Index,
        VariableDefinition Variable,
        ModbusPlcAddress Address,
        int ByteCount,
        int Units);

    private sealed class ReadGroup
    {
        public ReadGroup(ModbusDataArea area, ushort start, int units, List<ReadItem> items)
        {
            Area = area;
            Start = start;
            Units = units;
            Items = items;
        }

        public ModbusDataArea Area { get; }

        public ushort Start { get; }

        public int Units { get; set; }

        public List<ReadItem> Items { get; }
    }
}
