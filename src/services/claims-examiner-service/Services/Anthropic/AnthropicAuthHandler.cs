using Microsoft.Extensions.Configuration;

namespace ClaimsExaminerService.Services.Anthropic;

/// <summary>
/// Per-request Anthropic API key injector. Reads Anthropic:ApiKey from
/// IConfiguration on every outbound request, so Azure Key Vault rotation
/// picks up without an app restart — the configuration provider's reload
/// semantics drive the key we attach to the header.
/// </summary>
public class AnthropicAuthHandler : DelegatingHandler
{
    private readonly IConfiguration _configuration;

    public AnthropicAuthHandler(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = _configuration["Anthropic:ApiKey"] ?? string.Empty;
        request.Headers.Remove("x-api-key");
        request.Headers.Add("x-api-key", apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
