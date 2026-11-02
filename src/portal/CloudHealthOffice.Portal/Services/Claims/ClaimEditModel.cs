using System.Globalization;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The editable part of a claim for the examiner's correction screen.
///
/// <para>
/// The claims service hands the portal a full claim payload (the shape
/// <c>POST /api/v1/claims/{id}/adjustments</c> takes as the corrected claim).
/// This model exposes only what an examiner keys or fixes on a professional
/// claim: member, providers, place of service, prior auth, diagnoses and
/// service lines. <see cref="ApplyTo"/> writes those values back onto a copy
/// of the original payload, so every field the editor does not show (other
/// payers, attachments, institutional or dental detail, line NDCs, teeth)
/// goes back to the service exactly as it came.
/// </para>
/// </summary>
public sealed class ClaimEditModel
{
    private readonly JsonObject _source;
    private readonly Snapshot _original;

    public string MemberId { get; set; } = string.Empty;
    public string BillingProviderNpi { get; set; } = string.Empty;
    public string? RenderingProviderNpi { get; set; }
    public string PlaceOfServiceCode { get; set; } = "11";
    public string? PriorAuthorizationNumber { get; set; }
    public List<ClaimEditDiagnosis> Diagnoses { get; } = new();
    public List<ClaimEditLine> Lines { get; } = new();

    /// <summary>Claim number shown in the header; never edited.</summary>
    public string ClaimNumber { get; }

    public decimal TotalCharge => Lines.Sum(l => l.ChargeAmount);

    private ClaimEditModel(JsonObject source)
    {
        _source = source;
        ClaimNumber = Str(source, "claimNumber") ?? string.Empty;
        MemberId = Str(source, "memberId") ?? string.Empty;
        BillingProviderNpi = Str(source, "billingProviderNPI") ?? string.Empty;
        RenderingProviderNpi = Str(source, "renderingProviderNPI");
        PlaceOfServiceCode = Str(source, "placeOfServiceCode") ?? "11";
        PriorAuthorizationNumber = Str(source, "priorAuthorizationNumber");

        if (Get(source, "diagnosisCodes") is JsonArray dx)
        {
            foreach (var d in dx.OfType<JsonObject>()
                         .OrderBy(d => Int(d, "pointerNumber") ?? int.MaxValue))
            {
                Diagnoses.Add(new ClaimEditDiagnosis { Code = Str(d, "code") ?? string.Empty });
            }
        }

        var claimFrom = Date(source, "serviceDateFrom");
        var claimTo = Date(source, "serviceDateTo");
        if (Get(source, "claimLines") is JsonArray lines)
        {
            foreach (var l in lines.OfType<JsonObject>()
                         .OrderBy(l => Int(l, "lineNumber") ?? int.MaxValue))
            {
                Lines.Add(ClaimEditLine.FromJson(l, claimFrom, claimTo));
            }
        }

        _original = Snapshot.Of(this);
    }

    /// <summary>Starts an edit from a claim payload; the payload is copied, never modified.</summary>
    public static ClaimEditModel FromJson(JsonObject claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new ClaimEditModel((JsonObject)claim.DeepClone());
    }

    public void AddDiagnosis() => Diagnoses.Add(new ClaimEditDiagnosis());

    public void RemoveDiagnosis(ClaimEditDiagnosis diagnosis)
    {
        var removed = Diagnoses.IndexOf(diagnosis) + 1;
        if (removed == 0) return;
        Diagnoses.Remove(diagnosis);

        // Keep line pointers aimed at the same codes: drop pointers to the
        // removed code and shift the ones after it down by one.
        foreach (var line in Lines)
        {
            line.DiagnosisPointers = line.DiagnosisPointers
                .Where(p => p != removed)
                .Select(p => p > removed ? p - 1 : p)
                .ToList();
        }
    }

    public ClaimEditLine AddLine()
    {
        var last = Lines.LastOrDefault();
        var line = new ClaimEditLine
        {
            ServiceDateFrom = last?.ServiceDateFrom ?? DateTime.Today,
            ServiceDateTo = last?.ServiceDateTo ?? DateTime.Today,
            DiagnosisPointers = Diagnoses.Count > 0 ? new List<int> { 1 } : new List<int>(),
        };
        Lines.Add(line);
        return line;
    }

    public void RemoveLine(ClaimEditLine line) => Lines.Remove(line);

    /// <summary>Problems that would make the service reject the claim, in plain words.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(MemberId))
            errors.Add("Member ID is required.");
        if (!IsNpi(BillingProviderNpi))
            errors.Add("Billing provider NPI must be 10 digits.");
        if (!string.IsNullOrWhiteSpace(RenderingProviderNpi) && !IsNpi(RenderingProviderNpi))
            errors.Add("Rendering provider NPI must be 10 digits.");
        if (string.IsNullOrWhiteSpace(PlaceOfServiceCode))
            errors.Add("Place of service is required.");
        if (Diagnoses.Count == 0 || Diagnoses.All(d => string.IsNullOrWhiteSpace(d.Code)))
            errors.Add("At least one diagnosis code is required.");
        if (Diagnoses.Any(d => string.IsNullOrWhiteSpace(d.Code)) && Diagnoses.Count > 0)
            errors.Add("Remove empty diagnosis rows.");
        if (Lines.Count == 0)
            errors.Add("At least one service line is required.");

        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var n = i + 1;
            if (string.IsNullOrWhiteSpace(line.ProcedureCode))
                errors.Add($"Line {n}: procedure code is required.");
            if (line.Units <= 0)
                errors.Add($"Line {n}: units must be more than zero.");
            if (line.ChargeAmount < 0)
                errors.Add($"Line {n}: charge can't be negative.");
            if (line.ServiceDateFrom is null)
                errors.Add($"Line {n}: service date is required.");
            else if (line.ServiceDateTo is { } to && to.Date < line.ServiceDateFrom.Value.Date)
                errors.Add($"Line {n}: service end date is before the start date.");
            if (line.DiagnosisPointers.Count == 0)
                errors.Add($"Line {n}: point to at least one diagnosis.");
            if (line.DiagnosisPointers.Any(p => p < 1 || p > Diagnoses.Count))
                errors.Add($"Line {n}: a diagnosis pointer doesn't match a diagnosis row.");
            if (line.Modifiers.Count > 4)
                errors.Add($"Line {n}: at most four modifiers.");
        }

        return errors;
    }

    /// <summary>What the examiner changed, one plain sentence per change.</summary>
    public IReadOnlyList<string> Changes() => _original.DiffAgainst(Snapshot.Of(this));

    /// <summary>
    /// The corrected claim payload: a copy of the original with the edited
    /// values written in, lines renumbered, diagnosis pointers renumbered,
    /// total charge and claim service dates recomputed from the lines.
    /// </summary>
    public JsonObject ApplyTo()
    {
        var claim = (JsonObject)_source.DeepClone();

        Set(claim, "memberId", MemberId.Trim());
        Set(claim, "billingProviderNPI", BillingProviderNpi.Trim());
        Set(claim, "renderingProviderNPI", Blank(RenderingProviderNpi));
        Set(claim, "placeOfServiceCode", PlaceOfServiceCode.Trim());
        Set(claim, "priorAuthorizationNumber", Blank(PriorAuthorizationNumber));

        var originalDx = (Get(_source, "diagnosisCodes") as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var dx = new JsonArray();
        for (var i = 0; i < Diagnoses.Count; i++)
        {
            var code = Diagnoses[i].Code.Trim().ToUpperInvariant().Replace(".", string.Empty);
            var existing = originalDx.FirstOrDefault(d =>
                string.Equals(Str(d, "code")?.Replace(".", string.Empty), code, StringComparison.OrdinalIgnoreCase));
            var node = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
            Set(node, "code", code);
            // Keep a known code's qualifier (institutional claims carry
            // admitting, reason-for-visit and other qualifiers); new codes are
            // principal when first, otherwise "other".
            if (existing is null) Set(node, "codeQualifier", i == 0 ? "ABK" : "ABF");
            Set(node, "pointerNumber", i + 1);
            if (existing is null || !string.Equals(Str(existing, "code"), code, StringComparison.OrdinalIgnoreCase))
            {
                Remove(node, "description");
            }
            dx.Add(node);
        }
        if (dx.Count > 0 && !dx.OfType<JsonObject>().Any(d => Str(d, "codeQualifier") is "ABK" or "BK"))
        {
            // The principal diagnosis was removed: the first code takes its place.
            Set((JsonObject)dx[0]!, "codeQualifier", "ABK");
        }
        Set(claim, "diagnosisCodes", dx);

        var lines = new JsonArray();
        for (var i = 0; i < Lines.Count; i++)
        {
            lines.Add(Lines[i].ToJson(i + 1));
        }
        Set(claim, "claimLines", lines);
        Set(claim, "totalChargeAmount", TotalCharge);

        var froms = Lines.Where(l => l.ServiceDateFrom.HasValue).Select(l => l.ServiceDateFrom!.Value.Date).ToList();
        var tos = Lines.Select(l => (l.ServiceDateTo ?? l.ServiceDateFrom)?.Date).Where(d => d.HasValue).Select(d => d!.Value).ToList();
        if (froms.Count > 0) SetDate(claim, "serviceDateFrom", froms.Min());
        if (tos.Count > 0) SetDate(claim, "serviceDateTo", tos.Max());

        return claim;
    }

    private static bool IsNpi(string? value) =>
        value is { Length: 10 } v && v.All(char.IsAsciiDigit);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── JSON helpers: the service writes camelCase, but be lenient on read ──

    internal static JsonNode? Get(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var exact)) return exact;
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    internal static void Set(JsonObject obj, string name, JsonNode? value)
    {
        var key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        obj[key] = value;
    }

    internal static void Remove(JsonObject obj, string name)
    {
        var key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key is not null) obj.Remove(key);
    }

    internal static string? Str(JsonObject obj, string name) => Get(obj, name) switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    internal static int? Int(JsonObject obj, string name) =>
        Get(obj, name) is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    internal static decimal Dec(JsonObject obj, string name, decimal fallback = 0m) => Get(obj, name) switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s)
                         && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => fallback,
    };

    internal static DateTime? Date(JsonObject obj, string name) =>
        Get(obj, name) is JsonValue v && v.TryGetValue<string>(out var s)
        && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.Date
            : null;

    /// <summary>
    /// Writes a calendar date. Service dates are dates, not instants: keep the
    /// original's UTC marker if it had one so the round trip doesn't shift a
    /// day in a non-UTC host, and leave the field untouched when unchanged.
    /// </summary>
    internal static void SetDate(JsonObject obj, string name, DateTime date)
    {
        var existing = Get(obj, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (existing is not null
            && DateTime.TryParse(existing, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var old)
            && old.Date == date.Date)
        {
            return;
        }
        var utc = existing?.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ?? false;
        Set(obj, name, date.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture) + (utc ? "Z" : string.Empty));
    }

    private sealed record Snapshot(
        string MemberId,
        string BillingNpi,
        string RenderingNpi,
        string Pos,
        string PriorAuth,
        IReadOnlyList<string> Diagnoses,
        IReadOnlyList<string> Lines)
    {
        public static Snapshot Of(ClaimEditModel m) => new(
            m.MemberId.Trim(),
            m.BillingProviderNpi.Trim(),
            (m.RenderingProviderNpi ?? string.Empty).Trim(),
            m.PlaceOfServiceCode.Trim(),
            (m.PriorAuthorizationNumber ?? string.Empty).Trim(),
            m.Diagnoses.Select(d => d.Code.Trim().ToUpperInvariant().Replace(".", string.Empty)).ToList(),
            m.Lines.Select(l => l.Describe()).ToList());

        public IReadOnlyList<string> DiffAgainst(Snapshot now)
        {
            var changes = new List<string>();
            void Field(string label, string before, string after)
            {
                if (!string.Equals(before, after, StringComparison.Ordinal))
                    changes.Add($"{label}: {Show(before)} → {Show(after)}");
            }

            Field("Member ID", MemberId, now.MemberId);
            Field("Billing NPI", BillingNpi, now.BillingNpi);
            Field("Rendering NPI", RenderingNpi, now.RenderingNpi);
            Field("Place of service", Pos, now.Pos);
            Field("Prior auth", PriorAuth, now.PriorAuth);
            Field("Diagnoses", string.Join(", ", Diagnoses), string.Join(", ", now.Diagnoses));

            var count = Math.Max(Lines.Count, now.Lines.Count);
            for (var i = 0; i < count; i++)
            {
                var before = i < Lines.Count ? Lines[i] : null;
                var after = i < now.Lines.Count ? now.Lines[i] : null;
                if (before is null) changes.Add($"Line {i + 1} added: {after}");
                else if (after is null) changes.Add($"Line {i + 1} removed: {before}");
                else if (before != after) changes.Add($"Line {i + 1}: {before} → {after}");
            }

            return changes;
        }

        private static string Show(string value) => value.Length == 0 ? "(blank)" : value;
    }
}

public sealed class ClaimEditDiagnosis
{
    public string Code { get; set; } = string.Empty;
}

public sealed class ClaimEditLine
{
    private JsonObject? _source;

    public string ProcedureCode { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
    public List<int> DiagnosisPointers { get; set; } = new();
    public decimal Units { get; set; } = 1;
    public decimal ChargeAmount { get; set; }
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }

    /// <summary>Line place of service; blank means the claim's.</summary>
    public string? PlaceOfServiceCode { get; set; }

    /// <summary>Modifiers as the editor shows them, e.g. "25, 59".</summary>
    public string ModifiersText
    {
        get => string.Join(", ", Modifiers);
        set => Modifiers = Split(value).Select(m => m.ToUpperInvariant()).ToList();
    }

    /// <summary>Diagnosis pointers as the editor shows them, e.g. "1, 2".</summary>
    public string DiagnosisPointersText
    {
        get => string.Join(", ", DiagnosisPointers);
        set => DiagnosisPointers = Split(value)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToList();
    }

    internal static ClaimEditLine FromJson(JsonObject line, DateTime? claimFrom, DateTime? claimTo)
    {
        var from = ClaimEditModel.Date(line, "serviceDateFrom");
        if (from is { Year: <= 1 }) from = null;
        var to = ClaimEditModel.Date(line, "serviceDateTo");
        if (to is { Year: <= 1 }) to = null;

        return new ClaimEditLine
        {
            _source = line,
            ProcedureCode = ClaimEditModel.Str(line, "procedureCode") ?? string.Empty,
            Modifiers = Strings(ClaimEditModel.Get(line, "modifiers")),
            DiagnosisPointers = (ClaimEditModel.Get(line, "diagnosisPointers") as JsonArray)?
                .OfType<JsonValue>()
                .Select(v => v.TryGetValue<int>(out var n) ? n : 0)
                .ToList() ?? new(),
            Units = ClaimEditModel.Dec(line, "units", 1m),
            ChargeAmount = ClaimEditModel.Dec(line, "chargeAmount"),
            ServiceDateFrom = from ?? claimFrom,
            ServiceDateTo = to ?? from ?? claimTo,
            PlaceOfServiceCode = ClaimEditModel.Str(line, "placeOfServiceCode"),
        };
    }

    internal JsonObject ToJson(int lineNumber)
    {
        var node = _source is null ? new JsonObject() : (JsonObject)_source.DeepClone();
        var procedureChanged = _source is null
            || !string.Equals(ClaimEditModel.Str(_source, "procedureCode"), ProcedureCode.Trim(), StringComparison.OrdinalIgnoreCase);

        ClaimEditModel.Set(node, "lineNumber", lineNumber);
        ClaimEditModel.Set(node, "procedureCode", ProcedureCode.Trim().ToUpperInvariant());
        if (procedureChanged) ClaimEditModel.Remove(node, "procedureDescription");
        ClaimEditModel.Set(node, "modifiers", new JsonArray(Modifiers.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()));
        ClaimEditModel.Set(node, "diagnosisPointers", new JsonArray(DiagnosisPointers.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()));
        ClaimEditModel.Set(node, "units", Units);
        ClaimEditModel.Set(node, "chargeAmount", ChargeAmount);
        if (ServiceDateFrom is { } from) ClaimEditModel.SetDate(node, "serviceDateFrom", from);
        if ((ServiceDateTo ?? ServiceDateFrom) is { } to) ClaimEditModel.SetDate(node, "serviceDateTo", to);
        ClaimEditModel.Set(node, "placeOfServiceCode",
            string.IsNullOrWhiteSpace(PlaceOfServiceCode) ? null : PlaceOfServiceCode.Trim());
        // The corrected version is re-adjudicated from scratch.
        ClaimEditModel.Remove(node, "adjudicationResult");
        return node;
    }

    internal string Describe()
    {
        var mods = Modifiers.Count > 0 ? "-" + string.Join("-", Modifiers) : string.Empty;
        var date = ServiceDateFrom?.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) ?? "no date";
        return $"{ProcedureCode.Trim().ToUpperInvariant()}{mods} × {Units.ToString("0.##", CultureInfo.InvariantCulture)} " +
               $"{ChargeAmount.ToString("C", CultureInfo.GetCultureInfo("en-US"))} on {date} dx {string.Join(",", DiagnosisPointers)}";
    }

    private static List<string> Strings(JsonNode? node) =>
        (node as JsonArray)?.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList() ?? new();

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
