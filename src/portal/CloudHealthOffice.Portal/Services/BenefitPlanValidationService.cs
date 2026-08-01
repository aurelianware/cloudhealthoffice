using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Runs read-only plan checks in every environment and, when explicitly enabled,
/// proves the selected plan through the real member -> coverage -> raw 837 ->
/// adjudication path using isolated synthetic records.
/// </summary>
public sealed class BenefitPlanValidationService : IBenefitPlanValidationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> InProgressStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Submitted", "Received", "InAdjudication" };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IBenefitPlanService _benefitPlans;
    private readonly IClaimsService _claims;
    private readonly ITenantContextService _tenantContext;
    private readonly ILogger<BenefitPlanValidationService> _logger;

    public BenefitPlanValidationService(
        HttpClient httpClient,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IBenefitPlanService benefitPlans,
        IClaimsService claims,
        ITenantContextService tenantContext,
        ILogger<BenefitPlanValidationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _benefitPlans = benefitPlans;
        _claims = claims;
        _tenantContext = tenantContext;
        _logger = logger;

        SyntheticClaimsEnabled = environment.IsDevelopment()
            || string.Equals(configuration["Authentication:Mode"], "LocalDemo", StringComparison.OrdinalIgnoreCase)
            || configuration.GetValue<bool>("Features:BenefitPlanSyntheticValidationEnabled");
    }

    public bool SyntheticClaimsEnabled { get; }

    public async Task<BenefitPlanValidationResult> ValidateAsync(
        BenefitPlanDetails plan,
        DateTime serviceDate,
        CancellationToken cancellationToken = default)
    {
        serviceDate = serviceDate.Date;
        var result = new BenefitPlanValidationResult { ServiceDate = serviceDate };

        AddCheck(result, "Effective dates",
            serviceDate >= plan.EffectiveDate.Date
                && (!plan.TerminationDate.HasValue || serviceDate <= plan.TerminationDate.Value.Date),
            $"The plan is effective on {serviceDate:MMM d, yyyy}.",
            $"The plan is not effective on {serviceDate:MMM d, yyyy}.");

        AddCheck(result, "Published version",
            string.Equals(plan.VersionState, "Published", StringComparison.OrdinalIgnoreCase)
                || string.Equals(plan.Status, "Active", StringComparison.OrdinalIgnoreCase),
            VersionLabel(plan),
            $"This version is {plan.VersionState.DefaultIfBlank(plan.Status)}; publish it before adjudication.");

        AddCheck(result, "Covered benefits", plan.Benefits.Count > 0,
            $"{plan.Benefits.Count} covered benefit rule(s) are configured.",
            "No covered benefit rules are configured.");

        AddCheck(result, "Network tiers", plan.NetworkTiers.Count > 0,
            $"{plan.NetworkTiers.Count} network tier(s) are configured.",
            "No network tiers are configured.");

        var incompleteTiers = plan.NetworkTiers.Count(tier =>
            string.IsNullOrWhiteSpace(tier.TierName) || string.IsNullOrWhiteSpace(tier.NetworkId));
        AddCheck(result, "Network identifiers", incompleteTiers == 0,
            "Every network tier has a display name and provider-participation network ID.",
            $"{incompleteTiers} network tier(s) are missing a name or network ID.");

        result.Checks.Add(new BenefitPlanValidationCheck
        {
            Name = "Exclusions",
            Severity = plan.Exclusions.Count == 0 ? "Warning" : "Success",
            Message = plan.Exclusions.Count == 0
                ? "No explicit exclusions are configured. Confirm that this is intentional."
                : $"{plan.Exclusions.Count} explicit exclusion rule(s) are configured."
        });

        result.MemberView = await _benefitPlans.GetMemberViewAsync(plan.PlanId, serviceDate);
        if (result.MemberView is null)
        {
            result.Checks.Add(new BenefitPlanValidationCheck
            {
                Name = "Member view",
                Severity = "Error",
                Message = "The member-facing benefit view could not be resolved for this date."
            });
        }
        else
        {
            result.PlanVersion = result.MemberView.PlanVersion;
            AddCheck(result, "Member view", result.MemberView.Categories.Count > 0,
                $"Member view resolved {result.MemberView.Categories.Count} benefit categor{(result.MemberView.Categories.Count == 1 ? "y" : "ies")} for {result.MemberView.PlanVersion.DefaultIfBlank(VersionLabel(plan))}.",
                "The member view resolved, but contains no benefit categories.");
        }

        result.PlanVersion = result.PlanVersion.DefaultIfBlank(VersionLabel(plan));
        return result;
    }

    public async Task<SyntheticClaimValidationResult> RunSynthetic837Async(
        BenefitPlanDetails plan,
        SyntheticClaimValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SyntheticClaimsEnabled)
        {
            throw new InvalidOperationException(
                "Synthetic 837 validation is disabled. Enable Features:BenefitPlanSyntheticValidationEnabled only in an approved demo environment.");
        }

        ValidateSyntheticRequest(request);
        var validation = await ValidateAsync(plan, request.ServiceDate, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Resolve the plan validation errors before submitting a synthetic 837.");
        }

        var stamp = DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var memberId = $"BPV{stamp}{suffix}";
        var claimNumber = $"BPV837{stamp}{suffix}";
        var effectiveDate = request.ServiceDate.Date <= DateTime.UtcNow.Date
            ? request.ServiceDate.Date
            : DateTime.UtcNow.Date;

        await EnsureSyntheticNetworkAsync(
            plan, validation.MemberView, effectiveDate, cancellationToken);
        await EnsureSyntheticProviderAsync(
            plan, validation.MemberView, request.ProviderNpi, effectiveDate, cancellationToken);
        await CreateSyntheticMemberAsync(memberId, effectiveDate, cancellationToken);
        await CreateSyntheticCoverageAsync(memberId, plan.PlanId, effectiveDate, cancellationToken);

        var edi = Build837(memberId, claimNumber, request);
        var import = await Submit837Async(claimNumber, edi, cancellationToken);
        var imported = import.Results.FirstOrDefault();
        if (imported is null || !imported.Success || string.IsNullOrWhiteSpace(imported.ClaimId))
        {
            var errors = imported?.Errors.Count > 0 ? string.Join("; ", imported.Errors) : "No claim result was returned.";
            throw new InvalidOperationException($"The synthetic 837 was rejected: {errors}");
        }

        var timer = Stopwatch.StartNew();
        ClaimDetails? claim = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            cancellationToken.ThrowIfCancellationRequested();
            claim = await _claims.GetClaimByIdAsync(imported.ClaimId);
            if (claim is not null && !InProgressStatuses.Contains(claim.Status))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        timer.Stop();

        if (claim is null)
        {
            throw new InvalidOperationException("The claim was accepted, but its adjudication projection could not be read.");
        }

        var result = new SyntheticClaimValidationResult
        {
            ClaimId = imported.ClaimId,
            ClaimNumber = claimNumber,
            MemberId = memberId,
            ExpectedPlanId = plan.PlanId,
            ResolvedPlanId = claim.BenefitPlanId,
            PlanVersion = validation.PlanVersion,
            Status = claim.Status,
            NetworkTier = claim.NetworkTier,
            ChargeAmount = claim.TotalChargeAmount,
            AllowedAmount = claim.AllowedAmount,
            DeductibleAmount = claim.DeductibleAmount,
            CopayAmount = claim.CopayAmount,
            CoinsuranceAmount = claim.CoinsuranceAmount,
            PatientResponsibility = claim.PatientResponsibility,
            PaidAmount = claim.PaidAmount,
            OutcomeReason = claim.PendDetails?.PendReason ?? claim.DenialReason,
            Elapsed = timer.Elapsed
        };

        if (!result.ExactPlanMatched)
        {
            throw new InvalidOperationException(
                $"The claim resolved plan '{result.ResolvedPlanId ?? "<none>"}' instead of selected plan '{plan.PlanId}'.");
        }

        _logger.LogInformation(
            "Synthetic 837 validation completed for plan {PlanId}, claim {ClaimId}, status {Status}",
            plan.PlanId, result.ClaimId, result.Status);
        return result;
    }

    private async Task CreateSyntheticMemberAsync(string memberId, DateTime effectiveDate, CancellationToken ct)
    {
        var endpoint = ServiceEndpoint("MemberService", "/api/v1", "/members");
        using var create = await _httpClient.PostAsJsonAsync(endpoint, new
        {
            memberId,
            groupNumber = "BPVALIDATE",
            isSubscriber = true,
            firstName = "Plan",
            lastName = "Validator",
            dateOfBirth = new DateTime(1985, 1, 15),
            gender = "U",
            effectiveDate,
            eventId = $"{memberId}-create"
        }, ct);
        await EnsureSuccessAsync(create, "create the synthetic member", ct);

        using var activate = await _httpClient.PutAsJsonAsync(
            $"{endpoint}/{Uri.EscapeDataString(memberId)}",
            new { status = "Active", eventId = $"{memberId}-activate" }, ct);
        await EnsureSuccessAsync(activate, "activate the synthetic member", ct);
    }

    private async Task EnsureSyntheticProviderAsync(
        BenefitPlanDetails plan,
        MemberBenefitView? memberView,
        string npi,
        DateTime effectiveDate,
        CancellationToken ct)
    {
        var endpoint = ServiceEndpoint("ProviderService", "/api", "/v1/providers");
        ProviderProbe? provider = null;
        using (var lookup = await _httpClient.GetAsync($"{endpoint}/npi/{Uri.EscapeDataString(npi)}", ct))
        {
            if (lookup.IsSuccessStatusCode)
            {
                provider = await lookup.Content.ReadFromJsonAsync<ProviderProbe>(JsonOptions, ct);
            }
            else if (lookup.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(lookup, "look up the synthetic provider", ct);
            }
        }

        var primaryTier = plan.NetworkTiers.OrderBy(tier => tier.TierLevel).First();
        var lineOfBusiness = memberView?.LineOfBusiness.DefaultIfBlank("Commercial") ?? "Commercial";
        var participation = new
        {
            planId = plan.PlanId,
            networkId = primaryTier.NetworkId,
            lineOfBusiness,
            networkTier = primaryTier.TierName.DefaultIfBlank($"Tier{primaryTier.TierLevel}"),
            effectiveDate,
            terminationDate = plan.TerminationDate,
            acceptingNewPatients = true,
            panelAccepted = true,
            acceptedLobs = new[] { lineOfBusiness }
        };

        if (provider is null)
        {
            var tenantId = await _tenantContext.GetTenantIdAsync()
                ?? throw new InvalidOperationException("Tenant context is required for synthetic provider setup.");
            using var create = await _httpClient.PostAsJsonAsync(endpoint, new
            {
                tenantId,
                npi,
                providerType = "Organization",
                organizationName = "Synthetic Plan Validation Medical Group",
                primarySpecialty = "General Practice",
                taxonomyCode = "261Q00000X",
                address = "100 Test Claim Way",
                city = "Phoenix",
                state = "AZ",
                zipCode = "85001",
                phone = "6025550100",
                credentialingStatus = "Approved",
                credentialingDate = effectiveDate,
                recredentialingDueDate = DateTime.UtcNow.Date.AddYears(3),
                integrityScore = 100,
                integrityRating = "Clear",
                lastVerifiedAt = DateTimeOffset.UtcNow,
                nextVerificationDue = DateTimeOffset.UtcNow.AddYears(1),
                acceptingNewPatients = true,
                status = "Active",
                networkParticipations = new[] { participation }
            }, ct);
            await EnsureSuccessAsync(create, "create the synthetic provider", ct);
            provider = await create.Content.ReadFromJsonAsync<ProviderProbe>(JsonOptions, ct)
                ?? throw new InvalidOperationException("The synthetic provider was created, but no provider record was returned.");
        }

        var alreadyParticipating = provider.NetworkParticipations.Any(existing =>
            string.Equals(existing.NetworkId, primaryTier.NetworkId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.PlanId, plan.PlanId, StringComparison.OrdinalIgnoreCase));
        var providerId = provider.ProviderId.DefaultIfBlank(provider.Id);
        if (!alreadyParticipating)
        {
            using var addParticipation = await _httpClient.PostAsJsonAsync(
                $"{endpoint}/{Uri.EscapeDataString(providerId)}/network-participations", participation, ct);
            await EnsureSuccessAsync(addParticipation, "add synthetic provider network participation", ct);
        }

        CredentialingStatusProbe? credentialing = null;
        using (var credentialingLookup = await _httpClient.GetAsync(
            $"{endpoint}/{Uri.EscapeDataString(providerId)}/credentialing/status-as-of?asOfDate={effectiveDate:yyyy-MM-dd}", ct))
        {
            if (credentialingLookup.IsSuccessStatusCode)
            {
                credentialing = await credentialingLookup.Content.ReadFromJsonAsync<CredentialingStatusProbe>(JsonOptions, ct);
            }
            else
            {
                await EnsureSuccessAsync(credentialingLookup, "read synthetic provider credentialing status", ct);
            }
        }

        if (!string.Equals(credentialing?.Status, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            using var credential = await _httpClient.PostAsJsonAsync(
                $"{endpoint}/{Uri.EscapeDataString(providerId)}/credentialing/decisions",
                new
                {
                    decision = "Approved",
                    decidedAt = new DateTimeOffset(effectiveDate, TimeSpan.Zero),
                    credentialingDate = effectiveDate,
                    recredentialingDueDate = effectiveDate.AddYears(3),
                    decisionAuthorityType = "DelegatedAuthority",
                    decisionAuthorityId = "benefit-plan-validation-demo"
                }, ct);
            await EnsureSuccessAsync(credential, "credential the synthetic provider", ct);
        }
    }

    private async Task EnsureSyntheticNetworkAsync(
        BenefitPlanDetails plan,
        MemberBenefitView? memberView,
        DateTime effectiveDate,
        CancellationToken ct)
    {
        var providersEndpoint = ServiceEndpoint("ProviderService", "/api", "/v1/providers");
        var networksEndpoint = providersEndpoint[..^"/providers".Length] + "/networks";
        var primaryTier = plan.NetworkTiers.OrderBy(tier => tier.TierLevel).First();
        var networkUrl = $"{networksEndpoint}/{Uri.EscapeDataString(primaryTier.NetworkId)}";
        using var lookup = await _httpClient.GetAsync(networkUrl, ct);
        if (lookup.IsSuccessStatusCode) return;
        if (lookup.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(lookup, "look up the synthetic network", ct);
            return;
        }

        var lineOfBusiness = memberView?.LineOfBusiness.DefaultIfBlank("Commercial") ?? "Commercial";
        var tenantId = await _tenantContext.GetTenantIdAsync()
            ?? throw new InvalidOperationException("Tenant context is required for synthetic network setup.");
        using var create = await _httpClient.PostAsJsonAsync(networksEndpoint, new
        {
            tenantId,
            organizationId = primaryTier.NetworkId,
            name = primaryTier.TierName.DefaultIfBlank("Synthetic Validation Network"),
            networkType = NormalizeNetworkType(plan.ProductType),
            lineOfBusiness,
            effectiveDate,
            terminationDate = plan.TerminationDate,
            status = "Active",
            identifiers = new[]
            {
                new { system = "urn:cloudhealthoffice:network", value = primaryTier.NetworkId, type = "NIIP", use = "official" }
            }
        }, ct);
        await EnsureSuccessAsync(create, "create the synthetic provider network", ct);
    }

    private async Task CreateSyntheticCoverageAsync(
        string memberId, string planId, DateTime effectiveDate, CancellationToken ct)
    {
        var endpoint = ServiceEndpoint("CoverageService", "/api", "/v1/coverage");
        using var response = await _httpClient.PostAsJsonAsync(endpoint, new
        {
            memberId,
            groupNumber = "BPVALIDATE",
            planId,
            coverageLevel = "EMP",
            insuranceLineCode = "HLT",
            effectiveDate,
            maintenanceTypeCode = "021"
        }, ct);
        await EnsureSuccessAsync(response, "create synthetic coverage", ct);
    }

    private async Task<Raw837ImportResponse> Submit837Async(string claimNumber, string edi, CancellationToken ct)
    {
        var endpoint = ServiceEndpoint("ClaimsService", "/api", "/v1/claims/import/raw837");
        using var multipart = new MultipartFormDataContent();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(edi));
        content.Headers.ContentType = new("application/octet-stream");
        multipart.Add(content, "file", $"{claimNumber}.edi");

        using var response = await _httpClient.PostAsync(endpoint, multipart, ct);
        await EnsureSuccessAsync(response, "submit the synthetic 837", ct);
        return await response.Content.ReadFromJsonAsync<Raw837ImportResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Claims service returned an empty 837 import response.");
    }

    private string ServiceEndpoint(string serviceName, string expectedSuffix, string relativePath)
    {
        var configured = _configuration[$"Services:{serviceName}"]?.TrimEnd('/')
            ?? throw new InvalidOperationException($"Services:{serviceName} is not configured.");
        if (configured.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return configured + relativePath;
        }
        return configured + expectedSuffix + relativePath;
    }

    private static string Build837(string memberId, string claimNumber, SyntheticClaimValidationRequest request)
    {
        var now = DateTime.UtcNow;
        var serviceDate = request.ServiceDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var charge = request.ChargeAmount.ToString("0.00", CultureInfo.InvariantCulture);
        return $"ISA*00*          *00*          *ZZ*BPVALIDATOR    *ZZ*CHORECEIVER    *{now:yyMMdd}*{now:HHmm}*^*00501*000000001*0*P*:~" +
               $"GS*HC*BPVALIDATOR*CHORECEIVER*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X222A1~" +
               $"ST*837*0001*005010X222A1~BHT*0019*18*{claimNumber}*{now:yyyyMMdd}*{now:HHmm}*CH~" +
               "NM1*41*2*PLAN VALIDATOR*****46*BPVALIDATOR~PER*IC*PLAN VALIDATOR*TE*0000000000~" +
               "NM1*40*2*CLOUD HEALTH OFFICE*****46*CHORECEIVER~HL*1**20*1~" +
               $"NM1*85*2*SYNTHETIC MEDICAL GROUP*****XX*{request.ProviderNpi}~N3*ADDRESS ON FILE~N4*PHOENIX*AZ*85001~" +
               $"HL*2*1*22*0~SBR*P*18*****CI~NM1*IL*1*VALIDATOR*PLAN****MI*{memberId}~" +
               "NM1*PR*2*CLOUD HEALTH OFFICE*****PI*CHOPAYER~" +
               $"CLM*{claimNumber}*{charge}***11:B:1*Y*A*Y*Y~DTP*472*D8*{serviceDate}~HI*ABK:J06.9~" +
               $"LX*1~SV1*HC:{request.ProcedureCode}*{charge}*UN*1*11**1~DTP*472*D8*{serviceDate}~" +
               "SE*19*0001~GE*1*1~IEA*1*000000001~";
    }

    private static void ValidateSyntheticRequest(SyntheticClaimValidationRequest request)
    {
        if (request.ServiceDate == default) throw new ArgumentException("Service date is required.");
        if (!IsValidNpi(request.ProviderNpi))
            throw new ArgumentException("Provider NPI must be 10 digits and pass the NPI Luhn check.");
        if (string.IsNullOrWhiteSpace(request.ProcedureCode)) throw new ArgumentException("Procedure code is required.");
        if (request.ChargeAmount <= 0) throw new ArgumentException("Charge amount must be greater than zero.");
    }

    private static void AddCheck(
        BenefitPlanValidationResult result, string name, bool passed, string success, string failure)
        => result.Checks.Add(new BenefitPlanValidationCheck
        {
            Name = name,
            Severity = passed ? "Success" : "Error",
            Message = passed ? success : failure
        });

    private static string VersionLabel(BenefitPlanDetails plan)
        => plan.VersionNumber > 0 ? $"Version {plan.VersionNumber} ({plan.VersionState})" : plan.VersionState;

    private static string NormalizeNetworkType(string? productType) => productType?.ToUpperInvariant() switch
    {
        "PPO" => "PPO",
        "HMO" => "HMO",
        "EPO" => "EPO",
        "POS" => "POS",
        _ => "Custom"
    };

    private static bool IsValidNpi(string npi)
    {
        if (npi.Length != 10 || npi.Any(ch => !char.IsDigit(ch))) return false;
        var digits = "80840" + npi[..9];
        var sum = 0;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var value = (digits[index] - '0') * (((digits.Length - 1 - index) % 2 == 0) ? 2 : 1);
            sum += value > 9 ? value - 9 : value;
        }
        var checkDigit = (10 - (sum % 10)) % 10;
        return checkDigit == npi[9] - '0';
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 500) detail = detail[..500];
        throw new InvalidOperationException($"Could not {operation} ({(int)response.StatusCode}): {detail}");
    }

    private sealed class Raw837ImportResponse
    {
        public List<Raw837ClaimResponse> Results { get; set; } = new();
    }

    private sealed class Raw837ClaimResponse
    {
        public bool Success { get; set; }
        public string? ClaimId { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    private sealed class ProviderProbe
    {
        public string Id { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public List<ProviderParticipationProbe> NetworkParticipations { get; set; } = new();
    }

    private sealed class CredentialingStatusProbe
    {
        public string Status { get; set; } = string.Empty;
    }

    private sealed class ProviderParticipationProbe
    {
        public string? PlanId { get; set; }
        public string? NetworkId { get; set; }
    }
}

internal static class BenefitPlanValidationStringExtensions
{
    public static string DefaultIfBlank(this string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
