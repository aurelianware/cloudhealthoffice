using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilityDiTests
{
    [Fact]
    public void UseInMemoryDirectoryTrue_RegistersDemoDirectory()
    {
        var sp = Build(useInMemory: true);
        sp.GetRequiredService<IPayerEligibilityDirectory>()
            .Should().BeOfType<InMemoryPayerEligibilityDirectory>();
    }

    [Fact]
    public void UseInMemoryDirectoryFalse_DoesNotRegisterDemoDirectory()
    {
        var sp = Build(useInMemory: false);
        sp.GetRequiredService<IPayerEligibilityDirectory>()
            .Should().BeOfType<UnconfiguredPayerEligibilityDirectory>();
    }

    private static ServiceProvider Build(bool useInMemory)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PayerEligibilityResponder:UseInMemoryDirectory"] = useInMemory ? "true" : "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoPayerEligibilityResponder(config);
        return services.BuildServiceProvider();
    }
}
