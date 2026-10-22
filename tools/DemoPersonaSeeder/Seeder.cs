using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoPersonaSeeder;

public enum StepOutcome { Created, Existing, Updated, Skipped, Failed }

public sealed record StepResult(string Area, string Key, StepOutcome Outcome, string? Detail = null);

/// <summary>What the services handed back that the demo script needs (claim ids and statuses).</summary>
public sealed class SeedRun
{
    public List<StepResult> Steps { get; } = new();
    public Dictionary<string, string> ClaimIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ClaimStatuses { get; } = new(StringComparer.Ordinal);
    public string? BillingRunId { get; set; }

    public bool HasFailures => Steps.Any(s => s.Outcome == StepOutcome.Failed);

    public void Add(string area, string key, StepOutcome outcome, string? detail = null)
    {
        Steps.Add(new StepResult(area, key, outcome, detail));
        var mark = outcome switch
        {
            StepOutcome.Created => "+",
            StepOutcome.Updated => "~",
            StepOutcome.Existing => "=",
            StepOutcome.Skipped => "-",
            _ => "!",
        };
        Console.WriteLine($"  {mark} {area,-14} {key}{(detail is null ? "" : "  " + detail)}");
    }
}

/// <summary>
/// Creates the demo cast through the services' HTTP APIs, in dependency order. Each step
/// looks the record up first, so re-running the seeder finds what an earlier run made
/// instead of duplicating it. A failed step is recorded and the run continues.
/// </summary>
public sealed class Seeder
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly SeederOptions _o;
    private readonly DemoDataset _data;
    private readonly SeedRun _run = new();
    private readonly DateTime _effective;

    public Seeder(HttpClient http, SeederOptions options, DemoDataset data)
    {
        _http = http;
        _o = options;
        _data = data;
        _effective = new DateTime(data.AsOf.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    public async Task<SeedRun> RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("Networks and plans");
        foreach (var n in _data.Networks) await NetworkAsync(n, ct);
        foreach (var p in _data.Plans) await PlanAsync(p, ct);

        Console.WriteLine("Providers and contracts");
        foreach (var p in _data.Providers) await ProviderAsync(p, ct);
        foreach (var c in _data.Contracts) await ContractAsync(c, ct);

        Console.WriteLine("Groups and members");
        foreach (var s in _data.Sponsors) await SponsorAsync(s, ct);
        foreach (var m in _data.Members.OrderBy(m => m.IsSubscriber ? 0 : 1)) await MemberAsync(m, ct);
        foreach (var m in _data.Members) await CoverageAsync(m, ct);
        foreach (var m in _data.Members.Where(m => m.PcpProviderKey is not null)) await PcpAsync(m, ct);

        Console.WriteLine("Authorizations");
        foreach (var a in _data.Authorizations) await AuthorizationAsync(a, ct);

        Console.WriteLine("Claims");
        foreach (var c in _data.Claims) await ClaimAsync(c, ct);
        await WaitForClaimsAsync(ct);

        Console.WriteLine("Appeals");
        foreach (var a in _data.Appeals) await AppealAsync(a, ct);

        Console.WriteLine("Premium billing");
        await BillingRunAsync(ct);

        return _run;
    }

    // ---- steps -------------------------------------------------------------------

    private Task NetworkAsync(DemoNetwork n, CancellationToken ct) => Ensure("network", n.Id,
        exists: () => Exists($"{_o.ProviderUrl}/api/v1/networks/{E(n.Id)}", ct),
        create: () => Post($"{_o.ProviderUrl}/api/v1/networks", Payloads.Network(n, _effective), ct));

    private Task PlanAsync(DemoPlan p, CancellationToken ct) => Ensure("benefit plan", $"{p.PlanId} ({p.Name})",
        exists: () => Exists($"{_o.BenefitPlanUrl}/api/v1/plans/{p.Id}", ct),
        create: () => Post($"{_o.BenefitPlanUrl}/api/v1/plans", Payloads.Plan(p, _o.TenantId, _effective), ct));

    private Task ProviderAsync(DemoProvider p, CancellationToken ct) => Ensure("provider", $"{p.Npi} {p.DisplayName}",
        exists: () => Exists($"{_o.ProviderUrl}/api/v1/providers/npi/{p.Npi}", ct),
        create: () => Post($"{_o.ProviderUrl}/api/v1/providers", Payloads.Provider(p, _data, _o.TenantId, _effective), ct));

    private async Task ContractAsync(DemoContract c, CancellationToken ct)
    {
        await Ensure("contract", c.ContractNumber,
            exists: () => Exists($"{_o.ContractsUrl}/api/v1/contracts/number/{E(c.ContractNumber)}", ct),
            create: async () =>
            {
                var created = await Post($"{_o.ContractsUrl}/api/v1/contracts", Payloads.Contract(c, _data.Provider(c.ProviderKey)), ct);
                var id = created?.GetPropertyOrNull("id");
                if (id is not null)
                    await Send(HttpMethod.Put, $"{_o.ContractsUrl}/api/v1/contracts/{E(id)}/activate", new { }, ct);
                return created;
            });
    }

    private async Task SponsorAsync(DemoSponsor s, CancellationToken ct)
    {
        var wasThere = false;
        await Ensure("group", $"{s.GroupNumber} {s.EmployerName}",
            exists: async () => wasThere = await Exists($"{_o.SponsorUrl}/api/v1/sponsors/{E(s.GroupNumber)}", ct),
            create: () => Post($"{_o.SponsorUrl}/api/v1/sponsors", Payloads.Sponsor(s, _effective), ct));
        if (!wasThere)
        {
            // New sponsors start PendingActivation; billing only picks up Active ones.
            await Try("group status", s.GroupNumber, () =>
                Send(HttpMethod.Put, $"{_o.SponsorUrl}/api/v1/sponsors/{E(s.GroupNumber)}", new { status = "Active" }, ct),
                StepOutcome.Updated, "Active");
        }
    }

    private async Task MemberAsync(DemoMember m, CancellationToken ct)
    {
        var wasThere = false;
        await Ensure("member", $"{m.MemberId} {m.FullName}",
            exists: async () => wasThere = await Exists($"{_o.MemberUrl}/api/v1/members/{E(m.MemberId)}", ct),
            create: () => Post($"{_o.MemberUrl}/api/v1/members", Payloads.Member(m, _effective), ct));
        if (!wasThere)
        {
            await Try("member status", m.MemberId, () =>
                Send(HttpMethod.Put, $"{_o.MemberUrl}/api/v1/members/{E(m.MemberId)}",
                    new { status = "Active", eventId = $"demo-persona-seeder:member-status:{m.MemberId}" }, ct),
                StepOutcome.Updated, "Active");
        }
        foreach (var id in m.Identifiers)
        {
            await Try("identifier", $"{m.MemberId} {id.Type}", async () =>
            {
                using var response = await PostRaw($"{_o.MemberUrl}/api/v1/members/{E(m.MemberId)}/identifiers", Payloads.MemberIdentifier(id), ct);
                if (response.StatusCode == HttpStatusCode.Conflict) throw new AlreadyThereException();
                await EnsureSuccess(response, ct);
            });
        }
    }

    private Task CoverageAsync(DemoMember m, CancellationToken ct) => Ensure("coverage", $"{m.MemberId} → {_data.Plan(m.PlanKey).PlanId}",
        exists: () => HasItems($"{_o.CoverageUrl}/api/v1/coverage/member/{E(m.MemberId)}/active", ct),
        create: () => Post($"{_o.CoverageUrl}/api/v1/coverage", Payloads.Coverage(m, _data, _effective), ct));

    private Task PcpAsync(DemoMember m, CancellationToken ct)
    {
        var pcp = _data.Provider(m.PcpProviderKey!);
        return Try("PCP", $"{m.MemberId} → {pcp.DisplayName}", () =>
            Send(HttpMethod.Put, $"{_o.CoverageUrl}/api/v1/coverage/member/{E(m.MemberId)}/pcp",
                Payloads.PcpAssignment(m, pcp, _effective), ct),
            StepOutcome.Updated);
    }

    private async Task AuthorizationAsync(DemoAuthorization a, CancellationToken ct)
    {
        string? id = null;
        await Ensure("authorization", a.Number,
            exists: async () =>
            {
                var found = await GetJson($"{_o.AuthorizationUrl}/api/authorizations/number/{E(a.Number)}", ct);
                id = found?.GetPropertyOrNull("id");
                return found is not null;
            },
            create: async () =>
            {
                var created = await Post($"{_o.AuthorizationUrl}/api/authorizations", Payloads.Authorization(a, _data), ct);
                id = created?.GetPropertyOrNull("id");
                if (id is not null && a.Decision is not null)
                {
                    await Send(HttpMethod.Put, $"{_o.AuthorizationUrl}/api/authorizations/{E(id)}/status",
                        Payloads.AuthorizationDecision(a), ct);
                }
                return created;
            });
    }

    private async Task ClaimAsync(DemoClaim c, CancellationToken ct)
    {
        await Ensure("claim", c.ClaimNumber,
            exists: async () =>
            {
                var found = await GetJson($"{_o.ClaimsUrl}/api/claims/number/{E(c.ClaimNumber)}", ct);
                if (found is null) return false;
                Remember(c.ClaimNumber, found.Value);
                return true;
            },
            create: async () =>
            {
                var created = await Post($"{_o.ClaimsUrl}/api/v1/claims", Payloads.Claim(c, _data, _o.TenantId), ct);
                if (created is { } body) Remember(c.ClaimNumber, body);
                return created;
            });
    }

    private void Remember(string claimNumber, JsonElement claim)
    {
        if (claim.GetPropertyOrNull("id") is { } id) _run.ClaimIds[claimNumber] = id;
        if (claim.TryGetProperty("status", out var status)) _run.ClaimStatuses[claimNumber] = ClaimStatusName(status);
    }

    /// <summary>Adjudication is asynchronous; poll each new claim until it settles or the wait runs out.</summary>
    private async Task WaitForClaimsAsync(CancellationToken ct)
    {
        if (_o.ClaimWaitSeconds == 0 || _run.ClaimIds.Count == 0) return;
        var deadline = DateTime.UtcNow.AddSeconds(_o.ClaimWaitSeconds);
        var open = _run.ClaimIds.Keys.Where(n => !IsSettled(_run.ClaimStatuses.GetValueOrDefault(n))).ToHashSet();
        while (open.Count > 0 && DateTime.UtcNow < deadline)
        {
            foreach (var number in open.ToList())
            {
                var claim = await GetJson($"{_o.ClaimsUrl}/api/claims/{E(_run.ClaimIds[number])}", ct);
                if (claim is { } body) Remember(number, body);
                if (IsSettled(_run.ClaimStatuses.GetValueOrDefault(number))) open.Remove(number);
            }
            if (open.Count > 0) await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        foreach (var number in _run.ClaimIds.Keys.Order())
            _run.Add("claim status", number, StepOutcome.Updated, _run.ClaimStatuses.GetValueOrDefault(number, "unknown"));
    }

    private async Task AppealAsync(DemoAppeal a, CancellationToken ct)
    {
        if (!_run.ClaimIds.TryGetValue(a.ClaimNumber, out var claimId))
        {
            _run.Add("appeal", a.AppealNumber, StepOutcome.Skipped, $"claim {a.ClaimNumber} was not created");
            return;
        }
        await Ensure("appeal", a.AppealNumber,
            exists: () => Exists($"{_o.AppealsUrl}/api/appeals/number/{E(a.AppealNumber)}", ct),
            create: async () =>
            {
                var created = await Post($"{_o.AppealsUrl}/api/appeals", Payloads.Appeal(a, _data, claimId), ct);
                if (created?.GetPropertyOrNull("id") is { } id)
                    await Send(HttpMethod.Post, $"{_o.AppealsUrl}/api/appeals/{E(id)}/submit", new { }, ct);
                return created;
            });
    }

    private async Task BillingRunAsync(CancellationToken ct)
    {
        var period = new DateTime(_data.AsOf.Year, _data.AsOf.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        await Try("billing run", $"{string.Join(",", _data.BilledGroups)} {period:yyyy-MM}", async () =>
        {
            using var response = await PostRaw($"{_o.BillingUrl}/api/v1/billing-runs", Payloads.BillingRun(_data.BilledGroups, period), ct);
            if (response.StatusCode == HttpStatusCode.Conflict) throw new AlreadyThereException();
            var body = await ReadJson(response, ct);
            var id = body?.GetPropertyOrNull("id") ?? throw new InvalidOperationException("billing run returned no id");
            _run.BillingRunId = id;
            using var execute = await PostRaw($"{_o.BillingUrl}/api/v1/billing-runs/{E(id)}/execute", new { }, ct);
            await EnsureSuccess(execute, ct);
        });
    }

    // ---- plumbing ----------------------------------------------------------------

    private sealed class AlreadyThereException : Exception;

    private async Task Ensure(string area, string key, Func<Task<bool>> exists, Func<Task<JsonElement?>> create)
    {
        try
        {
            if (await exists())
            {
                _run.Add(area, key, StepOutcome.Existing);
                return;
            }
            await create();
            _run.Add(area, key, StepOutcome.Created);
        }
        catch (AlreadyThereException)
        {
            _run.Add(area, key, StepOutcome.Existing);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            _run.Add(area, key, StepOutcome.Failed, ex.Message);
        }
    }

    private async Task Try(string area, string key, Func<Task> action, StepOutcome success = StepOutcome.Created, string? detail = null)
    {
        try
        {
            await action();
            _run.Add(area, key, success, detail);
        }
        catch (AlreadyThereException)
        {
            _run.Add(area, key, StepOutcome.Existing);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            _run.Add(area, key, StepOutcome.Failed, ex.Message);
        }
    }

    private static string E(string value) => Uri.EscapeDataString(value);

    private async Task<bool> Exists(string url, CancellationToken ct) => await GetJson(url, ct) is not null;

    private async Task<bool> HasItems(string url, CancellationToken ct)
        => await GetJson(url, ct) is { } body
           && (body.ValueKind != JsonValueKind.Array || body.GetArrayLength() > 0);

    private async Task<JsonElement?> GetJson(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadJson(response, ct);
    }

    private async Task<JsonElement?> Post(string url, object body, CancellationToken ct)
    {
        using var response = await PostRaw(url, body, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new AlreadyThereException();
        return await ReadJson(response, ct);
    }

    private Task<HttpResponseMessage> PostRaw(string url, object body, CancellationToken ct)
        => _http.PostAsJsonAsync(url, body, Json, ct);

    private async Task Send(HttpMethod method, string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: Json) };
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {Trim(text)}");
    }

    private static async Task<JsonElement?> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccess(response, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    internal static string ClaimStatusName(JsonElement status) => status.ValueKind switch
    {
        JsonValueKind.Number => status.GetInt32() switch
        {
            1 => "Submitted", 2 => "Received", 3 => "InAdjudication", 4 => "Pended", 5 => "Approved",
            6 => "Denied", 7 => "Paid", 8 => "Voided", 9 => "PartiallyPaid", 10 => "Deny",
            var other => other.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        JsonValueKind.String => status.GetString() ?? "unknown",
        _ => "unknown",
    };

    internal static bool IsSettled(string? status)
        => status is "Pended" or "Approved" or "Denied" or "Paid" or "Voided" or "PartiallyPaid" or "Deny";
}

internal static class JsonElementExtensions
{
    public static string? GetPropertyOrNull(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        }
        return null;
    }

    public static string? GetPropertyOrNull(this JsonElement? element, string name)
        => element is { } e ? e.GetPropertyOrNull(name) : null;
}
