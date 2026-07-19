using Communication.Core.Checksums;

namespace Communication.UnitTests;

public sealed class ChecksumTests
{
    [Fact]
    public void Modbus_crc16_matches_the_standard_check_value()
    {
        Crc16Checksum checksum = new();
        byte[] payload = "123456789"u8.ToArray();

        ReadOnlyMemory<byte> result = checksum.Compute(payload);

        Assert.Equal(new byte[] { 0x37, 0x4B }, result.ToArray());
        Assert.True(checksum.Validate(payload, result.Span));
        Assert.False(checksum.Validate(payload, new byte[] { 0, 0 }));
    }

    [Fact]
    public void Lrc_makes_the_low_byte_sum_equal_zero()
    {
        LrcChecksum checksum = new();
        byte[] payload = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];

        byte value = checksum.Compute(payload).Span[0];
        int total = payload.Aggregate(0, (sum, current) => sum + current) + value;

        Assert.Equal(0, total & 0xFF);
        Assert.True(checksum.Validate(payload, [value]));
    }

    [Fact]
    public void Delegating_checksum_validates_returned_size()
    {
        DelegatingChecksum checksum = new(1, payload => new byte[] { (byte)payload.Length });

        Assert.Equal(new byte[] { 3 }, checksum.Compute([1, 2, 3]).ToArray());
        Assert.True(checksum.Validate([1, 2, 3], [3]));
    }
}
