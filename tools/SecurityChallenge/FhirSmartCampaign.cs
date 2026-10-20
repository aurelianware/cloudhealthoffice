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

public static class FhirSmartCampaign
{
    public static IReadOnlyList<string> ScenarioIds { get; } = Array.AsReadOnly(new[]
    {
        "identity.valid-control", "identity.anonymous", "identity.malformed", "identity.wrong-issuer", "identity.wrong-audience",
        "identity.expired", "identity.future", "identity.bad-signature", "identity.unsigned", "identity.second-issuer-control",
        "identity.issuer-key-confusion", "identity.issuer-audience-confusion", "identity.issuer-tenant-confusion", "identity.header-conflict",
        "scope.unrelated", "scope.write-only", "scope.lowercase-route", "scope.scp-control", "scope.missing-patient",
        "patient.foreign-id", "patient.foreign-query", "patient.foreign-subject", "patient.foreign-beneficiary",
        "patient.search-control", "patient.foreign-id-search", "patient.pagination-confinement",
        "tenant.a.shared-id", "tenant.b.shared-id", "tenant.b.foreign-id", "tenant.a.search", "tenant.b.search",
        "cho.reader-control", "cho.permission-denial", "cho.bulk-denial", "bulk.patient-denial", "bulk.scope-denial",
        "bulk.create-control", "bulk.own-status", "bulk.foreign-status", "bulk.foreign-cancel", "bulk.owner-state-unchanged", "upstream.audit"
    });

    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4 || !File.Exists(args[1]) || !File.Exists(args[2]) || Path.Exists(args[3]))
        { Console.Error.WriteLine("Usage: SecurityChallenge fhir-smart <fhir-service.dll> <member-service.dll> <new-evidence-directory>"); return 2; }
        var output = Path.GetFullPath(args[3]);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<CaseResult>(); var observations = new List<object>(); var states = new List<object>(); var audits = new List<object>();
        var started = DateTimeOffset.UtcNow; var runId = Guid.NewGuid().ToString("N");
        var source = Git("rev-parse", "HEAD"); var dirty = Git("status", "--porcelain").Length != 0;
        var binaries = args.Skip(1).Take(2).SelectMany(assembly => Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(assembly))!)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) || p.EndsWith(".deps.json", StringComparison.Ordinal) || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(p => new { service = Path.GetFileNameWithoutExtension(assembly), file = Path.GetFileName(p), sha256 = Hash(File.ReadAllBytes(p)) })).ToArray();
        var active = "lab.fixtures"; var completed = false; string? mongoVersion = null;
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
            foreach (var fixture in new[] { (Payer: "a", Id: "shared-member"), (Payer: "a", Id: "a-only-member"), (Payer: "b", Id: "shared-member") })
                await members.InsertOneAsync(new BsonDocument
                {
                    { "_id", Guid.NewGuid().ToString() }, { "TenantId", "synthetic-payer-" + fixture.Payer }, { "MemberId", fixture.Id },
                    { "FirstName", "Synthetic" }, { "LastName", "CANARY-" + fixture.Payer + "-" + fixture.Id },
                    { "DateOfBirth", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, { "Gender", "U" },
                    { "IsSubscriber", true }, { "GroupNumber", "SYNTHETIC" }, { "EffectiveDate", DateTime.UtcNow.Date },
                    { "CreatedDate", DateTime.UtcNow }, { "LastUpdatedDate", DateTime.UtcNow }, { "IsDraft", false }
                });
            var tokenA = issuerA.Token(); var tokenB = issuerB.Token(tenant: "synthetic-payer-b", audience: "second-fhir-api");
            var staffA = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-staff", "synthetic-payer-a", [], ["members:read"]);
            var staffB = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-staff", "synthetic-payer-b", [], ["members:read"]);
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
            async Task Case(string id, Func<Task<bool>> test, string? prerequisite = "identity.valid-control")
            {
                active = id;
                if (prerequisite != null && !EvidencePolicy.PositiveControlPassed(results, prerequisite))
                { results.Add(new(id, "INCONCLUSIVE", "Positive control unavailable: " + prerequisite)); return; }
                try { results.Add(new(id, await test() ? "PASS" : "FAIL", "See FHIR-SMART catalog v1")); }
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
            await Case("identity.valid-control", () => Read(own, tokenA, "CANARY-a-shared-member"), null);
            await Case("identity.second-issuer-control", () => Read(own, tokenB, "CANARY-b-shared-member"), null);
            foreach (var invalid in new (string Id, string? Token)[]
            {
                ("anonymous", null), ("malformed", "synthetic.invalid.jwt"), ("wrong-issuer", issuerA.Token(issuer: issuerA.Issuer + "/untrusted")),
                ("wrong-audience", issuerA.Token(audience: "unrelated-api")), ("expired", issuerA.Token(expiresMinutes: -2)),
                ("future", issuerA.Token(notBeforeMinutes: 2)), ("bad-signature", issuerA.Token(wrongKey: true)), ("unsigned", issuerA.Token(unsigned: true)),
                ("issuer-key-confusion", issuerB.Token(tenant: "synthetic-payer-a", issuer: issuerA.Issuer)),
                ("issuer-audience-confusion", issuerA.Token(audience: "second-fhir-api"))
            }) await Case("identity." + invalid.Id, () => Denied(own, invalid.Token, 401));
            await Case("identity.issuer-tenant-confusion", () => Denied(own, issuerA.Token(tenant: "synthetic-payer-b"), 403));
            await Case("identity.header-conflict", () => Denied(own, tokenA, 403, header: "synthetic-payer-b"));
            await Case("scope.unrelated", () => Denied(own, issuerA.Token(scopes: "patient/Coverage.read"), 403));
            await Case("scope.write-only", () => Denied(own, issuerA.Token(scopes: "patient/Patient.write"), 403));
            await Case("scope.lowercase-route", () => Denied("fhir/r4/patient/shared-member", issuerA.Token(scopes: "patient/Coverage.read"), 403));
            await Case("scope.scp-control", () => Read(own, issuerA.Token(scopeClaim: "scp"), "CANARY-a-shared-member"));
            await Case("scope.missing-patient", () => Denied(own, issuerA.Token(patient: null), 403));
            await Case("patient.foreign-id", () => Denied("fhir/r4/Patient/a-only-member", tokenA, 403));
            foreach (var parameter in new[] { "patient", "subject", "beneficiary" })
                await Case("patient.foreign-" + (parameter == "patient" ? "query" : parameter), () => Denied("fhir/r4/Patient?" + parameter + "=Patient/a-only-member", tokenA, 403));
            await Case("patient.search-control", async () =>
            {
                var r = await Request("fhir/r4/Patient", tokenA);
                using var body = JsonDocument.Parse(r.Body);
                return r.Status == 200 && body.RootElement.GetProperty("total").GetInt32() == 1
                    && body.RootElement.GetProperty("entry").GetArrayLength() == 1
                    && r.Body.Contains("CANARY-a-shared-member") && !r.Body.Contains("a-only-member") && !r.Body.Contains("CANARY-b");
            });
            await Case("patient.foreign-id-search", async () =>
            {
                var r = await Request("fhir/r4/Patient?_id=a-only-member", tokenA);
                using var body = JsonDocument.Parse(r.Body);
                return r.Status == 200 && body.RootElement.GetProperty("total").GetInt32() == 0 && !r.Body.Contains("CANARY-");
            });
            await Case("patient.pagination-confinement", async () =>
            {
                var r = await Request("fhir/r4/Patient?_count=1&_page=1", tokenA);
                using var body = JsonDocument.Parse(r.Body);
                return r.Status == 200 && body.RootElement.GetProperty("total").GetInt32() == 1 && r.Body.Contains("CANARY-a-shared-member") && !r.Body.Contains("a-only-member") && !r.Body.Contains("CANARY-b");
            }, "patient.search-control");
            await Case("tenant.a.shared-id", () => Read(own, tokenA, "CANARY-a-shared-member"));
            await Case("tenant.b.shared-id", () => Read(own, tokenB, "CANARY-b-shared-member"), "identity.second-issuer-control");
            await Case("tenant.b.foreign-id", () => Denied("fhir/r4/Patient/a-only-member", issuerB.Token(tenant: "synthetic-payer-b", patient: "a-only-member", audience: "second-fhir-api"), 404), "identity.second-issuer-control");
            foreach (var payer in new[] { "a", "b" })
                await Case("tenant." + payer + ".search", async () =>
                {
                    var r = await Request("fhir/r4/Patient?_count=1", payer == "a" ? staffA : staffB);
                    using var body = JsonDocument.Parse(r.Body);
                    var total = body.RootElement.GetProperty("total").GetInt32();
                    if (r.Status != 200 || total != (payer == "a" ? 2 : 1) || !r.Body.Contains("CANARY-" + payer) || r.Body.Contains("CANARY-" + (payer == "a" ? "b" : "a"))) return false;
                    if (!body.RootElement.TryGetProperty("link", out var links)) return payer == "b";
                    var next = links.EnumerateArray().FirstOrDefault(l => l.GetProperty("relation").GetString() == "next");
                    if (payer == "b") return next.ValueKind == JsonValueKind.Undefined;
                    var url = next.GetProperty("url").GetString()!;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != fhir.Http.BaseAddress!.GetLeftPart(UriPartial.Authority)) return false;
                    var page = await Request(uri.PathAndQuery, staffA);
                    return page.Status == 200 && page.Body.Contains("CANARY-a") && !page.Body.Contains("CANARY-b");
                }, payer == "a" ? "identity.valid-control" : "identity.second-issuer-control");
            await Case("cho.reader-control", () => Read(own, staffA, "CANARY-a-shared-member"));
            await Case("cho.permission-denial", () => Denied(own, ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-noperms", "synthetic-payer-a", [], []), 403), "cho.reader-control");
            await Case("cho.bulk-denial", () => Denied("fhir/r4/$export", ChoDevelopmentAuth.UserToken("synthetic-payer-a", ChoRolePermissions.TenantAdmin), 403, "POST"));
            await Case("bulk.patient-denial", () => Denied("fhir/r4/$export", issuerA.Token(scopes: "patient/*.*"), 403, "POST"));
            await Case("bulk.scope-denial", () => Denied("fhir/r4/$export", issuerA.Token(patient: null, scopes: "system/Patient.read"), 403, "POST"));
            var backendA = issuerA.Token(patient: null, scopes: "system/$export.read system/$export-status.read system/$export-status.write");
            var backendB = issuerB.Token(tenant: "synthetic-payer-b", patient: null, scopes: "system/$export.read system/$export-status.read system/$export-status.write", audience: "second-fhir-api");
            string? job = null; string? jobBody = null;
            await Case("bulk.create-control", async () =>
            {
                var r = await Request("fhir/r4/$export?_type=Patient", backendA, "POST");
                if (r.Status != 202 || !Uri.TryCreate(r.Location, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != fhir.Http.BaseAddress!.GetLeftPart(UriPartial.Authority)) return false;
                job = uri.PathAndQuery; return true;
            });
            await Case("bulk.own-status", async () => { var r = await Request(job!, backendA); jobBody = r.Body; return r.Status == 200 && r.Body.Contains("Patient"); }, "bulk.create-control");
            await Case("bulk.foreign-status", () => Denied(job!, backendB, 404), "bulk.own-status");
            await Case("bulk.foreign-cancel", () => Denied(job!, backendB, 404, "DELETE"), "bulk.own-status");
            await Case("bulk.owner-state-unchanged", async () =>
            {
                var r = await Request(job!, backendA); var same = r.Status == 200 && r.Body == jobBody;
                states.Add(new { scenarioId = active, ownerJobUnchanged = same }); return same;
            }, "bulk.own-status");
            await Case("upstream.audit", async () =>
            {
                var count = await db.GetCollection<BsonDocument>("RecordAccessAudit").CountDocumentsAsync(new BsonDocument { { "TenantId", "synthetic-payer-a" }, { "ActorId", "fhir-service" }, { "StatusCode", 200 } });
                audits.Add(new { scenarioId = active, tenantBoundFhirServiceReads = count }); return count > 0;
            });
            completed = true;
        }
        catch (Exception e) { Console.Error.WriteLine("Lab ERROR: " + e.GetType().Name); results.Add(new("lab.prerequisites", "ERROR", "Local service, fixture, identity or database setup/cleanup failed")); }
        var unsupported = new[] { "production-identity-and-tls", "member-production-encryption", "authorization-server-grant-pkce-refresh-revocation", "jwks-rotation-and-outage", "provider-attribution-consent", "non-Patient-resource-data", "bulk-data-downloads", "financial-accounting", "per-tenant-databases", "deployed-ingress", "full-log-phi-leakage" };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        async Task Write(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        await Write("manifest.json", new { schemaVersion = "1.0", campaign = "fhir-smart-v1", runId, started, finished = DateTimeOffset.UtcNow, completed, sourceCommit = source, sourceDirty = dirty, binaries, mongoVersion, runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fixtureVersion = "fhir-patient-synthetic-v1", profile = "loopback-fhir-testing-member-development-two-rsa-issuers", tenantStorage = "shared-database-row-filtering", memberSeed = "direct-mongo-existing-state", memberEncryption = "development-noop", identity = "ephemeral-rsa-jwks-http-loopback", bulkExport = "in-process-job-metadata-only", publication = "REQUIRES_REVIEW" });
        await File.WriteAllLinesAsync(Path.Combine(output, "scenario-results.jsonl"), results.Select(r => JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await Write("coverage.json", new { planned = ScenarioIds.Count, reported = results.Count(r => r.Id != "lab.prerequisites"), executed = EvidencePolicy.ExecutedCount(results), pass = results.Count(r => r.Outcome == "PASS"), fail = results.Count(r => r.Outcome == "FAIL"), error = results.Count(r => r.Outcome == "ERROR"), inconclusive = results.Count(r => r.Outcome == "INCONCLUSIVE"), unsupported });
        await Write("http-observations.json", observations); await Write("state-assertions.json", states); await Write("audit-assertions.json", audits);
        await Write("findings-summary.json", new { reviewState = "REQUIRES_PRIVATE_TRIAGE", confirmedFindings = (int?)null, failedScenarios = results.Where(r => r.Outcome == "FAIL").Select(r => r.Id), errors = results.Where(r => r.Outcome == "ERROR").Select(r => r.Id) });
        await File.WriteAllTextAsync(Path.Combine(output, "knowledge-base-draft.md"), $"# FHIR/SMART security review draft\n\nSource `{source}`, dirty: {dirty}.\n\n" + string.Join("\n", results.Select(r => $"- `{r.Id}`: **{r.Outcome}**")) + "\n\nUnsupported: " + string.Join(", ", unsupported) + ".\n\nPrivate review required. No production certification. See manifest, observations, state and audit assertions; sensitivity evidence is a separate packet.\n");
        await File.WriteAllTextAsync(Path.Combine(output, "reproduce.md"), "See docs/security-challenge/FHIR-SMART.md at source `" + source + "`. Rebuild services and runner with .NET 8.\n");
        await File.WriteAllLinesAsync(Path.Combine(output, "artifact-checksums.txt"), Directory.GetFiles(output).Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p)) + "  " + Path.GetFileName(p)));
        Console.WriteLine($"FHIR/SMART evidence: {output}; PASS={results.Count(r => r.Outcome == "PASS")}, FAIL={results.Count(r => r.Outcome == "FAIL")}, ERROR={results.Count(r => r.Outcome == "ERROR")}, INCONCLUSIVE={results.Count(r => r.Outcome == "INCONCLUSIVE")}");
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
