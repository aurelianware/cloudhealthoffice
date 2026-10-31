using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Models;
using FhirService.Services.Dtr;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services;

/// <summary>
/// DTR Questionnaire library and QuestionnaireResponse intake. Persistence goes
/// through <see cref="IDtrResourceStore"/>, which the host supplies (MongoDB;
/// ADR 017). Reads, searches, existence checks and updates all use the durable
/// store; no per-pod resource index is retained.
/// </summary>
public class DtrService : IDtrService
{
    private readonly IDtrResourceStore _store;
    private readonly DtrConfig _config;
    private readonly ILogger<DtrService> _logger;

    private const string DtrProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire";

    public DtrService(IOptions<DtrConfig> config, IDtrResourceStore store, ILogger<DtrService> logger)
    {
        _config = config.Value;
        _store = store;
        _logger = logger;

        LoadSeedData();
    }

    // ── Questionnaire CRUD ───────────────────────────────────────────────────

    public async Task<Questionnaire?> GetQuestionnaireAsync(
        string id, string tenantId, CancellationToken ct = default)
    {
        // Tenant-specific first, then fall back to the default library
        return await _store.FindQuestionnaireAsync(tenantId, id, ct)
               ?? await _store.FindQuestionnaireAsync(DefaultLibraryTenant, id, ct);
    }

    public async Task<(IReadOnlyList<Questionnaire> Items, int Total)> SearchQuestionnairesAsync(
        QuestionnaireSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = (await _store.ListVisibleQuestionnairesAsync(tenantId, ct)).AsEnumerable();

        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(q => q.Id == search.Id);
        if (!string.IsNullOrEmpty(search.Name))
            query = query.Where(q => q.Name != null &&
                q.Name.Contains(search.Name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(search.Title))
            query = query.Where(q => q.Title != null &&
                q.Title.Contains(search.Title, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(search.Status) &&
            Enum.TryParse<PublicationStatus>(search.Status, true, out var status))
            query = query.Where(q => q.Status == status);

        var all = query.OrderBy(q => q.Id, StringComparer.Ordinal).ToList();
        var total = all.Count;
        var items = all
            .Skip((search.Page - 1) * search.Count)
            .Take(search.Count)
            .ToList();

        return (items, total);
    }

    public async Task<Questionnaire> CreateQuestionnaireAsync(
        Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
    {
        EnsureNotDefaultLibrary(tenantId);
        var id = questionnaire.Id;
        if (string.IsNullOrEmpty(id))
            id = $"q-{Guid.NewGuid():N}";

        questionnaire.Id = id;
        questionnaire.Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.UtcNow,
            Profile = new[] { DtrProfile },
        };

        // Preserve create's existing duplicate-id behavior, but arbitrate it in
        // the store so a second pod cannot overwrite the original resource.
        var inserted = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await _store.TryInsertQuestionnaireAsync(questionnaire, tenantId, ct))
            {
                inserted = true;
                break;
            }
            questionnaire.Id = $"q-{Guid.NewGuid():N}";
        }
        if (!inserted) throw new ConcurrencyConflictException("Questionnaire creation conflicted; retry the request.");
        id = questionnaire.Id;
        _logger.LogInformation("Created Questionnaire {Id} for tenant {TenantId}", LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(tenantId));
        return questionnaire;
    }

    public async Task<Questionnaire?> UpdateQuestionnaireAsync(
        string id, Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
    {
        EnsureNotDefaultLibrary(tenantId);
        var previous = await _store.FindQuestionnaireAsync(tenantId, id, ct);
        if (previous == null && await _store.FindQuestionnaireAsync(DefaultLibraryTenant, id, ct) == null)
            return null;

        questionnaire.Id = id;
        var prevVersion = previous?.Meta?.VersionId;
        var nextVersion = int.TryParse(prevVersion, out var v) ? (v + 1).ToString() : "1";

        questionnaire.Meta = new Meta
        {
            VersionId = nextVersion,
            LastUpdated = DateTimeOffset.UtcNow,
            Profile = new[] { DtrProfile },
        };

        var saved = previous == null
            ? await _store.TryInsertQuestionnaireAsync(questionnaire, tenantId, ct)
            : await _store.TryReplaceQuestionnaireAsync(questionnaire, tenantId, prevVersion, ct);
        if (!saved) throw new ConcurrencyConflictException("Questionnaire changed during update; reload and retry.");
        _logger.LogInformation("Updated Questionnaire {Id} v{Version} for tenant {TenantId}",
            LogSanitizer.Sanitize(id), nextVersion, LogSanitizer.Sanitize(tenantId));
        return questionnaire;
    }

    // ── QuestionnaireResponse ────────────────────────────────────────────────

    public async Task<QuestionnaireResponse?> GetResponseAsync(
        string id, string tenantId, CancellationToken ct = default)
    {
        return await _store.FindResponseAsync(tenantId, id, ct);
    }

    public async Task<(IReadOnlyList<QuestionnaireResponse> Items, int Total)> SearchResponsesAsync(
        QuestionnaireResponseSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = (await _store.ListResponsesAsync(tenantId, ct)).AsEnumerable();
        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(r => r.Id == search.Id);
        if (!string.IsNullOrEmpty(search.QuestionnaireRef))
            query = query.Where(r => r.Questionnaire == search.QuestionnaireRef);
        if (!string.IsNullOrEmpty(search.Patient))
        {
            var wanted = StripPatient(search.Patient);
            query = query.Where(r => StripPatient(r.Subject?.Reference) == wanted);
        }
        if (!string.IsNullOrEmpty(search.Status) &&
            Enum.TryParse<QuestionnaireResponse.QuestionnaireResponseStatus>(search.Status, true, out var status))
            query = query.Where(r => r.Status == status);

        var all = query.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        return (all.Skip((search.Page - 1) * search.Count).Take(search.Count).ToList(), all.Count);
    }

    private static string? StripPatient(string? reference)
        => reference != null && reference.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase)
            ? reference["Patient/".Length..]
            : reference;

    public async Task<QuestionnaireResponse> SubmitResponseAsync(
        QuestionnaireResponse response, string tenantId, CancellationToken ct = default)
    {
        var id = $"qr-{Guid.NewGuid():N}";
        response.Id = id;
        response.Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.UtcNow,
        };
        response.Authored ??= DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        await _store.InsertResponseAsync(response, tenantId, ct);

        _logger.LogInformation("Submitted QuestionnaireResponse {Id} for tenant {TenantId}", LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(tenantId));
        return response;
    }

    // ── $questionnaire-package ───────────────────────────────────────────────

    public async Task<QuestionnairePackageResult> GetQuestionnairePackageAsync(
        QuestionnairePackageRequest request, string tenantId, string fhirBaseUrl, CancellationToken ct = default)
    {
        // Source data the package must reference (spec-130: a 4xx with an
        // OperationOutcome when it cannot be used). The draft response points
        // at the caller's own Coverage and member (spec-139), so both need ids.
        if (string.IsNullOrEmpty(request.Coverage.Id))
            return QuestionnairePackageResult.Reject(400, OperationOutcome.IssueType.Required,
                "coverage.id is required: the draft QuestionnaireResponse references the Coverage by id (DTR qr-coverage).");
        if (string.IsNullOrEmpty(request.Coverage.Beneficiary?.Reference))
            return QuestionnairePackageResult.Reject(400, OperationOutcome.IssueType.Required,
                "coverage.beneficiary must reference the member: it is the draft QuestionnaireResponse's subject.");

        // CHO does not keep CRD context ids yet (G-44: the coverage-assertion-id
        // is not persisted), so a context alone cannot be resolved.
        if (request.Context is not null && request.Orders.Count == 0 && request.Questionnaires.Count == 0)
            return QuestionnairePackageResult.Reject(422, OperationOutcome.IssueType.NotFound,
                $"Context id '{request.Context}' cannot be resolved by this server. " +
                "Send the order(s) the documentation is for, or the questionnaire canonical(s) CRD named.");

        var issues = new List<OperationOutcome.IssueComponent>();
        if (request.Context is not null)
        {
            issues.Add(QuestionnairePackageBuilder.Information(
                "The context id was not used: questionnaires were chosen from the orders and canonicals in the request."));
        }

        var visible = await VisibleWithCanonicalsAsync(tenantId, fhirBaseUrl, ct);

        // Union of every input, each questionnaire version once (oper-6).
        var selected = new Dictionary<string, (Questionnaire Q, List<Resource> Orders)>(StringComparer.Ordinal);
        var selectionOrder = new List<string>();

        foreach (var reference in request.Questionnaires)
        {
            var q = Resolve(visible, reference, fhirBaseUrl);
            if (q is null)
            {
                // oper-9: a warning in the outer Parameters' outcome, not a failure.
                issues.Add(QuestionnairePackageBuilder.Warning(OperationOutcome.IssueType.NotFound,
                    $"Questionnaire {reference} was not found."));
                continue;
            }
            var key = QuestionnairePackageBuilder.Canonical(q);
            if (selected.TryAdd(key, (q, new List<Resource>()))) selectionOrder.Add(key);
        }

        foreach (var requestOrder in request.Orders)
        {
            var codes = OrderCodes(requestOrder).ToList();
            var label = OrderLabel(requestOrder);
            if (codes.Count == 0)
            {
                issues.Add(QuestionnairePackageBuilder.Warning(OperationOutcome.IssueType.Incomplete,
                    $"{label} carries no code this server can match to a documentation template."));
                continue;
            }

            var matches = visible.Where(q => q.Status == PublicationStatus.Active && Covers(q, codes)).ToList();
            if (matches.Count == 0)
            {
                issues.Add(QuestionnairePackageBuilder.Information(
                    $"No documentation template applies to {label} " +
                    $"({string.Join(", ", codes.Select(c => $"{c.System}|{c.Code}"))})."));
            }
            foreach (var q in matches)
            {
                var key = QuestionnairePackageBuilder.Canonical(q);
                if (selected.TryAdd(key, (q, new List<Resource>()))) selectionOrder.Add(key);
                selected[key].Orders.Add(requestOrder);
            }
        }

        // A questionnaire asked for by canonical is completed for every order in
        // the request unless an order selected it specifically.
        var selections = selectionOrder
            .Select(k => selected[k])
            .Select(e => new SelectedQuestionnaire(e.Q, e.Orders.Count > 0 ? e.Orders : request.Orders.ToList()))
            .ToList();

        var bundles = new QuestionnairePackageBuilder(DtrContentLibrary.Default)
            .Build(selections, request.Coverage, DateTimeOffset.UtcNow, request.ChangedSince, issues);

        if (bundles.Count == 0 && request.ChangedSince is not null && selections.Count > 0)
        {
            issues.Add(QuestionnairePackageBuilder.Information(
                "No questionnaire package has changed since the requested time."));
        }
        else if (bundles.Count == 0 && issues.Count == 0)
        {
            // The output profile needs at least one parameter; say why it is empty.
            issues.Add(QuestionnairePackageBuilder.Information("No questionnaires apply to this request."));
        }

        _logger.LogInformation(
            "$questionnaire-package for tenant {TenantId}: {Packages} package(s), {Issues} issue(s)",
            LogSanitizer.Sanitize(tenantId), bundles.Count, issues.Count);

        return QuestionnairePackageResult.Ok(QuestionnairePackageBuilder.Wrap(bundles, issues));
    }

    // ── Canonical resolution ─────────────────────────────────────────────────

    public async Task<Questionnaire?> ResolveQuestionnaireAsync(
        string reference, string tenantId, string? fhirBaseUrl = null, CancellationToken ct = default)
        => Resolve(await VisibleWithCanonicalsAsync(tenantId, fhirBaseUrl, ct), reference, fhirBaseUrl);

    public async Task<bool> QuestionnaireExistsAsync(
        string questionnaireRef, string tenantId, string? fhirBaseUrl = null, CancellationToken ct = default)
        => await ResolveQuestionnaireAsync(questionnaireRef, tenantId, fhirBaseUrl, ct) is not null;

    private async Task<IReadOnlyList<Questionnaire>> VisibleWithCanonicalsAsync(
        string tenantId, string? fhirBaseUrl, CancellationToken ct)
        => (await _store.ListVisibleQuestionnairesAsync(tenantId, ct))
            .Select(q => WithCanonical(q, fhirBaseUrl))
            .ToList();

    /// <summary>
    /// Resolves a questionnaire reference the way DTR clients send them: a
    /// canonical (<c>url</c> or <c>url|version</c>, matched on
    /// <c>Questionnaire.url</c>), a literal <c>Questionnaire/{id}</c> or bare id,
    /// or this server's own <c>{base}/Questionnaire/{id}</c>. A canonical without
    /// a version resolves to the highest version (oper-6); with one, only that
    /// version matches.
    /// </summary>
    private static Questionnaire? Resolve(IReadOnlyList<Questionnaire> visible, string reference, string? fhirBaseUrl)
    {
        var (url, version) = DtrContentLibrary.SplitCanonical(reference.Trim());

        bool VersionMatches(Questionnaire q)
            => version is null || string.Equals(q.Version, version, StringComparison.Ordinal);

        var byUrl = visible.Where(q => string.Equals(q.Url, url, StringComparison.Ordinal)).ToList();
        if (byUrl.Count > 0)
        {
            return version is null
                ? byUrl.OrderByDescending(q => q.Version, DtrContentLibrary.VersionComparer.Instance).First()
                : byUrl.FirstOrDefault(VersionMatches);
        }

        string? id = null;
        var ownPrefix = fhirBaseUrl is null ? null : fhirBaseUrl.TrimEnd('/') + "/Questionnaire/";
        if (url.StartsWith("Questionnaire/", StringComparison.Ordinal))
            id = url["Questionnaire/".Length..];
        else if (!url.Contains('/') && !url.Contains(':'))
            id = url;
        else if (ownPrefix is not null && url.StartsWith(ownPrefix, StringComparison.Ordinal))
            id = url[ownPrefix.Length..];

        return id is null ? null : visible.FirstOrDefault(q => q.Id == id && VersionMatches(q));
    }

    /// <summary>
    /// A copy that always has a canonical: a questionnaire stored without
    /// <c>url</c>/<c>version</c> (one authored before DTR canonicals, or by a
    /// tenant that left them out) is published as <c>{base}/Questionnaire/{id}</c>
    /// at its stored version, so its package can still reference it
    /// version-specifically (oper-16, spec-122).
    /// </summary>
    private static Questionnaire WithCanonical(Questionnaire stored, string? fhirBaseUrl)
    {
        if (!string.IsNullOrEmpty(stored.Url) && !string.IsNullOrEmpty(stored.Version)) return stored;
        var q = (Questionnaire)stored.DeepCopy();
        if (string.IsNullOrEmpty(q.Url) && fhirBaseUrl is not null)
            q.Url = $"{fhirBaseUrl.TrimEnd('/')}/Questionnaire/{q.Id}";
        if (string.IsNullOrEmpty(q.Version))
            q.Version = q.Meta?.VersionId ?? "1";
        return q;
    }

    /// <summary>The codes an order asks for, which pick its documentation template.</summary>
    private static IEnumerable<Coding> OrderCodes(Resource order)
    {
        IEnumerable<CodeableConcept?> concepts = order switch
        {
            ServiceRequest sr => new[] { sr.Code },
            DeviceRequest dr => new[] { dr.Code as CodeableConcept },
            MedicationRequest mr => new[] { mr.Medication as CodeableConcept },
            SupplyRequest su => new[] { su.Item as CodeableConcept },
            NutritionOrder no => no.OralDiet?.Type ?? new List<CodeableConcept>(),
            Appointment ap => ap.ServiceType,
            Encounter en => en.Type,
            _ => Array.Empty<CodeableConcept>(),
        };
        return concepts.OfType<CodeableConcept>()
            .SelectMany(c => c.Coding)
            .Where(c => !string.IsNullOrEmpty(c.System) && !string.IsNullOrEmpty(c.Code));
    }

    /// <summary>
    /// True when the questionnaire declares one of the order's codes as a
    /// <c>focus</c> use context: that is how a payer says which services a
    /// documentation template is for.
    /// </summary>
    private static bool Covers(Questionnaire q, IReadOnlyList<Coding> codes)
        => q.UseContext
            .Where(u => u.Code?.Code == "focus" && u.Code.System == UsageContextTypeSystem)
            .Select(u => u.Value as CodeableConcept)
            .OfType<CodeableConcept>()
            .SelectMany(c => c.Coding)
            .Any(f => codes.Any(c => c.System == f.System && c.Code == f.Code));

    private const string UsageContextTypeSystem = "http://terminology.hl7.org/CodeSystem/usage-context-type";

    private static string OrderLabel(Resource order)
        => string.IsNullOrEmpty(order.Id) ? $"the {order.TypeName} order" : $"{order.TypeName}/{order.Id}";

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Store key of the read-only default library every tenant sees. Reserved:
    /// no caller may write under it (D-14).
    /// </summary>
    public const string DefaultLibraryTenant = "default";

    /// <summary>
    /// True when <paramref name="seed"/> should replace the default-library copy
    /// <paramref name="stored"/>: its <c>meta.versionId</c> is a higher number.
    /// A shipped form bumps its versionId when its content changes.
    /// </summary>
    public static bool IsNewerSeed(Questionnaire seed, Questionnaire? stored)
        => stored is not null
           && int.TryParse(seed.Meta?.VersionId, out var next)
           && (!int.TryParse(stored.Meta?.VersionId, out var current) || next > current);

    /// <summary>
    /// Defence in depth behind DtrController's 403: the shared library is
    /// seeded at startup, never written through the API.
    /// </summary>
    private static void EnsureNotDefaultLibrary(string tenantId)
    {
        if (string.Equals(tenantId, DefaultLibraryTenant, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The default questionnaire library is read-only; tenant id 'default' is reserved.");
    }

    // ── Seed Data ────────────────────────────────────────────────────────────

    private void LoadSeedData()
    {
        // The MRI form is DTR content (DtrContent/Questionnaire-pa-imaging-mri.json,
        // with its CQL Library and ValueSets); the other four are still the
        // original hand-written forms.
        var seeds = DtrContentLibrary.Default.Questionnaires.Concat(new[]
        {
            BuildDmeEquipmentQuestionnaire(),
            BuildSurgeryOrthoQuestionnaire(),
            BuildSpecialtyReferralQuestionnaire(),
            BuildMedicationPaQuestionnaire(),
        }).ToList();

        foreach (var q in seeds)
        {
            try
            {
                _store.SeedDefaultQuestionnaire(q);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed Questionnaire {Id} to MongoDB", q.Id);
            }
        }

        _logger.LogInformation("Loaded {Count} seed questionnaires", seeds.Count);
    }

    private static Questionnaire BuildDmeEquipmentQuestionnaire() => new()
    {
        Id = "q-dme-equipment",
        Name = "pa-dme-equipment",
        Title = "Prior Authorization: Durable Medical Equipment",
        Status = PublicationStatus.Active,
        Date = "2025-01-15",
        Publisher = "Cloud Health Office",
        Description = new Markdown("Questionnaire for prior authorization of durable medical equipment"),
        Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.Parse("2025-01-15T00:00:00Z"),
            Profile = new[] { DtrProfile },
        },
        Item = new List<Questionnaire.ItemComponent>
        {
            new()
            {
                LinkId = "1",
                Text = "Equipment type requested",
                Type = Questionnaire.QuestionnaireItemType.Choice,
                Required = true,
                AnswerOption = new List<Questionnaire.AnswerOptionComponent>
                {
                    new() { Value = new Coding("https://www.hcpcs.codes", "E1390", "Oxygen concentrator") },
                    new() { Value = new Coding("https://www.hcpcs.codes", "K0823", "Power wheelchair") },
                    new() { Value = new Coding("https://www.hcpcs.codes", "E0260", "Hospital bed") },
                    new() { Value = new Coding("https://www.hcpcs.codes", "E0601", "CPAP device") },
                },
            },
            new()
            {
                LinkId = "1a",
                Text = "Oxygen flow rate (L/min)",
                Type = Questionnaire.QuestionnaireItemType.Decimal,
                Required = true,
                EnableWhen = new List<Questionnaire.EnableWhenComponent>
                {
                    new()
                    {
                        Question = "1",
                        Operator = Questionnaire.QuestionnaireItemOperator.Equal,
                        Answer = new Coding("https://www.hcpcs.codes", "E1390", "Oxygen concentrator"),
                    },
                },
            },
            new()
            {
                LinkId = "2",
                Text = "Medical necessity justification",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
            },
            new()
            {
                LinkId = "3",
                Text = "Patient mobility assessment completed",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
            },
            new()
            {
                LinkId = "4",
                Text = "Expected duration of need (months)",
                Type = Questionnaire.QuestionnaireItemType.Integer,
                Required = false,
            },
        },
    };

    private static Questionnaire BuildSurgeryOrthoQuestionnaire() => new()
    {
        Id = "q-surgery-ortho",
        Name = "pa-surgery-ortho",
        Title = "Prior Authorization: Orthopedic Surgery",
        Status = PublicationStatus.Active,
        Date = "2025-01-15",
        Publisher = "Cloud Health Office",
        Description = new Markdown("Questionnaire for prior authorization of orthopedic surgical procedures"),
        Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.Parse("2025-01-15T00:00:00Z"),
            Profile = new[] { DtrProfile },
        },
        Item = new List<Questionnaire.ItemComponent>
        {
            new()
            {
                LinkId = "1",
                Text = "Planned procedure",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = true,
            },
            new()
            {
                LinkId = "2",
                Text = "Affected joint/body part",
                Type = Questionnaire.QuestionnaireItemType.Choice,
                Required = true,
                AnswerOption = new List<Questionnaire.AnswerOptionComponent>
                {
                    new() { Value = new Coding("http://snomed.info/sct", "72696002", "Knee") },
                    new() { Value = new Coding("http://snomed.info/sct", "16982005", "Shoulder") },
                    new() { Value = new Coding("http://snomed.info/sct", "29836001", "Hip") },
                    new() { Value = new Coding("http://snomed.info/sct", "421060004", "Spine") },
                },
            },
            new()
            {
                LinkId = "3",
                Text = "Conservative treatment history (minimum 6 weeks required)",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
            },
            new()
            {
                LinkId = "3a",
                Text = "Has physical therapy been completed?",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
                EnableWhen = new List<Questionnaire.EnableWhenComponent>
                {
                    new()
                    {
                        Question = "2",
                        Operator = Questionnaire.QuestionnaireItemOperator.Equal,
                        Answer = new Coding("http://snomed.info/sct", "72696002", "Knee"),
                    },
                },
            },
            new()
            {
                LinkId = "4",
                Text = "Imaging results available",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
            },
            new()
            {
                LinkId = "5",
                Text = "Functional limitations description",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
            },
            new()
            {
                LinkId = "6",
                Text = "Planned surgery date",
                Type = Questionnaire.QuestionnaireItemType.Date,
                Required = false,
            },
        },
    };

    private static Questionnaire BuildSpecialtyReferralQuestionnaire() => new()
    {
        Id = "q-specialty-referral",
        Name = "pa-specialty-referral",
        Title = "Prior Authorization: Specialty Referral",
        Status = PublicationStatus.Active,
        Date = "2025-01-15",
        Publisher = "Cloud Health Office",
        Description = new Markdown("Questionnaire for prior authorization of specialist referrals"),
        Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.Parse("2025-01-15T00:00:00Z"),
            Profile = new[] { DtrProfile },
        },
        Item = new List<Questionnaire.ItemComponent>
        {
            new()
            {
                LinkId = "1",
                Text = "Reason for referral",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
            },
            new()
            {
                LinkId = "2",
                Text = "Specialty type",
                Type = Questionnaire.QuestionnaireItemType.Choice,
                Required = true,
                AnswerOption = new List<Questionnaire.AnswerOptionComponent>
                {
                    new() { Value = new FhirString("Cardiology") },
                    new() { Value = new FhirString("Neurology") },
                    new() { Value = new FhirString("Orthopedics") },
                    new() { Value = new FhirString("Oncology") },
                    new() { Value = new FhirString("Gastroenterology") },
                },
            },
            new()
            {
                LinkId = "3",
                Text = "Relevant diagnosis",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = true,
            },
            new()
            {
                LinkId = "4",
                Text = "Prior treatment by PCP",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
            },
            new()
            {
                LinkId = "4a",
                Text = "Were medications prescribed by PCP?",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
                EnableWhen = new List<Questionnaire.EnableWhenComponent>
                {
                    new()
                    {
                        Question = "2",
                        Operator = Questionnaire.QuestionnaireItemOperator.Equal,
                        Answer = new FhirString("Cardiology"),
                    },
                },
            },
            new()
            {
                LinkId = "5",
                Text = "Is this an urgent referral?",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
            },
        },
    };

    private static Questionnaire BuildMedicationPaQuestionnaire() => new()
    {
        Id = "q-medication-pa",
        Name = "pa-medication",
        Title = "Prior Authorization: Medication",
        Status = PublicationStatus.Draft,
        Date = "2025-02-01",
        Publisher = "Cloud Health Office",
        Description = new Markdown("Questionnaire for prior authorization of specialty medications"),
        Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.Parse("2025-02-01T00:00:00Z"),
            Profile = new[] { DtrProfile },
        },
        Item = new List<Questionnaire.ItemComponent>
        {
            new()
            {
                LinkId = "1",
                Text = "Medication name and dosage",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = true,
            },
            new()
            {
                LinkId = "2",
                Text = "Clinical indication",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = true,
            },
            new()
            {
                LinkId = "3",
                Text = "Prior medications tried",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
            },
            new()
            {
                LinkId = "3a",
                Text = "Reason for discontinuation of prior medication",
                Type = Questionnaire.QuestionnaireItemType.Text,
                Required = true,
                EnableWhen = new List<Questionnaire.EnableWhenComponent>
                {
                    new()
                    {
                        Question = "3",
                        Operator = Questionnaire.QuestionnaireItemOperator.Exists,
                        Answer = new FhirBoolean(true),
                    },
                },
            },
            new()
            {
                LinkId = "4",
                Text = "Step therapy documentation provided",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
            },
            new()
            {
                LinkId = "5",
                Text = "Expected duration of therapy",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = false,
            },
        },
    };
}
