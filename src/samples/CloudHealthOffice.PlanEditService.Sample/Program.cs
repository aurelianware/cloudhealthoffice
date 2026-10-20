namespace PlanEditServiceSample;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<SamplePlanEditOptions>(builder.Configuration.GetSection(SamplePlanEditOptions.SectionName));
        builder.Services.AddSingleton(TimeProvider.System);

        var app = builder.Build();
        app.MapSamplePlanEdits();
        app.MapGet("/health", () => Results.Ok());
        app.Run();
    }
}
