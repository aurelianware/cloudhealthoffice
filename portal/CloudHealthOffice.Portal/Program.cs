using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Hubs;
using System.Net.Http;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();

// Add HttpClient for service calls
builder.Services.AddHttpClient("default")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 50
    });

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("default"));

// Register microservice clients
builder.Services.AddScoped<IMemberService, MemberService>();
builder.Services.AddScoped<ICoverageService, CoverageService>();
builder.Services.AddScoped<IClaimsService, ClaimsService>();
builder.Services.AddScoped<IEligibilityService, EligibilityService>();
builder.Services.AddScoped<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IProviderService, ProviderService>();
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanService>();
builder.Services.AddScoped<IWorkflowService, WorkflowService>();
builder.Services.AddScoped<IMetricsService, MetricsService>();

// Add SignalR
builder.Services.AddSignalR();

// Add session state
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();

app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<ClaimsHub>("/hubs/claims");
app.MapHub<WorkflowHub>("/hubs/workflows");
app.MapGet("/health", () => Results.Ok("ok"));
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.svg"));
app.MapFallbackToPage("/_Host");

app.Run();
