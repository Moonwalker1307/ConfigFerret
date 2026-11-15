using ConfigFerret.GitOps;
using ConfigFerret.Kubernetes;
using ConfigFerret.Terraform;

namespace ConfigFerret.Tests;

/// <summary>
/// Golden tests for the GitOps and Terraform sources: a container traced from outside through an Argo CD Application,
/// a Flux HelmRelease, a Flux Kustomization with postBuild substitutions, and Terraform outputs. See <see cref="GoldenTests"/>.
/// </summary>
public class SourceGoldenTests
{
    [Fact]
    public Task Argo_cd_application_text() => VerifyText(TraceArgoCd().ToText());

    [Fact]
    public Task Argo_cd_application_json() => VerifyJson(TraceArgoCd().ToJson());

    [Fact]
    public Task Flux_helm_release_text() => VerifyText(TraceFluxHelmRelease().ToText());

    [Fact]
    public Task Flux_helm_release_text_with_secrets_revealed() => VerifyText(TraceFluxHelmRelease(reveal: true).ToText());

    [Fact]
    public Task Flux_kustomization_text() => VerifyText(TraceFluxKustomization().ToText());

    [Fact]
    public Task Flux_kustomization_json() => VerifyJson(TraceFluxKustomization().ToJson());

    [Fact]
    public Task Terraform_outputs_text() => VerifyText(TraceTerraform().ToText());

    [Fact]
    public Task Terraform_outputs_json() => VerifyJson(TraceTerraform().ToJson());

    [Fact]
    public void Secrets_never_appear_unless_revealed()
    {
        var outputs = new[]
        {
            TraceFluxHelmRelease().ToText(), TraceFluxHelmRelease().ToJson(),
            TraceFluxKustomization().ToText(), TraceFluxKustomization().ToJson(),
            TraceTerraform().ToText(), TraceTerraform().ToJson(),
        };

        foreach (var output in outputs)
        {
            Assert.DoesNotContain("flux-s3cr3t", output, StringComparison.Ordinal);
            Assert.DoesNotContain("stripe-flux-s3cr3t", output, StringComparison.Ordinal);
            Assert.DoesNotContain("tf-s3cr3t-pw", output, StringComparison.Ordinal);
            Assert.DoesNotContain("resource-attribute-s3cr3t", output, StringComparison.Ordinal);
        }

        Assert.Contains("flux-s3cr3t", TraceFluxHelmRelease(reveal: true).ToText(), StringComparison.Ordinal);
    }

    internal static ProvenanceReport TraceArgoCd()
    {
        var dir = Fixture("argocd");
        var application = GitOpsApplication.Load(Path.Join(dir, "repo", "apps", "orders-prod.yaml"));
        return ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(dir, "rendered.yaml")), "orders", "orders")
            .WithGitOps(application, new GitOpsOptions { RepositoryRoot = Path.Join(dir, "repo") })
            .Trace();
    }

    internal static ProvenanceReport TraceFluxHelmRelease(bool reveal = false)
    {
        var dir = Fixture("flux");
        var release = GitOpsApplication.Load(Path.Join(dir, "repo", "clusters", "prod", "orders-helmrelease.yaml"));
        return ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(dir, "orders-rendered.yaml")), "orders", "orders")
            .WithOptions(o => o.RevealSensitiveValues = reveal)
            .WithGitOps(release, new GitOpsOptions { ChartFolder = Path.Join(Fixture("argocd"), "repo", "charts", "orders") })
            .Trace();
    }

    internal static ProvenanceReport TraceFluxKustomization()
    {
        var dir = Fixture("flux");
        var kustomization = GitOpsApplication.Load(Path.Join(dir, "repo", "clusters", "prod", "billing.yaml"));
        return ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(dir, "billing-rendered.yaml")), "billing", "billing")
            .WithGitOps(kustomization, new GitOpsOptions { RepositoryRoot = Path.Join(dir, "repo") })
            .Trace();
    }

    internal static ProvenanceReport TraceTerraform()
    {
        var dir = Fixture("terraform");
        var outputs = TerraformOutputs.Load(Path.Join(dir, "outputs.json"), Path.Join(dir, "infra", "terraform.tfstate"), Path.Join(dir, "infra"));
        return ConfigTracer.ForContainer(KubernetesManifests.LoadFile(Path.Join(dir, "rendered.yaml")), "orders", "orders")
            .WithTerraform(outputs)
            .Trace();
    }

    internal static string Fixture(string name) => TestPaths.Data("gitops", name);

    private static Task VerifyText(string text) => Verify(text, Settings());

    private static Task VerifyJson(string json) => Verify(json, "json", Settings());

    private static VerifySettings Settings()
    {
        var settings = new VerifySettings();
        settings.UseDirectory("Snapshots");
        var root = TestPaths.Data();
        settings.AddScrubber(sb =>
        {
            sb.Replace(root, "{TestData}");
            sb.Replace(root.Replace('\\', '/'), "{TestData}");
        });
        return settings;
    }
}
