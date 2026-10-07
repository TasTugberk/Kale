using AuthService.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AuthDbContext>(options => options.UseAuthDatabase(
    builder.Configuration.GetConnectionString(AuthDatabaseOptions.ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"Connection string '{AuthDatabaseOptions.ConnectionStringName}' is not configured.")));

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
