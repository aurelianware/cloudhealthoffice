namespace PlanEditServiceSample;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<SamplePlanEditOptions>(builder.Configuration.GetSection(SamplePlanEditOptions.SectionName));
        builder.Services.AddSingleton(TimeProvider.System);

        var secret = builder.Configuration[$"{SamplePlanEditOptions.SectionName}:SigningSecret"];
        if (string.IsNullOrEmpty(secret) || secret.Length < CloudHealthOffice.PlanEdits.Contracts.PlanEditContract.MinSecretLength)
        {
            throw new InvalidOperationException(
                $"{SamplePlanEditOptions.SectionName}:SigningSecret must be at least " +
                $"{CloudHealthOffice.PlanEdits.Contracts.PlanEditContract.MinSecretLength} characters (from a secret store).");
        }

        var app = builder.Build();
        app.MapSamplePlanEdits();
        app.MapGet("/health", () => Results.Ok());
        app.Run();
    }
}
