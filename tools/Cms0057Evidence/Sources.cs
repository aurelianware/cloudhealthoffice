using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Cms0057Evidence;

/// <summary>Test outcome as recorded in the TRX.</summary>
public enum TestOutcome { Passed, Failed, Skipped }

/// <summary>One scenario/backend association read from a test's [Trait]s.</summary>
public sealed record ScenarioTrait(string TestName, string ScenarioId, string Backend, bool IsGap)
{
    public const string ReplaceBackend = "Replace";
    public const string AugmentBackend = "Augment";
}

/// <summary>A single test result from the TRX.</summary>
public sealed record TestResult(string TestName, TestOutcome Outcome);

public sealed record TestRunSummary(int Passed, int Failed, int Skipped, IReadOnlyList<TestResult> Results)
{
    public int Total => Passed + Failed + Skipped;
}

public sealed class TrxException : Exception
{
    public TrxException(string message) : base(message) { }
}

/// <summary>Parses a VSTest TRX file into per-test outcomes and counts.</summary>
public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestRunSummary Parse(string path)
    {
        if (!File.Exists(path))
            throw new TrxException($"Test result (TRX) file not found: {path}");

        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception ex) { throw new TrxException($"TRX file is malformed: {ex.Message}"); }

        var results = new List<TestResult>();
        int passed = 0, failed = 0, skipped = 0;

        foreach (var r in doc.Descendants(Ns + "UnitTestResult"))
        {
            var name = (string?)r.Attribute("testName") ?? "";
            var outcome = (string?)r.Attribute("outcome") ?? "";
            var mapped = outcome switch
            {
                "Passed" => TestOutcome.Passed,
                "Failed" => TestOutcome.Failed,
                _ => TestOutcome.Skipped, // NotExecuted / Inconclusive / etc.
            };
            switch (mapped)
            {
                case TestOutcome.Passed: passed++; break;
                case TestOutcome.Failed: failed++; break;
                default: skipped++; break;
            }
            results.Add(new TestResult(Normalize(name), mapped));
        }

        if (results.Count == 0)
            throw new TrxException($"TRX file contained no UnitTestResult entries: {path}");

        return new TestRunSummary(passed, failed, skipped, results);
    }

    /// <summary>Strip theory arguments so display names match reflected method names.</summary>
    internal static string Normalize(string testName)
    {
        var paren = testName.IndexOf('(');
        return (paren >= 0 ? testName[..paren] : testName).Trim();
    }
}

/// <summary>
/// Reads <c>[Trait]</c> attribute arguments from a compiled test assembly using
/// <see cref="MetadataLoadContext"/> — metadata only, no code execution.
/// </summary>
public static class TraitReader
{
    public static IReadOnlyList<ScenarioTrait> Read(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Test assembly not found: {assemblyPath}");

        var assemblyDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
            paths.Add(dll);
        foreach (var dll in Directory.GetFiles(assemblyDir, "*.dll"))
            paths.Add(dll);

        using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));
        var asm = mlc.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

        var results = new List<ScenarioTrait>();
        foreach (var type in asm.GetTypes())
        {
            var classTraits = ReadTraits(type.GetCustomAttributesData());
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var methodTraits = ReadTraits(method.GetCustomAttributesData());
                var scenario = FirstValue(methodTraits, "Scenario") ?? FirstValue(classTraits, "Scenario");
                if (scenario is null) continue;

                var backend = FirstValue(methodTraits, "Backend")
                              ?? FirstValue(classTraits, "Backend")
                              ?? ScenarioTrait.ReplaceBackend;
                var isGap = HasValue(methodTraits, "Kind", "GAP") || HasValue(classTraits, "Kind", "GAP");

                results.Add(new ScenarioTrait($"{type.FullName}.{method.Name}", scenario, backend, isGap));
            }
        }
        return results;
    }

    private static List<(string Name, string Value)> ReadTraits(IEnumerable<CustomAttributeData> attrs)
    {
        var list = new List<(string, string)>();
        foreach (var a in attrs)
        {
            if (a.AttributeType.Name != "TraitAttribute" || a.ConstructorArguments.Count != 2) continue;
            if (a.ConstructorArguments[0].Value is string name && a.ConstructorArguments[1].Value is string value)
                list.Add((name, value));
        }
        return list;
    }

    private static string? FirstValue(List<(string Name, string Value)> traits, string name) =>
        traits.FirstOrDefault(t => t.Name == name).Value;

    private static bool HasValue(List<(string Name, string Value)> traits, string name, string value) =>
        traits.Any(t => t.Name == name && string.Equals(t.Value, value, StringComparison.OrdinalIgnoreCase));
}
