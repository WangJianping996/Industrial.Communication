namespace Communication.Abstractions.Models;

/// <summary>Describes the lifecycle state of a communication endpoint.</summary>
public enum ConnectionState
{
    /// <summary>The endpoint has no active connection.</summary>
    Disconnected,
    /// <summary>A connection attempt is in progress.</summary>
    Connecting,
    /// <summary>The endpoint is connected and available.</summary>
    Connected,
    /// <summary>The endpoint is recovering an interrupted connection.</summary>
    Reconnecting,
    /// <summary>An orderly disconnection is in progress.</summary>
    Disconnecting,
    /// <summary>The endpoint stopped because of an error.</summary>
    Faulted,
}

/// <summary>Classifies communication failures without relying on exception text.</summary>
public enum CommunicationErrorCode
{
    /// <summary>An unclassified failure.</summary>
    Unknown,
    /// <summary>An operation exceeded its deadline.</summary>
    Timeout,
    /// <summary>An operation was canceled by its caller.</summary>
    Canceled,
    /// <summary>A connection could not be established or was lost.</summary>
    ConnectionFailure,
    /// <summary>A frame or protocol message was invalid.</summary>
    ProtocolError,
    /// <summary>Message integrity validation failed.</summary>
    ChecksumFailure,
    /// <summary>The remote device returned an error response.</summary>
    DeviceError,
    /// <summary>An address was invalid or unsupported.</summary>
    InvalidAddress,
    /// <summary>An input value was invalid or unsupported.</summary>
    InvalidValue,
    /// <summary>A bounded queue rejected an item.</summary>
    QueueFull,
    /// <summary>The operation is invalid in the endpoint's current state.</summary>
    InvalidState,
    /// <summary>A monitoring history or export destination failed.</summary>
    StorageFailure,
}

/// <summary>Indicates the direction in which a message traveled.</summary>
public enum MessageDirection
{
    /// <summary>A message sent to a remote endpoint.</summary>
    Outbound,
    /// <summary>A message received from a remote endpoint.</summary>
    Inbound,
}

/// <summary>Indicates the result of an incremental decoding attempt.</summary>
public enum DecodeStatus
{
    /// <summary>More bytes are required to complete a value.</summary>
    NeedMoreData,
    /// <summary>One complete value was decoded.</summary>
    Done,
    /// <summary>The buffered data is invalid.</summary>
    InvalidData,
}

/// <summary>Represents the confidence in a sampled variable value.</summary>
public enum VariableQuality
{
    /// <summary>The value is valid.</summary>
    Good,
    /// <summary>The value is usable but has a warning condition.</summary>
    Uncertain,
    /// <summary>The value is unavailable or invalid.</summary>
    Bad,
}

/// <summary>Represents the high-level health of a device adapter.</summary>
public enum DeviceHealthStatus
{
    /// <summary>The adapter has not reported health yet.</summary>
    Unknown,
    /// <summary>The adapter is operating normally.</summary>
    Healthy,
    /// <summary>The adapter is operating with reduced capability.</summary>
    Degraded,
    /// <summary>The adapter cannot perform its purpose.</summary>
    Unhealthy,
}

/// <summary>Specifies how a bounded queue reacts when it reaches capacity.</summary>
public enum QueueBackpressureStrategy
{
    /// <summary>Wait until capacity becomes available.</summary>
    Wait,
    /// <summary>Reject the item being written.</summary>
    Reject,
    /// <summary>Discard the oldest queued item.</summary>
    DropOldest,
    /// <summary>Discard the item being written.</summary>
    DropNewest,
}

/// <summary>Specifies how replay delays are calculated.</summary>
public enum ReplayTimingMode
{
    /// <summary>Preserve recorded intervals, optionally scaled by speed.</summary>
    OriginalIntervals,
    /// <summary>Use one configured interval between all messages.</summary>
    FixedInterval,
    /// <summary>Emit messages without intentional delays.</summary>
    AsFastAsPossible,
}

/// <summary>Describes a protocol-independent PLC value type.</summary>
public enum PlcDataType
{
    /// <summary>A Boolean value.</summary>
    Boolean,
    /// <summary>An unsigned 8-bit integer.</summary>
    Byte,
    /// <summary>A signed 16-bit integer.</summary>
    Int16,
    /// <summary>An unsigned 16-bit integer.</summary>
    UInt16,
    /// <summary>A signed 32-bit integer.</summary>
    Int32,
    /// <summary>An unsigned 32-bit integer.</summary>
    UInt32,
    /// <summary>An IEEE 754 single-precision value.</summary>
    Float32,
    /// <summary>An IEEE 754 double-precision value.</summary>
    Float64,
    /// <summary>A text value.</summary>
    String,
    /// <summary>An opaque byte sequence.</summary>
    Bytes,
}

/// <summary>Specifies how protocol bytes are transformed relative to canonical big-endian order.</summary>
public enum PlcByteOrder
{
    /// <summary>Bytes are stored most-significant first (ABCD).</summary>
    BigEndian,
    /// <summary>All bytes are reversed (DCBA).</summary>
    LittleEndian,
    /// <summary>Bytes are reversed inside each 16-bit word (BADC).</summary>
    ByteSwap,
    /// <summary>The order of 16-bit words is reversed (CDAB).</summary>
    WordSwap,
}

/// <summary>Identifies the text encoding used by a mapped PLC string.</summary>
public enum PlcStringEncoding
{
    /// <summary>Seven-bit ASCII-compatible encoding.</summary>
    Ascii,
    /// <summary>UTF-8 encoding.</summary>
    Utf8,
}

/// <summary>Specifies whether a mapped variable may be read or written.</summary>
[Flags]
public enum VariableAccess
{
    /// <summary>No operation is allowed.</summary>
    None = 0,
    /// <summary>Read operations are allowed.</summary>
    Read = 1,
    /// <summary>Write operations are allowed.</summary>
    Write = 2,
    /// <summary>Both read and write operations are allowed.</summary>
    ReadWrite = Read | Write,
}

/// <summary>Describes a fault that a simulator can inject.</summary>
public enum SimulatorFaultKind
{
    /// <summary>Delay the next response.</summary>
    Delay,
    /// <summary>Discard the next response.</summary>
    Drop,
    /// <summary>Close the simulated connection.</summary>
    Disconnect,
    /// <summary>Return a malformed frame.</summary>
    InvalidFrame,
    /// <summary>Return an explicit device error.</summary>
    DeviceError,
}
