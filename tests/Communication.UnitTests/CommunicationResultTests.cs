using Communication.Abstractions.Exceptions;
using Communication.Abstractions.Models;

namespace Communication.UnitTests;

public sealed class CommunicationResultTests
{
    [Fact]
    public void Successful_result_returns_its_value()
    {
        CommunicationResult<int> result = CommunicationResult<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(42, result.GetValueOrThrow());
    }

    [Theory]
    [InlineData(CommunicationErrorCode.Timeout, typeof(CommunicationTimeoutException))]
    [InlineData(CommunicationErrorCode.Canceled, typeof(OperationCanceledException))]
    [InlineData(CommunicationErrorCode.ConnectionFailure, typeof(ConnectionException))]
    [InlineData(CommunicationErrorCode.ProtocolError, typeof(ProtocolException))]
    [InlineData(CommunicationErrorCode.ChecksumFailure, typeof(ChecksumException))]
    [InlineData(CommunicationErrorCode.DeviceError, typeof(DeviceException))]
    public void Failed_result_maps_error_code_to_typed_exception(
        CommunicationErrorCode errorCode,
        Type expectedExceptionType)
    {
        CommunicationResult<int> result = CommunicationResult<int>.Failure(
            new CommunicationError(errorCode, "Expected failure."));

        Exception exception = Assert.ThrowsAny<Exception>(() => result.GetValueOrThrow());

        Assert.IsType(expectedExceptionType, exception);
    }

    [Fact]
    public void Exception_is_converted_to_structured_error()
    {
        ProtocolException exception = new("Bad frame.");

        CommunicationError error = CommunicationError.FromException(exception);

        Assert.Equal(CommunicationErrorCode.ProtocolError, error.Code);
        Assert.Same(exception, error.Exception);
    }
}
