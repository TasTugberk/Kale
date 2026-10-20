using AuthService.Applications;
using AuthService.Grpc;
using AuthService.Hosting;
using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.SignIn;
using AuthService.Tokens;
using AuthService.Users;

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
builder.Services.AddScoped<SessionStopper>();
builder.Services.AddScoped<RoleEditor>();
builder.Services.AddScoped<UserDisabler>();
builder.Services.AddAppApi();
builder.Services.AddUserSignIn(builder.Environment);
builder.Services.AddOutboxPublishing(builder.Configuration, builder.Environment);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseLoginPageLanguages();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthorizeEndpoint();
app.MapTokenEndpoint();
app.MapEndSessionEndpoint();
app.MapRazorPages();
app.MapAppApi();

// For the container healthcheck. The app only finishes starting once its keys load from the database, so
// answering here already means the database was reachable.
app.MapHealthChecks("/health");

app.MapGet("/", () => "Hello World!");

app.Run();
