using Microsoft.Azure.Cosmos;
using CoverageService.Middleware;
using CoverageService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Cloud Health Office - Coverage Service API", 
        Version = "v1",
        Description = "Links Member → Sponsor → Benefit Plan. Populated by X12 834 HD/COB segments. Critical for 270/271 eligibility."
    });
});

builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["CosmosDb:Endpoint"] 
        ?? throw new InvalidOperationException("CosmosDb:Endpoint configuration missing");
    var key = configuration["CosmosDb:Key"] 
        ?? throw new InvalidOperationException("CosmosDb:Key configuration missing");
    
    return new CosmosClient(endpoint, key, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    });
});

builder.Services.AddScoped<ICoverageRepository>(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
    return new CoverageRepository(cosmosClient, databaseName);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseTenantContext();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
