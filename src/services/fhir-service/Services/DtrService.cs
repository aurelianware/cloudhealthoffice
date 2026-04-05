using System.Collections.Concurrent;
using FhirService.Models;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services;

public class DtrService : IDtrService
{
    private readonly ConcurrentDictionary<string, Questionnaire> _questionnaires = new();
    private readonly ConcurrentDictionary<string, QuestionnaireResponse> _responses = new();
    private readonly DtrConfig _config;
    private readonly ILogger<DtrService> _logger;

    private const string DtrProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire";

    public DtrService(IOptions<DtrConfig> config, ILogger<DtrService> logger)
    {
        _config = config.Value;
        _logger = logger;
        LoadSeedData();
    }

    // ── Questionnaire CRUD ───────────────────────────────────────────────────

    public Task<Questionnaire?> GetQuestionnaireAsync(
        string id, string tenantId, CancellationToken ct = default)
    {
        // Try tenant-specific key first, then default tenant (seed data)
        if (_questionnaires.TryGetValue($"{tenantId}:{id}", out var q))
            return Task.FromResult<Questionnaire?>(q);
        if (_questionnaires.TryGetValue($"default:{id}", out q))
            return Task.FromResult<Questionnaire?>(q);
        return Task.FromResult<Questionnaire?>(null);
    }

    public Task<(IReadOnlyList<Questionnaire> Items, int Total)> SearchQuestionnairesAsync(
        QuestionnaireSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = _questionnaires.Values
            .Where(q => IsAccessible(q, tenantId));

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

        var all = query.ToList();
        var total = all.Count;
        var items = all
            .Skip((search.Page - 1) * search.Count)
            .Take(search.Count)
            .ToList();

        return Task.FromResult<(IReadOnlyList<Questionnaire>, int)>((items, total));
    }

    public Task<Questionnaire> CreateQuestionnaireAsync(
        Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
    {
        var id = questionnaire.Id;
        if (string.IsNullOrEmpty(id) || _questionnaires.ContainsKey($"{tenantId}:{id}"))
            id = $"q-{Guid.NewGuid().ToString("N")[..8]}";

        questionnaire.Id = id;
        questionnaire.Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.UtcNow,
            Profile = new[] { DtrProfile },
        };

        _questionnaires[$"{tenantId}:{id}"] = questionnaire;
        _logger.LogInformation("Created Questionnaire {Id} for tenant {TenantId}", id, tenantId);
        return Task.FromResult(questionnaire);
    }

    public Task<Questionnaire?> UpdateQuestionnaireAsync(
        string id, Questionnaire questionnaire, string tenantId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{id}";
        if (!_questionnaires.ContainsKey(key) && !_questionnaires.ContainsKey($"default:{id}"))
            return Task.FromResult<Questionnaire?>(null);

        questionnaire.Id = id;
        var prevVersion = _questionnaires.TryGetValue(key, out var prev)
            ? prev.Meta?.VersionId : "0";
        var nextVersion = int.TryParse(prevVersion, out var v) ? (v + 1).ToString() : "1";

        questionnaire.Meta = new Meta
        {
            VersionId = nextVersion,
            LastUpdated = DateTimeOffset.UtcNow,
            Profile = new[] { DtrProfile },
        };

        _questionnaires[key] = questionnaire;
        _logger.LogInformation("Updated Questionnaire {Id} v{Version} for tenant {TenantId}",
            id, nextVersion, tenantId);
        return Task.FromResult<Questionnaire?>(questionnaire);
    }

    // ── QuestionnaireResponse ────────────────────────────────────────────────

    public Task<QuestionnaireResponse?> GetResponseAsync(
        string id, string tenantId, CancellationToken ct = default)
    {
        _responses.TryGetValue($"{tenantId}:{id}", out var qr);
        return Task.FromResult(qr);
    }

    public Task<(IReadOnlyList<QuestionnaireResponse> Items, int Total)> SearchResponsesAsync(
        QuestionnaireResponseSearchParams search, string tenantId, CancellationToken ct = default)
    {
        var query = _responses.Values
            .Where(r => GetTenantFromKey(r.Id) == tenantId ||
                        _responses.ContainsKey($"{tenantId}:{r.Id}"));

        // Re-filter using actual keyed responses for this tenant
        query = _responses
            .Where(kv => kv.Key.StartsWith($"{tenantId}:", StringComparison.Ordinal))
            .Select(kv => kv.Value);

        if (!string.IsNullOrEmpty(search.Id))
            query = query.Where(r => r.Id == search.Id);
        if (!string.IsNullOrEmpty(search.QuestionnaireRef))
            query = query.Where(r => r.Questionnaire == search.QuestionnaireRef);
        if (!string.IsNullOrEmpty(search.Patient))
            query = query.Where(r => r.Subject?.Reference == search.Patient);
        if (!string.IsNullOrEmpty(search.Status) &&
            Enum.TryParse<QuestionnaireResponse.QuestionnaireResponseStatus>(search.Status, true, out var status))
            query = query.Where(r => r.Status == status);

        var all = query.ToList();
        var total = all.Count;
        var items = all
            .Skip((search.Page - 1) * search.Count)
            .Take(search.Count)
            .ToList();

        return Task.FromResult<(IReadOnlyList<QuestionnaireResponse>, int)>((items, total));
    }

    public Task<QuestionnaireResponse> SubmitResponseAsync(
        QuestionnaireResponse response, string tenantId, CancellationToken ct = default)
    {
        var id = $"qr-{Guid.NewGuid().ToString("N")[..8]}";
        response.Id = id;
        response.Meta = new Meta
        {
            VersionId = "1",
            LastUpdated = DateTimeOffset.UtcNow,
        };
        response.Authored ??= DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        _responses[$"{tenantId}:{id}"] = response;
        _logger.LogInformation("Submitted QuestionnaireResponse {Id} for tenant {TenantId}", id, tenantId);
        return Task.FromResult(response);
    }

    // ── $questionnaire-package ───────────────────────────────────────────────

    public async Task<Bundle?> GetQuestionnairePackageAsync(
        string questionnaireId, string? patientId, string tenantId, CancellationToken ct = default)
    {
        var questionnaire = await GetQuestionnaireAsync(questionnaireId, tenantId, ct);
        if (questionnaire == null) return null;

        var bundle = new Bundle
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = Bundle.BundleType.Collection,
            Meta = new Meta { LastUpdated = DateTimeOffset.UtcNow },
            Entry = new List<Bundle.EntryComponent>
            {
                new()
                {
                    FullUrl = $"Questionnaire/{questionnaire.Id}",
                    Resource = questionnaire,
                },
            },
        };

        return bundle;
    }

    // ── Validation helpers (called by controller) ────────────────────────────

    public bool QuestionnaireExists(string questionnaireRef, string tenantId)
    {
        // questionnaireRef may be a canonical URL or "Questionnaire/{id}"
        var id = questionnaireRef;
        if (id.StartsWith("Questionnaire/", StringComparison.Ordinal))
            id = id["Questionnaire/".Length..];

        return _questionnaires.ContainsKey($"{tenantId}:{id}") ||
               _questionnaires.ContainsKey($"default:{id}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsAccessible(Questionnaire q, string tenantId)
    {
        // Questionnaire is accessible if stored under this tenant or under "default"
        var id = q.Id;
        return _questionnaires.ContainsKey($"{tenantId}:{id}") ||
               _questionnaires.ContainsKey($"default:{id}");
    }

    private static string GetTenantFromKey(string? id) => string.Empty;

    // ── Seed Data ────────────────────────────────────────────────────────────

    private void LoadSeedData()
    {
        var seeds = new[]
        {
            BuildImagingMriQuestionnaire(),
            BuildDmeEquipmentQuestionnaire(),
            BuildSurgeryOrthoQuestionnaire(),
            BuildSpecialtyReferralQuestionnaire(),
            BuildMedicationPaQuestionnaire(),
        };

        foreach (var q in seeds)
            _questionnaires[$"default:{q.Id}"] = q;

        _logger.LogInformation("Loaded {Count} seed questionnaires", seeds.Length);
    }

    private static Questionnaire BuildImagingMriQuestionnaire() => new()
    {
        Id = "q-imaging-mri",
        Name = "pa-imaging-mri",
        Title = "Prior Authorization: MRI Imaging",
        Status = PublicationStatus.Active,
        Date = "2025-01-15",
        Publisher = "Cloud Health Office",
        Description = new Markdown("Questionnaire for prior authorization of MRI imaging services"),
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
                Text = "Clinical indication for MRI",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = true,
            },
            new()
            {
                LinkId = "2",
                Text = "Body region",
                Type = Questionnaire.QuestionnaireItemType.Choice,
                Required = true,
                AnswerOption = new List<Questionnaire.AnswerOptionComponent>
                {
                    new() { Value = new Coding("http://snomed.info/sct", "122495006", "Head") },
                    new() { Value = new Coding("http://snomed.info/sct", "421060004", "Spine") },
                    new() { Value = new Coding("http://snomed.info/sct", "72696002", "Knee") },
                    new() { Value = new Coding("http://snomed.info/sct", "16982005", "Shoulder") },
                },
            },
            new()
            {
                LinkId = "2a",
                Text = "Does the patient have radiculopathy symptoms?",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
                EnableWhen = new List<Questionnaire.EnableWhenComponent>
                {
                    new()
                    {
                        Question = "2",
                        Operator = Questionnaire.QuestionnaireItemOperator.Equal,
                        Answer = new Coding("http://snomed.info/sct", "421060004", "Spine"),
                    },
                },
            },
            new()
            {
                LinkId = "3",
                Text = "Prior conservative treatment attempted",
                Type = Questionnaire.QuestionnaireItemType.Boolean,
                Required = true,
            },
            new()
            {
                LinkId = "4",
                Text = "Ordering provider NPI",
                Type = Questionnaire.QuestionnaireItemType.String,
                Required = true,
            },
            new()
            {
                LinkId = "5",
                Text = "Date of onset of symptoms",
                Type = Questionnaire.QuestionnaireItemType.Date,
                Required = false,
            },
        },
    };

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
