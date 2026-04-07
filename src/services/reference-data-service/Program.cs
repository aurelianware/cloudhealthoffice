using Microsoft.EntityFrameworkCore;
using ReferenceDataService.Repositories;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Resolve PostgreSQL connection string (supports env var substitution)
var postgresConnection = builder.Configuration.GetConnectionString("PostgreSQL") ?? string.Empty;
var postgresPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
if (!string.IsNullOrEmpty(postgresPassword))
{
    postgresConnection = postgresConnection.Replace("${POSTGRES_PASSWORD}", postgresPassword);
}

if (string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException("PostgreSQL connection string is not configured.");
}

// Add PostgreSQL DbContext
builder.Services.AddDbContext<ReferenceDataContext>(options =>
    options.UseNpgsql(postgresConnection));

// Add repositories
builder.Services.AddScoped<IReferenceDataRepository, ReferenceDataRepository>();

// Add memory cache for hot code lookups
builder.Services.AddMemoryCache();

// Add controllers
builder.Services.AddControllers();

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks (PostgreSQL — uses NpgSql check alongside standard CHO checks)
builder.Services.AddChoHealthChecks()
    .AddNpgSql(postgresConnection, name: "postgres", tags: new[] { "ready", "db" }, timeout: TimeSpan.FromSeconds(10));

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapChoHealthChecks();

app.Run();
