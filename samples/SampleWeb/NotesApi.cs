namespace SampleWeb;

/// <summary>
/// SampleApp's /notes, called as the signed-in user: the HttpClient comes with AddKaleAccessToken (Program.cs),
/// which attaches the user's access token to every request.
/// </summary>
public sealed class NotesApi(HttpClient http)
{
    public Task<HttpResponseMessage> ListAsync(CancellationToken cancellationToken) =>
        http.GetAsync(new Uri("notes", UriKind.Relative), cancellationToken);
}
