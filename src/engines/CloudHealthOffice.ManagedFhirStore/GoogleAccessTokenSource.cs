using Google.Apis.Auth.OAuth2;

namespace CloudHealthOffice.ManagedFhirStore;

/// <summary>Supplies OAuth access tokens for Google APIs. Replaced in tests.</summary>
public interface IGoogleAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Application Default Credentials scoped to the Cloud Healthcare API: the
/// Workload Identity service account on GKE, a user login on a workstation.
/// The credential caches and refreshes its own token.
/// </summary>
public sealed class ApplicationDefaultGoogleAccessTokenSource : IGoogleAccessTokenSource
{
    private const string CloudHealthcareScope = "https://www.googleapis.com/auth/cloud-healthcare";

    private readonly Lazy<Task<GoogleCredential>> _credential = new(async () =>
        (await GoogleCredential.GetApplicationDefaultAsync()).CreateScoped(CloudHealthcareScope));

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var credential = await _credential.Value;
        return await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
    }
}
