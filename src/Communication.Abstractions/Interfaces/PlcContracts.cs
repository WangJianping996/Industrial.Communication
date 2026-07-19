using Communication.Abstractions.Models;

namespace Communication.Abstractions.Interfaces;

/// <summary>Defines protocol-independent PLC reads and writes.</summary>
public interface IPlcClient : IAsyncDisposable
{
    /// <summary>Gets the current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>Connects to the PLC.</summary>
    ValueTask<CommunicationResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects from the PLC.</summary>
    ValueTask<CommunicationResult> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one variable.</summary>
    ValueTask<CommunicationResult<VariableValue>> ReadAsync(
        VariableDefinition variable,
        CancellationToken cancellationToken = default);

    /// <summary>Reads variables using the most efficient supported grouping.</summary>
    ValueTask<IReadOnlyList<CommunicationResult<VariableValue>>> ReadAsync(
        IReadOnlyList<VariableDefinition> variables,
        CancellationToken cancellationToken = default);

    /// <summary>Writes one variable.</summary>
    ValueTask<CommunicationResult> WriteAsync(
        PlcWriteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Writes variables using the most efficient supported grouping.</summary>
    ValueTask<IReadOnlyList<CommunicationResult>> WriteAsync(
        IReadOnlyList<PlcWriteRequest> requests,
        CancellationToken cancellationToken = default);
}

/// <summary>Parses textual PLC addresses without performing I/O.</summary>
public interface IAddressParser
{
    /// <summary>Parses one address.</summary>
    CommunicationResult<PlcAddress> Parse(string address);
}

/// <summary>Converts protocol bytes and CLR values using explicit byte-order rules.</summary>
public interface IValueConverter
{
    /// <summary>Converts bytes into the value described by a variable definition.</summary>
    CommunicationResult<object?> FromBytes(ReadOnlySpan<byte> bytes, VariableDefinition definition);

    /// <summary>Converts a value into protocol bytes described by a variable definition.</summary>
    CommunicationResult<ReadOnlyMemory<byte>> ToBytes(object? value, VariableDefinition definition);
}

/// <summary>Periodically observes PLC variables and isolates per-variable failures.</summary>
public interface IVariableMonitor : IAsyncDisposable
{
    /// <summary>Gets whether polling is currently active.</summary>
    bool IsRunning { get; }

    /// <summary>Starts monitoring the supplied variables.</summary>
    ValueTask<CommunicationResult> StartAsync(
        IReadOnlyList<VariableDefinition> variables,
        VariableMonitorOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops monitoring.</summary>
    ValueTask<CommunicationResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams value changes and per-variable failures.</summary>
    IAsyncEnumerable<VariableValue> WatchAsync(CancellationToken cancellationToken = default);
}
