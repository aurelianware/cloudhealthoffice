namespace CloudHealthOffice.Infrastructure.Data;

/// <summary>
/// A conditional write lost to a concurrent one: the document changed (its
/// revision / ETag moved on) between the read the write was based on and the
/// write. Nothing was written. Answered 409 Conflict by
/// <see cref="Middleware.ExceptionHandlingMiddleware"/>; the caller re-reads
/// and retries. Services derive their own types from it.
/// </summary>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
