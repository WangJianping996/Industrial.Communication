using Communication.Abstractions.Models;

namespace Communication.Abstractions.Exceptions;

/// <summary>Base exception for non-cancellation communication failures.</summary>
public class CommunicationException : Exception
{
    /// <summary>Initializes a communication exception.</summary>
    public CommunicationException(
        CommunicationErrorCode errorCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the machine-readable error category.</summary>
    public CommunicationErrorCode ErrorCode { get; }

    /// <summary>Creates the most specific exception available for a structured error.</summary>
    public static Exception FromError(CommunicationError error) => error.Code switch
    {
        CommunicationErrorCode.Timeout => new CommunicationTimeoutException(error.Message, error.Exception),
        CommunicationErrorCode.Canceled => new OperationCanceledException(error.Message, error.Exception),
        CommunicationErrorCode.ConnectionFailure => new ConnectionException(error.Message, error.Exception),
        CommunicationErrorCode.ProtocolError => new ProtocolException(error.Message, error.Exception),
        CommunicationErrorCode.ChecksumFailure => new ChecksumException(error.Message, error.Exception),
        CommunicationErrorCode.DeviceError => new DeviceException(error.Message, error.Detail, error.Exception),
        _ => new CommunicationException(error.Code, error.Message, error.Exception),
    };
}

/// <summary>Represents a connection establishment or connection-loss failure.</summary>
public sealed class ConnectionException : CommunicationException
{
    /// <summary>Initializes a connection exception.</summary>
    public ConnectionException(string message, Exception? innerException = null)
        : base(CommunicationErrorCode.ConnectionFailure, message, innerException)
    {
    }
}

/// <summary>Represents an invalid protocol message or protocol state.</summary>
public sealed class ProtocolException : CommunicationException
{
    /// <summary>Initializes a protocol exception.</summary>
    public ProtocolException(string message, Exception? innerException = null)
        : base(CommunicationErrorCode.ProtocolError, message, innerException)
    {
    }
}

/// <summary>Represents a checksum or integrity validation failure.</summary>
public sealed class ChecksumException : CommunicationException
{
    /// <summary>Initializes a checksum exception.</summary>
    public ChecksumException(string message, Exception? innerException = null)
        : base(CommunicationErrorCode.ChecksumFailure, message, innerException)
    {
    }
}

/// <summary>Represents an explicit error response returned by a device.</summary>
public sealed class DeviceException : CommunicationException
{
    /// <summary>Initializes a device exception.</summary>
    public DeviceException(string message, string? deviceErrorCode = null, Exception? innerException = null)
        : base(CommunicationErrorCode.DeviceError, message, innerException)
    {
        DeviceErrorCode = deviceErrorCode;
    }

    /// <summary>Gets an optional protocol-specific device error code.</summary>
    public string? DeviceErrorCode { get; }
}

/// <summary>Represents a communication deadline that elapsed.</summary>
public sealed class CommunicationTimeoutException : TimeoutException
{
    /// <summary>Initializes a timeout exception.</summary>
    public CommunicationTimeoutException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
