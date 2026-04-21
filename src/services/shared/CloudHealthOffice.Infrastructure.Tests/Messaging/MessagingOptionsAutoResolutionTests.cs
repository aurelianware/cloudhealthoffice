using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Tests.Messaging;

public class MessagingOptionsAutoResolutionTests
{
    [Fact]
    public void Auto_InDev_ResolvesToInMemory()
    {
        var decision = MessagingServiceCollectionExtensions.ResolveBackend(
            new MessagingOptions { Backend = "Auto" }, Env("Development"));

        Assert.Equal(
            MessagingServiceCollectionExtensions.MessagingBackend.InMemory,
            decision.Backend);
        Assert.Contains("Development", decision.Reason);
    }

    [Fact]
    public void Auto_InProdWithoutConnectionString_ResolvesToInMemory()
    {
        var decision = MessagingServiceCollectionExtensions.ResolveBackend(
            new MessagingOptions { Backend = "Auto" }, Env("Production"));

        Assert.Equal(
            MessagingServiceCollectionExtensions.MessagingBackend.InMemory,
            decision.Backend);
        Assert.Contains("no ConnectionString", decision.Reason);
    }

    [Fact]
    public void Auto_InProdWithConnectionString_ResolvesToServiceBus()
    {
        var decision = MessagingServiceCollectionExtensions.ResolveBackend(
            new MessagingOptions
            {
                Backend = "Auto",
                ServiceBusConnectionString = FakeServiceBusConnection
            },
            Env("Production"));

        Assert.Equal(
            MessagingServiceCollectionExtensions.MessagingBackend.ServiceBus,
            decision.Backend);
        Assert.Contains("ConnectionString configured", decision.Reason);
    }

    [Fact]
    public void ExplicitServiceBus_WithoutConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MessagingServiceCollectionExtensions.ResolveBackend(
                new MessagingOptions { Backend = "ServiceBus" }, Env("Production")));
    }

    [Fact]
    public void ExplicitInMemory_IsHonouredInProduction()
    {
        var decision = MessagingServiceCollectionExtensions.ResolveBackend(
            new MessagingOptions { Backend = "InMemory" }, Env("Production"));

        Assert.Equal(
            MessagingServiceCollectionExtensions.MessagingBackend.InMemory,
            decision.Backend);
        Assert.Contains("forced", decision.Reason);
    }

    [Fact]
    public void ExplicitNull_IsHonoured()
    {
        var decision = MessagingServiceCollectionExtensions.ResolveBackend(
            new MessagingOptions { Backend = "Null" }, Env("Production"));

        Assert.Equal(
            MessagingServiceCollectionExtensions.MessagingBackend.Null,
            decision.Backend);
    }

    [Fact]
    public void UnknownBackend_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MessagingServiceCollectionExtensions.ResolveBackend(
                new MessagingOptions { Backend = "Bogus" }, Env("Production")));
    }

    [Fact]
    public void LegacyConnectionStringKey_BatchEligibility_IsFallback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Backend"] = "Auto",
            ["BatchEligibility:ServiceBus:ConnectionString"] = FakeServiceBusConnection
        }).Build();

        var options = new MessagingOptions { Backend = "Auto" };
        var (cs, deprecated) = MessagingServiceCollectionExtensions
            .ResolveConnectionString(config, options);

        Assert.Equal(FakeServiceBusConnection, cs);
        Assert.Equal("BatchEligibility:ServiceBus:ConnectionString", deprecated);
    }

    [Fact]
    public void LegacyConnectionStringKey_IdCardMirror_IsFallback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Backend"] = "Auto",
            ["IdCard:QnxtMirror:ServiceBusConnectionString"] = FakeServiceBusConnection
        }).Build();

        var options = new MessagingOptions { Backend = "Auto" };
        var (cs, deprecated) = MessagingServiceCollectionExtensions
            .ResolveConnectionString(config, options);

        Assert.Equal(FakeServiceBusConnection, cs);
        Assert.Equal("IdCard:QnxtMirror:ServiceBusConnectionString", deprecated);
    }

    [Fact]
    public void CanonicalKey_WinsOverLegacyKeys()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:ServiceBusConnectionString"] = FakeServiceBusConnection,
            ["BatchEligibility:ServiceBus:ConnectionString"] = "legacy-value"
        }).Build();

        var options = new MessagingOptions
        {
            Backend = "Auto",
            ServiceBusConnectionString = FakeServiceBusConnection
        };
        var (cs, deprecated) = MessagingServiceCollectionExtensions
            .ResolveConnectionString(config, options);

        Assert.Equal(FakeServiceBusConnection, cs);
        Assert.Null(deprecated);
    }

    [Fact]
    public async Task AddChoMessaging_RegistersIMessageBusAsSingleton_InMemoryPath()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Backend"] = "InMemory"
        }).Build();

        services.AddChoMessaging(config, Env("Development"));
        await using var sp = services.BuildServiceProvider();

        var bus1 = sp.GetRequiredService<IMessageBus>();
        var bus2 = sp.GetRequiredService<IMessageBus>();
        Assert.Same(bus1, bus2);
        Assert.IsType<InMemoryMessageBus>(bus1);
    }

    [Fact]
    public async Task AddChoMessaging_RegistersNullBus_WhenForced()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Backend"] = "Null"
        }).Build();

        services.AddChoMessaging(config, Env("Development"));
        await using var sp = services.BuildServiceProvider();

        Assert.IsType<NullMessageBus>(sp.GetRequiredService<IMessageBus>());
    }

    private const string FakeServiceBusConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=ZmFrZQ==";

    private static IHostEnvironment Env(string name)
    {
        var env = new FakeHostEnvironment { EnvironmentName = name };
        return env;
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "cho-test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
