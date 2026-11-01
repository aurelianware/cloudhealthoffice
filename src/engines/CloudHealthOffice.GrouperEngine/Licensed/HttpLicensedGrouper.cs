using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.GrouperEngine.Licensed;

/// <summary>
/// Asks the plan's licensed APR-DRG and EAPG grouper over HTTP, through the
/// contract in docs/integration/LICENSED-GROUPER-CONTRACT.md.
///
/// <para>
/// The plan runs the grouper (3M/Solventum Core Grouping Software or a
/// vendor's hosted equivalent) under its own licence, behind a thin adapter
/// that speaks this JSON. CHO sends grouper inputs and reads back a
/// classification; no weight, rate or price crosses the wire.
/// </para>
///
/// <para>
/// <b>Fail closed.</b> Every answer is checked before it is believed: the
/// APR-DRG must be three digits and the severity 1 to 4, the version must be
/// the one asked for, and an EAPG answer must cover every requested line
/// exactly once. Anything else is <see cref="LicensedGroupingStatus.InvalidResponse"/>
/// and the caller pends. A transport failure is
/// <see cref="LicensedGroupingStatus.Unavailable"/>. Neither falls back to a
/// billed group.
/// </para>
///
/// <para>
/// The request carries PHI (diagnoses, age, sex), so logs name only the
/// claim reference and the outcome.
/// </para>
/// </summary>
public sealed partial class HttpLicensedGrouper : IAprDrgGrouper, IEapgGrouper
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly LicensedGrouperOptions _options;
    private readonly ILogger<HttpLicensedGrouper> _logger;

    /// <summary>Creates the client. <paramref name="http"/> must already carry the base address.</summary>
    public HttpLicensedGrouper(
        HttpClient http,
        LicensedGrouperOptions options,
        ILogger<HttpLicensedGrouper>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger<HttpLicensedGrouper>.Instance;
    }

    // ─────────────────────────────────────────────────────────
    //  APR-DRG
    // ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AprDrgGroupingResult> GroupAsync(
        AprDrgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (wire, failure) = await PostAsync<AprDrgWireResponse>(
            _options.AprDrgPath, request, request.ClaimReference, "APR-DRG", cancellationToken).ConfigureAwait(false);
        if (wire is null)
        {
            return AprDrgGroupingResult.NotGrouped(failure!.Value.Status, request.BilledDrg, failure.Value.Note);
        }

        return InterpretAprDrg(request, wire);
    }

    internal static AprDrgGroupingResult InterpretAprDrg(AprDrgGroupingRequest request, AprDrgWireResponse wire)
    {
        var messages = wire.Messages ?? [];

        AprDrgGroupingResult Refuse(LicensedGroupingStatus status, string note) => new()
        {
            Status = status,
            GrouperReturnCode = wire.ReturnCode,
            GrouperVersion = wire.Version,
            GrouperName = wire.GrouperName,
            BilledDrg = request.BilledDrg,
            Notes = [note, .. messages],
        };

        if (!wire.Grouped)
        {
            return Refuse(LicensedGroupingStatus.RejectedByGrouper,
                $"The licensed grouper did not group the stay (return code {wire.ReturnCode ?? "none"}).");
        }

        var drg = wire.AprDrg?.Trim();
        if (drg is null || !ThreeDigits().IsMatch(drg))
        {
            return Refuse(LicensedGroupingStatus.InvalidResponse,
                $"The grouper reported APR-DRG '{wire.AprDrg}', which is not a three-digit group.");
        }

        if (wire.Soi is not (>= 1 and <= 4))
        {
            return Refuse(LicensedGroupingStatus.InvalidResponse,
                $"The grouper reported severity of illness '{wire.Soi}', outside 1 to 4.");
        }

        if (wire.Rom is { } rom && rom is < 1 or > 4)
        {
            return Refuse(LicensedGroupingStatus.InvalidResponse,
                $"The grouper reported risk of mortality '{rom}', outside 1 to 4.");
        }

        if (!VersionMatches(request.RequestedVersion, wire.Version))
        {
            return Refuse(LicensedGroupingStatus.InvalidResponse,
                $"APR-DRG version {request.RequestedVersion} was requested and the grouper used '{wire.Version}'; "
                + "the state's weights are published per version.");
        }

        return new AprDrgGroupingResult
        {
            Status = LicensedGroupingStatus.Grouped,
            AprDrg = drg,
            SeverityOfIllness = wire.Soi,
            RiskOfMortality = wire.Rom,
            Mdc = wire.Mdc,
            GrouperReturnCode = wire.ReturnCode,
            GrouperVersion = wire.Version,
            GrouperName = wire.GrouperName,
            BilledDrg = request.BilledDrg,
            Notes = messages,
        };
    }

    // ─────────────────────────────────────────────────────────
    //  EAPG
    // ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<EapgGroupingResult> GroupAsync(
        EapgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (wire, failure) = await PostAsync<EapgWireResponse>(
            _options.EapgPath, request, request.ClaimReference, "EAPG", cancellationToken).ConfigureAwait(false);
        if (wire is null)
        {
            return EapgGroupingResult.NotGrouped(failure!.Value.Status, failure.Value.Note);
        }

        return InterpretEapg(request, wire);
    }

    internal static EapgGroupingResult InterpretEapg(EapgGroupingRequest request, EapgWireResponse wire)
    {
        var messages = wire.Messages ?? [];

        EapgGroupingResult Refuse(LicensedGroupingStatus status, string note) => new()
        {
            Status = status,
            GrouperReturnCode = wire.ReturnCode,
            GrouperVersion = wire.Version,
            GrouperName = wire.GrouperName,
            Notes = [note, .. messages],
        };

        if (!wire.Grouped)
        {
            return Refuse(LicensedGroupingStatus.RejectedByGrouper,
                $"The licensed grouper did not group the claim (return code {wire.ReturnCode ?? "none"}).");
        }

        if (!VersionMatches(request.RequestedVersion, wire.Version))
        {
            return Refuse(LicensedGroupingStatus.InvalidResponse,
                $"EAPG version {request.RequestedVersion} was requested and the grouper used '{wire.Version}'.");
        }

        var requested = request.Lines.Select(l => l.LineNumber).ToHashSet();
        var answered = wire.Lines ?? [];
        var seen = new HashSet<int>();
        var lines = new List<EapgLineResult>(answered.Count);

        foreach (var line in answered)
        {
            if (!requested.Contains(line.LineNumber))
            {
                return Refuse(LicensedGroupingStatus.InvalidResponse,
                    $"The grouper answered for line {line.LineNumber}, which was not on the claim.");
            }

            if (!seen.Add(line.LineNumber))
            {
                return Refuse(LicensedGroupingStatus.InvalidResponse,
                    $"The grouper answered for line {line.LineNumber} more than once.");
            }

            if (line.Action is not { } action || !Enum.IsDefined(action))
            {
                return Refuse(LicensedGroupingStatus.InvalidResponse,
                    $"The grouper gave line {line.LineNumber} no recognisable payment action.");
            }

            decimal factor;
            switch (action)
            {
                case EapgLineAction.Payable:
                case EapgLineAction.Discounted:
                    if (string.IsNullOrWhiteSpace(line.Eapg))
                    {
                        return Refuse(LicensedGroupingStatus.InvalidResponse,
                            $"Line {line.LineNumber} is {action} but carries no EAPG to weight it by.");
                    }

                    factor = line.DiscountFactor ?? (action == EapgLineAction.Payable ? 1m : -1m);
                    var valid = action == EapgLineAction.Payable
                        ? factor == 1m
                        : factor is > 0m and < 1m;
                    if (!valid)
                    {
                        return Refuse(LicensedGroupingStatus.InvalidResponse,
                            $"Line {line.LineNumber} is {action} with discount factor '{line.DiscountFactor}'; "
                            + "payable lines pay 1 and discounted lines a share between 0 and 1.");
                    }
                    break;

                default:
                    // Packaged, consolidated and not-payable lines pay nothing
                    // separately whatever factor a vendor fills in by default.
                    factor = 0m;
                    break;
            }

            lines.Add(new EapgLineResult
            {
                LineNumber = line.LineNumber,
                Eapg = line.Eapg?.Trim(),
                EapgType = line.EapgType,
                EapgCategory = line.EapgCategory,
                Action = action,
                DiscountFactor = factor,
                VisitNumber = line.VisitNumber,
                Messages = line.Messages ?? [],
            });
        }

        var missing = requested.Except(seen).OrderBy(n => n).ToList();
        if (missing.Count > 0)
        {
            return Refuse(LicensedGroupingStatus.InvalidResponse,
                $"The grouper gave no answer for line(s) {string.Join(", ", missing)}.");
        }

        return new EapgGroupingResult
        {
            Status = LicensedGroupingStatus.Grouped,
            Lines = lines.OrderBy(l => l.LineNumber).ToList(),
            GrouperReturnCode = wire.ReturnCode,
            GrouperVersion = wire.Version,
            GrouperName = wire.GrouperName,
            Notes = messages,
        };
    }

    // ─────────────────────────────────────────────────────────
    //  Transport
    // ─────────────────────────────────────────────────────────

    private async Task<(T? Response, (LicensedGroupingStatus Status, string Note)? Failure)> PostAsync<T>(
        string path, object body, string claimReference, string kind, CancellationToken ct)
        where T : class
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body, body.GetType(), options: Json),
            };
            if (!string.IsNullOrEmpty(_options.ApiKey))
            {
                message.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.ApiKey);
            }

            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Licensed {Kind} grouper answered HTTP {StatusCode} for claim {ClaimReference}",
                    kind, (int)response.StatusCode, claimReference);
                return (null, (LicensedGroupingStatus.Unavailable,
                    $"The licensed {kind} grouper answered HTTP {(int)response.StatusCode}."));
            }

            var parsed = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
            if (parsed is null)
            {
                return (null, (LicensedGroupingStatus.InvalidResponse,
                    $"The licensed {kind} grouper returned an empty body."));
            }

            return (parsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Licensed {Kind} grouper timed out for claim {ClaimReference}", kind, claimReference);
            return (null, (LicensedGroupingStatus.Unavailable,
                $"The licensed {kind} grouper did not answer within {_options.TimeoutSeconds} seconds."));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Licensed {Kind} grouper unreachable for claim {ClaimReference}", kind, claimReference);
            return (null, (LicensedGroupingStatus.Unavailable,
                $"The licensed {kind} grouper could not be reached."));
        }
        catch (JsonException)
        {
            // The exception text can echo the body, which can echo PHI.
            _logger.LogWarning(
                "Licensed {Kind} grouper returned unreadable JSON for claim {ClaimReference}", kind, claimReference);
            return (null, (LicensedGroupingStatus.InvalidResponse,
                $"The licensed {kind} grouper returned a body CHO could not read."));
        }
    }

    /// <summary>
    /// Whether the grouper used the requested version. "40" matches "40" and
    /// "40.0"; it does not match "41" or "400".
    /// </summary>
    internal static bool VersionMatches(string? requested, string? used)
    {
        if (string.IsNullOrWhiteSpace(requested)) return true;
        if (string.IsNullOrWhiteSpace(used)) return false;

        var want = requested.Trim();
        var got = used.Trim();
        return string.Equals(want, got, StringComparison.OrdinalIgnoreCase)
            || got.StartsWith(want + ".", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex ThreeDigits();

    // ─────────────────────────────────────────────────────────
    //  Wire shapes (the contract's responses)
    // ─────────────────────────────────────────────────────────

    internal sealed record AprDrgWireResponse
    {
        public bool Grouped { get; init; }
        public string? ReturnCode { get; init; }
        public string? AprDrg { get; init; }
        public int? Soi { get; init; }
        public int? Rom { get; init; }
        public int? Mdc { get; init; }
        public string? Version { get; init; }
        public string? GrouperName { get; init; }
        public List<string>? Messages { get; init; }
    }

    internal sealed record EapgWireResponse
    {
        public bool Grouped { get; init; }
        public string? ReturnCode { get; init; }
        public string? Version { get; init; }
        public string? GrouperName { get; init; }
        public List<string>? Messages { get; init; }
        public List<EapgWireLine>? Lines { get; init; }
    }

    internal sealed record EapgWireLine
    {
        public int LineNumber { get; init; }
        public string? Eapg { get; init; }
        public string? EapgType { get; init; }
        public string? EapgCategory { get; init; }
        public EapgLineAction? Action { get; init; }
        public decimal? DiscountFactor { get; init; }
        public int? VisitNumber { get; init; }
        public List<string>? Messages { get; init; }
    }
}
