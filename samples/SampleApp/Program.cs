using System.Security.Claims;
using Auth.Client;
using SampleApp;

var builder = WebApplication.CreateBuilder(args);

// Every setting from the "Kale" section: appsettings, user secrets (scripts/register-app.sh writes them for the
// local Kale), or in a container the environment plus Kale:ClientSecretFile (a mounted Podman secret).
builder.AddKaleAuth<SampleOperations>();

builder.Services.AddHealthChecks();
builder.Services.AddControllers();
builder.Services.AddSingleton<NoteStore>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// For the container healthcheck; anonymous, like every endpoint without [Authorize].
app.MapHealthChecks("/health");

// Who is calling, and what they may do: for a front end that shows only the buttons the user can use. Any signed-in
// user with an active Kale session (plain RequireAuthorization); KaleAccess asks the same as [Authorize(...)].
app.MapGet("/me", async (ClaimsPrincipal user, KaleAccess<SampleOperations> access, CancellationToken cancellationToken) =>
{
    var operations = await access.GetOperationsAsync(user, cancellationToken);
    return new { sub = user.FindFirstValue("sub"), operations = operations.Select(operation => operation.ToString()) };
}).RequireAuthorization();

app.MapGet("/notes", [Authorize(SampleOperations.NoteRead)] (NoteStore notes) => notes.All());

app.MapPost("/notes", [Authorize(SampleOperations.NoteWrite)] (Note note, ClaimsPrincipal user, NoteStore notes) =>
{
    notes.Add(note, user.FindFirstValue("sub")!);
    return Results.Created("/notes", note);
});

// The same rules on a controller: GET and DELETE /my-notes (MyNotesController.cs).
app.MapControllers();

app.Run();
