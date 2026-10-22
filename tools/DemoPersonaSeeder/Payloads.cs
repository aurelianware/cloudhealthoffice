namespace DemoPersonaSeeder;

/// <summary>
/// Request bodies for each service, built from the demo cast. Shapes follow the
/// services' create DTOs and the payloads tools/mcc-platform-validator already sends.
/// </summary>
public static class Payloads
{
    /// <summary>Event IDs make member and coverage writes traceable to the seeder.</summary>
    private static string EventId(string kind, string key) => $"demo-persona-seeder:{kind}:{key}";

    public static object Network(DemoNetwork n, DateTime effectiveDate) => new
    {
        id = n.Id,
        organizationId = n.Id,
        name = n.Name,
        networkType = "custom",
        lineOfBusiness = n.LineOfBusiness,
        effectiveDate,
        status = "active",
        identifiers = new[] { new { system = "urn:cho:network", value = n.Id, type = "NIIP", use = "official" } },
    };

    public static object Plan(DemoPlan p, string tenantId, DateTime effectiveDate)
    {
        var medicaid = p.LineOfBusiness == "Medicaid";
        decimal? Copay(decimal commercial, decimal medicare) => medicaid ? 0m : p.LineOfBusiness == "Medicare" ? medicare : commercial;
        return new
        {
            id = p.Id.ToString(),
            tenantId,
            planId = p.PlanId,
            planName = p.Name,
            payer = "Cloud Health Office Demo",
            effectiveDate,
            planType = p.PlanType,
            lineOfBusiness = p.LineOfBusiness,
            isActive = true,
            networkTiers = new[] { new { tierName = "In-Network", tierLevel = 1, networkId = p.InNetworkId } },
            costSharing = medicaid
                ? new { individualDeductible = 0m, familyDeductible = 0m, individualOutOfPocketMax = 0m, familyOutOfPocketMax = 0m,
                        inNetworkDeductible = 0m, outOfNetworkDeductible = 0m, inNetworkOutOfPocketMax = 0m, outOfNetworkOutOfPocketMax = 0m }
                : new { individualDeductible = 1500m, familyDeductible = 3000m, individualOutOfPocketMax = 6000m, familyOutOfPocketMax = 12000m,
                        inNetworkDeductible = 1500m, outOfNetworkDeductible = 3000m, inNetworkOutOfPocketMax = 6000m, outOfNetworkOutOfPocketMax = 15000m },
            benefits = new object[]
            {
                new { benefitType = "medical", serviceCategory = "98", description = "Professional Office Visit",
                      cptCodes = new[] { "99202", "99203", "99204", "99205", "99211", "99212", "99213", "99214", "99215" },
                      inNetworkCopay = Copay(30m, 10m), deductibleApplies = false, oopApplies = true, priorAuthRequired = false },
                new { benefitType = "medical", serviceCategory = "PT", description = "Physical Therapy",
                      cptCodes = new[] { "97110", "97112", "97140", "97530" },
                      inNetworkCopay = Copay(40m, 20m), deductibleApplies = false, oopApplies = true, priorAuthRequired = false,
                      visitLimit = 20, visitLimitPeriod = "PlanYear" },
                new { benefitType = "medical", serviceCategory = "62", description = "Advanced Imaging (MRI, CT, PET)",
                      cptCodes = new[] { "70551", "70553", "72148", "73721", "74177" },
                      inNetworkCoinsurance = medicaid ? 0m : 0.20m, deductibleApplies = !medicaid, oopApplies = true, priorAuthRequired = true },
                new { benefitType = "medical", serviceCategory = "48", description = "Hospital Inpatient",
                      cptCodes = Array.Empty<string>(),
                      inNetworkCoinsurance = medicaid ? 0m : 0.20m, deductibleApplies = !medicaid, oopApplies = true, priorAuthRequired = true },
                new { benefitType = "medical", serviceCategory = "86", description = "Emergency Services",
                      cptCodes = new[] { "99281", "99282", "99283", "99284", "99285" },
                      inNetworkCopay = Copay(250m, 90m), deductibleApplies = false, oopApplies = true, priorAuthRequired = false },
                new { benefitType = "medical", serviceCategory = "73", description = "Diagnostic Lab",
                      cptCodes = new[] { "80048", "80053", "85025", "36415" },
                      inNetworkCoinsurance = medicaid ? 0m : 0.10m, deductibleApplies = !medicaid, oopApplies = true, priorAuthRequired = false },
            },
        };
    }

    public static object Provider(DemoProvider p, DemoDataset data, string tenantId, DateTime effectiveDate) => new
    {
        tenantId,
        npi = p.Npi,
        providerType = p.IsOrganization ? "organization" : "individual",
        taxId = p.TaxId,
        firstName = p.FirstName,
        lastName = p.LastName,
        organizationName = p.OrganizationName,
        credentials = p.Credentials,
        primarySpecialty = p.Specialty,
        taxonomyCode = p.TaxonomyCode,
        address = p.Address.Street,
        city = p.Address.City,
        state = p.Address.State,
        zipCode = p.Address.Zip,
        phone = p.Phone,
        credentialingStatus = "approved",
        credentialingDate = effectiveDate,
        recredentialingDueDate = effectiveDate.AddYears(3),
        acceptingNewPatients = true,
        status = "active",
        networkParticipations = p.PlanKeys.Select(key =>
        {
            var plan = data.Plan(key);
            return new
            {
                planId = plan.Id.ToString(),
                networkId = plan.InNetworkId,
                lineOfBusiness = plan.LineOfBusiness,
                networkTier = "InNetwork",
                effectiveDate,
                acceptingNewPatients = true,
                panelLimit = p.IsPcp ? 2500 : (int?)null,
                panelAccepted = true,
                acceptedLobs = new[] { plan.LineOfBusiness },
                rates = new { feeScheduleName = "Demo Fee Schedule", percentOfMedicare = 1.10m },
            };
        }).ToArray(),
    };

    public static object Contract(DemoContract c, DemoProvider provider) => new
    {
        contractNumber = c.ContractNumber,
        providerNPI = provider.Npi,
        providerName = provider.DisplayName,
        providerTin = provider.TaxId,
        providerType = provider.IsOrganization ? "Organization" : "Individual",
        lineOfBusiness = c.LineOfBusiness,
        paymentMethodology = c.PaymentMethodology,
        networkStatus = "Participating",
        status = "Draft",
        effectiveDate = c.EffectiveDate,
        terminationDate = c.TerminationDate,
    };

    public static object Sponsor(DemoSponsor s, DateTime effectiveDate) => new
    {
        groupNumber = s.GroupNumber,
        employerName = s.EmployerName,
        taxId = s.TaxId,
        address = s.Address.Street,
        city = s.Address.City,
        state = s.Address.State,
        zipCode = s.Address.Zip,
        contactName = s.ContactName,
        contactPhone = s.ContactPhone,
        effectiveDate,
        billingInfo = new { premiumAmount = s.PremiumPerMember, billingDay = s.BillingDay, gracePeriodDays = s.GracePeriodDays },
    };

    public static object Member(DemoMember m, DateTime effectiveDate) => new
    {
        memberId = m.MemberId,
        groupNumber = m.GroupNumber,
        isSubscriber = m.IsSubscriber,
        subscriberMemberId = m.SubscriberMemberId,
        relationshipCode = m.RelationshipCode,
        firstName = m.FirstName,
        lastName = m.LastName,
        dateOfBirth = m.DateOfBirth,
        gender = m.Gender,
        address = m.Address.Street,
        city = m.Address.City,
        state = m.Address.State,
        zipCode = m.Address.Zip,
        phone = m.Phone,
        effectiveDate,
        maintenanceTypeCode = "021",
        eventId = EventId("member", m.MemberId),
    };

    public static object MemberIdentifier(DemoIdentifier id) => new { type = id.Type, value = id.Value };

    public static object Coverage(DemoMember m, DemoDataset data, DateTime effectiveDate) => new
    {
        memberId = m.MemberId,
        groupNumber = m.GroupNumber,
        planId = data.Plan(m.PlanKey).Id.ToString(),
        coverageLevel = m.IsSubscriber ? "EMP" : "DEP",
        insuranceLineCode = "HLT",
        effectiveDate,
        isCOBRA = false,
        monthlyPremium = m.MonthlyPremium > 0 ? m.MonthlyPremium : (decimal?)null,
        employerContribution = m.EmployerContribution > 0 ? m.EmployerContribution : (decimal?)null,
        otherInsurance = m.OtherInsurance is { } o
            ? new { payerName = o.PayerName, policyNumber = o.PolicyNumber, groupNumber = o.GroupNumber, isPrimaryPayer = true, effectiveDate }
            : null,
        maintenanceTypeCode = "021",
        maintenanceReasonCode = m.OtherInsurance is null ? null : "COB",
    };

    public static object PcpAssignment(DemoMember m, DemoProvider pcp, DateTime effectiveDate) => new
    {
        providerNpi = pcp.Npi,
        effectiveDate,
        reason = "Demo persona PCP",
        assignmentSource = "MemberChoice",
        memberDateOfBirth = m.DateOfBirth,
    };

    public static object Authorization(DemoAuthorization a, DemoDataset data)
    {
        var member = data.Member(a.MemberId);
        var requesting = data.Provider(a.RequestingProviderKey);
        var servicing = a.ServicingProviderKey is null ? null : data.Provider(a.ServicingProviderKey);
        var facility = a.FacilityProviderKey is null ? null : data.Provider(a.FacilityProviderKey);
        return new
        {
            authorizationNumber = a.Number,
            memberId = member.MemberId,
            subscriberId = member.SubscriberMemberId ?? member.MemberId,
            patientFirstName = member.FirstName,
            patientLastName = member.LastName,
            patientDateOfBirth = member.DateOfBirth,
            lineOfBusiness = data.Plan(member.PlanKey).LineOfBusiness,
            requestingProviderNPI = requesting.Npi,
            requestingProviderName = requesting.DisplayName,
            servicingProviderNPI = servicing?.Npi,
            servicingProviderName = servicing?.DisplayName,
            facilityNPI = facility?.Npi,
            authorizationType = a.AuthorizationType,
            levelOfService = a.LevelOfService,
            serviceTypeCode = a.ServiceTypeCode,
            requestedServiceDateFrom = a.From,
            requestedServiceDateTo = a.To,
            diagnosisCodes = a.DiagnosisCodes.Select(code => new { code }).ToArray(),
            requestedServices = a.Services.Select((s, i) => new
            {
                lineNumber = i + 1,
                procedureCode = s.ProcedureCode,
                procedureDescription = s.Description,
                requestedUnits = s.Units,
            }).ToArray(),
        };
    }

    public static object AuthorizationDecision(DemoAuthorization a) => new
    {
        status = a.Decision,
        reviewDecision = a.Decision == "Approved" ? "A1" : null,
        approvedUnits = a.ApprovedUnits,
        notes = "Decision recorded by the demo persona seeder.",
    };

    public static object Claim(DemoClaim c, DemoDataset data, string tenantId)
    {
        var member = data.Member(c.MemberId);
        var subscriber = member.SubscriberMemberId is null ? member : data.Member(member.SubscriberMemberId);
        var billing = data.Provider(c.BillingProviderKey);
        var rendering = data.Provider(c.RenderingProviderKey);
        return new
        {
            tenantId,
            claimNumber = c.ClaimNumber,
            // AdapterClaim (POST /api/v1/claims) has no patient control number field today,
            // so the provider's account number is kept in the demo script only.
            memberId = member.MemberId,
            subscriberId = subscriber.MemberId,
            benefitPlanId = data.Plan(c.PlanKey).PlanId,
            subscriberFirstName = subscriber.FirstName,
            subscriberLastName = subscriber.LastName,
            patientFirstName = member.FirstName,
            patientLastName = member.LastName,
            patientRelationship = member.RelationshipCode,
            lineOfBusiness = data.Plan(c.PlanKey).LineOfBusiness,
            billingProviderNPI = billing.Npi,
            billingProviderName = billing.DisplayName,
            renderingProviderNPI = rendering.Npi,
            renderingProviderName = rendering.DisplayName,
            placeOfServiceCode = c.PlaceOfService,
            claimType = c.ClaimType,
            claimFrequencyCode = "1",
            totalChargeAmount = c.Lines.Sum(l => l.Charge),
            serviceDateFrom = c.ServiceDateFrom,
            serviceDateTo = c.ServiceDateTo,
            diagnosisCodes = c.DiagnosisCodes,
            claimLines = c.Lines.Select((l, i) => new
            {
                lineNumber = i + 1,
                procedureCode = l.ProcedureCode,
                procedureDescription = l.Description,
                diagnosisPointers = new[] { 1 },
                units = l.Units,
                chargeAmount = l.Charge,
                serviceDateFrom = c.ServiceDateFrom,
                serviceDateTo = c.ServiceDateTo,
                placeOfServiceCode = c.PlaceOfService,
                revenueCode = l.RevenueCode,
            }).ToArray(),
            status = 1,
            priorAuthorizationNumber = c.PriorAuthorizationNumber,
        };
    }

    public static object Appeal(DemoAppeal a, DemoDataset data, string claimId)
    {
        var claim = data.Claim(a.ClaimNumber);
        var member = data.Member(claim.MemberId);
        var provider = data.Provider(claim.BillingProviderKey);
        return new
        {
            appealNumber = a.AppealNumber,
            claimId,
            claimNumber = claim.ClaimNumber,
            memberId = member.MemberId,
            patientName = member.FullName,
            providerNPI = provider.Npi,
            providerName = provider.DisplayName,
            appealedAmount = claim.Lines.Sum(l => l.Charge),
            appealType = a.AppealType,
            appealLevel = "FirstLevel",
            lineOfBusiness = data.Plan(claim.PlanKey).LineOfBusiness,
            appealReason = a.Reason,
            source = "ProviderPortal",
            isUrgent = a.IsUrgent,
            serviceDate = claim.ServiceDateFrom,
            diagnosisCodes = claim.DiagnosisCodes,
            procedureCodes = claim.Lines.Select(l => l.ProcedureCode).ToArray(),
            eventId = EventId("appeal", a.AppealNumber),
        };
    }

    public static object BillingRun(IReadOnlyList<string> groups, DateTime billingPeriod) => new
    {
        billingPeriod,
        description = "Demo persona seeder",
        criteria = new { groupNumbers = groups },
    };
}
