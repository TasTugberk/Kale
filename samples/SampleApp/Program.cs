using System.Collections.Concurrent;
using Auth.Client;
using SampleApp;

var builder = WebApplication.CreateBuilder(args);

// Every setting from the "Kale" section: appsettings, user secrets (scripts/register-app.sh writes them for the
// local Kale), or in a container the environment plus Kale:ClientSecretFile / Kale:RabbitMqPasswordFile (mounted
// Podman secrets).
builder.AddKaleAuth<SampleOperations>();

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
