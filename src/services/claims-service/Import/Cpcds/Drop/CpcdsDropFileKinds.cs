namespace ClaimsService.Import.Cpcds.Drop;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CpcdsDropFileKind { Unknown, Members, Coverages, Claims, Authorizations }

/// <summary>
/// Which CPCDS file a drop file is, from its name, as the CPCDS exporters
/// name them (<c>CPCDS_Members.csv</c>, <c>CPCDS_Coverages.csv</c>,
/// <c>CPCDS_Claims.csv</c>) and as most extracts do (<c>qnxt_members_20261101.csv</c>).
/// Authorization files are not CPCDS but travel the same drop: a name with
/// <c>auth</c> or <c>278</c> in it (<c>jiva_authorizations_20261101.csv</c>,
/// <c>um_278_20261101.x12</c>).
/// </summary>
public static class CpcdsDropFileKinds
{
    public static CpcdsDropFileKind Classify(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (name.Contains("auth") || name.Contains("278")) return CpcdsDropFileKind.Authorizations;
        if (name.Contains("coverage")) return CpcdsDropFileKind.Coverages;
        if (name.Contains("member")) return CpcdsDropFileKind.Members;
        if (name.Contains("claim")) return CpcdsDropFileKind.Claims;
        return CpcdsDropFileKind.Unknown;
    }
}
