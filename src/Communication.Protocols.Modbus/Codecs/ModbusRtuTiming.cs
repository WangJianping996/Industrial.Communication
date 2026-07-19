namespace Communication.Protocols.Modbus.Codecs;

/// <summary>Calculates Modbus RTU character and frame timing.</summary>
public static class ModbusRtuTiming
{
    /// <summary>Calculates the minimum 3.5-character inter-frame silence.</summary>
    public static TimeSpan GetInterFrameDelay(
        int baudRate,
        int dataBits = 8,
        bool hasParity = false,
        double stopBits = 1)
    {
        Validate(baudRate, dataBits, stopBits);
        if (baudRate > 19_200)
        {
            return TimeSpan.FromMilliseconds(1.75);
        }

        double bitsPerCharacter = 1 + dataBits + (hasParity ? 1 : 0) + stopBits;
        return TimeSpan.FromSeconds((bitsPerCharacter * 3.5) / baudRate);
    }

    /// <summary>Calculates the maximum 1.5-character inter-character interval.</summary>
    public static TimeSpan GetInterCharacterDelay(
        int baudRate,
        int dataBits = 8,
        bool hasParity = false,
        double stopBits = 1)
    {
        Validate(baudRate, dataBits, stopBits);
        if (baudRate > 19_200)
        {
            return TimeSpan.FromMilliseconds(0.75);
        }

        double bitsPerCharacter = 1 + dataBits + (hasParity ? 1 : 0) + stopBits;
        return TimeSpan.FromSeconds((bitsPerCharacter * 1.5) / baudRate);
    }

    private static void Validate(int baudRate, int dataBits, double stopBits)
    {
        if (baudRate <= 0 || dataBits is < 5 or > 9 ||
            double.IsNaN(stopBits) || double.IsInfinity(stopBits) || stopBits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        }
    }
}
