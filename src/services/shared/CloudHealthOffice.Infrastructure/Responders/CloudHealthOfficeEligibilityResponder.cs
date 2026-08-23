using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Responders;

/// <summary>
/// Orchestrates payer-side eligibility against Cloud Health Office member,
/// coverage, benefit, network, and accumulator data. Read-only: a 270
/// inquiry never mutates claims, accumulators, authorizations, payments,
/// enrollment, or coverage.
/// </summary>
public sealed class CloudHealthOfficeEligibilityResponder : IEligibilityResponder
{
    public const string AdapterName = "canonical";

    private readonly IPayerEligibilityRouter _router;
    private readonly IPayerEligibilityDirectory _directory;
    private readonly ILogger<CloudHealthOfficeEligibilityResponder> _logger;
    private readonly TimeProvider _clock;

    public CloudHealthOfficeEligibilityResponder(
        IPayerEligibilityRouter router,
        IPayerEligibilityDirectory directory,
        ILogger<CloudHealthOfficeEligibilityResponder> logger,
        TimeProvider? clock = null)
    {
        _router = router;
        _directory = directory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GatewayResponse<PayerEligibilityResponse>> RespondAsync(
        PayerEligibilityInquiry inquiry,
        CancellationToken ct = default)
    {
        var started = _clock.GetUtcNow();
        var choTransactionId = Guid.NewGuid().ToString("N");
        var adapter = string.IsNullOrWhiteSpace(inquiry.AdapterName)
            ? AdapterName
            : inquiry.AdapterName.Trim();
        var correlationId = FirstNonBlank(inquiry.CorrelationId, inquiry.TransactionId, choTransactionId);

        try
        {
            if (inquiry.DateOfService == default)
            {
                return Complete(
                    BuildRejection(
                        inquiry, choTransactionId, correlationId, adapter,
                        EligibilityBusinessStatus.InvalidDate,
                        tenantId: null,
                        "A date of service is required."),
                    started, adapter, correlationId);
            }

            var route = _router.Resolve(inquiry);
            if (!route.IsResolved)
            {
                var status = route.Status == EligibilityBusinessStatus.Success
                    ? EligibilityBusinessStatus.InvalidPayer
                    : route.Status;
                return Complete(
                    BuildRejection(
                        inquiry, choTransactionId, correlationId, adapter, status,
                        tenantId: null,
                        route.Message ?? "Payer could not be resolved."),
                    started, adapter, correlationId);
            }

            var subscriberLookup = await _directory.FindSubscriberAsync(
                route.TenantId!, PersonLookupQuery.From(inquiry.Subscriber), ct).ConfigureAwait(false);

            if (subscriberLookup.Status != MemberLookupStatus.Matched)
            {
                return Complete(
                    BuildRejection(
                        inquiry, choTransactionId, correlationId, adapter,
                        MapSubscriberLookup(subscriberLookup.Status),
                        route.TenantId,
                        SubscriberMessage(subscriberLookup.Status),
                        canonicalPayerId: route.CanonicalPayerId,
                        payerName: route.PayerName),
                    started, adapter, correlationId);
            }

            var subscriber = subscriberLookup.Member!;
            PayerDirectoryMember patientMember = subscriber;
            var dependentInquiry = inquiry.IsDependentInquiry();

            if (dependentInquiry)
            {
                var dependentLookup = await _directory.FindDependentAsync(
                    route.TenantId!,
                    subscriber.MemberId,
                    PersonLookupQuery.From(inquiry.Patient),
                    ct).ConfigureAwait(false);

                if (dependentLookup.Status != MemberLookupStatus.Matched)
                {
                    return Complete(
                        BuildRejection(
                            inquiry, choTransactionId, correlationId, adapter,
                            MapDependentLookup(dependentLookup.Status),
                            route.TenantId,
                            DependentMessage(dependentLookup.Status),
                            canonicalPayerId: route.CanonicalPayerId,
                            payerName: route.PayerName,
                            subscriber: ToPerson(subscriber)),
                        started, adapter, correlationId);
                }

                patientMember = dependentLookup.Member!;
            }

            var coverage = await _directory.GetCoverageAsync(
                route.TenantId!, patientMember.MemberId, inquiry.DateOfService, ct).ConfigureAwait(false);

            if (coverage is null)
            {
                return Complete(
                    BuildRejection(
                        inquiry, choTransactionId, correlationId, adapter,
                        EligibilityBusinessStatus.Success,
                        route.TenantId,
                        "No coverage on file for the requested date of service.",
                        canonicalPayerId: route.CanonicalPayerId,
                        payerName: route.PayerName,
                        subscriber: ToPerson(subscriber),
                        patient: dependentInquiry ? ToPerson(patientMember) : ToPerson(subscriber),
                        coverageStatus: PayerEligibilityCoverageStatus.Inactive),
                    started, adapter, correlationId);
            }

            var coverageStatus = coverage.Evaluate(inquiry.DateOfService);
            var serviceType = inquiry.PrimaryServiceTypeCode();
            var plan = await _directory.GetPlanAsync(route.TenantId!, coverage.PlanId, ct).ConfigureAwait(false);

            var provider = await _directory.FindProviderAsync(
                route.TenantId!, inquiry.RequestingProvider?.Npi, ct).ConfigureAwait(false);
            var networkStatus = ResolveNetwork(inquiry.RequestingProvider, provider);
            var inNetwork = networkStatus != PayerEligibilityNetworkStatus.OutOfNetwork;

            var accumulators = await _directory.GetAccumulatorsAsync(
                route.TenantId!, patientMember.MemberId, coverage.PlanId, ct).ConfigureAwait(false);

            var response = new PayerEligibilityResponse
            {
                TransactionId = inquiry.TransactionId,
                CorrelationId = correlationId,
                ExternalTransactionId = inquiry.ExternalTransactionId,
                ChoTransactionId = choTransactionId,
                TransportStatus = EligibilityTransportStatus.Success,
                BusinessStatus = EligibilityBusinessStatus.Success,
                CoverageStatus = coverageStatus,
                TenantId = route.TenantId,
                CanonicalPayerId = route.CanonicalPayerId,
                PayerName = route.PayerName,
                Subscriber = ToPerson(subscriber),
                Patient = ToPerson(patientMember),
                PlanId = coverage.PlanId,
                PlanName = coverage.PlanName,
                GroupNumber = coverage.GroupNumber,
                CoverageEffectiveDate = coverage.EffectiveDate,
                CoverageTerminationDate = coverage.TerminationDate,
                NetworkStatus = networkStatus,
                ProviderNpi = inquiry.RequestingProvider?.Npi,
                Deductible = accumulators?.ToDeductible(inNetwork),
                OutOfPocket = accumulators?.ToOutOfPocket(inNetwork)
            };

            if (networkStatus == PayerEligibilityNetworkStatus.ProviderNotOnFile)
            {
                response.ProviderMessage = "Requesting provider was not found on file; network-specific amounts may be incomplete.";
                response.Messages.Add(response.ProviderMessage);
            }

            if (plan is null)
            {
                response.BusinessStatus = EligibilityBusinessStatus.UnableToRespond;
                response.RejectionCode = nameof(EligibilityBusinessStatus.UnableToRespond);
                response.RejectionMessage = "Benefit plan data is unavailable for this coverage.";
                response.Messages.Add(response.RejectionMessage);
            }
            else if (!SupportsServiceType(plan, serviceType))
            {
                response.BusinessStatus = EligibilityBusinessStatus.UnsupportedServiceType;
                response.RejectionCode = nameof(EligibilityBusinessStatus.UnsupportedServiceType);
                response.RejectionMessage = "The requested service type is not supported for this plan.";
                response.Messages.Add(response.RejectionMessage);
            }
            else if (coverageStatus == PayerEligibilityCoverageStatus.Active)
            {
                response.Benefits = SelectBenefits(plan, serviceType, inNetwork, accumulators);
            }
            else
            {
                response.Messages.Add(CoverageMessage(coverageStatus));
            }

            return Complete(response, started, adapter, correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Inbound eligibility failed. Tenant={TenantId} Transaction={TransactionType} CorrelationId={CorrelationId} Adapter={Adapter} ErrorCategory={ErrorCategory}",
                null,
                HealthcareTransactionType.Eligibility270271,
                SanitizeForLog(correlationId),
                SanitizeForLog(adapter),
                GatewayErrorCategory.Internal);

            var failed = new PayerEligibilityResponse
            {
                TransactionId = inquiry.TransactionId,
                CorrelationId = correlationId,
                ExternalTransactionId = inquiry.ExternalTransactionId,
                ChoTransactionId = choTransactionId,
                TransportStatus = EligibilityTransportStatus.Failed,
                BusinessStatus = EligibilityBusinessStatus.UnableToRespond,
                CoverageStatus = PayerEligibilityCoverageStatus.Unknown,
                RejectionCode = nameof(EligibilityBusinessStatus.UnableToRespond),
                RejectionMessage = "Unable to respond to the eligibility inquiry."
            };

            return GatewayResponse<PayerEligibilityResponse>.Failure(
                "Unable to respond to the eligibility inquiry.",
                Metadata(inquiry, failed, started, adapter, correlationId, GatewayTransactionStatus.Failed, GatewayErrorCategory.Internal));
        }
    }

    private GatewayResponse<PayerEligibilityResponse> Complete(
        PayerEligibilityResponse response,
        DateTimeOffset started,
        string adapter,
        string? correlationId)
    {
        var latency = _clock.GetUtcNow() - started;
        var business = response.BusinessStatus.ToString();
        var coverage = response.CoverageStatus.ToString();
        var transport = response.TransportStatus.ToString();

        ChoMetrics.PayerEligibilityInquiries.Add(1,
            new KeyValuePair<string, object?>("cho.adapter", adapter),
            new KeyValuePair<string, object?>("cho.business_status", business),
            new KeyValuePair<string, object?>("cho.coverage_status", coverage),
            new KeyValuePair<string, object?>("cho.transport_status", transport));
        ChoMetrics.PayerEligibilityDuration.Record(latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.adapter", adapter),
            new KeyValuePair<string, object?>("cho.business_status", business));

        var errorCategory = response.TransportStatus == EligibilityTransportStatus.Failed
            ? GatewayErrorCategory.Internal
            : response.BusinessStatus is EligibilityBusinessStatus.Success
                ? GatewayErrorCategory.None
                : GatewayErrorCategory.PayerRejected;

        var txStatus = response.TransportStatus == EligibilityTransportStatus.Failed
            ? GatewayTransactionStatus.Failed
            : response.BusinessStatus is EligibilityBusinessStatus.Success
                ? GatewayTransactionStatus.Completed
                : GatewayTransactionStatus.Rejected;

        _logger.LogInformation(
            "Inbound eligibility completed. Tenant={TenantId} Transaction={TransactionType} CorrelationId={CorrelationId} Transport={Transport} Business={Business} Coverage={Coverage} LatencyMs={LatencyMs} Adapter={Adapter} ErrorCategory={ErrorCategory}",
            SanitizeForLog(response.TenantId),
            HealthcareTransactionType.Eligibility270271,
            SanitizeForLog(correlationId),
            transport,
            business,
            coverage,
            (int)latency.TotalMilliseconds,
            SanitizeForLog(adapter),
            errorCategory);

        return GatewayResponse<PayerEligibilityResponse>.Success(
            response,
            Metadata(
                inquiry: null,
                response,
                started,
                adapter,
                correlationId,
                txStatus,
                errorCategory));
    }

    private GatewayTransactionMetadata Metadata(
        PayerEligibilityInquiry? inquiry,
        PayerEligibilityResponse response,
        DateTimeOffset started,
        string adapter,
        string? correlationId,
        GatewayTransactionStatus status,
        GatewayErrorCategory errorCategory)
    {
        var completed = _clock.GetUtcNow();
        return new GatewayTransactionMetadata
        {
            GatewayName = adapter,
            TransactionType = HealthcareTransactionType.Eligibility270271,
            SubmittedAtUtc = started,
            CompletedAtUtc = completed,
            Status = status,
            ExternalTransactionId = response.ExternalTransactionId ?? inquiry?.ExternalTransactionId,
            CorrelationId = correlationId,
            TenantId = response.TenantId ?? string.Empty,
            Latency = completed - started,
            ErrorCategory = errorCategory
        };
    }

    private static PayerEligibilityResponse BuildRejection(
        PayerEligibilityInquiry inquiry,
        string choTransactionId,
        string? correlationId,
        string adapter,
        EligibilityBusinessStatus status,
        string? tenantId,
        string message,
        string? canonicalPayerId = null,
        string? payerName = null,
        GatewayEligibilityPerson? subscriber = null,
        GatewayEligibilityPerson? patient = null,
        PayerEligibilityCoverageStatus coverageStatus = PayerEligibilityCoverageStatus.Unknown)
    {
        _ = adapter;
        return new PayerEligibilityResponse
        {
            TransactionId = inquiry.TransactionId,
            CorrelationId = correlationId,
            ExternalTransactionId = inquiry.ExternalTransactionId,
            ChoTransactionId = choTransactionId,
            TransportStatus = EligibilityTransportStatus.Success,
            BusinessStatus = status,
            CoverageStatus = coverageStatus,
            RejectionCode = status.ToString(),
            RejectionMessage = message,
            TenantId = tenantId,
            CanonicalPayerId = canonicalPayerId,
            PayerName = payerName,
            Subscriber = subscriber,
            Patient = patient,
            Messages = { message }
        };
    }

    private static EligibilityBusinessStatus MapSubscriberLookup(MemberLookupStatus status) =>
        status switch
        {
            MemberLookupStatus.NotFound => EligibilityBusinessStatus.SubscriberNotFound,
            MemberLookupStatus.Ambiguous => EligibilityBusinessStatus.SubscriberAmbiguous,
            MemberLookupStatus.InvalidRequest => EligibilityBusinessStatus.InvalidSubscriber,
            _ => EligibilityBusinessStatus.UnableToRespond
        };

    private static EligibilityBusinessStatus MapDependentLookup(MemberLookupStatus status) =>
        status switch
        {
            MemberLookupStatus.NotFound => EligibilityBusinessStatus.DependentNotFound,
            MemberLookupStatus.Ambiguous => EligibilityBusinessStatus.InvalidDependent,
            MemberLookupStatus.InvalidRequest => EligibilityBusinessStatus.InvalidDependent,
            _ => EligibilityBusinessStatus.UnableToRespond
        };

    private static string SubscriberMessage(MemberLookupStatus status) =>
        status switch
        {
            MemberLookupStatus.NotFound => "Subscriber was not found.",
            MemberLookupStatus.Ambiguous => "Subscriber identity matched more than one member.",
            MemberLookupStatus.InvalidRequest => "Subscriber identity is missing or invalid.",
            _ => "Unable to resolve subscriber."
        };

    private static string DependentMessage(MemberLookupStatus status) =>
        status switch
        {
            MemberLookupStatus.NotFound => "Dependent was not found on the subscriber's coverage.",
            MemberLookupStatus.Ambiguous => "Dependent identity matched more than one member.",
            MemberLookupStatus.InvalidRequest => "Dependent identity is missing or invalid.",
            _ => "Unable to resolve dependent."
        };

    private static string CoverageMessage(PayerEligibilityCoverageStatus status) =>
        status switch
        {
            PayerEligibilityCoverageStatus.Future => "Coverage is not yet effective for the requested date of service.",
            PayerEligibilityCoverageStatus.Terminated => "Coverage is terminated for the requested date of service.",
            PayerEligibilityCoverageStatus.Inactive => "Coverage is inactive for the requested date of service.",
            _ => "Coverage status could not be determined."
        };

    private static PayerEligibilityNetworkStatus ResolveNetwork(
        PayerEligibilityProvider? requested, PayerDirectoryProvider? resolved)
    {
        if (requested is null || !requested.HasIdentity)
        {
            return PayerEligibilityNetworkStatus.Unknown;
        }

        if (resolved is null)
        {
            return PayerEligibilityNetworkStatus.ProviderNotOnFile;
        }

        return resolved.InNetwork
            ? PayerEligibilityNetworkStatus.InNetwork
            : PayerEligibilityNetworkStatus.OutOfNetwork;
    }

    private static bool SupportsServiceType(PayerDirectoryPlan plan, string serviceType) =>
        plan.SupportedServiceTypeCodes.Any(code =>
            string.Equals(code, serviceType, StringComparison.OrdinalIgnoreCase));

    private static List<GatewayEligibilityBenefit> SelectBenefits(
        PayerDirectoryPlan plan,
        string serviceType,
        bool inNetwork,
        PayerDirectoryAccumulatorSnapshot? accumulators)
    {
        var selected = plan.Benefits
            .Where(b => string.Equals(b.ServiceTypeCode, serviceType, StringComparison.OrdinalIgnoreCase))
            .Where(b => b.InNetwork == inNetwork)
            .Select(b => new GatewayEligibilityBenefit
            {
                BenefitCode = b.BenefitCode,
                ServiceTypeCode = b.ServiceTypeCode,
                ServiceTypeName = b.ServiceTypeName,
                CoverageLevel = b.CoverageLevel,
                InNetwork = b.InNetwork,
                TimePeriod = b.TimePeriod,
                Amount = OverlayRemaining(b, accumulators),
                Percent = b.Percent,
                CopayAmount = b.CopayAmount,
                CoinsurancePercent = b.CoinsurancePercent,
                AuthorizationRequired = b.AuthorizationRequired,
                Messages = b.Messages.ToList()
            })
            .ToList();

        return selected;
    }

    private static decimal? OverlayRemaining(
        PayerDirectoryBenefit benefit, PayerDirectoryAccumulatorSnapshot? accumulators)
    {
        if (accumulators is null)
        {
            return benefit.Amount;
        }

        if (string.Equals(benefit.BenefitCode, "C", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(benefit.TimePeriod, "Remaining", StringComparison.OrdinalIgnoreCase))
        {
            return accumulators.IndividualDeductibleRemaining;
        }

        if (string.Equals(benefit.BenefitCode, "G", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(benefit.TimePeriod, "Remaining", StringComparison.OrdinalIgnoreCase))
        {
            return accumulators.IndividualOutOfPocketRemaining;
        }

        return benefit.Amount;
    }

    private static GatewayEligibilityPerson ToPerson(PayerDirectoryMember member) =>
        new()
        {
            MemberId = member.MemberId,
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth,
            RelationshipToSubscriber = member.RelationshipToSubscriber
        };

    private static string? SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
