namespace CloudHealthOffice.Portal.Services;

public class ClaimDiagnosisCode
{
    private string _codeQualifier = string.Empty;
    private string _type = string.Empty;

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CodeQualifier
    {
        get => _codeQualifier;
        set => _codeQualifier = value ?? string.Empty;
    }
    public string Type
    {
        get => !string.IsNullOrWhiteSpace(_type) ? _type : DiagnosisTypeLabel(CodeQualifier);
        set => _type = value ?? string.Empty;
    }
    public int PointerNumber { get; set; }

    private static string DiagnosisTypeLabel(string qualifier)
        => qualifier.Trim().ToUpperInvariant() switch
        {
            "ABK" => "Principal",
            "ABF" => "Secondary",
            "" => "Unspecified",
            _ => qualifier
        };
}
