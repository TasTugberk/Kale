using AuthService.Applications;
using AuthService.Grpc;
using AuthService.Hosting;
using AuthService.Persistence;
using AuthService.SignIn;
using AuthService.Tokens;

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
builder.Services.AddAppApi();
builder.Services.AddUserSignIn(builder.Environment);

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

app.MapGet("/", () => "Hello World!");

app.Run();
