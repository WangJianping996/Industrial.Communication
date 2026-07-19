using Communication.Abstractions.Models;
using Communication.Core.Plc;

namespace Communication.UnitTests;

public sealed class PlcValueConverterTests
{
    private readonly PlcValueConverter _converter = new();

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "12345678")]
    [InlineData(PlcByteOrder.LittleEndian, "78563412")]
    [InlineData(PlcByteOrder.ByteSwap, "34127856")]
    [InlineData(PlcByteOrder.WordSwap, "56781234")]
    public void Int32_round_trips_with_explicit_order(PlcByteOrder order, string expectedHex)
    {
        var definition = new VariableDefinition("Counter", "DB1.DBD0", PlcDataType.Int32, ByteOrder: order);

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes(0x12345678, definition);
        CommunicationResult<object?> decoded = _converter.FromBytes(encoded.Value!.Span, definition);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(expectedHex, Convert.ToHexString(encoded.Value.Span));
        Assert.True(decoded.IsSuccess);
        Assert.Equal(0x12345678, decoded.Value);
    }

    [Fact]
    public void Numeric_scale_is_applied_in_both_directions()
    {
        var definition = new VariableDefinition("Temperature", "D100", PlcDataType.Int16, Scale: 0.1);

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes(25.5, definition);
        CommunicationResult<object?> decoded = _converter.FromBytes(encoded.Value!.Span, definition);

        Assert.Equal("00FF", Convert.ToHexString(encoded.Value.Span));
        Assert.Equal(25.5, Assert.IsType<double>(decoded.Value), 5);
    }

    [Fact]
    public void Numeric_array_scale_is_applied_to_each_element()
    {
        var definition = new VariableDefinition(
            "Temperatures", "D100", PlcDataType.Int16, Length: 2, Scale: 0.1);

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes(
            new[] { 12.3, 45.6 }, definition);
        CommunicationResult<object?> decoded = _converter.FromBytes(encoded.Value!.Span, definition);

        Assert.Equal("007B01C8", Convert.ToHexString(encoded.Value.Span));
        Assert.Equal(new[] { 12.3, 45.6 }, Assert.IsType<double[]>(decoded.Value));
    }

    [Fact]
    public void Utf8_string_is_zero_padded_and_trimmed()
    {
        var definition = new VariableDefinition(
            "Label",
            "DB1.DBB0",
            PlcDataType.String,
            Length: 8,
            StringEncoding: PlcStringEncoding.Utf8);

        CommunicationResult<ReadOnlyMemory<byte>> encoded = _converter.ToBytes("温度", definition);
        CommunicationResult<object?> decoded = _converter.FromBytes(encoded.Value!.Span, definition);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(8, encoded.Value.Length);
        Assert.Equal("温度", decoded.Value);
    }

    [Fact]
    public void Too_long_string_returns_structured_failure()
    {
        var definition = new VariableDefinition("Label", "D100", PlcDataType.String, Length: 2);

        CommunicationResult<ReadOnlyMemory<byte>> result = _converter.ToBytes("long", definition);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommunicationErrorCode.InvalidValue, result.Error!.Code);
    }
}
