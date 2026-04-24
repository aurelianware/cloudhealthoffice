using CloudHealthOffice.Infrastructure.Caching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Tests.Caching;

public class CacheKeyGuardTests
{
    [Fact]
    public void Build_WithTenantContext_PrependsEnvAndTenant()
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");

        var key = guard.Build("enrollment:config:txmco01");

        Assert.Equal("production:txmco01:enrollment:config:txmco01", key);
    }

    [Fact]
    public void Build_GlobalScope_UsesGlobalSentinel_NoTenantRequired()
    {
        var guard = MakeGuard("Development", tenantId: null);

        var key = guard.Build("feature-flags:pas-auto", CacheScope.Global);

        Assert.Equal("development:_global:feature-flags:pas-auto", key);
    }

    [Fact]
    public void Build_TenantScopeWithoutContext_Throws()
    {
        var guard = MakeGuard("Production", tenantId: null);

        Assert.Throws<InvalidOperationException>(() => guard.Build("enrollment:config"));
    }

    [Theory]
    [InlineData("enrollment:ssn:123")]          // ssn
    [InlineData("users:ssnHash:abc")]           // ssnHash (hashes of SSN still rejected)
    [InlineData("members:MBI:something")]       // mbi, case-insensitive
    [InlineData("mbr:DOB:1970-01-01")]           // dob
    [InlineData("config:memberId:M12345")]       // memberId raw
    [InlineData("patient:patientId:P99")]        // patientId
    public void Build_RejectsPhiTokens(string badKey)
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");
        Assert.Throws<ArgumentException>(() => guard.Build(badKey));
    }

    [Fact]
    public void Build_AllowsMemberIdHash_NotPhi()
    {
        // The hashed form of a member ID is explicitly permitted. Rejecting
        // it would block a large class of legitimate per-member cache keys
        // that have already been pseudonymized.
        var guard = MakeGuard("Production", tenantId: "txmco01");

        var key = guard.Build("member:memberIdHash:deadbeef");

        Assert.Equal("production:txmco01:member:memberIdHash:deadbeef", key);
    }

    [Theory]
    [InlineData("enrollment config txmco01")]      // spaces
    [InlineData("enrollment\tconfig")]            // tab
    [InlineData("enrollment\nconfig")]            // newline
    [InlineData("enrollment\0config")]            // null byte
    public void Build_RejectsControlAndWhitespaceChars(string badKey)
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");
        Assert.Throws<ArgumentException>(() => guard.Build(badKey));
    }

    [Fact]
    public void Build_NullOrEmptyKey_Throws()
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");

        Assert.Throws<ArgumentException>(() => guard.Build(""));
        Assert.Throws<ArgumentException>(() => guard.Build(null!));
    }

    [Fact]
    public void Build_RejectionMessage_DoesNotEchoRawKey()
    {
        // The exception message flows through ExceptionHandlingMiddleware
        // and into production logs; if it echoed the PHI-containing key,
        // the guard would leak the exact value it was designed to reject.
        var guard = MakeGuard("Production", tenantId: "txmco01");

        var sensitive = "member:ssn:123-45-6789";
        var ex = Assert.Throws<ArgumentException>(() => guard.Build(sensitive));
        Assert.DoesNotContain("123-45-6789", ex.Message);
        Assert.DoesNotContain(sensitive, ex.Message);
        Assert.Contains("ssn", ex.Message); // token name is OK — not the value
    }

    [Fact]
    public void Build_WhitespaceRejectionMessage_DoesNotEchoRawKey()
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");
        var sensitive = "something member-ssn-123-45-6789";
        var ex = Assert.Throws<ArgumentException>(() => guard.Build(sensitive));
        Assert.DoesNotContain(sensitive, ex.Message);
        Assert.DoesNotContain("123-45-6789", ex.Message);
    }

    [Fact]
    public void BuildPrefix_TenantScope_UsesAmbientTenant()
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");
        Assert.Equal("production:txmco01:", guard.BuildPrefix());
    }

    [Fact]
    public void BuildPrefix_GlobalScope_UsesGlobalSentinel()
    {
        var guard = MakeGuard("Development", tenantId: null);
        Assert.Equal("development:_global:", guard.BuildPrefix(CacheScope.Global));
    }

    [Fact]
    public void BuildMany_AppliesGuardToEach()
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");

        var keys = guard.BuildMany(new[] { "a:b", "c:d" });

        Assert.Equal(new[] { "production:txmco01:a:b", "production:txmco01:c:d" }, keys);
    }

    [Fact]
    public void BuildMany_OneBadKey_ThrowsAndDoesNotReturnPartial()
    {
        var guard = MakeGuard("Production", tenantId: "txmco01");

        Assert.Throws<ArgumentException>(() =>
            guard.BuildMany(new[] { "ok:one", "bad:ssn:2", "ok:three" }));
    }

    private static CacheKeyGuard MakeGuard(string envName, string? tenantId)
    {
        var accessor = new HttpContextAccessor();
        if (tenantId is not null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Items["TenantId"] = tenantId;
            accessor.HttpContext = ctx;
        }

        var env = new FakeEnv { EnvironmentName = envName };
        return new CacheKeyGuard(accessor, env);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "cho-test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
