namespace VirtualDofMatrix.Service.Api;

public enum ServiceErrorCode
{
    None = 0,
    ValidationFailed = 1000,
    DriverMissing = 2000,
    SigningBlocked = 2001,
    SecureBootConflict = 2002,
    PortConflict = 3000,
    PairNotFound = 3001,
    InternalError = 9000,
}

public sealed record ServiceResult<T>(bool Success, T? Data, ServiceErrorCode ErrorCode, string Message)
{
    public static ServiceResult<T> Ok(T data, string message = "ok") => new(true, data, ServiceErrorCode.None, message);

    public static ServiceResult<T> Fail(ServiceErrorCode errorCode, string message) => new(false, default, errorCode, message);
}

public sealed record PairMetadata(string TxPort, string RxPort, DateTimeOffset CreatedUtc);

public sealed record HealthInfo(string Version, bool DriverReachable, int ActivePairCount, DateTimeOffset UtcNow);

public sealed record PipeRequest(string Command, string? TxPort, string? RxPort);
