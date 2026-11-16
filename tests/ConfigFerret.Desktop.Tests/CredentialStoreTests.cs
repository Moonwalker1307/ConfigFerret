using ConfigFerret.Desktop.Credentials;

namespace ConfigFerret.Desktop.Tests;

public class CredentialStoreTests
{
    private const string Account = "GitHub/token";

    private sealed class ThrowingStore(string displayName = "broken") : ICredentialStore
    {
        public string DisplayName => displayName;

        public string? Read(string account) => throw new CredentialStoreException("No keyring.");

        public void Write(string account, string secret) => throw new CredentialStoreException("No keyring.");

        public void Delete(string account) => throw new CredentialStoreException("No keyring.");
    }

    private static EnvironmentFallbackCredentialStore Fallback(ICredentialStore inner, Dictionary<string, string?> environment, params string[] names) =>
        new(inner, new Dictionary<string, string[]> { [Account] = names }, name => environment.GetValueOrDefault(name));

    [Fact]
    public void The_in_memory_store_keeps_replaces_and_deletes_secrets()
    {
        var store = new InMemoryCredentialStore();

        Assert.Null(store.Read(Account));
        store.Write(Account, "one");
        store.Write(Account, "two");
        store.Write("other", "x");
        Assert.Equal("two", store.Read(Account));
        store.Delete(Account);
        store.Delete("never-saved");
        Assert.Null(store.Read(Account));
        Assert.Equal("x", store.Read("other"));
        Assert.Equal("memory", store.DisplayName);
    }

    [Fact]
    public void The_in_memory_store_tells_accounts_apart_by_case()
    {
        var store = new InMemoryCredentialStore();
        store.Write("Token", "x");

        Assert.Null(store.Read("token"));
    }

    [Fact]
    public void The_credential_store_exception_keeps_its_message_and_cause()
    {
        var cause = new InvalidOperationException("inner");

        Assert.Equal("boom", new CredentialStoreException("boom").Message);
        Assert.Same(cause, new CredentialStoreException("boom", cause).InnerException);
        Assert.NotNull(new CredentialStoreException().Message);
    }

    [Fact]
    public void The_platform_store_is_the_one_for_this_operating_system()
    {
        var store = CredentialStores.ForCurrentPlatform();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsCredentialStore>(store);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacKeychainStore>(store);
        }
        else
        {
            Assert.IsType<SecretServiceStore>(store);
        }

        Assert.Equal("ConfigFerret", CredentialStores.ServiceName);
    }

    [Fact]
    public void Needs_a_store_and_a_list_of_variables()
    {
        Assert.Throws<ArgumentNullException>(() => new EnvironmentFallbackCredentialStore(null!, new Dictionary<string, string[]>()));
        Assert.Throws<ArgumentNullException>(() => new EnvironmentFallbackCredentialStore(new InMemoryCredentialStore(), null!));
    }

    [Fact]
    public void A_saved_secret_wins_over_the_environment()
    {
        var inner = new InMemoryCredentialStore();
        inner.Write(Account, "saved");
        var store = Fallback(inner, new() { ["GITHUB_TOKEN"] = "from-env" }, "GITHUB_TOKEN");

        Assert.Equal("saved", store.Read(Account));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_saved_secret_falls_back_to_the_environment(string? saved)
    {
        var inner = new InMemoryCredentialStore();
        if (saved is not null)
        {
            inner.Write(Account, saved);
        }

        var store = Fallback(inner, new() { ["GITHUB_TOKEN"] = "from-env" }, "GITHUB_TOKEN");

        Assert.Equal("from-env", store.Read(Account));
    }

    [Fact]
    public void Says_whether_the_secret_came_from_the_environment()
    {
        var inner = new InMemoryCredentialStore();
        var store = Fallback(inner, new() { ["GITHUB_TOKEN"] = "from-env" }, "GITHUB_TOKEN");

        Assert.Equal("from-env", store.Read(Account, out var fromEnvironment));
        Assert.True(fromEnvironment);
        inner.Write(Account, "saved");
        Assert.Equal("saved", store.Read(Account, out fromEnvironment));
        Assert.False(fromEnvironment);
        Assert.Null(Fallback(new InMemoryCredentialStore(), [], "GITHUB_TOKEN").Read(Account, out fromEnvironment));
        Assert.False(fromEnvironment);
    }

    [Fact]
    public void Nothing_saved_and_nothing_in_the_environment_gives_null()
    {
        var store = Fallback(new InMemoryCredentialStore(), [], "GITHUB_TOKEN");

        Assert.Null(store.Read(Account));
    }

    [Fact]
    public void An_account_without_variables_gives_null()
    {
        var store = new EnvironmentFallbackCredentialStore(new InMemoryCredentialStore(), new Dictionary<string, string[]>(), _ => "x");

        Assert.Null(store.Read(Account));
    }

    [Fact]
    public void Variables_are_tried_in_order_and_blank_ones_are_skipped()
    {
        var environment = new Dictionary<string, string?> { ["A"] = null, ["B"] = "  ", ["C"] = "  third \n", ["D"] = "fourth" };
        var store = Fallback(new InMemoryCredentialStore(), environment, "A", "B", "C", "D");

        Assert.Equal("third", store.Read(Account));
    }

    [Fact]
    public void Variables_are_only_read_until_one_has_a_value()
    {
        var read = new List<string>();
        var store = new EnvironmentFallbackCredentialStore(
            new InMemoryCredentialStore(),
            new Dictionary<string, string[]> { [Account] = ["A", "B", "C"] },
            name =>
            {
                read.Add(name);
                return name == "B" ? "value" : null;
            });

        Assert.Equal("value", store.Read(Account));
        Assert.Equal(["A", "B"], read);
    }

    [Fact]
    public void Account_names_are_matched_exactly()
    {
        var store = new EnvironmentFallbackCredentialStore(new InMemoryCredentialStore(), new Dictionary<string, string[]> { [Account] = ["A"] }, _ => "x");

        Assert.Null(store.Read(Account.ToUpperInvariant()));
    }

    [Fact]
    public void A_missing_keyring_is_not_an_error_when_the_environment_has_the_credential()
    {
        var store = Fallback(new ThrowingStore(), new() { ["GITHUB_TOKEN"] = "from-env" }, "GITHUB_TOKEN");

        Assert.Equal("from-env", store.Read(Account));
    }

    [Fact]
    public void A_missing_keyring_is_an_error_when_the_environment_has_nothing()
    {
        var store = Fallback(new ThrowingStore(), [], "GITHUB_TOKEN");

        Assert.Throws<CredentialStoreException>(() => store.Read(Account));
    }

    [Fact]
    public void Other_errors_from_the_store_are_not_hidden()
    {
        var store = Fallback(new FailingStore(), new() { ["GITHUB_TOKEN"] = "from-env" }, "GITHUB_TOKEN");

        Assert.Throws<InvalidOperationException>(() => store.Read(Account));
    }

    [Fact]
    public void Saving_and_removing_only_touch_the_inner_store()
    {
        var inner = new InMemoryCredentialStore();
        var environment = new Dictionary<string, string?> { ["GITHUB_TOKEN"] = "from-env" };
        var store = Fallback(inner, environment, "GITHUB_TOKEN");

        store.Write(Account, "saved");
        Assert.Equal("saved", inner.Read(Account));
        store.Delete(Account);

        Assert.Null(inner.Read(Account));
        Assert.Equal("from-env", store.Read(Account));
        Assert.Equal("from-env", environment["GITHUB_TOKEN"]);
    }

    [Fact]
    public void Saving_and_removing_report_a_missing_keyring()
    {
        var store = Fallback(new ThrowingStore(), new() { ["GITHUB_TOKEN"] = "from-env" }, "GITHUB_TOKEN");

        Assert.Throws<CredentialStoreException>(() => store.Write(Account, "x"));
        Assert.Throws<CredentialStoreException>(() => store.Delete(Account));
    }

    [Fact]
    public void The_display_name_is_the_inner_stores()
    {
        Assert.Equal("my keyring", Fallback(new ThrowingStore("my keyring"), []).DisplayName);
    }

    [Fact]
    public void Reads_the_process_environment_by_default()
    {
        const string name = "CONFIGFERRET_TEST_CREDENTIAL_VARIABLE";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "from-process");
            var store = new EnvironmentFallbackCredentialStore(new InMemoryCredentialStore(), new Dictionary<string, string[]> { [Account] = [name] });

            Assert.Equal("from-process", store.Read(Account));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    private sealed class FailingStore : ICredentialStore
    {
        public string DisplayName => "failing";

        public string? Read(string account) => throw new InvalidOperationException("bug");

        public void Write(string account, string secret) => throw new InvalidOperationException("bug");

        public void Delete(string account) => throw new InvalidOperationException("bug");
    }
}
