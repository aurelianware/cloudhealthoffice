using FhirService.Formatters;
using FhirService.Middleware;
using FhirService.Services;

var builder = WebApplication.CreateBuilder(args);

// FHIR data adapter — swap MockFhirDataAdapter for real adapters in Sprint 3
builder.Services.AddSingleton<IFhirDataAdapter, MockFhirDataAdapter>();
builder.Services.AddSingleton<FhirBundleBuilder>();

// Insert FHIR formatters first so they take priority over default System.Text.Json
builder.Services.AddControllers(options =>
{
    options.InputFormatters.Insert(0, new FhirInputFormatter());
    options.OutputFormatters.Insert(0, new FhirOutputFormatter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Exposed for WebApplicationFactory in integration tests
public partial class Program { }
