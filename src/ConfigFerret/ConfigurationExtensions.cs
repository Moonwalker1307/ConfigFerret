using Microsoft.Extensions.Configuration;

namespace ConfigFerret;

/// <summary>Shortcuts for tracing .NET configuration without Kubernetes or Helm.</summary>
public static class ConfigurationExtensions
{
    /// <summary>Traces every effective value in <paramref name="configuration"/>.</summary>
    public static ProvenanceReport TraceProvenance(this IConfigurationRoot configuration, TraceOptions? options = null)
    {
        var tracer = ConfigTracer.For(configuration);
        if (options is not null)
        {
            tracer.WithOptions(options);
        }

        return tracer.Trace();
    }

    /// <summary>Answers "why is this value what it is?" for one key, as text.</summary>
    public static string Explain(this IConfigurationRoot configuration, string key, TraceOptions? options = null) =>
        configuration.TraceProvenance(options).Explain(key);
}
