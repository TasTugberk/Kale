using Auth.Client;

namespace SampleApp;

/// <summary>What this app lets users do. Registered in AuthService as "sample.NoteRead" and "sample.NoteWrite".</summary>
public enum SampleOperations
{
    NoteRead,
    [Implies(NoteRead)] NoteWrite,
}
