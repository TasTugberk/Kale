using System.ComponentModel.DataAnnotations;

namespace AuthService;

/// <summary>
/// One reason for a refusal: a code from <see cref="RefusalCodes"/> (or "identity.{Identity's code}" for one
/// Identity reports that we don't know), the values its text needs (e.g. a name), and the English text.
/// </summary>
public sealed record RefusalReason(string Code, string Detail, IReadOnlyDictionary<string, string> Values)
{
    public RefusalReason(string code, string detail, params (string Name, string Value)[] values)
        : this(code, detail, values.ToDictionary(value => value.Name, value => value.Value))
    {
    }
}

/// <summary>
/// The domain services' answer to invalid input: one or more reasons, each with a stable code, so the admin UI can
/// show them in the admin's language. The admin API turns it into a 400. It stays a
/// <see cref="ValidationException"/> whose message is the English text, so callers that report those (the admin
/// commands, the account pages) keep doing so.
/// </summary>
public sealed class RefusedException : ValidationException
{
    public RefusedException(string code, string detail, params (string Name, string Value)[] values)
        : this([new RefusalReason(code, detail, values)])
    {
    }

    /// <summary>For a refusal found through a database error (e.g. a unique index), which stays the inner exception.</summary>
    public RefusedException(Exception innerException, string code, string detail, params (string Name, string Value)[] values)
        : base(detail, innerException)
    {
        Reasons = [new RefusalReason(code, detail, values)];
    }

    /// <summary>Several reasons at once (e.g. a new user's name and password), so they can all be fixed in one go.</summary>
    public RefusedException(IReadOnlyList<RefusalReason> reasons)
        : base(string.Join(" ", reasons.Select(reason => reason.Detail)))
    {
        Reasons = reasons;
    }

    public IReadOnlyList<RefusalReason> Reasons { get; }
}
