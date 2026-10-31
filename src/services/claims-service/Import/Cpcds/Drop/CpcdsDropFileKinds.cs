namespace ClaimsService.Import.Cpcds.Drop;

public enum CpcdsDropFileKind { Unknown, Members, Coverages, Claims }

/// <summary>
/// Which CPCDS file a drop file is, from its name, as the CPCDS exporters
/// name them (<c>CPCDS_Members.csv</c>, <c>CPCDS_Coverages.csv</c>,
/// <c>CPCDS_Claims.csv</c>) and as most extracts do (<c>qnxt_members_20261101.csv</c>).
/// </summary>
public static class CpcdsDropFileKinds
{
    public static CpcdsDropFileKind Classify(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (name.Contains("coverage")) return CpcdsDropFileKind.Coverages;
        if (name.Contains("member")) return CpcdsDropFileKind.Members;
        if (name.Contains("claim")) return CpcdsDropFileKind.Claims;
        return CpcdsDropFileKind.Unknown;
    }
}
