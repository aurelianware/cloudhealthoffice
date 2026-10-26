using System.Net;
using System.Text;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Services;

public sealed class GoogleSecretManagerAppealEncryptionTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.EndsWith("/secrets/appeal-body-encryption-key-v1/versions/latest:access", request.RequestUri!.AbsoluteUri);
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"payload\":{\"data\":\"QUFFQ0F3UUZCZ2NJQ1FvTERBME9EeEFSRWhNVUZSWVhHQmthR3h3ZEhoOD0=\",\"dataCrc32c\":\"1080124008\"}}", Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Google_payload_drives_existing_versioned_AES_encryption_without_plaintext_fallback()
    {
        var handler = new Handler();
        using var provider = new GoogleSecretManagerSecretProvider(
            new SecretProviderOptions { GoogleSecretManagerProjectId = "demo-project", GoogleSecretManagerSecretNames = ["appeal-body-encryption-key-v1"] },
            new HttpClient(handler), _ => Task.FromResult("synthetic-token"), NullLogger<GoogleSecretManagerSecretProvider>.Instance);
        var keys = new RotatingKeyProvider(provider, NullLogger<RotatingKeyProvider>.Instance);
        var encryptor = new AppealFieldEncryptor(keys, NullLogger<AppealFieldEncryptor>.Instance, new AppealEncryptionOptions());
        var ciphertext = await encryptor.EncryptAsync("Synthetic GCP appeal summary");
        Assert.NotNull(ciphertext);
        Assert.DoesNotContain("Synthetic GCP appeal summary", ciphertext);
        Assert.Equal("Synthetic GCP appeal summary", await encryptor.DecryptAsync(ciphertext));
        Assert.Equal(1, handler.Calls);
    }

}
