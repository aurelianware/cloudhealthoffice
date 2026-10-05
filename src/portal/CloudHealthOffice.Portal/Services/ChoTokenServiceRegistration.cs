using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Web;

namespace CloudHealthOffice.Portal.Services;

public static class ChoTokenServiceRegistration
{
    /// <summary>
    /// CHO tokens: the signed-in user's Entra token is exchanged at the CHO token
    /// service for a CHO token, which is sent to CHO backend services only.
    ///
    /// Registers <see cref="IChoTokenProvider"/> (scoped: per circuit, or per HTTP
    /// request), the token service's named client, and the scoped
    /// <see cref="HttpClient"/> every portal service and page uses. That client wraps
    /// <see cref="ChoBearerTokenHandler"/> around the pooled <c>"default"</c> pipeline
    /// in the caller's own scope, instead of via AddHttpMessageHandler, because
    /// IHttpClientFactory creates pipeline handlers in a separate DI scope where the
    /// circuit's (or request's) user is not available. It adds the CHO token and
    /// X-Tenant-ID only for hosts configured under Services:* (not the token
    /// service, Argo or Prometheus); other hosts get nothing. The <c>"default"</c>
    /// client's primary handler is configured by the host.
    /// </summary>
    public static IServiceCollection AddChoUserTokens(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddSingleton<ChoServiceHosts>();
        services.AddScoped<IChoReauthenticationHandler, MicrosoftIdentityReauthenticationHandler>();
        services.AddScoped<IChoTokenProvider>(sp => new ChoTokenProvider(
            sp.GetRequiredService<AuthenticationStateProvider>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<ChoTokenProvider>>(),
            sp.GetService<ITokenAcquisition>(),
            sp.GetService<IChoReauthenticationHandler>(),
            sp.GetService<IDistributedCache>(),
            // Outside a circuit (a minimal-API request) the user comes from the request.
            httpContextAccessor: sp.GetService<IHttpContextAccessor>()));

        // The token service gets the user's Entra token, never a CHO token.
        services.AddHttpClient(ChoTokenProvider.TokenServiceClientName)
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));

        services.AddScoped(sp =>
        {
            var handler = new ChoBearerTokenHandler(
                sp.GetRequiredService<IChoTokenProvider>(),
                sp.GetRequiredService<ChoServiceHosts>(),
                sp.GetRequiredService<ILogger<ChoBearerTokenHandler>>())
            {
                InnerHandler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("default"),
            };
            return new HttpClient(handler, disposeHandler: true);
        });

        return services;
    }
}
