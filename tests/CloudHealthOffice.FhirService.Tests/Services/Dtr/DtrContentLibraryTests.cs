using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudHealthOffice.Testing.FhirService;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Dtr;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services.Dtr;

/// <summary>
/// The DTR content CHO ships (src/services/fhir-service/DtrContent/) holds
/// together: the ELM is the compile of the CQL beside it, every CQL expression a
/// questionnaire uses names a define that exists, carries its ELM, and every
/// dependency a package will need is shipped. CHO never runs this CQL (Q-6), so
/// these checks are what stands between an edit and a form whose prepopulation
/// silently does nothing in a provider's DTR client.
/// </summary>
public class DtrContentLibraryTests
{
    private const string AlternativeExpression =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/alternativeExpression";

    private static readonly DtrContentLibrary Content = DtrContentLibrary.Default;

    private static string ContentDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "services", "fhir-service", "DtrContent");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/services/fhir-service/DtrContent not found above the test output.");
    }

    [Fact]
    public void Content_is_shipped_in_the_assembly()
    {
        Content.Questionnaires.Should().Contain(q => q.Id == "q-imaging-mri");
        Content.Libraries.Should().NotBeEmpty();
        Content.ValueSets.Should().NotBeEmpty();
    }

    [Fact]
    public void Every_cql_file_matches_the_hash_it_was_compiled_from()
    {
        var dir = ContentDirectory();
        var cqlFiles = Directory.GetFiles(dir, "*.cql");
        cqlFiles.Should().NotBeEmpty();

        foreach (var cql in cqlFiles)
        {
            var name = Path.GetFileNameWithoutExtension(cql);
            var sidecar = Path.Combine(dir, name + ".cql.sha256");
            File.Exists(sidecar).Should().BeTrue($"{name}.cql needs its compile hash; run scripts/dtr/compile-cql.sh");
            File.Exists(Path.Combine(dir, name + ".elm.json")).Should().BeTrue();

            // Hash the bytes git checks out (LF), as sha256sum does in the script.
            var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(cql).Replace("\r\n", "\n"));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.ReadAllText(sidecar).Trim().Should().Be(hash,
                $"{name}.cql changed after its ELM was compiled; run scripts/dtr/compile-cql.sh");
        }
    }

    [Fact]
    public void Every_library_carries_cql_and_elm_for_the_same_name_and_version()
    {
        foreach (var library in Content.Libraries)
        {
            library.Url.Should().NotBeNullOrEmpty();
            library.Version.Should().NotBeNullOrEmpty();
            library.Status.Should().Be(PublicationStatus.Active);

            var cql = Text(library, "text/cql");
            cql.Should().Contain($"library {library.Name} version '{library.Version}'");

            using var elm = JsonDocument.Parse(Text(library, "application/elm+json"));
            var identifier = elm.RootElement.GetProperty("library").GetProperty("identifier");
            identifier.GetProperty("id").GetString().Should().Be(library.Name);
            identifier.GetProperty("version").GetString().Should().Be(library.Version);

            // The translator records errors as annotations; a clean compile has none.
            if (elm.RootElement.GetProperty("library").TryGetProperty("annotation", out var annotations))
            {
                annotations.EnumerateArray()
                    .Where(a => a.TryGetProperty("errorSeverity", out _))
                    .Should().BeEmpty($"{library.Name} compiled with errors or warnings");
            }
        }
    }

    [Fact]
    public void Every_cql_expression_in_a_questionnaire_names_a_define_in_a_library_it_declares()
    {
        foreach (var questionnaire in Content.Questionnaires)
        {
            var libraries = questionnaire.Extension
                .Where(e => e.Url == QuestionnairePackageBuilder.CqfLibraryExtension)
                .Select(e => Content.ResolveLibrary(((Canonical)e.Value).Value))
                .ToList();
            libraries.Should().NotContainNulls($"{questionnaire.Url} names a Library that is not shipped");

            var defines = libraries.ToDictionary(l => l!.Name, l => Defines(l!));

            foreach (var (linkId, expression) in Expressions(questionnaire.Item))
            {
                expression.Language.Should().Be("text/cql-identifier", $"item {linkId}: DTR spec-86");
                var match = Regex.Match(expression.Expression_, "^\"(?<lib>[^\"]+)\"\\.(?<def>[A-Za-z][A-Za-z0-9_]*)$");
                match.Success.Should().BeTrue($"item {linkId}: expression must be \"Library\".Define (spec-97)");

                var lib = match.Groups["lib"].Value;
                var def = match.Groups["def"].Value;
                defines.Should().ContainKey(lib, $"item {linkId} names library {lib}, which the questionnaire does not declare");
                defines[lib].Should().Contain(def, $"item {linkId}: {lib} has no define {def}");

                // spec-96: the CQL is accompanied by its compiled ELM.
                var elm = expression.Extension.Should().ContainSingle(e => e.Url == AlternativeExpression)
                    .Which.Value.Should().BeOfType<Expression>().Subject;
                elm.Language.Should().Be("application/elm+json");
                using var json = JsonDocument.Parse(elm.Expression_);
                json.RootElement.GetProperty("type").GetString().Should().Be("ExpressionRef");
                json.RootElement.GetProperty("libraryName").GetString().Should().Be(lib);
                json.RootElement.GetProperty("name").GetString().Should().Be(def);
            }
        }
    }

    [Fact]
    public void Every_library_dependency_is_shipped_and_version_specific()
    {
        foreach (var library in Content.Libraries)
        {
            foreach (var related in library.RelatedArtifact.Where(r => r.Type == RelatedArtifact.RelatedArtifactType.DependsOn))
            {
                related.Resource.Should().Contain("|", "oper-16: dependencies are version specific");
                var resolved = related.Resource.Contains("/ValueSet/")
                    ? (Resource?)Content.ResolveValueSet(related.Resource)
                    : Content.ResolveLibrary(related.Resource);
                resolved.Should().NotBeNull($"{library.Name} depends on {related.Resource}, which is not shipped");
            }
        }
    }

    [Fact]
    public void The_value_sets_the_cql_declares_are_the_ones_the_library_depends_on()
    {
        foreach (var library in Content.Libraries)
        {
            var declared = Regex.Matches(Text(library, "text/cql"), "valueset \"[^\"]+\": '(?<url>[^']+)' version '(?<v>[^']+)'")
                .Select(m => $"{m.Groups["url"].Value}|{m.Groups["v"].Value}");
            library.RelatedArtifact.Where(r => r.Resource.Contains("/ValueSet/")).Select(r => r.Resource)
                .Should().BeEquivalentTo(declared);
        }
    }

    [Fact]
    public void Shipped_questionnaires_meet_the_dtr_standard_questionnaire_basics()
    {
        foreach (var q in Content.Questionnaires)
        {
            q.Meta.Profile.Should().Contain("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire");
            q.Url.Should().NotBeNullOrEmpty();
            q.Version.Should().NotBeNullOrEmpty();
            q.SubjectType.Should().Equal(ResourceType.Patient);
            q.Text.Should().NotBeNull();
            int.TryParse(q.Meta.VersionId, out _).Should().BeTrue("seed upgrades compare meta.versionId numerically");
            q.UseContext.Should().NotBeEmpty("orders select a form by its focus use context");
        }
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_newer_shipped_form_replaces_the_older_copy_already_seeded()
    {
        var store = new InMemoryDtrResourceStore();
        store.SeedDefaultQuestionnaire(new Questionnaire
        {
            Id = "q-imaging-mri",
            Meta = new Meta { VersionId = "1" },
            Title = "old MRI form",
            Status = PublicationStatus.Active,
        });

        _ = new DtrService(Options.Create(new DtrConfig()), store, NullLogger<DtrService>.Instance);

        var seeded = (await store.FindQuestionnaireAsync(DtrService.DefaultLibraryTenant, "q-imaging-mri"))!;
        seeded.Url.Should().Be("https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri");
    }

    [Fact]
    public async Task An_older_shipped_form_never_replaces_a_newer_copy()
    {
        var store = new InMemoryDtrResourceStore();
        store.SeedDefaultQuestionnaire(new Questionnaire
        {
            Id = "q-imaging-mri",
            Meta = new Meta { VersionId = "99" },
            Title = "future MRI form",
            Status = PublicationStatus.Active,
        });

        _ = new DtrService(Options.Create(new DtrConfig()), store, NullLogger<DtrService>.Instance);

        (await store.FindQuestionnaireAsync(DtrService.DefaultLibraryTenant, "q-imaging-mri"))!
            .Title.Should().Be("future MRI form");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Text(Library library, string contentType)
        => Encoding.UTF8.GetString(library.Content.Single(c => c.ContentType == contentType).Data);

    private static HashSet<string> Defines(Library library)
    {
        using var elm = JsonDocument.Parse(Text(library, "application/elm+json"));
        return elm.RootElement.GetProperty("library").GetProperty("statements").GetProperty("def")
            .EnumerateArray().Select(d => d.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string LinkId, Expression Expression)> Expressions(IEnumerable<Questionnaire.ItemComponent> items)
    {
        foreach (var item in items)
        {
            foreach (var ext in item.Extension.Where(e => e.Value is Expression))
                yield return (item.LinkId, (Expression)ext.Value);
            foreach (var nested in Expressions(item.Item))
                yield return nested;
        }
    }
}
