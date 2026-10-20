using System.Collections.Concurrent;
using Auth.Client;
using SampleApp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKaleAuth<SampleOperations>(options =>
{
    // Kale:Authority, Kale:ApplicationKey, Kale:AllowInsecureHttpDevOnly, and Kale:ClientSecret when it
    // comes from .NET user secrets (dotnet run).
    builder.Configuration.GetSection("Kale").Bind(options);

    // In a container the secret is a Podman secret: a mounted file, never an environment variable.
    var secretFile = builder.Configuration["Kale:ClientSecretFile"];
    if (!string.IsNullOrEmpty(secretFile))
    {
        options.ClientSecret = File.ReadAllText(secretFile).Trim();
    }
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// For the container healthcheck; anonymous, like every endpoint without [Authorize].
app.MapHealthChecks("/health");

// Kept in memory: the sample only needs something to protect.
var notes = new ConcurrentQueue<Note>();

app.MapGet("/notes", [Authorize(SampleOperations.NoteRead)] () => notes.ToArray());

app.MapPost("/notes", [Authorize(SampleOperations.NoteWrite)] (Note note) =>
{
    notes.Enqueue(note);
    return Results.Created("/notes", note);
});

app.Run();
