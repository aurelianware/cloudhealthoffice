using System.Net.Http.Headers;
using DemoPersonaSeeder;

SeederOptions options;
try
{
    options = SeederOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(SeederOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(SeederOptions.Usage);
    return 0;
}

if (options.Validate() is { } problem)
{
    Console.Error.WriteLine(problem);
    return 2;
}

var data = PersonaCatalog.Build(options.AsOf);
Console.WriteLine($"Demo personas for tenant {options.TenantId}, as of {options.AsOf:yyyy-MM-dd}");

SeedRun? run = null;
if (options.DryRun)
{
    Console.WriteLine("Dry run: no service calls.");
}
else
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Add("X-Tenant-ID", options.TenantId);
    if (!string.IsNullOrWhiteSpace(options.BearerToken))
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken.Trim());
    else
        Console.WriteLine("No CHO_BEARER_TOKEN set: calls carry only X-Tenant-ID, which works only where services allow it.");

    run = await new Seeder(http, options, data).RunAsync();
}

await File.WriteAllTextAsync(options.OutputPath, DemoScript.Render(data, options.TenantId, run));
Console.WriteLine($"Demo script written to {Path.GetFullPath(options.OutputPath)}");

if (run is { HasFailures: true })
{
    Console.Error.WriteLine($"{run.Steps.Count(s => s.Outcome == StepOutcome.Failed)} step(s) failed; see the script's last section.");
    return 1;
}
return 0;
