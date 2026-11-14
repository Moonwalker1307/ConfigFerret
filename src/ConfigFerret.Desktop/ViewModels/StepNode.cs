namespace ConfigFerret.Desktop.ViewModels;

/// <summary>One layer in the provenance tree of the selected setting.</summary>
public sealed class StepNode
{
    /// <summary>Creates a node for <paramref name="step"/> and its upstream layers.</summary>
    public StepNode(ProvenanceStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Step = step;
        Children = step.Origins.Select(o => new StepNode(o)).ToList();
    }

    /// <summary>The layer.</summary>
    public ProvenanceStep Step { get; }

    /// <summary>The upstream layers that produced this one's value.</summary>
    public IReadOnlyList<StepNode> Children { get; }

    /// <summary>The source name, for example values-prod.yaml.</summary>
    public string Source => Step.Source;

    /// <summary>Where in the source, for example line 3.</summary>
    public string? Location => Step.Location;

    /// <summary>True when there is a location to show.</summary>
    public bool HasLocation => !string.IsNullOrEmpty(Step.Location);

    /// <summary>The value, quoted, or a word for a removal or an unknown value.</summary>
    public string Value => Step.IsRemoval ? "removed" : Step.Value is null ? "(unknown)" : $"\"{Step.Value.ReplaceLineEndings(" ⏎ ")}\"";

    /// <summary>The note, such as which container an env entry is on.</summary>
    public string? Note => Step.Note;

    /// <summary>True when there is a note.</summary>
    public bool HasNote => !string.IsNullOrEmpty(Step.Note);

    /// <summary>A short label for the kind of layer.</summary>
    public string KindLabel => LayerKinds.ShortName(Step.Kind);

    /// <summary>The style class for the kind of layer.</summary>
    public string KindCss => LayerKinds.CssClass(Step.Kind);

    /// <summary>"effective", "overridden" or "removes".</summary>
    public string State => Step.IsRemoval ? "removes" : Step.IsEffective ? "effective" : "overridden";

    /// <summary>True when this layer wins among its siblings.</summary>
    public bool IsEffective => Step.IsEffective;

    /// <summary>True when the link to this layer is a best guess.</summary>
    public bool IsInferred => Step.IsInferred;

    /// <summary>True when the value is secret.</summary>
    public bool IsSensitive => Step.IsSensitive;
}
