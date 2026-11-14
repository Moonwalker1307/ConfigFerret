namespace Orders.Api;

public sealed class DbOptions
{
    public const string SectionName = "Db";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    public string Password { get; set; } = string.Empty;
}

public sealed record SmtpOptions
{
    public string? Host { get; init; }

    public string? Password { get; init; }
}
