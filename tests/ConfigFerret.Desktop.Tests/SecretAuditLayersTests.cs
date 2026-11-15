using ConfigFerret.Desktop.Services;
using ConfigFerret.GitHub;
using ConfigFerret.Octopus;
using Microsoft.Extensions.Configuration;

namespace ConfigFerret.Desktop.Tests;

/// <summary>Rates settings built by hand, so each rule of the audit is tested on its own.</summary>
public class SecretAuditLayersTests
{
    private const string Token = "ghp_" + "Zx81kQp4LmT7vRw2Nc9YbH3sAbCdEf";

    private static ProvenanceStep Step(
        LayerKind kind,
        string source,
        string? value = "hunter2",
        bool effective = true,
        bool sensitive = false,
        bool inferred = false,
        bool removal = false,
        string? location = null,
        SourceRef? reference = null,
        params ProvenanceStep[] origins) =>
        new()
        {
            Kind = kind,
            Source = source,
            Value = value,
            IsEffective = effective,
            IsSensitive = sensitive,
            IsInferred = inferred,
            IsRemoval = removal,
            Location = location,
            Reference = reference,
            Origins = origins,
        };

    private static ProvenanceStep Json(string source, string? value = "hunter2", bool effective = true, string? location = null) =>
        Step(LayerKind.DotNetProvider, source, value, effective, location: location, reference: new FileRef("/repo/" + source));

    private static ProvenanceStep Env(string name, string? value = "hunter2", bool effective = true, params ProvenanceStep[] origins) =>
        Step(LayerKind.DotNetProvider, "environment variable " + name, value, effective, reference: new EnvironmentVariableRef(name), origins: origins);

    private static SecretFinding Rate(string key, params ProvenanceStep[] steps) =>
        SecretAudit.Rate(new TracedSetting(key, "ignored", steps, false));

    [Fact]
    public void Audit_and_rate_need_something_to_rate()
    {
        Assert.Throws<ArgumentNullException>(() => SecretAudit.Audit(null!));
        Assert.Throws<ArgumentNullException>(() => SecretAudit.Rate(null!));
    }

    [Fact]
    public void Audit_rates_each_setting_in_order()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:Level"] = "Info", ["Db:Password"] = "hunter2" }).Build();
        var report = ConfigTracer.For(root).WithOptions(o => o.RevealSensitiveValues = true).Trace();

        var findings = SecretAudit.Audit(report);

        Assert.Equal(report.Settings.Select(s => s.Key), findings.Select(f => f.Key));
        Assert.Equal(SecretRating.NotSecret, findings.Single(f => f.Key == "Logging:Level").Rating);
        Assert.Equal(SecretRating.Check, findings.Single(f => f.Key == "Db:Password").Rating);
    }

    [Fact]
    public void A_setting_that_does_not_look_secret_says_so_and_names_the_winning_layer()
    {
        var finding = Rate("Logging:Level", Json("appsettings.json", "Info"), Env("LOGGING__LEVEL", "Debug"));

        Assert.Equal(SecretRating.NotSecret, finding.Rating);
        Assert.Equal("Does not look like a secret.", finding.Reason);
        Assert.Equal("environment variable LOGGING__LEVEL", finding.Where);
    }

    [Fact]
    public void The_winning_layer_of_a_setting_that_does_not_look_secret_is_the_deepest_effective_one()
    {
        var finding = Rate("Logging:Level", Env("LOGGING__LEVEL", "Debug", true, Step(LayerKind.KubernetesConfigMap, "ConfigMap a", "x", effective: false), Step(LayerKind.Helm, "values.yaml", "Debug")));

        Assert.Equal("values.yaml", finding.Where);
    }

    [Fact]
    public void A_setting_without_layers_has_no_place()
    {
        Assert.Equal(string.Empty, Rate("Logging:Level").Where);
    }

    [Fact]
    public void A_layer_name_that_looks_secret_makes_the_setting_one()
    {
        var finding = Rate("Connection", Env("CONNECTION", "x", true, Step(LayerKind.KubernetesSecret, "Secret db", "x", reference: new SecretKeyRef("db", "DB_PASSWORD"), sensitive: true)));

        Assert.Equal(SecretRating.Protected, finding.Rating);
    }

    [Theory]
    [MemberData(nameof(SecretReferences))]
    public void Every_kind_of_reference_gives_a_name_to_look_at(SourceRef reference)
    {
        var finding = Rate("Connection", Env("CONNECTION", "x", true, Step(LayerKind.Helm, "values.yaml", "hunter2", reference: reference)));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
    }

    public static TheoryData<SourceRef> SecretReferences() => new()
    {
        new ConfigMapKeyRef("cm", "DB_PASSWORD"),
        new SecretKeyRef("s", "DB_PASSWORD"),
        new WorkloadEnvRef("api", "web", "DB_PASSWORD"),
        new EnvironmentVariableRef("DB_PASSWORD"),
        new HelmValueRef("db.password"),
        new GitHubVariableRef(GitHubVariableKind.Variable, GitHubVariableScope.Repository, "contoso/orders", "DB_PASSWORD"),
        new OctopusVariableRef("Set", "DB_PASSWORD"),
    };

    [Fact]
    public void The_name_of_a_dotnet_layer_is_not_looked_at_when_the_key_gives_nothing_away()
    {
        var finding = Rate("Connection", Step(LayerKind.DotNetProvider, "environment variable", "Info", reference: new EnvironmentVariableRef("DB_PASSWORD")));

        Assert.Equal(SecretRating.NotSecret, finding.Rating);
    }

    [Fact]
    public void A_harmless_layer_name_does_not_make_the_setting_secret()
    {
        var finding = Rate("Connection", Env("CONNECTION", "Info", true, Step(LayerKind.Helm, "values.yaml", "Info", reference: new HelmValueRef("db.host"))));

        Assert.Equal(SecretRating.NotSecret, finding.Rating);
    }

    [Fact]
    public void One_layer_with_a_credential_is_enough()
    {
        var finding = Rate("Blob", Json("appsettings.json", Token, effective: false), Env("BLOB", "short"));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("appsettings.json", finding.Where);
    }

    [Fact]
    public void A_committed_secret_is_exposed_and_names_the_file_and_place()
    {
        var finding = Rate("Db:Password", Json("appsettings.json", "hunter2", location: "line 4"));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("appsettings.json (line 4)", finding.Where);
        Assert.Equal("The value is kept in plain text in appsettings.json. Move it to a secret store (a Kubernetes Secret, a GitHub secret, a sensitive Octopus variable or a key vault).", finding.Reason);
        Assert.DoesNotContain("hunter2", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_place_is_only_added_when_there_is_one()
    {
        Assert.Equal("appsettings.json", Rate("Db:Password", Json("appsettings.json", "hunter2")).Where);
    }

    [Fact]
    public void A_committed_value_that_a_later_layer_overrides_is_still_exposed_and_says_so()
    {
        var finding = Rate("Db:Password", Json("appsettings.json", "hunter2", effective: false), Env("DB__PASSWORD", "x"));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("A later layer overrides it, but appsettings.json still holds a plain-text value. Remove it from there.", finding.Reason);
    }

    [Fact]
    public void The_last_layer_counts_as_the_one_in_use_even_when_not_marked_effective()
    {
        var finding = Rate("Db:Password", Json("appsettings.json", "hunter2", effective: false));

        Assert.StartsWith("The value is kept in plain text in appsettings.json", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_effective_layer_counts_as_in_use_even_when_a_deeper_one_wins()
    {
        var step = Step(LayerKind.DotNetProvider, "appsettings.json", "hunter2", reference: new FileRef("/repo/appsettings.json"), origins: Step(LayerKind.Helm, "values.yaml", null));

        var finding = Rate("Db:Password", step);

        Assert.StartsWith("The value is kept in plain text in appsettings.json", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layer_that_removes_the_key_is_not_exposed()
    {
        var finding = Rate("Db:Password", Step(LayerKind.Helm, "values.yaml", "hunter2", removal: true), Env("DB__PASSWORD", "x"));

        Assert.NotEqual(SecretRating.Exposed, finding.Rating);
    }

    [Fact]
    public void A_sensitive_layer_is_not_plain_text()
    {
        var finding = Rate("Db:Password", Step(LayerKind.Helm, "values.yaml", "hunter2", sensitive: true));

        Assert.NotEqual(SecretRating.Exposed, finding.Rating);
    }

    [Theory]
    [InlineData("")]
    [InlineData("changeme-in-vault")]
    [InlineData("${DB_PASSWORD}")]
    public void A_placeholder_in_a_plain_layer_is_not_exposed(string value)
    {
        var finding = Rate("Db:Password", Json("appsettings.json", value));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.StartsWith("Looks like a secret but only has an empty or placeholder value here.", finding.Reason, StringComparison.Ordinal);
        Assert.Equal("appsettings.json", finding.Where);
    }

    [Fact]
    public void A_plain_value_is_only_exposed_when_the_key_or_the_value_looks_secret()
    {
        var plain = Rate("Db:Host", Json("appsettings.json", "db.example.test"), Step(LayerKind.KubernetesSecret, "Secret db", "x", sensitive: true));

        Assert.NotEqual(SecretRating.Exposed, plain.Rating);
        Assert.Equal(SecretRating.Exposed, Rate("Db:Host", Json("appsettings.json", Token)).Rating);
    }

    [Theory]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.development.json")]
    [InlineData("appsettings.Local.json")]
    public void A_secret_in_a_development_file_is_to_be_checked(string source)
    {
        var finding = Rate("Db:Password", Json(source, "hunter2"));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.Equal($"A development value is kept in plain text in {source}. Fine for a local placeholder; check it isn't a real credential.", finding.Reason);
        Assert.Equal(source, finding.Where);
    }

    [Theory]
    [InlineData("Server=localhost;Database=x")]
    [InlineData("Server=LOCALHOST;Database=x")]
    [InlineData("Server=127.0.0.1;Database=x")]
    public void A_local_address_marks_a_value_as_for_development(string value)
    {
        var finding = Rate("ConnectionStrings:Main", Json("appsettings.json", value));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.StartsWith("A development value is kept in plain text in appsettings.json.", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_address_with_a_password_is_still_exposed()
    {
        var finding = Rate("ConnectionStrings:Main", Json("appsettings.json", "Server=localhost;Password=hunter2"));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
    }

    [Fact]
    public void A_real_secret_is_exposed_even_when_a_development_value_comes_first()
    {
        var finding = Rate("Db:Password", Json("appsettings.Development.json", "dev", effective: false), Json("appsettings.json", "hunter2", effective: false), Env("DB__PASSWORD", "x"));

        Assert.Equal(SecretRating.Exposed, finding.Rating);
        Assert.Equal("appsettings.json", finding.Where);
    }

    [Fact]
    public void A_secret_from_a_kubernetes_secret_is_protected()
    {
        var secret = Step(LayerKind.KubernetesSecret, "Secret db-credentials", "hunter2", sensitive: true, location: "key password");
        var finding = Rate("Db:Password", Env("DB__PASSWORD", "hunter2", true, secret));

        Assert.Equal(SecretRating.Protected, finding.Rating);
        Assert.Equal("Kept in Secret db-credentials.", finding.Reason);
        Assert.Equal("Secret db-credentials (key password)", finding.Where);
    }

    [Fact]
    public void The_last_secret_store_in_the_chain_is_named()
    {
        var first = Step(LayerKind.KubernetesSecret, "Secret first", "x", sensitive: true);
        var second = Step(LayerKind.KubernetesSecret, "Secret second", "x", sensitive: true);
        var other = Step(LayerKind.KubernetesConfigMap, "ConfigMap plain", null);

        var finding = Rate("Db:Password", Env("DB__PASSWORD", null, true, first, second, other));

        Assert.Equal(SecretRating.Protected, finding.Rating);
        Assert.Equal("Kept in Secret second.", finding.Reason);
    }

    [Fact]
    public void A_sensitive_layer_that_does_not_win_is_not_a_secret_store()
    {
        var secret = Step(LayerKind.KubernetesSecret, "Secret db", "x", effective: false, sensitive: true);

        var finding = Rate("Db:Host", Env("DB__HOST", "db.example.test", true, secret));

        Assert.Equal(SecretRating.NotSecret, finding.Rating);
    }

    [Fact]
    public void A_layer_is_only_a_secret_store_when_it_is_marked_sensitive()
    {
        var secret = Step(LayerKind.KubernetesSecret, "Secret db", "x");

        Assert.Equal(SecretRating.NotSecret, Rate("Db:Host", Env("DB__HOST", "x", true, secret)).Rating);
    }

    [Fact]
    public void Only_sensitive_values_of_deployment_tools_are_secret_stores()
    {
        var sensitive = Step(LayerKind.OctopusDeploy, "Octopus variable Db.Password", "x", sensitive: true);
        var plain = Step(LayerKind.OctopusDeploy, "Octopus variable Db.Password", "hunter2");
        var gitHubSensitive = Step(LayerKind.GitHubActions, "GitHub secret DB_PASSWORD", null, sensitive: true);
        var gitHubPlain = Step(LayerKind.GitHubActions, "GitHub variable DB_PASSWORD", "hunter2");

        var octopus = Rate("Db:Password", Env("DB__PASSWORD", "x", true, sensitive));
        var octopusPlain = Rate("Db:Password", Env("DB__PASSWORD", "x", true, plain));
        var github = Rate("Db:Password", Env("DB__PASSWORD", "x", true, gitHubSensitive));
        var githubPlain = Rate("Db:Password", Env("DB__PASSWORD", "x", true, gitHubPlain));

        Assert.Equal(SecretRating.Protected, octopus.Rating);
        Assert.Equal("Kept in sensitive Octopus variable Db.Password.", octopus.Reason);
        Assert.Equal(SecretRating.Exposed, octopusPlain.Rating);
        Assert.Equal(SecretRating.Protected, github.Rating);
        Assert.Equal("Kept in GitHub secret DB_PASSWORD.", github.Reason);
        Assert.Equal(SecretRating.Exposed, githubPlain.Rating);
    }

    [Fact]
    public void A_user_secret_is_protected_and_says_it_is_not_committed()
    {
        var finding = Rate("Db:Password", Step(LayerKind.DotNetProvider, "user secrets", "hunter2", sensitive: true));

        Assert.Equal(SecretRating.Protected, finding.Rating);
        Assert.Equal("Kept in user secrets (not committed).", finding.Reason);
    }

    [Fact]
    public void A_link_matched_by_name_is_to_be_checked()
    {
        var secret = Step(LayerKind.KubernetesSecret, "Secret db", "x", sensitive: true, inferred: true);

        var finding = Rate("Db:Password", Env("DB__PASSWORD", "x", true, secret));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.Equal("Comes from Secret db, but the link was matched by name. Check it's the right one.", finding.Reason);
        Assert.Equal("Secret db", finding.Where);
    }

    [Fact]
    public void A_guess_that_does_not_win_does_not_matter()
    {
        var secret = Step(LayerKind.KubernetesSecret, "Secret db", "x", sensitive: true);
        var guess = Step(LayerKind.Helm, "values.yaml", null, effective: false, inferred: true);

        Assert.Equal(SecretRating.Protected, Rate("Db:Password", Env("DB__PASSWORD", "x", true, secret, guess)).Rating);
    }

    [Fact]
    public void An_environment_variable_that_nothing_in_the_repository_fills_is_to_be_checked()
    {
        var finding = Rate("Db:Password", Env("DB__PASSWORD", "hunter2"));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.Equal("Set by an environment variable whose origin isn't in the repository. Check that the deployment fills it from a secret store.", finding.Reason);
        Assert.Equal("environment variable DB__PASSWORD", finding.Where);
    }

    [Fact]
    public void An_environment_variable_with_an_origin_is_not_called_unknown()
    {
        var finding = Rate("Db:Password", Env("DB__PASSWORD", "", true, Step(LayerKind.KubernetesConfigMap, "ConfigMap a", null)));

        Assert.StartsWith("Looks like a secret but only has an empty or placeholder value", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_value_is_not_explained_by_the_environment_rule()
    {
        var finding = Rate("Db:Password", Step(LayerKind.DotNetProvider, "command line", "hunter2"));

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.Equal("Looks like a secret, but where it comes from is unclear. Check it.", finding.Reason);
        Assert.Equal("command line", finding.Where);
    }

    [Fact]
    public void A_non_dotnet_winner_with_an_environment_reference_is_not_called_unknown()
    {
        var finding = Rate("Db:Password", Step(LayerKind.KubernetesField, "field", "hunter2", reference: new EnvironmentVariableRef("X")));

        Assert.Equal("Looks like a secret, but where it comes from is unclear. Check it.", finding.Reason);
    }

    [Fact]
    public void Unclear_when_only_some_layers_are_placeholders()
    {
        var finding = Rate("Db:Password", Step(LayerKind.DotNetProvider, "command line", "hunter2", effective: false), Step(LayerKind.DotNetProvider, "other", ""));

        Assert.Equal("Looks like a secret, but where it comes from is unclear. Check it.", finding.Reason);
        Assert.Equal("other", finding.Where);
    }

    [Fact]
    public void A_secret_key_with_nothing_at_all_has_no_place()
    {
        var finding = Rate("Db:Password");

        Assert.Equal(SecretRating.Check, finding.Rating);
        Assert.StartsWith("Looks like a secret but only has an empty or placeholder value", finding.Reason, StringComparison.Ordinal);
        Assert.Equal(string.Empty, finding.Where);
    }

    [Theory]
    [InlineData("appsettings.json", false, true)]
    [InlineData("appsettings.json (line 2)", false, true)]
    [InlineData("config.ini", true, true)]
    [InlineData("secrets.txt", false, false)]
    public void Dotnet_layers_are_plain_text_when_they_are_files_or_json(string source, bool fileReference, bool expectedExposed)
    {
        var step = Step(LayerKind.DotNetProvider, source, "hunter2", reference: fileReference ? new FileRef("/repo/" + source) : null);
        Assert.Equal(expectedExposed, Rate("Db:Password", step).Rating == SecretRating.Exposed);
    }

    [Theory]
    [InlineData(LayerKind.KubernetesConfigMap, true)]
    [InlineData(LayerKind.KubernetesWorkload, true)]
    [InlineData(LayerKind.Helm, true)]
    [InlineData(LayerKind.Kustomize, true)]
    [InlineData(LayerKind.GitHubActions, true)]
    [InlineData(LayerKind.OctopusDeploy, true)]
    [InlineData(LayerKind.KubernetesField, false)]
    public void Which_layers_keep_values_in_plain_text(LayerKind kind, bool plain)
    {
        var layer = Step(kind, "source", "hunter2");

        var finding = Rate("Db:Password", Env("DB__PASSWORD", "hunter2", true, layer));

        Assert.Equal(plain, finding.Rating == SecretRating.Exposed);
    }

    [Fact]
    public void Octopus_variable_names_in_findings_are_marked_sensitive_only_for_secret_stores()
    {
        var layer = Step(LayerKind.OctopusDeploy, "Octopus variable Db.Password", "x", sensitive: true);

        Assert.Equal("Kept in sensitive Octopus variable Db.Password.", Rate("Db:Password", Env("DB__PASSWORD", "x", true, layer)).Reason);
    }

    // LooksLikeCredential

    private static string Distinct(int count)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-";
        return string.Concat(Enumerable.Range(0, count).Select(i => alphabet[i % alphabet.Length]));
    }

    [Fact]
    public void A_credential_is_at_least_24_characters()
    {
        Assert.True(SecretAudit.LooksLikeCredential("ABCDEFGHIabcdefghi012345"));
        Assert.False(SecretAudit.LooksLikeCredential("ABCDEFGHIabcdefghi01234"));
    }

    [Fact]
    public void A_credential_is_at_most_512_characters()
    {
        Assert.True(SecretAudit.LooksLikeCredential(Distinct(64)));
        Assert.True(SecretAudit.LooksLikeCredential(string.Concat(Enumerable.Repeat(Distinct(64), 8))));
        Assert.False(SecretAudit.LooksLikeCredential(string.Concat(Enumerable.Repeat(Distinct(64), 8)) + "A"));
    }

    [Fact]
    public void A_credential_has_upper_lower_and_digits()
    {
        Assert.True(SecretAudit.LooksLikeCredential("ABCDEFGHIabcdefghi012345"));
        Assert.False(SecretAudit.LooksLikeCredential("abcdefghijklmnopqrstuvwx0123456789"));
        Assert.False(SecretAudit.LooksLikeCredential("ABCDEFGHIJKLMNOPQRSTUVWX0123456789"));
        Assert.False(SecretAudit.LooksLikeCredential("ABCDEFGHIJKLMabcdefghijklmnopqrstuvwxyz"));
    }

    [Fact]
    public void A_credential_has_an_entropy_of_at_least_four_bits_per_character()
    {
        // 16 different characters, each twice: exactly 4.0 bits.
        Assert.True(SecretAudit.LooksLikeCredential("ABCDEFabcdef0123ABCDEFabcdef0123"));
        // Three classes and long enough, but made of few different characters.
        Assert.False(SecretAudit.LooksLikeCredential("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAaaaaaaaaaaaaaaaaaaaaaaaaaa1"));
        Assert.False(SecretAudit.LooksLikeCredential("Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1Aa1"));
    }

    [Fact]
    public void A_credential_has_no_spaces_and_is_not_a_url()
    {
        Assert.False(SecretAudit.LooksLikeCredential("ABCDEFGHIabcdefghi 012345"));
        Assert.False(SecretAudit.LooksLikeCredential("https://ABCDEFGHIabcdefghi012345"));
    }

    [Fact]
    public void A_credential_is_judged_without_surrounding_white_space()
    {
        Assert.True(SecretAudit.LooksLikeCredential("  ABCDEFGHIabcdefghi012345  "));
        Assert.False(SecretAudit.LooksLikeCredential("  ABCDEFGHIabcdefghi01234  "));
    }

    [Theory]
    [InlineData("Server=x;Password=hunter2", true)]
    [InlineData("Server=x;Pwd=hunter2;", true)]
    [InlineData("Password=hunter2", true)]
    [InlineData("Server=x;Password=;", false)]
    [InlineData("Server=x;Password=${DB_PASSWORD}", false)]
    [InlineData("Server=x;Database=y", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", true)]
    [InlineData("AKIAABCDEFGHIJKLMNOP", true)]
    [InlineData("xoxb-1234567890-abcdef", true)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghij", true)]
    public void Connection_strings_and_known_formats_are_credentials(string value, bool expected)
    {
        Assert.Equal(expected, SecretAudit.LooksLikeCredential(value));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("***", true)]
    [InlineData("****", true)]
    [InlineData("<secret>", true)]
    [InlineData("<token>", true)]
    [InlineData("changeme-in-vault", true)]
    [InlineData("  changeme-in-vault  ", true)]
    [InlineData("#{Db.Password}", true)]
    [InlineData("${DB_PASSWORD}", true)]
    [InlineData("${{ secrets.DB }}", true)]
    [InlineData("{{ .Values.db.password }}", true)]
    [InlineData("__DB_PASSWORD__", true)]
    [InlineData("%DB_PASSWORD%", true)]
    [InlineData("$(DbPassword)", true)]
    [InlineData("hunter2", false)]
    [InlineData("changeme", false)]
    [InlineData("a ${X} b", false)]
    public void Placeholders(string? value, bool expected)
    {
        Assert.Equal(expected, SecretAudit.IsPlaceholder(value));
    }

    [Fact]
    public void A_placeholder_is_never_a_credential_even_when_it_looks_like_one()
    {
        Assert.False(SecretAudit.LooksLikeCredential("${{ secrets.ABCDEFGHIJKLMNOPQRSTUVWXYZ0123 }}"));
        Assert.False(SecretAudit.LooksLikeCredential("changeme-in-vault"));
    }
}
