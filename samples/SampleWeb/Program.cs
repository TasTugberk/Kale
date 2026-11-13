using Auth.Web;
using SampleWeb;

var builder = WebApplication.CreateBuilder(args);

// Signs users in through Kale. Every setting comes from the "Kale" section, as for SampleApp: appsettings, user
// secrets, or in a container the environment plus Kale:ClientSecretFile (a mounted Podman secret).
builder.AddKaleSignIn();

// SampleApp's API, called as the signed-in user. It shares this application's key ("sample"): Kale's tokens are
// made for one audience, the application key.
builder.Services.AddHttpClient<NotesApi>(http => http.BaseAddress = builder.Configuration.GetValue<Uri>("SampleApi"))
    .AddKaleAccessToken();

builder.Services.AddHealthChecks();

var app = builder.Build();

// The front end (wwwroot/index.html stands in for a React build) is public; its script asks /auth/me.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

// GET /auth/login, POST /auth/logout, GET /auth/me.
app.MapKaleSignIn();

// The front end's API: passes SampleApp's answer on (the notes, or 403 when the user's role lacks NoteRead).
app.MapGet("/api/notes", async (NotesApi notes, CancellationToken cancellationToken) =>
{
    using var response = await notes.ListAsync(cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
}).RequireAuthorization();

app.Run();
