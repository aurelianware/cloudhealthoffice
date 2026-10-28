using CloudHealthOffice.Infrastructure.Security;

namespace FhirService.Services;

/// <summary>
/// A request context for work fhir-service does with no request: a bulk
/// export running in the background. It names one tenant and no user, so the
/// readers that take their tenant from the request (the CHO-native FHIR data
/// adapter, the Payer-to-Payer member directory) read that tenant, and their
/// calls to CHO services carry fhir-service's own service token for it
/// (<see cref="BackgroundTenantOutboundHandler"/>).
///
/// Only fhir-service's own code creates one. A real request's context never
/// carries the marker, so no caller can make its request look like this.
/// </summary>
public static class BackgroundTenantContext
{
    private static readonly object Marker = new();

    public static HttpContext Create(IServiceProvider services, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Items["TenantId"] = tenantId;
        context.Items[Marker] = true;
        return context;
    }

    public static bool IsBackground(HttpContext? context) => context?.Items.ContainsKey(Marker) == true;
}

/// <summary>
/// Inside a <see cref="BackgroundTenantContext"/>, asks for fhir-service's own
/// identity on every outbound call (<see cref="ChoOutboundTokenHandlerExtensions.UseChoServiceIdentity"/>).
/// ChoOutboundTokenHandler then names the context's tenant and mints the
/// service token for it, and refuses a call that names another tenant. It only
/// ever attaches tokens to CHO hosts. Outside such a context it does nothing.
///
/// Registered on every factory client before AddChoAuthentication, so it runs
/// before the token handler.
/// </summary>
public sealed class BackgroundTenantOutboundHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;

    public BackgroundTenantOutboundHandler(IHttpContextAccessor accessor) => _accessor = accessor;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (BackgroundTenantContext.IsBackground(_accessor.HttpContext))
            request.UseChoServiceIdentity();
        return base.SendAsync(request, cancellationToken);
    }
}
