using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Events.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// The claim-event outbox dispatcher scans only the base claims database. With
/// tenant-scoped databases (<c>MongoDb:UseTenantScoping</c>) a tenant's pending
/// <c>claims.finalized.v1</c> events live in its own database and would never be
/// retried, so the dispatcher refuses to start instead of running silently.
/// The dispatcher is built the way the host builds it (constructor injection),
/// so the test also covers that the configuration actually reaches it.
/// </summary>
public sealed class ClaimEventOutboxScopeTests
{
    private static ClaimEventOutboxDispatcher Build(bool tenantScoping, bool enabled = true, bool kafkaConfigured = true)
    {
        var producer = Substitute.For<IChoEventProducer>();
        producer.IsConfigured.Returns(kafkaConfigured);
        var claims = Substitute.For<IClaimRepository>();
        claims.FindDueClaimEventsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Claim>>([]));

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:UseTenantScoping"] = tenantScoping ? "true" : "false",
            }).Build())
            .AddSingleton(Options.Create(new ClaimEventOutboxOptions { Enabled = enabled }))
            .AddSingleton(producer)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<ClaimEventOutboxDrainer>()
            .AddScoped(_ => claims)
            .BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ClaimEventOutboxDispatcher>(services);
    }

    [Fact]
    public async Task WithTenantScoping_TheDispatcherRefusesToStart()
    {
        var dispatcher = Build(tenantScoping: true);
        try
        {
            var start = () => dispatcher.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*MongoDb:UseTenantScoping*not supported*");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, true, true)]  // base database only: supported
    [InlineData(true, false, true)]  // dispatcher switched off by the operator
    [InlineData(true, true, false)]  // no Kafka: nothing is queued
    public async Task OtherwiseItStarts(bool tenantScoping, bool enabled, bool kafkaConfigured)
    {
        var dispatcher = Build(tenantScoping, enabled, kafkaConfigured);

        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);
    }
}
