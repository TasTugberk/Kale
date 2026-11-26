using System.Security.Claims;
using Auth.Client;
using Microsoft.AspNetCore.Mvc;

namespace SampleApp;

/// <summary>
/// The caller's own notes, as a controller (the integration guide's other style): the class needs NoteRead, and the
/// action that deletes needs NoteWrite as well (attributes on the class and the action must all pass).
/// </summary>
[ApiController]
[Route("my-notes")]
[Authorize(SampleOperations.NoteRead)]
public sealed class MyNotesController(NoteStore notes) : ControllerBase
{
    // "sub" is the user's id in Kale: the token carries no name or roles, only who it is (and the session, "sid").
    private string UserId => User.FindFirstValue("sub")!;

    [HttpGet]
    public Note[] List() => notes.WrittenBy(UserId);

    [HttpDelete]
    [Authorize(SampleOperations.NoteWrite)]
    public IActionResult Clear()
    {
        notes.RemoveWrittenBy(UserId);
        return NoContent();
    }
}
