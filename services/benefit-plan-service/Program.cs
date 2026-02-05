using Microsoft.Azure.Cosmos;
using BenefitPlanService.Middleware;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;

var builder = WebApplication.CreateBuilder(args);

// Add Cosmos DB
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["CosmosDb:Endpoint"];
    var key = configuration["CosmosDb:Key"];
    return new CosmosClient(endpoint, key);
});

// Add repositories
builder.Services.AddScoped<IBenefitPlanRepository, BenefitPlanRepository>();

// Add business logic services
builder.Services.AddScoped<IBenefitPlanService, Services.BenefitPlanService>();

// Add controllers
builder.Services.AddControllers();

// Add Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks
builder.Services.AddHealthChecks();

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

app.UseHttpsRedirection();

app.UseCors("AllowAll");

// Add tenant middleware
app.UseMiddleware<TenantMiddleware>();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
