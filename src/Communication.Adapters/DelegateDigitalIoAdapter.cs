using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.Adapters;

/// <summary>Adapts private digital I/O delegates and emits input edge changes.</summary>
public sealed class DelegateDigitalIoAdapter : DeviceAdapterBase, IDigitalIoDevice
{
    private readonly Func<CancellationToken, ValueTask<CommunicationResult<DigitalIoSnapshot>>> _read;
    private readonly Func<int, bool, CancellationToken, ValueTask<CommunicationResult>> _write;
    private bool[]? _previousInputs;

    /// <summary>Initializes delegate-backed digital I/O.</summary>
    public DelegateDigitalIoAdapter(
        string deviceId,
        Func<CancellationToken, ValueTask<CommunicationResult<DigitalIoSnapshot>>> read,
        Func<int, bool, CancellationToken, ValueTask<CommunicationResult>> write)
        : base(deviceId)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <inheritdoc />
    public event EventHandler<DigitalEdgeChangedEventArgs>? InputChanged;

    /// <inheritdoc />
    public async ValueTask<CommunicationResult<DigitalIoSnapshot>> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        CommunicationResult state = EnsureConnected();
        if (!state.IsSuccess)
        {
            return CommunicationResult<DigitalIoSnapshot>.Failure(state.Error!);
        }

        CommunicationResult<DigitalIoSnapshot> result = await _read(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            PublishEdges(result.Value!);
        }

        return result;
    }

    /// <inheritdoc />
    public ValueTask<CommunicationResult> SetOutputAsync(
        int index,
        bool value,
        CancellationToken cancellationToken = default)
    {
        CommunicationResult state = EnsureConnected();
        return state.IsSuccess
            ? _write(index, value, cancellationToken)
            : new ValueTask<CommunicationResult>(state);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CommunicationResult>> SetOutputsAsync(
        IReadOnlyDictionary<int, bool> values,
        CancellationToken cancellationToken = default)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var results = new List<CommunicationResult>(values.Count);
        foreach (KeyValuePair<int, bool> item in values)
        {
            results.Add(await SetOutputAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc />
    protected override ValueTask<CommunicationResult> OnStartAsync(CancellationToken cancellationToken) =>
        new(CommunicationResult.Success());

    /// <inheritdoc />
    protected override ValueTask<CommunicationResult> OnStopAsync(CancellationToken cancellationToken) =>
        new(CommunicationResult.Success());

    private void PublishEdges(DigitalIoSnapshot snapshot)
    {
        bool[] current = snapshot.Inputs.ToArray();
        bool[]? previous = _previousInputs;
        _previousInputs = current;
        if (previous is null)
        {
            return;
        }

        for (int index = 0; index < Math.Min(previous.Length, current.Length); index++)
        {
            if (previous[index] != current[index])
            {
                InputChanged?.Invoke(this, new DigitalEdgeChangedEventArgs(
                    index, previous[index], current[index], snapshot.Timestamp));
            }
        }
    }
}
