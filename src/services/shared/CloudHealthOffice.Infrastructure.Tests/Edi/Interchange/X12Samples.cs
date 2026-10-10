namespace CloudHealthOffice.Infrastructure.Tests.Edi.X12;

/// <summary>Synthetic X12 interchanges for the envelope tests. No real identifiers.</summary>
internal static class X12Samples
{
    /// <summary>ISA01..ISA16 of a valid 5010 interchange from SUBMITTER01 to CHO, control 000000101.</summary>
    public static string[] IsaFields() =>
    [
        "00", "          ", "00", "          ", "ZZ", "SUBMITTER01    ", "ZZ", "CHO            ",
        "260115", "1200", "^", "00501", "000000101", "0", "T", ":",
    ];

    public static string Isa(string[] fields, char element = '*', char terminator = '~') =>
        "ISA" + element + string.Join(element, fields) + terminator;

    public static readonly string[] Body =
    [
        "GS*HC*SUBMITTER01*CHO*20260115*1200*101*X*005010X222A1",
        "ST*837*0001*005010X222A1",
        "BHT*0019*00*0123*20260115*1200*CH",
        "SE*3*0001",
        "GE*1*101",
    ];

    /// <summary>A whole interchange. <paramref name="mutate"/> edits ISA fields; <paramref name="iea"/> overrides the trailer.</summary>
    public static string Interchange(Action<string[]>? mutate = null, string[]? body = null, string? iea = null)
    {
        var fields = IsaFields();
        mutate?.Invoke(fields);
        var segments = new List<string> { Isa(fields).TrimEnd('~') };
        segments.AddRange(body ?? Body);
        segments.Add(iea ?? $"IEA*1*{fields[12]}");
        return string.Join("~", segments) + "~";
    }

    public static string Field(string value, int width) => value.Length >= width ? value[..width] : value.PadRight(width);
}
