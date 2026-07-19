using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Communication.Abstractions.Interfaces;
using Communication.Abstractions.Models;

namespace Communication.ProtocolTests;

internal sealed class ScriptedTransportChannel : ITransportChannel
{
    private readonly Func<ReadOnlyMemory<byte>, IEnumerable<ReadOnlyMemory<byte>>> _script;
    private readonly Channel<ReadOnlyMemory<byte>> _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public ScriptedTransportChannel(Func<ReadOnlyMemory<byte>, IEnumerable<ReadOnlyMemory<byte>>> script)
    {
        _script = script;
    }

    public string ChannelId => "scripted";

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Connected);
        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    public ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        return new ValueTask<CommunicationResult>(CommunicationResult.Success());
    }

    public ValueTask<CommunicationResult<int>> SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        foreach (ReadOnlyMemory<byte> response in _script(payload))
        {
            _responses.Writer.TryWrite(response);
        }

        return new ValueTask<CommunicationResult<int>>(
            CommunicationResult<int>.Success(payload.Length));
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ReadOnlyMemory<byte> response in _responses.Reader
            .ReadAllAsync(cancellationToken))
        {
            yield return response;
        }
    }

    public ValueTask DisposeAsync()
    {
        _responses.Writer.TryComplete();
        SetState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    private void SetState(ConnectionState state)
    {
        ConnectionState previous = State;
        State = state;
        if (previous != state)
        {
            StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(
                previous,
                state,
                DateTimeOffset.UtcNow,
                null));
        }
    }
}
