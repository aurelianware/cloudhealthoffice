using System.Collections.Concurrent;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TenantService.Controllers;
using TenantService.Models;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests.Security;

/// <summary>
/// The real tenant-service pipeline (authentication, tenant resolution,
/// authorization, route tenant check, controllers and management services)
/// over mocked repositories, Stripe and the identity directory.
/// </summary>
public class TenantServiceFactory : WebApplicationFactory<TenantsController>
{
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";
    public const string WebhookSecret = "whsec_test_0123456789abcdef";

    public Mock<ITenantRepository> Tenants { get; } = new();
    public Mock<ITenantUserRepository> Users { get; } = new();
    public Mock<ITenantRoleRepository> Roles { get; } = new();
    public Mock<IIdentityDirectory> Directory { get; } = new();
    public Mock<IStripeService> Stripe { get; } = new();
    public Mock<ISftpProvisioningService> Sftp { get; } = new();
    public Mock<IInvitationStore> Invitations { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Stripe:WebhookSecret for this host; null leaves appsettings.json's value.</summary>
    protected virtual string? ConfiguredWebhookSecret => WebhookSecret;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
        if (ConfiguredWebhookSecret != null)
            builder.UseSetting("Stripe:WebhookSecret", ConfiguredWebhookSecret);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantRepository>();
            services.RemoveAll<ITenantUserRepository>();
            services.RemoveAll<ITenantRoleRepository>();
            services.RemoveAll<IIdentityDirectory>();
            services.RemoveAll<IStripeService>();
            services.RemoveAll<ISftpProvisioningService>();
            services.RemoveAll<IInvitationStore>();
            services.AddSingleton(_ => Invitations.Object);
            services.AddSingleton(_ => Tenants.Object);
            services.AddSingleton(_ => Users.Object);
            services.AddSingleton(_ => Roles.Object);
            services.AddSingleton(_ => Directory.Object);
            services.AddSingleton(_ => Stripe.Object);
            services.AddSingleton(_ => Sftp.Object);
        });
    }

    /// <summary>Clears recorded calls and logs and restores the standard data.</summary>
    public void ResetMocks()
    {
        _ = Server; // build the host first, so its startup seeding is not counted

        Tenants.Reset();
        Users.Reset();
        Roles.Reset();
        Directory.Reset();
        Stripe.Reset();
        Sftp.Reset();
        Invitations.Reset();
        Logs.Clear();

        Tenants.Setup(r => r.GetByTenantIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id is TenantA or TenantB ? NewTenant(id) : null);
        Tenants.Setup(r => r.GetAllAsync(It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(() => new List<Tenant> { NewTenant(TenantA), NewTenant(TenantB) });
        Tenants.Setup(r => r.ExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        Tenants.Setup(r => r.CreateAsync(It.IsAny<Tenant>())).ReturnsAsync((Tenant t) => t);
        Tenants.Setup(r => r.UpdateAsync(It.IsAny<Tenant>())).ReturnsAsync((Tenant t) => t);

        Users.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        Users.Setup(r => r.CreateAsync(It.IsAny<TenantUser>())).ReturnsAsync((TenantUser u) => u);
        Users.Setup(r => r.UpdateAsync(It.IsAny<TenantUser>())).ReturnsAsync((TenantUser u) => u);
        Users.Setup(r => r.GetByTenantIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string t) => new List<TenantUser> { NewUser("user-1", t) });
        Users.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id == "user-1" ? NewUser("user-1", TenantA) : null);

        Roles.Setup(r => r.GetAllAsync()).ReturnsAsync(() => StandardRoles.All.ToList());
        Roles.Setup(r => r.CreateAsync(It.IsAny<TenantRole>())).ReturnsAsync((TenantRole r) => r);

        Sftp.Setup(s => s.ProvisionTenantSftpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<string?>()))
            .ReturnsAsync(new SftpProvisioningResult { Success = true });

        Directory.Setup(d => d.GetMembershipsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IdentityMembership>());
        Directory.Setup(d => d.GetTenantsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IdentityTenant>());
        Directory.Setup(d => d.GetTenantAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new IdentityTenant { TenantId = id, Status = "active", IsActive = true });
    }

    public static Tenant NewTenant(string tenantId) => new()
    {
        TenantId = tenantId,
        TenantName = tenantId,
        OrganizationName = "Org " + tenantId,
        Status = "active",
        ContactInfo = new ContactInfo { Email = "admin@" + tenantId + ".example" },
        ApiKeys = new List<ApiKey>
        {
            new() { Name = "integration", KeyHash = "HASH-OF-SECRET-KEY", KeyPrefix = "cho_abcd" },
        },
        Billing = new BillingInfo { StripeCustomerId = "cus_SECRET", StripeSubscriptionId = "sub_SECRET" },
        Configuration = new TenantConfiguration
        {
            PaymentControls = new PaymentControlsConfig { EnforceSeparationOfDuties = false },
        },
    };

    public static TenantUser NewUser(string id, string tenantId) => new()
    {
        Id = id,
        TenantId = tenantId,
        Email = id + "@" + tenantId + ".example",
        DisplayName = id,
        Roles = new List<string> { ChoRolePermissions.ClaimsExaminer },
    };

    /// <summary>A user client in <paramref name="tenant"/> with the given roles and subject.</summary>
    public HttpClient UserClient(string tenant, string subject, params string[] roles)
    {
        var client = CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    /// <summary>A client presenting a service token for <paramref name="clientId"/> in <paramref name="tenant"/>.</summary>
    public HttpClient ServiceClient(string clientId, string tenant)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant));
        return client;
    }
}

/// <summary>Host with no webhook secret configured (appsettings.json ships none).</summary>
public sealed class TenantServiceWithoutWebhookSecretFactory : TenantServiceFactory
{
    protected override string? ConfiguredWebhookSecret => null;
}

/// <summary>Host configured with the published placeholder <c>whsec_...</c>.</summary>
public sealed class TenantServiceWithPlaceholderWebhookSecretFactory : TenantServiceFactory
{
    public const string Placeholder = "whsec_...";
    protected override string? ConfiguredWebhookSecret => Placeholder;
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<(string, LogLevel, string)> _entries;

        public Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((_category, logLevel, formatter(state, exception)));
    }
}
