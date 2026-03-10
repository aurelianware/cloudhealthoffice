using Microsoft.Azure.Cosmos;
using Microsoft.OpenApi.Models;
using PaymentService.Middleware;
using PaymentService.Repositories;
using PaymentService.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Payment Service API",
        Version = "v1",
        Description = "835 ERA (Electronic Remittance Advice) payment processing for Cloud Health Office. " +
                     "Handles payment posting, reconciliation, and claim remittance tracking."
    });
});

// Cosmos DB client (singleton)
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["CosmosDb:Endpoint"];
    var key = config["CosmosDb:Key"];

    if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
    {
        throw new InvalidOperationException("CosmosDb:Endpoint and CosmosDb:Key must be configured");
    }

    var options = new CosmosClientOptions
    {
        Serializer = new CosmosSystemTextJsonSerializer()
    };
    return new CosmosClient(endpoint, key, options);
});

// HTTP context accessor (for tenant middleware)
builder.Services.AddHttpContextAccessor();

// Repositories
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentRunRepository, PaymentRunRepository>();

// Services
builder.Services.AddScoped<IPaymentRunService, PaymentRunService>();
builder.Services.AddScoped<IEraGeneratorService, EraGeneratorService>();

// Add HttpClient for claims service integration
builder.Services.AddHttpClient("ClaimsService", client =>
{
    var claimsServiceUrl = builder.Configuration["ClaimsService:BaseUrl"] ?? "http://claims-service:8080";
    client.BaseAddress = new Uri(claimsServiceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Health checks
builder.Services.AddHealthChecks();

// CORS (for development)
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
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Payment Service API v1");
        c.RoutePrefix = string.Empty; // Swagger at root
    });
}

app.UseHttpsRedirection();

// Multi-tenant middleware (extract TenantId from JWT or headers)
app.UseTenantMiddleware();

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
