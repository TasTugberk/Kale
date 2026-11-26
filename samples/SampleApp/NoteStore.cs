namespace SampleApp;

/// <summary>
/// The notes, kept in memory (the sample only needs something to protect), each with who wrote it: the writer's
/// "sub", their id in Kale.
/// </summary>
public sealed class NoteStore
{
    private readonly Lock _gate = new();
    private readonly List<(Note Note, string AuthorId)> _notes = [];

    public void Add(Note note, string authorId)
    {
        lock (_gate)
        {
            _notes.Add((note, authorId));
        }
    }

    public Note[] All()
    {
        lock (_gate)
        {
            return _notes.Select(entry => entry.Note).ToArray();
        }
    }

    public Note[] WrittenBy(string authorId)
    {
        lock (_gate)
        {
            return _notes.Where(entry => entry.AuthorId == authorId).Select(entry => entry.Note).ToArray();
        }
    }

    public void RemoveWrittenBy(string authorId)
    {
        lock (_gate)
        {
            _notes.RemoveAll(entry => entry.AuthorId == authorId);
        }
    }
}
