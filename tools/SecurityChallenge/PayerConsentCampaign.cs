using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SecurityChallenge;

public static class PayerConsentCampaign
{
    private const string Export = "fhir/r4/PayerToPayer/$member-data-export";
    private const string Match = "fhir/r4/Patient/$member-match";
    private const string Policy = "http://hl7.org/fhir/us/davinci-hrex/StructureDefinition-hrex-consent.html#sensitive";
    private static readonly string[] InvalidConsents = ["inactive", "wrong-recipient", "wrong-patient", "expired", "future", "regular", "nested-deny", "wrong-scope", "malformed-period", "unknown-party"];
    public static IReadOnlyList<string> ScenarioIds { get; } = Array.AsReadOnly(new[]
    {
        "export.inline-control", "export.cache-control", "match.control", "match.export-control",
        "cache.foreign-payer", "cache.foreign-member", "cache.foreign-tenant", "tenant.b.inline-control", "tenant.b.cache-control",
        "tenant.foreign-member", "member.other-control", "binding.export-body-payer", "binding.match-body-payer", "binding.header-conflict",
        "binding.no-payer", "binding.non-registration-issuer", "binding.patient", "binding.user", "binding.cho", "binding.scope", "identity.anonymous",
        "match.missing-consent", "match.foreign-member", "match.demographic-conflict", "cache.expired", "cache.future", "cache.fresh-process",
        "audit.member", "audit.coverage", "audit.claims", "resource.member-confinement", "resource.payer-confinement", "resource.cost-redaction",
        "attestation.attribution", "state.domain-unchanged"
    }.Concat(from operation in new[] { "export", "match" } from invalid in InvalidConsents select operation + ".consent." + invalid).ToArray());

    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 6 || args.Skip(1).Take(4).Any(p => !File.Exists(p)) || Path.Exists(args[5]))
        { Console.Error.WriteLine("Usage: SecurityChallenge p2p-consent <fhir-service.dll> <member-service.dll> <coverage-service.dll> <claims-service.dll> <new-evidence-directory>"); return 2; }
        var output = Path.GetFullPath(args[5]);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<CaseResult>(); var observations = new List<object>(); var states = new List<object>(); var audits = new List<object>();
        var started = DateTimeOffset.UtcNow; var runId = Guid.NewGuid().ToString("N");
        var source = Git("rev-parse", "HEAD"); var dirty = Git("status", "--porcelain").Length != 0;
        var binaries = args.Skip(1).Take(4).SelectMany(assembly => Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(assembly))!)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) || p.EndsWith(".deps.json", StringComparison.Ordinal) || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(p => new { service = Path.GetFileNameWithoutExtension(assembly), file = Path.GetFileName(p), sha256 = Hash(File.ReadAllBytes(p)) })).ToArray();
        var active = "lab.fixtures"; var completed = false; string? mongoVersion = null;
        try
        {
            active = "lab.mongo";
            using var mongo = MongoRunner.Run(new MongoRunnerOptions { Version = MongoVersion.V7, Edition = MongoEdition.Community, ConnectionTimeout = TimeSpan.FromSeconds(30) });
            using var client = new MongoClient(mongo.ConnectionString);
            var db = client.GetDatabase("p2p_security_" + runId);
            mongoVersion = (await db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1)))["version"].AsString;
            await using var authority = new LocalSmartIssuer(); await using var otherIssuer = new LocalSmartIssuer();
            await using var member = new LocalService(args[2], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, true,
                new Dictionary<string, string?> { ["Downstream:CoverageService:BaseUrl"] = "http://127.0.0.1:1/" });
            await using var coverage = new LocalService(args[3], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false);
            await using var claims = new LocalService(args[4], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false);
            active = "lab.member-ready"; await member.Ready();
            active = "lab.coverage-ready"; await coverage.Ready();
            active = "lab.claims-ready"; await claims.Ready();
            Dictionary<string, string?> Settings(string tenant)
            {
                var settings = new Dictionary<string, string?>
                {
                    ["SmartAuth:Mode"] = "ExternalIssuer", ["SmartAuth:ClockSkewSeconds"] = "0",
                    ["FhirAdapters:Sources:Patient"] = "ChoNative", ["FhirAdapters:Sources:Coverage"] = "ChoNative",
                    ["FhirAdapters:Sources:PayerToPayer"] = "ChoNative", ["FhirAdapters:TenantId"] = tenant,
                    ["Cms0057:PayerToPayerOutbound:LocalPayerId"] = "source-plan",
                    ["ChoAuth:Outbound:DevelopmentHosts:0"] = "127.0.0.1", ["Appeals:UseMockAdapter"] = "true",
                    ["Fhir:ServerBaseUrl"] = "{SELF}fhir/r4", ["Nppes:BaseUrl"] = "http://127.0.0.1:1/"
                };
                foreach (var name in new[] { "AppealsServiceUrl", "AuthorizationServiceUrl", "RfaiServiceUrl", "ProviderVerificationServiceUrl", "ProviderServiceUrl", "BenefitPlanServiceUrl", "ConsentServiceUrl" }) settings["Services:" + name] = "http://127.0.0.1:1/";
                settings["Services:MemberServiceUrl"] = member.Http.BaseAddress!.ToString();
                settings["Services:CoverageServiceUrl"] = coverage.Http.BaseAddress!.ToString();
                settings["Services:ClaimsServiceUrl"] = claims.Http.BaseAddress!.ToString();
                foreach (var i in new[] { 0, 1 })
                {
                    var issuer = i == 0 ? authority : otherIssuer; var key = $"SmartAuth:TrustedIssuers:{i}:";
                    settings[key + "Issuer"] = issuer.Issuer; settings[key + "JwksUri"] = issuer.Issuer + "/jwks";
                    settings[key + "Audiences:0"] = "fhir-api"; settings[key + "Tenants:0"] = "synthetic-payer-a"; settings[key + "Tenants:1"] = "synthetic-payer-b";
                    settings[key + "RequireHttpsMetadata"] = "false"; settings[key + "AllowedAlgorithms:0"] = "RS256";
                    settings[key + "Claims:TenantClaim"] = "tenant_id"; settings[key + "IssuesChoClientRegistrations"] = i == 0 ? "true" : "false";
                }
                return settings;
            }
            await using var fhirA = new LocalService(args[1], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false, Settings("synthetic-payer-a"));
            await using var fhirB = new LocalService(args[1], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false, Settings("synthetic-payer-b"));
            active = "lab.fhir-a-ready"; await fhirA.Ready();
            active = "lab.fhir-b-ready"; await fhirB.Ready();
            active = "lab.seed-fixtures";
            var hosts = new Dictionary<string, LocalService> { ["a"] = fhirA, ["b"] = fhirB };
            var ids = new Dictionary<string, (string Coverage, string Claim)>();
            foreach (var fixture in new[] { (Payer: "a", Id: "shared-member"), (Payer: "a", Id: "other-member"), (Payer: "a", Id: "match-member"), (Payer: "b", Id: "shared-member"), (Payer: "b", Id: "foreign-only") })
            {
                var key = fixture.Payer + "/" + fixture.Id; var covId = Guid.NewGuid().ToString(); var claimId = Guid.NewGuid().ToString(); ids[key] = (covId, claimId);
                await db.GetCollection<BsonDocument>("Members").InsertOneAsync(new BsonDocument
                {
                    { "_id", Guid.NewGuid().ToString() }, { "TenantId", "synthetic-payer-" + fixture.Payer }, { "MemberId", fixture.Id },
                    { "FirstName", "Synthetic" }, { "LastName", "CANARY-" + fixture.Payer + "-" + fixture.Id },
                    { "DateOfBirth", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, { "Gender", "U" }, { "IsSubscriber", true },
                    { "GroupNumber", "SYNTHETIC" }, { "EffectiveDate", DateTime.UtcNow.Date.AddYears(-1) }, { "CreatedDate", DateTime.UtcNow }, { "LastUpdatedDate", DateTime.UtcNow }, { "IsDraft", false }
                });
                await db.GetCollection<BsonDocument>("Coverage").InsertOneAsync(new BsonDocument
                {
                    { "_id", covId }, { "TenantId", "synthetic-payer-" + fixture.Payer }, { "MemberId", fixture.Id },
                    { "GroupNumber", "SYNTHETIC" }, { "PlanId", "SYNTHETIC-PLAN" }, { "EffectiveDate", DateTime.UtcNow.Date.AddYears(-1) }, { "Status", 1 }, { "LineOfBusiness", 1 }
                });
                await db.GetCollection<BsonDocument>("Claims").InsertOneAsync(new BsonDocument
                {
                    { "_id", claimId }, { "TenantId", "synthetic-payer-" + fixture.Payer }, { "MemberId", fixture.Id }, { "ClaimNumber", "SHARED-NUMBER" },
                    { "PatientLastName", "CANARY-" + fixture.Payer + "-" + fixture.Id }, { "Status", 7 }, { "ClaimType", 1 }, { "TotalChargeAmount", 9876 },
                    { "ServiceDateFrom", DateTime.UtcNow.Date }, { "ServiceDateTo", DateTime.UtcNow.Date }, { "CreatedDate", DateTime.UtcNow }, { "LastUpdatedDate", DateTime.UtcNow },
                    { "AdjudicationResult", new BsonDocument { { "AllowedAmount", 8765 }, { "PayerPayment", 7654 }, { "PatientResponsibility", 1111 }, { "PaymentDate", DateTime.UtcNow.Date } } }
                });
            }
            string Token(string tenant = "a", string? payer = "receiving-alpha", string scopes = "system/PayerToPayer.read", bool other = false) =>
                (other ? otherIssuer : authority).Token(tenant: "synthetic-payer-" + tenant, patient: null, scopes: scopes, payerId: payer, clientId: "synthetic-backend");
            var tokenA = Token(); var tokenB = Token("b"); var tokenOtherPayer = Token(payer: "receiving-beta");
            var attestations = db.GetCollection<BsonDocument>("p2p_consent_attestations");
            async Task<string> Snapshot(bool domainsOnly = false)
            {
                var content = new StringBuilder();
                var names = domainsOnly ? new List<string> { "Members", "Coverage", "Claims" } : (await (await db.ListCollectionNamesAsync()).ToListAsync())
                    .Where(n => n != "RecordAccessAudit" && n != "ClaimAccessAudit" && !n.StartsWith("system.", StringComparison.Ordinal)).ToList();
                foreach (var name in names.Order(StringComparer.Ordinal))
                {
                    var docs = await db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                    if (docs.Count > 0) content.Append(name).AppendJoin('\n', docs.Select(d => d.ToJson()).Order(StringComparer.Ordinal));
                }
                var hash = Hash(Encoding.UTF8.GetBytes(content.ToString())); states.Add(new { scenarioId = active, domainsOnly, sha256 = hash }); return hash;
            }
            var initialDomain = await Snapshot(true);
            async Task<(int Status, string Body)> Request(string host, string path, string? token, JsonObject body, string? header = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (header != null) request.Headers.Add("X-Tenant-ID", header);
                using var response = await hosts[host].Http.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
                observations.Add(new { scenarioId = active, host, method = "POST", path, status = (int)response.StatusCode, bodySha256 = Hash(Encoding.UTF8.GetBytes(text)), payerACanary = text.Contains("CANARY-a"), payerBCanary = text.Contains("CANARY-b") });
                return ((int)response.StatusCode, text);
            }
            JsonObject Body(string id = "shared-member", JsonObject? consent = null) => new() { ["memberId"] = id, ["consent"] = consent };
            async Task Case(string id, Func<Task<bool>> test, string? prerequisite = "export.inline-control")
            {
                active = id;
                if (prerequisite != null && !EvidencePolicy.PositiveControlPassed(results, prerequisite)) { results.Add(new(id, "INCONCLUSIVE", "Positive control unavailable: " + prerequisite)); return; }
                try { results.Add(new(id, await test() ? "PASS" : "FAIL", "See P2P-CONSENT catalog v1")); }
                catch { results.Add(new(id, "ERROR", "Execution failed; inspect restricted diagnostics")); }
            }
            async Task<bool> Denied(string path, JsonObject body, int expected, string? token = null, string host = "a", string? header = null, bool anonymous = false)
            {
                var before = await Snapshot(); var r = await Request(host, path, anonymous ? null : token ?? tokenA, body, header); var after = await Snapshot();
                return r.Status == expected && !r.Body.Contains("CANARY-") && before == after;
            }
            bool Confined(string text, string payer, string id)
            {
                using var json = JsonDocument.Parse(text); var entries = json.RootElement.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToArray();
                var expected = ids[payer + "/" + id];
                return entries.Length == 3 && text.Contains("CANARY-" + payer + "-" + id) && !text.Contains("CANARY-" + (payer == "a" ? "b" : "a"))
                    && entries.Count(r => r.GetProperty("resourceType").GetString() == "Patient" && r.GetProperty("id").GetString() == id) == 1
                    && entries.Count(r => r.GetProperty("resourceType").GetString() == "Coverage" && r.GetProperty("id").GetString() == expected.Coverage && r.GetProperty("beneficiary").GetProperty("reference").GetString() == "Patient/" + id) == 1
                    && entries.Count(r => r.GetProperty("resourceType").GetString() == "ExplanationOfBenefit" && r.GetProperty("id").GetString() == expected.Claim && r.GetProperty("patient").GetProperty("reference").GetString() == "Patient/" + id) == 1;
            }
            string? exportA = null; string? exportB = null;
            var suppliedConsent = Consent(); var expectedFingerprint = Hash(Encoding.UTF8.GetBytes(suppliedConsent.ToJsonString()));
            await Case("export.inline-control", async () => { var r = await Request("a", Export, tokenA, Body(consent: suppliedConsent)); exportA = r.Body; return r.Status == 200 && Confined(r.Body, "a", "shared-member"); }, null);
            await Case("export.cache-control", async () => { var r = await Request("a", Export, tokenA, Body()); return r.Status == 200 && Confined(r.Body, "a", "shared-member"); });
            await Case("cache.foreign-payer", () => Denied(Export, Body(), 403, tokenOtherPayer));
            await Case("cache.foreign-member", () => Denied(Export, Body("other-member"), 403));
            await Case("cache.foreign-tenant", () => Denied(Export, Body(), 403, tokenB, "b"));
            await Case("tenant.b.inline-control", async () => { var r = await Request("b", Export, tokenB, Body(consent: Consent())); exportB = r.Body; return r.Status == 200 && Confined(r.Body, "b", "shared-member"); }, null);
            await Case("tenant.b.cache-control", async () => { var r = await Request("b", Export, tokenB, Body()); return r.Status == 200 && Confined(r.Body, "b", "shared-member"); }, "tenant.b.inline-control");
            await Case("tenant.foreign-member", () => Denied(Export, Body("foreign-only", Consent("foreign-only")), 404));
            await Case("member.other-control", async () => { var r = await Request("a", Export, tokenA, Body("other-member", Consent("other-member"))); return r.Status == 200 && Confined(r.Body, "a", "other-member"); });
            await Case("match.control", async () => { var r = await Request("a", Match, tokenA, Body("match-member", Consent("match-member"))); return r.Status == 200 && r.Body.Contains("match-member"); });
            await Case("match.export-control", async () => { var r = await Request("a", Export, tokenA, Body("match-member")); return r.Status == 200 && Confined(r.Body, "a", "match-member"); }, "match.control");
            foreach (var operation in new[] { "export", "match" })
            foreach (var invalid in InvalidConsents)
                await Case(operation + ".consent." + invalid, () => Denied(operation == "export" ? Export : Match, Body(consent: Consent(variant: invalid)), operation == "export" ? 403 : 422), operation == "export" ? "export.inline-control" : "match.control");
            foreach (var operation in new[] { "export", "match" })
                await Case("binding." + operation + "-body-payer", () => { var body = Body(consent: Consent()); body["receivingPayerId"] = "receiving-beta"; return Denied(operation == "export" ? Export : Match, body, 403); });
            await Case("binding.header-conflict", () => Denied(Export, Body(consent: Consent()), 403, header: "synthetic-payer-b"));
            await Case("binding.no-payer", () => Denied(Export, Body(consent: Consent()), 403, Token(payer: null)));
            await Case("binding.non-registration-issuer", () => Denied(Export, Body(consent: Consent()), 403, Token(other: true)));
            await Case("binding.patient", () => Denied(Export, Body(consent: Consent()), 403, Token(scopes: "patient/PayerToPayer.read")));
            await Case("binding.user", () => Denied(Export, Body(consent: Consent()), 403, Token(scopes: "user/PayerToPayer.read")));
            await Case("binding.cho", () => Denied(Export, Body(consent: Consent()), 403, ChoDevelopmentAuth.UserToken("synthetic-payer-a", ChoRolePermissions.TenantAdmin)));
            await Case("binding.scope", () => Denied(Export, Body(consent: Consent()), 403, Token(scopes: "system/Patient.read")));
            await Case("identity.anonymous", () => Denied(Export, Body(consent: Consent()), 401, anonymous: true));
            await Case("match.missing-consent", () => Denied(Match, Body(), 422), "match.control");
            await Case("match.foreign-member", () => Denied(Match, Body("foreign-only", Consent("foreign-only")), 422), "match.control");
            await Case("match.demographic-conflict", () => { var body = Body(consent: Consent()); body["familyName"] = "WRONG"; return Denied(Match, body, 422); }, "match.control");
            var keyFilter = new BsonDocument { { "tenantId", "synthetic-payer-a" }, { "memberId", "shared-member" }, { "receivingPayerId", "receiving-alpha" } };
            foreach (var state in new[] { "expired", "future" })
                await Case("cache." + state, async () =>
                {
                    var original = await attestations.Find(keyFilter).SingleAsync();
                    try
                    {
                        await attestations.UpdateOneAsync(keyFilter, new BsonDocument("$set", new BsonDocument(state == "expired" ? "periodEnd" : "periodStart", DateTime.UtcNow.AddDays(state == "expired" ? -1 : 1))));
                        return await Denied(Export, Body(), 403);
                    }
                    finally { await attestations.ReplaceOneAsync(keyFilter, original); }
                }, "export.cache-control");
            await Case("cache.fresh-process", async () =>
            {
                await using var fresh = new LocalService(args[1], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false, Settings("synthetic-payer-a"));
                await fresh.Ready(); hosts["fresh"] = fresh; var r = await Request("fresh", Export, tokenA, Body("match-member"));
                return r.Status == 200 && Confined(r.Body, "a", "match-member");
            }, "match.export-control");
            await Case("resource.member-confinement", () => Task.FromResult(exportA != null && Confined(exportA, "a", "shared-member")));
            await Case("resource.payer-confinement", () => Task.FromResult(exportB != null && Confined(exportB, "b", "shared-member")), "tenant.b.inline-control");
            await Case("resource.cost-redaction", async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "fhir/ExplanationOfBenefit/" + ids["a/shared-member"].Claim);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChoDevelopmentAuth.UserToken("synthetic-payer-a", ChoRolePermissions.TenantAdmin));
                using var response = await claims.Http.SendAsync(request); var text = await response.Content.ReadAsStringAsync(); using var raw = JsonDocument.Parse(text);
                using var exported = JsonDocument.Parse(exportA!); var eob = exported.RootElement.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).Single(r => r.GetProperty("resourceType").GetString() == "ExplanationOfBenefit");
                var present = raw.RootElement.TryGetProperty("payment", out _) && raw.RootElement.TryGetProperty("total", out _);
                var absent = !eob.TryGetProperty("payment", out _) && !eob.TryGetProperty("total", out _) && !eob.TryGetProperty("adjudication", out _);
                states.Add(new { scenarioId = active, upstreamHasFinancialFields = present, exportRemovedFinancialFields = absent });
                return response.IsSuccessStatusCode && present && absent;
            });
            await Case("attestation.attribution", async () =>
            {
                var record = await attestations.Find(keyFilter).SingleAsync();
                return record["recordedBy"] == "client:synthetic-smart-user" && record["policy"] == Policy && record["consentFingerprint"] == expectedFingerprint && record["recordedAt"].ToUniversalTime() >= started.UtcDateTime.AddSeconds(-1);
            });
            foreach (var service in new[] { "member", "coverage", "claims" })
                await Case("audit." + service, async () =>
                {
                    var filter = new BsonDocument { { "TenantId", "synthetic-payer-a" }, { "ActorId", "fhir-service" }, { "StatusCode", 200 } };
                    if (service != "claims") filter["RecordType"] = service == "member" ? "Member" : "Coverage";
                    var count = await db.GetCollection<BsonDocument>(service == "claims" ? "ClaimAccessAudit" : "RecordAccessAudit").CountDocumentsAsync(filter);
                    audits.Add(new { scenarioId = active, scopedServiceReadEvents = count }); return count > 0;
                });
            await Case("state.domain-unchanged", async () => initialDomain == await Snapshot(true));
            completed = true;
        }
        catch (Exception e) { completed = false; Console.Error.WriteLine("Lab ERROR at " + active + ": " + e.GetType().Name); results.Add(new("lab.prerequisites", "ERROR", "Owned lab setup, fixture, execution or cleanup failed")); }
        var unsupported = new[] { "production-identity-tls-encryption", "provider-access-opt-outs", "consent-revocation-propagation", "cryptographic-consent-provenance", "claims-appeals-authorization-link-writes", "outbound-payer-exchange", "financial-accounting", "per-tenant-databases", "live-messaging", "durable-fhir-decision-audit", "comprehensive-eob-redaction" };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        async Task Write(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        await Write("manifest.json", new { schemaVersion = "1.0", campaign = "p2p-consent-v1", runId, started, finished = DateTimeOffset.UtcNow, completed, sourceCommit = source, sourceDirty = dirty, binaries, mongoVersion, runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fixtureVersion = "p2p-consent-synthetic-v1", profile = "loopback-native-p2p-two-source-tenants-three-upstreams", tenantStorage = "shared-database-row-filtering", memberSeed = "direct-mongo-existing-state", memberEncryption = "development-noop", identity = "ephemeral-rsa-jwks-http-loopback", consentStore = "mongo-tenant-member-requesting-payer", fixtureSeed = "direct-mongo-existing-state", publication = "REQUIRES_REVIEW" });
        await File.WriteAllLinesAsync(Path.Combine(output, "scenario-results.jsonl"), results.Select(r => JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await Write("coverage.json", new { planned = ScenarioIds.Count, reported = results.Count(r => r.Id != "lab.prerequisites"), executed = EvidencePolicy.ExecutedCount(results), pass = results.Count(r => r.Outcome == "PASS"), fail = results.Count(r => r.Outcome == "FAIL"), error = results.Count(r => r.Outcome == "ERROR"), inconclusive = results.Count(r => r.Outcome == "INCONCLUSIVE"), unsupported });
        await Write("http-observations.json", observations); await Write("state-assertions.json", states); await Write("audit-assertions.json", audits);
        await Write("findings-summary.json", new { reviewState = "REQUIRES_PRIVATE_TRIAGE", confirmedFindings = (int?)null, failedScenarios = results.Where(r => r.Outcome == "FAIL").Select(r => r.Id), errors = results.Where(r => r.Outcome == "ERROR").Select(r => r.Id) });
        await File.WriteAllTextAsync(Path.Combine(output, "knowledge-base-draft.md"), $"# Payer consent security review draft\n\nSource `{source}`, dirty: {dirty}.\n\n" + string.Join("\n", results.Select(r => $"- `{r.Id}`: **{r.Outcome}**")) + "\n\nUnsupported: " + string.Join(", ", unsupported) + ".\n\nPrivate review required. No production certification. See manifest, observations, state and audit assertions; sensitivity evidence is a separate packet.\n");
        await File.WriteAllTextAsync(Path.Combine(output, "reproduce.md"), "See docs/security-challenge/P2P-CONSENT.md at source `" + source + "`. Rebuild services and runner with .NET 8.\n");
        await File.WriteAllLinesAsync(Path.Combine(output, "artifact-checksums.txt"), Directory.GetFiles(output).Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p)) + "  " + Path.GetFileName(p)));
        Console.WriteLine($"Payer consent evidence: {output}; PASS={results.Count(r => r.Outcome == "PASS")}, FAIL={results.Count(r => r.Outcome == "FAIL")}, ERROR={results.Count(r => r.Outcome == "ERROR")}, INCONCLUSIVE={results.Count(r => r.Outcome == "INCONCLUSIVE")}");
        return EvidencePolicy.IsCompletePass(completed, results, ScenarioIds) ? 0 : 1;
    }
    private static JsonObject Consent(string member = "shared-member", string variant = "valid")
    {
        JsonObject Concept(string system, string code) => new() { ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code }) };
        JsonObject Actor(string role, string payer) => new() { ["role"] = Concept("http://terminology.hl7.org/CodeSystem/v3-ParticipationType", role), ["reference"] = new JsonObject { ["identifier"] = new JsonObject { ["value"] = payer } } };
        var period = new JsonObject { ["start"] = DateTime.UtcNow.AddDays(-1).ToString("o"), ["end"] = DateTime.UtcNow.AddDays(1).ToString("o") };
        var actors = new JsonArray(Actor("PERF", "source-plan"), Actor("IRCP", variant == "wrong-recipient" ? "receiving-beta" : "receiving-alpha"));
        var provision = new JsonObject { ["type"] = "permit", ["period"] = period, ["actor"] = actors };
        var consent = new JsonObject
        {
            ["resourceType"] = "Consent", ["status"] = variant == "inactive" ? "inactive" : "active",
            ["scope"] = Concept("http://terminology.hl7.org/CodeSystem/consentscope", variant == "wrong-scope" ? "research" : "patient-privacy"),
            ["category"] = new JsonArray(Concept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "IDSCL")),
            ["patient"] = new JsonObject { ["identifier"] = new JsonObject { ["value"] = variant == "wrong-patient" ? "other-member" : member } },
            ["policy"] = new JsonArray(new JsonObject { ["uri"] = variant == "regular" ? Policy.Replace("#sensitive", "#regular", StringComparison.Ordinal) : Policy }),
            ["provision"] = provision
        };
        if (variant == "expired") period["end"] = DateTime.UtcNow.AddHours(-1).ToString("o");
        if (variant == "future") { period["start"] = DateTime.UtcNow.AddDays(2).ToString("o"); period["end"] = DateTime.UtcNow.AddDays(3).ToString("o"); }
        if (variant == "malformed-period") period["start"] = "not-a-date";
        if (variant == "nested-deny") provision["provision"] = new JsonArray(new JsonObject { ["type"] = "deny" });
        if (variant == "unknown-party") actors.Add(Actor("IRCP", "unrelated-party"));
        return consent;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git unavailable");
        var value = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Run inside source checkout"); return value.Trim();
    }
}
