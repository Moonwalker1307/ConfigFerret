using System.Text.Json;
using ConfigFerret.Shared;
using ConfigFerret.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ConfigFerret.Tests.Common;

/// <summary>A registration of this package that binds credentials, for the shared startup check tests.</summary>
/// <param name="Name">The registration method, used to name the test case.</param>
/// <param name="Section">The section the options are bound from.</param>
/// <param name="Settings">Settings, relative to the section, that pass validation, including any credentials they need.</param>
/// <param name="Credentials">The settings that must not come from an appsettings file.</param>
/// <param name="Register">Calls the registration method with the configuration it binds from.</param>
/// <param name="Read">Reads the options, which runs the startup checks.</param>
internal sealed record GuardedRegistration(
    string Name,
    string Section,
    IReadOnlyDictionary<string, string?> Settings,
    IReadOnlyList<string> Credentials,
    Action<IServiceCollection, IConfiguration> Register,
    Func<IServiceProvider, object> Read);

/// <summary>
/// The startup checks that keep credentials out of appsettings files, run through each registration of the package.
/// This file is compiled into every test project whose package compiles src/Shared, and each of those projects lists
/// its registrations in <see cref="Registrations"/>.
/// </summary>
public partial class CredentialGuardRegistrationTests
{
    private static partial IEnumerable<GuardedRegistration> Registrations();

    public static TheoryData<string, string> Credentials()
    {
        var data = new TheoryData<string, string>();
        foreach (var registration in Registrations())
        {
            foreach (var credential in registration.Credentials)
            {
                data.Add(registration.Name, credential);
            }
        }

        return data;
    }

    public static TheoryData<string> Names() => [.. Registrations().Select(r => r.Name)];

    private static GuardedRegistration Find(string name) => Registrations().Single(r => r.Name == name);

    private static string FakeValue(string credential) => $"fake-{credential}-not-a-real-credential";

    private static string Key(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}:{name}";

    // Every setting except the credential under test, which goes into a file of its own.
    private static Dictionary<string, string?> Others(GuardedRegistration registration, string credential, string prefix) =>
        registration.Settings
            .Where(s => !string.Equals(s.Key, credential, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(s => Key(prefix, s.Key), s => s.Value);

    private static string WriteCredential(TempSettingsFolder folder, string fileName, string key, string value) =>
        folder.Write(fileName, JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value }));

    private static OptionsValidationException Rejected(GuardedRegistration registration, ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(() => registration.Read(provider));

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Rejects_a_credential_in_appsettings_when_reads_are_recorded(string name, string credential)
    {
        var registration = Find(name);
        var key = Key(registration.Section, credential);
        using var folder = new TempSettingsFolder();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(Others(registration, credential, registration.Section));
        configuration.AddJsonFile(WriteCredential(folder, "appsettings.json", key, FakeValue(credential)));
        configuration.RecordReads();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        registration.Register(services, configuration.GetSection(registration.Section));
        using var provider = services.BuildServiceProvider();

        var ex = Rejected(registration, provider);

        Assert.Contains(ex.Failures, f => f.StartsWith($"{key} is set in appsettings.json.", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeValue(credential), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Rejects_a_credential_in_appsettings_bound_from_the_root_without_a_registered_configuration(string name, string credential)
    {
        var registration = Find(name);
        using var folder = new TempSettingsFolder();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Others(registration, credential, ""))
            .AddJsonFile(WriteCredential(folder, "appsettings.json", credential, FakeValue(credential)))
            .Build();
        var services = new ServiceCollection();
        registration.Register(services, configuration);
        using var provider = services.BuildServiceProvider();

        var ex = Rejected(registration, provider);

        Assert.Contains(ex.Failures, f => f.StartsWith($"{credential} is set in appsettings.json.", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeValue(credential), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Rejects_a_credential_bound_from_a_section_without_a_registered_configuration(string name, string credential)
    {
        var registration = Find(name);
        var key = Key(registration.Section, credential);
        using var folder = new TempSettingsFolder();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Others(registration, credential, registration.Section))
            .AddJsonFile(WriteCredential(folder, "appsettings.json", key, FakeValue(credential)))
            .Build();
        var services = new ServiceCollection();
        registration.Register(services, configuration.GetSection(registration.Section));
        using var provider = services.BuildServiceProvider();

        var ex = Rejected(registration, provider);

        Assert.Contains(ex.Failures, f => f.StartsWith($"{key} is set in appsettings.json.", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeValue(credential), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Accepts_credentials_from_environment_variables_bound_from_a_section_without_a_registered_configuration(string name)
    {
        var registration = Find(name);
        var prefix = $"CFGUARD_{name}_";
        var variables = registration.Settings.ToDictionary(
            s => prefix + CredentialGuard.EnvironmentVariable(Key(registration.Section, s.Key)), s => s.Value);
        foreach (var (variable, value) in variables)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var services = new ServiceCollection();
            registration.Register(services, configuration.GetSection(registration.Section));
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(registration.Read(provider));
        }
        finally
        {
            foreach (var variable in variables.Keys)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Rejects_a_credential_in_appsettings_of_the_bound_configuration_when_another_root_is_registered(string name, string credential)
    {
        var registration = Find(name);
        var key = Key(registration.Section, credential);
        using var folder = new TempSettingsFolder();
        var host = new ConfigurationBuilder().AddInMemoryCollection(Others(registration, credential, registration.Section)).Build();
        var own = new ConfigurationBuilder()
            .AddInMemoryCollection(Others(registration, credential, registration.Section))
            .AddJsonFile(WriteCredential(folder, "appsettings.json", key, FakeValue(credential)))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(host);
        registration.Register(services, own.GetSection(registration.Section));
        using var provider = services.BuildServiceProvider();

        var ex = Rejected(registration, provider);

        Assert.Contains(ex.Failures, f => f.StartsWith($"{key} is set in appsettings.json.", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeValue(credential), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Rejects_a_credential_in_the_registered_root_when_the_bound_section_keeps_another_root(string name, string credential)
    {
        var registration = Find(name);
        var key = Key(registration.Section, credential);
        using var folder = new TempSettingsFolder();
        var host = new ConfigurationBuilder()
            .AddInMemoryCollection(Others(registration, credential, registration.Section))
            .AddJsonFile(WriteCredential(folder, "appsettings.json", key, FakeValue(credential)))
            .Build();
        var defaults = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Unrelated"] = "1" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(host);
        registration.Register(services, new FallbackSection(host.GetSection(registration.Section), defaults));
        using var provider = services.BuildServiceProvider();

        var ex = Rejected(registration, provider);

        Assert.Contains(ex.Failures, f => f.StartsWith($"{key} is set in appsettings.json.", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeValue(credential), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Rejects_a_credential_in_the_registered_root_when_the_options_are_bound_from_another_root(string name, string credential)
    {
        var registration = Find(name);
        foreach (var prefix in new[] { registration.Section, "" })
        {
            var key = Key(prefix, credential);
            var variable = $"CFGUARD_OWN_{name}_{CredentialGuard.EnvironmentVariable(key)}";
            using var folder = new TempSettingsFolder();
            var host = new ConfigurationBuilder()
                .AddInMemoryCollection(Others(registration, credential, prefix))
                .AddJsonFile(WriteCredential(folder, "appsettings.json", key, FakeValue(credential)))
                .Build();
            Environment.SetEnvironmentVariable(variable, FakeValue(credential));
            try
            {
                // The app's own configuration takes the credential from an environment variable.
                var own = new ConfigurationBuilder()
                    .AddInMemoryCollection(Others(registration, credential, prefix))
                    .AddEnvironmentVariables($"CFGUARD_OWN_{name}_")
                    .Build();
                var services = new ServiceCollection();
                services.AddSingleton<IConfiguration>(host);
                registration.Register(services, prefix.Length == 0 ? own : own.GetSection(prefix));
                using var provider = services.BuildServiceProvider();

                var ex = Rejected(registration, provider);

                Assert.Contains(ex.Failures, f => f.StartsWith($"{key} is set in appsettings.json.", StringComparison.Ordinal));
                Assert.DoesNotContain(FakeValue(credential), ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Checks_the_bound_root_when_a_configuration_without_a_root_is_registered(string name)
    {
        var registration = Find(name);
        // The in-memory source stands in for environment variables.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(registration.Settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new RootlessSection(configuration.GetSection("Unrelated")));
        registration.Register(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(registration.Read(provider));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Accepts_credentials_bound_from_a_root_without_files_or_a_registered_configuration(string name)
    {
        var registration = Find(name);
        // The in-memory source stands in for environment variables.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(registration.Settings).Build();
        var services = new ServiceCollection();
        registration.Register(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(registration.Read(provider));
    }

    // A section of a type of its own, which passes everything to a section but keeps no configuration root.
    private sealed class RootlessSection(IConfigurationSection section) : IConfigurationSection
    {
        public string? this[string key]
        {
            get => section[key];
            set => section[key] = value;
        }

        public string Key => section.Key;

        public string Path => section.Path;

        public string? Value
        {
            get => section.Value;
            set => section.Value = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => section.GetChildren();

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => section.GetReloadToken();

        public IConfigurationSection GetSection(string key) => section.GetSection(key);
    }
}
