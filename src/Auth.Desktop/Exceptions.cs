namespace Auth.Desktop;

/// <summary>
/// The user has to sign in (again): never signed in, signed out, or Kale ended the session (stopped by an admin,
/// expired, signed in elsewhere in this application, access taken away). Call <see cref="KaleDesktopSignIn.SignInAsync"/>.
/// </summary>
public sealed class KaleSignInRequiredException : Exception
{
    public KaleSignInRequiredException()
        : base("Sign in to Kale first.")
    {
    }

    public KaleSignInRequiredException(string message)
        : base(message)
    {
    }

    public KaleSignInRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A sign-in didn't succeed: Kale refused it (e.g. no access to this application), or its answer was wrong.</summary>
public sealed class KaleSignInException : Exception
{
    public KaleSignInException()
    {
    }

    public KaleSignInException(string message)
        : base(message)
    {
    }

    public KaleSignInException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
