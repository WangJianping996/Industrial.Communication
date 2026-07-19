using System.Runtime.CompilerServices;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Replay;

/// <summary>Replays recorded messages using original, fixed, or unrestricted timing.</summary>
public sealed class MessageReplayService : IMessageReplayService
{
    /// <inheritdoc />
    public async IAsyncEnumerable<MessageEnvelope> ReplayAsync(
        IAsyncEnumerable<MessageEnvelope> messages,
        MessageReplayOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        ValidateOptions(options);
        DateTimeOffset? previousTimestamp = null;
        await foreach (MessageEnvelope message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (options.Direction.HasValue && message.Direction != options.Direction.Value)
            {
                continue;
            }

            TimeSpan delay = GetDelay(previousTimestamp, message.Timestamp, options);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            previousTimestamp = message.Timestamp;
        }
    }

    private static TimeSpan GetDelay(
        DateTimeOffset? previousTimestamp,
        DateTimeOffset currentTimestamp,
        MessageReplayOptions options)
    {
        if (!previousTimestamp.HasValue || options.TimingMode == ReplayTimingMode.AsFastAsPossible)
        {
            return TimeSpan.Zero;
        }

        if (options.TimingMode == ReplayTimingMode.FixedInterval)
        {
            return options.FixedInterval;
        }

        TimeSpan recorded = currentTimestamp - previousTimestamp.Value;
        return recorded <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(recorded.Ticks / options.Speed));
    }

    private static void ValidateOptions(MessageReplayOptions options)
    {
        if (double.IsNaN(options.Speed) || double.IsInfinity(options.Speed) || options.Speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Replay speed must be finite and greater than zero.");
        }

        if (options.FixedInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The fixed replay interval cannot be negative.");
        }

        if (!Enum.IsDefined(typeof(ReplayTimingMode), options.TimingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The replay timing mode is invalid.");
        }
    }
}
