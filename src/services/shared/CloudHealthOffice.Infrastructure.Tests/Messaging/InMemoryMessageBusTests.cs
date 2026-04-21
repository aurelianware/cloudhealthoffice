using CloudHealthOffice.Infrastructure.Messaging;

namespace CloudHealthOffice.Infrastructure.Tests.Messaging;

public class InMemoryMessageBusTests : MessageBusContractTests
{
    protected override ValueTask<IMessageBus> CreateBusAsync()
        => ValueTask.FromResult<IMessageBus>(new InMemoryMessageBus());
}
