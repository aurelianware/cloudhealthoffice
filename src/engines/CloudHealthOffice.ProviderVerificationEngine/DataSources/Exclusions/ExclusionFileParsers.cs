namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Globalization;
using CloudHealthOffice.ProviderVerificationEngine.Models;

/// <summary>Thrown when a downloaded file is not the expected exclusion list (wrong/HTML/empty file).</summary>
public class ExclusionFileFormatException : Exception
{
    public ExclusionFileFormatException(string message) : base(message) { }
}

/// <summary>Counters filled while an exclusion file is enumerated.</summary>
public sealed class ExclusionParseStats
{
    public int RowsRead { get; set; }
    public int RowsSkipped { get; set; }
}

/// <summary>
/// OIG LEIE <c>UPDATED.csv</c> parser. Columns are mapped by header name:
/// LASTNAME, FIRSTNAME, MIDNAME, BUSNAME, GENERAL, SPECIALTY, UPIN, NPI, DOB,
/// ADDRESS, CITY, STATE, ZIP, EXCLTYPE, EXCLDATE, REINDATE, WAIVERDATE, WVRSTATE.
/// Dates are yyyyMMdd; 00000000 / 0000000000 mean "none".
/// </summary>
public static class LeieCsvParser
{
    /// <summary>
    /// Lazily parse rows (the file is never materialized). Throws
    /// <see cref="ExclusionFileFormatException"/> on first enumeration when
    /// the header is not the expected format.
    /// </summary>
    public static IEnumerable<ExclusionRecord> Parse(TextReader reader, ExclusionParseStats? stats = null)
    {
        stats ??= new ExclusionParseStats();
        CsvHeaderMap? map = null;

        foreach (var row in CsvStreamReader.ReadRows(reader))
        {
            if (map is null)
            {
                map = new CsvHeaderMap(row);
                if (!map.Has("EXCLTYPE") || !map.Has("NPI") || !map.Has("LASTNAME") || !map.Has("BUSNAME"))
                {
                    throw new ExclusionFileFormatException(
                        "File is not an OIG LEIE CSV: header lacks EXCLTYPE/NPI/LASTNAME/BUSNAME");
                }
                continue;
            }

            stats.RowsRead++;
            var record = new ExclusionRecord
            {
                Source = ExclusionScreeningSource.OigLeie,
                LastName = map.Get(row, "LASTNAME"),
                FirstName = map.Get(row, "FIRSTNAME"),
                MiddleName = map.Get(row, "MIDNAME"),
                BusinessName = map.Get(row, "BUSNAME"),
                General = map.Get(row, "GENERAL"),
                Specialty = map.Get(row, "SPECIALTY"),
                Upin = map.Get(row, "UPIN"),
                Npi = map.Get(row, "NPI"),
                DobKey = ExclusionDates.ToKey(ExclusionDates.Parse(map.Get(row, "DOB"))),
                Address = map.Get(row, "ADDRESS"),
                City = map.Get(row, "CITY"),
                State = map.Get(row, "STATE"),
                Zip = map.Get(row, "ZIP"),
                ExclusionType = map.Get(row, "EXCLTYPE"),
                ExclusionDate = ExclusionDates.Parse(map.Get(row, "EXCLDATE")),
                EndDate = ExclusionDates.Parse(map.Get(row, "REINDATE")),
                WaiverDate = ExclusionDates.Parse(map.Get(row, "WAIVERDATE")),
                WaiverState = map.Get(row, "WVRSTATE"),
                IsActive = true
            }.Normalize();

            if (record.NormalizedLastName is null && record.NormalizedBusinessName is null && record.Npi is null)
            {
                stats.RowsSkipped++;
                continue;
            }

            yield return record;
        }

        if (map is null)
            throw new ExclusionFileFormatException("LEIE file is empty");
    }
}

/// <summary>
/// SAM.gov public exclusions extract (CSV inside the daily ZIP). Columns are
/// mapped by header name so column-order changes between extract versions do
/// not shift fields; known aliases are accepted for renamed columns.
/// </summary>
public static class SamExtractCsvParser
{
    /// <summary>
    /// Lazily parse rows (the file is never materialized). Throws
    /// <see cref="ExclusionFileFormatException"/> on first enumeration when
    /// the header is not the expected format.
    /// </summary>
    public static IEnumerable<ExclusionRecord> Parse(TextReader reader, ExclusionParseStats? stats = null)
    {
        stats ??= new ExclusionParseStats();
        CsvHeaderMap? map = null;

        foreach (var row in CsvStreamReader.ReadRows(reader))
        {
            if (map is null)
            {
                map = new CsvHeaderMap(row);
                if (!map.Has("Classification") || !map.Has("Name", "Last", "Last Name") ||
                    !map.Has("Exclusion Type") || !map.Has("NPI"))
                {
                    throw new ExclusionFileFormatException(
                        "File is not a SAM.gov exclusions extract: header lacks Classification/Name/Exclusion Type/NPI");
                }
                continue;
            }

            stats.RowsRead++;
            var classification = map.Get(row, "Classification");
            var first = map.Get(row, "First", "First Name");
            var last = map.Get(row, "Last", "Last Name");
            var name = map.Get(row, "Name", "Entity Name");
            var isIndividual = string.Equals(classification, "Individual", StringComparison.OrdinalIgnoreCase);

            var status = map.Get(row, "Record Status", "Status");
            var record = new ExclusionRecord
            {
                Source = ExclusionScreeningSource.SamGov,
                Classification = classification,
                FirstName = first,
                MiddleName = map.Get(row, "Middle", "Middle Name"),
                LastName = last,
                // Individuals are matched on First/Last; their "Name" column is
                // the same person, not a business.
                BusinessName = isIndividual && last is not null ? null : name,
                Npi = map.Get(row, "NPI"),
                UeiSam = map.Get(row, "Unique Entity ID", "UEI", "SAM Number"),
                CageCode = map.Get(row, "CAGE", "CAGE Code"),
                Address = map.Get(row, "Address 1", "Address"),
                City = map.Get(row, "City"),
                State = map.Get(row, "State / Province", "State"),
                Zip = map.Get(row, "Zip Code", "Zip"),
                ExclusionType = map.Get(row, "Exclusion Type"),
                ExclusionProgram = map.Get(row, "Exclusion Program"),
                ExcludingAgency = map.Get(row, "Excluding Agency"),
                ExclusionDate = ExclusionDates.Parse(map.Get(row, "Active Date", "Activation Date")),
                EndDate = ExclusionDates.Parse(map.Get(row, "Termination Date")),
                // Rows are on the exclusion list; only an explicit non-active
                // status takes one off. Unknown → active (never a false clear).
                IsActive = !IsInactiveStatus(status)
            }.Normalize();

            if (isIndividual && record.NormalizedLastName is null && name is not null)
            {
                // Some extract versions only populate "Name" for individuals.
                var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    record.FirstName = parts[0];
                    record.LastName = parts[^1];
                    record.Normalize();
                }
            }

            if (record.NormalizedLastName is null && record.NormalizedBusinessName is null && record.Npi is null)
            {
                stats.RowsSkipped++;
                continue;
            }

            yield return record;
        }

        if (map is null)
            throw new ExclusionFileFormatException("SAM exclusions extract is empty");
    }

    internal static bool IsInactiveStatus(string? status) =>
        status is not null &&
        (status.Equals("Inactive", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Terminated", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Deleted", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Archived", StringComparison.OrdinalIgnoreCase));
}

public static class ExclusionDates
{
    private static readonly string[] Formats =
    [
        "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "MM-dd-yyyy", "M-d-yyyy",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "MM/dd/yyyy HH:mm:ss"
    ];

    /// <summary>Parse a list date; null for blank, 00000000, "Indefinite" or unparseable values.</summary>
    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value.All(c => c == '0') || value.Equals("Indefinite", StringComparison.OrdinalIgnoreCase))
            return null;
        if (DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
        }
        return null;
    }

    public static string? ToKey(DateTime? date) => date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
