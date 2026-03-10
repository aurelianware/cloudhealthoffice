using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using RfaiService.Middleware;
using RfaiService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "RFAI Service API",
        Version = "v1",
        Description =
            "Manages Request for Additional Information (RFAI) cases for the " +
            "Availity/Cognizant auth attachment workflow. " +
            "Cases are linked to prior authorizations via the 278 TRN02 auth number."
    });
});

// ── Database ─────────────────────────────────────────────────────────────────

var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(
        _ => new MongoDB.Driver.MongoClient(mongoConnectionString));

    builder.Services.AddScoped<MongoDB.Driver.IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<MongoDB.Driver.IMongoClient>();
        var dbName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(dbName);
    });

    builder.Services.AddScoped<IRfaiRepository, RfaiRepositoryMongo>();
    Console.WriteLine("Using MongoDB database provider");
}
else
{
    var endpoint = builder.Configuration["CosmosDb:Endpoint"]
        ?? throw new InvalidOperationException("CosmosDb:Endpoint must be configured when MongoDb is not used.");
    var key = builder.Configuration["CosmosDb:Key"]
        ?? throw new InvalidOperationException("CosmosDb:Key must be configured when MongoDb is not used.");

    builder.Services.AddSingleton<CosmosClient>(_ =>
        new CosmosClient(endpoint, key));

    builder.Services.AddScoped<IRfaiRepository, RfaiRepositoryCosmos>();
    Console.WriteLine("Using Cosmos DB database provider");
}

// ── Middleware / infra ────────────────────────────────────────────────────────

builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// ── Pipeline ──────────────────────────────────────────────────────────────────

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "RFAI Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseCors("AllowAll");
app.UseTenantMiddleware();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
