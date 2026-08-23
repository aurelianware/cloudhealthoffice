namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Envelope returned by every gateway transaction. Pairs a normalized,
/// vendor-neutral Cloud Health Office result (<typeparamref name="TResult"/>)
/// with non-PHI <see cref="GatewayTransactionMetadata"/>.
///
/// The <typeparamref name="TResult"/> payload is always a Cloud Health Office
/// canonical model — never a vendor DTO or raw X12 — so domain services can
/// consume gateway output without taking a dependency on any vendor.
/// </summary>
/// <typeparam name="TResult">The canonical Cloud Health Office result type.</typeparam>
public sealed class GatewayResponse<TResult>
    where TResult : class
{
    /// <summary>True when the transaction produced a usable canonical result.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>The normalized result, present when <see cref="IsSuccess"/> is true.</summary>
    public TResult? Result { get; init; }

    /// <summary>Non-PHI transaction metadata. Always present.</summary>
    public GatewayTransactionMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Human-readable, non-PHI failure summary when <see cref="IsSuccess"/> is
    /// false. Must not contain member identifiers or other PHI.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Build a successful response around <paramref name="result"/>.</summary>
    public static GatewayResponse<TResult> Success(TResult result, GatewayTransactionMetadata metadata) =>
        new() { IsSuccess = true, Result = result, Metadata = metadata };

    /// <summary>Build a failed response with a non-PHI <paramref name="errorMessage"/>.</summary>
    public static GatewayResponse<TResult> Failure(string errorMessage, GatewayTransactionMetadata metadata) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage, Metadata = metadata };
}
