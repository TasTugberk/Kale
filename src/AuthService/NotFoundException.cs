namespace AuthService;

/// <summary>
/// The domain services' answer to an id that doesn't exist (e.g. deleted a moment ago). The admin API turns it
/// into a 404. It stays an <see cref="InvalidOperationException"/>, so callers that already report those (the
/// admin commands) keep doing so.
/// </summary>
public sealed class NotFoundException(string message) : InvalidOperationException(message);
