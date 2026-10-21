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

public static class DocumentIsolationCampaign
{
    public static IReadOnlyList<string> ScenarioIds { get; } = Array.AsReadOnly(new[]
    {
        "catalog.upload-control",
        "catalog.tenant-b-control",
        "catalog.download-control",
        "catalog.metadata-control",
        "catalog.anonymous",
        "catalog.bad-token",
        "catalog.tenant-conflict",
        "catalog.foreign-document",
        "catalog.wrong-subject",
        "catalog.missing-context",
        "catalog.subject-permission",
        "catalog.document-permission",
        "catalog.pending-denial",
        "catalog.infected-denial",
        "catalog.failed-denial",
        "catalog.clean-restoration",
        "sensitive.exact-control",
        "sensitive.reader-denial",
        "sensitive.wildcard-denial",
        "sensitive.part2-denial",
        "sensitive.list-withholding",
        "sensitive.hidden-dedup",
        "upload.signature-denial",
        "upload.size-denial",
        "hold.set-control",
        "hold.link-denial",
        "hold.release-reason",
        "audit.user-spoof",
        "audit.service-header-invalid",
        "attachment.download-control",
        "attachment.foreign-tenant",
        "attachment.document-permission",
        "attachment.subject-permission",
        "attachment.wrong-parent",
        "attachment.sensitive-denial",
        "attachment.audit-identity",
        "audit.available-control",
        "audit.outage-denial",
        "audit.recovery-control"
    });

    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 4 || !File.Exists(args[1]) || !File.Exists(args[2]) || Path.Exists(args[3]))
        { Console.Error.WriteLine("Usage: SecurityChallenge documents <document-service.dll> <attachment-service.dll> <new-evidence-directory>"); return 2; }
        var output = Path.GetFullPath(args[3]);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(output);
        else Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<CaseResult>(); var observations = new List<object>(); var states = new List<object>(); var audits = new List<object>();
        var started = DateTimeOffset.UtcNow; var runId = Guid.NewGuid().ToString("N");
        var source = Git("rev-parse", "HEAD"); var dirty = Git("status", "--porcelain").Length != 0;
        var binaries = args.Skip(1).Take(2).SelectMany(assembly => Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(assembly))!)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal) || p.EndsWith(".deps.json", StringComparison.Ordinal) || p.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(p => new { service = Path.GetFileNameWithoutExtension(assembly), file = Path.GetFileName(p), sha256 = Hash(File.ReadAllBytes(p)) })).ToArray();
        var active = "lab.mongo"; var completed = false; string? mongoVersion = null;
        try
        {
            using var mongo = MongoRunner.Run(new MongoRunnerOptions { Version = MongoVersion.V7, Edition = MongoEdition.Community, ConnectionTimeout = TimeSpan.FromSeconds(30) });
            using var client = new MongoClient(mongo.ConnectionString);
            var db = client.GetDatabase("fhir_smart_security_" + runId);
            mongoVersion = (await db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1)))["version"].AsString;
            await using var catalog = new LocalService(args[1], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, true,
                new Dictionary<string, string?> { ["DocumentStore:Provider"] = "InMemory", ["Documents:AllowUnscannedContent"] = "false", ["Documents:MaxUploadBytes"] = "4096" });
            await catalog.Ready();
            await using var attachment = new LocalService(args[2], mongo.ConnectionString, db.DatabaseNamespace.DatabaseName, true,
                new Dictionary<string, string?> { ["Services:DocumentService"] = catalog.Http.BaseAddress!.ToString(), ["ChoAuth:Outbound:DevelopmentHosts:0"] = "127.0.0.1" });
            await attachment.Ready();
            const string tenantA = "synthetic-payer-a", tenantB = "synthetic-payer-b", subject = "CLM-shared";
            string User(string tenant, params string[] permissions) => ChoDevelopmentAuth.UserTokenIssuer(TimeSpan.FromMinutes(15)).IssueUserToken("synthetic-document-user", tenant, [], permissions);
            var reader = User(tenantA, "documents:read", "claims:read");
            var writer = User(tenantA, "documents:read", "documents:write", "claims:read");
            var privileged = User(tenantA, "documents:read", "documents:write", "claims:read", "documents:read-sensitive", "records:legal-hold");
            var writerB = User(tenantB, "documents:read", "documents:write", "claims:read");
            var gateway = User(tenantA, "attachments:read", "documents:read", "claims:read");
            var content = Encoding.UTF8.GetBytes("%PDF-1.4\nSYNTHETIC-CANARY-a-document\n%%EOF");
            var contentB = Encoding.UTF8.GetBytes("%PDF-1.4\nSYNTHETIC-CANARY-b-document\n%%EOF");
            var documents = db.GetCollection<BsonDocument>("CatalogDocuments");
            async Task Scan(string id, string status) => await documents.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("Scan.Status", status)));
            string Context(string id, string suffix = "", string parent = subject) => $"api/v1/documents/{id}{suffix}?subjectKind=Claim&subjectId={parent}";
            async Task<string> Snapshot()
            {
                var text = new StringBuilder();
                foreach (var name in new[] { "CatalogDocuments", "Attachments" })
                {
                    var rows = await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).ToListAsync();
                    text.Append(name).AppendJoin('\n', rows.Select(x => x.ToJson()).Order(StringComparer.Ordinal));
                }
                var hash = Hash(Encoding.UTF8.GetBytes(text.ToString())); states.Add(new { scenarioId = active, sha256 = hash }); return hash;
            }
            async Task<(int Status, byte[] Bytes, string Body, bool SafeDownload)> Request(LocalService service, string path, string? token, string method = "GET", HttpContent? body = null, string? behalf = null, string? tenantHeader = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
                if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (behalf != null) request.Headers.Add("X-CHO-On-Behalf-Of", behalf);
                if (tenantHeader != null) request.Headers.Add("X-Tenant-ID", tenantHeader);
                request.Headers.Add("X-CHO-Screen", "security-" + active);
                using var response = await service.Http.SendAsync(request);
                var bytes = await response.Content.ReadAsByteArrayAsync(); var text = Encoding.UTF8.GetString(bytes);
                observations.Add(new { scenarioId = active, service = service == catalog ? "catalog" : "attachment", method, path, status = (int)response.StatusCode, bodySha256 = Hash(bytes), canary = text.Contains("SYNTHETIC-CANARY") });
                return ((int)response.StatusCode, bytes, text, response.Content.Headers.ContentDisposition?.DispositionType == "attachment" && response.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Contains("nosniff"));
            }
            async Task<bool> Denied(LocalService service, string path, string? token, int expected, string method = "GET", HttpContent? body = null, string? behalf = null, string? tenantHeader = null)
            {
                var before = await Snapshot(); var r = await Request(service, path, token, method, body, behalf, tenantHeader);
                return r.Status == expected && !r.Body.Contains("SYNTHETIC-CANARY") && !r.Body.Contains("PART2_SUD") && !r.Body.Contains("HIV") && before == await Snapshot();
            }
            async Task Case(string id, Func<Task<bool>> test, string? prerequisite = "catalog.download-control")
            {
                active = id;
                if (prerequisite != null && !EvidencePolicy.PositiveControlPassed(results, prerequisite))
                { results.Add(new(id, "INCONCLUSIVE", "Positive control unavailable: " + prerequisite)); return; }
                try { results.Add(new(id, await test() ? "PASS" : "FAIL", "See document isolation catalog v1")); }
                catch { results.Add(new(id, "ERROR", "Execution failed; inspect restricted diagnostics")); }
            }
            MultipartFormDataContent Form(byte[] bytes, string link = "Claim:CLM-shared", string? sensitivity = null)
            {
                var form = new MultipartFormDataContent { { new StringContent("Other"), "Type" }, { new StringContent("Synthetic document"), "Title" }, { new StringContent(link), "Links" } };
                if (sensitivity != null) form.Add(new StringContent(sensitivity), "Sensitivity");
                var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(file, "file", "synthetic.pdf"); return form;
            }
            async Task<string> Upload(byte[] bytes, string token, string link = "Claim:CLM-shared", string? sensitivity = null)
            {
                var r = await Request(catalog, "api/v1/documents", token, "POST", Form(bytes, link, sensitivity));
                if (r.Status != 201) throw new InvalidOperationException("Synthetic upload fixture failed");
                using var json = JsonDocument.Parse(r.Body); return json.RootElement.GetProperty("id").GetString()!;
            }
            async Task<bool> Download(LocalService service, string path, string token, byte[] expected)
            {
                var r = await Request(service, path, token); return r.Status == 200 && r.SafeDownload && r.Bytes.SequenceEqual(expected);
            }
            string? idA = null, idB = null;
            await Case("catalog.upload-control", async () => { idA = await Upload(content, writer); await Scan(idA, "Clean"); return true; }, null);
            await Case("catalog.tenant-b-control", async () => { idB = await Upload(contentB, writerB); await Scan(idB, "Clean"); return await Download(catalog, Context(idB, "/content"), writerB, contentB); }, null);
            await Case("catalog.download-control", () => Download(catalog, Context(idA!, "/content"), reader, content), "catalog.upload-control");
            await Case("catalog.metadata-control", async () => { var r = await Request(catalog, Context(idA!), reader); return r.Status == 200 && r.Body.Contains(idA!) && !r.Body.Contains("storagePath", StringComparison.OrdinalIgnoreCase) && !r.Body.Contains("storageContainer", StringComparison.OrdinalIgnoreCase); });
            await Case("catalog.anonymous", () => Denied(catalog, Context(idA!, "/content"), null, 401));
            await Case("catalog.bad-token", () => Denied(catalog, Context(idA!, "/content"), "synthetic.invalid.jwt", 401));
            await Case("catalog.tenant-conflict", () => Denied(catalog, Context(idA!, "/content"), reader, 403, tenantHeader: tenantB));
            await Case("catalog.foreign-document", () => Denied(catalog, Context(idA!, "/content"), writerB, 404), "catalog.tenant-b-control");
            await Case("catalog.wrong-subject", () => Denied(catalog, Context(idA!, "/content", "CLM-other"), reader, 404));
            await Case("catalog.missing-context", () => Denied(catalog, $"api/v1/documents/{idA}/content", reader, 400));
            await Case("catalog.subject-permission", () => Denied(catalog, Context(idA!, "/content"), User(tenantA, "documents:read"), 403));
            await Case("catalog.document-permission", () => Denied(catalog, Context(idA!, "/content"), User(tenantA, "claims:read"), 403));
            foreach (var scan in new[] { ("pending", "Pending"), ("infected", "Infected"), ("failed", "Failed") })
                await Case("catalog." + scan.Item1 + "-denial", async () => { await Scan(idA!, scan.Item2); return await Denied(catalog, Context(idA!, "/content"), reader, 409); });
            await Case("catalog.clean-restoration", async () => { await Scan(idA!, "Clean"); return await Download(catalog, Context(idA!, "/content"), reader, content); });
            var sensitiveBytes = Encoding.UTF8.GetBytes("%PDF-1.4\nSYNTHETIC-CANARY-sensitive\n%%EOF");
            var sensitive = await Upload(sensitiveBytes, privileged, sensitivity: "HIV"); await Scan(sensitive, "Clean");
            var part2 = await Upload(Encoding.UTF8.GetBytes("%PDF-1.4\nSYNTHETIC-CANARY-part2\n%%EOF"), privileged, sensitivity: "PART2_SUD"); await Scan(part2, "Clean");
            await Case("sensitive.exact-control", () => Download(catalog, Context(sensitive, "/content"), privileged, sensitiveBytes));
            await Case("sensitive.reader-denial", () => Denied(catalog, Context(sensitive, "/content"), reader, 403));
            await Case("sensitive.wildcard-denial", () => Denied(catalog, Context(sensitive, "/content"), User(tenantA, "*:*"), 403));
            await Case("sensitive.part2-denial", () => Denied(catalog, Context(part2, "/content"), privileged, 403));
            await Case("sensitive.list-withholding", async () => { var r = await Request(catalog, "api/v1/documents?subjectKind=Claim&subjectId=" + subject, reader); return r.Status == 200 && !r.Body.Contains(sensitive) && !r.Body.Contains(part2) && r.Body.Contains(idA!); });
            await Case("sensitive.hidden-dedup", async () => { var before = (await documents.Find(new BsonDocument("_id", sensitive)).SingleAsync()).ToJson(); var r = await Request(catalog, "api/v1/documents", writer, "POST", Form(sensitiveBytes, "Claim:CLM-new")); if (r.Status != 201) return false; using var freshJson = JsonDocument.Parse(r.Body); var fresh = freshJson.RootElement.GetProperty("id").GetString(); return fresh != sensitive && before == (await documents.Find(new BsonDocument("_id", sensitive)).SingleAsync()).ToJson(); });
            await Case("upload.signature-denial", () => Denied(catalog, "api/v1/documents", writer, 415, "POST", Form(Encoding.UTF8.GetBytes("not a pdf"))));
            await Case("upload.size-denial", () => Denied(catalog, "api/v1/documents", writer, 413, "POST", Form(new byte[4097])));
            await Case("hold.set-control", async () => { var r = await Request(catalog, $"api/v1/documents/{idA}/legal-hold", privileged, "PUT", System.Net.Http.Json.JsonContent.Create(new { legalHold = true, reason = "Synthetic hold" })); return r.Status == 200 && !r.Body.Contains("sha256") && !r.Body.Contains("title"); });
            await Case("hold.link-denial", () => Denied(catalog, Context(idA!, "/links"), writer, 409, "POST", System.Net.Http.Json.JsonContent.Create(new { link = "Claim:CLM-new" })), "hold.set-control");
            await Case("hold.release-reason", () => Denied(catalog, $"api/v1/documents/{idA}/legal-hold", privileged, 400, "PUT", System.Net.Http.Json.JsonContent.Create(new { legalHold = false })), "hold.set-control");
            var audit = db.GetCollection<BsonDocument>("DocumentAccessAudit");
            await Case("audit.user-spoof", async () => { var r = await Request(catalog, Context(idA!, "/content"), reader, behalf: "forged-user"); return r.Status == 200 && await audit.CountDocumentsAsync(new BsonDocument { { "OnBehalfOf", "forged-user" } }) == 0 && await audit.CountDocumentsAsync(new BsonDocument { { "DocumentId", idA! }, { "Actor", "synthetic-document-user" }, { "OnBehalfOf", BsonNull.Value }, { "Screen", "security-audit.user-spoof" }, { "Allowed", true } }) == 1; });
            await Case("audit.service-header-invalid", () => Denied(catalog, Context(idA!, "/content"), ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("attachment-service", tenantA), 400, behalf: "invalid user"));
            async Task SeedAttachment(string id, string tenant, string document, string parent = subject)
            {
                await db.GetCollection<BsonDocument>("Attachments").InsertOneAsync(new BsonDocument { { "_id", id }, { "TenantId", tenant }, { "ClaimId", parent }, { "DocumentId", document }, { "DocumentFormat", "PDF" }, { "Status", "Received" }, { "CreatedBy", "synthetic-document-user" }, { "CreatedDate", DateTime.UtcNow } });
            }
            await SeedAttachment("synthetic-attachment-a", tenantA, idA!);
            await SeedAttachment("synthetic-wrong-parent", tenantA, idA!, "CLM-other");
            await SeedAttachment("synthetic-sensitive", tenantA, sensitive);
            const string download = "api/attachments/synthetic-attachment-a/download";
            await Case("attachment.download-control", () => Download(attachment, download, gateway, content));
            await Case("attachment.foreign-tenant", () => Denied(attachment, download, User(tenantB, "attachments:read", "documents:read", "claims:read"), 404), "attachment.download-control");
            await Case("attachment.document-permission", () => Denied(attachment, download, User(tenantA, "attachments:read", "claims:read"), 403), "attachment.download-control");
            await Case("attachment.subject-permission", () => Denied(attachment, download, User(tenantA, "attachments:read", "documents:read"), 403), "attachment.download-control");
            await Case("attachment.wrong-parent", () => Denied(attachment, "api/attachments/synthetic-wrong-parent/download", gateway, 404), "attachment.download-control");
            await Case("attachment.sensitive-denial", () => Denied(attachment, "api/attachments/synthetic-sensitive/download", gateway, 403), "attachment.download-control");
            await Case("attachment.audit-identity", async () => { var count = await audit.CountDocumentsAsync(new BsonDocument { { "TenantId", tenantA }, { "DocumentId", idA! }, { "Actor", "attachment-service" }, { "OnBehalfOf", "synthetic-document-user" }, { "SubjectId", subject }, { "Action", "Download" }, { "Allowed", true } }); audits.Add(new { scenarioId = active, tenantSubjectServiceAndUserBoundDownloads = count }); return count > 0; }, "attachment.download-control");
            await Case("audit.available-control", async () => await audit.CountDocumentsAsync(new BsonDocument { { "DocumentId", idA! }, { "Actor", "synthetic-document-user" }, { "Action", "Download" }, { "Allowed", true } }) > 0);
            await Case("audit.outage-denial", async () =>
            {
                // Owned Mongo validator fault: audit inserts fail while catalog/storage stay healthy.
                await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "DocumentAccessAudit" }, { "validator", new BsonDocument("Actor", "impossible-synthetic-actor") }, { "validationLevel", "strict" } });
                try { return await Denied(catalog, Context(idA!, "/content"), reader, 500); }
                finally { await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "DocumentAccessAudit" }, { "validator", new BsonDocument() } }); }
            }, "audit.available-control");
            await Case("audit.recovery-control", () => Download(catalog, Context(idA!, "/content"), reader, content), "audit.available-control");
            completed = true;
        }
        catch (Exception e) { completed = false; Console.Error.WriteLine("Lab ERROR: " + e.GetType().Name); results.Add(new("lab.prerequisites", "ERROR", "Local service, fixture, identity or database setup/cleanup failed")); }
        var unsupported = new[] { "fhir-bulk-file-route-not-implemented", "cloud-signed-url-expiry-replay", "azure-gcs-s3-storage-policy", "real-malware-scanner", "attachment-http-upload-parent-lookup", "presigned-finalize-concurrency", "production-identity-tls-ingress", "full-log-phi-detection" };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        async Task Write(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, json));
        await Write("manifest.json", new { schemaVersion = "1.0", campaign = "document-isolation-v1", runId, started, finished = DateTimeOffset.UtcNow, completed, sourceCommit = source, sourceDirty = dirty, binaries, mongoVersion, runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fixtureVersion = "synthetic-catalog-attachment-v1", profile = "loopback-real-catalog-attachment-development", documentStore = "development-inmemory-real-bytes", scanner = "none-status-fixtures-set-in-mongo", attachmentSeed = "direct-mongo-existing-state", catalogSeed = "http-multipart-uploads", tenantStorage = "shared-database-row-filtering", identity = "development-cho-user-and-service-tokens", publication = "REQUIRES_REVIEW" });
        await File.WriteAllLinesAsync(Path.Combine(output, "scenario-results.jsonl"), results.Select(r => JsonSerializer.Serialize(r, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        await Write("coverage.json", new { planned = ScenarioIds.Count, reported = results.Count(r => r.Id != "lab.prerequisites"), executed = EvidencePolicy.ExecutedCount(results), pass = results.Count(r => r.Outcome == "PASS"), fail = results.Count(r => r.Outcome == "FAIL"), error = results.Count(r => r.Outcome == "ERROR"), inconclusive = results.Count(r => r.Outcome == "INCONCLUSIVE"), unsupported });
        await Write("http-observations.json", observations); await Write("state-assertions.json", states); await Write("audit-assertions.json", audits);
        await Write("findings-summary.json", new { reviewState = "REQUIRES_PRIVATE_TRIAGE", confirmedFindings = (int?)null, failedScenarios = results.Where(r => r.Outcome == "FAIL").Select(r => r.Id), errors = results.Where(r => r.Outcome == "ERROR").Select(r => r.Id) });
        await File.WriteAllTextAsync(Path.Combine(output, "knowledge-base-draft.md"), $"# Document isolation security review draft\n\nSource `{source}`, dirty: {dirty}.\n\n" + string.Join("\n", results.Select(r => $"- `{r.Id}`: **{r.Outcome}**")) + "\n\nUnsupported: " + string.Join(", ", unsupported) + ".\n\nPrivate review required. No production certification. See manifest, observations, state and audit assertions; sensitivity evidence is a separate packet.\n");
        await File.WriteAllTextAsync(Path.Combine(output, "reproduce.md"), "See docs/security-challenge/DOCUMENT-ISOLATION.md at source `" + source + "`. Rebuild services and runner with .NET 8.\n");
        await File.WriteAllLinesAsync(Path.Combine(output, "artifact-checksums.txt"), Directory.GetFiles(output).Order(StringComparer.Ordinal).Select(p => Hash(File.ReadAllBytes(p)) + "  " + Path.GetFileName(p)));
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.GetFiles(output)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"Document isolation evidence: {output}; PASS={results.Count(r => r.Outcome == "PASS")}, FAIL={results.Count(r => r.Outcome == "FAIL")}, ERROR={results.Count(r => r.Outcome == "ERROR")}, INCONCLUSIVE={results.Count(r => r.Outcome == "INCONCLUSIVE")}");
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
