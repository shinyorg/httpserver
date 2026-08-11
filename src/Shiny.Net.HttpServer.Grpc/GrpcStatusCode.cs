namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// The gRPC status codes, as defined by the gRPC specification and carried in the
/// <c>grpc-status</c> trailer. The numeric values are part of the wire protocol.
/// </summary>
public enum GrpcStatusCode
{
    /// <summary>The call completed successfully.</summary>
    Ok = 0,

    /// <summary>The call was cancelled, typically by the caller.</summary>
    Cancelled = 1,

    /// <summary>An error whose cause is not otherwise mapped. The default for an unhandled exception.</summary>
    Unknown = 2,

    /// <summary>The caller supplied an argument the server cannot work with, whatever the state.</summary>
    InvalidArgument = 3,

    /// <summary>The deadline expired before the call completed.</summary>
    DeadlineExceeded = 4,

    /// <summary>The requested entity was not found.</summary>
    NotFound = 5,

    /// <summary>The entity the caller tried to create already exists.</summary>
    AlreadyExists = 6,

    /// <summary>The caller is authenticated but not allowed to perform this operation.</summary>
    PermissionDenied = 7,

    /// <summary>A resource — quota, memory, message size — has been exhausted.</summary>
    ResourceExhausted = 8,

    /// <summary>The system is not in a state the operation can run against.</summary>
    FailedPrecondition = 9,

    /// <summary>The operation was aborted, typically by a concurrency conflict.</summary>
    Aborted = 10,

    /// <summary>The operation was attempted past the valid range.</summary>
    OutOfRange = 11,

    /// <summary>The method is not implemented or not supported by this server.</summary>
    Unimplemented = 12,

    /// <summary>An internal invariant was broken. Something is wrong with the server itself.</summary>
    Internal = 13,

    /// <summary>The service is unavailable — usually transient, and usually worth retrying.</summary>
    Unavailable = 14,

    /// <summary>Unrecoverable data loss or corruption.</summary>
    DataLoss = 15,

    /// <summary>The caller could not be authenticated.</summary>
    Unauthenticated = 16
}
