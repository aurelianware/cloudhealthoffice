using SecurityChallenge;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using EphemeralMongo;
using MongoDB.Bson;
using MongoDB.Driver;

// This runner accepts no remote target. It owns the service and database lifecycle.
if (args.Length != 2 || !File.Exists(args[0]) || Directory.Exists(args[1]))
{
    Console.Error.WriteLine("Usage: SecurityChallenge <authorization-service.dll> <new-evidence-directory>");
    return 2;
}
var serviceDll = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var results = new List<CaseResult>();
var runId = Guid.NewGuid().ToString("N");
var started = DateTimeOffset.UtcNow;
var serviceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(serviceDll))).ToLowerInvariant();
var phase = "mongo-startup";
var completed = false;
var observations = new List<object>();
var stateProofs = new List<object>();
var auditProofs = new List<object>();
var activeCase = "lab.fixtures";
var sourceCommit = ReadGit("rev-parse", "HEAD");
var sourceDirty = ReadGit("status", "--porcelain").Length != 0;
string? mongoVersion = null;
var binaryFingerprints = Directory.GetFiles(Path.GetDirectoryName(serviceDll)!)
    .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) || p.EndsWith(".deps.json", StringComparison.Ordinal) || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
    .Order(StringComparer.Ordinal).Select(p => new { file = Path.GetFileName(p), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant() }).ToArray();
try
{
    using var mongo = MongoRunner.Run(new MongoRunnerOptions
    {
        Version = MongoVersion.V7, Edition = MongoEdition.Community,
        ConnectionTimeout = TimeSpan.FromSeconds(30)
    });
    using var mongoClient = new MongoClient(mongo.ConnectionString);
    var database = mongoClient.GetDatabase("security_challenge_" + runId);
    var collection = database.GetCollection<BsonDocument>("Authorizations");
    var buildInfo = await database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
    mongoVersion = buildInfo["version"].AsString;
    using var socket = new TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    var port = ((IPEndPoint)socket.LocalEndpoint).Port;
    socket.Stop(); // Kestrel binds next; a collision is an ERROR, never a PASS.
    var address = new Uri($"http://127.0.0.1:{port}");
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = Path.GetDirectoryName(serviceDll)!,
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
    };
    start.ArgumentList.Add(serviceDll);
    // Do not inherit operator credentials, remote databases, service URLs or auth configuration.
    var inherited = start.Environment.ToDictionary(x => x.Key, x => x.Value);
    start.Environment.Clear();
    foreach (var key in new[] { "PATH", "HOME", "TMPDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
        if (inherited.TryGetValue(key, out var value)) start.Environment[key] = value;
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
    start.Environment["CHO_TESTING"] = "1";
    start.Environment["ASPNETCORE_URLS"] = address.ToString();
    start.Environment["MongoDb__ConnectionString"] = mongo.ConnectionString;
    start.Environment["MongoDb__DatabaseName"] = database.DatabaseNamespace.DatabaseName;
    start.Environment["Kafka__BootstrapServers"] = "";
    start.Environment["Observability__OtlpEndpoint"] = "";
    start.Environment["Services__ClaimsServiceUrl"] = "http://127.0.0.1:1/";
    start.Environment["Services__RfaiServiceUrl"] = "http://127.0.0.1:1/";
    foreach (var setting in ChoDevelopmentAuth.Configuration("authorization-service"))
        start.Environment[setting.Key.Replace(":", "__")] = setting.Value;
    phase = "service-startup";
    using var service = Process.Start(start) ?? throw new InvalidOperationException("Service failed to start");
    // Drain logs without publishing token/exception-bearing raw output.
    service.OutputDataReceived += (_, e) => { if (Environment.GetEnvironmentVariable("CHO_SECURITY_LAB_DEBUG") == "1") Console.Error.WriteLine(e.Data); };
    service.ErrorDataReceived += (_, e) => { if (Environment.GetEnvironmentVariable("CHO_SECURITY_LAB_DEBUG") == "1") Console.Error.WriteLine(e.Data); };
    service.BeginOutputReadLine(); service.BeginErrorReadLine();
    try
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
        var ready = false;
        for (var attempt = 0; attempt < 100 && !service.HasExited; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("api/authorizations/backend-status");
                ready = response.IsSuccessStatusCode;
                if (ready) break;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200);
        }
        if (!ready) throw new InvalidOperationException("Local service readiness failed");
        var tokens = new Dictionary<string, string>
        {
            ["a"] = ChoDevelopmentAuth.UserToken("synthetic-payer-a", ChoRolePermissions.TenantAdmin),
            ["b"] = ChoDevelopmentAuth.UserToken("synthetic-payer-b", ChoRolePermissions.TenantAdmin),
            ["reader"] = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("synthetic-reader", "synthetic-payer-a", [], ["authorizations:read"])
        };
        async Task<(int Status, string Body)> Request(string method, string path, string? token, object? body = null, string? tenant = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (tenant != null) request.Headers.Add("X-Tenant-ID", tenant);
            if (body != null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            observations.Add(new { scenarioId = activeCase, method, path, status = (int)response.StatusCode,
                bodySha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                payerACanary = text.Contains("CANARY-a"), payerBCanary = text.Contains("CANARY-b") });
            return ((int)response.StatusCode, text);
        }
        object Fixture(string payer) => new
        {
            authorizationNumber = "SYNTHETIC-SHARED-NUMBER", memberId = "synthetic-dependent",
            patientFirstName = "Synthetic", patientLastName = "CANARY-" + payer,
            patientDateOfBirth = "2000-01-01T00:00:00Z", requestingProviderNPI = "1234567890",
            serviceTypeCode = "1", requestedServiceDateFrom = "2099-01-01T00:00:00Z",
            requestedServices = new[] { new { lineNumber = 1, procedureCode = "99213", requestedUnits = 1 } },
            tenantId = "injected-tenant", createdBy = "injected-actor", status = "Approved", approvedUnits = 999
        };
        phase = "fixture-creation";
        var ids = new Dictionary<string, string>();
        foreach (var payer in new[] { "a", "b" })
        {
            var response = await Request("POST", "api/authorizations", tokens[payer], Fixture(payer));
            if (response.Status != 201) throw new InvalidOperationException("Synthetic fixture creation failed: " + response.Status);
            ids[payer] = JsonDocument.Parse(response.Body).RootElement.GetProperty("id").GetString()!;
        }
        async Task<string> Snapshot()
        {
            var documents = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            var canonical = string.Join("\n", documents.OrderBy(x => x["_id"].ToString()).Select(x => x.ToJson()));
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            stateProofs.Add(new { scenarioId = activeCase, sequence = stateProofs.Count, documentCount = documents.Count, sha256 = hash });
            return hash;
        }
        async Task Case(string id, Func<Task<bool>> assertion)
        {
            activeCase = id;
            try { results.Add(new(id, await assertion() ? "PASS" : "FAIL", "Scenario-specific predicate; see versioned catalog")); }
            catch { results.Add(new(id, "ERROR", "Assertion execution failed; restricted diagnostics required")); }
        }
        phase = "scenario-execution";
        foreach (var payer in new[] { "a", "b" })
        {
            var other = payer == "a" ? "b" : "a";
            await Case($"isolation.{payer}.own-id", async () =>
            {
                var r = await Request("GET", "api/authorizations/" + ids[payer], tokens[payer]);
                return r.Status == 200 && r.Body.Contains("CANARY-" + payer) && !r.Body.Contains("CANARY-" + other);
            });
            await Case($"isolation.{payer}.foreign-id", async () =>
            {
                var before = await Snapshot();
                var r = await Request("GET", "api/authorizations/" + ids[other], tokens[payer]);
                return r.Status == 404 && !r.Body.Contains("CANARY-") && before == await Snapshot();
            });
            await Case($"isolation.{payer}.overlapping-number", async () =>
            {
                var r = await Request("GET", "api/authorizations/number/SYNTHETIC-SHARED-NUMBER", tokens[payer]);
                return r.Status == 200 && r.Body.Contains("CANARY-" + payer) && !r.Body.Contains("CANARY-" + other);
            });
            await Case($"isolation.{payer}.search", async () =>
            {
                var r = await Request("GET", "api/authorizations/search?memberId=synthetic-dependent", tokens[payer]);
                return r.Status == 200 && r.Body.Contains("CANARY-" + payer) && !r.Body.Contains("CANARY-" + other);
            });
            await Case($"integrity.{payer}.create-overposting", async () =>
            {
                var document = await collection.Find(new BsonDocument("_id", ids[payer])).SingleAsync();
                return document["TenantId"] == "synthetic-payer-" + payer
                    && document["CreatedBy"] != "injected-actor"
                    && document["ApprovedUnits"].IsBsonNull
                    && document["Status"].AsInt32 == 1;
            });
        }
        foreach (var test in new[] { ("anonymous", (string?)null, 401), ("malformed", "not-a-token", 401), ("reader", tokens["reader"], 403) })
            await Case("identity.write." + test.Item1, async () =>
            {
                var before = await Snapshot();
                var r = await Request("POST", "api/authorizations", test.Item2, Fixture("attempt"));
                return r.Status == test.Item3 && !r.Body.Contains("CANARY-") && before == await Snapshot();
            });
        await Case("privilege.reader-decision", async () =>
        {
            var before = await Snapshot();
            var r = await Request("PUT", $"api/authorizations/{ids["a"]}/status", tokens["reader"], new { status = "Cancelled" });
            return r.Status == 403 && before == await Snapshot();
        });
        await Case("identity.reader-positive-control", async () =>
        {
            var r = await Request("GET", "api/authorizations/" + ids["a"], tokens["reader"]);
            return r.Status == 200 && r.Body.Contains("CANARY-a") && !r.Body.Contains("CANARY-b");
        });
        await Case("identity.tenant-header-conflict", async () =>
        {
            var before = await Snapshot();
            var r = await Request("GET", "api/authorizations/" + ids["b"], tokens["a"], tenant: "synthetic-payer-b");
            return r.Status == 403 && !r.Body.Contains("CANARY-") && before == await Snapshot();
        });
        foreach (var bad in new[] { ("issuer", "untrusted-issuer", ChoDevelopmentAuth.Audience), ("audience", ChoDevelopmentAuth.UserIssuer, "untrusted-audience") })
            await Case("identity.wrong-" + bad.Item1, async () =>
            {
                var token = ChoTokenIssuer.FromKeys(bad.Item2, bad.Item3, null, ChoDevelopmentAuth.SymmetricKey, TimeSpan.FromMinutes(5))
                    .IssueUserToken("synthetic-untrusted", "synthetic-payer-a", [ChoRolePermissions.TenantAdmin]);
                var before = await Snapshot();
                var r = await Request("GET", "api/authorizations/" + ids["a"], token);
                return r.Status == 401 && !r.Body.Contains("CANARY-") && before == await Snapshot();
            });
        await Case("audit.authorized-read-attribution", async () =>
        {
            var audit = database.GetCollection<BsonDocument>("RecordAccessAudit");
            var filter = new BsonDocument { { "TenantId", "synthetic-payer-a" }, { "RecordId", ids["a"] }, { "ActorId", "dev-user-synthetic-payer-a" }, { "StatusCode", 200 } };
            var matches = await audit.CountDocumentsAsync(filter);
            auditProofs.Add(new { scenarioId = activeCase, tenantId = "synthetic-payer-a", actorId = "dev-user-synthetic-payer-a", recordId = ids["a"], statusCode = 200, matches });
            return matches > 0;
        });
        await Case("privilege.admin-decision-control", async () =>
        {
            var r = await Request("PUT", $"api/authorizations/{ids["a"]}/status", tokens["a"], new { status = "Cancelled" });
            var document = await collection.Find(new BsonDocument("_id", ids["a"])).SingleAsync();
            var cancelled = document["Status"].AsInt32 == 8;
            stateProofs.Add(new { scenarioId = activeCase, statusIsCancelled = cancelled });
            return r.Status == 200 && cancelled;
        });
        completed = true;
    }
    finally
    {
        if (!service.HasExited) service.Kill(entireProcessTree: true);
        await service.WaitForExitAsync();
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Lab ERROR in {phase} ({exception.GetType().Name}).");
    results.Add(new("lab.prerequisites", "ERROR", $"Lab failed in {phase}; no assurance claim. Check build, Mongo availability and service configuration."));
}
var unsupported = new[] { "production-oidc", "member-binding", "claims", "appeals", "payments", "fhir-interop", "ingress", "resilience", "mutation-sensitivity" };
await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
{
    schemaVersion = "1.0", runId, started, finished = DateTimeOffset.UtcNow, completed,
    profile = "local-kestrel-mongo-development-identity", serviceSha256 = serviceHash,
    sourceCommit, sourceDirty, binaryFingerprints, mongoVersion,
    runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fixtureVersion = "synthetic-auth-v1", publication = "REQUIRES_REVIEW"
}, json));
await File.WriteAllLinesAsync(Path.Combine(output, "scenario-results.jsonl"), results.Select(x => JsonSerializer.Serialize(x, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
await File.WriteAllTextAsync(Path.Combine(output, "coverage.json"), JsonSerializer.Serialize(new
{
    planned = EvidencePolicy.ScenarioIds.Count, executed = results.Count(x => x.Id != "lab.prerequisites"),
    pass = results.Count(x => x.Outcome == "PASS"), fail = results.Count(x => x.Outcome == "FAIL"),
    error = results.Count(x => x.Outcome == "ERROR"), unsupported
}, json));
await File.WriteAllTextAsync(Path.Combine(output, "http-observations.json"), JsonSerializer.Serialize(observations, json));
await File.WriteAllTextAsync(Path.Combine(output, "state-assertions.json"), JsonSerializer.Serialize(stateProofs, json));
await File.WriteAllTextAsync(Path.Combine(output, "audit-assertions.json"), JsonSerializer.Serialize(auditProofs, json));
// No bodies, credentials, connection strings or raw service logs enter this review packet.
await File.WriteAllTextAsync(Path.Combine(output, "knowledge-base-draft.md"),
    $"# Security challenge review draft\n\nRun `{runId}`; binary SHA-256 `{serviceHash}`.\n\n" +
    string.Join("\n", results.Select(x => $"- `{x.Id}`: **{x.Outcome}**")) +
    "\n\nUnsupported: " + string.Join(", ", unsupported) +
    ".\n\nRequires private security review before publication. This local profile does not establish production identity, healthcare compliance or whole-platform security.\n");
await File.WriteAllTextAsync(Path.Combine(output, "reproduce.md"),
    "# Reproduction\n\nSee docs/security-challenge/README.md at source commit `" + sourceCommit +
    "`. Rebuild the service and runner with .NET 8 and use a new output directory.\n\n" +
    "Fixture: synthetic-auth-v1. IDs and token times are generated per run; compare assertions, not byte-identical responses.\n" +
    "Dirty source: " + sourceDirty + ". Binary identities are in manifest.json.\n");
var checksums = Directory.GetFiles(output).OrderBy(Path.GetFileName)
    .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() + "  " + Path.GetFileName(path));
await File.WriteAllLinesAsync(Path.Combine(output, "artifact-checksums.txt"), checksums);
Console.WriteLine($"Evidence: {output}; PASS={results.Count(x => x.Outcome == "PASS")}, FAIL={results.Count(x => x.Outcome == "FAIL")}, ERROR={results.Count(x => x.Outcome == "ERROR")}");
return EvidencePolicy.IsCompletePass(completed, results) ? 0 : 1;

static string ReadGit(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Git provenance unavailable");
    var text = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException("Run from the source checkout for Git provenance");
    return text.Trim();
}
