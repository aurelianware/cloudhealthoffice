using CloudHealthOffice.Portal.Models;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Portal.Services;

public class TmppmRuleQueryService : ITmppmRuleQueryService
{
    private const string DefaultDatabaseName = "cho_terminology";
    private const string DefaultPaRulesCollectionName = "tmppm_pa_rules";
    private const string DefaultEditionsCollectionName = "tmppm_editions";
    private const string DefaultDiffReportsCollectionName = "tmppm_diff_reports";

    private readonly IMongoCollection<BsonDocument> _paRules;
    private readonly IMongoCollection<BsonDocument> _editions;
    private readonly IMongoCollection<BsonDocument> _diffs;
    private readonly ILogger<TmppmRuleQueryService> _logger;

    public TmppmRuleQueryService(
        IMongoClient mongoClient,
        IConfiguration configuration,
        ILogger<TmppmRuleQueryService> logger)
    {
        var databaseName = configuration["Mongo:Tmppm:DatabaseName"] ?? DefaultDatabaseName;
        var paRulesCollectionName = configuration["Mongo:Tmppm:PaRulesCollectionName"] ?? DefaultPaRulesCollectionName;
        var editionsCollectionName = configuration["Mongo:Tmppm:EditionsCollectionName"] ?? DefaultEditionsCollectionName;
        var diffReportsCollectionName = configuration["Mongo:Tmppm:DiffReportsCollectionName"] ?? DefaultDiffReportsCollectionName;

        var database = mongoClient.GetDatabase(databaseName);
        _paRules = database.GetCollection<BsonDocument>(paRulesCollectionName);
        _editions = database.GetCollection<BsonDocument>(editionsCollectionName);
        _diffs = database.GetCollection<BsonDocument>(diffReportsCollectionName);
        _logger = logger;
    }

    public async Task<List<TmppmPaRuleViewModel>> SearchByCodeAsync(string code, string? tenantId = null, string? state = null)
    {
        var normalizedCode = code.Trim().ToUpperInvariant();

        var filter = Builders<BsonDocument>.Filter.AnyEq("procedureCodes", normalizedCode);
        if (!string.IsNullOrEmpty(tenantId))
            filter &= Builders<BsonDocument>.Filter.Eq("tenantId", tenantId);
        if (!string.IsNullOrEmpty(state))
            filter &= Builders<BsonDocument>.Filter.Eq("state", state);

        var docs = await _paRules.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("category"))
            .ToListAsync();

        return docs.Select(MapToRuleViewModel).ToList();
    }

    public async Task<List<PaCategoryGroup>> GetCategoriesAsync(string state = "TX")
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("state", state)),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument { { "category", "$category" }, { "tmppmRef", "$tmppmRef" } } },
                { "ruleCount", new BsonDocument("$sum", 1) },
                { "codes", new BsonDocument("$addToSet", new BsonDocument("$ifNull",
                    new BsonArray { "$procedureCodes", new BsonArray() })) },
                { "ruleType", new BsonDocument("$first", "$ruleType") }
            }),
            new BsonDocument("$sort", new BsonDocument("_id.category", 1))
        };

        var results = await _paRules.Aggregate<BsonDocument>(pipeline).ToListAsync();

        var categories = results.Select(doc =>
        {
            var id = doc["_id"].AsBsonDocument;
            var codes = doc.Contains("codes")
                ? doc["codes"].AsBsonArray
                    .SelectMany(a => a.IsBsonArray ? a.AsBsonArray.Select(v => v.AsString) : Enumerable.Empty<string>())
                    .Distinct()
                    .ToList()
                : new List<string>();

            return new
            {
                Category = id.GetValue("category", "").AsString,
                TmppmRef = id.GetValue("tmppmRef", "").AsString,
                RuleCount = doc["ruleCount"].ToInt32(),
                CodeCount = codes.Count,
                RuleType = doc.GetValue("ruleType", "").AsString
            };
        }).ToList();

        // Group by priority based on rule type heuristics
        var grouped = new List<PaCategoryGroup>
        {
            new()
            {
                Priority = "P1",
                Categories = categories
                    .Where(c => c.RuleType is "AuthRequired" or "DiagnosisRestriction")
                    .Select(c => new PaCategorySummary
                    {
                        Category = c.Category,
                        TmppmRef = c.TmppmRef,
                        RuleCount = c.RuleCount,
                        CodeCount = c.CodeCount
                    })
                    .OrderBy(c => c.Category)
                    .ToList()
            },
            new()
            {
                Priority = "P2",
                Categories = categories
                    .Where(c => c.RuleType is "AgeLimit" or "UnitLimit")
                    .Select(c => new PaCategorySummary
                    {
                        Category = c.Category,
                        TmppmRef = c.TmppmRef,
                        RuleCount = c.RuleCount,
                        CodeCount = c.CodeCount
                    })
                    .OrderBy(c => c.Category)
                    .ToList()
            },
            new()
            {
                Priority = "P3",
                Categories = categories
                    .Where(c => c.RuleType is not "AuthRequired" and not "DiagnosisRestriction"
                        and not "AgeLimit" and not "UnitLimit")
                    .Select(c => new PaCategorySummary
                    {
                        Category = c.Category,
                        TmppmRef = c.TmppmRef,
                        RuleCount = c.RuleCount,
                        CodeCount = c.CodeCount
                    })
                    .OrderBy(c => c.Category)
                    .ToList()
            }
        };

        return grouped.Where(g => g.Categories.Count > 0).ToList();
    }

    public async Task<List<TmppmPaRuleViewModel>> GetRulesByCategoryAsync(string category, string? tenantId = null, string? state = null)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("category", category);
        if (!string.IsNullOrEmpty(tenantId))
            filter &= Builders<BsonDocument>.Filter.Eq("tenantId", tenantId);
        if (!string.IsNullOrEmpty(state))
            filter &= Builders<BsonDocument>.Filter.Eq("state", state);

        var docs = await _paRules.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("tmppmRef"))
            .ToListAsync();

        return docs.Select(MapToRuleViewModel).ToList();
    }

    public async Task<TmppmEditionViewModel?> GetCurrentEditionAsync()
    {
        var doc = await _editions.Find(Builders<BsonDocument>.Filter.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("ingestedAt"))
            .FirstOrDefaultAsync();

        return doc == null ? null : MapToEditionViewModel(doc);
    }

    public async Task<List<TmppmEditionViewModel>> GetAllEditionsAsync()
    {
        var docs = await _editions.Find(Builders<BsonDocument>.Filter.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("ingestedAt"))
            .Limit(12)
            .ToListAsync();

        return docs.Select(MapToEditionViewModel).ToList();
    }

    public async Task<TmppmDiffViewModel?> GetDiffAsync(string fromEdition, string toEdition)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("fromEdition", fromEdition),
            Builders<BsonDocument>.Filter.Eq("toEdition", toEdition));

        var doc = await _diffs.Find(filter).FirstOrDefaultAsync();
        if (doc == null) return null;

        return new TmppmDiffViewModel
        {
            FromEdition = doc.GetValue("fromEdition", "").AsString,
            ToEdition = doc.GetValue("toEdition", "").AsString,
            GeneratedAt = doc.GetValue("generatedAt", BsonDateTime.Create(DateTime.MinValue)).ToUniversalTime(),
            Deltas = doc.GetValue("deltas", new BsonArray()).AsBsonArray.Select(d =>
            {
                var delta = d.AsBsonDocument;
                return new TmppmRuleDeltaViewModel
                {
                    DeltaType = delta.GetValue("deltaType", "").AsString,
                    RuleId = delta.GetValue("ruleId", "").AsString,
                    Category = delta.GetValue("category", "").AsString,
                    PreviousValue = delta.GetValue("previousValue", BsonNull.Value).IsBsonNull
                        ? null : delta["previousValue"].AsString,
                    NewValue = delta.GetValue("newValue", BsonNull.Value).IsBsonNull
                        ? null : delta["newValue"].AsString,
                    Description = delta.GetValue("description", BsonNull.Value).IsBsonNull
                        ? null : delta["description"].AsString,
                    RequiresHumanReview = delta.GetValue("requiresHumanReview", false).AsBoolean
                };
            }).ToList()
        };
    }

    public async Task<List<string>> AutocompleteCodeAsync(string prefix, int maxResults = 10)
    {
        var normalizedPrefix = prefix.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalizedPrefix)) return [];

        var filter = Builders<BsonDocument>.Filter.Regex(
            "procedureCodes",
            new BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(normalizedPrefix)}", "i"));

        var docs = await _paRules.Find(filter)
            .Limit(50)
            .ToListAsync();

        return docs
            .SelectMany(d => d.GetValue("procedureCodes", new BsonArray()).AsBsonArray.Select(v => v.AsString))
            .Where(c => c.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(c => c)
            .Take(maxResults)
            .ToList();
    }

    private static TmppmPaRuleViewModel MapToRuleViewModel(BsonDocument doc)
    {
        return new TmppmPaRuleViewModel
        {
            RuleId = doc.GetValue("ruleId", "").AsString,
            Category = doc.GetValue("category", "").AsString,
            TmppmRef = doc.GetValue("tmppmRef", "").AsString,
            AuthRequired = doc.GetValue("authRequired", false).AsBoolean,
            AuthType = doc.GetValue("authType", BsonNull.Value).IsBsonNull ? null : doc["authType"].AsString,
            ProcedureCodes = doc.GetValue("procedureCodes", new BsonArray()).AsBsonArray
                .Select(v => v.AsString).ToList(),
            CodeSystem = doc.GetValue("codeSystem", "CPT").AsString,
            ClinicalCriteriaSummary = doc.GetValue("clinicalCriteriaSummary", BsonNull.Value).IsBsonNull
                ? null : doc["clinicalCriteriaSummary"].AsString,
            RequiredDocumentation = doc.GetValue("requiredDocumentation", BsonNull.Value).IsBsonNull
                ? null : doc["requiredDocumentation"].AsBsonArray.Select(v => v.AsString).ToList(),
            AgeLimit = doc.Contains("ageLimit") && !doc["ageLimit"].IsBsonNull
                ? new AgeRuleViewModel
                {
                    MinAge = doc["ageLimit"].AsBsonDocument.GetValue("minAge", BsonNull.Value).IsBsonNull
                        ? null : doc["ageLimit"]["minAge"].ToInt32(),
                    MaxAge = doc["ageLimit"].AsBsonDocument.GetValue("maxAge", BsonNull.Value).IsBsonNull
                        ? null : doc["ageLimit"]["maxAge"].ToInt32(),
                    Unit = doc["ageLimit"].AsBsonDocument.GetValue("unit", "years").AsString
                }
                : null,
            UnitLimit = doc.Contains("unitLimit") && !doc["unitLimit"].IsBsonNull
                ? new UnitLimitViewModel
                {
                    MaxUnits = doc["unitLimit"].AsBsonDocument.GetValue("maxUnits", 0).ToInt32(),
                    Per = doc["unitLimit"].AsBsonDocument.GetValue("per", "").AsString,
                    ResetCondition = doc["unitLimit"].AsBsonDocument.GetValue("resetCondition", BsonNull.Value).IsBsonNull
                        ? null : doc["unitLimit"]["resetCondition"].AsString
                }
                : null,
            AllowedDiagnoses = doc.GetValue("allowedDiagnoses", BsonNull.Value).IsBsonNull
                ? null : doc["allowedDiagnoses"].AsBsonArray.Select(v => v.AsString).ToList(),
            State = doc.GetValue("state", "TX").AsString,
            SourceEdition = doc.GetValue("sourceEdition", "").AsString
        };
    }

    private static TmppmEditionViewModel MapToEditionViewModel(BsonDocument doc)
    {
        return new TmppmEditionViewModel
        {
            EditionId = doc.GetValue("editionId", "").AsString,
            PublicationDate = DateOnly.FromDateTime(
                doc.GetValue("publicationDate", BsonDateTime.Create(DateTime.MinValue)).ToUniversalTime()),
            PolicyThroughDate = DateOnly.FromDateTime(
                doc.GetValue("policyThroughDate", BsonDateTime.Create(DateTime.MinValue)).ToUniversalTime()),
            SourceUrl = doc.GetValue("sourceUrl", "").AsString,
            IngestedAt = doc.GetValue("ingestedAt", BsonDateTime.Create(DateTime.MinValue)).ToUniversalTime(),
            Chapters = doc.GetValue("chapters", new BsonArray()).AsBsonArray.Select(c =>
            {
                var ch = c.AsBsonDocument;
                return new TmppmChapterViewModel
                {
                    ChapterId = ch.GetValue("chapterId", "").AsString,
                    Title = ch.GetValue("title", "").AsString,
                    PdfUrl = ch.GetValue("pdfUrl", "").AsString,
                    Sha256 = ch.GetValue("sha256", BsonNull.Value).IsBsonNull ? null : ch["sha256"].AsString,
                    ExtractedRuleCount = ch.GetValue("extractedRuleCount", 0).ToInt32()
                };
            }).ToList()
        };
    }
}
