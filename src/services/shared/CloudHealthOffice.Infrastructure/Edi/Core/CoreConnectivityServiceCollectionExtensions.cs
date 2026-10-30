using System.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

public static class CoreConnectivityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the CAQH CORE real-time envelope processor and the submitter
    /// authenticator (configuration section <c>CoreConnectivity</c>). The host
    /// registers its own <see cref="ICoreRealTimeHandler"/>s for the payloads
    /// it answers.
    /// </summary>
    public static IServiceCollection AddChoCoreConnectivity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CoreConnectivityOptions>(configuration.GetSection(CoreConnectivityOptions.SectionName));
        services.TryAddSingleton<ICoreSubmitterAuthenticator, CoreSubmitterAuthenticator>();
        services.TryAddScoped<CoreRealTimeProcessor>();
        return services;
    }

    /// <summary>
    /// Maps the CORE real-time endpoint at <paramref name="pattern"/>:
    /// <list type="bullet">
    /// <item><c>POST</c>: a SOAP 1.2 or MIME multipart CORE envelope.</item>
    /// <item><c>GET ?wsdl</c>: the WSDL for the SOAP form.</item>
    /// <item><c>GET availability</c>: the published downtime schedule and weekly availability.</item>
    /// </list>
    /// The endpoint is anonymous to the platform's token check because CORE
    /// credentials (client certificate, UsernameToken, MIME UserName/Password)
    /// are not CHO tokens; <see cref="CoreSubmitterAuthenticator"/> checks
    /// every request before any payload is read. A CHO bearer token, when one
    /// is sent, must carry <paramref name="requiredPermission"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapChoCoreConnectivity(this IEndpointRouteBuilder app, string pattern, string requiredPermission)
    {
        var group = app.MapGroup(pattern).WithTags("CAQH CORE").ExcludeFromDescription();

        group.MapPost("", (HttpContext http, CoreRealTimeProcessor processor) => processor.ProcessAsync(http, requiredPermission))
            .AllowAnonymous()
            .DisableAntiforgery();

        group.MapGet("", (HttpContext http) =>
        {
            if (!http.Request.Query.ContainsKey("wsdl"))
                return Results.Text("POST a CAQH CORE vC2.2.0 real-time envelope here. GET ?wsdl for the SOAP WSDL.", "text/plain");
            var location = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{http.Request.Path}";
            return Results.Text(CoreWsdl.For(location), "text/xml");
        }).AllowAnonymous();

        // CORE system availability: the published downtime schedule and the
        // weekly availability figure. Public, like the WSDL; it names no
        // trading partner.
        group.MapGet("availability", (HttpContext http, IOptionsMonitor<CoreConnectivityOptions> options) =>
            {
                var time = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                return Results.Json(CoreAvailabilityReport.Build(options.CurrentValue.Availability, time.GetUtcNow()));
            })
            .AllowAnonymous();

        var availability = app.ServiceProvider.GetRequiredService<IOptionsMonitor<CoreConnectivityOptions>>().CurrentValue.Availability;
        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CoreAvailabilitySchedule));
        foreach (var problem in CoreAvailabilitySchedule.Problems(availability))
            logger.LogWarning("CORE availability schedule: {Problem}", problem);

        return group;
    }
}

/// <summary>WSDL 1.1 for the CORE real-time SOAP 1.2 operation.</summary>
public static class CoreWsdl
{
    public static string For(string location) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
             xmlns:soap12="http://schemas.xmlsoap.org/wsdl/soap12/"
             xmlns:xsd="http://www.w3.org/2001/XMLSchema"
             xmlns:CORE="{CoreRule.XsdNamespace}"
             xmlns:tns="{CoreRule.WsdlNamespace}"
             targetNamespace="{CoreRule.WsdlNamespace}"
             name="CORETransactions">
  <types>
    <xsd:schema targetNamespace="{CoreRule.XsdNamespace}" elementFormDefault="unqualified">
      <xsd:element name="COREEnvelopeRealTimeRequest">
        <xsd:complexType><xsd:sequence>
          <xsd:element name="PayloadType" type="xsd:string"/>
          <xsd:element name="ProcessingMode" type="xsd:string"/>
          <xsd:element name="PayloadID" type="xsd:string"/>
          <xsd:element name="TimeStamp" type="xsd:dateTime"/>
          <xsd:element name="SenderID" type="xsd:string"/>
          <xsd:element name="ReceiverID" type="xsd:string"/>
          <xsd:element name="CORERuleVersion" type="xsd:string"/>
          <xsd:element name="Payload" type="xsd:string"/>
        </xsd:sequence></xsd:complexType>
      </xsd:element>
      <xsd:element name="COREEnvelopeRealTimeResponse">
        <xsd:complexType><xsd:sequence>
          <xsd:element name="PayloadType" type="xsd:string"/>
          <xsd:element name="ProcessingMode" type="xsd:string"/>
          <xsd:element name="PayloadID" type="xsd:string"/>
          <xsd:element name="TimeStamp" type="xsd:dateTime"/>
          <xsd:element name="SenderID" type="xsd:string"/>
          <xsd:element name="ReceiverID" type="xsd:string"/>
          <xsd:element name="CORERuleVersion" type="xsd:string"/>
          <xsd:element name="Payload" type="xsd:string" minOccurs="0"/>
          <xsd:element name="ErrorCode" type="xsd:string"/>
          <xsd:element name="ErrorMessage" type="xsd:string"/>
        </xsd:sequence></xsd:complexType>
      </xsd:element>
    </xsd:schema>
  </types>
  <message name="RealTimeRequestMessage"><part name="body" element="CORE:COREEnvelopeRealTimeRequest"/></message>
  <message name="RealTimeResponseMessage"><part name="body" element="CORE:COREEnvelopeRealTimeResponse"/></message>
  <portType name="CORETransactions">
    <operation name="RealTimeTransaction">
      <input message="tns:RealTimeRequestMessage"/>
      <output message="tns:RealTimeResponseMessage"/>
    </operation>
  </portType>
  <binding name="CoreSoapBinding" type="tns:CORETransactions">
    <soap12:binding style="document" transport="http://schemas.xmlsoap.org/soap/http"/>
    <operation name="RealTimeTransaction">
      <soap12:operation soapAction="RealTimeTransaction" style="document"/>
      <input><soap12:body use="literal"/></input>
      <output><soap12:body use="literal"/></output>
    </operation>
  </binding>
  <service name="CoreService">
    <port name="CoreSoapPort" binding="tns:CoreSoapBinding">
      <soap12:address location="{SecurityElement.Escape(location)}"/>
    </port>
  </service>
</definitions>
""";
}
