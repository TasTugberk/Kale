using AuthService.Admin;
using AuthService.Applications;
using AuthService.Groups;
using AuthService.Grpc;
using AuthService.Hosting;
using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.SignIn;
using AuthService.Tokens;
using AuthService.Users;

// `AuthService admin <command> ...` runs one setup command (Admin/AdminCommands.cs) instead of the web server.
// Its arguments aren't configuration, so they don't go to the builder.
var isAdminCommand = args is ["admin", ..];
var builder = WebApplication.CreateBuilder(isAdminCommand ? [] : args);
if (isAdminCommand)
{
    // Standard output carries only the command's result (e.g. a client secret piped into `podman secret
    // create`), so every log line goes to standard error.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
}

// Read eagerly so a missing connection string stops startup instead of failing on the first request.
var authDbConnectionString = AuthDatabaseOptions.ConnectionString(builder.Configuration);

if (args is ["admin", "migrate"])
{
    return await AdminCommands.MigrateAsync(authDbConnectionString, Console.Error);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AuthDbContext>(options => options.UseAuthDatabase(authDbConnectionString));

builder.Services.AddAuthServer(builder.Configuration, builder.Environment);

builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<SessionStopper>();
builder.Services.AddSessionCleanup(builder.Configuration);
builder.Services.AddScoped<RoleEditor>();
builder.Services.AddScoped<UserAccounts>();
builder.Services.AddScoped<UserCreator>();
builder.Services.AddScoped<UserAccess>();
builder.Services.AddScoped<GroupEditor>();
builder.Services.AddAppApi(builder.Configuration, builder.Environment);
builder.Services.AddUserSignIn(builder.Environment);
builder.Services.AddOutboxPublishing(builder.Configuration, builder.Environment);
builder.Services.AddAdminApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (isAdminCommand)
{
    try
    {
        return await AdminCommands.RunAsync(app.Services, args[1..], Console.In, Console.Out, Console.Error);
    }
    finally
    {
        // Writes out log lines the console logger still has queued, even when the command crashed.
        await app.DisposeAsync();
    }
}

app.UseLoginPageLanguages();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthorizeEndpoint();
app.MapTokenEndpoint();
app.MapEndSessionEndpoint();
app.MapRazorPages();
app.MapAppApi();
app.MapAdminApi();

// For the container healthcheck: liveness only. The app finishes starting only once its keys load from the
// database, so a healthy answer means the database was reachable at startup, not that it still is.
app.MapHealthChecks("/health");

app.MapGet("/", () => "Hello World!");

app.Run();
return 0;
