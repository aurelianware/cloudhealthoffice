using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationService.Models;

namespace AuthorizationService.Import;

/// <summary>
/// Reads X12 005010X217 278 transactions (a core's or clearinghouse's
/// request and response files) into <see cref="AuthorizationImportCandidate"/>s.
///
/// <para>Each <b>response</b> (BHT02 <c>11</c>) patient event (HL <c>EV</c>,
/// loop 2000E) is one authorization. Its number is the certification number
/// (HCR02, at the event or else on a service line), or else the administrative
/// reference (REF*NT). Its status is the event's HCR01, or else it is derived
/// from the service lines' HCR01s.</para>
///
/// <para>A <b>request</b> (BHT02 <c>13</c>) in the same file or upload is
/// paired with its response by the event's trace number (TRN02 and TRN03, which
/// the response echoes). The request supplies when the request was received
/// (its BHT04 and BHT05) and the quantities requested. A response without its
/// request still loads: its own date stands in for the receipt, with a warning,
/// because decision timeframes can't be measured from it. A request with no
/// response has nothing to load, since it has no decision and no number, and
/// is reported.</para>
///
/// <para>The content hash covers the event's own segments and those of the
/// parties above it, never the envelope (ISA, GS, ST or BHT control numbers
/// change every time a file is re-sent). So the same response re-sent in
/// tomorrow's file is unchanged.</para>
/// </summary>
public static class X12_278Reader
{
    public const string Format = "x12-278";

    public sealed class ParsedFile
    {
        public int TransactionSets { get; set; }
        public List<string> FileErrors { get; } = [];
        public List<AuthorizationImportCandidate> Candidates { get; } = [];
    }

    internal sealed record Segment(string Id, string[] Elements)
    {
        public string? E(int position)
            => position >= 1 && position <= Elements.Length && Elements[position - 1].Length > 0 ? Elements[position - 1].Trim() : null;

        public string Text => Id + "*" + string.Join('*', Elements);
    }

    /// <summary>One HL loop: its level code, parent, and the segments it holds.</summary>
    private sealed class Loop
    {
        public required string Id { get; init; }
        public string? ParentId { get; init; }
        public required string Level { get; init; }
        public List<Segment> Segments { get; } = [];
        public Segment? First(string id, Func<Segment, bool>? where = null)
            => Segments.FirstOrDefault(s => s.Id == id && (where is null || where(s)));
    }

    private sealed class Transaction
    {
        public bool IsResponse { get; init; }
        public DateTime? CreatedAt { get; init; }
        public Dictionary<string, Loop> Loops { get; } = new(StringComparer.Ordinal);
        public List<Loop> Order { get; } = [];
    }

    public static ParsedFile Read(string content, AuthorizationImportOptions options)
    {
        var parsed = new ParsedFile();
        List<Segment> segments;
        char component;
        try
        {
            (segments, component) = Tokenize(content);
        }
        catch (FormatException ex)
        {
            parsed.FileErrors.Add(ex.Message);
            return parsed;
        }

        var transactions = Split(segments, parsed);
        if (transactions.Count == 0)
        {
            if (parsed.FileErrors.Count == 0)
                parsed.FileErrors.Add("The file holds no 278 transaction sets (ST*278).");
            return parsed;
        }

        // Requests by the trace their responses echo.
        var requests = new Dictionary<string, (Transaction Tx, Loop Event)>(StringComparer.Ordinal);
        foreach (var tx in transactions.Where(t => !t.IsResponse))
        {
            foreach (var ev in tx.Order.Where(l => l.Level == "EV"))
            {
                if (TraceKey(ev) is { } key)
                    requests.TryAdd(key, (tx, ev));
            }
        }

        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tx in transactions.Where(t => t.IsResponse))
        {
            foreach (var ev in tx.Order.Where(l => l.Level == "EV"))
            {
                var key = TraceKey(ev);
                (Transaction Tx, Loop Event)? request = key is not null && requests.TryGetValue(key, out var r) ? r : null;
                if (key is not null && request is not null)
                    answered.Add(key);
                parsed.Candidates.Add(Map(tx, ev, request, component, options));
            }
        }

        var unanswered = requests.Keys.Count(k => !answered.Contains(k));
        if (unanswered > 0)
        {
            parsed.FileErrors.Add(
                $"{unanswered} request(s) have no response in this file. A request alone has no decision or " +
                "authorization number to load; send it with its response.");
        }
        return parsed;
    }

    private static AuthorizationImportCandidate Map(
        Transaction tx, Loop ev, (Transaction Tx, Loop Event)? request, char component, AuthorizationImportOptions options)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var chain = Ancestors(tx, ev);
        var subscriber = chain.FirstOrDefault(l => l.Level == "22");
        var dependent = chain.FirstOrDefault(l => l.Level == "23");
        var requester = chain.FirstOrDefault(l => l.Level == "21");
        var services = tx.Order.Where(l => l.Level == "SS" && l.ParentId == ev.Id).ToList();

        // The number: the certification number, from the event or a line, else the administrative reference.
        var number = ev.First("HCR")?.E(2)
                     ?? services.Select(s => s.First("HCR")?.E(2)).FirstOrDefault(n => n is not null)
                     ?? ev.First("REF", s => s.E(1) == "NT")?.E(2);
        var label = number ?? $"HL {ev.Id}";

        // Member: the patient's own id when a dependent loop names one, else the subscriber's.
        var subscriberName = subscriber?.First("NM1", s => s.E(1) == "IL");
        var dependentName = dependent?.First("NM1", s => s.E(1) == "QC");
        var memberId = dependentName?.E(9) ?? subscriberName?.E(9);
        if (dependent is not null && dependentName?.E(9) is null && memberId is not null)
            warnings.Add("The patient is a dependent with no id of their own; filed under the subscriber's member id.");
        var patientName = dependentName ?? subscriberName;
        var demographics = (dependent ?? subscriber)?.First("DMG");

        // Status: the event's decision, or the lines' decisions combined.
        var eventAction = ev.First("HCR")?.E(1);
        var lineActions = services.Select(s => s.First("HCR")?.E(1)).Where(a => a is not null).Select(a => a!).ToList();
        var action = eventAction ?? Combine(lineActions);
        (AuthorizationStatus Status, string? Decision)? status = action switch
        {
            null => null,
            "CT" => (AuthorizationStatus.Pended, "A4"),
            _ => AuthorizationImportValues.Status(action),
        };
        if (action == "CT")
            warnings.Add("HCR01 CT (contact payer) is stored as pended.");
        if (action == "NA")
            errors.Add("HCR01 NA (no action required): no authorization was needed, so there is nothing to load.");
        else if (status is null)
            errors.Add(action is null ? "The response carries no decision (HCR01)." : $"HCR01 '{action}' is not a 278 action code.");

        if (number is null)
            errors.Add("No certification number (HCR02) or administrative reference (REF*NT) to identify the authorization by.");
        else if (number.Length > 50)
            errors.Add("The certification number is longer than 50 characters.");
        if (memberId is null)
            errors.Add("No member id (2010C NM109).");

        var requestingNpi = Npi(requester?.First("NM1"));
        if (requestingNpi is null)
            errors.Add("No requesting provider NPI (2010B NM108 XX).");

        var eventProviders = ev.Segments.Where(s => s.Id == "NM1").ToList();
        var servicing = Npi(eventProviders.FirstOrDefault(s => s.E(1) is "SJ" or "71" or "72" or "DK" && s.E(8) == "XX"));
        var facility = Npi(eventProviders.FirstOrDefault(s => s.E(1) == "FA" && s.E(8) == "XX"));

        var decidedAt = tx.CreatedAt;
        var receivedAt = request?.Tx.CreatedAt;
        if (receivedAt is null)
        {
            receivedAt = decidedAt;
            warnings.Add("The request is not in this file; the response's date stands in for when it was received, so its decision timeframe cannot be reported.");
        }
        if (receivedAt is null)
            errors.Add("The transaction has no date (BHT04).");

        var um = ev.First("UM");
        var requestedPeriod = DatePeriod(ev.First("DTP", s => s.E(1) == "472"));
        var admission = DatePeriod(ev.First("DTP", s => s.E(1) == "435"));
        var certified = DatePeriod(ev.First("DTP", s => s.E(1) == "AAH"));
        var expiration = DatePeriod(ev.First("DTP", s => s.E(1) == "036")).From;

        var diagnoses = Diagnoses(ev.First("HI"), component, warnings);
        var requestServices = request is { } req
            ? req.Tx.Order.Where(l => l.Level == "SS" && l.ParentId == req.Event.Id).ToList()
            : [];
        var lines = Lines(services, requestServices, component, warnings);

        var hash = Hash(chain.Append(ev).Concat(services).Concat(request is { } rq ? [rq.Event] : []).Concat(requestServices));
        var fileLines = new List<int>();

        if (errors.Count > 0 || status is null || number is null || memberId is null || requestingNpi is null || receivedAt is null)
        {
            return new AuthorizationImportCandidate
            {
                AuthorizationNumber = label, ContentHash = hash, SourceMemberId = memberId,
                FileLines = fileLines, Errors = errors, Warnings = warnings,
            };
        }

        var decided = AuthorizationImportValues.IsDecided(status.Value.Status);
        var authorization = new Authorization
        {
            AuthorizationNumber = number,
            MemberId = memberId,
            PatientLastName = patientName?.E(3) ?? string.Empty,
            PatientFirstName = patientName?.E(4) ?? string.Empty,
            PatientDateOfBirth = demographics?.E(1) == "D8" ? AuthorizationImportValues.Date(demographics.E(2)) ?? default : default,
            LineOfBusiness = options.LineOfBusiness,
            RequestingProviderNPI = requestingNpi,
            RequestingProviderName = Name(requester?.First("NM1")),
            ServicingProviderNPI = servicing,
            FacilityNPI = facility,
            AuthorizationType = um?.E(1) switch
            {
                "SC" => AuthorizationType.Referral,
                "AR" when um.E(2) is "R" or "S" => AuthorizationType.ConcurrentReview,
                _ => AuthorizationType.PreAuthorization,
            },
            CertificationType = um?.E(2) is "I" or "R" or "S" ? um.E(2)! : "I",
            ServiceTypeCode = um?.E(3) is { Length: <= 2 } serviceType ? serviceType : null,
            LevelOfService = AuthorizationImportValues.LevelOfService(um?.E(6)),
            RequestedServiceDateFrom = requestedPeriod.From ?? admission.From ?? certified.From ?? receivedAt.Value,
            RequestedServiceDateTo = requestedPeriod.To ?? admission.To,
            ApprovedServiceDateFrom = decided && status.Value.Status != AuthorizationStatus.Denied ? certified.From : null,
            ApprovedServiceDateTo = decided && status.Value.Status != AuthorizationStatus.Denied ? certified.To : null,
            ExpirationDate = expiration,
            DiagnosisCodes = diagnoses,
            RequestedServices = lines,
            Status = status.Value.Status,
            ReviewDecision = status.Value.Decision,
            DenialReasonCode = status.Value.Status is AuthorizationStatus.Denied or AuthorizationStatus.Modified
                ? ev.First("HCR")?.E(3) ?? services.Select(s => s.First("HCR")?.E(3)).FirstOrDefault(c => c is not null)
                : null,
            SubmittedDate = receivedAt.Value,
            ReviewedDate = decided ? decidedAt : null,
        };

        return new AuthorizationImportCandidate
        {
            AuthorizationNumber = number, Authorization = authorization, ContentHash = hash, SourceMemberId = memberId,
            FileLines = fileLines, Errors = errors, Warnings = warnings,
        };
    }

    /// <summary>The lines' decisions as one: all certified is approved, all denied is denied, a pend anywhere is pended, anything else is modified.</summary>
    private static string? Combine(List<string> actions)
    {
        if (actions.Count == 0) return null;
        if (actions.Contains("A4")) return "A4";
        if (actions.All(a => a == "A1")) return "A1";
        if (actions.All(a => a == "A3")) return "A3";
        if (actions.All(a => a == "C")) return "C";
        return "A2";
    }

    private static List<RequestedService> Lines(
        List<Loop> responseLines, List<Loop> requestLines, char component, List<string> warnings)
    {
        var lines = new List<RequestedService>();
        for (var i = 0; i < responseLines.Count; i++)
        {
            var loop = responseLines[i];
            var service = Service(loop, component);
            if (service is null)
                continue;

            // The request's line: the same trace, else the same position.
            var trace = loop.First("TRN")?.E(2);
            var requested = requestLines.FirstOrDefault(r => trace is not null && r.First("TRN")?.E(2) == trace)
                            ?? (i < requestLines.Count ? requestLines[i] : null);
            var requestedService = requested is null ? null : Service(requested, component);

            var action = loop.First("HCR")?.E(1);
            var decision = action is null ? null : AuthorizationImportValues.Status(action)?.Decision;
            var certifiedQuantity = loop.First("HSD")?.E(2) is { } hsd && decimal.TryParse(hsd, NumberStyles.Number, CultureInfo.InvariantCulture, out var q)
                ? q
                : service.Value.Quantity;

            lines.Add(new RequestedService
            {
                LineNumber = lines.Count + 1,
                ProcedureCode = service.Value.Code,
                ProductOrServiceSystem = service.Value.System,
                Modifiers = service.Value.Modifiers,
                RequestedUnits = requestedService?.Quantity ?? service.Value.Quantity ?? 1m,
                ApprovedUnits = decision is "A1" or "A2" ? certifiedQuantity : decision == "A3" ? 0m : null,
                UnitType = service.Value.Unit,
                PlaceOfServiceCode = service.Value.PlaceOfService,
                RevenueCode = service.Value.RevenueCode,
                ServiceStatus = decision,
                DenialReasonCode = decision is "A2" or "A3" ? loop.First("HCR")?.E(3) : null,
            });
        }
        if (responseLines.Count > 0 && lines.Count < responseLines.Count)
            warnings.Add($"{responseLines.Count - lines.Count} service line(s) name no procedure (SV1, SV2 or SV3) and were left out.");
        return lines;
    }

    private readonly record struct ServiceCode(
        string Code, string? System, List<string> Modifiers, decimal? Quantity, string? Unit, string? PlaceOfService, string? RevenueCode);

    /// <summary>A line's code and quantity: SV1 (professional), SV2 (institutional) or SV3 (dental).</summary>
    private static ServiceCode? Service(Loop loop, char component)
    {
        static decimal? Quantity(string? value)
            => value is not null && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) && q > 0 ? q : null;

        static (string Code, string? System, List<string> Modifiers)? Composite(string? value, char component)
        {
            if (value is null) return null;
            var parts = value.Split(component);
            if (parts.Length < 2 || parts[1].Length == 0) return null;
            var system = parts[0] switch { "N4" => "NDC", "HC" or "AD" or "ER" or "IV" or "WK" => null, _ => null };
            return (parts[1].ToUpperInvariant(), system, parts.Skip(2).Take(4).Where(m => m.Length > 0).Select(m => m.ToUpperInvariant()).ToList());
        }

        if (loop.First("SV1") is { } sv1 && Composite(sv1.E(1), component) is { } professional)
            return new ServiceCode(professional.Code, professional.System, professional.Modifiers, Quantity(sv1.E(4)), sv1.E(3), sv1.E(5) is { Length: 2 } pos ? pos : null, null);
        if (loop.First("SV2") is { } sv2)
        {
            var procedure = Composite(sv2.E(2), component);
            var revenue = sv2.E(1) is { Length: <= 4 } rc && rc.All(char.IsAsciiDigit) ? rc : null;
            var code = procedure?.Code ?? revenue;
            if (code is not null)
                return new ServiceCode(code, procedure?.System, procedure?.Modifiers ?? [], Quantity(sv2.E(5)), sv2.E(4), null, revenue);
        }
        if (loop.First("SV3") is { } sv3 && Composite(sv3.E(1), component) is { } dental)
            return new ServiceCode(dental.Code, dental.System, dental.Modifiers, Quantity(sv3.E(6)) ?? 1m, "UN", sv3.E(3) is { Length: 2 } dpos ? dpos : null, null);
        return null;
    }

    private static List<DiagnosisCode> Diagnoses(Segment? hi, char component, List<string> warnings)
    {
        var codes = new List<DiagnosisCode>();
        if (hi is null) return codes;
        foreach (var element in hi.Elements)
        {
            var parts = element.Split(component);
            if (parts.Length < 2) continue;
            if (parts[0] is not ("ABK" or "ABF" or "ABJ" or "ABN"))
            {
                warnings.Add($"Diagnosis qualifier '{parts[0]}' is not ICD-10-CM; left out.");
                continue;
            }
            codes.AddRange(AuthorizationImportValues.DiagnosisCodes(parts[1], warnings).Where(c => codes.All(x => x.Code != c.Code)));
        }
        for (var i = 0; i < codes.Count; i++)
            codes[i].CodeQualifier = i == 0 ? "BK" : "BF";
        return codes;
    }

    private static (DateTime? From, DateTime? To) DatePeriod(Segment? dtp)
    {
        if (dtp?.E(3) is not { } value) return (null, null);
        if (dtp.E(2) == "RD8" && value.Split('-') is [var from, var to])
            return (AuthorizationImportValues.Date(from), AuthorizationImportValues.Date(to));
        return (AuthorizationImportValues.Date(value.Length >= 8 ? value[..8] : value), null);
    }

    private static string? Npi(Segment? nm1)
        => nm1?.E(8) == "XX" && nm1.E(9) is { Length: 10 } npi && npi.All(char.IsAsciiDigit) ? npi : null;

    private static string? Name(Segment? nm1)
    {
        if (nm1 is null) return null;
        var name = nm1.E(2) == "2" ? nm1.E(3) : string.Join(' ', new[] { nm1.E(4), nm1.E(3) }.Where(p => p is not null));
        return string.IsNullOrWhiteSpace(name) ? null : name.Length > 300 ? name[..300] : name;
    }

    private static string? TraceKey(Loop ev)
        => ev.Segments.FirstOrDefault(s => s.Id == "TRN") is { } trn && trn.E(2) is { } trace ? $"{trace}|{trn.E(3)}" : null;

    private static List<Loop> Ancestors(Transaction tx, Loop loop)
    {
        var chain = new List<Loop>();
        var parent = loop.ParentId;
        while (parent is not null && tx.Loops.TryGetValue(parent, out var p) && chain.Count < 10)
        {
            chain.Insert(0, p);
            parent = p.ParentId;
        }
        return chain;
    }

    private static string Hash(IEnumerable<Loop> loops)
    {
        var text = new StringBuilder(Format).Append('\n');
        foreach (var loop in loops)
        {
            text.Append("HL ").Append(loop.Level).Append('\n');
            foreach (var segment in loop.Segments)
                text.Append(segment.Text).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static List<Transaction> Split(List<Segment> segments, ParsedFile parsed)
    {
        var transactions = new List<Transaction>();
        Transaction? current = null;
        Loop? loop = null;
        var inSet = false;
        foreach (var segment in segments)
        {
            switch (segment.Id)
            {
                case "ST":
                    inSet = segment.E(1) == "278";
                    if (!inSet)
                        parsed.FileErrors.Add($"Transaction set {segment.E(1)} (ST02 {segment.E(2)}) is not a 278 and was skipped.");
                    current = null;
                    loop = null;
                    continue;
                case "SE":
                    if (current is not null) transactions.Add(current);
                    parsed.TransactionSets += inSet ? 1 : 0;
                    current = null;
                    loop = null;
                    inSet = false;
                    continue;
                case "ISA" or "GS" or "GE" or "IEA":
                    continue;
            }
            if (!inSet)
                continue;
            if (segment.Id == "BHT")
            {
                current = new Transaction { IsResponse = segment.E(2) == "11", CreatedAt = BhtDate(segment) };
                continue;
            }
            if (current is null)
                continue;
            if (segment.Id == "HL")
            {
                loop = new Loop { Id = segment.E(1) ?? string.Empty, ParentId = segment.E(2), Level = segment.E(3) ?? string.Empty };
                current.Loops[loop.Id] = loop;
                current.Order.Add(loop);
                continue;
            }
            loop?.Segments.Add(segment);
        }
        return transactions;
    }

    private static DateTime? BhtDate(Segment bht)
    {
        if (bht.E(4) is not { Length: 8 } date) return null;
        var time = bht.E(5) is { Length: >= 4 } t && t.All(char.IsAsciiDigit) ? t[..4] : "0000";
        return DateTime.TryParseExact(date + time, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
            : null;
    }

    /// <summary>Splits X12 text into segments, taking the delimiters from the ISA as the standard defines them.</summary>
    internal static (List<Segment> Segments, char Component) Tokenize(string content)
    {
        var trimmed = (content ?? string.Empty).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!trimmed.StartsWith("ISA", StringComparison.Ordinal))
            throw new FormatException("The file does not start with an ISA segment: it is not an X12 interchange.");
        if (trimmed.Length < 106)
            throw new FormatException("The ISA segment is shorter than 106 characters.");

        var element = trimmed[3];
        var component = trimmed[104];
        var terminator = trimmed[105];
        var segments = new List<Segment>();
        foreach (var raw in trimmed.Split(terminator))
        {
            var text = raw.Trim('\r', '\n', ' ', '\t');
            if (text.Length == 0) continue;
            var parts = text.Split(element);
            segments.Add(new Segment(parts[0], parts[1..]));
        }
        return (segments, component);
    }
}
