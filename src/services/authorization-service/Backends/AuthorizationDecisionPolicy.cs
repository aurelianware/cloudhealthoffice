using AuthorizationService.Models;

namespace AuthorizationService.Backends;

/// <summary>Validates a complete decision before changing the authoritative record.</summary>
public static class AuthorizationDecisionPolicy
{
    public static string? DecisionFor(AuthorizationStatus status) => status switch
    {
        AuthorizationStatus.Approved => "A1",
        AuthorizationStatus.Modified => "A2",
        AuthorizationStatus.Denied => "A3",
        AuthorizationStatus.Pended => "A4",
        _ => null,
    };

    public static void InitializeLines(Authorization authorization)
    {
        var services = authorization.RequestedServices ?? new();
        if (services.Any(s => s is null || s.RequestedUnits <= 0))
            throw new ArgumentException("Requested service lines must have positive units.");
        var numbers = services.Select((s, i) => s.LineNumber ?? i + 1).ToArray();
        if (numbers.Any(n => n <= 0) || numbers.Distinct().Count() != numbers.Length)
            throw new ArgumentException("Requested service line numbers must be positive and unique.");
        for (var i = 0; i < services.Count; i++) services[i].LineNumber = numbers[i];
    }

    public static List<AuthorizationLineDecision> Prepare(
        Authorization authorization, AuthorizationStatus status, string? decision,
        AuthorizationDecisionDetails details)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("Unknown authorization status.");
        var expected = DecisionFor(status);
        if (expected != decision)
            throw new ArgumentException("Review decision must match the authorization status.");
        if (expected is null)
        {
            if (details.LineDecisions?.Count > 0)
                throw new ArgumentException("Service-line decisions require an A1, A2, A3 or A4 status.");
            return new();
        }

        var services = authorization.RequestedServices ?? new();
        if (services.Any(s => s is null)) throw new ArgumentException("Requested service lines cannot be null.");
        var numbers = services.Select((s, i) => s.LineNumber ?? i + 1).ToArray();
        if (numbers.Any(n => n <= 0) || numbers.Distinct().Count() != numbers.Length)
            throw new ArgumentException("Requested service line numbers must be positive and unique.");
        if (services.Any(s => s.RequestedUnits <= 0))
            throw new ArgumentException("Requested service units must be positive.");
        if (services.Count > 1 && details.ApprovedUnits.HasValue)
            throw new ArgumentException("Specify quantities per line for a multi-line authorization.");
        var explicitLines = details.LineDecisions ?? new();
        if (explicitLines.Any(l => l is null)) throw new ArgumentException("Service-line decisions cannot be null.");
        if (explicitLines.Count > 0 && (explicitLines.Count != services.Count ||
            explicitLines.Select(l => l.LineNumber).Distinct().Count() != explicitLines.Count ||
            explicitLines.Any(l => !numbers.Contains(l.LineNumber))))
            throw new ArgumentException("Supply exactly one decision for every requested service line.");
        if (decision == "A2" && explicitLines.Count == 0 && services.Count != 1)
            throw new ArgumentException("A modified multi-line authorization requires explicit line decisions.");
        if (services.Count == 0 && decision == "A3") RequireReason(details.DenialReasonCode, details.DenialReason);

        var result = services.Select((s, i) =>
        {
            var input = explicitLines.Count == 0 ? new AuthorizationLineDecision
            {
                LineNumber = numbers[i], ReviewDecision = decision!,
                ApprovedUnits = services.Count == 1 ? details.ApprovedUnits : null,
                DenialReasonCode = details.DenialReasonCode, DenialReason = details.DenialReason,
                PendReason = details.PendReason,
            } : explicitLines.Single(l => l.LineNumber == numbers[i]);
            var units = input.ApprovedUnits;
            switch (input.ReviewDecision)
            {
                case "A1":
                    if (units.HasValue && units != s.RequestedUnits)
                        throw new ArgumentException("An A1 line must approve all requested units; use A2 for a reduction.");
                    units = s.RequestedUnits;
                    break;
                case "A2":
                    if (!units.HasValue || units <= 0 || units >= s.RequestedUnits)
                        throw new ArgumentException("An A2 line requires positive approved units below the requested units.");
                    RequireReason(input.DenialReasonCode, input.DenialReason);
                    break;
                case "A3":
                    RequireReason(input.DenialReasonCode, input.DenialReason);
                    if (units.HasValue && units != 0) throw new ArgumentException("A denied line cannot approve units.");
                    units = 0;
                    break;
                case "A4":
                    if (units.HasValue) throw new ArgumentException("A pended line cannot approve units.");
                    break;
                default: throw new ArgumentException("Line decisions must be A1, A2, A3 or A4.");
            }
            return new AuthorizationLineDecision
            {
                LineNumber = numbers[i], ReviewDecision = input.ReviewDecision, ApprovedUnits = units,
                DenialReasonCode = input.ReviewDecision is "A2" or "A3" ? input.DenialReasonCode : null,
                DenialReason = input.ReviewDecision is "A2" or "A3" ? input.DenialReason : null,
                PendReason = input.ReviewDecision == "A4" ? input.PendReason : null,
            };
        }).ToList();
        if (result.Count > 0)
        {
            var aggregate = result.All(l => l.ReviewDecision == "A1") ? "A1"
                : result.All(l => l.ReviewDecision == "A3") ? "A3"
                : result.Any(l => l.ReviewDecision is "A1" or "A2") ? "A2" : "A4";
            if (aggregate != decision) throw new ArgumentException("Header decision must agree with the service-line outcomes.");
        }
        return result;
    }

    public static void Apply(Authorization authorization, AuthorizationStatus status, string? decision,
        AuthorizationDecisionDetails details, List<AuthorizationLineDecision> lines)
    {
        if (DecisionFor(status) is null) return;
        for (var i = 0; i < lines.Count; i++)
        {
            var service = authorization.RequestedServices[i];
            var line = lines[i];
            service.LineNumber = line.LineNumber;
            service.ServiceStatus = line.ReviewDecision;
            service.ApprovedUnits = line.ApprovedUnits;
            service.DenialReasonCode = line.DenialReasonCode;
            service.DenialReason = line.DenialReason;
            service.PendReason = line.PendReason;
        }
        authorization.ApprovedUnits = lines.Count == 1 ? lines[0].ApprovedUnits : null;
        var adverse = lines.FirstOrDefault(l => l.ReviewDecision is "A2" or "A3");
        authorization.DenialReasonCode = adverse?.DenialReasonCode ?? (decision == "A3" ? details.DenialReasonCode : null);
        authorization.DenialReason = adverse?.DenialReason ?? (decision == "A3" ? details.DenialReason : null);
        authorization.PendReason = lines.FirstOrDefault(l => l.ReviewDecision == "A4")?.PendReason
            ?? (decision == "A4" ? details.PendReason : null);
    }

    public static List<AuthorizationLineDecision> Snapshot(Authorization authorization) =>
        (authorization.RequestedServices ?? new()).Select((s, i) => new AuthorizationLineDecision
        {
            LineNumber = s.LineNumber ?? i + 1, ReviewDecision = s.ServiceStatus ?? string.Empty,
            ApprovedUnits = s.ApprovedUnits, DenialReasonCode = s.DenialReasonCode,
            DenialReason = s.DenialReason, PendReason = s.PendReason,
        }).Where(l => l.ReviewDecision.Length > 0).ToList();

    private static void RequireReason(string? code, string? text)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Every denied or reduced service requires a coded and human-readable reason.");
    }
}
