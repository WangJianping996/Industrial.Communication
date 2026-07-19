using System.Globalization;
using System.Text;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Plc;

/// <summary>Converts common PLC scalar, string, and byte values with explicit ordering and scale.</summary>
public sealed class PlcValueConverter : IValueConverter
{
    /// <summary>Gets the required byte count for one variable definition.</summary>
    public static CommunicationResult<int> GetByteCount(VariableDefinition definition)
    {
        CommunicationError? validation = ValidateDefinition(definition);
        if (validation is not null)
        {
            return CommunicationResult<int>.Failure(validation);
        }

        int count = definition.DataType switch
        {
            PlcDataType.Boolean or PlcDataType.Byte => definition.Length,
            PlcDataType.Int16 or PlcDataType.UInt16 => checked(definition.Length * 2),
            PlcDataType.Int32 or PlcDataType.UInt32 or PlcDataType.Float32 => checked(definition.Length * 4),
            PlcDataType.Float64 => checked(definition.Length * 8),
            PlcDataType.String or PlcDataType.Bytes => definition.Length,
            _ => 0,
        };
        return count > 0
            ? CommunicationResult<int>.Success(count)
            : CommunicationResult<int>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "The PLC data type is unsupported."));
    }

    /// <inheritdoc />
    public CommunicationResult<object?> FromBytes(ReadOnlySpan<byte> bytes, VariableDefinition definition)
    {
        CommunicationResult<int> byteCount = GetByteCount(definition);
        if (!byteCount.IsSuccess)
        {
            return CommunicationResult<object?>.Failure(byteCount.Error!);
        }

        if (bytes.Length < byteCount.Value)
        {
            return CommunicationResult<object?>.Failure(new CommunicationError(
                CommunicationErrorCode.ProtocolError,
                $"Variable '{definition.Name}' requires {byteCount.Value} bytes but only {bytes.Length} were supplied."));
        }

        try
        {
            object? value = definition.DataType switch
            {
                PlcDataType.Boolean => ReadBoolean(bytes, definition.Length),
                PlcDataType.Byte => definition.Length == 1 ? bytes[0] : bytes.Slice(0, definition.Length).ToArray(),
                PlcDataType.Int16 => ReadValues(bytes, definition, 2, data => unchecked((short)ReadUInt16(data))),
                PlcDataType.UInt16 => ReadValues(bytes, definition, 2, ReadUInt16),
                PlcDataType.Int32 => ReadValues(bytes, definition, 4, data => unchecked((int)ReadUInt32(data))),
                PlcDataType.UInt32 => ReadValues(bytes, definition, 4, ReadUInt32),
                PlcDataType.Float32 => ReadValues(bytes, definition, 4, data =>
                    BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32(data)))),
                PlcDataType.Float64 => ReadValues(bytes, definition, 8, data =>
                    BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64(data)))),
                PlcDataType.String => GetEncoding(definition.StringEncoding)
                    .GetString(bytes.Slice(0, definition.Length).ToArray())
                    .TrimEnd('\0'),
                PlcDataType.Bytes => bytes.Slice(0, definition.Length).ToArray(),
                _ => throw new InvalidOperationException("Unsupported PLC data type."),
            };
            return CommunicationResult<object?>.Success(ApplyReadScale(value, definition));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommunicationResult<object?>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                $"Unable to decode variable '{definition.Name}'.",
                exception.Message,
                exception));
        }
    }

    /// <inheritdoc />
    public CommunicationResult<ReadOnlyMemory<byte>> ToBytes(object? value, VariableDefinition definition)
    {
        CommunicationResult<int> byteCount = GetByteCount(definition);
        if (!byteCount.IsSuccess)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(byteCount.Error!);
        }

        try
        {
            object? unscaled = ApplyWriteScale(value, definition);
            byte[] bytes = definition.DataType switch
            {
                PlcDataType.Boolean => WriteBooleans(unscaled, definition.Length),
                PlcDataType.Byte => WriteBytes(unscaled, definition.Length),
                PlcDataType.Int16 => WriteValues(unscaled, definition, 2, (item, target) =>
                    WriteUInt16(target, unchecked((ushort)Convert.ToInt16(item, CultureInfo.InvariantCulture)))),
                PlcDataType.UInt16 => WriteValues(unscaled, definition, 2, (item, target) =>
                    WriteUInt16(target, Convert.ToUInt16(item, CultureInfo.InvariantCulture))),
                PlcDataType.Int32 => WriteValues(unscaled, definition, 4, (item, target) =>
                    WriteUInt32(target, unchecked((uint)Convert.ToInt32(item, CultureInfo.InvariantCulture)))),
                PlcDataType.UInt32 => WriteValues(unscaled, definition, 4, (item, target) =>
                    WriteUInt32(target, Convert.ToUInt32(item, CultureInfo.InvariantCulture))),
                PlcDataType.Float32 => WriteValues(unscaled, definition, 4, (item, target) =>
                    WriteUInt32(target, unchecked((uint)BitConverter.SingleToInt32Bits(
                        Convert.ToSingle(item, CultureInfo.InvariantCulture))))),
                PlcDataType.Float64 => WriteValues(unscaled, definition, 8, (item, target) =>
                    WriteUInt64(target, unchecked((ulong)BitConverter.DoubleToInt64Bits(
                        Convert.ToDouble(item, CultureInfo.InvariantCulture))))),
                PlcDataType.String => WriteString(unscaled, definition),
                PlcDataType.Bytes => WriteBytes(unscaled, definition.Length),
                _ => throw new InvalidOperationException("Unsupported PLC data type."),
            };
            return CommunicationResult<ReadOnlyMemory<byte>>.Success(bytes);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommunicationResult<ReadOnlyMemory<byte>>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                $"Unable to encode variable '{definition.Name}'.",
                exception.Message,
                exception));
        }
    }

    private static object ReadBoolean(ReadOnlySpan<byte> bytes, int length)
    {
        if (length == 1)
        {
            return bytes[0] != 0;
        }

        bool[] values = new bool[length];
        for (int index = 0; index < length; index++)
        {
            values[index] = bytes[index] != 0;
        }

        return values;
    }

    private static object ReadValues<T>(
        ReadOnlySpan<byte> bytes,
        VariableDefinition definition,
        int elementSize,
        Func<byte[], T> read)
    {
        T[] values = new T[definition.Length];
        for (int index = 0; index < definition.Length; index++)
        {
            byte[] element = bytes.Slice(index * elementSize, elementSize).ToArray();
            Transform(element, definition.ByteOrder);
            values[index] = read(element);
        }

        return definition.Length == 1 ? values[0]! : values;
    }

    private static byte[] WriteValues(
        object? value,
        VariableDefinition definition,
        int elementSize,
        Action<object, byte[]> write)
    {
        object[] values = GetItems(value, definition.Length);
        byte[] result = new byte[definition.Length * elementSize];
        for (int index = 0; index < values.Length; index++)
        {
            byte[] element = new byte[elementSize];
            write(values[index], element);
            Transform(element, definition.ByteOrder);
            element.CopyTo(result, index * elementSize);
        }

        return result;
    }

    private static object[] GetItems(object? value, int length)
    {
        if (length == 1)
        {
            return [value ?? throw new InvalidCastException("A scalar value is required.")];
        }

        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            throw new InvalidCastException($"An enumerable with {length} values is required.");
        }

        object[] values = enumerable.Cast<object>().ToArray();
        return values.Length == length
            ? values
            : throw new InvalidCastException($"Expected {length} values but received {values.Length}.");
    }

    private static byte[] WriteBooleans(object? value, int length)
    {
        object[] values = GetItems(value, length);
        return values.Select(item => Convert.ToBoolean(item, CultureInfo.InvariantCulture) ? (byte)1 : (byte)0).ToArray();
    }

    private static byte[] WriteBytes(object? value, int length)
    {
        if (length == 1 && value is not byte[] && value is not ReadOnlyMemory<byte>)
        {
            return [Convert.ToByte(value, CultureInfo.InvariantCulture)];
        }

        byte[] bytes = value switch
        {
            byte[] array => array.ToArray(),
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            IEnumerable<byte> enumerable => enumerable.ToArray(),
            _ => throw new InvalidCastException("A byte sequence is required."),
        };
        return bytes.Length == length
            ? bytes
            : throw new InvalidCastException($"Expected {length} bytes but received {bytes.Length}.");
    }

    private static byte[] WriteString(object? value, VariableDefinition definition)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        byte[] encoded = GetEncoding(definition.StringEncoding).GetBytes(text);
        if (encoded.Length > definition.Length)
        {
            throw new InvalidCastException($"Encoded string requires {encoded.Length} bytes; maximum is {definition.Length}.");
        }

        byte[] result = new byte[definition.Length];
        encoded.CopyTo(result, 0);
        return result;
    }

    private static object? ApplyReadScale(object? value, VariableDefinition definition)
    {
        if (definition.Scale == 1 || value is null || definition.DataType is
            PlcDataType.Boolean or PlcDataType.String or PlcDataType.Bytes)
        {
            return value;
        }

        if (value is Array array)
        {
            return array.Cast<object>()
                .Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture) * definition.Scale)
                .ToArray();
        }

        return Convert.ToDouble(value, CultureInfo.InvariantCulture) * definition.Scale;
    }

    private static object? ApplyWriteScale(object? value, VariableDefinition definition)
    {
        if (definition.Scale == 1 || value is null || definition.DataType is
            PlcDataType.Boolean or PlcDataType.String or PlcDataType.Bytes)
        {
            return value;
        }

        if (definition.Length == 1)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture) / definition.Scale;
        }

        if (value is not System.Collections.IEnumerable enumerable)
        {
            return value;
        }

        return enumerable.Cast<object>()
            .Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture) / definition.Scale)
            .ToArray();
    }

    private static void Transform(byte[] bytes, PlcByteOrder order)
    {
        if (order == PlcByteOrder.BigEndian || bytes.Length < 2)
        {
            return;
        }

        if (order == PlcByteOrder.LittleEndian)
        {
            Array.Reverse(bytes);
            return;
        }

        if (order == PlcByteOrder.ByteSwap)
        {
            for (int index = 0; index + 1 < bytes.Length; index += 2)
            {
                (bytes[index], bytes[index + 1]) = (bytes[index + 1], bytes[index]);
            }

            return;
        }

        for (int left = 0, right = bytes.Length - 2; left < right; left += 2, right -= 2)
        {
            (bytes[left], bytes[right]) = (bytes[right], bytes[left]);
            (bytes[left + 1], bytes[right + 1]) = (bytes[right + 1], bytes[left + 1]);
        }
    }

    private static ushort ReadUInt16(byte[] bytes) => (ushort)((bytes[0] << 8) | bytes[1]);

    private static uint ReadUInt32(byte[] bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static ulong ReadUInt64(byte[] bytes)
    {
        ulong value = 0;
        foreach (byte current in bytes)
        {
            value = (value << 8) | current;
        }

        return value;
    }

    private static void WriteUInt16(byte[] bytes, ushort value)
    {
        bytes[0] = (byte)(value >> 8);
        bytes[1] = (byte)value;
    }

    private static void WriteUInt32(byte[] bytes, uint value)
    {
        bytes[0] = (byte)(value >> 24);
        bytes[1] = (byte)(value >> 16);
        bytes[2] = (byte)(value >> 8);
        bytes[3] = (byte)value;
    }

    private static void WriteUInt64(byte[] bytes, ulong value)
    {
        for (int index = bytes.Length - 1; index >= 0; index--)
        {
            bytes[index] = (byte)value;
            value >>= 8;
        }
    }

    private static Encoding GetEncoding(PlcStringEncoding encoding) => encoding switch
    {
        PlcStringEncoding.Ascii => Encoding.ASCII,
        PlcStringEncoding.Utf8 => Encoding.UTF8,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    private static CommunicationError? ValidateDefinition(VariableDefinition definition)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.Name) ||
            string.IsNullOrWhiteSpace(definition.Address) ||
            definition.Length <= 0 ||
            double.IsNaN(definition.Scale) ||
            double.IsInfinity(definition.Scale) ||
            definition.Scale == 0)
        {
            return new CommunicationError(CommunicationErrorCode.InvalidValue, "The variable definition is invalid.");
        }

        return null;
    }
}
