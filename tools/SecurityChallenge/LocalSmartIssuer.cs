using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SecurityChallenge;

/// <summary>Ephemeral RSA issuer: serves public JWKS only; never persists keys or tokens.</summary>
internal sealed class LocalSmartIssuer : IAsyncDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly HttpListener _listener = new();
    private readonly Task _serve;
    private readonly string _jwks;
    public string Issuer { get; }
    public LocalSmartIssuer()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Issuer = $"http://127.0.0.1:{port}";
        var key = _rsa.ExportParameters(false);
        _jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "synthetic-rsa", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } });
        _listener.Prefixes.Add(Issuer + "/"); _listener.Start();
        _serve = Serve();
    }
    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { break; }
            context.Response.StatusCode = context.Request.Url?.AbsolutePath == "/jwks" ? 200 : 404;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(context.Response.StatusCode == 200 ? _jwks : "{}");
            await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
        }
    }
    public string Token(string tenant = "synthetic-payer-a", string? patient = "shared-member", string scopes = "patient/Patient.read",
        string? issuer = null, string audience = "fhir-api", int expiresMinutes = 5, int notBeforeMinutes = -10, bool wrongKey = false, bool unsigned = false, string scopeClaim = "scope", string? payerId = null, string? clientId = null)
    {
        using var other = wrongKey ? RSA.Create(2048) : null;
        var claims = new List<Claim> { new("sub", "synthetic-smart-user"), new("tenant_id", tenant), new(scopeClaim, scopes) };
        if (payerId != null) claims.Add(new("payer_id", payerId));
        if (clientId != null) claims.Add(new("client_id", clientId));
        if (patient != null) claims.Add(new("patient", patient));
        var credentials = unsigned ? null : new SigningCredentials(new RsaSecurityKey(other ?? _rsa) { KeyId = "synthetic-rsa" }, SecurityAlgorithms.RsaSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? Issuer, audience, claims,
            DateTime.UtcNow.AddMinutes(notBeforeMinutes), DateTime.UtcNow.AddMinutes(expiresMinutes), credentials));
    }
    public async ValueTask DisposeAsync()
    {
        _listener.Close(); await _serve; _rsa.Dispose();
    }
}
