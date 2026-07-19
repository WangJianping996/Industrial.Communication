using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;
using Communication.Core.Monitoring;

namespace Communication.Core.Storage;

/// <summary>Configures rolling JSON Lines message history.</summary>
public sealed record FileMessageStoreOptions
{
    /// <summary>Gets the dedicated history directory.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>Gets the maximum size of one history file.</summary>
    public long RollFileSizeBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the maximum total history size.</summary>
    public long MaxTotalSizeBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>Gets the maximum file retention period.</summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>Stores redacted message history in bounded, rolling JSON Lines files.</summary>
public sealed class FileMessageStore : IMessageStore, IAsyncDisposable
{
    private const string FilePrefix = "messages-";
    private readonly FileMessageStoreOptions _options;
    private readonly string _directoryPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentPath;
    private DateTime _currentDate;
    private int _disposed;

    /// <summary>Initializes a rolling file message store.</summary>
    public FileMessageStore(FileMessageStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        _directoryPath = Path.GetFullPath(options.DirectoryPath);
        string? root = Path.GetPathRoot(_directoryPath);
        if (string.Equals(
            _directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The history directory cannot be a filesystem root.", nameof(options));
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommunicationResult> AppendAsync(
        MessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directoryPath);
            string path = SelectCurrentFile();
            string json = MessageRecordSerializer.Serialize(message);
            using FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true);
            using StreamWriter writer = new(stream, new UTF8Encoding(false), 4096, leaveOpen: false);
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            CleanupHistory();
            return CommunicationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CommunicationResult.Failure(new CommunicationError(
                CommunicationErrorCode.StorageFailure,
                "Unable to append message history.",
                exception.Message,
                exception));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MessageEnvelope> QueryAsync(
        MessageFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (filter is null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        if (!Directory.Exists(_directoryPath))
        {
            yield break;
        }

        string[] files = Directory.GetFiles(_directoryPath, $"{FilePrefix}*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (string file in files)
        {
            using FileStream stream = new(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                useAsync: true);
            using StreamReader reader = new(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                MessageEnvelope? message = MessageRecordSerializer.TryDeserialize(line);
                if (message is not null && MessageMonitor.Matches(message, filter))
                {
                    yield return message;
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }

        return default;
    }

    private string SelectCurrentFile()
    {
        DateTime today = DateTime.UtcNow.Date;
        if (_currentPath is not null &&
            _currentDate == today &&
            (!File.Exists(_currentPath) || new FileInfo(_currentPath).Length < _options.RollFileSizeBytes))
        {
            return _currentPath;
        }

        string datePrefix = $"{FilePrefix}{today:yyyyMMdd}-";
        string[] candidates = Directory.GetFiles(_directoryPath, $"{datePrefix}*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        int index = 0;
        if (candidates.Length > 0)
        {
            string last = candidates[candidates.Length - 1];
            if (new FileInfo(last).Length < _options.RollFileSizeBytes)
            {
                _currentPath = last;
                _currentDate = today;
                return last;
            }

            string name = Path.GetFileNameWithoutExtension(last);
            string indexText = name.Substring(datePrefix.Length);
            _ = int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out index);
            index++;
        }

        _currentPath = Path.Combine(_directoryPath, $"{datePrefix}{index:D4}.jsonl");
        _currentDate = today;
        return _currentPath;
    }

    private void CleanupHistory()
    {
        DateTime cutoff = DateTime.UtcNow.Subtract(_options.RetentionPeriod);
        FileInfo[] files = Directory.GetFiles(_directoryPath, $"{FilePrefix}*.jsonl")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        foreach (FileInfo file in files.Where(file => file.LastWriteTimeUtc < cutoff))
        {
            DeleteOwnedFile(file);
        }

        files = Directory.GetFiles(_directoryPath, $"{FilePrefix}*.jsonl")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files)
        {
            if (total <= _options.MaxTotalSizeBytes)
            {
                break;
            }

            if (string.Equals(file.FullName, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length = file.Length;
            DeleteOwnedFile(file);
            total -= length;
        }
    }

    private void DeleteOwnedFile(FileInfo file)
    {
        string fullPath = Path.GetFullPath(file.FullName);
        string directoryPrefix = _directoryPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase) ||
            !file.Name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !file.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        file.Delete();
    }

    private static void ValidateOptions(FileMessageStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DirectoryPath) ||
            options.RollFileSizeBytes <= 0 ||
            options.MaxTotalSizeBytes <= 0 ||
            options.RetentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(FileMessageStore));
        }
    }
}

internal static class MessageRecordSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(MessageEnvelope message) =>
        JsonSerializer.Serialize(MessageRecord.FromEnvelope(message), Options);

    public static MessageEnvelope? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MessageRecord>(json, Options)?.ToEnvelope();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal sealed class MessageRecord
    {
        public Guid Id { get; set; }

        public DateTimeOffset Timestamp { get; set; }

        public MessageDirection Direction { get; set; }

        public string ChannelId { get; set; } = string.Empty;

        public string PayloadBase64 { get; set; } = string.Empty;

        public string? SessionId { get; set; }

        public string? Protocol { get; set; }

        public string? Summary { get; set; }

        public long? DurationTicks { get; set; }

        public bool IsRedacted { get; set; }

        public Dictionary<string, string>? Metadata { get; set; }

        public static MessageRecord FromEnvelope(MessageEnvelope message) => new()
        {
            Id = message.Id,
            Timestamp = message.Timestamp,
            Direction = message.Direction,
            ChannelId = message.ChannelId,
            PayloadBase64 = Convert.ToBase64String(message.Payload.ToArray()),
            SessionId = message.SessionId,
            Protocol = message.Protocol,
            Summary = message.Summary,
            DurationTicks = message.Duration?.Ticks,
            IsRedacted = message.IsRedacted,
            Metadata = message.Metadata is null
                ? null
                : new Dictionary<string, string>(message.Metadata, StringComparer.Ordinal),
        };

        public MessageEnvelope ToEnvelope() => new(
            Id,
            Timestamp,
            Direction,
            ChannelId,
            Convert.FromBase64String(PayloadBase64),
            SessionId,
            Protocol,
            Summary,
            DurationTicks.HasValue ? TimeSpan.FromTicks(DurationTicks.Value) : null,
            IsRedacted,
            Metadata);
    }
}
