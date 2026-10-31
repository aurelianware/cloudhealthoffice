namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A deployed CHO that the conformance kits judge in place of the local
/// <c>interop-cho</c> stack: a plan's own installation, or a tenant Aurelianware
/// hosts, loaded with the plan's own data (for example by the CPCDS importer).
///
/// <para>
/// This is how a plan answers "do my CMS-0057-F APIs pass, with my members and
/// my claims?" rather than "does CHO pass with its seed?". The kit, its pin,
/// the groups each scenario runs and the known kit issues are the same as the
/// local run, so the two verdicts are comparable. What changes:
/// </para>
/// <list type="bullet">
///   <item>Only the kit is started. CHO is already running somewhere else.</item>
///   <item>Nothing is seeded. Writing synthetic members into a plan's
///   installation would put fake people in its Patient Access API; the run
///   judges the data the plan loaded.</item>
///   <item>The kit is given the deployment's address, the plan's own patient
///   ids and an access token the plan issued, because the harness holds no
///   admin credential there and should not.</item>
/// </list>
///
/// <para>
/// Only the scenarios that take a bearer token or need no token run in this
/// mode: US Core and CARIN BB (Patient Access) and Plan-Net (the public
/// Provider Directory). Bulk Data and PDex authenticate themselves with SMART
/// Backend Services, which needs the kit's key registered as a client of the
/// deployment; that is a separate, deliberate step and is refused here rather
/// than attempted with credentials the harness does not have.
/// </para>
///
/// <para>
/// Remote mode is on when <see cref="UrlVariable"/> is set. The values are read
/// from the environment so a token never appears in a command line, a test
/// filter or the evidence.
/// </para>
/// </summary>
public sealed record ChoRemoteTarget
{
    /// <summary>The deployment's FHIR base, e.g. <c>https://api.plan.example/fhir/r4</c>. Turns remote mode on.</summary>
    public const string UrlVariable = "CHO_INTEROP_TARGET_URL";

    /// <summary>The public Provider Directory base, for Plan-Net.</summary>
    public const string DirectoryUrlVariable = "CHO_INTEROP_TARGET_DIRECTORY_URL";

    /// <summary>Comma-separated FHIR Patient ids from the plan's own data.</summary>
    public const string PatientIdsVariable = "CHO_INTEROP_TARGET_PATIENT_IDS";

    /// <summary>An access token the deployment issued that can read those patients.</summary>
    public const string TokenVariable = "CHO_INTEROP_TARGET_TOKEN";

    /// <summary>A name for the deployment in the evidence (the plan or tenant). Never a secret.</summary>
    public const string LabelVariable = "CHO_INTEROP_TARGET_LABEL";

    /// <summary>The auth modes a remote run can satisfy without registering anything at the deployment.</summary>
    public static readonly IReadOnlySet<string> SupportedAuthModes =
        new HashSet<string>(StringComparer.Ordinal) { InfernoAuthModes.ChoToken, InfernoAuthModes.PatientApp };

    public required string Label { get; init; }
    public required Uri FhirBaseUrl { get; init; }
    public Uri? DirectoryUrl { get; init; }
    public IReadOnlyList<string> PatientIds { get; init; } = [];

    /// <summary>Never written to evidence; <see cref="ToString"/> leaves it out.</summary>
    public string? AccessToken { get; init; }

    /// <summary>The remote target the environment names, or null for the local stack.</summary>
    public static ChoRemoteTarget? FromEnvironment()
        => FromValues(Environment.GetEnvironmentVariable);

    /// <summary>Reads a remote target through <paramref name="read"/>; null when <see cref="UrlVariable"/> is unset.</summary>
    public static ChoRemoteTarget? FromValues(Func<string, string?> read)
    {
        var url = read(UrlVariable)?.Trim();
        if (string.IsNullOrEmpty(url))
            return null;

        var fhir = AbsoluteHttp(UrlVariable, url);
        var directory = read(DirectoryUrlVariable)?.Trim();
        var label = read(LabelVariable)?.Trim();

        return new ChoRemoteTarget
        {
            Label = string.IsNullOrEmpty(label) ? fhir.Host : label,
            FhirBaseUrl = fhir,
            DirectoryUrl = string.IsNullOrEmpty(directory) ? null : AbsoluteHttp(DirectoryUrlVariable, directory),
            PatientIds = (read(PatientIdsVariable) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            AccessToken = string.IsNullOrWhiteSpace(read(TokenVariable)) ? null : read(TokenVariable)!.Trim(),
        };
    }

    /// <summary>
    /// Refuses a scenario this mode cannot run honestly, naming what is
    /// missing, before any container is started.
    /// </summary>
    public void EnsureCanRun(InteropScenarioDefinition scenario)
    {
        if (!SupportedAuthModes.Contains(scenario.InfernoAuth))
        {
            throw new InteropEnvironmentException(
                $"'{scenario.Id}' authenticates with '{scenario.InfernoAuth}', which a run against a deployed CHO " +
                "does not support yet: the kit would have to be registered as a SMART Backend Services client of " +
                $"'{Label}' first. Run US Core, CARIN BB and Plan-Net against a deployment; run this one locally.");
        }

        if (scenario.ChoSurface == ChoSurfaces.PublicDirectory && DirectoryUrl is null)
        {
            throw new InteropEnvironmentException(
                $"'{scenario.Id}' judges the public Provider Directory. Set {DirectoryUrlVariable} to its base " +
                $"(on CHO, {{fhir base}}/directory/{{directory id}}).");
        }

        foreach (var input in scenario.InfernoInputs)
        {
            if (input == "patient_ids" && PatientIds.Count == 0)
            {
                throw new InteropEnvironmentException(
                    $"'{scenario.Id}' reads patients. Set {PatientIdsVariable} to one or more FHIR Patient ids " +
                    "from the plan's own data: the run seeds nothing, so there is no default member to read.");
            }

            if (input == "smart_auth_info" && AccessToken is null)
            {
                throw new InteropEnvironmentException(
                    $"'{scenario.Id}' needs an access token. Set {TokenVariable} to a token '{Label}' issued that " +
                    $"can read the patients in {PatientIdsVariable}.");
            }
        }
    }

    /// <summary>The base the kit is given for <paramref name="surface"/>.</summary>
    public string BaseUrl(string surface) => surface switch
    {
        ChoSurfaces.Fhir => Trim(FhirBaseUrl),
        ChoSurfaces.PublicDirectory => Trim(DirectoryUrl ?? throw new InteropEnvironmentException(
            $"No public Provider Directory was given; set {DirectoryUrlVariable}.")),
        _ => throw new InteropEnvironmentException($"Unknown CHO surface '{surface}' in interop/scenarios.json."),
    };

    /// <summary>
    /// The suite inputs for a run against this deployment, by the names the
    /// scenario declares. The same contract as
    /// <see cref="InfernoSuiteScenario.BuildInputs"/>: an input it does not know
    /// is refused, never left unset.
    /// </summary>
    public List<InfernoInput> BuildInputs(InteropScenarioDefinition scenario)
    {
        EnsureCanRun(scenario);
        return scenario.InfernoInputs.Select(name => name switch
        {
            "url" => new InfernoInput { Name = name, Value = BaseUrl(scenario.ChoSurface) },
            "patient_ids" => new InfernoInput { Name = name, Value = string.Join(',', PatientIds) },
            "smart_auth_info" => InfernoInput.AccessToken(name, AccessToken!),
            _ => throw new InteropEnvironmentException(
                $"Scenario '{scenario.Id}' declares an input '{name}' that a run against a deployed CHO does not " +
                "know how to supply."),
        }).ToList();
    }

    /// <summary>What the evidence says about the target: never the token, never patient ids.</summary>
    public string Describe()
        => $"'{Label}' at {Trim(FhirBaseUrl)}" +
           (DirectoryUrl is null ? string.Empty : $", public directory {Trim(DirectoryUrl)}") +
           $", {PatientIds.Count} patient id(s), " +
           (AccessToken is null ? "no access token" : "an access token the deployment issued");

    public override string ToString() => Describe();

    private static Uri AbsoluteHttp(string variable, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InteropEnvironmentException($"{variable} must be an absolute http(s) URL; got '{value}'.");
        }

        // The kit runs in a container, where loopback is the container itself:
        // the same mistake ChoUnderTest exists to prevent.
        if (uri.IsLoopback)
        {
            throw new InteropEnvironmentException(
                $"{variable} is a loopback address ('{value}'), which inside the kit's container is the kit " +
                "itself. Give an address the container can reach (for a CHO on this machine, " +
                "host.docker.internal), or run the local stack with scripts/interop/run.sh instead.");
        }

        return uri;
    }

    private static string Trim(Uri uri) => uri.ToString().TrimEnd('/');
}
