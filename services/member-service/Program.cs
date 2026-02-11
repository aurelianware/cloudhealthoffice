using CloudHealthOffice.Infrastructure.DocumentStore;
using Microsoft.Azure.Cosmos;
using MemberService.Middleware;
using MemberService.Models;
using MemberService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Cloud Health Office - Member Service API", 
        Version = "v1",
        Description = "Manages health plan member data (subscribers and dependents) populated by X12 834 Enrollment transactions"
    });
});

// Multi-cloud document store (auto-detects Azure Cosmos DB or MongoDB based on CloudProvider env var)
builder.Services.AddDocumentStore(builder.Configuration);

// Register repositories (backward compatible - uses CosmosClient registered by AddDocumentStore)
builder.Services.AddScoped<IMemberRepository>(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
    return new MemberRepository(cosmosClient, databaseName);
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
