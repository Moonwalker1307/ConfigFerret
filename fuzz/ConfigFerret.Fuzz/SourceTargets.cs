using ConfigFerret.GitOps;
using ConfigFerret.Helm;
using ConfigFerret.Kubernetes;
using ConfigFerret.Terraform;

namespace ConfigFerret.Fuzz;

/// <summary>Fuzz targets for the GitOps and Terraform sources in the core package. They read no files.</summary>
internal static partial class Targets
{
    // GitOps manifests (Argo CD, Flux) %% source manifests holding ${VAR} placeholders.
    private static void GitOpsTarget(ReadOnlySpan<byte> data)
    {
        var parts = Split(data);
        IReadOnlyList<GitOpsApplication> applications;
        try
        {
            applications = GitOpsApplication.ParseAll(parts[0], "fuzz.yaml");
        }
        catch (FormatException)
        {
            return;
        }

        KubernetesManifests? sources = null;
        if (parts.Count > 1)
        {
            try
            {
                sources = KubernetesManifests.Parse(parts[1], "source.yaml");
            }
            catch (FormatException)
            {
                sources = null;
            }
        }

        var diagnostics = new TraceDiagnostics();
        foreach (var application in applications.Take(10))
        {
            if (application.HelmSources.Count > 0)
            {
                var chart = HelmChart.ForGitOps(application);
                ExerciseChart(chart, chart.History.Keys);
            }

            var resolver = new GitOpsResolver(application);
            if (sources is not null)
            {
                resolver.AddTemplates(sources);
            }

            var patched = (application.Kustomize?.Patches ?? []).SelectMany(p => p.Values).Take(100)
                .Select(v => Step(LayerKind.KubernetesConfigMap, new ConfigMapKeyRef(v.ConfigMap, v.Key), v.Value));
            var templated = (sources?.ConfigMaps.Values ?? []).SelectMany(d => d.Data.Keys.Select(k => Step(LayerKind.KubernetesConfigMap, new ConfigMapKeyRef(d.Name, k), null)))
                .Concat((sources?.Secrets.Values ?? []).SelectMany(d => d.Data.Keys.Select(k => Step(LayerKind.KubernetesSecret, new SecretKeyRef(d.Name, k), null))))
                .Concat((sources?.Workloads ?? []).SelectMany(w => w.Containers.SelectMany(c => c.Env.Select(e => Step(LayerKind.KubernetesWorkload, new WorkloadEnvRef(w.Name, c.Name, e.Name), null)))))
                .Take(200);
            var variables = application.Substitutions.Select(s => s.Name)
                .Concat(application.SubstituteFrom.Select(s => s.Name))
                .Take(100)
                .Select(n => Step(LayerKind.GitOps, new GitOpsVariableRef(application.Name, n), null));
            foreach (var step in patched.Concat(templated).Concat(variables).ToList())
            {
                var resolved = resolver.Resolve(step, diagnostics);
                Consume(resolved);
                foreach (var origin in resolved.Where(o => o.Reference is GitOpsVariableRef))
                {
                    Consume(resolver.Resolve(origin, diagnostics));
                }
            }

            if (resolver.Entries().Any(e => string.IsNullOrEmpty(e.Name) && e.Reference is not HelmValueRef))
            {
                throw new InvariantException("A GitOps entry has no name.");
            }
        }
    }

    // terraform output -json %% a state file %% a .tf file.
    private static void TerraformTarget(ReadOnlySpan<byte> data)
    {
        var parts = Split(data);
        var all = new List<TerraformOutputs>();
        try
        {
            all.Add(TerraformOutputs.Parse(parts[0], "outputs.json"));
        }
        catch (FormatException)
        {
            // Malformed outputs are rejected with the documented exception.
        }

        if (parts.Count > 1)
        {
            try
            {
                all.Add(TerraformOutputs.ParseState(parts[1], "terraform.tfstate"));
            }
            catch (FormatException)
            {
                // Same for a malformed state file.
            }
        }

        var diagnostics = new TraceDiagnostics();
        foreach (var outputs in all)
        {
            if (parts.Count > 2)
            {
                outputs.AddDefinitions(parts[2], "outputs.tf");
                if (outputs.Definitions.Values.Any(d => d.Line < 1))
                {
                    throw new InvariantException("A Terraform definition has a line below 1.");
                }
            }

            var resolver = new TerraformResolver(outputs, new TerraformTraceOptions());
            foreach (var value in outputs.Values.Take(200))
            {
                if (resolver.Match(value.Name) is null || outputs.Find(value.Name) is null)
                {
                    throw new InvariantException($"Terraform output '{value.Name}' does not match its own name.");
                }

                if (value.IsSensitive && value.ToString() != $"Terraform output {value.Name} = (sensitive)")
                {
                    throw new InvariantException("A sensitive Terraform output shows its value in ToString.");
                }

                foreach (var step in resolver.Resolve(Step(LayerKind.KubernetesWorkload, new WorkloadEnvRef("w", "c", value.Name), LayerValue), diagnostics))
                {
                    if (step.IsSensitive != value.IsSensitive && step.Reference == new TerraformOutputRef(value.Output, value.Path))
                    {
                        throw new InvariantException("A Terraform step lost the output's sensitivity.");
                    }

                    if (step.Note?.Contains(LayerValue, StringComparison.Ordinal) == true)
                    {
                        throw new InvariantException("A Terraform note holds the layer's value.");
                    }
                }
            }
        }
    }
}
