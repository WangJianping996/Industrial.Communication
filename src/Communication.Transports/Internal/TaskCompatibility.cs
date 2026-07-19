namespace Communication.Transports.Internal;

internal static class TaskCompatibility
{
    public static async Task<T> WaitAsync<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        if (operation.IsCompleted)
        {
            return await operation.ConfigureAwait(false);
        }

        Task cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
        Task completed = await Task.WhenAny(operation, cancellation).ConfigureAwait(false);
        if (completed == operation)
        {
            return await operation.ConfigureAwait(false);
        }

        ObserveFault(operation);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Cancellation wait completed unexpectedly.");
    }

    public static async Task WaitAsync(
        Task operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(timeout, delayCancellation.Token);
        Task completed = await Task.WhenAny(operation, delay).ConfigureAwait(false);
        if (completed == operation)
        {
            delayCancellation.Cancel();
            await operation.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObserveFault(operation);
        throw new TimeoutException($"The operation exceeded its timeout of {timeout}.");
    }

    public static async Task<T> WaitAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(timeout, delayCancellation.Token);
        Task completed = await Task.WhenAny(operation, delay).ConfigureAwait(false);
        if (completed == operation)
        {
            delayCancellation.Cancel();
            return await operation.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObserveFault(operation);
        throw new TimeoutException($"The operation exceeded its timeout of {timeout}.");
    }

    public static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
