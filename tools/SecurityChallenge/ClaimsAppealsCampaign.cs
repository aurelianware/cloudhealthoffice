using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SecurityChallenge;

public static class ClaimsAppealsCampaign
{
    public static IReadOnlyList<string> ScenarioIds { get; } = Array.AsReadOnly(
        (from service in new[] { "claims", "appeals" }
         from payer in new[] { "a", "b" }
         from operation in new[] { "own-id", "foreign-id", "number", "search", "foreign-write" }
         select $"{service}.{payer}.{operation}").Concat(new[]
         {
             "claims.reader-control", "claims.reader-write", "claims.worker-control", "claims.worker-void", "claims.worker-delete", "claims.worker-status-void",
             "claims.inject-approved", "claims.inject-paid", "claims.inject-denied", "claims.anonymous", "claims.audit",
             "appeals.reader-control", "appeals.reader-submit", "appeals.reader-note", "appeals.writer-submit", "appeals.writer-note",
             "appeals.note-actor", "appeals.note-replay", "appeals.begin-review-control", "appeals.admin-replay-control", "appeals.invalid-transition", "appeals.withdraw", "appeals.closed-reopen",
             "appeals.writer-replay-admin", "appeals.create-overposting", "appeals.anonymous", "appeals.audit"
         }).ToArray());

    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4 || !File.Exists(args[1]) || !File.Exists(args[2]) || Path.Exists(args[3]))
        {
            Console.Error.WriteLine("Usage: SecurityChallenge claims-appeals <claims-service.dll> <appeals-service.dll> <new-evidence-directory>");
            return 2;
        }
        var output = Path.GetFullPath(args[3]);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<CaseResult>();
        var observations = new List<object>();
        var states = new List<object>();
        var audits = new List<object>();
        var started = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var source = Git("rev-parse", "HEAD");
        var dirty = Git("status", "--porcelain").Length != 0;
        var binaries = args.Skip(1).Take(2).SelectMany(assembly => Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(assembly))!)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) || p.EndsWith(".deps.json", StringComparison.Ordinal) || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(p => new { service = Path.GetFileNameWithoutExtension(assembly), file = Path.GetFileName(p), sha256 = Hash(File.ReadAllBytes(p)) })).ToArray();
        string? mongoVersion = null;
        var active = "lab.fixtures";
        var completed = false;
        try
        {
            using var mongo = MongoRunner.Run(new MongoRunnerOptions { Version = MongoVersion.V7, Edition = MongoEdition.Community, ConnectionTimeout = TimeSpan.FromSeconds(30) });
            using var client = new MongoClient(mongo.ConnectionString);
            var db = client.GetDatabase("claims_appeals_security_" + runId);
            mongoVersion = (await db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1)))["version"].AsString;
            await using var claims = new LocalService(args[1], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, false);
            await using var appeals = new LocalService(args[2], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, true);
            await claims.Ready(); await appeals.Ready();
            var services = new Dictionary<string, LocalService> { ["claims"] = claims, ["appeals"] = appeals };
            var tokens = new Dictionary<string, string>
            {
                ["a"] = ChoDevelopmentAuth.UserToken("synthetic-payer-a", ChoRolePermissions.TenantAdmin),
                ["b"] = ChoDevelopmentAuth.UserToken("synthetic-payer-b", ChoRolePermissions.TenantAdmin),
                ["reader"] = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-reader", "synthetic-payer-a", [], ["claims:read", "appeals:read"]),
                ["worker"] = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-worker", "synthetic-payer-a", [], ["claims:read", "claims:work", "appeals:read", "appeals:write"])
            };
            async Task<(int Status, string Body)> Request(string service, string method, string path, string? actor, object? body = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), "api/" + service + path);
                if (actor != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens[actor]);
                if (body != null) request.Content = JsonContent.Create(body);
                using var response = await services[service].Http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                observations.Add(new { scenarioId = active, service, method, path, status = (int)response.StatusCode, bodySha256 = Hash(Encoding.UTF8.GetBytes(text)), payerACanary = text.Contains("CANARY-a"), payerBCanary = text.Contains("CANARY-b") });
                return ((int)response.StatusCode, text);
            }
            var claimIds = new Dictionary<string, string>();
            var appealIds = new Dictionary<string, string>();
            var claimsCollection = db.GetCollection<BsonDocument>("Claims");
            foreach (var payer in new[] { "a", "b" })
            {
                var id = Guid.NewGuid().ToString(); claimIds[payer] = id;
                // Existing-state fixtures, not evidence of intake/adjudication. Same natural IDs across tenants.
                await claimsCollection.InsertOneAsync(new BsonDocument
                {
                    { "_id", id }, { "TenantId", "synthetic-payer-" + payer }, { "ClaimNumber", "SYNTHETIC-SHARED-CLAIM" },
                    { "MemberId", "synthetic-member" }, { "PatientLastName", "CANARY-" + payer }, { "Status", 2 },
                    { "ClaimType", 1 }, { "TotalChargeAmount", 100 }, { "ServiceDateFrom", DateTime.UtcNow.Date },
                    { "CreatedDate", DateTime.UtcNow }, { "LastUpdatedDate", DateTime.UtcNow }
                });
                var created = await Request("appeals", "POST", "", payer, new
                {
                    appealNumber = "SYNTHETIC-SHARED-APPEAL", claimId = id, claimNumber = "SYNTHETIC-SHARED-CLAIM",
                    memberId = "synthetic-member", patientName = "CANARY-" + payer, providerNPI = "1234567890",
                    appealReason = "Synthetic only", lineOfBusiness = "Commercial", tenantId = "injected-tenant",
                    createdBy = "injected-actor", status = "Closed"
                });
                if (created.Status != 201) throw new InvalidOperationException("Appeal fixture create rejected");
                using var body = JsonDocument.Parse(created.Body);
                appealIds[payer] = body.RootElement.GetProperty("id").GetString()!;
            }
            async Task<string> Snapshot()
            {
                var collections = (await (await db.ListCollectionNamesAsync()).ToListAsync())
                    .Where(n => n != "ClaimAccessAudit" && n != "RecordAccessAudit" && !n.StartsWith("system.", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
                var content = new StringBuilder();
                foreach (var name in collections)
                {
                    var docs = await db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                    if (docs.Count == 0) continue;
                    content.Append(name).AppendJoin('\n', docs.Select(d => d.ToJson()).Order(StringComparer.Ordinal));
                }
                var hash = Hash(Encoding.UTF8.GetBytes(content.ToString()));
                states.Add(new { scenarioId = active, sequence = states.Count, sha256 = hash });
                return hash;
            }
            async Task Case(string id, Func<Task<bool>> test, string? prerequisite = null)
            {
                active = id;
                if (prerequisite != null && !EvidencePolicy.PositiveControlPassed(results, prerequisite))
                { results.Add(new(id, "INCONCLUSIVE", "Positive control unavailable: " + prerequisite)); return; }
                try { results.Add(new(id, await test() ? "PASS" : "FAIL", "See claims-appeals catalog v1")); }
                catch { results.Add(new(id, "ERROR", "Execution failed; inspect restricted diagnostics")); }
            }
            async Task<bool> Denied(string service, string method, string path, string? actor, object? body, int expected)
            {
                var before = await Snapshot();
                var response = await Request(service, method, path, actor, body);
                return response.Status == expected && !response.Body.Contains("CANARY-") && before == await Snapshot();
            }
            var claimPath = "/" + claimIds["a"];
            var appealPath = "/" + appealIds["a"];
            await Case("claims.worker-control", async () =>
            {
                var r = await Request("claims", "PUT", claimPath + "/status", "worker", new { status = 3 });
                var doc = await claimsCollection.Find(new BsonDocument("_id", claimIds["a"])).SingleAsync();
                var ok = doc["Status"] == 3 || doc["Status"] == "InAdjudication";
                states.Add(new { scenarioId = active, statusIsInAdjudication = ok });
                return r.Status == 200 && ok;
            });
            var appealCollection = db.GetCollection<BsonDocument>("Appeals");
            async Task<BsonDocument> Appeal() => await appealCollection.Find(new BsonDocument("_id", appealIds["a"])).SingleAsync();
            await Case("appeals.create-overposting", async () =>
            {
                var doc = await Appeal();
                return doc["TenantId"] == "synthetic-payer-a" && doc["CreatedBy"] == "dev-user-synthetic-payer-a" && doc["Status"] == "Draft";
            });
            await Case("appeals.invalid-transition", () => Denied("appeals", "POST", appealPath + "/begin-review", "worker", new { }, 409));
            await Case("appeals.writer-submit", async () =>
            {
                var r = await Request("appeals", "POST", appealPath + "/submit", "worker", new { });
                return r.Status == 200 && (await Appeal())["Status"] == "Submitted";
            });
            foreach (var service in new[] { "claims", "appeals" })
            foreach (var payer in new[] { "a", "b" })
            {
                var ids = service == "claims" ? claimIds : appealIds;
                var other = payer == "a" ? "b" : "a";
                var prefix = service + "." + payer + ".";
                await Case(prefix + "own-id", async () =>
                {
                    var r = await Request(service, "GET", "/" + ids[payer], payer);
                    return r.Status == 200 && r.Body.Contains("CANARY-" + payer) && !r.Body.Contains("CANARY-" + other);
                });
                await Case(prefix + "foreign-id", () => Denied(service, "GET", "/" + ids[other], payer, null, 404), prefix + "own-id");
                await Case(prefix + "number", async () =>
                {
                    var number = service == "claims" ? "SYNTHETIC-SHARED-CLAIM" : "SYNTHETIC-SHARED-APPEAL";
                    var r = await Request(service, "GET", "/number/" + number, payer);
                    return r.Status == 200 && r.Body.Contains("CANARY-" + payer) && !r.Body.Contains("CANARY-" + other);
                }, prefix + "own-id");
                await Case(prefix + "search", async () =>
                {
                    var r = await Request(service, "GET", "/search?memberId=synthetic-member", payer);
                    return r.Status == 200 && r.Body.Contains("CANARY-" + payer) && !r.Body.Contains("CANARY-" + other);
                }, prefix + "own-id");
                await Case(prefix + "foreign-write", () => service == "claims"
                    ? Denied(service, "PUT", "/" + ids[other] + "/status", payer, new { status = 3 }, 404)
                    : Denied(service, "POST", "/" + ids[other] + "/submit", payer, new { }, 404), service == "claims" ? "claims.worker-control" : "appeals.writer-submit");
            }
            async Task<bool> OwnRead(string service)
            {
                var r = await Request(service, "GET", "/" + (service == "claims" ? claimIds["a"] : appealIds["a"]), "reader");
                return r.Status == 200 && r.Body.Contains("CANARY-a") && !r.Body.Contains("CANARY-b");
            }
            await Case("claims.reader-control", () => OwnRead("claims"));
            await Case("claims.reader-write", () => Denied("claims", "PUT", claimPath + "/status", "reader", new { status = 3 }, 403), "claims.reader-control");
            await Case("claims.worker-void", () => Denied("claims", "POST", claimPath + "/void", "worker", new { reason = "synthetic" }, 403), "claims.worker-control");
            await Case("claims.worker-delete", () => Denied("claims", "DELETE", claimPath, "worker", null, 403), "claims.worker-control");
            await Case("claims.worker-status-void", () => Denied("claims", "PUT", claimPath + "/status", "worker", new { status = 8 }, 403), "claims.worker-control");
            foreach (var outcome in new[] { (Name: "approved", Value: 5), (Name: "paid", Value: 7), (Name: "denied", Value: 6) })
                await Case("claims.inject-" + outcome.Name, () => Denied("claims", "PUT", claimPath + "/status", "a", new { status = outcome.Value }, 422), "claims.worker-control");
            await Case("claims.anonymous", () => Denied("claims", "GET", claimPath, null, null, 401));
            await Case("appeals.reader-control", () => OwnRead("appeals"));
            await Case("appeals.reader-submit", () => Denied("appeals", "POST", appealPath + "/submit", "reader", new { }, 403), "appeals.reader-control");
            await Case("appeals.reader-note", () => Denied("appeals", "POST", appealPath + "/notes", "reader", new { noteText = "synthetic" }, 403), "appeals.reader-control");
            await Case("appeals.begin-review-control", async () =>
            {
                var r = await Request("appeals", "POST", appealPath + "/begin-review", "worker", new { });
                return r.Status == 200 && (await Appeal())["Status"] == "InReview";
            }, "appeals.writer-submit");
            var note = new { noteText = "SYNTHETIC-NOTE", createdBy = "injected-actor", eventId = "synthetic-note-once" };
            await Case("appeals.writer-note", async () =>
            {
                var r = await Request("appeals", "POST", appealPath + "/notes", "worker", note);
                return r.Status == 200 && (await Appeal())["Notes"].AsBsonArray.Count == 1;
            });
            await Case("appeals.note-actor", async () => (await Appeal())["Notes"].AsBsonArray.Single()["CreatedBy"] == "synthetic-worker", "appeals.writer-note");
            await Case("appeals.note-replay", async () =>
            {
                var before = await Snapshot();
                var r = await Request("appeals", "POST", appealPath + "/notes", "worker", note);
                return r.Status == 200 && before == await Snapshot();
            }, "appeals.writer-note");
            await Case("appeals.admin-replay-control", async () =>
            {
                var r = await Request("appeals", "POST", appealPath + "/outbox/replay", "a");
                return r.Status == 200;
            });
            await Case("appeals.writer-replay-admin", () => Denied("appeals", "POST", appealPath + "/outbox/replay", "worker", new { }, 403), "appeals.admin-replay-control");
            await Case("appeals.withdraw", async () =>
            {
                var r = await Request("appeals", "POST", appealPath + "/withdraw", "worker", new { });
                return r.Status == 200 && (await Appeal())["Status"] == "Closed";
            }, "appeals.writer-submit");
            await Case("appeals.closed-reopen", () => Denied("appeals", "POST", appealPath + "/submit", "worker", new { }, 409), "appeals.withdraw");
            await Case("appeals.anonymous", () => Denied("appeals", "GET", appealPath, null, null, 401));
            foreach (var service in new[] { "claims", "appeals" })
                await Case(service + ".audit", async () =>
                {
                    var filter = new BsonDocument { { "TenantId", "synthetic-payer-a" }, { "ActorId", "synthetic-reader" }, { "StatusCode", 200 } };
                    var count = await db.GetCollection<BsonDocument>(service == "claims" ? "ClaimAccessAudit" : "RecordAccessAudit").CountDocumentsAsync(filter);
                    audits.Add(new { scenarioId = active, matchingActorTenantReadEvents = count });
                    return count > 0;
                }, service + ".reader-control");
            completed = true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Lab ERROR: " + exception.GetType().Name);
            results.Add(new("lab.prerequisites", "ERROR", "Local service, fixture or database setup/cleanup failed"));
        }
        var unsupported = new[] { "per-tenant-database-mode", "claims-intake-adjudication", "claims-adjustments-replacements-finalized-state", "cross-service-claim-link-validation", "payment-and-accumulator-accounting", "appeals-production-encryption", "production-identity", "live-kafka-delivery", "self-review-separation", "attachments", "fhir", "mutation-sensitivity" };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        async Task Write(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        await Write("manifest.json", new { schemaVersion = "1.0", campaign = "claims-appeals-v1", runId, started, finished = DateTimeOffset.UtcNow, completed, sourceCommit = source, sourceDirty = dirty, binaries, mongoVersion, runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fixtureVersion = "claims-appeals-synthetic-v1", tenantStorage = "shared-database-row-filtering", claimsSeed = "direct-mongo-existing-state", appealsEncryption = "development-noop", kafkaDelivery = "disabled", profile = "loopback-claims-testing-appeals-development", publication = "REQUIRES_REVIEW" });
        await File.WriteAllLinesAsync(Path.Combine(output, "scenario-results.jsonl"), results.Select(r => JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await Write("coverage.json", new { planned = ScenarioIds.Count, reported = results.Count(r => r.Id != "lab.prerequisites"), executed = EvidencePolicy.ExecutedCount(results), pass = results.Count(r => r.Outcome == "PASS"), fail = results.Count(r => r.Outcome == "FAIL"), error = results.Count(r => r.Outcome == "ERROR"), inconclusive = results.Count(r => r.Outcome == "INCONCLUSIVE"), unsupported });
        await Write("http-observations.json", observations); await Write("state-assertions.json", states); await Write("audit-assertions.json", audits);
        await Write("findings-summary.json", new { reviewState = "REQUIRES_PRIVATE_TRIAGE", confirmedFindings = (int?)null, failedScenarios = results.Where(r => r.Outcome == "FAIL").Select(r => r.Id), errors = results.Where(r => r.Outcome == "ERROR").Select(r => r.Id), inconclusive = results.Where(r => r.Outcome == "INCONCLUSIVE").Select(r => r.Id) });
        await File.WriteAllTextAsync(Path.Combine(output, "knowledge-base-draft.md"), $"# Claims and appeals security review draft\n\nRun `{runId}`, source `{source}`, dirty: {dirty}.\n\n" + string.Join("\n", results.Select(r => $"- `{r.Id}`: **{r.Outcome}**")) + "\n\nEvidence: [manifest](manifest.json), [results](scenario-results.jsonl), [state](state-assertions.json), [audit](audit-assertions.json), [coverage](coverage.json).\n\nUnsupported: " + string.Join(", ", unsupported) + ".\n\nPrivate review required before publication; no compliance or whole-platform assurance claim.\n");
        await File.WriteAllTextAsync(Path.Combine(output, "reproduce.md"), "See docs/security-challenge/CLAIMS-APPEALS.md at source `" + source + "`. Rebuild both services and the runner with .NET 8; IDs vary per run.\n");
        await File.WriteAllLinesAsync(Path.Combine(output, "artifact-checksums.txt"), Directory.GetFiles(output).Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p)) + "  " + Path.GetFileName(p)));
        Console.WriteLine($"Claims/appeals evidence: {output}; PASS={results.Count(r => r.Outcome == "PASS")}, FAIL={results.Count(r => r.Outcome == "FAIL")}, ERROR={results.Count(r => r.Outcome == "ERROR")}, INCONCLUSIVE={results.Count(r => r.Outcome == "INCONCLUSIVE")}");
        return EvidencePolicy.IsCompletePass(completed, results, ScenarioIds) ? 0 : 1;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git unavailable");
        var value = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Run inside source checkout");
        return value.Trim();
    }
}
