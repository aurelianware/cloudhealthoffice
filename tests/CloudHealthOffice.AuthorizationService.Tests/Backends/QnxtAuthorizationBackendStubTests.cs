using AuthorizationService.Backends;
using CloudHealthOffice.Infrastructure.Tests.Adapters;
using FluentAssertions;

namespace CloudHealthOffice.AuthorizationService.Tests.Backends;

/// <summary>
/// ADR 027: the QNXT authorization backend fronts a core that stays
/// authoritative, and until an engagement implements it every operation fails
/// explicitly rather than recording anything in CHO.
/// </summary>
public class QnxtAuthorizationBackendStubTests
{
    [Fact]
    public async Task Unimplemented_backend_is_not_authoritative_and_fails_explicitly()
    {
        var backend = new QnxtAuthorizationBackend();

        backend.BackendKey.Should().Be("qnxt");
        backend.IsAuthoritative.Should().BeFalse();
        var offenders = await VendorConnectorStubAssertions.MethodsThatDoNotFailExplicitlyAsync<IAuthorizationBackend>(backend);

        offenders.Should().BeEmpty();
    }
}
