using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Mock and synthetic-data components (a mock gateway, a stub client, an
/// in-memory data provider) answer with made-up data. Outside Development and
/// Testing a host refuses to start with one unless the deployment opts in
/// with <c>SyntheticData:Allowed=true</c>, which only a demo that holds no
/// real data should set. A production host can then never answer a real
/// member or provider from mock data by default.
/// </summary>
public static class ChoSyntheticData
{
    public const string AllowedSetting = "SyntheticData:Allowed";

    /// <summary>Whether this host may use mock or synthetic-data components.</summary>
    public static bool IsAllowed(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment()
        || environment.IsEnvironment(ChoTestingEnvironment.Name)
        || configuration.GetValue(AllowedSetting, false);

    /// <summary>
    /// Throws at startup when <paramref name="component"/> (a mock or
    /// synthetic-data component this host is configured with) is not allowed here.
    /// </summary>
    public static void EnsureAllowed(IHostEnvironment environment, IConfiguration configuration, string component)
    {
        if (IsAllowed(environment, configuration))
            return;

        throw new InvalidOperationException(
            $"{component} answers with mock or synthetic data, which is not allowed in the {environment.EnvironmentName} environment. " +
            $"Configure the real implementation, or set {AllowedSetting}=true only for a demo that holds no real data.");
    }
}
