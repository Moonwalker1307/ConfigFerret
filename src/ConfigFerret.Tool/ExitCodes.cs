namespace ConfigFerret.Tool;

/// <summary>The command's exit codes.</summary>
internal static class ExitCodes
{
    /// <summary>The trace ran.</summary>
    public const int Success = 0;

    /// <summary>A key passed to trace or container is not set, or unused found something and <c>--exit-code</c> was passed.</summary>
    public const int Findings = 1;

    /// <summary>Bad arguments, or an input that could not be read.</summary>
    public const int Usage = 2;
}
