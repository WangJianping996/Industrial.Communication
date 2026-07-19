using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Monitoring;

/// <summary>Removes all raw payload bytes. This is the default monitoring policy.</summary>
public sealed class SuppressPayloadMessageRedactor : IMessageRedactor
{
    /// <inheritdoc />
    public ValueTask<CommunicationResult<MessageEnvelope>> RedactAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<CommunicationResult<MessageEnvelope>>(
            CommunicationResult<MessageEnvelope>.Success(message with
            {
                Payload = ReadOnlyMemory<byte>.Empty,
                IsRedacted = true,
            }));
    }
}

/// <summary>Explicitly permits recording the complete payload.</summary>
public sealed class PassThroughMessageRedactor : IMessageRedactor
{
    /// <inheritdoc />
    public ValueTask<CommunicationResult<MessageEnvelope>> RedactAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<CommunicationResult<MessageEnvelope>>(
            CommunicationResult<MessageEnvelope>.Success(message));
    }
}

/// <summary>Suppresses the entire message, disabling monitoring and persistence.</summary>
public sealed class DenyMessageRecordingRedactor : IMessageRedactor
{
    /// <inheritdoc />
    public ValueTask<CommunicationResult<MessageEnvelope>> RedactAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<CommunicationResult<MessageEnvelope>>(
            CommunicationResult<MessageEnvelope>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidState,
                "Message recording is disabled by policy.")));
    }
}

/// <summary>Identifies a zero-based byte range to mask.</summary>
/// <param name="Offset">The first byte to mask.</param>
/// <param name="Length">The maximum number of bytes to mask.</param>
public readonly record struct ByteRedactionRange(int Offset, int Length);

/// <summary>Masks configured byte ranges while retaining the rest of the payload.</summary>
public sealed class ByteRangeMessageRedactor : IMessageRedactor
{
    private readonly ByteRedactionRange[] _ranges;
    private readonly byte _mask;

    /// <summary>Initializes a byte-range redactor.</summary>
    public ByteRangeMessageRedactor(IEnumerable<ByteRedactionRange> ranges, byte mask = 0x2A)
    {
        _ranges = ranges?.ToArray() ?? throw new ArgumentNullException(nameof(ranges));
        if (_ranges.Any(range => range.Offset < 0 || range.Length < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(ranges));
        }

        _mask = mask;
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult<MessageEnvelope>> RedactAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload = message.Payload.ToArray();
        bool changed = false;
        foreach (ByteRedactionRange range in _ranges)
        {
            int start = Math.Min(range.Offset, payload.Length);
            int end = (int)Math.Min(payload.Length, (long)range.Offset + range.Length);
            for (int index = start; index < end; index++)
            {
                payload[index] = _mask;
                changed = true;
            }
        }

        return new ValueTask<CommunicationResult<MessageEnvelope>>(
            CommunicationResult<MessageEnvelope>.Success(message with
            {
                Payload = payload,
                IsRedacted = message.IsRedacted || changed,
            }));
    }
}

/// <summary>Defines protocol, direction, address, and byte-range matching for redaction.</summary>
public sealed record MessageRedactionRule
{
    /// <summary>Gets an optional protocol name.</summary>
    public string? Protocol { get; init; }

    /// <summary>Gets an optional message direction.</summary>
    public MessageDirection? Direction { get; init; }

    /// <summary>Gets an optional inclusive numeric address start.</summary>
    public long? AddressFrom { get; init; }

    /// <summary>Gets an optional inclusive numeric address end.</summary>
    public long? AddressTo { get; init; }

    /// <summary>Gets byte ranges to mask when the rule matches.</summary>
    public IReadOnlyList<ByteRedactionRange> ByteRanges { get; init; } = [];

    /// <summary>Gets parsed metadata field names whose values must be masked.</summary>
    public IReadOnlyList<string> MetadataFields { get; init; } = [];
}

/// <summary>Applies byte masking by protocol, direction, and optional parsed numeric address.</summary>
public sealed class RuleBasedMessageRedactor : IMessageRedactor
{
    private readonly MessageRedactionRule[] _rules;
    private readonly IMessageRedactor _fallback;
    private readonly byte _mask;

    /// <summary>Initializes a rule-based redactor.</summary>
    public RuleBasedMessageRedactor(
        IEnumerable<MessageRedactionRule> rules,
        IMessageRedactor? fallback = null,
        byte mask = 0x2A)
    {
        _rules = rules?.ToArray() ?? throw new ArgumentNullException(nameof(rules));
        _fallback = fallback ?? new SuppressPayloadMessageRedactor();
        _mask = mask;
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<MessageEnvelope>> RedactAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        MessageRedactionRule? rule = _rules.FirstOrDefault(candidate => Matches(message, candidate));
        if (rule is null)
        {
            return await _fallback.RedactAsync(message, cancellationToken).ConfigureAwait(false);
        }

        CommunicationResult<MessageEnvelope> result = await new ByteRangeMessageRedactor(rule.ByteRanges, _mask)
            .RedactAsync(message, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null || rule.MetadataFields.Count == 0)
        {
            return result;
        }

        Dictionary<string, string>? metadata = result.Value.Metadata is null
            ? null
            : new Dictionary<string, string>(result.Value.Metadata, StringComparer.Ordinal);
        bool changed = false;
        if (metadata is not null)
        {
            foreach (string field in rule.MetadataFields)
            {
                string? key = metadata.Keys.FirstOrDefault(candidate => string.Equals(
                    candidate,
                    field,
                    StringComparison.OrdinalIgnoreCase));
                if (key is not null)
                {
                    metadata[key] = "***";
                    changed = true;
                }
            }
        }

        return CommunicationResult<MessageEnvelope>.Success(result.Value with
        {
            Metadata = metadata,
            IsRedacted = result.Value.IsRedacted || changed,
        });
    }

    private static bool Matches(MessageEnvelope message, MessageRedactionRule rule)
    {
        if (rule.Protocol is not null && !string.Equals(
                message.Protocol,
                rule.Protocol,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.Direction.HasValue && message.Direction != rule.Direction.Value)
        {
            return false;
        }

        if (!rule.AddressFrom.HasValue && !rule.AddressTo.HasValue)
        {
            return true;
        }

        return message.Metadata is not null &&
               message.Metadata.TryGetValue("address", out string? addressText) &&
               long.TryParse(addressText, out long address) &&
               (!rule.AddressFrom.HasValue || address >= rule.AddressFrom.Value) &&
               (!rule.AddressTo.HasValue || address <= rule.AddressTo.Value);
    }
}
