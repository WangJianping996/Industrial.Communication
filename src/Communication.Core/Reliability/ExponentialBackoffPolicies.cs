using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Core.Reliability;

/// <summary>Retries transient operations with capped exponential backoff and jitter.</summary>
public sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
{
    private readonly Random _random;
    private readonly object _randomLock = new();
    private readonly Func<CommunicationError, bool> _shouldRetry;

    /// <summary>Initializes an exponential retry policy.</summary>
    public ExponentialBackoffRetryPolicy(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double multiplier = 2.0,
        double jitterRatio = 0.2,
        Func<CommunicationError, bool>? shouldRetry = null,
        int? randomSeed = null)
    {
        Validate(maxAttempts, initialDelay, maxDelay, multiplier, jitterRatio);
        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
        Multiplier = multiplier;
        JitterRatio = jitterRatio;
        _shouldRetry = shouldRetry ?? DefaultShouldRetry;
        _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
    }

    /// <summary>Gets the total number of attempts, including the first.</summary>
    public int MaxAttempts { get; }

    /// <summary>Gets the delay before the first retry.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>Gets the maximum delay.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>Gets the exponential multiplier.</summary>
    public double Multiplier { get; }

    /// <summary>Gets the random jitter ratio.</summary>
    public double JitterRatio { get; }

    /// <inheritdoc />
    public ValueTask<RetryDecision> GetDecisionAsync(
        RetryContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Attempt >= MaxAttempts || !_shouldRetry(context.Error))
        {
            return new ValueTask<RetryDecision>(RetryDecision.Stop);
        }

        return new ValueTask<RetryDecision>(new RetryDecision(true, GetDelay(context.Attempt)));
    }

    private TimeSpan GetDelay(int failedAttempt)
    {
        double baseMilliseconds = Math.Min(
            MaxDelay.TotalMilliseconds,
            InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, Math.Max(0, failedAttempt - 1)));
        double sample;
        lock (_randomLock)
        {
            sample = _random.NextDouble();
        }

        double jitter = 1.0 + ((sample * 2.0 - 1.0) * JitterRatio);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseMilliseconds * jitter));
    }

    private static bool DefaultShouldRetry(CommunicationError error) => error.Code is
        CommunicationErrorCode.Timeout or
        CommunicationErrorCode.ConnectionFailure or
        CommunicationErrorCode.QueueFull;

    private static void Validate(
        int maxAttempts,
        TimeSpan? initialDelay,
        TimeSpan? maxDelay,
        double multiplier,
        double jitterRatio)
    {
        if (maxAttempts <= 0 ||
            initialDelay is { } initial && initial < TimeSpan.Zero ||
            maxDelay is { } maximum && maximum < TimeSpan.Zero ||
            multiplier < 1.0 ||
            jitterRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }
    }
}

/// <summary>Reconnects interrupted channels with capped exponential backoff and jitter.</summary>
public sealed class ExponentialBackoffReconnectPolicy : IReconnectPolicy
{
    private readonly ExponentialBackoffRetryPolicy _inner;

    /// <summary>Initializes an exponential reconnection policy.</summary>
    public ExponentialBackoffReconnectPolicy(
        int maxAttempts = 10,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double multiplier = 2.0,
        double jitterRatio = 0.2,
        int? randomSeed = null)
    {
        _inner = new ExponentialBackoffRetryPolicy(
            maxAttempts,
            initialDelay ?? TimeSpan.FromMilliseconds(250),
            maxDelay ?? TimeSpan.FromSeconds(30),
            multiplier,
            jitterRatio,
            _ => true,
            randomSeed);
    }

    /// <inheritdoc />
    public ValueTask<RetryDecision> GetDecisionAsync(
        ReconnectContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.WasUserInitiated)
        {
            return new ValueTask<RetryDecision>(RetryDecision.Stop);
        }

        return _inner.GetDecisionAsync(
            new RetryContext("Reconnect", Math.Max(0, context.Attempt - 1), context.Elapsed, context.Error),
            cancellationToken);
    }
}
