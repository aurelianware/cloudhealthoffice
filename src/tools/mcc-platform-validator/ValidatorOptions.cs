namespace CloudHealthOffice.Tools.MccPlatformValidator;

public sealed record ValidatorOptions(
    int Claims,
    int Seed,
    string TenantId,
    string ClaimsUrl,
    string BenefitUrl,
    string MemberUrl,
    string CoverageUrl,
    string ProviderUrl,
    string AuthorizationUrl,
    bool SeedMembers,
    bool SeedProviders,
    bool SeedAuthorizations,
    bool SkipClaimUpdate,
    bool ServiceBusOnly,
    bool ServiceBusReconciliationEnabled,
    int ServiceBusReconciliationTimeoutSeconds,
    bool PendObservationEnabled,
    int PendObservationTimeoutSeconds,
    int PendObservationIntervalMilliseconds,
    string? PendDiagnosticsPath,
    int PendDiagnosticsNcciSampleSize,
    int TimeoutSeconds,
    int ProgressEvery,
    int Parallelism,
    int SeedParallelism,
    int LineOfBusiness,
    string? SummaryJsonPath,
    bool NoPublishSummary,
    int PublishClaimResultsLimit,
    bool PriorAuthScenariosEnabled,
    double PriorAuthScenarioRate,
    bool ShowHelp)
{
    public const int DefaultMaxClaims = 10_000;
    // Three local claims-service replicas default to 32 Service Bus calls each.
    // Allow the validator to keep that 96-call consumer pool fed.
    public const int MaxParallelism = 96;

    public static ValidatorOptions Parse(string[] args)
    {
        var options = new MutableOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--claims" or "-n" when i + 1 < args.Length:
                    options.Claims = int.Parse(args[++i]);
                    break;
                case "--seed" or "-s" when i + 1 < args.Length:
                    options.Seed = int.Parse(args[++i]);
                    break;
                case "--tenant" when i + 1 < args.Length:
                    options.TenantId = args[++i];
                    break;
                case "--claims-url" when i + 1 < args.Length:
                    options.ClaimsUrl = args[++i].TrimEnd('/');
                    break;
                case "--benefit-url" when i + 1 < args.Length:
                    options.BenefitUrl = args[++i].TrimEnd('/');
                    break;
                case "--member-url" when i + 1 < args.Length:
                    options.MemberUrl = args[++i].TrimEnd('/');
                    break;
                case "--coverage-url" when i + 1 < args.Length:
                    options.CoverageUrl = args[++i].TrimEnd('/');
                    break;
                case "--provider-url" when i + 1 < args.Length:
                    options.ProviderUrl = args[++i].TrimEnd('/');
                    break;
                case "--authorization-url" when i + 1 < args.Length:
                    options.AuthorizationUrl = args[++i].TrimEnd('/');
                    break;
                case "--no-seed-members":
                    options.SeedMembers = false;
                    break;
                case "--no-seed-providers":
                    options.SeedProviders = false;
                    break;
                case "--no-seed-authorizations":
                    options.SeedAuthorizations = false;
                    break;
                case "--skip-claim-update":
                    options.SkipClaimUpdate = true;
                    break;
                case "--servicebus-only":
                    options.ServiceBusOnly = true;
                    break;
                case "--no-servicebus-reconciliation":
                    options.ServiceBusReconciliationEnabled = false;
                    break;
                case "--servicebus-reconciliation-timeout" when i + 1 < args.Length:
                    options.ServiceBusReconciliationTimeoutSeconds = int.Parse(args[++i]);
                    break;
                case "--no-pend-observation":
                    options.PendObservationEnabled = false;
                    break;
                case "--pend-observation-timeout" when i + 1 < args.Length:
                    options.PendObservationTimeoutSeconds = int.Parse(args[++i]);
                    break;
                case "--pend-observation-interval-ms" when i + 1 < args.Length:
                    options.PendObservationIntervalMilliseconds = int.Parse(args[++i]);
                    break;
                case "--pend-diagnostics" when i + 1 < args.Length:
                    options.PendDiagnosticsPath = args[++i];
                    break;
                case "--pend-diagnostics-ncci-sample" when i + 1 < args.Length:
                    options.PendDiagnosticsNcciSampleSize = int.Parse(args[++i]);
                    break;
                case "--timeout" when i + 1 < args.Length:
                    options.TimeoutSeconds = int.Parse(args[++i]);
                    break;
                case "--progress-every" when i + 1 < args.Length:
                    options.ProgressEvery = int.Parse(args[++i]);
                    break;
                case "--parallelism" or "-p" when i + 1 < args.Length:
                    options.Parallelism = int.Parse(args[++i]);
                    break;
                case "--seed-parallelism" when i + 1 < args.Length:
                    options.SeedParallelism = int.Parse(args[++i]);
                    break;
                case "--max-claims" when i + 1 < args.Length:
                    options.MaxClaims = int.Parse(args[++i]);
                    break;
                case "--line-of-business" when i + 1 < args.Length:
                    options.LineOfBusiness = int.Parse(args[++i]);
                    break;
                case "--no-prior-auth-scenarios":
                    options.PriorAuthScenariosEnabled = false;
                    break;
                case "--prior-auth-rate" when i + 1 < args.Length:
                    options.PriorAuthScenarioRate = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--summary-json" when i + 1 < args.Length:
                    options.SummaryJsonPath = args[++i];
                    break;
                case "--no-publish-summary":
                    options.NoPublishSummary = true;
                    break;
                case "--claim-results-limit" when i + 1 < args.Length:
                    options.PublishClaimResultsLimit = int.Parse(args[++i]);
                    break;
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;
            }
        }

        var maxClaims = Math.Max(1, options.MaxClaims);
        if (options.Claims > maxClaims)
        {
            Console.Error.WriteLine($"warning: capping --claims to {maxClaims:N0} to avoid excessive in-memory allocation");
            options.Claims = maxClaims;
        }

        if (options.Parallelism > MaxParallelism)
        {
            Console.Error.WriteLine($"warning: capping --parallelism to {MaxParallelism:N0} for local validation");
            options.Parallelism = MaxParallelism;
        }

        var effectiveClaims = Math.Max(1, options.Claims);

        return new ValidatorOptions(
            effectiveClaims,
            options.Seed,
            options.TenantId,
            options.ClaimsUrl.TrimEnd('/'),
            options.BenefitUrl.TrimEnd('/'),
            options.MemberUrl.TrimEnd('/'),
            options.CoverageUrl.TrimEnd('/'),
            options.ProviderUrl.TrimEnd('/'),
            options.AuthorizationUrl.TrimEnd('/'),
            options.SeedMembers,
            options.SeedProviders,
            options.SeedAuthorizations,
            options.SkipClaimUpdate,
            options.ServiceBusOnly,
            options.ServiceBusReconciliationEnabled,
            Math.Clamp(options.ServiceBusReconciliationTimeoutSeconds, 1, 900),
            options.PendObservationEnabled,
            Math.Clamp(options.PendObservationTimeoutSeconds, 1, 300),
            Math.Clamp(options.PendObservationIntervalMilliseconds, 100, 30_000),
            string.IsNullOrWhiteSpace(options.PendDiagnosticsPath) ? null : options.PendDiagnosticsPath,
            Math.Clamp(options.PendDiagnosticsNcciSampleSize, 0, 100_000),
            Math.Max(5, options.TimeoutSeconds),
            Math.Max(1, options.ProgressEvery),
            Math.Max(1, options.Parallelism),
            options.SeedParallelism > 0
                ? Math.Clamp(options.SeedParallelism, 1, MaxParallelism)
                : Math.Max(1, options.Parallelism),
            Math.Clamp(options.LineOfBusiness, 1, 5),
            options.SummaryJsonPath,
            options.NoPublishSummary,
            Math.Clamp(options.PublishClaimResultsLimit, 0, effectiveClaims),
            options.PriorAuthScenariosEnabled,
            Math.Clamp(options.PriorAuthScenarioRate, 0.0, 0.25),
            options.ShowHelp);
    }

    private sealed class MutableOptions
    {
        public int Claims { get; set; } = 25;
        public int Seed { get; set; } = 42;
        public string TenantId { get; set; } = "demo";
        public string ClaimsUrl { get; set; } = "http://localhost:5001";
        public string BenefitUrl { get; set; } = "http://localhost:5002";
        public string MemberUrl { get; set; } = "http://localhost:5003";
        public string CoverageUrl { get; set; } = "http://localhost:5005";
        public string ProviderUrl { get; set; } = "http://localhost:5004";
        public string AuthorizationUrl { get; set; } = "http://authorization-service";
        public bool SeedMembers { get; set; } = true;
        public bool SeedProviders { get; set; } = true;
        public bool SeedAuthorizations { get; set; } = true;
        public bool SkipClaimUpdate { get; set; }
        public bool ServiceBusOnly { get; set; }
        public bool ServiceBusReconciliationEnabled { get; set; } = true;
        public int ServiceBusReconciliationTimeoutSeconds { get; set; } = 300;
        public bool PendObservationEnabled { get; set; } = true;
        public int PendObservationTimeoutSeconds { get; set; } = 45;
        public int PendObservationIntervalMilliseconds { get; set; } = 1000;
        public string? PendDiagnosticsPath { get; set; }
        public int PendDiagnosticsNcciSampleSize { get; set; } = 200;
        public int TimeoutSeconds { get; set; } = 60;
        public int ProgressEvery { get; set; } = 10;
        public int Parallelism { get; set; } = 10;
        public int SeedParallelism { get; set; }
        public int MaxClaims { get; set; } = DefaultMaxClaims;
        public int LineOfBusiness { get; set; } = 3;
        public string? SummaryJsonPath { get; set; }
        public bool NoPublishSummary { get; set; }
        public int PublishClaimResultsLimit { get; set; } = 1000;
        public bool PriorAuthScenariosEnabled { get; set; } = true;
        public double PriorAuthScenarioRate { get; set; } = 0.02;
        public bool ShowHelp { get; set; }
    }
}
