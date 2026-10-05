using System.Text.RegularExpressions;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The portal's Services:BillingService must reach premium-billing-service.
/// Before, appsettings.json and the k8s ConfigMap named host
/// billing-service.cloudhealthoffice, which no deployment serves.
/// </summary>
public class PremiumBillingConfigTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    private static string Host(string url) => new Uri(url).Host;

    [Fact]
    public void Appsettings_PointsAtPremiumBillingService()
    {
        var json = File.ReadAllText(RepoFile("src", "portal", "CloudHealthOffice.Portal", "appsettings.json"));
        var url = Regex.Match(json, "\"BillingService\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;

        Host(url).Should().Be("premium-billing-service.cloudhealthoffice");
        url.Should().EndWith("/api");
    }

    [Fact]
    public void K8sConfigMap_PointsAtThePremiumBillingServiceService()
    {
        var portal = File.ReadAllText(RepoFile("src", "portal", "CloudHealthOffice.Portal", "k8s", "portal-deployment.yaml"));
        var url = Regex.Match(portal, "Services__BillingService:\\s*\"([^\"]+)\"").Groups[1].Value;

        Host(url).Should().Be("premium-billing-service.cloudhealthoffice");
        url.Should().EndWith("/api");

        // That name is the premium-billing-service Service in the same namespace.
        var service = File.ReadAllText(RepoFile("src", "services", "premium-billing-service", "k8s", "premium-billing-service-deployment.yaml"));
        service.Should().MatchRegex(@"kind: Service\s+metadata:\s+name: premium-billing-service\s+namespace: cloudhealthoffice");
    }

    [Fact]
    public void Compose_PointsAtPremiumBillingService()
    {
        var compose = File.ReadAllText(RepoFile("docker-compose.development.yml"));
        var url = Regex.Match(compose, @"Services__BillingService:\s*(\S+)").Groups[1].Value;

        url.Should().Be("http://premium-billing-service:8080/api");
    }
}
