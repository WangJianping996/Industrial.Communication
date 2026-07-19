using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Export;

/// <summary>Exports message streams as CSV or JSON without taking ownership of the destination.</summary>
public sealed class MessageExporter : IMessageExporter
{
    /// <inheritdoc />
    public async ValueTask<CommunicationResult<long>> ExportAsync(
        IAsyncEnumerable<MessageEnvelope> messages,
        Stream destination,
        MessageExportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!destination.CanWrite)
        {
            return CommunicationResult<long>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                "The export destination is not writable."));
        }

        try
        {
            if (string.Equals(options.Format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                return CommunicationResult<long>.Success(await ExportCsvAsync(
                    messages,
                    destination,
                    options.IncludePayload,
                    cancellationToken).ConfigureAwait(false));
            }

            if (string.Equals(options.Format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return CommunicationResult<long>.Success(await ExportJsonAsync(
                    messages,
                    destination,
                    options.IncludePayload,
                    cancellationToken).ConfigureAwait(false));
            }

            return CommunicationResult<long>.Failure(new CommunicationError(
                CommunicationErrorCode.InvalidValue,
                $"Unsupported export format '{options.Format}'. Use 'csv' or 'json'."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CommunicationResult<long>.Failure(new CommunicationError(
                CommunicationErrorCode.StorageFailure,
                "Unable to export messages.",
                exception.Message,
                exception));
        }
    }

    private static async Task<long> ExportCsvAsync(
        IAsyncEnumerable<MessageEnvelope> messages,
        Stream destination,
        bool includePayload,
        CancellationToken cancellationToken)
    {
        using StreamWriter writer = new(destination, new UTF8Encoding(false), 4096, leaveOpen: true);
        await writer.WriteLineAsync(
            "id,timestamp,direction,channelId,sessionId,protocol,summary,durationMs,isRedacted,metadata,payloadBase64")
            .ConfigureAwait(false);

        long count = 0;
        await foreach (MessageEnvelope message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string metadata = message.Metadata is null
                ? string.Empty
                : JsonSerializer.Serialize(message.Metadata);
            string payload = includePayload
                ? Convert.ToBase64String(message.Payload.ToArray())
                : string.Empty;
            string[] values =
            [
                message.Id.ToString("D", CultureInfo.InvariantCulture),
                message.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                message.Direction.ToString(),
                message.ChannelId,
                message.SessionId ?? string.Empty,
                message.Protocol ?? string.Empty,
                message.Summary ?? string.Empty,
                message.Duration?.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                message.IsRedacted ? "true" : "false",
                metadata,
                payload,
            ];
            await writer.WriteLineAsync(string.Join(",", values.Select(EscapeCsv))).ConfigureAwait(false);
            count++;
        }

        await writer.FlushAsync().ConfigureAwait(false);
        return count;
    }

    private static async Task<long> ExportJsonAsync(
        IAsyncEnumerable<MessageEnvelope> messages,
        Stream destination,
        bool includePayload,
        CancellationToken cancellationToken)
    {
        using Utf8JsonWriter writer = new(destination, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        long count = 0;
        await foreach (MessageEnvelope message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("id", message.Id);
            writer.WriteString("timestamp", message.Timestamp);
            writer.WriteString("direction", message.Direction.ToString());
            writer.WriteString("channelId", message.ChannelId);
            WriteNullableString(writer, "sessionId", message.SessionId);
            WriteNullableString(writer, "protocol", message.Protocol);
            WriteNullableString(writer, "summary", message.Summary);
            if (message.Duration.HasValue)
            {
                writer.WriteNumber("durationMs", message.Duration.Value.TotalMilliseconds);
            }
            else
            {
                writer.WriteNull("durationMs");
            }

            writer.WriteBoolean("isRedacted", message.IsRedacted);
            writer.WritePropertyName("metadata");
            JsonSerializer.Serialize(writer, message.Metadata);
            if (includePayload)
            {
                writer.WriteBase64String("payload", message.Payload.Span);
            }

            writer.WriteEndObject();
            count++;
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
