using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Plc;
using Communication.Protocols.Mc.Models;

namespace Communication.Protocols.Mc;

/// <summary>Maps protocol-independent PLC variables onto MC X/Y/M/D/W batch access.</summary>
public sealed class McPlcClient : IPlcClient
{
    private readonly IMcDataAccess _access;
    private readonly McAddressParser _parser;
    private readonly IValueConverter _converter;
    private readonly McClientOptions _options;

    /// <summary>Initializes an MC PLC client over replaceable device access.</summary>
    public McPlcClient(
        IMcDataAccess access,
        McClientOptions? options = null,
        McAddressParser? parser = null,
        IValueConverter? converter = null)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _options = options ?? new McClientOptions();
        if (_options.MaxWordPoints <= 0 || _options.MaxWordPoints > ushort.MaxValue ||
            _options.MaxBitPoints <= 0 || _options.MaxBitPoints > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _parser = parser ?? new McAddressParser();
        _converter = converter ?? new PlcValueConverter();
    }

    /// <inheritdoc />
    public ConnectionState State => _access.State;

    /// <inheritdoc />
    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        _access.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default) =>
        _access.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<VariableValue>> ReadAsync(
        VariableDefinition variable,
        CancellationToken cancellationToken = default)
    {
        CommunicationResult<ReadItem> parsed = ParseReadItem(variable, 0);
        if (!parsed.IsSuccess)
        {
            return CommunicationResult<VariableValue>.Failure(parsed.Error!);
        }

        ReadItem item = parsed.Value!;
        CommunicationResult<ReadOnlyMemory<byte>> read = await _access.ReadAsync(
            item.Address,
            checked((ushort)item.Points),
            cancellationToken).ConfigureAwait(false);
        return ConvertRead(item, read.IsSuccess
            ? CommunicationResult<ReadOnlyMemory<byte>>.Success(read.Value!.Slice(0, item.ByteCount))
            : read);
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
            CommunicationResult<ReadItem> parsed = ParseReadItem(variables[index], index);
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
            McAddress start = group.Items[0].Address with { DeviceNumber = group.Start };
            CommunicationResult<ReadOnlyMemory<byte>> read = await _access.ReadAsync(
                start,
                checked((ushort)group.Points),
                cancellationToken).ConfigureAwait(false);
            foreach (ReadItem item in group.Items)
            {
                int byteOffset = item.Address.IsBitDevice
                    ? item.Address.DeviceNumber - group.Start
                    : (item.Address.DeviceNumber - group.Start) * 2;
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
                $"No MC batch result was produced for variable '{variables[index].Name}'.")))
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

        CommunicationResult<McAddress> parsed = _parser.ParseMc(variable.Address);
        if (!parsed.IsSuccess)
        {
            return CommunicationResult.Failure(parsed.Error!);
        }

        CommunicationError? compatibility = ValidateCompatibility(variable, parsed.Value!);
        if (compatibility is not null)
        {
            return CommunicationResult.Failure(compatibility);
        }

        CommunicationResult<int> byteCount = PlcValueConverter.GetByteCount(variable);
        if (!byteCount.IsSuccess)
        {
            return CommunicationResult.Failure(byteCount.Error!);
        }

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes(request.Value, variable);
        if (!encoded.IsSuccess)
        {
            return CommunicationResult.Failure(encoded.Error!);
        }

        int points = parsed.Value!.IsBitDevice ? variable.Length : (byteCount.Value + 1) / 2;
        byte[] normalized = new byte[parsed.Value.IsBitDevice ? points : points * 2];
        encoded.Value!.CopyTo(normalized);
        return await _access.WriteAsync(
            parsed.Value,
            checked((ushort)points),
            normalized,
            cancellationToken).ConfigureAwait(false);
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
                    $"MC write for '{requests[index].Definition.Name}' threw an exception.",
                    exception.Message,
                    exception));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _access.DisposeAsync();

    private CommunicationResult<ReadItem> ParseReadItem(VariableDefinition variable, int index)
    {
        if (variable is null)
        {
            throw new ArgumentNullException(nameof(variable));
        }

        if ((variable.Access & VariableAccess.Read) == 0)
        {
            return ItemFailure($"Variable '{variable.Name}' is not readable.", CommunicationErrorCode.InvalidState);
        }

        CommunicationResult<McAddress> parsed = _parser.ParseMc(variable.Address);
        if (!parsed.IsSuccess)
        {
            return CommunicationResult<ReadItem>.Failure(parsed.Error!);
        }

        CommunicationError? compatibility = ValidateCompatibility(variable, parsed.Value!);
        if (compatibility is not null)
        {
            return CommunicationResult<ReadItem>.Failure(compatibility);
        }

        CommunicationResult<int> byteCount = PlcValueConverter.GetByteCount(variable);
        if (!byteCount.IsSuccess)
        {
            return CommunicationResult<ReadItem>.Failure(byteCount.Error!);
        }

        int points = parsed.Value!.IsBitDevice ? variable.Length : (byteCount.Value + 1) / 2;
        int maximum = parsed.Value.IsBitDevice ? _options.MaxBitPoints : _options.MaxWordPoints;
        if (points > maximum)
        {
            return ItemFailure(
                $"Variable '{variable.Name}' requires {points} MC points; the configured limit is {maximum}.",
                CommunicationErrorCode.InvalidValue);
        }

        return CommunicationResult<ReadItem>.Success(new ReadItem(
            index,
            variable,
            parsed.Value,
            byteCount.Value,
            points));
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
                item.Variable,
                converted.Value,
                VariableQuality.Good,
                DateTimeOffset.UtcNow))
            : CommunicationResult<VariableValue>.Failure(converted.Error!);
    }

    private IEnumerable<ReadGroup> CreateGroups(List<ReadItem> items)
    {
        foreach (IGrouping<McDeviceCode, ReadItem> device in items.GroupBy(item => item.Address.DeviceCode))
        {
            ReadGroup? current = null;
            int maximum = device.Key is McDeviceCode.X or McDeviceCode.Y or McDeviceCode.M
                ? _options.MaxBitPoints
                : _options.MaxWordPoints;
            foreach (ReadItem item in device.OrderBy(item => item.Address.DeviceNumber))
            {
                int itemEnd = checked(item.Address.DeviceNumber + item.Points);
                if (current is null || itemEnd - current.Start > maximum)
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = new ReadGroup(item.Address.DeviceNumber, item.Points, [item]);
                }
                else
                {
                    current.Items.Add(item);
                    current.Points = Math.Max(current.Points, itemEnd - current.Start);
                }
            }

            if (current is not null)
            {
                yield return current;
            }
        }
    }

    private static CommunicationError? ValidateCompatibility(VariableDefinition variable, McAddress address)
    {
        if (address.IsBitDevice && variable.DataType != PlcDataType.Boolean)
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "MC X/Y/M devices require Boolean variables.");
        }

        if (!address.IsBitDevice && variable.DataType == PlcDataType.Boolean)
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "MC D/W word devices do not accept Boolean variables.");
        }

        return null;
    }

    private static CommunicationResult<ReadItem> ItemFailure(string message, CommunicationErrorCode code) =>
        CommunicationResult<ReadItem>.Failure(new CommunicationError(code, message));

    private static CommunicationResult Failure(CommunicationErrorCode code, string message) =>
        CommunicationResult.Failure(new CommunicationError(code, message));

    private sealed record ReadItem(
        int Index,
        VariableDefinition Variable,
        McAddress Address,
        int ByteCount,
        int Points);

    private sealed class ReadGroup
    {
        public ReadGroup(int start, int points, List<ReadItem> items)
        {
            Start = start;
            Points = points;
            Items = items;
        }

        public int Start { get; }

        public int Points { get; set; }

        public List<ReadItem> Items { get; }
    }
}
