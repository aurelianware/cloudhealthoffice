// Spike harness: runs CMS125 (QI-Core 2025) ELM through the Firely CQL SDK.
// Usage: dotnet CqlEngineSpike.dll <measure-bundle.json> <elm-dir> tests
//        dotnet CqlEngineSpike.dll <measure-bundle.json> <elm-dir> scale <copies> [parallelism]
using System.Collections.Concurrent;
using System.Diagnostics;
using Hl7.Cql.CodeGeneration.NET.Toolkit;
using Hl7.Cql.CodeGeneration.NET.Toolkit.Extensions;
using Hl7.Cql.Fhir;
using Hl7.Cql.Invocation.Toolkit.Extensions;
using Hl7.Cql.Primitives;
using Hl7.Cql.Runtime;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

var bundlePath = args[0];
var elmDir = args[1];
var mode = args[2];

var parser = new FhirJsonDeserializer();
var serializer = new FhirJsonSerializer();
var source = parser.Deserialize<Bundle>(File.ReadAllText(bundlePath));

var expected = new List<MeasureReport>();
var valueSetResources = new List<ValueSet>();
var data = new List<Resource>();
foreach (var e in source.Entry)
{
    switch (e.Resource)
    {
        case MeasureReport mr: expected.Add(mr); break;
        case ValueSet vs: valueSetResources.Add(vs); break;
        case Measure or Library: break;
        default: data.Add(e.Resource); break;
    }
}
// The published bundle carries a partial VSAC expansion (AdvancedIllness: 1000 of 1997). Firely refuses
// partial expansions; the Java engine silently uses them. To compare like with like, ALLOW_PARTIAL=1 treats
// the partial expansion as complete, which is exactly what the Java engine does.
var partial = valueSetResources.Where(v => v.Expansion?.Total is int t && t != v.Expansion.Contains.Count).ToList();
foreach (var v in partial) Console.WriteLine($"partial value set: {v.Url} {v.Expansion.Contains.Count} of {v.Expansion.Total}");
if (Environment.GetEnvironmentVariable("ALLOW_PARTIAL") == "1")
    foreach (var v in partial) v.Expansion.Total = v.Expansion.Contains.Count;
var valueSets = valueSetResources.ToValueSetDictionary();

var sw = Stopwatch.StartNew();
var elmToolkit = new ElmToolkit().AddElmFilesFromDirectory(new DirectoryInfo(elmDir));
using var invoker = elmToolkit.CreateLibrarySetInvoker();
Console.WriteLine($"firely: compiled ELM to assemblies in {sw.ElapsedMilliseconds} ms");

var library = CqlVersionedLibraryIdentifier.ParseFromIdentifierAndVersion(Environment.GetEnvironmentVariable("LIB") ?? "CMS125FHIRBreastCancerScreen", Environment.GetEnvironmentVariable("LIBVER") ?? "1.0.000");
var populations = new (string Code, string Define)[]
{
    ("initial-population", "Initial Population"),
    ("denominator", "Denominator"),
    ("denominator-exclusion", "Denominator Exclusions"),
    ("numerator", "Numerator"),
};

var patientIds = data.OfType<Patient>().Select(p => p.Id).ToList();
var byPatient = new Dictionary<string, List<string>>();
foreach (var r in data)
{
    var json = serializer.SerializeToString(r);
    var pid = patientIds.FirstOrDefault(json.Contains);
    if (pid is null) continue;
    if (!byPatient.TryGetValue(pid, out var list)) byPatient[pid] = list = new List<string>();
    list.Add(json);
}

IDictionary<string, object> Period(int year) => new Dictionary<string, object>
{
    ["Measurement Period"] = new CqlInterval<CqlDateTime>(
        new CqlDateTime(year, 1, 1, 0, 0, 0, 0, 0, 0),
        new CqlDateTime(year, 12, 31, 23, 59, 59, 999, 0, 0), true, true)
};

Dictionary<string, int> Evaluate(Bundle b, IDictionary<string, object> parameters)
{
    var ctx = FhirCqlContext.ForBundle(b, parameters, valueSets);
    var result = new Dictionary<string, int>();
    foreach (var (code, define) in populations)
    {
        var v = invoker.InvokeLibraryDefinition(ctx, library, define);
        result[code] = v is true ? 1 : 0;
    }
    return result;
}

Bundle BundleOf(IEnumerable<string> jsons)
{
    var b = new Bundle { Type = Bundle.BundleType.Collection };
    foreach (var j in jsons) b.Entry.Add(new Bundle.EntryComponent { Resource = parser.Deserialize<Resource>(j) });
    return b;
}

if (mode == "tests")
{
    int pass = 0, fail = 0;
    sw.Restart();
    foreach (var exp in expected)
    {
        var subject = exp.Contained.OfType<Parameters>().SelectMany(p => p.Parameter)
            .First(p => p.Name == "subject").Value.ToString()!;
        var desc = exp.GetExtensionValue<Markdown>("http://hl7.org/fhir/us/cqfmeasures/StructureDefinition/cqfm-testCaseDescription")?.Value ?? "";
        var year = int.Parse(exp.Period.Start[..4]);
        try
        {
            var got = Evaluate(BundleOf(byPatient[subject]), Period(year));
            var diffs = new List<string>();
            foreach (var p in exp.Group[0].Population)
            {
                var code = p.Code.Coding[0].Code;
                var g = got.TryGetValue(code, out var c) ? c : -1;
                if (g != p.Count) diffs.Add($"{code} expected {p.Count} got {g}");
            }
            if (diffs.Count == 0) pass++;
            else { fail++; Console.WriteLine($"FAIL {subject} [{desc}] {string.Join(", ", diffs)}"); }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine($"ERROR {subject} [{desc}] {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"firely tests: {pass} pass, {fail} fail of {expected.Count} in {sw.ElapsedMilliseconds} ms");
}
else
{
    int copies = int.Parse(args[3]);
    int parallelism = args.Length > 4 ? int.Parse(args[4]) : 1;
    // Members are built lazily, one bundle per synthetic member (parse included in the timing), the way a
    // CHO batch worker would read one member's data, evaluate it and drop it.
    IEnumerable<Bundle> Members()
    {
        for (int c = 0; c < copies; c++)
            foreach (var (pid, jsons) in byPatient)
            {
                var newPid = pid[..24] + c.ToString("D12");
                yield return BundleOf(jsons.Select(j => j.Replace(pid, newPid)));
            }
    }
    var totals = new ConcurrentDictionary<string, int>();
    var mp = Period(2026);
    long count = 0;
    sw.Restart();
    Parallel.ForEach(Members(), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, b =>
    {
        foreach (var (k, v) in Evaluate(b, mp)) totals.AddOrUpdate(k, v, (_, o) => o + v);
        Interlocked.Increment(ref count);
    });
    var evalMs = sw.ElapsedMilliseconds;
    var mb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
    Console.WriteLine($"firely scale: {count} members, parallelism {parallelism}, parse+evaluate {evalMs} ms " +
                      $"({(double)evalMs / count:F3} ms/member), peak working set {mb} MB, " +
                      string.Join(" ", totals.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}")));
}
