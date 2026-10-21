using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SecurityChallenge;

public static class ProviderAccessCampaign
{
    public static IReadOnlyList<string> ScenarioIds { get; } = Array.AsReadOnly(new[]
    {
        "provider.control",
        "provider.system-control",
        "provider.mixed-control",
        "attribution.foreign-member",
        "attribution.foreign-tenant",
        "attribution.lowercase",
        "attribution.query-spoof",
        "identity.tenant-header-conflict",
        "memberless.search",
        "provider.search-control",
        "tenant.b.control",
        "consent.draft-control",
        "consent.activated-denial",
        "consent.system-denial",
        "consent.lowercase-denial",
        "consent.search-denial",
        "consent.patient-self-control",
        "consent.other-member-control",
        "consent.other-tenant-control",
        "consent.revoked-optout-control",
        "consent.p2p-isolation-control",
        "consent.permit-control",
        "consent.withdrawn-permit-denial",
        "consent.repermit-control",
        "consent.deny-overrides-permit",
        "registry.pre-outage-control",
        "registry.outage-denial",
        "registry.outage-after-denial",
        "registry.patient-self-control",
        "upstream.audit"
    });

    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 5 || !File.Exists(args[1]) || !File.Exists(args[2]) || !File.Exists(args[3]) || Path.Exists(args[4]))
        { Console.Error.WriteLine("Usage: SecurityChallenge provider-access <fhir-service.dll> <member-service.dll> <consent-service.dll> <new-evidence-directory>"); return 2; }
        var output = Path.GetFullPath(args[4]);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<CaseResult>(); var observations = new List<object>(); var states = new List<object>(); var audits = new List<object>();
        var started = DateTimeOffset.UtcNow; var runId = Guid.NewGuid().ToString("N");
        var source = Git("rev-parse", "HEAD"); var dirty = Git("status", "--porcelain").Length != 0;
        var binaries = args.Skip(1).Take(3).SelectMany(assembly => Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(assembly))!)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) || p.EndsWith(".deps.json", StringComparison.Ordinal) || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(p => new { service = Path.GetFileNameWithoutExtension(assembly), file = Path.GetFileName(p), sha256 = Hash(File.ReadAllBytes(p)) })).ToArray();
        var active = "lab.mongo"; var completed = false; string? mongoVersion = null;
        try
        {
            using var mongo = MongoRunner.Run(new MongoRunnerOptions { Version = MongoVersion.V7, Edition = MongoEdition.Community, ConnectionTimeout = TimeSpan.FromSeconds(30) });
            using var client = new MongoClient(mongo.ConnectionString);
            var db = client.GetDatabase("fhir_smart_security_" + runId);
            mongoVersion = (await db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1)))["version"].AsString;
            await using var issuerA = new LocalSmartIssuer(); await using var issuerB = new LocalSmartIssuer();
            await using var member = new LocalService(args[2], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, true,
                new Dictionary<string, string?> { ["Downstream:CoverageService:BaseUrl"] = "http://127.0.0.1:1/" });
            await member.Ready();
            await using var consent = new LocalService(args[3], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, true);
            await consent.Ready();
            var settings = new Dictionary<string, string?>
            {
                ["SmartAuth:Mode"] = "ExternalIssuer", ["SmartAuth:ClockSkewSeconds"] = "0",
                ["FhirAdapters:Sources:Patient"] = "ChoNative", ["FhirAdapters:Sources:PayerToPayer"] = "Mock",
                ["Services:MemberServiceUrl"] = member.Http.BaseAddress!.ToString(),
                ["ChoAuth:Outbound:DevelopmentHosts:0"] = "127.0.0.1", ["Appeals:UseMockAdapter"] = "true",
                ["Fhir:ServerBaseUrl"] = "{SELF}fhir/r4"
            };
            // Every configured downstream stays inside the owned loopback lab.
            foreach (var name in new[] { "CoverageServiceUrl", "ClaimsServiceUrl", "AppealsServiceUrl", "AuthorizationServiceUrl", "RfaiServiceUrl", "ProviderVerificationServiceUrl", "ProviderServiceUrl", "BenefitPlanServiceUrl", "ConsentServiceUrl" })
                settings["Services:" + name] = "http://127.0.0.1:1/";
            settings["Services:ConsentServiceUrl"] = consent.Http.BaseAddress!.ToString();
            foreach (var tenant in new[] { "a", "b" })
            {
                var panel = "Cms0057:ProviderAttribution:PanelsByTenant:synthetic-payer-" + tenant + ":0:";
                settings[panel + "ProviderId"] = "synthetic-smart-user";
                settings[panel + "MemberIds:0"] = "shared-member";
                if (tenant == "a") settings[panel + "MemberIds:1"] = "a-only-member";
            }
            settings["Nppes:BaseUrl"] = "http://127.0.0.1:1/";
            foreach (var i in new[] { 0, 1 })
            {
                var issuer = i == 0 ? issuerA : issuerB; var key = $"SmartAuth:TrustedIssuers:{i}:";
                settings[key + "Issuer"] = issuer.Issuer; settings[key + "JwksUri"] = issuer.Issuer + "/jwks";
                settings[key + "Audiences:0"] = i == 0 ? "fhir-api" : "second-fhir-api";
                settings[key + "Tenants:0"] = "synthetic-payer-" + (i == 0 ? "a" : "b");
                settings[key + "RequireHttpsMetadata"] = "false"; settings[key + "AllowedAlgorithms:0"] = "RS256";
                settings[key + "Claims:TenantClaim"] = "tenant_id"; settings[key + "Claims:PatientClaim"] = "patient";
                settings[key + "IssuesChoClientRegistrations"] = i == 0 ? "true" : "false";
            }
            await using var fhir = new LocalService(args[1], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false, settings);
            await fhir.Ready();
            var members = db.GetCollection<BsonDocument>("Members");
            foreach (var fixture in new[] { (Payer: "a", Id: "shared-member"), (Payer: "a", Id: "a-only-member"), (Payer: "a", Id: "unattributed-member"), (Payer: "b", Id: "shared-member") })
                await members.InsertOneAsync(new BsonDocument
                {
                    { "_id", Guid.NewGuid().ToString() }, { "TenantId", "synthetic-payer-" + fixture.Payer }, { "MemberId", fixture.Id },
                    { "FirstName", "Synthetic" }, { "LastName", "CANARY-" + fixture.Payer + "-" + fixture.Id },
                    { "DateOfBirth", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, { "Gender", "U" },
                    { "IsSubscriber", true }, { "GroupNumber", "SYNTHETIC" }, { "EffectiveDate", DateTime.UtcNow.Date },
                    { "CreatedDate", DateTime.UtcNow }, { "LastUpdatedDate", DateTime.UtcNow }, { "IsDraft", false }
                });
            var provider = issuerA.Token(patient: null, scopes: "user/Patient.read");
            var system = issuerA.Token(patient: null, scopes: "system/Patient.read");
            var mixed = issuerA.Token(scopes: "patient/Patient.read user/Patient.read");
            var patient = issuerA.Token();
            var providerB = issuerB.Token(tenant: "synthetic-payer-b", patient: null, scopes: "user/Patient.read", audience: "second-fhir-api");
            var writer = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-consent-recorder", "synthetic-payer-a", [], ["consent:read", "consent:write"]);
            async Task<string> Registry(string path, object body, int expected)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writer);
                request.Content = System.Net.Http.Json.JsonContent.Create(body);
                using var response = await consent.Http.SendAsync(request);
                var payload = await response.Content.ReadAsStringAsync();
                observations.Add(new { scenarioId = active, method = "POST", path, status = (int)response.StatusCode, bodySha256 = Hash(Encoding.UTF8.GetBytes(payload)) });
                if ((int)response.StatusCode != expected) throw new InvalidOperationException("Consent fixture transition failed");
                return payload;
            }
            const string registry = "api/v1/members/shared-member/consents";
            async Task<string> Create(string provision, string purpose = "ProviderAccess")
            {
                var body = await Registry(registry, new { consentType = "GeneralAuthorization", purposeOfUse = purpose, provision, grantorType = "Member", grantedBy = "shared-member" }, 201);
                using var json = JsonDocument.Parse(body); return json.RootElement.GetProperty("id").GetString()!;
            }
            async Task Activate(string id) => await Registry(registry + "/" + id + "/activate", new { }, 200);
            async Task Revoke(string id) => await Registry(registry + "/" + id + "/revoke", new { reasonCode = "MemberRequest" }, 200);
            async Task<string> Snapshot()
            {
                var text = new StringBuilder();
                foreach (var name in (await (await db.ListCollectionNamesAsync()).ToListAsync()).Where(n => n != "RecordAccessAudit" && !n.StartsWith("system.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    var docs = await db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                    if (docs.Count > 0) text.Append(name).AppendJoin('\n', docs.Select(d => d.ToJson()).Order(StringComparer.Ordinal));
                }
                var hash = Hash(Encoding.UTF8.GetBytes(text.ToString())); states.Add(new { scenarioId = active, sha256 = hash }); return hash;
            }
            async Task<(int Status, string Body, string? Location)> Request(string path, string? token, string method = "GET", string? tenantHeader = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (tenantHeader != null) request.Headers.Add("X-Tenant-ID", tenantHeader);
                request.Headers.TryAddWithoutValidation("Prefer", "respond-async");
                using var response = await fhir.Http.SendAsync(request); var body = await response.Content.ReadAsStringAsync();
                observations.Add(new { scenarioId = active, method, path, status = (int)response.StatusCode, bodySha256 = Hash(Encoding.UTF8.GetBytes(body)), payerACanary = body.Contains("CANARY-a"), payerBCanary = body.Contains("CANARY-b") });
                return ((int)response.StatusCode, body, response.Content.Headers.ContentLocation?.ToString());
            }
            async Task Case(string id, Func<Task<bool>> test, string? prerequisite = "provider.control")
            {
                active = id;
                if (prerequisite != null && !EvidencePolicy.PositiveControlPassed(results, prerequisite))
                { results.Add(new(id, "INCONCLUSIVE", "Positive control unavailable: " + prerequisite)); return; }
                try { results.Add(new(id, await test() ? "PASS" : "FAIL", "See Provider Access catalog v1")); }
                catch { results.Add(new(id, "ERROR", "Execution failed; inspect restricted diagnostics")); }
            }
            async Task<bool> Denied(string path, string? token, int expected, string method = "GET", string? header = null)
            {
                var before = await Snapshot(); var r = await Request(path, token, method, header);
                var after = await Snapshot();
                return r.Status == expected && !r.Body.Contains("CANARY-") && before == after;
            }
            const string own = "fhir/r4/Patient/shared-member";
            async Task<bool> Read(string path, string token, string canary)
            {
                var r = await Request(path, token);
                return r.Status == 200 && r.Body.Contains(canary) && !r.Body.Contains(canary.StartsWith("CANARY-a", StringComparison.Ordinal) ? "CANARY-b" : "CANARY-a");
            }
            await Case("provider.control", () => Read(own, provider, "CANARY-a-shared-member"), null);
            await Case("provider.system-control", () => Read(own, system, "CANARY-a-shared-member"));
            await Case("provider.mixed-control", () => Read(own, mixed, "CANARY-a-shared-member"));
            await Case("tenant.b.control", () => Read(own, providerB, "CANARY-b-shared-member"), null);
            await Case("attribution.foreign-member", () => Denied("fhir/r4/Patient/unattributed-member", provider, 403));
            await Case("attribution.foreign-tenant", () => Denied("fhir/r4/Patient/unattributed-member", providerB, 403), "tenant.b.control");
            await Case("attribution.lowercase", () => Denied("fhir/r4/patient/unattributed-member", provider, 403));
            await Case("attribution.query-spoof", () => Denied("fhir/r4/Patient/unattributed-member?provider=synthetic-smart-user&patient=shared-member", provider, 403));
            await Case("identity.tenant-header-conflict", () => Denied(own, provider, 403, header: "synthetic-payer-b"));
            await Case("memberless.search", () => Denied("fhir/r4/Patient", provider, 403));
            await Case("provider.search-control", async () =>
            {
                var r = await Request("fhir/r4/Patient?_id=shared-member", provider);
                using var body = JsonDocument.Parse(r.Body);
                return r.Status == 200 && body.RootElement.GetProperty("total").GetInt32() == 1
                    && body.RootElement.GetProperty("entry").GetArrayLength() == 1
                    && r.Body.Contains("CANARY-a-shared-member") && !r.Body.Contains("CANARY-b")
                    && !r.Body.Contains("CANARY-a-a-only-member") && !r.Body.Contains("unattributed-member");
            });
            string? optout = null;
            await Case("consent.draft-control", async () => { optout = await Create("Deny"); return await Read(own, provider, "CANARY-a-shared-member"); });
            await Case("consent.activated-denial", async () => { await Activate(optout!); return await Denied(own, provider, 403); }, "consent.draft-control");
            await Case("consent.system-denial", () => Denied(own, system, 403), "consent.draft-control");
            await Case("consent.lowercase-denial", () => Denied("fhir/r4/patient/shared-member", provider, 403), "consent.draft-control");
            await Case("consent.search-denial", () => Denied("fhir/r4/Patient?_id=shared-member", provider, 403), "consent.draft-control");
            await Case("consent.patient-self-control", () => Read(own, patient, "CANARY-a-shared-member"));
            await Case("consent.other-member-control", () => Read("fhir/r4/Patient/a-only-member", provider, "CANARY-a-a-only-member"));
            await Case("consent.other-tenant-control", () => Read(own, providerB, "CANARY-b-shared-member"), "tenant.b.control");
            await Case("consent.revoked-optout-control", async () => { await Revoke(optout!); return await Read(own, provider, "CANARY-a-shared-member"); }, "consent.draft-control");
            await Case("consent.p2p-isolation-control", async () => { var id = await Create("Deny", "PayerToPayerExchange"); await Activate(id); return await Read(own, provider, "CANARY-a-shared-member"); }, "consent.revoked-optout-control");
            string? permit = null;
            await Case("consent.permit-control", async () => { permit = await Create("Permit"); await Activate(permit); return await Read(own, provider, "CANARY-a-shared-member"); }, "consent.revoked-optout-control");
            await Case("consent.withdrawn-permit-denial", async () => { await Revoke(permit!); return await Denied(own, provider, 403); }, "consent.permit-control");
            await Case("consent.repermit-control", async () => { var id = await Create("Permit"); await Activate(id); return await Read(own, provider, "CANARY-a-shared-member"); }, "consent.permit-control");
            await Case("consent.deny-overrides-permit", async () => { var id = await Create("Deny"); await Activate(id); return await Denied(own, provider, 403); }, "consent.repermit-control");
            await Case("registry.pre-outage-control", () => Read(own, providerB, "CANARY-b-shared-member"), "tenant.b.control");
            await consent.StopAsync();
            await Case("registry.outage-denial", () => Denied(own, providerB, 403), "registry.pre-outage-control");
            await Case("registry.outage-after-denial", () => Denied(own, provider, 403), "provider.control");
            await Case("registry.patient-self-control", () => Read(own, patient, "CANARY-a-shared-member"));
            await Case("upstream.audit", async () =>
            {
                var count = await db.GetCollection<BsonDocument>("RecordAccessAudit").CountDocumentsAsync(new BsonDocument { { "TenantId", "synthetic-payer-a" }, { "ActorId", "fhir-service" }, { "StatusCode", 200 } });
                audits.Add(new { scenarioId = active, tenantBoundFhirServiceReads = count }); return count > 0;
            });
            completed = true;
        }
        catch (Exception e) { completed = false; Console.Error.WriteLine("Lab ERROR: " + e.GetType().Name); results.Add(new("lab.prerequisites", "ERROR", "Local service, fixture, identity or database setup/cleanup failed")); }
        var unsupported = new[] { "production-identity-and-tls", "member-production-encryption", "authorization-server-grant-pkce-refresh-revocation", "jwks-rotation-and-outage", "non-Patient-resource-data", "bulk-data-downloads", "live-attribution-feed", "registry-recovery", "consent-production-encryption", "non-Patient-provider-resources", "financial-accounting", "per-tenant-databases", "deployed-ingress", "full-log-phi-leakage" };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        async Task Write(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        await Write("manifest.json", new { schemaVersion = "1.0", campaign = "provider-access-v1", runId, started, finished = DateTimeOffset.UtcNow, completed, sourceCommit = source, sourceDirty = dirty, binaries, mongoVersion, runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fixtureVersion = "provider-consent-synthetic-v1", profile = "loopback-real-consent-member-development-fhir-testing", tenantStorage = "shared-database-row-filtering", memberSeed = "direct-mongo-existing-state", memberEncryption = "development-noop", identity = "ephemeral-rsa-jwks-http-loopback", consentEncryption = "development-noop", consentSeed = "real-http-lifecycle", attribution = "configured-catalog-no-live-feed", publication = "REQUIRES_REVIEW" });
        await File.WriteAllLinesAsync(Path.Combine(output, "scenario-results.jsonl"), results.Select(r => JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await Write("coverage.json", new { planned = ScenarioIds.Count, reported = results.Count(r => r.Id != "lab.prerequisites"), executed = EvidencePolicy.ExecutedCount(results), pass = results.Count(r => r.Outcome == "PASS"), fail = results.Count(r => r.Outcome == "FAIL"), error = results.Count(r => r.Outcome == "ERROR"), inconclusive = results.Count(r => r.Outcome == "INCONCLUSIVE"), unsupported });
        await Write("http-observations.json", observations); await Write("state-assertions.json", states); await Write("audit-assertions.json", audits);
        await Write("findings-summary.json", new { reviewState = "REQUIRES_PRIVATE_TRIAGE", confirmedFindings = (int?)null, failedScenarios = results.Where(r => r.Outcome == "FAIL").Select(r => r.Id), errors = results.Where(r => r.Outcome == "ERROR").Select(r => r.Id) });
        await File.WriteAllTextAsync(Path.Combine(output, "knowledge-base-draft.md"), $"# Provider Access security review draft\n\nSource `{source}`, dirty: {dirty}.\n\n" + string.Join("\n", results.Select(r => $"- `{r.Id}`: **{r.Outcome}**")) + "\n\nUnsupported: " + string.Join(", ", unsupported) + ".\n\nPrivate review required. No production certification. See manifest, observations, state and audit assertions; sensitivity evidence is a separate packet.\n");
        await File.WriteAllTextAsync(Path.Combine(output, "reproduce.md"), "See docs/security-challenge/PROVIDER-ACCESS.md at source `" + source + "`. Rebuild services and runner with .NET 8.\n");
        await File.WriteAllLinesAsync(Path.Combine(output, "artifact-checksums.txt"), Directory.GetFiles(output).Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p)) + "  " + Path.GetFileName(p)));
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.GetFiles(output)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"Provider Access evidence: {output}; PASS={results.Count(r => r.Outcome == "PASS")}, FAIL={results.Count(r => r.Outcome == "FAIL")}, ERROR={results.Count(r => r.Outcome == "ERROR")}, INCONCLUSIVE={results.Count(r => r.Outcome == "INCONCLUSIVE")}");
        return EvidencePolicy.IsCompletePass(completed, results, ScenarioIds) ? 0 : 1;
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
