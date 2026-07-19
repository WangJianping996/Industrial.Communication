using System.Text;
using Communication.Abstractions.Models;
using Communication.Adapters;
using Communication.Core.Framing;

namespace Communication.ProtocolTests;

public sealed class DeviceAdapterTests
{
    [Fact]
    public async Task Digital_io_supports_batch_output_and_input_edges()
    {
        bool input = false;
        var written = new Dictionary<int, bool>();
        await using var adapter = new DelegateDigitalIoAdapter(
            "io-1",
            _ => new ValueTask<CommunicationResult<DigitalIoSnapshot>>(
                CommunicationResult<DigitalIoSnapshot>.Success(new DigitalIoSnapshot(
                    new[] { input }, written.OrderBy(item => item.Key).Select(item => item.Value).ToArray(),
                    DateTimeOffset.UtcNow))),
            (index, value, _) =>
            {
                written[index] = value;
                return new ValueTask<CommunicationResult>(CommunicationResult.Success());
            });
        DigitalEdgeChangedEventArgs? edge = null;
        adapter.InputChanged += (_, args) => edge = args;

        Assert.True((await adapter.StartAsync()).IsSuccess);
        await adapter.ReadStatusAsync();
        input = true;
        await adapter.ReadStatusAsync();
        IReadOnlyList<CommunicationResult> results = await adapter.SetOutputsAsync(
            new Dictionary<int, bool> { [0] = true, [2] = false });

        Assert.NotNull(edge);
        Assert.Equal(0, edge.Index);
        Assert.False(edge.PreviousValue);
        Assert.True(edge.CurrentValue);
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.True(written[0]);
        Assert.False(written[2]);
    }

    [Fact]
    public async Task Motion_commands_are_explicit_and_never_replayed_on_restart()
    {
        int moves = 0;
        var profile = new MotionProfile(100, 200, 200);
        await using var adapter = new DelegateMotionControllerAdapter(
            "motion-1",
            (axis, _) => new ValueTask<CommunicationResult<AxisState>>(
                CommunicationResult<AxisState>.Success(new AxisState(
                    axis, true, true, false, false, 0, DateTimeOffset.UtcNow))),
            (_, _, _) => Success(),
            (_, _, _) => Success(),
            (_, _, _, _, _) =>
            {
                moves++;
                return Success();
            },
            (_, _, _) => Success());

        await adapter.StartAsync();
        Assert.Equal(0, moves);
        Assert.True((await adapter.MoveAbsoluteAsync(0, 42, profile)).IsSuccess);
        Assert.Equal(1, moves);
        await adapter.StopAsync();
        await adapter.StartAsync();
        Assert.Equal(1, moves);

        Assert.False((await adapter.MoveRelativeAsync(-1, 2, profile)).IsSuccess);
        Assert.Equal(1, moves);
    }

    [Fact]
    public async Task Scanner_handles_sticky_frames_filters_duplicates_and_sends_trigger()
    {
        byte[] trigger = Encoding.ASCII.GetBytes("TRIGGER\r");
        var channel = new ScriptedTransportChannel(command =>
        {
            Assert.Equal(trigger, command.ToArray());
            return new ReadOnlyMemory<byte>[] { Encoding.ASCII.GetBytes("ABC\rABC\rXYZ\r") };
        });
        await using var scanner = new BarcodeScannerAdapter(
            "scanner-1", channel, new DelimiterFrameDecoder(new byte[] { 13 }), trigger,
            TimeSpan.FromSeconds(1));
        var received = new List<string>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.BarcodeScanned += (_, args) =>
        {
            received.Add(args.Value);
            if (received.Count == 2)
            {
                complete.TrySetResult();
            }
        };

        await scanner.StartAsync();
        Assert.True((await scanner.TriggerAsync()).IsSuccess);
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(new[] { "ABC", "XYZ" }, received);
    }

    [Fact]
    public async Task Weighing_adapter_parses_continuous_gross_net_unit_and_stability()
    {
        byte[] tare = Encoding.ASCII.GetBytes("T\r");
        byte[] zero = Encoding.ASCII.GetBytes("Z\r");
        var channel = new ScriptedTransportChannel(command =>
        {
            if (command.Span.SequenceEqual(tare) || command.Span.SequenceEqual(zero))
            {
                return Array.Empty<ReadOnlyMemory<byte>>();
            }

            return new ReadOnlyMemory<byte>[] { Encoding.ASCII.GetBytes("ST,12.5,10.0,kg\r") };
        });
        await using var scale = new WeighingDeviceAdapter(
            "scale-1",
            channel,
            new DelimiterFrameDecoder(new byte[] { 13 }),
            ParseWeight,
            tare,
            zero);
        var received = new TaskCompletionSource<WeightReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        scale.WeightReported += (_, args) => received.TrySetResult(args.Reading);

        await scale.StartAsync();
        await channel.SendAsync(Encoding.ASCII.GetBytes("READ\r"));
        WeightReading reading = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(reading.IsStable);
        Assert.Equal(12.5, reading.Gross);
        Assert.Equal(10, reading.Net);
        Assert.Equal(WeightUnit.Kilogram, reading.Unit);
        Assert.True((await scale.TareAsync()).IsSuccess);
        Assert.True((await scale.ZeroAsync()).IsSuccess);
        Assert.True((await scale.ReadWeightAsync()).IsSuccess);
    }

    [Fact]
    public async Task Delegate_framed_adapter_bridges_a_private_binary_protocol()
    {
        var channel = new ScriptedTransportChannel(_ => new ReadOnlyMemory<byte>[]
        {
            new byte[] { 0x12 },
            new byte[] { 0x34 },
        });
        var received = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var adapter = new DelegateFramedDeviceAdapter<ushort>(
            "private-binary",
            channel,
            new FixedLengthFrameDecoder(2),
            frame => CommunicationResult<ushort>.Success(
                checked((ushort)((frame.Span[0] << 8) | frame.Span[1]))),
            (reading, _) => received.TrySetResult(reading));

        await adapter.StartAsync();
        await channel.SendAsync(new byte[] { 0x01 });

        Assert.Equal(0x1234, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static ValueTask<CommunicationResult> Success() =>
        new(CommunicationResult.Success());

    private static CommunicationResult<WeightReading> ParseWeight(ReadOnlyMemory<byte> frame)
    {
        string[] parts = Encoding.ASCII.GetString(frame.Span).Split(',');
        if (parts.Length != 4 ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double gross) ||
            !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double net))
        {
            return CommunicationResult<WeightReading>.Failure(new CommunicationError(
                CommunicationErrorCode.ProtocolError, "Invalid weighing frame."));
        }

        return CommunicationResult<WeightReading>.Success(new WeightReading(
            gross,
            net,
            string.Equals(parts[3], "kg", StringComparison.OrdinalIgnoreCase)
                ? WeightUnit.Kilogram
                : WeightUnit.Unknown,
            string.Equals(parts[0], "ST", StringComparison.Ordinal),
            DateTimeOffset.UtcNow));
    }
}
