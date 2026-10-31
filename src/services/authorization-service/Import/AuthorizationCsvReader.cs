using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Import.Cpcds;

namespace AuthorizationService.Import;

/// <summary>
/// Reads the authorization CSV (comma, pipe or tab delimited, header row
/// first) into <see cref="AuthorizationImportCandidate"/>s: rows are grouped
/// by authorization number, the first row supplies the header fields, and
/// each row with a procedure code is a service line. Values are checked here;
/// whether to store them is <see cref="AuthorizationImportService"/>'s call.
/// </summary>
public static class AuthorizationCsvReader
{
    public const string Format = "csv";

    public sealed class ParsedFile
    {
        public char Delimiter { get; init; }
        public int RowsRead { get; set; }
        public List<string> FileErrors { get; } = [];
        public List<string> UnrecognizedColumns { get; set; } = [];
        public List<AuthorizationImportCandidate> Candidates { get; } = [];
    }

    private sealed record Row(int LineNumber, IReadOnlyList<string> Fields, Dictionary<AuthorizationCsvColumn, string> Values)
    {
        public string? Get(AuthorizationCsvColumn column)
            => Values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    public static ParsedFile Read(string content, AuthorizationImportOptions options)
    {
        content ??= string.Empty;
        var delimiter = CpcdsDelimitedReader.DetectDelimiter(content);
        var parsed = new ParsedFile { Delimiter = delimiter };

        using var records = CpcdsDelimitedReader.Read(content, delimiter).GetEnumerator();
        if (!records.MoveNext())
        {
            parsed.FileErrors.Add("The file is empty: an authorization file starts with a header row.");
            return parsed;
        }

        var (positions, unrecognized) = AuthorizationCsvColumns.Resolve(records.Current.Fields, options.ColumnMap);
        parsed.UnrecognizedColumns = unrecognized;
        foreach (var required in AuthorizationCsvColumns.Required.Where(c => !positions.ContainsKey(c)))
        {
            parsed.FileErrors.Add(
                $"Required column \"{AuthorizationCsvColumns.DisplayName(required)}\" is missing. " +
                "If the file names it differently, supply a column map.");
        }
        if (parsed.FileErrors.Count > 0)
            return parsed;

        var groups = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var order = new List<string>();
        var orphans = new List<int>();
        while (records.MoveNext())
        {
            parsed.RowsRead++;
            var record = records.Current;
            var values = new Dictionary<AuthorizationCsvColumn, string>(positions.Count);
            foreach (var (column, index) in positions)
            {
                if (index < record.Fields.Count)
                    values[column] = record.Fields[index];
            }
            var row = new Row(record.LineNumber, record.Fields, values);
            var number = row.Get(AuthorizationCsvColumn.AuthorizationNumber);
            if (number is null)
            {
                orphans.Add(record.LineNumber);
                continue;
            }
            if (!groups.TryGetValue(number, out var rows))
            {
                groups[number] = rows = [];
                order.Add(number);
            }
            rows.Add(row);
        }

        if (orphans.Count > 0)
        {
            parsed.FileErrors.Add(
                $"{orphans.Count} row(s) have no authorization number and were skipped " +
                $"(file lines {string.Join(", ", orphans.Take(20))}{(orphans.Count > 20 ? ", ..." : string.Empty)}).");
        }

        foreach (var number in order)
            parsed.Candidates.Add(Map(number, groups[number], options));
        return parsed;
    }

    private static AuthorizationImportCandidate Map(string number, List<Row> rows, AuthorizationImportOptions options)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var first = rows[0];

        string? Header(AuthorizationCsvColumn column)
        {
            var value = first.Get(column);
            if (value is not null
                && rows.Skip(1).Select(r => r.Get(column)).Any(other => other is not null && other != value))
            {
                warnings.Add($"Rows disagree on \"{AuthorizationCsvColumns.DisplayName(column)}\"; the first row's value was used.");
            }
            return value;
        }

        if (number.Length > 50)
            errors.Add("Authorization number is longer than 50 characters.");

        var memberId = Header(AuthorizationCsvColumn.MemberId);
        if (memberId is null)
            errors.Add("Member id is required.");
        else if (memberId.Length > 50)
            errors.Add("Member id is longer than 50 characters.");

        var statusText = Header(AuthorizationCsvColumn.Status);
        var status = AuthorizationImportValues.Status(statusText);
        if (status is null)
            errors.Add($"Status '{statusText}' is not recognized. Use approved, modified, denied, pended, in_review, submitted, cancelled or expired (or an X12 HCR01 code).");

        var received = Date(Header(AuthorizationCsvColumn.ReceivedDate), "Received date", errors, required: true);
        var decided = Date(Header(AuthorizationCsvColumn.DecisionDate), "Decision date", errors);
        if (status is { } s && AuthorizationImportValues.IsDecided(s.Status) && decided is null)
            warnings.Add("A decided authorization without a decision date: decision timeframes cannot be reported for it.");
        if (received is { } r && decided is { } d && d < r)
            errors.Add("Decision date is before the received date.");

        var requesting = Npi(Header(AuthorizationCsvColumn.RequestingProviderNpi), "Requesting provider NPI", errors, required: true);
        var servicing = Npi(Header(AuthorizationCsvColumn.ServicingProviderNpi), "Servicing provider NPI", errors);
        var facility = Npi(Header(AuthorizationCsvColumn.FacilityNpi), "Facility NPI", errors);

        var typeText = Header(AuthorizationCsvColumn.RequestType);
        var type = AuthorizationImportValues.RequestType(typeText);
        if (type == AuthorizationType.Unknown)
            warnings.Add($"Request type '{typeText}' is not recognized; stored as unknown.");

        var serviceType = Header(AuthorizationCsvColumn.ServiceTypeCode);
        if (serviceType is not null && (serviceType.Length > 2 || !serviceType.All(char.IsLetterOrDigit)))
        {
            warnings.Add($"Service type code '{serviceType}' is not an X12 service type code; left out.");
            serviceType = null;
        }

        var levelText = Header(AuthorizationCsvColumn.LevelOfService);
        var level = AuthorizationImportValues.LevelOfService(levelText);
        if (levelText is not null && level is null)
            warnings.Add($"Level of service '{levelText}' is not recognized; use urgent or standard.");

        var lob = options.LineOfBusiness;
        var lobText = Header(AuthorizationCsvColumn.LineOfBusiness);
        if (lobText is not null && (!Enum.TryParse(lobText, ignoreCase: true, out lob) || !Enum.IsDefined(lob) || lob == LineOfBusiness.Unknown))
        {
            errors.Add($"Line of business '{lobText}' is not one of: Commercial, Medicare, Medicaid, Exchange, TRICARE, VA.");
            lob = options.LineOfBusiness;
        }

        var requestedFrom = Date(Header(AuthorizationCsvColumn.RequestedFromDate), "Requested from date", errors);
        var requestedTo = Date(Header(AuthorizationCsvColumn.RequestedToDate), "Requested to date", errors);
        var approvedFrom = Date(Header(AuthorizationCsvColumn.ApprovedFromDate), "Approved from date", errors);
        var approvedTo = Date(Header(AuthorizationCsvColumn.ApprovedToDate), "Approved to date", errors);
        var expiration = Date(Header(AuthorizationCsvColumn.ExpirationDate), "Expiration date", errors);
        var birthDate = Date(Header(AuthorizationCsvColumn.PatientBirthDate), "Patient birth date", errors);
        if (requestedFrom is null && approvedFrom is null && received is not null)
            warnings.Add("No requested or approved service dates; the received date stands in for the service start.");

        var diagnoses = AuthorizationImportValues.DiagnosisCodes(Header(AuthorizationCsvColumn.DiagnosisCodes), warnings);
        var lines = Lines(rows, errors, warnings);

        var hash = Hash(rows);
        if (errors.Count > 0 || status is null || received is null || memberId is null || requesting is null)
        {
            return new AuthorizationImportCandidate
            {
                AuthorizationNumber = number, ContentHash = hash, SourceMemberId = memberId,
                FileLines = rows.Select(x => x.LineNumber).ToList(), Errors = errors, Warnings = warnings,
            };
        }

        var authorization = new Authorization
        {
            AuthorizationNumber = number,
            MemberId = memberId,
            PatientFirstName = Header(AuthorizationCsvColumn.PatientFirstName) ?? string.Empty,
            PatientLastName = Header(AuthorizationCsvColumn.PatientLastName) ?? string.Empty,
            PatientDateOfBirth = birthDate ?? default,
            LineOfBusiness = lob,
            RequestingProviderNPI = requesting,
            RequestingProviderName = Header(AuthorizationCsvColumn.RequestingProviderName),
            ServicingProviderNPI = servicing,
            ServicingProviderName = Header(AuthorizationCsvColumn.ServicingProviderName),
            FacilityNPI = facility,
            FacilityName = Header(AuthorizationCsvColumn.FacilityName),
            AuthorizationType = type,
            ServiceTypeCode = serviceType,
            LevelOfService = level,
            RequestedServiceDateFrom = requestedFrom ?? approvedFrom ?? received.Value,
            RequestedServiceDateTo = requestedTo,
            ApprovedServiceDateFrom = approvedFrom,
            ApprovedServiceDateTo = approvedTo,
            ExpirationDate = expiration,
            DiagnosisCodes = diagnoses,
            RequestedServices = lines,
            Status = status.Value.Status,
            ReviewDecision = status.Value.Decision,
            DenialReasonCode = Header(AuthorizationCsvColumn.DenialReasonCode),
            DenialReason = Header(AuthorizationCsvColumn.DenialReason),
            SubmittedDate = received.Value,
            ReviewedDate = AuthorizationImportValues.IsDecided(status.Value.Status) ? decided : null,
        };

        return new AuthorizationImportCandidate
        {
            AuthorizationNumber = number, Authorization = authorization, ContentHash = hash, SourceMemberId = memberId,
            FileLines = rows.Select(x => x.LineNumber).ToList(), Errors = errors, Warnings = warnings,
        };
    }

    private static List<RequestedService> Lines(List<Row> rows, List<string> errors, List<string> warnings)
    {
        var lines = new List<RequestedService>();
        foreach (var row in rows)
        {
            var code = row.Get(AuthorizationCsvColumn.ProcedureCode);
            if (code is null)
                continue;
            var at = $"Line {row.LineNumber}";

            int? lineNumber = null;
            if (row.Get(AuthorizationCsvColumn.LineNumber) is { } lineText)
            {
                if (int.TryParse(lineText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                    lineNumber = n;
                else
                    errors.Add($"{at}: line number '{lineText}' is not a positive whole number.");
            }

            var requested = Units(row.Get(AuthorizationCsvColumn.RequestedUnits), $"{at}: requested units", errors) ?? 1m;
            if (requested <= 0)
                errors.Add($"{at}: requested units must be more than zero.");
            var approved = Units(row.Get(AuthorizationCsvColumn.ApprovedUnits), $"{at}: approved units", errors);

            var lineStatusText = row.Get(AuthorizationCsvColumn.LineStatus);
            var lineStatus = lineStatusText is null ? null : AuthorizationImportValues.Status(lineStatusText)?.Decision;
            if (lineStatusText is not null && lineStatus is null)
                warnings.Add($"{at}: line status '{lineStatusText}' is not a decision; left out.");

            var pos = row.Get(AuthorizationCsvColumn.PlaceOfService);
            if (pos is not null && (pos.Length > 2 || !pos.All(char.IsDigit)))
            {
                warnings.Add($"{at}: place of service '{pos}' is not a two-digit code; left out.");
                pos = null;
            }

            var revenue = row.Get(AuthorizationCsvColumn.RevenueCode);
            if (revenue is not null && (revenue.Length > 4 || !revenue.All(char.IsDigit)))
            {
                warnings.Add($"{at}: revenue code '{revenue}' is not a revenue code; left out.");
                revenue = null;
            }

            if (code.Length > 64)
            {
                errors.Add($"{at}: procedure code is longer than 64 characters.");
                continue;
            }

            lines.Add(new RequestedService
            {
                LineNumber = lineNumber,
                ProcedureCode = code.ToUpperInvariant(),
                ProductOrServiceSystem = AuthorizationImportValues.CodeSystem(row.Get(AuthorizationCsvColumn.CodeSystem)),
                ProcedureDescription = row.Get(AuthorizationCsvColumn.ProcedureDescription),
                Modifiers = AuthorizationImportValues.List(row.Get(AuthorizationCsvColumn.Modifiers)).Take(4).ToList(),
                RequestedUnits = requested,
                ApprovedUnits = approved,
                UnitType = row.Get(AuthorizationCsvColumn.UnitType) is { Length: <= 10 } unit ? unit : null,
                PlaceOfServiceCode = pos,
                RevenueCode = revenue,
                ServiceStatus = lineStatus,
                DenialReasonCode = row.Get(AuthorizationCsvColumn.LineDenialReasonCode),
                DenialReason = row.Get(AuthorizationCsvColumn.LineDenialReason),
            });
        }

        // A line number is the line's identity; files that give none are numbered in order.
        if (lines.Any(l => l.LineNumber is null))
        {
            if (lines.Any(l => l.LineNumber is not null))
                errors.Add("Some service lines have a line number and some do not; give every line one or none.");
            else
                for (var i = 0; i < lines.Count; i++) lines[i].LineNumber = i + 1;
        }
        if (lines.GroupBy(l => l.LineNumber).Any(g => g.Count() > 1))
            errors.Add("Two service lines have the same line number.");
        return lines;
    }

    private static DateTime? Date(string? value, string name, List<string> errors, bool required = false)
    {
        if (value is null)
        {
            if (required) errors.Add($"{name} is required.");
            return null;
        }
        var date = AuthorizationImportValues.Date(value);
        if (date is null)
            errors.Add($"{name} '{value}' is not a date (use yyyy-MM-dd, yyyyMMdd, MM/dd/yyyy or ISO 8601).");
        return date;
    }

    private static string? Npi(string? value, string name, List<string> errors, bool required = false)
    {
        if (value is null)
        {
            if (required) errors.Add($"{name} is required.");
            return null;
        }
        if (value.Length != 10 || !value.All(char.IsAsciiDigit))
        {
            errors.Add($"{name} '{value}' is not a 10-digit NPI.");
            return null;
        }
        return value;
    }

    private static decimal? Units(string? value, string name, List<string> errors)
    {
        if (value is null)
            return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var units) && units >= 0)
            return units;
        errors.Add($"{name} '{value}' is not a number.");
        return null;
    }

    /// <summary>The rows' raw fields, in order: a reload of the same rows hashes the same.</summary>
    private static string Hash(List<Row> rows)
    {
        var text = new StringBuilder(Format).Append('\n');
        foreach (var row in rows)
        {
            foreach (var (column, value) in row.Values.OrderBy(v => v.Key))
                text.Append((int)column).Append('=').Append(value.Trim()).Append('\u001f');
            text.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
