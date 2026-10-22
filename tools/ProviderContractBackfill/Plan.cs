using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace ProviderContractBackfill;

/// <summary>What the backfill proposes to do with one provider-and-plan.</summary>
public enum PlanDecision
{
    /// <summary>Write it: the canonical contract is complete and nothing needs a person.</summary>
    Ready,

    /// <summary>
    /// Write it, but somebody should look: it came from an engine contract with
    /// no legal agreement, the contract is not active, or its network status has
    /// no engine equivalent. Written because a flagged contract that prices is
    /// better than a provider with no contract at all; flagged because the
    /// cutover (phase 5) should not happen while the flag stands.
    /// </summary>
    Review,

    /// <summary>
    /// Do not write it: the two stores disagree, or the contract names something
    /// that does not exist. Guessing here would quietly change what a provider
    /// is paid, so the run reports it and leaves the tenant's contract as it is.
    /// </summary>
    Blocked,

    /// <summary>
    /// Nothing to do: the legal contract has no rates anywhere — no rate config
    /// children and no engine contract — so there is nothing to make canonical.
    /// </summary>
    Skipped,
}

/// <summary>
/// One canonical contract the backfill would write, with everything a reviewer
/// needs to agree with it.
/// </summary>
public sealed class PlannedContract
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The engine's own key for a provider on a plan
    /// (<c>{tenant}:{npi}:{plan}</c>), so a second run of the backfill updates
    /// this contract rather than adding another.
    /// </summary>
    public string CanonicalId { get; set; } = string.Empty;

    /// <summary>The legal agreement this came from, or null for an engine-only contract.</summary>
    public string? LegalContractId { get; set; }

    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNpi { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;

    /// <summary><c>legal</c> or <c>engine-only</c>.</summary>
    public string Origin { get; set; } = "legal";

    public PlanDecision Decision { get; set; }

    /// <summary>Whether the engine store already holds a contract under <see cref="CanonicalId"/>.</summary>
    public bool Replaces { get; set; }

    /// <summary>Where each term came from, in the order the terms are evaluated.</summary>
    public List<string> TermSources { get; set; } = new();

    /// <summary>What a reviewer should know. A <see cref="PlanDecision.Review"/> row has at least one.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// Why this contract cannot be written. A <see cref="PlanDecision.Blocked"/>
    /// row has at least one, and no other row has any. These are the conflicts
    /// the cutover is gated on.
    /// </summary>
    public List<string> Conflicts { get; set; } = new();

    /// <summary>
    /// The document that would be written, exactly. A reviewer reads this, and
    /// <c>apply</c> writes it unchanged — so what was reviewed is what lands.
    /// </summary>
    public EngineModels.ProviderContract? Contract { get; set; }

    /// <summary>A one-line summary for the CSV and the console.</summary>
    public string Summary => Conflicts.Count > 0
        ? Conflicts[0]
        : Notes.Count > 0
            ? Notes[0]
            : $"{Contract?.ReimbursementTerms.Count ?? 0} term(s) from {string.Join(" + ", TermSources.Distinct())}";
}

/// <summary>
/// One run's proposal: every canonical contract it would write, and every
/// conflict that stopped it. Written as JSON because the documents themselves
/// are the plan: <c>apply</c> writes what the file holds rather than deriving
/// it again, so a reviewer's approval is of the actual contracts.
/// </summary>
public sealed class BackfillPlan
{
    public string GeneratedAtUtc { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public List<string> Tenants { get; set; } = new();
    public List<PlannedContract> Contracts { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<PlannedContract> Writable
        => Contracts.Where(c => c.Decision is PlanDecision.Ready or PlanDecision.Review);

    [JsonIgnore]
    public IEnumerable<PlannedContract> Blocked
        => Contracts.Where(c => c.Decision == PlanDecision.Blocked);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BackfillPlan FromJson(string json)
        => JsonSerializer.Deserialize<BackfillPlan>(json, Json)
           ?? throw new FormatException("the plan file is empty");

    /// <summary>
    /// The plan's SHA-256, lower-case hex. <c>apply --execute</c> requires it,
    /// so a plan that was edited — or regenerated against a database that moved
    /// on — cannot be applied by accident.
    /// </summary>
    public static string Sha256Of(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>
    /// The plan as a spreadsheet: one row per provider-and-plan, for the people
    /// who have to agree to it. The JSON is the contract; this is the summary.
    /// </summary>
    public string ToCsv()
    {
        var csv = new StringBuilder();
        csv.AppendLine("tenantId,decision,canonicalId,legalContractId,contractNumber,providerNpi," +
                       "planId,origin,replaces,terms,termSources,summary");

        foreach (var row in Contracts)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                Field(row.TenantId),
                Field(row.Decision.ToString()),
                Field(row.CanonicalId),
                Field(row.LegalContractId),
                Field(row.ContractNumber),
                Field(row.ProviderNpi),
                Field(row.PlanId),
                Field(row.Origin),
                Field(row.Replaces ? "replace" : "create"),
                Field((row.Contract?.ReimbursementTerms.Count ?? 0).ToString()),
                Field(string.Join(" | ", row.TermSources)),
                Field(string.Join(" | ", row.Conflicts.Concat(row.Notes))),
            }));
        }

        return csv.ToString();
    }

    /// <summary>
    /// A CSV field: quoted, with quotes doubled, and never starting with a
    /// character a spreadsheet would read as a formula.
    /// </summary>
    private static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
            text = "'" + text;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
