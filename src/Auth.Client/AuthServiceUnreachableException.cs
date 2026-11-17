namespace Auth.Client;

/// <summary>
/// AuthService couldn't be asked: no connection, no answer in time, or the call broke off. Unlike an answer
/// (unknown session, refused token), this lets the last known answer be used a little longer
/// (<see cref="SessionAccess.UsableWhileUnreachable"/>).
/// </summary>
public sealed class AuthServiceUnreachableException : Exception
{
    public AuthServiceUnreachableException()
    {
    }

    public AuthServiceUnreachableException(string message)
        : base(message)
    {
    }

    public AuthServiceUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
