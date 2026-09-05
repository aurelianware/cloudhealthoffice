namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>How a call to a remote payer resolved, independent of transport.</summary>
public enum RemoteCallOutcome
{
    /// <summary>The payer answered successfully and returned a payload.</summary>
    Success,

    /// <summary>The payer resolved no member from the supplied identity.</summary>
    NoMatch,

    /// <summary>The payer resolved more than one member.</summary>
    Ambiguous,

    /// <summary>The payer rejected our credentials or refused the exchange.</summary>
    Unauthorized,

    /// <summary>The payer could not be reached, timed out, or failed server-side.</summary>
    Unavailable,

    /// <summary>The payer answered, but the answer was empty/oversized/not usable as a response.</summary>
    InvalidResponse,
}

/// <summary>
/// The identity Cloud Health Office presents to a remote payer's
/// <c>$member-match</c>. Deliberately minimal — the member's own payer already
/// knows them, so only what the operation needs to resolve identity is sent. No
/// SSN, address, phone, or email leaves CHO on this path.
/// </summary>
public sealed class RemoteMemberMatchRequest
{
    /// <summary>Payer id CHO identifies itself as (the receiving payer of the exchange).</summary>
    public string ReceivingPayerId { get; init; } = string.Empty;

    /// <summary>The member's identifier with the target payer, when CHO holds it (a strong identifier).</summary>
    public string? MemberId { get; init; }

    public string? FamilyName { get; init; }
    public string? BirthDate { get; init; }

    /// <summary>Coverage context to resolve against: the target payer, as of a date.</summary>
    public string? RequestedPayerId { get; init; }
    public string? AsOfDate { get; init; }
}

/// <summary>
/// The member-scoped data request CHO sends after a successful match. Mirrors the
/// P2P-01 respond contract CHO itself serves — one Payer-to-Payer wire format,
/// used in both directions.
/// </summary>
public sealed class RemoteMemberDataRequest
{
    public string ReceivingPayerId { get; init; } = string.Empty;

    /// <summary>The member id as the REMOTE payer resolved them.</summary>
    public string MemberId { get; init; } = string.Empty;

    public int LookbackYears { get; init; } = 5;
}

/// <summary>
/// A remote payer's answer: an outcome plus, on success, the raw response
/// payload exactly as received. Parsing, validation, and interpretation are the
/// application layer's job — the transport neither trusts nor understands the
/// body.
/// </summary>
public sealed class RemoteCallResponse
{
    public RemoteCallOutcome Outcome { get; init; }

    /// <summary>Raw response payload (FHIR JSON) — present only on <see cref="RemoteCallOutcome.Success"/>.</summary>
    public string? Payload { get; init; }

    public static RemoteCallResponse Success(string payload) =>
        new() { Outcome = RemoteCallOutcome.Success, Payload = payload };

    public static RemoteCallResponse Failure(RemoteCallOutcome outcome) => new() { Outcome = outcome };
}

/// <summary>
/// Transport seam for calling another payer's Payer-to-Payer operations. The
/// application layer (<see cref="PayerToPayerOutboundService"/>) depends only on
/// this contract, so it knows nothing about HTTP, authentication schemes, or
/// status codes — and tests can drive the real orchestration against a
/// deterministic peer.
///
/// Implementations MUST only call the endpoint they are handed (resolved from
/// the trusted directory) and MUST NOT follow redirects to other hosts.
/// </summary>
public interface IPayerToPayerRemoteClient
{
    Task<RemoteCallResponse> MatchMemberAsync(
        PayerToPayerEndpoint endpoint, RemoteMemberMatchRequest request, CancellationToken ct = default);

    Task<RemoteCallResponse> RequestMemberDataAsync(
        PayerToPayerEndpoint endpoint, RemoteMemberDataRequest request, CancellationToken ct = default);
}

/// <summary>
/// Supplies the bearer credential to present to a remote payer, looked up by the
/// endpoint's <see cref="PayerToPayerEndpoint.CredentialKey"/>.
///
/// Payer-to-Payer transport security (SMART Backend Services / UDAP client
/// registration, mTLS) is negotiated per trading-partner and is deployment
/// integration work, not product code — this abstraction is the seam it lands
/// on. The default implementation supplies nothing, so an unonboarded payer's
/// endpoint answers Unauthorized rather than CHO pretending to hold a credential.
/// </summary>
public interface IPayerToPayerCredentialProvider
{
    Task<string?> GetAccessTokenAsync(PayerToPayerEndpoint endpoint, CancellationToken ct = default);
}

/// <summary>
/// Default credential provider: no credential is available until a payer is
/// onboarded. Fail-honest rather than fail-open — it never fabricates a token.
/// </summary>
public sealed class UnconfiguredPayerToPayerCredentialProvider : IPayerToPayerCredentialProvider
{
    public Task<string?> GetAccessTokenAsync(PayerToPayerEndpoint endpoint, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
