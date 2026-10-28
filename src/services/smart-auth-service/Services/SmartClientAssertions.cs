using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace SmartAuthService.Services;

/// <summary>
/// SMART Backend Services client authentication (G-42): a backend client
/// proves itself with a JWT signed by a key it registered
/// (<c>client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer</c>),
/// instead of a shared secret.
///
/// OpenIddict validates the assertion's signature against the application's
/// JWK Set and checks <c>iss</c> = <c>sub</c> = <c>client_id</c> and
/// <c>exp</c>. These handlers close the distance between that and the SMART
/// Backend Services profile
/// (https://hl7.org/fhir/smart-app-launch/backend-services.html):
/// <list type="bullet">
///   <item>the JWT <c>typ</c> SMART clients send (<c>JWT</c>, or none) is
///   accepted alongside OpenIddict's <c>client-authentication+jwt</c>;</item>
///   <item>only asymmetric algorithms (RS/ES 256, 384, 512) are accepted;</item>
///   <item>keys come from the client's <c>jwks_uri</c> when it registered one
///   (<see cref="SmartJwksFetcher"/>), else from its registered JWK Set;</item>
///   <item><c>aud</c> may be the token endpoint URL, as SMART requires, as well
///   as the issuer OpenIddict expects;</item>
///   <item><c>jti</c> is required and single use, and <c>exp</c> may be at most
///   five minutes ahead.</item>
/// </list>
/// Every refusal is <c>invalid_client</c>.
/// </summary>
public static class SmartClientAssertions
{
    /// <summary>Signing algorithms accepted on a client assertion. SMART requires RS384 and ES384.</summary>
    public static readonly string[] SigningAlgorithms =
    [
        SecurityAlgorithms.RsaSha384, SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256,
        SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha512,
    ];

    /// <summary>SMART: an assertion's <c>exp</c> is no more than five minutes in the future.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Tolerance for the client's clock on <see cref="MaxLifetime"/>.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    public const int MaxJtiLength = 256;

    /// <summary>Registers the handlers on the OpenIddict server.</summary>
    public static OpenIddictServerBuilder AddSmartClientAssertions(this OpenIddictServerBuilder options)
    {
        options.RemoveEventHandler(OpenIddictServerHandlers.ValidateClientAssertionAudience.Descriptor);
        options.AddEventHandler(PrepareValidation.Descriptor);
        options.AddEventHandler(AcceptJwtType.Descriptor);
        options.AddEventHandler(ValidateAudience.Descriptor);
        options.AddEventHandler(ValidateLifetimeAndReplay.Descriptor);
        return options;
    }

    internal static bool IsClientAssertionOnly(ValidateTokenContext context)
        => context.ValidTokenTypes.Count == 1
           && context.ValidTokenTypes.Contains(TokenTypeIdentifiers.Private.ClientAssertion);

    /// <summary>
    /// Runs after OpenIddict builds the client-assertion validation parameters:
    /// widens the accepted <c>typ</c> to SMART's, pins the algorithms, and
    /// supplies the client's keys (resolved here, asynchronously, from the
    /// unverified <c>iss</c>; OpenIddict later requires <c>iss</c> to be the
    /// authenticated client id).
    /// </summary>
    public sealed class PrepareValidation : IOpenIddictServerHandler<ValidateTokenContext>
    {
        private readonly SmartClientKeyResolver _keys;

        public PrepareValidation(SmartClientKeyResolver keys) => _keys = keys;

        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseScopedHandler<PrepareValidation>()
                .SetOrder(OpenIddictServerHandlers.Protection.ResolveTokenValidationParameters.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(ValidateTokenContext context)
        {
            if (!IsClientAssertionOnly(context) || context.TokenValidationParameters is not { } parameters)
                return;

            parameters.ValidTypes = (parameters.ValidTypes ?? [])
                .Append(JsonWebTokenTypes.GenericJsonWebToken)
                .Append(JsonWebTokenTypes.Prefixes.Application + JsonWebTokenTypes.GenericJsonWebToken)
                .ToArray();
            parameters.ValidAlgorithms = SigningAlgorithms;

            string? issuer = null, kid = null;
            try
            {
                var token = new JsonWebToken(context.Token);
                issuer = token.Issuer;
                kid = string.IsNullOrEmpty(token.Kid) ? null : token.Kid;
            }
            catch (ArgumentException)
            {
                // Not a JWT: validation fails on its own, with no keys to try.
            }

            parameters.IssuerSigningKeyResolver = null;
            parameters.IssuerSigningKeys = string.IsNullOrEmpty(issuer)
                ? []
                : await _keys.ResolveAsync(issuer, kid, context.CancellationToken);
        }
    }

    /// <summary>
    /// OpenIddict types a <c>typ: JWT</c> token as an identity token. Where the
    /// only acceptable type is a client assertion (and the token was just
    /// verified against the client's own keys), it is one.
    /// </summary>
    public sealed class AcceptJwtType : IOpenIddictServerHandler<ValidateTokenContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<AcceptJwtType>()
                .SetOrder(OpenIddictServerHandlers.Protection.ValidateIdentityModelToken.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(ValidateTokenContext context)
        {
            if (IsClientAssertionOnly(context) && context.Principal is { } principal
                && principal.GetTokenType() == TokenTypeIdentifiers.IdentityToken)
            {
                principal.SetTokenType(TokenTypeIdentifiers.Private.ClientAssertion);
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Replaces OpenIddict's audience check, which accepts only the issuer.
    /// SMART clients address the assertion to the token endpoint URL. One
    /// audience, compared as an absolute URI (scheme, host, port and path;
    /// a trailing slash is ignored).
    /// </summary>
    public sealed class ValidateAudience : IOpenIddictServerHandler<ProcessAuthenticationContext>
    {
        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
                .AddFilter<OpenIddictServerHandlerFilters.RequireClientAssertionPrincipal>()
                .UseSingletonHandler<ValidateAudience>()
                .SetOrder(OpenIddictServerHandlers.ValidateClientAssertionAudience.Descriptor.Order)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(ProcessAuthenticationContext context)
        {
            var audiences = context.ClientAssertionPrincipal!.GetClaims(Claims.Audience);
            var issuer = context.Options.Issuer ?? context.BaseUri
                ?? throw new InvalidOperationException("The OpenIddict issuer is not configured.");

            if (audiences is not [var audience]
                || !Uri.TryCreate(audience, UriKind.Absolute, out var uri)
                || !AcceptedAudiences(issuer, context.Options.TokenEndpointUris).Any(a => SameUri(a, uri)))
            {
                context.Reject(
                    error: Errors.InvalidClient,
                    description: "The client assertion's aud must be this server's token endpoint URL.");
            }
            return ValueTask.CompletedTask;
        }

        public static IEnumerable<Uri> AcceptedAudiences(Uri issuer, IEnumerable<Uri> tokenEndpoints)
        {
            yield return issuer;
            var baseUri = new Uri(issuer.AbsoluteUri.EndsWith('/') ? issuer.AbsoluteUri : issuer.AbsoluteUri + "/");
            foreach (var endpoint in tokenEndpoints)
                yield return endpoint.IsAbsoluteUri ? endpoint : new Uri(baseUri, endpoint.OriginalString.TrimStart('/'));
        }

        public static bool SameUri(Uri expected, Uri actual)
            => string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase)
               && expected.Port == actual.Port
               && string.IsNullOrEmpty(actual.Query) && string.IsNullOrEmpty(actual.Fragment)
               && string.Equals(expected.AbsolutePath.TrimEnd('/'), actual.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>jti</c> required and used once per client; <c>exp</c> no more than
    /// five minutes ahead (OpenIddict has already refused an expired one).
    /// </summary>
    public sealed class ValidateLifetimeAndReplay : IOpenIddictServerHandler<ProcessAuthenticationContext>
    {
        private readonly IClientAssertionReplayStore _replay;
        private readonly SmartAuthAudit _audit;
        private readonly TimeProvider _time;

        public ValidateLifetimeAndReplay(IClientAssertionReplayStore replay, SmartAuthAudit audit, TimeProvider time)
        {
            _replay = replay;
            _audit = audit;
            _time = time;
        }

        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
                .AddFilter<OpenIddictServerHandlerFilters.RequireClientAssertionPrincipal>()
                .UseScopedHandler<ValidateLifetimeAndReplay>()
                .SetOrder(OpenIddictServerHandlers.ValidateClientAssertionAudience.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(ProcessAuthenticationContext context)
        {
            var principal = context.ClientAssertionPrincipal!;
            var clientId = principal.GetClaim(Claims.Issuer) ?? string.Empty;
            var refusal = Check(principal, _time.GetUtcNow());
            if (refusal is null)
            {
                var jti = principal.GetClaim(Claims.JwtId)!;
                var keepUntil = ExpirationOf(principal)!.Value + ClockSkew;
                if (!await _replay.TryRecordAsync(clientId, jti, keepUntil, context.CancellationToken))
                    refusal = ("client_assertion_replayed", "The client assertion's jti has already been used.");
            }

            if (refusal is { } r)
            {
                _audit.TokenRefused(null, clientId, r.Reason);
                context.Reject(error: Errors.InvalidClient, description: r.Description);
            }
        }

        public static (string Reason, string Description)? Check(ClaimsPrincipal principal, DateTimeOffset now)
        {
            var jti = principal.GetClaim(Claims.JwtId);
            if (string.IsNullOrEmpty(jti) || jti.Length > MaxJtiLength)
                return ("client_assertion_without_jti", "The client assertion must carry a jti (at most 256 characters).");

            if (ExpirationOf(principal) is not { } exp)
                return ("client_assertion_without_exp", "The client assertion must carry an exp.");
            if (exp - now > MaxLifetime + ClockSkew)
                return ("client_assertion_lifetime_too_long", "The client assertion's exp must be no more than five minutes in the future.");

            return null;
        }

        /// <summary>OpenIddict's mapped expiration, else the assertion's own <c>exp</c>.</summary>
        private static DateTimeOffset? ExpirationOf(ClaimsPrincipal principal)
            => principal.GetExpirationDate()
               ?? (long.TryParse(principal.GetClaim(Claims.ExpiresAt), out var seconds)
                   ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                   : null);
    }
}

/// <summary>Remembers client-assertion <c>jti</c>s until they expire.</summary>
public interface IClientAssertionReplayStore
{
    /// <summary>
    /// Records (client, jti). False when that pair is already recorded and
    /// unexpired, i.e. the assertion is a replay.
    /// </summary>
    Task<bool> TryRecordAsync(string clientId, string jti, DateTimeOffset keepUntil, CancellationToken ct = default);
}

/// <summary>
/// Replay records in MongoDB, shared by every pod: the insert of a unique
/// (client, jti) key is the check, so two concurrent uses of one assertion
/// cannot both succeed. A TTL index removes records once the assertion could
/// no longer be accepted anyway.
/// </summary>
public sealed class MongoClientAssertionReplayStore : IClientAssertionReplayStore
{
    public const string CollectionName = "smart_client_assertion_jtis";

    private readonly IMongoCollection<UsedAssertion> _used;
    private readonly Lazy<Task> _indexes;

    public MongoClientAssertionReplayStore(IMongoDatabase database)
    {
        _used = database.GetCollection<UsedAssertion>(CollectionName);
        _indexes = new Lazy<Task>(() => _used.Indexes.CreateOneAsync(new CreateIndexModel<UsedAssertion>(
            Builders<UsedAssertion>.IndexKeys.Ascending(u => u.ExpiresAt),
            new CreateIndexOptions { Name = "ttl_expires_at", ExpireAfter = TimeSpan.Zero })),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<bool> TryRecordAsync(string clientId, string jti, DateTimeOffset keepUntil, CancellationToken ct = default)
    {
        await _indexes.Value;
        try
        {
            await _used.InsertOneAsync(new UsedAssertion
            {
                Id = clientId + "\n" + jti,
                ExpiresAt = keepUntil.UtcDateTime,
            }, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public sealed class UsedAssertion
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }
}

/// <summary>
/// A backend client's assertion-verification keys: fetched from its
/// registered <c>jwks_uri</c> when it has one, otherwise the JWK Set stored on
/// its OpenIddict application. A client with neither (a secret client, or an
/// unknown id) has no keys, so its assertion fails signature validation.
/// </summary>
public sealed class SmartClientKeyResolver
{
    private readonly ISmartIdentityStore _store;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly SmartJwksFetcher _fetcher;

    public SmartClientKeyResolver(ISmartIdentityStore store, IOpenIddictApplicationManager applications, SmartJwksFetcher fetcher)
    {
        _store = store;
        _applications = applications;
        _fetcher = fetcher;
    }

    public async Task<IReadOnlyList<SecurityKey>> ResolveAsync(string clientId, string? kid, CancellationToken ct)
    {
        var binding = await _store.FindClientAsync(clientId, ct);
        if (binding is null || binding.Kind != Models.SmartClientKind.Backend)
            return [];

        JsonWebKeySet? set;
        if (!string.IsNullOrEmpty(binding.JwksUri))
        {
            set = SmartClientJwks.IsAcceptableJwksUri(binding.JwksUri, out var uri)
                ? await _fetcher.GetAsync(uri, kid, ct)
                : null;
        }
        else
        {
            var application = await _applications.FindByClientIdAsync(clientId, ct);
            set = application is null ? null : await _applications.GetJsonWebKeySetAsync(application, ct);
        }

        return set?.GetSigningKeys().ToList() ?? [];
    }
}
