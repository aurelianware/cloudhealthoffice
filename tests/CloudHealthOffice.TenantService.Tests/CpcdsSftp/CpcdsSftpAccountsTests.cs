using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TenantService.CpcdsSftp;

namespace CloudHealthOffice.TenantService.Tests.CpcdsSftp;

/// <summary>
/// A plan's SFTP login for the hosted CPCDS drop: only well-formed, strong
/// public keys are accepted, and the login SFTPGo is asked to create confines
/// the plan to its own prefix of the drop bucket, keys only.
/// </summary>
public class CpcdsSftpAccountsTests
{
    private const string Tenant = "plan-a";
    private const string Bucket = "demo-cho-cpcds-drops";

    internal static string Ed25519Line(byte fill = 7, string comment = "core-prod")
        => $"ssh-ed25519 {Convert.ToBase64String(Blob("ssh-ed25519", Enumerable.Repeat(fill, 32).ToArray()))} {comment}";

    private static string RsaLine(int bits)
    {
        using var rsa = RSA.Create(bits);
        var p = rsa.ExportParameters(false);
        // mpint: a leading zero byte when the top bit is set.
        var modulus = (p.Modulus![0] & 0x80) != 0 ? [0, .. p.Modulus] : p.Modulus;
        return $"ssh-rsa {Convert.ToBase64String(Blob("ssh-rsa", p.Exponent!, modulus))}";
    }

    private static byte[] Blob(string type, params byte[][] fields)
    {
        var parts = new List<byte[]> { Encoding.ASCII.GetBytes(type) };
        parts.AddRange(fields);
        var buffer = new List<byte>();
        foreach (var part in parts)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)part.Length);
            buffer.AddRange(length);
            buffer.AddRange(part);
        }
        return buffer.ToArray();
    }

    // ── keys ────────────────────────────────────────────────────────────

    [Fact]
    public void An_ed25519_key_is_accepted_without_its_comment()
    {
        var (key, error) = SshPublicKey.Parse("  " + Ed25519Line(comment: "jane@laptop") + "  ");

        error.Should().BeNull();
        key!.Line.Should().StartWith("ssh-ed25519 ").And.NotContain("jane");
        key.Fingerprint.Should().StartWith("SHA256:").And.NotEndWith("=");
    }

    [Fact]
    public void A_3072_bit_rsa_key_is_accepted_and_a_2048_bit_one_is_not()
    {
        SshPublicKey.Parse(RsaLine(3072)).Error.Should().BeNull();
        SshPublicKey.Parse(RsaLine(2048)).Error.Should().Contain("3072").And.Contain("2048");
    }

    [Theory]
    [InlineData("", "Not an OpenSSH public key")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3M=", "not accepted")]
    [InlineData("ssh-ed25519 not-base64!!", "not base64")]
    public void Malformed_keys_are_refused_with_a_reason(string line, string reason)
        => SshPublicKey.Parse(line).Error.Should().Contain(reason);

    [Fact]
    public void A_key_whose_data_names_another_type_is_refused()
    {
        var mismatched = "ssh-rsa " + Ed25519Line().Split(' ')[1];
        SshPublicKey.Parse(mismatched).Error.Should().Contain("does not match");
    }

    [Fact]
    public void A_truncated_ed25519_key_is_refused()
    {
        var truncated = $"ssh-ed25519 {Convert.ToBase64String(Blob("ssh-ed25519", new byte[16]))}";
        SshPublicKey.Parse(truncated).Error.Should().Contain("malformed");
    }

    // ── SFTPGo ──────────────────────────────────────────────────────────

    private static (SftpGoCpcdsSftpAccounts Accounts, FakeSftpGo Server) Accounts(bool enabled = true)
    {
        var server = new FakeSftpGo();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(SftpGoCpcdsSftpAccounts.HttpClientName)).Returns(() => new HttpClient(server));
        var options = Options.Create(new CpcdsSftpOptions
        {
            Enabled = enabled,
            ApiBaseUrl = "http://sftpgo-api.cho-sftp:8080",
            AdminUsername = "cho-tenant-service",
            AdminPassword = "s3cret",
            Bucket = Bucket,
            Host = "203.0.113.5",
        });
        return (new SftpGoCpcdsSftpAccounts(factory.Object, options), server);
    }

    private static SshPublicKey Key(byte fill = 7) => SshPublicKey.Parse(Ed25519Line(fill)).Key!;

    [Fact]
    public async Task Creating_a_login_confines_the_plan_to_its_prefix_with_keys_only()
    {
        var (accounts, server) = Accounts();

        var account = await accounts.UpsertAsync(Tenant, [Key()]);

        account.Exists.Should().BeTrue();
        account.Enabled.Should().BeTrue();
        account.Username.Should().Be(Tenant);
        account.Host.Should().Be("203.0.113.5");
        account.KeyFingerprints.Should().Equal(Key().Fingerprint);

        var user = server.Users[Tenant];
        user["filesystem"]!["provider"]!.GetValue<int>().Should().Be(2);
        user["filesystem"]!["gcsconfig"]!["bucket"]!.GetValue<string>().Should().Be(Bucket);
        user["filesystem"]!["gcsconfig"]!["key_prefix"]!.GetValue<string>().Should().Be("plan-a/");
        user["filters"]!["denied_login_methods"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo("password", "keyboard-interactive");
        var permissions = user["permissions"]!.AsObject();
        permissions["/"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("list");
        permissions["/processed"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("list", "download");
        permissions["/outbound"]!.AsArray().Select(n => n!.GetValue<string>()).Should().BeEquivalentTo("list", "download");
        permissions["/inbound"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("upload");
        user.ToJsonString().Should().NotContain("password\":\"");
    }

    [Fact]
    public async Task The_admin_credential_buys_a_token_and_is_never_sent_with_user_calls()
    {
        var (accounts, server) = Accounts();

        await accounts.UpsertAsync(Tenant, [Key()]);

        server.Requests.First().Should().Be("GET /api/v2/token Basic");
        server.Requests.Skip(1).Should().OnlyContain(r => r.EndsWith(" Bearer"));
    }

    [Fact]
    public async Task Setting_keys_again_replaces_them_and_turns_the_login_back_on()
    {
        var (accounts, server) = Accounts();
        await accounts.UpsertAsync(Tenant, [Key(1)]);
        await accounts.DisableAsync(Tenant);
        (await accounts.GetAsync(Tenant)).Enabled.Should().BeFalse();

        var account = await accounts.UpsertAsync(Tenant, [Key(2), Key(3)]);

        account.Enabled.Should().BeTrue();
        account.KeyFingerprints.Should().Equal(Key(2).Fingerprint, Key(3).Fingerprint);
        server.Requests.Should().Contain("PUT /api/v2/users/plan-a Bearer");
    }

    [Fact]
    public async Task A_tenant_without_a_login_reads_as_not_existing()
    {
        var (accounts, _) = Accounts();
        var account = await accounts.GetAsync(Tenant);
        account.Exists.Should().BeFalse();
        account.Enabled.Should().BeFalse();
        (await accounts.DisableAsync(Tenant)).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_called_when_the_hosted_drop_is_off()
    {
        var (accounts, server) = Accounts(enabled: false);
        var act = () => accounts.GetAsync(Tenant);
        await act.Should().ThrowAsync<CpcdsSftpUnavailableException>();
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_from_the_server_is_reported_as_unavailable()
    {
        var (accounts, server) = Accounts();
        server.RefuseWrites = true;
        var act = () => accounts.UpsertAsync(Tenant, [Key()]);
        (await act.Should().ThrowAsync<CpcdsSftpUnavailableException>()).Which.Message.Should().Contain("HTTP 400");
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("Plan A")]
    public async Task A_tenant_id_that_cannot_name_a_login_is_refused(string tenantId)
    {
        var (accounts, server) = Accounts();
        var act = () => accounts.GetAsync(tenantId);
        await act.Should().ThrowAsync<ArgumentException>();
        server.Requests.Should().BeEmpty();
    }

    /// <summary>Just enough of SFTPGo's REST API: token, and users by name.</summary>
    private sealed class FakeSftpGo : HttpMessageHandler
    {
        public Dictionary<string, JsonObject> Users { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public bool RefuseWrites { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path} {request.Headers.Authorization?.Scheme}");

            if (path == "/api/v2/token")
            {
                return request.Headers.Authorization?.Scheme == "Basic"
                    ? Json(HttpStatusCode.OK, """{"access_token":"jwt","expires_at":"2026-11-01T00:00:00Z"}""")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (request.Headers.Authorization?.Scheme != "Bearer")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var name = path.StartsWith("/api/v2/users/") ? Uri.UnescapeDataString(path["/api/v2/users/".Length..]) : null;
            if (request.Method == HttpMethod.Get && name is not null)
                return Users.TryGetValue(name, out var user)
                    ? Json(HttpStatusCode.OK, user.ToJsonString())
                    : Json(HttpStatusCode.NotFound, """{"error":"not found"}""");

            if (RefuseWrites)
                return Json(HttpStatusCode.BadRequest, """{"error":"invalid public key"}""");

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            if (request.Method == HttpMethod.Post && path == "/api/v2/users")
            {
                Users[body["username"]!.GetValue<string>()] = body;
                return Json(HttpStatusCode.Created, body.ToJsonString());
            }
            if (request.Method == HttpMethod.Put && name is not null && Users.ContainsKey(name))
            {
                Users[name] = body;
                return Json(HttpStatusCode.OK, """{"message":"User updated"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
