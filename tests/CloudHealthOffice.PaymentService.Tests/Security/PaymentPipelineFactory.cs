using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentService.Repositories;
using PaymentService.Services;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// The real payment-service pipeline: real authentication, controllers, payment
/// and reversal run services, separation of duties and 835 generator. Only the
/// repositories and the trading-partner client are substitutes, and the
/// claims-service client's wire is captured (it answers 200 with an empty list).
/// </summary>
public sealed class PaymentPipelineFactory : WebApplicationFactory<Program>
{
    public IPaymentRepository Payments { get; } = Substitute.For<IPaymentRepository>();
    public IPaymentRunRepository Runs { get; } = Substitute.For<IPaymentRunRepository>();
    public IReversalRunRepository ReversalRuns { get; } = Substitute.For<IReversalRunRepository>();
    public IEraEnvelopeRepository Envelopes { get; } = Substitute.For<IEraEnvelopeRepository>();
    public ITradingPartnersClient TradingPartners { get; } = Substitute.For<ITradingPartnersClient>();
    public InMemoryClaimReservationRepository Reservations { get; } = new();
    public NoCallerHost.CapturingHandler Claims { get; } = new() { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") } };

    /// <summary>Bank numbers the 835 generator puts in BPR (payer then payee).</summary>
    public const string PayerRouting = "011000015";
    public const string PayerAccount = "123456789012";
    public const string PayeeRouting = "021000021";
    public const string PayeeAccount = "987654321098";
    /// <summary>BPR10 / TRN03 originating company identifier ("1" + payer TIN).</summary>
    public const string OriginatingCompanyId = "1123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Era:PayerRoutingNumber"] = PayerRouting,
            ["Era:PayerAccountNumber"] = PayerAccount,
            ["Era:OriginatingCompanyId"] = OriginatingCompanyId,
            ["Era:PayeeRoutingNumber"] = PayeeRouting,
            ["Era:PayeeAccountNumber"] = PayeeAccount,
        }));
        builder.ConfigureServices(services =>
        {
            var remove = services
                .Where(d => d.ServiceType == typeof(IPaymentRepository)
                         || d.ServiceType == typeof(IPaymentRunRepository)
                         || d.ServiceType == typeof(IReversalRunRepository)
                         || d.ServiceType == typeof(IEraEnvelopeRepository)
                         || d.ServiceType == typeof(ITradingPartnersClient)
                         || d.ServiceType.FullName?.Contains("Cosmos") == true
                         || d.ServiceType.FullName?.Contains("Mongo") == true
                         || d.ImplementationType?.FullName?.Contains("Cosmos") == true
                         || d.ImplementationType?.FullName?.Contains("Mongo") == true)
                .ToList();
            foreach (var d in remove)
                services.Remove(d);

            services.AddSingleton(Payments);
            services.AddSingleton(Runs);
            services.AddSingleton(ReversalRuns);
            services.AddSingleton(Envelopes);
            services.AddSingleton(TradingPartners);
            services.AddSingleton<IClaimReservationRepository>(Reservations);
            services.AddSingleton<IReservationAuditLog>(new InMemoryReservationAuditLog());
            services.AddSingleton<IProviderReceivableRepository>(new InMemoryProviderReceivableRepository());
            Runs.TryStartAsync(default!, default!, default).ReturnsForAnyArgs(true);
            ReversalRuns.TryStartAsync(default!, default!, default).ReturnsForAnyArgs(true);
            services.AddHttpClient(ClaimsServiceClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Claims);
        });
    }
}
