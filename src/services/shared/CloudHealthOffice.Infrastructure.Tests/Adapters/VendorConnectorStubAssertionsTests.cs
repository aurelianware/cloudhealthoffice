namespace CloudHealthOffice.Infrastructure.Tests.Adapters;

public class VendorConnectorStubAssertionsTests
{
    public interface IProbe
    {
        Task<string> ReadAsync(ProbeRequest request, CancellationToken ct = default);
        Task<string> SearchAsync(string term, CancellationToken ct = default);
    }

    public sealed class ProbeRequest
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class Stub : IProbe
    {
        public Task<string> ReadAsync(ProbeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SearchAsync(string term, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class SyntheticSuccess : IProbe
    {
        public Task<string> ReadAsync(ProbeRequest request, CancellationToken ct = default) => Task.FromResult("fake");
        public async Task<string> SearchAsync(string term, CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("wrong failure");
        }
    }

    [Fact]
    public async Task A_template_that_throws_NotImplementedException_everywhere_passes()
    {
        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync<IProbe>(new Stub());

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task Synthetic_success_and_other_failures_are_reported()
    {
        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync<IProbe>(new SyntheticSuccess());

        offenders.Should().HaveCount(2);
        offenders.Should().Contain(o => o.StartsWith("ReadAsync returned"));
        offenders.Should().Contain(o => o.StartsWith("SearchAsync threw InvalidOperationException"));
    }

    [Fact]
    public async Task Implemented_methods_are_skipped()
    {
        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync<IProbe>(
            new SyntheticSuccess(), nameof(IProbe.ReadAsync), nameof(IProbe.SearchAsync));

        offenders.Should().BeEmpty();
    }
}
