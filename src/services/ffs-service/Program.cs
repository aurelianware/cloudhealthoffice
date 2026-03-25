var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();
app.MapControllers();
app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
