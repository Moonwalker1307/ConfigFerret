using System.Text.Json;
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
}
