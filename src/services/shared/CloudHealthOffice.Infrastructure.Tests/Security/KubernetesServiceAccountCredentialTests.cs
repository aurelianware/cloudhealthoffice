using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Tests.Security;

/// <summary>
/// A service on any Kubernetes cluster (GKE, AKS, EKS, on premises) proves its
/// identity to token-service with its projected service-account token
/// (<c>ChoAuth:ServiceToken:Credential=KubernetesServiceAccount</c>).
/// </summary>
public sealed class KubernetesServiceAccountCredentialTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cho-sa-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TokenFile(string content)
    {
        var path = Path.Combine(_dir, "token");
        File.WriteAllText(path, content);
        return path;
    }

    private static string ProjectedToken(string serviceAccount, DateTime expires)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://container.googleapis.com/v1/projects/cho/locations/us-west1/clusters/cho",
            Audience = "cho-token-service",
            IssuedAt = expires.AddHours(-1),
            NotBefore = expires.AddHours(-1),
            Expires = expires,
            Claims = new Dictionary<string, object> { ["sub"] = "system:serviceaccount:cloudhealthoffice:" + serviceAccount },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(new byte[32]), SecurityAlgorithms.HmacSha256),
        });

    [Fact]
    public async Task Presents_the_projected_token_with_its_expiry()
    {
        var expires = new DateTime(2026, 10, 21, 22, 0, 0, DateTimeKind.Utc);
        var token = ProjectedToken("claims-service", expires);
        var credential = new KubernetesServiceAccountTokenCredential(TokenFile(token + "\n"));

        var access = await credential.GetTokenAsync(new TokenRequestContext(Array.Empty<string>()), default);

        access.Token.Should().Be(token);
        access.ExpiresOn.Should().Be(new DateTimeOffset(expires));
    }

    [Fact]
    public async Task Reads_the_file_again_after_the_kubelet_rotates_it()
    {
        var path = TokenFile(ProjectedToken("claims-service", DateTime.UtcNow.AddMinutes(30)));
        var credential = new KubernetesServiceAccountTokenCredential(path);
        var first = await credential.GetTokenAsync(new TokenRequestContext(Array.Empty<string>()), default);

        var rotated = ProjectedToken("claims-service", DateTime.UtcNow.AddMinutes(60));
        File.WriteAllText(path, rotated);
        var second = await credential.GetTokenAsync(new TokenRequestContext(Array.Empty<string>()), default);

        second.Token.Should().Be(rotated).And.NotBe(first.Token);
    }

    [Fact]
    public async Task An_empty_or_missing_file_is_an_error_not_an_empty_bearer()
    {
        var empty = new KubernetesServiceAccountTokenCredential(TokenFile("  \n"));
        var missing = new KubernetesServiceAccountTokenCredential(Path.Combine(_dir, "absent"));

        await ((Func<Task>)(async () => await empty.GetTokenAsync(new TokenRequestContext(Array.Empty<string>()), default)))
            .Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(async () => await missing.GetTokenAsync(new TokenRequestContext(Array.Empty<string>()), default)))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task The_token_service_source_sends_the_service_account_token_as_its_proof()
    {
        var saToken = ProjectedToken("claims-service", DateTime.UtcNow.AddMinutes(30));
        var options = new ChoServiceTokenOptions
        {
            Source = ChoServiceTokenSourceKind.TokenService,
            Credential = ChoServiceIdentityCredential.KubernetesServiceAccount,
            ServiceAccountTokenFile = TokenFile(saToken),
            ClientId = "claims-service",
            TokenServiceUrl = "http://token-service",
        };
        options.Validate(allowLocalKey: false);

        var tokenService = new RecordingTokenService();
        var source = new TokenServiceChoServiceTokenSource(
            options,
            new KubernetesServiceAccountTokenCredential(options.ServiceAccountTokenFile),
            new HttpClient(tokenService) { BaseAddress = new Uri("http://token-service/") });

        var token = await source.GetTokenAsync("acme");

        token.Should().Be("svc-token");
        tokenService.Authorization.Should().Be("Bearer " + saToken);
    }

    [Fact]
    public void Kubernetes_credential_needs_no_entra_scope()
    {
        var options = new ChoServiceTokenOptions
        {
            Source = ChoServiceTokenSourceKind.TokenService,
            Credential = ChoServiceIdentityCredential.KubernetesServiceAccount,
            ClientId = "claims-service",
            TokenServiceUrl = "http://token-service",
        };

        ((Action)(() => options.Validate(allowLocalKey: false))).Should().NotThrow();
        options.ServiceAccountTokenFile.Should().Be(KubernetesServiceAccountTokenCredential.DefaultTokenFile);
    }

    [Fact]
    public void The_token_file_path_must_be_absolute()
    {
        var options = new ChoServiceTokenOptions
        {
            Source = ChoServiceTokenSourceKind.TokenService,
            Credential = ChoServiceIdentityCredential.KubernetesServiceAccount,
            ServiceAccountTokenFile = "token",
            ClientId = "claims-service",
            TokenServiceUrl = "http://token-service",
        };

        ((Action)(() => options.Validate(allowLocalKey: false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*ServiceAccountTokenFile*");
    }

    [Fact]
    public void The_entra_credential_still_requires_its_scope()
    {
        var options = new ChoServiceTokenOptions
        {
            Source = ChoServiceTokenSourceKind.TokenService,
            ClientId = "claims-service",
            TokenServiceUrl = "http://token-service",
        };

        ((Action)(() => options.Validate(allowLocalKey: false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*EntraScope*");
    }

    private sealed class RecordingTokenService : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Dictionary<string, object?>
                {
                    ["access_token"] = "svc-token",
                    ["expires_in"] = 300,
                    ["tenant_id"] = body.GetProperty("tenantId").GetString(),
                    ["client_id"] = "claims-service",
                }),
            };
        }
    }
}
