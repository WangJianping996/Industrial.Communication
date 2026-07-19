using System.IO.Compression;
using System.Text;

const int size = 128;
string output = args.Length == 0 ? Path.Combine("assets", "package-icon.png") : args[0];
string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
if (directory is not null)
{
    Directory.CreateDirectory(directory);
}

byte[] pixels = new byte[(size * 4 + 1) * size];
for (int y = 0; y < size; y++)
{
    int row = y * ((size * 4) + 1);
    pixels[row] = 0;
    for (int x = 0; x < size; x++)
    {
        int offset = row + 1 + (x * 4);
        double distance = Math.Sqrt(Math.Pow(x - 64, 2) + Math.Pow(y - 64, 2));
        bool ring = distance is >= 40 and <= 52 && x < 91;
        bool vertical = x is >= 58 and <= 69 && y is >= 32 and <= 96;
        bool cap = x is >= 45 and <= 82 && (y is >= 31 and <= 42 || y is >= 86 and <= 97);
        bool node = Math.Pow(x - 96, 2) + Math.Pow(y - 64, 2) <= 11 * 11;
        (byte red, byte green, byte blue) = ring || node
            ? ((byte)34, (byte)211, (byte)238)
            : vertical || cap
                ? ((byte)248, (byte)250, (byte)252)
                : ((byte)15, (byte)23, (byte)42);
        pixels[offset] = red;
        pixels[offset + 1] = green;
        pixels[offset + 2] = blue;
        pixels[offset + 3] = 255;
    }
}

using var compressed = new MemoryStream();
using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
{
    zlib.Write(pixels);
}

using FileStream stream = File.Create(output);
stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
byte[] header = new byte[13];
WriteBigEndian(header, 0, size);
WriteBigEndian(header, 4, size);
header[8] = 8;
header[9] = 6;
WriteChunk(stream, "IHDR", header);
WriteChunk(stream, "IDAT", compressed.ToArray());
WriteChunk(stream, "IEND", Array.Empty<byte>());
Console.WriteLine(Path.GetFullPath(output));

static void WriteChunk(Stream stream, string type, byte[] data)
{
    byte[] typeBytes = Encoding.ASCII.GetBytes(type);
    Span<byte> length = stackalloc byte[4];
    WriteBigEndian(length, 0, data.Length);
    stream.Write(length);
    stream.Write(typeBytes);
    stream.Write(data);
    byte[] crcInput = typeBytes.Concat(data).ToArray();
    Span<byte> crc = stackalloc byte[4];
    WriteBigEndian(crc, 0, unchecked((int)CalculateCrc32(crcInput)));
    stream.Write(crc);
}

static uint CalculateCrc32(byte[] data)
{
    uint crc = uint.MaxValue;
    foreach (byte value in data)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
    }

    return ~crc;
}

static void WriteBigEndian(Span<byte> target, int offset, int value)
{
    target[offset] = (byte)(value >> 24);
    target[offset + 1] = (byte)(value >> 16);
    target[offset + 2] = (byte)(value >> 8);
    target[offset + 3] = (byte)value;
}
