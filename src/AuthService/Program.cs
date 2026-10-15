using AuthService.Applications;
using AuthService.Hosting;
using AuthService.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Read eagerly so a missing connection string stops startup instead of failing on the first request.
var authDbConnectionString = builder.Configuration.GetConnectionString(AuthDatabaseOptions.ConnectionStringName);
if (string.IsNullOrWhiteSpace(authDbConnectionString))
{
    throw new InvalidOperationException(
        $"Connection string '{AuthDatabaseOptions.ConnectionStringName}' is not configured.");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AuthDbContext>(options => options.UseAuthDatabase(authDbConnectionString));

builder.Services.AddAuthServer(builder.Configuration, builder.Environment);

builder.Services.AddScoped<ApplicationService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
