using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.HostedServices;

/// <summary>
/// Loads the curated <c>system-defaults.json</c> bundle into a tenant's
/// service-category mapping store the first time the tenant is seen, and
/// applies only what is new when the bundle's version is bumped
/// (capability BP 5.6 — Service Category Mapping; see
/// <see cref="EnsureTenantSeededAsync"/>).
///
/// <para>
/// <b>Per-installation seed, per-tenant application.</b> The seed file is
/// installation-wide CHO-curated reference data; applying it stamps the
/// bundle into a specific tenant's collection so the resolver finds rows on
/// its standard read path. The <c>SystemDefaultsApplied</c> idempotency
/// document records which bundle version was last applied for the tenant —
/// reruns at the same version are a no-op.
/// </para>
///
/// <para>
/// <b>Tenant discovery.</b> The seeder does not enumerate tenants on its
/// own; the hosted service's startup pass only loads and validates the
/// seed bundle. Per-tenant application is operator-triggered via the
/// admin write API:
/// <c>POST /api/v1/service-category-mappings/seed-system-defaults</c>
/// (with the <c>X-Tenant-ID</c> header), which calls
/// <see cref="EnsureTenantSeededAsync"/>. There is no middleware lazy-
/// trigger today.
/// </para>
///
/// <para>
/// See <c>schemas/service-category-mappings/README.md</c> for bundle
/// authoring conventions and <c>docs/architecture/service-category-mapping.md</c>
/// for the canonical seed-and-apply flow.
/// </para>
/// </summary>
public sealed class SystemDefaultMappingSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<ServiceCategoryMappingOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SystemDefaultMappingSeeder> _logger;

    private SeedBundle? _bundle;

    public SystemDefaultMappingSeeder(
        IServiceProvider serviceProvider,
        IOptionsMonitor<ServiceCategoryMappingOptions> options,
        IHostEnvironment environment,
        ILogger<SystemDefaultMappingSeeder> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>The parsed bundle, exposed for tests and admin diagnostics.</summary>
    public SeedBundle? LoadedBundle => _bundle;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.SeedSystemDefaultsOnStartup)
        {
            _logger.LogInformation(
                "SystemDefaultMappingSeeder: SeedSystemDefaultsOnStartup=false; skipping bundle load.");
            return Task.CompletedTask;
        }

        try
        {
            _bundle = LoadBundle();
            _logger.LogInformation(
                "SystemDefaultMappingSeeder: loaded bundle version={Version} mappings={Count} from {Path}",
                _bundle?.Version, _bundle?.Mappings.Count ?? 0, ResolveSeedPath());
        }
        catch (Exception ex)
        {
            // Failure to load the seed file is not fatal — the resolver's
            // POS-code inference fallback continues to work, and operator-
            // authored mappings remain functional. Log loudly so the gap is
            // visible to operators.
            _logger.LogError(
                ex,
                "SystemDefaultMappingSeeder: failed to load bundle from {Path}; " +
                "tenants will receive no system-default mappings until the bundle " +
                "is fixed and the service restarts.",
                ResolveSeedPath());
            _bundle = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stamp the loaded seed bundle onto <paramref name="tenantId"/> if no
    /// matching <c>SystemDefaultsApplied</c> record exists for the bundle's
    /// current version. Returns the number of mapping rows written (zero on
    /// no-op or when the bundle is unavailable).
    ///
    /// <para>
    /// <b>First seed</b> (no record): every bundle mapping is written.
    /// </para>
    ///
    /// <para>
    /// <b>Version bump</b> (record at an older version): only the delta is
    /// written, never the whole bundle again. Reads sort newest-first and the
    /// first matching row wins, so re-inserting every default would let fresh
    /// default rows outrank the tenant's own mappings, bring back defaults
    /// the tenant deleted, and duplicate every category. The delta is:
    /// <list type="number">
    ///   <item>rules introduced after the recorded version (a mapping's or a
    ///     rule's <c>since</c> in the bundle; absent means version 1) — rules
    ///     from versions already applied are never re-created, even when the
    ///     tenant has since deleted them; and</item>
    ///   <item>of those, only rules whose codes no existing tenant-default row
    ///     already covers (same code type, overlapping code / range /
    ///     wildcard, active or not): a new default only fills a gap, it never
    ///     changes how a code the tenant already maps resolves.</item>
    /// </list>
    /// Each mapping with at least one remaining rule is written as one new
    /// row. Re-running at the recorded version is a no-op.
    /// </para>
    /// </summary>
    public async Task<int> EnsureTenantSeededAsync(string tenantId, CancellationToken ct = default)
    {
        if (_bundle is null) return 0;
        if (string.IsNullOrEmpty(tenantId)) return 0;

        using var scope = _serviceProvider.CreateScope();
        var writeRepo = scope.ServiceProvider.GetRequiredService<IServiceCategoryMappingWriteRepository>();
        var appliedRepo = scope.ServiceProvider.GetRequiredService<ISystemDefaultsAppliedRecordRepository>();

        var applied = await appliedRepo.GetAsync(tenantId, ct);
        if (applied is not null && applied.AppliedSeedVersion >= _bundle.Version)
        {
            return 0;
        }

        var fromVersion = applied?.AppliedSeedVersion ?? 0;
        var isDelta = fromVersion > 0;

        // Tenant-default rows already in place. Only consulted on a version
        // bump; plan-scoped overrides are evaluated before tenant defaults by
        // the resolver, so they always win and need no protection here.
        List<ProcedureCodeRule> existingRules = isDelta
            ? (await writeRepo.ListAsync(tenantId, null, ct)).SelectMany(m => m.Rules).ToList()
            : [];

        // Seed mappings are tenant-default scope (BenefitPlanId == null).
        // Plan-specific overrides are operator-authored only.
        var written = 0;
        var skippedCovered = 0;
        foreach (var seed in _bundle.Mappings)
        {
            var rules = new List<SeedRule>();
            foreach (var rule in seed.Rules)
            {
                if (EffectiveSince(seed, rule) <= fromVersion) continue; // applied before; never re-create
                if (isDelta && existingRules.Any(e => Overlaps(rule, e)))
                {
                    skippedCovered++;
                    _logger.LogInformation(
                        "SystemDefaultMappingSeeder: tenant={Tenant} already maps {CodeType} {CodePattern}-{CodeRangeEnd}; " +
                        "not adding the v{Version} default rule for {ServiceTypeCode}",
                        Sanitize(tenantId), rule.CodeType, rule.CodePattern, rule.CodeRangeEnd ?? rule.CodePattern,
                        _bundle.Version, seed.ServiceTypeCode);
                    continue;
                }
                rules.Add(rule);
            }
            if (rules.Count == 0) continue;

            var mapping = new ServiceCategoryMapping
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                BenefitPlanId = null,
                ServiceTypeCode = seed.ServiceTypeCode,
                ServiceTypeDescription = seed.ServiceTypeDescription,
                Rules = rules.Select(r => new ProcedureCodeRule
                {
                    Id = Guid.NewGuid(),
                    Priority = r.Priority,
                    CodeType = r.CodeType,
                    CodePattern = r.CodePattern,
                    CodeRangeEnd = r.CodeRangeEnd,
                    PlaceOfServiceCode = r.PlaceOfServiceCode,
                    RequiredModifier = r.RequiredModifier,
                    RevenueCode = r.RevenueCode,
                }).ToList(),
                IsActive = true,
            };
            await writeRepo.CreateAsync(mapping, ct);
            written++;
        }

        await appliedRepo.UpsertAsync(new SystemDefaultsAppliedRecord
        {
            TenantId = tenantId,
            AppliedSeedVersion = _bundle.Version,
            AppliedAt = DateTimeOffset.UtcNow,
            MappingCount = written,
        }, ct);

        _logger.LogInformation(
            "SystemDefaultMappingSeeder: applied bundle version={Version} (from version={FromVersion}) " +
            "mappings={Count} rulesAlreadyCovered={Skipped} tenant={Tenant}",
            _bundle.Version, fromVersion, written, skippedCovered, Sanitize(tenantId));

        return written;
    }

    /// <summary>The bundle version a rule was introduced in: the rule's <c>since</c>, else its mapping's, else 1.</summary>
    internal static int EffectiveSince(SeedMapping mapping, SeedRule rule) => rule.Since ?? mapping.Since ?? 1;

    /// <summary>
    /// True when <paramref name="existing"/> could match any code
    /// <paramref name="seed"/> matches: same code type (REV rules only
    /// against REV rules) and overlapping code spans. POS / modifier /
    /// revenue-code qualifiers are ignored on purpose, so the check errs on
    /// the side of leaving the tenant's mapping alone.
    /// </summary>
    internal static bool Overlaps(SeedRule seed, ProcedureCodeRule existing)
    {
        var seedIsRev = IsRevenue(seed.CodeType);
        if (seedIsRev != IsRevenue(existing.CodeType)) return false;
        if (!seedIsRev && !string.Equals(seed.CodeType?.Trim(), existing.CodeType?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var (lo1, hi1) = Span(seed.CodePattern, seed.CodeRangeEnd, seedIsRev);
        var (lo2, hi2) = Span(existing.CodePattern, existing.CodeRangeEnd, seedIsRev);
        return string.CompareOrdinal(lo1, hi2) <= 0 && string.CompareOrdinal(lo2, hi1) <= 0;
    }

    private static bool IsRevenue(string? codeType) =>
        string.Equals(codeType?.Trim(), "REV", StringComparison.OrdinalIgnoreCase);

    /// <summary>Inclusive [low, high] code span of a rule: range, prefix wildcard, or exact code.</summary>
    private static (string Low, string High) Span(string? pattern, string? rangeEnd, bool isRevenue)
    {
        string Norm(string v)
        {
            var t = v.Trim().ToUpperInvariant();
            return isRevenue && t.Length < 4 && !t.EndsWith('*') ? t.PadLeft(4, '0') : t;
        }

        var p = Norm(pattern ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(rangeEnd)) return (p, Norm(rangeEnd));
        if (p.EndsWith('*'))
        {
            var prefix = p[..^1];
            return (prefix, prefix + char.MaxValue);
        }
        return (p, p);
    }

    private SeedBundle LoadBundle()
    {
        var path = ResolveSeedPath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Service-category mapping seed file not found at '{path}'. " +
                "Either ship the bundle at this path or set " +
                "ServiceCategoryMapping:SeedSystemDefaultsOnStartup=false " +
                "to suppress seeding.",
                path);
        }

        using var stream = File.OpenRead(path);
        var bundle = JsonSerializer.Deserialize<SeedBundle>(stream, JsonOpts)
            ?? throw new InvalidOperationException(
                $"Service-category mapping seed at '{path}' deserialized to null.");

        if (bundle.Version <= 0)
        {
            throw new InvalidOperationException(
                $"Service-category mapping seed at '{path}' must declare a positive integer 'version'.");
        }
        if (bundle.Mappings.Count == 0)
        {
            throw new InvalidOperationException(
                $"Service-category mapping seed at '{path}' contains zero mappings; " +
                "an empty bundle is treated as a configuration error.");
        }

        // Catch obvious schema violations early so the seeder fails loudly
        // at startup rather than producing surprising mappings on first
        // tenant seed.
        foreach (var m in bundle.Mappings)
        {
            if (string.IsNullOrWhiteSpace(m.ServiceTypeCode))
                throw new InvalidOperationException(
                    $"Seed bundle at '{path}' contains a mapping with empty serviceTypeCode.");
            if (m.Rules.Count == 0)
                throw new InvalidOperationException(
                    $"Seed bundle at '{path}' mapping '{m.ServiceTypeCode}' has no rules.");
            foreach (var since in m.Rules.Select(r => r.Since).Append(m.Since).OfType<int>())
            {
                if (since < 1 || since > bundle.Version)
                    throw new InvalidOperationException(
                        $"Seed bundle at '{path}' mapping '{m.ServiceTypeCode}' declares since={since}; " +
                        $"it must be between 1 and the bundle version ({bundle.Version}).");
            }
        }

        return bundle;
    }

    private string ResolveSeedPath()
    {
        var configured = _options.CurrentValue.SeedFilePath;
        if (Path.IsPathRooted(configured)) return configured;
        return Path.Combine(_environment.ContentRootPath, configured);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string Sanitize(string value)
        => value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    /// <summary>Top-level seed bundle shape.</summary>
    public sealed class SeedBundle
    {
        public int Version { get; set; }
        public string? Source { get; set; }
        public string? Notes { get; set; }
        public List<SeedMapping> Mappings { get; set; } = [];
    }

    /// <summary>One seed mapping entry — mirrors <see cref="ServiceCategoryMapping"/>
    /// minus tenant/id/audit fields (those are populated per-tenant).</summary>
    public sealed class SeedMapping
    {
        public string ServiceTypeCode { get; set; } = default!;
        public string ServiceTypeDescription { get; set; } = default!;

        /// <summary>
        /// Bundle version the mapping was introduced in (absent = 1). A
        /// version bump seeds a tenant only with mappings / rules introduced
        /// after the version the tenant last applied.
        /// </summary>
        public int? Since { get; set; }

        public List<SeedRule> Rules { get; set; } = [];
    }

    /// <summary>One seed rule entry — mirrors <see cref="ProcedureCodeRule"/>
    /// minus the per-mapping <c>Id</c> (assigned per-tenant).</summary>
    public sealed class SeedRule
    {
        public int Priority { get; set; }
        public string CodeType { get; set; } = "CPT";
        public string CodePattern { get; set; } = default!;
        public string? CodeRangeEnd { get; set; }
        public string? PlaceOfServiceCode { get; set; }
        public string? RequiredModifier { get; set; }
        public string? RevenueCode { get; set; }

        /// <summary>Bundle version the rule was added in, when later than its mapping's <see cref="SeedMapping.Since"/>.</summary>
        public int? Since { get; set; }
    }
}
