using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using SampleApp;

namespace EndToEnd.Tests;

/// <summary>
/// samples/SampleApp, configured the way a deployment configures it, works with AuthService: it registers its
/// operations and lets users do what their role allows.
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class SampleAppTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _auth = null!;
    private CreatedApplication _sample = null!;
    private SampleAppHost _app = null!;

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            _sample = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .CreateAsync(new NewApplication("sample", "Sample", [Browser.RedirectUri("sample")], []));
        }

        _app = new SampleAppHost(_auth, _sample.ClientSecret);
        _app.StartServer();
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => await db.Operations.CountAsync() == 2);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _auth.DisposeAsync();
    }

    [Fact]
    public async Task The_health_endpoint_answers_without_a_token()
    {
        // The container's healthcheck (compose) asks /health.
        using var http = _app.CreateClient();

        using var response = await http.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_health_endpoint_reports_the_registered_operations()
    {
        using var http = _app.CreateClient();

        // A default MapHealthChecks answers only the overall status, which includes Kale's check. The operations
        // can be in Kale's database a moment before SampleApp has read Kale's answer, so wait for it.
        await AuthServiceSteps.WaitUntilAsync(_auth, async _ =>
        {
            using var response = await http.GetAsync(new Uri("/health", UriKind.Relative));
            return await response.Content.ReadAsStringAsync() == "Healthy";
        });
    }

    [Fact]
    public async Task Me_says_who_is_calling_and_what_they_may_do()
    {
        using var http = await SignedInClient("sample.NoteWrite");

        var me = await http.GetFromJsonAsync<JsonElement>(new Uri("/me", UriKind.Relative));

        me.GetProperty("sub").GetString().ShouldNotBeNullOrEmpty();
        // NoteWrite implies NoteRead: Kale expands it.
        me.GetProperty("operations").EnumerateArray().Select(o => o.GetString()).ShouldBe(["NoteRead", "NoteWrite"], ignoreOrder: true);
    }

    [Fact]
    public async Task Me_needs_a_signed_in_user()
    {
        using var http = _app.CreateClient();

        (await http.GetAsync(new Uri("/me", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_user_whose_role_has_no_note_operation_cannot_list_notes()
    {
        using var http = await SignedInClient(operation: null);

        (await http.GetAsync(new Uri("/notes", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_controller_lists_only_the_callers_own_notes()
    {
        using var writer = await SignedInClient("sample.NoteWrite");
        using var otherWriter = await SignedInClient("sample.NoteWrite");
        (await writer.PostAsJsonAsync(new Uri("/notes", UriKind.Relative), new { Text = "mine" })).EnsureSuccessStatusCode();
        (await otherWriter.PostAsJsonAsync(new Uri("/notes", UriKind.Relative), new { Text = "theirs" })).EnsureSuccessStatusCode();

        var mine = await writer.GetFromJsonAsync<JsonElement[]>(new Uri("/my-notes", UriKind.Relative));

        mine!.Select(note => note.GetProperty("text").GetString()).ShouldBe(["mine"]);
    }

    [Fact]
    public async Task The_controllers_action_needs_its_own_operation_too()
    {
        // The class needs NoteRead, the action NoteWrite as well (AND).
        using var reader = await SignedInClient("sample.NoteRead");
        using var writer = await SignedInClient("sample.NoteWrite");
        (await writer.PostAsJsonAsync(new Uri("/notes", UriKind.Relative), new { Text = "to clear" })).EnsureSuccessStatusCode();

        (await reader.DeleteAsync(new Uri("/my-notes", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await writer.DeleteAsync(new Uri("/my-notes", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await writer.GetFromJsonAsync<JsonElement[]>(new Uri("/my-notes", UriKind.Relative)))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_controller_needs_a_token()
    {
        using var http = _app.CreateClient();

        (await http.GetAsync(new Uri("/my-notes", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_reader_can_list_notes_but_not_add_one()
    {
        using var http = await SignedInClient("sample.NoteRead");

        (await http.GetAsync(new Uri("/notes", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PostAsJsonAsync(new Uri("/notes", UriKind.Relative), new { Text = "hello" })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_writer_can_add_a_note_and_read_it_back()
    {
        using var http = await SignedInClient("sample.NoteWrite");

        (await http.PostAsJsonAsync(new Uri("/notes", UriKind.Relative), new { Text = "hello" })).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        var notes = await http.GetFromJsonAsync<List<Note>>(new Uri("/notes", UriKind.Relative));

        notes!.Select(n => n.Text).ShouldBe(["hello"], "NoteWrite implies NoteRead");
    }

    [Fact]
    public async Task A_stopped_session_is_refused_right_away()
    {
        // The sample receives AuthService's events over its gRPC connection, so it doesn't wait for its cached
        // session status to expire.
        var tokens = await AuthServiceSteps.SignInAsync(_auth, _sample, "sample.NoteRead");
        using var http = _app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        using (var first = await http.GetAsync(new Uri("/notes", UriKind.Relative)))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var sessionId = Guid.Parse(new JsonWebToken(tokens.GetProperty("access_token").GetString()).GetClaim("sid").Value);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await http.GetAsync(new Uri("/notes", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The sample kept accepting the stopped session.");
    }

    private async Task<HttpClient> SignedInClient(string? operation)
    {
        var tokens = await AuthServiceSteps.SignInAsync(_auth, _sample, operation);
        var http = _app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        return http;
    }
}
