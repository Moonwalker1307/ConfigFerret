using ConfigFerret.Kubernetes;

namespace ConfigFerret.HashiCorp.Tests;

/// <summary>The core reads an ExternalSecret; the Vault resolver attaches the Vault secret under it.</summary>
public class ExternalSecretsIntegrationTests
{
    private const string Manifests = """
        apiVersion: apps/v1
        kind: Deployment
        metadata: { name: orders }
        spec:
          template:
            spec:
              containers:
                - name: api
                  env:
                    - name: DB_PASSWORD
                      valueFrom: { secretKeyRef: { name: orders-db, key: password } }
        ---
        apiVersion: external-secrets.io/v1
        kind: SecretStore
        metadata: { name: vault-backend }
        spec:
          provider:
            vault: { server: "https://vault.example.com", path: secret, version: v2 }
        ---
        apiVersion: external-secrets.io/v1
        kind: ExternalSecret
        metadata: { name: orders-db }
        spec:
          secretStoreRef: { name: vault-backend, kind: SecretStore }
          data:
            - secretKey: password
              remoteRef: { key: orders/prod/db, property: password }
        """;

    [Fact]
    public void A_container_secret_traces_through_the_ExternalSecret_to_Vault()
    {
        var vault = new VaultSecrets([new VaultSecret("orders/prod/db", "orders/prod/db", Version: 3)]);
        var report = ConfigTracer.ForContainer(KubernetesManifests.Parse(Manifests))
            .WithVault(vault)
            .Trace();

        var setting = report.Find("DB_PASSWORD")!;
        Assert.True(setting.IsSensitive);
        var chain = Flatten(setting.EffectiveStep!).ToList();
        Assert.Contains(chain, s => s.Reference is ExternalSecretRef { Provider: "vault", RemoteKey: "orders/prod/db", Property: "password" });
        var vaultStep = Assert.Single(chain, s => s.Kind == LayerKind.HashiCorpVault);
        Assert.True(vaultStep.IsSensitive);
        Assert.False(vaultStep.IsInferred);
    }

    private static IEnumerable<ProvenanceStep> Flatten(ProvenanceStep step) =>
        new[] { step }.Concat(step.Origins.SelectMany(Flatten));
}
