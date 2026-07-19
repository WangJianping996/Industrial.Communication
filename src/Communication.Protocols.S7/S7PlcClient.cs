using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Plc;
using Communication.Protocols.S7.Models;

namespace Communication.Protocols.S7;

/// <summary>Maps unified PLC variables onto S7 DB/I/Q/M absolute byte access.</summary>
public sealed class S7PlcClient : IPlcClient
{
    private readonly IS7DataAccess _access;
    private readonly S7AddressParser _parser;
    private readonly IValueConverter _converter;
    private readonly int _maxBatchBytes;

    /// <summary>Initializes an S7 PLC client over replaceable byte access.</summary>
    public S7PlcClient(
        IS7DataAccess access,
        S7ClientOptions? options = null,
        S7AddressParser? parser = null,
        IValueConverter? converter = null)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        options ??= new S7ClientOptions();
        if (options.MaxBatchBytes <= 0 || options.MaxBatchBytes > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _maxBatchBytes = options.MaxBatchBytes;
        _parser = parser ?? new S7AddressParser();
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
        CommunicationResult<ReadOnlyMemory<byte>> read = await _access.ReadBytesAsync(
            item.Address with { BitOffset = null },
            item.ByteCount,
            cancellationToken).ConfigureAwait(false);
        return ConvertRead(item, read);
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
            S7Address start = group.Items[0].Address with { ByteOffset = group.Start, BitOffset = null };
            CommunicationResult<ReadOnlyMemory<byte>> read = await _access.ReadBytesAsync(
                start,
                group.Length,
                cancellationToken).ConfigureAwait(false);
            foreach (ReadItem item in group.Items)
            {
                CommunicationResult<ReadOnlyMemory<byte>> slice = read.IsSuccess
                    ? CommunicationResult<ReadOnlyMemory<byte>>.Success(
                        read.Value!.Slice(item.Address.ByteOffset - group.Start, item.ByteCount))
                    : CommunicationResult<ReadOnlyMemory<byte>>.Failure(read.Error!);
                results[item.Index] = ConvertRead(item, slice);
            }
        }

        return results.Select((result, index) => result ?? CommunicationResult<VariableValue>.Failure(
            new CommunicationError(
                CommunicationErrorCode.ProtocolError,
                $"No S7 batch result was produced for variable '{variables[index].Name}'.")))
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

        CommunicationResult<S7Address> address = _parser.ParseS7(variable.Address);
        if (!address.IsSuccess)
        {
            return CommunicationResult.Failure(address.Error!);
        }

        CommunicationError? compatibility = ValidateCompatibility(variable, address.Value!);
        if (compatibility is not null)
        {
            return CommunicationResult.Failure(compatibility);
        }

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes(request.Value, variable);
        if (!encoded.IsSuccess)
        {
            return CommunicationResult.Failure(encoded.Error!);
        }

        ReadOnlyMemory<byte> writeBytes = variable.DataType == PlcDataType.String
            ? EncodeS7String(encoded.Value!, variable.Length)
            : encoded.Value!;
        if (!address.Value!.BitOffset.HasValue)
        {
            return await _access.WriteBytesAsync(address.Value, writeBytes, cancellationToken).ConfigureAwait(false);
        }

        CommunicationResult<ReadOnlyMemory<byte>> current = await _access.ReadBytesAsync(
            address.Value with { BitOffset = null },
            1,
            cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return CommunicationResult.Failure(current.Error!);
        }

        byte value = current.Value!.Span[0];
        int mask = 1 << address.Value.BitOffset.Value;
        value = encoded.Value!.Span[0] != 0 ? (byte)(value | mask) : (byte)(value & ~mask);
        return await _access.WriteBytesAsync(
                address.Value with { BitOffset = null },
                new byte[] { value },
                cancellationToken)
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
                    $"S7 write for '{requests[index].Definition.Name}' threw an exception.",
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

        CommunicationResult<S7Address> parsed = _parser.ParseS7(variable.Address);
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

        return CommunicationResult<ReadItem>.Success(new ReadItem(
            index,
            variable,
            parsed.Value!,
            parsed.Value!.BitOffset.HasValue
                ? 1
                : variable.DataType == PlcDataType.String ? byteCount.Value + 2 : byteCount.Value));
    }

    private CommunicationResult<VariableValue> ConvertRead(
        ReadItem item,
        CommunicationResult<ReadOnlyMemory<byte>> read)
    {
        if (!read.IsSuccess)
        {
            return CommunicationResult<VariableValue>.Failure(read.Error!);
        }

        ReadOnlyMemory<byte> bytes = read.Value!;
        if (item.Address.BitOffset.HasValue)
        {
            bytes = new byte[]
            {
                (byte)((bytes.Span[0] & (1 << item.Address.BitOffset.Value)) != 0 ? 1 : 0),
            };
        }

        if (item.Variable.DataType == PlcDataType.String)
        {
            if (bytes.Length < 2 || bytes.Span[0] > item.Variable.Length ||
                bytes.Span[1] > bytes.Span[0] || bytes.Span[1] > item.Variable.Length)
            {
                return CommunicationResult<VariableValue>.Failure(new CommunicationError(
                    CommunicationErrorCode.ProtocolError,
                    $"S7 STRING '{item.Variable.Name}' has an invalid maximum/current length header."));
            }

            byte[] content = new byte[item.Variable.Length];
            bytes.Slice(2, bytes.Span[1]).CopyTo(content);
            bytes = content;
        }

        CommunicationResult<object?> converted = _converter.FromBytes(bytes.Span, item.Variable);
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
        foreach (IGrouping<(S7MemoryArea Area, ushort Db), ReadItem> area in items.GroupBy(item =>
                     (item.Address.Area, item.Address.DbNumber)))
        {
            ReadGroup? current = null;
            foreach (ReadItem item in area.OrderBy(item => item.Address.ByteOffset))
            {
                int itemEnd = checked(item.Address.ByteOffset + item.ByteCount);
                if (current is null || itemEnd - current.Start > _maxBatchBytes)
                {
                    if (current is not null)
                    {
                        yield return current;
                    }

                    current = new ReadGroup(item.Address.ByteOffset, item.ByteCount, [item]);
                }
                else
                {
                    current.Items.Add(item);
                    current.Length = Math.Max(current.Length, itemEnd - current.Start);
                }
            }

            if (current is not null)
            {
                yield return current;
            }
        }
    }

    private static CommunicationError? ValidateCompatibility(VariableDefinition variable, S7Address address)
    {
        if (address.BitOffset.HasValue && (variable.DataType != PlcDataType.Boolean || variable.Length != 1))
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "An S7 bit address requires one Boolean value.");
        }

        if (!address.BitOffset.HasValue && variable.DataType == PlcDataType.Boolean)
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "An S7 Boolean variable requires a DBX, I, Q, or M bit address.");
        }

        if (variable.DataType == PlcDataType.String && variable.Length > 254)
        {
            return new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "An S7 STRING length must be between 1 and 254 bytes.");
        }

        return null;
    }

    private static ReadOnlyMemory<byte> EncodeS7String(ReadOnlyMemory<byte> encoded, int maximumLength)
    {
        byte[] data = new byte[maximumLength + 2];
        data[0] = checked((byte)maximumLength);
        int currentLength = encoded.Span.IndexOf((byte)0);
        if (currentLength < 0)
        {
            currentLength = encoded.Length;
        }

        data[1] = checked((byte)currentLength);
        encoded.Slice(0, currentLength).CopyTo(data.AsMemory(2));
        return data;
    }

    private static CommunicationResult<ReadItem> ItemFailure(string message, CommunicationErrorCode code) =>
        CommunicationResult<ReadItem>.Failure(new CommunicationError(code, message));

    private static CommunicationResult Failure(CommunicationErrorCode code, string message) =>
        CommunicationResult.Failure(new CommunicationError(code, message));

    private sealed record ReadItem(
        int Index,
        VariableDefinition Variable,
        S7Address Address,
        int ByteCount);

    private sealed class ReadGroup
    {
        public ReadGroup(int start, int length, List<ReadItem> items)
        {
            Start = start;
            Length = length;
            Items = items;
        }

        public int Start { get; }

        public int Length { get; set; }

        public List<ReadItem> Items { get; }
    }
}
