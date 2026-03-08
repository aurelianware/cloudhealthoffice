using MongoDB.Driver;
using RfaiService.Middleware;
using RfaiService.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Cloud Health Office - RFAI Service API",
        Version = "v1",
        Description = "Manages RFAI (Request for Additional Information) cases. " +
                      "Tracks open requests for clinical attachments required by prior-auth and claims adjudication. " +
                      "Receives attachment notifications from attachment-service (inbound 275 processing)."
    });
});

// ── Database Configuration ────────────────────────────────────────────────────
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddSingleton<IMongoClient>(sp =>
        new MongoClient(mongoConnectionString));

    builder.Services.AddScoped<IMongoDatabase>(sp =>
    {
        var client = sp.GetRequiredService<IMongoClient>();
        var databaseName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        return client.GetDatabase(databaseName);
    });

    builder.Services.AddScoped<IRfaiRepository, RfaiRepositoryMongo>();
}
else
{
    throw new InvalidOperationException(
        "MongoDb:ConnectionString must be configured. " +
        "Set the MongoDb__ConnectionString environment variable.");
}

// ── Infrastructure ────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "RFAI Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseHttpsRedirection();
app.UseTenantMiddleware();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
