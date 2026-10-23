using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PremiumBillingService.Tests.Support;

/// <summary>
/// The carrier directory as an in-process host test needs it.
///
/// <para>
/// The host registers the real directory, which reads tenant-service, and a
/// carrier that could not be read deliberately fails the release rather than
/// originating under the configured entity (plan-configuration.md §12.3). A
/// test host has no tenant-service, so leaving the real one in place turns
/// every release into a 500. An empty directory is the honest stand-in: no
/// carrier states an origination, so the file is originated under the
/// deployment's own <c>Nacha:*</c> values, which is what these tests assert.
/// </para>
/// </summary>
public static class StubCarriers
{
    public static InMemoryCarrierDirectory Register(IServiceCollection services)
    {
        var carriers = new InMemoryCarrierDirectory();
        services.RemoveAll<ICarrierDirectory>();
        services.AddSingleton<ICarrierDirectory>(carriers);
        return carriers;
    }
}
