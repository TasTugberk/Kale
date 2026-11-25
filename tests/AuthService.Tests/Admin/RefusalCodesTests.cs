using System.Reflection;
using System.Text.Json;

namespace AuthService.Tests.Admin;

/// <summary>
/// The admin UI shows a refusal in the admin's language by its code (admin-ui/messages, section "Refusals"); a code
/// without a text there would fall back to AuthService's English.
/// </summary>
public sealed class RefusalCodesTests
{
    public static TheoryData<string> Codes()
    {
        // Every constant of RefusalCodes: reading them here means a new code can't be forgotten.
        var codes = new TheoryData<string>();
        foreach (var field in typeof(RefusalCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            codes.Add((string)field.GetRawConstantValue()!);
        }

        return codes;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_refusal_code_has_a_text_in_the_admin_UI_in_both_languages(string code)
    {
        foreach (var language in new[] { "tr", "en" })
        {
            TextOf(language, code).ShouldNotBeNullOrWhiteSpace($"admin-ui/messages/{language}.json has no Refusals.{code}");
        }
    }

    [Fact]
    public void The_codes_are_found()
    {
        // The theory above would pass with no codes at all.
        Codes().Count.ShouldBeGreaterThan(10);
    }

    // "role.name-taken" is Refusals → role → name-taken, as next-intl reads a key with dots.
    private static string? TextOf(string language, string code)
    {
        using var messages = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "admin-ui", "messages", $"{language}.json")));
        if (!messages.RootElement.TryGetProperty("Refusals", out var current))
        {
            return null;
        }

        foreach (var part in code.Split('.'))
        {
            if (!current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root (global.json).");
    }
}
