namespace DemoPersonaSeeder;

public sealed class SeederOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string? ConfirmSyntheticTenant { get; set; }
    public DateTime AsOf { get; set; } = DateTime.UtcNow.Date;
    public bool DryRun { get; set; }
    public string OutputPath { get; set; } = "demo-script.md";
    public string? BearerToken { get; set; } = Environment.GetEnvironmentVariable("CHO_BEARER_TOKEN");
    public int ClaimWaitSeconds { get; set; } = 90;
    public bool ShowHelp { get; set; }

    // Defaults match the portal's appsettings.Development.json (local docker compose).
    public string ClaimsUrl { get; set; } = Env("CHO_CLAIMS_URL", "http://localhost:5001");
    public string BenefitPlanUrl { get; set; } = Env("CHO_BENEFIT_PLAN_URL", "http://localhost:5002");
    public string MemberUrl { get; set; } = Env("CHO_MEMBER_URL", "http://localhost:5003");
    public string ProviderUrl { get; set; } = Env("CHO_PROVIDER_URL", "http://localhost:5004");
    public string AuthorizationUrl { get; set; } = Env("CHO_AUTHORIZATION_URL", "http://localhost:5005");
    public string CoverageUrl { get; set; } = Env("CHO_COVERAGE_URL", "http://localhost:5009");
    public string AppealsUrl { get; set; } = Env("CHO_APPEALS_URL", "http://localhost:5013");
    public string SponsorUrl { get; set; } = Env("CHO_SPONSOR_URL", "http://localhost:5015");
    public string BillingUrl { get; set; } = Env("CHO_BILLING_URL", "http://localhost:5018");
    public string ContractsUrl { get; set; } = Env("CHO_CONTRACTS_URL", "http://localhost:5019");

    private static string Env(string name, string fallback)
        => (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback).TrimEnd('/');

    public const string Usage = """
        demo-persona-seeder: seeds a demo tenant with named synthetic personas and writes a demo script.

        Required:
          --tenant <id>                     tenant to seed
          --confirm-synthetic <id>          repeat the tenant id: confirms it holds synthetic data only

        Options:
          --as-of <yyyy-MM-dd>              date the stories are relative to (default: today, UTC)
          --out <path>                      demo script path (default: demo-script.md)
          --claim-wait-seconds <n>          how long to wait for each claim to adjudicate (default: 90)
          --dry-run                         build everything and write the script, but call no service
          --claims-url, --benefit-plan-url, --member-url, --provider-url, --authorization-url,
          --coverage-url, --appeals-url, --sponsor-url, --billing-url, --contracts-url <url>
                                            service base URLs (or CHO_*_URL environment variables)

        Authentication: set CHO_BEARER_TOKEN to a CHO token for the tenant. Services take the tenant
        from the token; X-Tenant-ID is sent too and must match.
        """;

    public static SeederOptions Parse(string[] args)
    {
        var o = new SeederOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--tenant": o.TenantId = Next().Trim(); break;
                case "--confirm-synthetic": o.ConfirmSyntheticTenant = Next().Trim(); break;
                case "--as-of": o.AsOf = DateTime.SpecifyKind(DateTime.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture).Date, DateTimeKind.Utc); break;
                case "--out": o.OutputPath = Next(); break;
                case "--claim-wait-seconds": o.ClaimWaitSeconds = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--dry-run": o.DryRun = true; break;
                case "--claims-url": o.ClaimsUrl = Next().TrimEnd('/'); break;
                case "--benefit-plan-url": o.BenefitPlanUrl = Next().TrimEnd('/'); break;
                case "--member-url": o.MemberUrl = Next().TrimEnd('/'); break;
                case "--provider-url": o.ProviderUrl = Next().TrimEnd('/'); break;
                case "--authorization-url": o.AuthorizationUrl = Next().TrimEnd('/'); break;
                case "--coverage-url": o.CoverageUrl = Next().TrimEnd('/'); break;
                case "--appeals-url": o.AppealsUrl = Next().TrimEnd('/'); break;
                case "--sponsor-url": o.SponsorUrl = Next().TrimEnd('/'); break;
                case "--billing-url": o.BillingUrl = Next().TrimEnd('/'); break;
                case "--contracts-url": o.ContractsUrl = Next().TrimEnd('/'); break;
                case "--help" or "-h": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }
        return o;
    }

    /// <summary>
    /// The seeder writes invented people into whatever tenant it is pointed at, so the
    /// operator must name the tenant twice: once to target it, once to confirm it holds
    /// synthetic data only.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
            return "--tenant is required.";
        if (!string.Equals(TenantId, ConfirmSyntheticTenant, StringComparison.Ordinal))
            return "Repeat the tenant id with --confirm-synthetic to confirm it holds synthetic data only. "
                 + "Never run this against a tenant that holds real members.";
        if (ClaimWaitSeconds < 0)
            return "--claim-wait-seconds must be 0 or more.";
        return null;
    }
}
