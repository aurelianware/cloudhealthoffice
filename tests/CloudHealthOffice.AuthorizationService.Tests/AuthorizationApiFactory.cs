using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AuthorizationService.Repositories;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// WebApplicationFactory for authorization-service tests: the real pipeline
/// (CHO token authentication, token-only tenant resolution, permission
/// policies) over an NSubstitute repository. Runs in Development, so the
/// service trusts the development issuers from appsettings.Development.json.
/// </summary>
public class AuthorizationApiFactory : WebApplicationFactory<Program>
{
    public const string TestSubject = "test-user";

    public IAuthorizationRepository AuthorizationRepository { get; } = Substitute.For<IAuthorizationRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove real repository registrations
            var descriptorsToRemove = services
                .Where(d => d.ServiceType == typeof(IAuthorizationRepository))
                .ToList();

            foreach (var descriptor in descriptorsToRemove)
                services.Remove(descriptor);

            // Remove Cosmos/Mongo registrations that would fail without config
            var infraDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true)
                .ToList();

            foreach (var descriptor in infraDescriptors)
                services.Remove(descriptor);

            services.AddSingleton(AuthorizationRepository);
        });
    }

    /// <summary>
    /// Issues a development-signed CHO user token for <paramref name="tenantId"/>.
    /// The default role, UMCoordinator, holds authorizations:read/write/decide.
    /// </summary>
    public string IssueToken(
        string tenantId = "test-tenant",
        string role = ChoRolePermissions.UMCoordinator,
        string subject = TestSubject)
        => ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(subject, tenantId, new[] { role });

    /// <summary>A token that carries exactly <paramref name="permissions"/> (no role expansion).</summary>
    public string IssueTokenWithPermissions(string tenantId, string subject, params string[] permissions)
        => ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
            subject, tenantId, Array.Empty<string>(), permissions: permissions);
}
