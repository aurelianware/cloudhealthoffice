using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Routing;

/// <summary>
/// Maps an external payer / trading-partner / authenticated-endpoint identity
/// onto a configured Cloud Health Office tenant. Trusted identifiers only:
/// <see cref="PayerEligibilityInquiry.ClaimedTenantId"/> is ignored.
/// </summary>
public sealed class PayerEligibilityRouter : IPayerEligibilityRouter
{
    private readonly IPayerEligibilityDirectory _directory;

    public PayerEligibilityRouter(IPayerEligibilityDirectory directory)
    {
        _directory = directory;
    }

    public PayerEligibilityRouteResolution ResolveIdentity(
        string? payerId,
        string? tradingPartnerId,
        string? authenticatedEndpointId) =>
        Resolve(new PayerEligibilityInquiry
        {
            PayerId = payerId,
            TradingPartnerId = tradingPartnerId,
            AuthenticatedEndpointId = authenticatedEndpointId
        });

    public PayerEligibilityRouteResolution Resolve(PayerEligibilityInquiry inquiry)
    {
        var routes = _directory.GetInboundRoutes();
        if (routes.Count == 0)
        {
            return PayerEligibilityRouteResolution.Fail(
                EligibilityBusinessStatus.UnableToRespond,
                "No inbound payer routes are configured.");
        }

        // Authenticated endpoint identity is the strongest signal: it comes
        // from the adapter trust boundary, not the request body.
        if (!string.IsNullOrWhiteSpace(inquiry.AuthenticatedEndpointId))
        {
            var endpointMatches = Match(
                routes,
                inquiry.AuthenticatedEndpointId,
                PayerEligibilityRoute.IdentifierKinds.EndpointId);
            var endpointResolution = ResolveMatches(endpointMatches, "authenticated endpoint");
            if (endpointResolution is not null)
            {
                return endpointResolution;
            }
        }

        var candidates = new List<PayerEligibilityRoute>();
        AddMatches(candidates, routes, inquiry.PayerId, PayerEligibilityRoute.IdentifierKinds.PayerId);
        AddMatches(candidates, routes, inquiry.TradingPartnerId, PayerEligibilityRoute.IdentifierKinds.TradingPartnerId);

        // A payer id is often also published as a trading-partner id (and
        // vice versa). Search both kinds when the inquiry supplied a value.
        AddMatches(candidates, routes, inquiry.PayerId, PayerEligibilityRoute.IdentifierKinds.TradingPartnerId);
        AddMatches(candidates, routes, inquiry.TradingPartnerId, PayerEligibilityRoute.IdentifierKinds.PayerId);

        if (candidates.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(inquiry.PayerId) &&
                string.IsNullOrWhiteSpace(inquiry.TradingPartnerId) &&
                string.IsNullOrWhiteSpace(inquiry.AuthenticatedEndpointId))
            {
                return PayerEligibilityRouteResolution.Fail(
                    EligibilityBusinessStatus.InvalidPayer,
                    "Payer identifier is required.");
            }

            return PayerEligibilityRouteResolution.Fail(
                EligibilityBusinessStatus.InvalidPayer,
                "Payer identifier did not match a configured Cloud Health Office payer.");
        }

        return ResolveMatches(candidates, "payer identifier")
               ?? PayerEligibilityRouteResolution.Fail(
                   EligibilityBusinessStatus.UnableToRespond,
                   "Unable to resolve payer tenant.");
    }

    private static PayerEligibilityRouteResolution? ResolveMatches(
        IReadOnlyList<PayerEligibilityRoute> matches, string signal)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var distinctTenants = matches
            .Select(m => m.TenantId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctTenants.Count > 1)
        {
            return PayerEligibilityRouteResolution.Fail(
                EligibilityBusinessStatus.AmbiguousPayer,
                $"Multiple Cloud Health Office tenants matched the {signal}.");
        }

        var distinctPayers = matches
            .Select(m => m.CanonicalPayerId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctPayers.Count > 1)
        {
            return PayerEligibilityRouteResolution.Fail(
                EligibilityBusinessStatus.AmbiguousPayer,
                $"Multiple Cloud Health Office payers matched the {signal}.");
        }

        var chosen = matches[0];
        return PayerEligibilityRouteResolution.Found(
            chosen.TenantId, chosen.CanonicalPayerId, chosen.PayerName);
    }

    private static List<PayerEligibilityRoute> Match(
        IReadOnlyList<PayerEligibilityRoute> routes, string value, string kind) =>
        routes
            .Where(r => string.Equals(r.IdentifierKind, kind, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.ExternalIdentifier, value, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static void AddMatches(
        List<PayerEligibilityRoute> accumulator,
        IReadOnlyList<PayerEligibilityRoute> routes,
        string? value,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var match in Match(routes, value, kind))
        {
            var already = accumulator.Any(existing =>
                string.Equals(existing.TenantId, match.TenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.CanonicalPayerId, match.CanonicalPayerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.ExternalIdentifier, match.ExternalIdentifier, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.IdentifierKind, match.IdentifierKind, StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                accumulator.Add(match);
            }
        }
    }
}
