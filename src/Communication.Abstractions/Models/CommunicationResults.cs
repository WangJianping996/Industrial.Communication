using Communication.Abstractions.Exceptions;

namespace Communication.Abstractions.Models;

/// <summary>Contains structured details about a failed communication operation.</summary>
/// <param name="Code">The machine-readable error category.</param>
/// <param name="Message">A safe, human-readable description.</param>
/// <param name="Detail">Optional protocol or device detail that does not contain secrets.</param>
/// <param name="Exception">The originating exception, when retaining it is safe.</param>
public sealed record CommunicationError(
    CommunicationErrorCode Code,
    string Message,
    string? Detail = null,
    Exception? Exception = null)
{
    /// <summary>Creates a structured error from a known exception.</summary>
    public static CommunicationError FromException(Exception exception) => exception switch
    {
        TimeoutException => new(CommunicationErrorCode.Timeout, exception.Message, Exception: exception),
        OperationCanceledException => new(CommunicationErrorCode.Canceled, exception.Message, Exception: exception),
        ConnectionException => new(CommunicationErrorCode.ConnectionFailure, exception.Message, Exception: exception),
        ProtocolException => new(CommunicationErrorCode.ProtocolError, exception.Message, Exception: exception),
        ChecksumException => new(CommunicationErrorCode.ChecksumFailure, exception.Message, Exception: exception),
        DeviceException device => new(CommunicationErrorCode.DeviceError, device.Message, device.DeviceErrorCode, device),
        _ => new(CommunicationErrorCode.Unknown, exception.Message, Exception: exception),
    };
}

/// <summary>Represents a successful or failed operation without a return value.</summary>
public sealed class CommunicationResult
{
    private CommunicationResult(bool isSuccess, CommunicationError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets failure details, or <see langword="null"/> on success.</summary>
    public CommunicationError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static CommunicationResult Success() => new(true, null);

    /// <summary>Creates a failed result.</summary>
    public static CommunicationResult Failure(CommunicationError error) =>
        new(false, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Represents a successful or failed operation that returns a value.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class CommunicationResult<T>
{
    private readonly T? _value;

    private CommunicationResult(bool isSuccess, T? value, CommunicationError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the successful value, or the default value after failure.</summary>
    public T? Value => _value;

    /// <summary>Gets failure details, or <see langword="null"/> on success.</summary>
    public CommunicationError? Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static CommunicationResult<T> Success(T value) => new(true, value, null);

    /// <summary>Creates a failed result.</summary>
    public static CommunicationResult<T> Failure(CommunicationError error) =>
        new(false, default, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Returns the value or throws a typed communication exception.</summary>
    public T GetValueOrThrow()
    {
        if (IsSuccess)
        {
            return _value!;
        }

        throw CommunicationException.FromError(Error!);
    }
}
