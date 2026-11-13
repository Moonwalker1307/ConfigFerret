namespace ConfigFerret.Tool;

/// <summary>A problem with the command's arguments or input files, reported as one line without a stack trace.</summary>
public sealed class UsageException : Exception
{
    public UsageException()
    {
    }

    public UsageException(string message)
        : base(message)
    {
    }

    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
