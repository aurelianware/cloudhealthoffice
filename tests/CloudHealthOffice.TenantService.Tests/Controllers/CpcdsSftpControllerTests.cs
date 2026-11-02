using CloudHealthOffice.TenantService.Tests.Configuration;
using CloudHealthOffice.TenantService.Tests.CpcdsSftp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TenantService.Controllers;
using TenantService.CpcdsSftp;
using TenantService.Security;

namespace CloudHealthOffice.TenantService.Tests.Controllers;

/// <summary>
/// The CPCDS SFTP login endpoints: every key is checked before the SFTP
/// server is touched, one bad key refuses the whole request, and an SFTP
/// outage is a 503 rather than an error page.
/// </summary>
public class CpcdsSftpControllerTests
{
    private const string Tenant = "plan-a";
    private readonly Mock<ICpcdsSftpAccounts> _accounts = new(MockBehavior.Strict);

    private CpcdsSftpController Controller() => new(_accounts.Object,
        new TenantAuditLog(NullLoggerFactory.Instance, new TestActor("admin", Tenant, "TenantAdmin")));

    private static CpcdsSftpAccount Account(params string[] fingerprints) => new()
    {
        TenantId = Tenant, Exists = true, Enabled = true, Username = Tenant, KeyFingerprints = fingerprints,
    };

    private static CpcdsSftpKeysRequest Keys(params string[] lines) => new() { PublicKeys = lines.ToList() };

    private static IReadOnlyList<string> Errors(IActionResult result)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var errors = bad.Value!.GetType().GetProperty("errors")!.GetValue(bad.Value);
        return ((IEnumerable<string>)errors!).ToList();
    }

    [Fact]
    public async Task Get_returns_the_login()
    {
        _accounts.Setup(a => a.GetAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(Account("SHA256:x"));

        var result = await Controller().Get(Tenant, default);

        ((CpcdsSftpAccount)((OkObjectResult)result).Value!).KeyFingerprints.Should().Equal("SHA256:x");
    }

    [Fact]
    public async Task No_keys_is_400_and_the_server_is_not_called()
    {
        Errors(await Controller().Put(Tenant, Keys(), default)).Should().ContainSingle().Which.Should().Contain("at least one");
        Errors(await Controller().Put(Tenant, new CpcdsSftpKeysRequest(), default)).Should().ContainSingle();
    }

    [Fact]
    public async Task More_than_the_maximum_keys_is_400()
    {
        var lines = Enumerable.Range(1, CpcdsSftpKeysRequest.MaxKeys + 1)
            .Select(i => CpcdsSftpAccountsTests.Ed25519Line((byte)i)).ToArray();

        Errors(await Controller().Put(Tenant, Keys(lines), default)).Should().ContainSingle().Which.Should().Contain("at most");
    }

    [Fact]
    public async Task One_bad_key_refuses_the_whole_request_and_names_its_position()
    {
        var result = await Controller().Put(Tenant, Keys(CpcdsSftpAccountsTests.Ed25519Line(), "not-a-key"), default);

        Errors(result).Should().ContainSingle().Which.Should().StartWith("publicKeys[1]:");
        _accounts.Verify(a => a.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SshPublicKey>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Valid_keys_are_sent_for_the_route_tenant_only()
    {
        IReadOnlyList<SshPublicKey>? sent = null;
        _accounts.Setup(a => a.UpsertAsync(Tenant, It.IsAny<IReadOnlyList<SshPublicKey>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<SshPublicKey>, CancellationToken>((_, keys, _) => sent = keys)
            .ReturnsAsync(Account("SHA256:a", "SHA256:b"));

        var result = await Controller().Put(Tenant,
            Keys(CpcdsSftpAccountsTests.Ed25519Line(1), CpcdsSftpAccountsTests.Ed25519Line(2, "jane@laptop")), default);

        result.Should().BeOfType<OkObjectResult>();
        sent.Should().HaveCount(2);
        sent!.Should().OnlyContain(k => !k.Line.Contains("jane"));
    }

    [Fact]
    public async Task An_unreachable_sftp_server_is_503_with_its_reason()
    {
        _accounts.Setup(a => a.UpsertAsync(Tenant, It.IsAny<IReadOnlyList<SshPublicKey>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CpcdsSftpUnavailableException("SFTPGo refused the user."));

        var result = await Controller().Put(Tenant, Keys(CpcdsSftpAccountsTests.Ed25519Line()), default);

        var error = result.Should().BeOfType<ObjectResult>().Subject;
        error.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        error.Value!.ToString().Should().Contain("SFTPGo refused the user.");
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task Transport_failures_are_503_without_leaking_details(Type exception)
    {
        _accounts.Setup(a => a.DisableAsync(Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exception, "10.0.0.7:8080 connection refused")!);

        var result = await Controller().Delete(Tenant, default);

        var error = result.Should().BeOfType<ObjectResult>().Subject;
        error.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        error.Value!.ToString().Should().NotContain("10.0.0.7");
    }

    [Fact]
    public async Task An_argument_error_from_the_accounts_service_is_400()
    {
        _accounts.Setup(a => a.GetAsync(Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new ArgumentException("bad tenant id"));

        var result = await Controller().Get(Tenant, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_turns_the_login_off()
    {
        _accounts.Setup(a => a.DisableAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CpcdsSftpAccount { TenantId = Tenant, Exists = true, Enabled = false });

        var result = await Controller().Delete(Tenant, default);

        ((CpcdsSftpAccount)((OkObjectResult)result).Value!).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Unexpected_errors_are_not_swallowed()
    {
        _accounts.Setup(a => a.GetAsync(Tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bug"));

        var act = () => Controller().Get(Tenant, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
