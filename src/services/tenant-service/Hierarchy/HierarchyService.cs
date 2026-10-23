using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using TenantService.Services;

namespace TenantService.Hierarchy;

public enum HierarchyOutcome
{
    Ok,
    Invalid,
    Forbidden,
    NotFound,
    Conflict,
}

public sealed record HierarchyResult<T>(HierarchyOutcome Outcome, IReadOnlyList<string> Errors, T? Value = default)
{
    public static HierarchyResult<T> Ok(T value) => new(HierarchyOutcome.Ok, [], value);
    public static HierarchyResult<T> Fail(HierarchyOutcome outcome, params string[] errors) => new(outcome, errors);
}

/// <summary>What a caller may set on a carrier.</summary>
public sealed record CarrierWrite
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? LegalName { get; init; }
    public string? TaxId { get; init; }
    public string? NaicCode { get; init; }
    public Dictionary<string, string>? PayerIds { get; init; }
    public string? NachaImmediateOrigin { get; init; }
    public string? NachaCompanyIdentification { get; init; }
    public string? CorrespondenceName { get; init; }
    public Models.Address? CorrespondenceAddress { get; init; }
    public bool? Active { get; init; }
}

/// <summary>What a caller may set on a program.</summary>
public sealed record ProgramWrite
{
    public string? CarrierId { get; init; }
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? LineOfBusiness { get; init; }
    public string? StateCode { get; init; }
    public string? RegulatoryContractNumber { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public bool? Active { get; init; }
}

/// <summary>
/// Carrier and program records (plan-configuration.md §12 step 2). Carriers
/// are enterprise configuration (<c>config:enterprise</c>); programs are
/// product configuration (<c>config:product</c>), the same split the resolver
/// applies to settings at those levels. Nothing here crosses tenants: the
/// tenant comes from the caller's token.
/// </summary>
public sealed class HierarchyService(
    IHierarchyStore store,
    ITenantRepository tenants,
    TimeProvider clock,
    ILogger<HierarchyService> logger,
    IBenefitPlanProgramLookup? benefitPlans = null)
{
    /// <summary>The code of the carrier created automatically with a tenant.</summary>
    public const string DefaultCarrierCode = "DEFAULT";

    private static readonly Regex CodePattern = new("^[A-Z0-9][A-Z0-9-]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex DigitsOnly = new("^[0-9]+$", RegexOptions.Compiled);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ── Carriers ───────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's carriers, creating the default one if it has none. A tenant
    /// has exactly one carrier until someone adds a second, so the list is
    /// never empty for a tenant that exists (plan-configuration.md §12 step 2).
    /// </summary>
    public async Task<IReadOnlyList<Carrier>> ListCarriersAsync(string tenantId, CancellationToken ct = default)
    {
        var carriers = await store.ListCarriersAsync(tenantId, ct);
        if (carriers.Count > 0) return carriers;
        return await EnsureDefaultCarrierAsync(tenantId, ct) is { } created ? [created] : carriers;
    }

    public Task<Carrier?> GetCarrierAsync(string tenantId, string id, CancellationToken ct = default) =>
        store.GetCarrierAsync(tenantId, id, ct);

    /// <summary>
    /// Creates the tenant's default carrier from its own record, or returns the
    /// one already there. Null when the tenant does not exist. Two concurrent
    /// callers do not create two: the id is <c>{tenant}|DEFAULT</c>, so the
    /// loser of the insert reads the winner's.
    /// </summary>
    public async Task<Carrier?> EnsureDefaultCarrierAsync(string tenantId, CancellationToken ct = default)
    {
        var id = Carrier.IdFor(tenantId, DefaultCarrierCode);
        if (await store.GetCarrierAsync(tenantId, id, ct) is { } existing) return existing;
        if (await tenants.GetByTenantIdAsync(tenantId) is not { } tenant) return null;

        var carrier = new Carrier
        {
            Id = id,
            TenantId = tenantId,
            Code = DefaultCarrierCode,
            Name = string.IsNullOrWhiteSpace(tenant.OrganizationName) ? tenant.TenantName : tenant.OrganizationName,
            LegalName = string.IsNullOrWhiteSpace(tenant.OrganizationName) ? null : tenant.OrganizationName,
            CorrespondenceAddress = tenant.ContactInfo?.Address,
            IsDefault = true,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedBy = "tenant-service:default-carrier",
            UpdatedBy = "tenant-service:default-carrier",
        };
        if (await store.TryInsertCarrierAsync(carrier, ct)) return carrier;

        // Someone else inserted it between the read and the write.
        return await store.GetCarrierAsync(tenantId, id, ct);
    }

    public async Task<HierarchyResult<Carrier>> CreateCarrierAsync(ICurrentActor actor, CarrierWrite write, CancellationToken ct = default)
    {
        if (!actor.HasPermission(PlanConfigurationPermissions.Enterprise))
            return HierarchyResult<Carrier>.Fail(HierarchyOutcome.Forbidden, $"Creating a carrier needs {PlanConfigurationPermissions.Enterprise}.");

        var code = Normalize(write.Code);
        var errors = new List<string>();
        if (!CodePattern.IsMatch(code))
            errors.Add("code must be 1 to 32 characters: upper-case letters, digits and '-'.");
        if (string.IsNullOrWhiteSpace(write.Name))
            errors.Add("name is required.");
        errors.AddRange(LegalEntityErrors(write));
        if (errors.Count > 0) return new HierarchyResult<Carrier>(HierarchyOutcome.Invalid, errors);

        var carrier = new Carrier
        {
            Id = Carrier.IdFor(actor.TenantId, code),
            TenantId = actor.TenantId,
            Code = code,
            CreatedAt = Now,
            CreatedBy = actor.UserId,
        };
        Apply(carrier, write, actor.UserId);
        if (!await store.TryInsertCarrierAsync(carrier, ct))
            return HierarchyResult<Carrier>.Fail(HierarchyOutcome.Conflict, $"The tenant already has a carrier with code '{code}'.");

        logger.LogInformation("Created carrier {Code} for tenant {TenantId}",
            LogSanitizer.Sanitize(code), LogSanitizer.Sanitize(actor.TenantId));
        return HierarchyResult<Carrier>.Ok(carrier);
    }

    /// <summary>
    /// Changes a carrier. The code is the carrier's identity and is not
    /// changed here: programs, and later settings, name the carrier by id.
    /// </summary>
    public async Task<HierarchyResult<Carrier>> UpdateCarrierAsync(ICurrentActor actor, string id, CarrierWrite write, CancellationToken ct = default)
    {
        if (!actor.HasPermission(PlanConfigurationPermissions.Enterprise))
            return HierarchyResult<Carrier>.Fail(HierarchyOutcome.Forbidden, $"Changing a carrier needs {PlanConfigurationPermissions.Enterprise}.");
        if (await store.GetCarrierAsync(actor.TenantId, id, ct) is not { } carrier)
            return HierarchyResult<Carrier>.Fail(HierarchyOutcome.NotFound, "No such carrier.");

        var errors = LegalEntityErrors(write).ToList();
        if (write.Code is not null && !string.Equals(Normalize(write.Code), carrier.Code, StringComparison.Ordinal))
            errors.Add("A carrier's code cannot be changed; create a carrier instead.");
        if (write.Name is not null && string.IsNullOrWhiteSpace(write.Name))
            errors.Add("name cannot be blank.");
        if (write.Active == false)
        {
            var carriers = await store.ListCarriersAsync(actor.TenantId, ct);
            if (carriers.Count(c => c.Active) <= 1)
                errors.Add("A tenant must keep one active carrier; add another before retiring this one.");
        }
        if (errors.Count > 0) return new HierarchyResult<Carrier>(HierarchyOutcome.Invalid, errors);

        var revision = carrier.Revision;
        Apply(carrier, write, actor.UserId);
        if (!await store.TryReplaceCarrierAsync(carrier, revision, ct))
            return HierarchyResult<Carrier>.Fail(HierarchyOutcome.Conflict, "The carrier changed while you were editing it; read it again.");
        return HierarchyResult<Carrier>.Ok(carrier);
    }

    // ── Programs ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<PlanProgram>> ListProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default) =>
        store.ListProgramsAsync(tenantId, carrierId, ct);

    public Task<PlanProgram?> GetProgramAsync(string tenantId, string id, CancellationToken ct = default) =>
        store.GetProgramAsync(tenantId, id, ct);

    /// <summary>
    /// The program a benefit plan belongs to. <see cref="HierarchyOutcome.NotFound"/>
    /// means the plan has no program (or no such plan);
    /// <see cref="HierarchyOutcome.Conflict"/> means benefit-plan-service could
    /// not be asked, which a caller must not read as "no program".
    /// </summary>
    public async Task<HierarchyResult<PlanProgram>> ProgramForBenefitPlanAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        if (benefitPlans is null)
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.Conflict, "Benefit plan lookups are not configured on this host.");

        string? programId;
        try
        {
            programId = await benefitPlans.GetProgramIdAsync(tenantId, planId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read benefit plan {PlanId}'s program for tenant {TenantId}",
                LogSanitizer.Sanitize(planId), LogSanitizer.Sanitize(tenantId));
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.Conflict, "benefit-plan-service could not be reached.");
        }

        if (programId is null)
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.NotFound, $"Benefit plan '{planId}' carries no program.");
        if (await store.GetProgramAsync(tenantId, programId, ct) is not { } program)
        {
            // The plan names a program the tenant does not have: a bad link, not an absent one.
            logger.LogWarning("Benefit plan {PlanId} names program {ProgramId}, which tenant {TenantId} does not have",
                LogSanitizer.Sanitize(planId), LogSanitizer.Sanitize(programId), LogSanitizer.Sanitize(tenantId));
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.NotFound, $"Benefit plan '{planId}' names a program that does not exist.");
        }
        return HierarchyResult<PlanProgram>.Ok(program);
    }

    public async Task<HierarchyResult<PlanProgram>> CreateProgramAsync(ICurrentActor actor, ProgramWrite write, CancellationToken ct = default)
    {
        if (!actor.HasPermission(PlanConfigurationPermissions.Product))
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.Forbidden, $"Creating a program needs {PlanConfigurationPermissions.Product}.");

        var code = Normalize(write.Code);
        var errors = new List<string>();
        if (!CodePattern.IsMatch(code))
            errors.Add("code must be 1 to 32 characters: upper-case letters, digits and '-'.");
        if (string.IsNullOrWhiteSpace(write.Name))
            errors.Add("name is required.");
        errors.AddRange(ProgramFieldErrors(write, requireAll: true));

        // The carrier the caller named, or the tenant's default one.
        var carrier = string.IsNullOrWhiteSpace(write.CarrierId)
            ? await EnsureDefaultCarrierAsync(actor.TenantId, ct)
            : await store.GetCarrierAsync(actor.TenantId, write.CarrierId.Trim(), ct);
        if (carrier is null)
            errors.Add(string.IsNullOrWhiteSpace(write.CarrierId)
                ? "The tenant has no carrier and no default one could be created."
                : $"No carrier '{write.CarrierId}'.");
        else if (!carrier.Active)
            errors.Add($"Carrier '{carrier.Code}' is retired; programs cannot be added to it.");
        if (errors.Count > 0) return new HierarchyResult<PlanProgram>(HierarchyOutcome.Invalid, errors);

        var program = new PlanProgram
        {
            Id = PlanProgram.IdFor(actor.TenantId, carrier!.Code, code),
            TenantId = actor.TenantId,
            CarrierId = carrier.Id,
            Code = code,
            CreatedAt = Now,
            CreatedBy = actor.UserId,
        };
        Apply(program, write, actor.UserId);
        if (!await store.TryInsertProgramAsync(program, ct))
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.Conflict, $"Carrier '{carrier.Code}' already has a program with code '{code}'.");

        logger.LogInformation("Created program {Code} ({State} {Lob}) under carrier {Carrier} for tenant {TenantId}",
            LogSanitizer.Sanitize(code), LogSanitizer.Sanitize(program.StateCode), LogSanitizer.Sanitize(program.LineOfBusiness),
            LogSanitizer.Sanitize(carrier.Code), LogSanitizer.Sanitize(actor.TenantId));
        return HierarchyResult<PlanProgram>.Ok(program);
    }

    /// <summary>
    /// Changes a program. Its code, carrier, state and line of business are
    /// what every rule set, setting and filing hangs off, so they are fixed
    /// once the program exists: moving a live program to another state would
    /// silently repoint everything keyed on it. Retire it and add another.
    /// </summary>
    public async Task<HierarchyResult<PlanProgram>> UpdateProgramAsync(ICurrentActor actor, string id, ProgramWrite write, CancellationToken ct = default)
    {
        if (!actor.HasPermission(PlanConfigurationPermissions.Product))
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.Forbidden, $"Changing a program needs {PlanConfigurationPermissions.Product}.");
        if (await store.GetProgramAsync(actor.TenantId, id, ct) is not { } program)
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.NotFound, "No such program.");

        var errors = ProgramFieldErrors(write, requireAll: false).ToList();
        if (write.Code is not null && !string.Equals(Normalize(write.Code), program.Code, StringComparison.Ordinal))
            errors.Add("A program's code cannot be changed; create a program instead.");
        if (write.CarrierId is not null && !string.Equals(write.CarrierId.Trim(), program.CarrierId, StringComparison.Ordinal))
            errors.Add("A program cannot be moved to another carrier; create a program instead.");
        if (write.StateCode is not null && !string.Equals(Normalize(write.StateCode), program.StateCode, StringComparison.Ordinal))
            errors.Add("A program's state cannot be changed; create a program instead.");
        if (write.LineOfBusiness is not null
            && !string.Equals(LinesOfBusiness.Normalize(write.LineOfBusiness), program.LineOfBusiness, StringComparison.Ordinal))
            errors.Add("A program's line of business cannot be changed; create a program instead.");
        if (write.Name is not null && string.IsNullOrWhiteSpace(write.Name))
            errors.Add("name cannot be blank.");
        if (errors.Count > 0) return new HierarchyResult<PlanProgram>(HierarchyOutcome.Invalid, errors);

        var revision = program.Revision;
        Apply(program, write, actor.UserId);
        if (!await store.TryReplaceProgramAsync(program, revision, ct))
            return HierarchyResult<PlanProgram>.Fail(HierarchyOutcome.Conflict, "The program changed while you were editing it; read it again.");
        return HierarchyResult<PlanProgram>.Ok(program);
    }

    // ── Validation and mapping ─────────────────────────────────────────

    private static IEnumerable<string> LegalEntityErrors(CarrierWrite write)
    {
        if (!string.IsNullOrWhiteSpace(write.TaxId) && (write.TaxId.Trim().Length != 9 || !DigitsOnly.IsMatch(write.TaxId.Trim())))
            yield return "taxId must be the nine digits of an EIN, without punctuation.";
        if (!string.IsNullOrWhiteSpace(write.NaicCode) && !DigitsOnly.IsMatch(write.NaicCode.Trim()))
            yield return "naicCode must be digits.";
        foreach (var (transaction, payerId) in write.PayerIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(transaction) || string.IsNullOrWhiteSpace(payerId))
                yield return "Each payerIds entry needs a transaction and a payer id.";
            else if (payerId.Trim().Length > 80)
                yield return $"payerIds['{transaction}'] is too long.";
        }
        // NACHA immediate origin: ten digits, or a routing number; the file
        // writer validates the rest when it is actually used (step 2b).
        if (!string.IsNullOrWhiteSpace(write.NachaImmediateOrigin)
            && (write.NachaImmediateOrigin.Trim().Length is < 9 or > 10 || !DigitsOnly.IsMatch(write.NachaImmediateOrigin.Trim())))
            yield return "nachaImmediateOrigin must be 9 or 10 digits.";
        if (!string.IsNullOrWhiteSpace(write.NachaCompanyIdentification) && write.NachaCompanyIdentification.Trim().Length > 10)
            yield return "nachaCompanyIdentification is at most 10 characters.";
    }

    private static IEnumerable<string> ProgramFieldErrors(ProgramWrite write, bool requireAll)
    {
        if (requireAll || write.LineOfBusiness is not null)
        {
            if (LinesOfBusiness.Normalize(write.LineOfBusiness) is null)
                yield return $"lineOfBusiness must be one of {string.Join(", ", LinesOfBusiness.All)}.";
        }
        if (requireAll || write.StateCode is not null)
        {
            var state = Normalize(write.StateCode);
            if (state.Length != 2 || !state.All(char.IsAsciiLetterUpper))
                yield return "stateCode must be a two-letter state code.";
        }
        if (write.EffectiveFrom is { } from && write.EffectiveTo is { } to && to <= from)
            yield return "effectiveTo must be after effectiveFrom.";
    }

    private void Apply(Carrier carrier, CarrierWrite write, string actor)
    {
        if (write.Name is not null) carrier.Name = write.Name.Trim();
        if (write.LegalName is not null) carrier.LegalName = Blank(write.LegalName);
        if (write.TaxId is not null) carrier.TaxId = Blank(write.TaxId);
        if (write.NaicCode is not null) carrier.NaicCode = Blank(write.NaicCode);
        if (write.PayerIds is not null)
            carrier.PayerIds = write.PayerIds.ToDictionary(p => p.Key.Trim(), p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        if (write.NachaImmediateOrigin is not null) carrier.NachaImmediateOrigin = Blank(write.NachaImmediateOrigin);
        if (write.NachaCompanyIdentification is not null) carrier.NachaCompanyIdentification = Blank(write.NachaCompanyIdentification);
        if (write.CorrespondenceName is not null) carrier.CorrespondenceName = Blank(write.CorrespondenceName);
        if (write.CorrespondenceAddress is not null) carrier.CorrespondenceAddress = write.CorrespondenceAddress;
        if (write.Active is { } active) carrier.Active = active;
        carrier.UpdatedAt = Now;
        carrier.UpdatedBy = actor;
    }

    private void Apply(PlanProgram program, ProgramWrite write, string actor)
    {
        if (write.Name is not null) program.Name = write.Name.Trim();
        if (LinesOfBusiness.Normalize(write.LineOfBusiness) is { } lob) program.LineOfBusiness = lob;
        if (write.StateCode is not null) program.StateCode = Normalize(write.StateCode);
        if (write.RegulatoryContractNumber is not null) program.RegulatoryContractNumber = Blank(write.RegulatoryContractNumber);
        if (write.EffectiveFrom is { } from) program.EffectiveFrom = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        if (write.EffectiveTo is { } to) program.EffectiveTo = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc);
        if (write.Active is { } active) program.Active = active;
        program.UpdatedAt = Now;
        program.UpdatedBy = actor;
    }

    private static string Normalize(string? code) => code?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
