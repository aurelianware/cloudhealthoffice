using Azure.Messaging.ServiceBus;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Messaging;

/// <summary>
/// Runs the shared contract against a real Azure Service Bus namespace.
/// Tests skip (not fail) unless <c>CHO_SERVICEBUS_CONNECTION_STRING</c>
/// is set. CI filters by <c>Category=Integration</c> to exclude this
/// class from the default unit-test run anyway.
/// </summary>
[Trait("Category", "Integration")]
public class ServiceBusMessageBusContractTests : MessageBusContractTests
{
    private const string EnvVar = "CHO_SERVICEBUS_CONNECTION_STRING";

    protected override ValueTask<IMessageBus> CreateBusAsync()
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(cs),
            $"Set {EnvVar} to run Service Bus contract tests");

        var client = new ServiceBusClient(cs);
        return ValueTask.FromResult<IMessageBus>(
            new ServiceBusMessageBus(client, NullLogger<ServiceBusMessageBus>.Instance));
    }
}
