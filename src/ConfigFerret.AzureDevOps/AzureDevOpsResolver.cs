using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ConfigFerret.Sources;

namespace ConfigFerret.AzureDevOps;

/// <summary>
/// Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to Azure Pipelines variable group
/// variables and pipeline YAML variables.
/// </summary>
internal sealed partial class AzureDevOpsResolver(AzureDevOpsVariableGroups groups, AzureDevOpsTraceOptions options) : IOriginResolver
{
    // How far what a variable comes to is followed, through references and through the candidates for each one, before
    // the trace takes the values it compares to differ.
    private const int MaxReferences = 8;
    private const int MaxCandidates = 32;

    // The most pipeline entries one trace step looks at: the tracer keeps no more upstream steps than this per setting.
    private const int MaxSteps = 1_000;

    private readonly List<string> _pipelineWarnings = options.ReadConfiguredPipelines();
    private readonly HashSet<string> _partWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _secretVariables = groups.Variables.Where(IsSensitive).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly ILookup<string, (AzureDevOpsVariable Variable, int Index)> _groupsByName = groups.Variables.Select((v, i) => (v, i)).ToLookup(g => g.v.Name, StringComparer.OrdinalIgnoreCase);
    private readonly ILookup<string, (AzureDevOpsVariable Variable, int Index)> _groupsByEnvironmentName =
        groups.Variables.Select((v, i) => (v, i)).ToLookup(g => AzureDevOpsVariableGroups.ToEnvironmentName(g.v.Name), StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<ProvenanceStep, string> _seen = new();
    private ILookup<string, AzurePipelinesVariable> _byScope = Array.Empty<AzurePipelinesVariable>().ToLookup(v => v.Name);
    private ILookup<string, AzurePipelinesVariable> _byScopeNotSelf = Array.Empty<AzurePipelinesVariable>().ToLookup(v => v.Name);
    private ILookup<string, AzurePipelinesVariable> _byEnvironmentScope = Array.Empty<AzurePipelinesVariable>().ToLookup(v => v.Name);
    private ILookup<string, AzurePipelinesGroupLink> _groupLinks = Array.Empty<AzurePipelinesGroupLink>().ToLookup(g => g.Group);
    private ILookup<string, AzurePipelinesGroupLink> _groupLinksByScope = Array.Empty<AzurePipelinesGroupLink>().ToLookup(g => g.Group);
    private ILookup<string, (AzurePipelinesMapping Mapping, PipelineScope Scope)> _links = Array.Empty<(AzurePipelinesMapping Mapping, PipelineScope Scope)>().ToLookup(l => l.Mapping.Name);
    private Dictionary<(string File, int Line, string Name), AzurePipelinesVariable> _defined = [];
    private HashSet<string> _maySecret = [];
    private int _indexed = -1;
    private int _room;
    private bool _cut;
    private bool _warned;

    /// <summary>How many pipeline entries the resolver looked at, so a test can check that the work grows with the file.</summary>
    internal long Examined { get; private set; }

    /// <summary>One entry per variable group variable, matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() => groups.Variables.Select(v => new SourceEntry(
        v.Name,
        Reference(v),
        IsSensitive(v),
        v.GroupType == AzureDevOpsGroupType.AzureKeyVault
            ? $"variable group {v.Group}, linked to Azure Key Vault {v.KeyVaultName ?? "(unknown vault)"}"
            : $"variable group {v.Group}{(v.IsSecret ? ", secret" : string.Empty)}",
        Location(v)));

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in groups.Warnings.Concat(_pipelineWarnings))
            {
                diagnostics.Warn(warning);
            }
        }

        Index();
        _room = MaxSteps;
        _cut = false;
        var found = Find(layer, diagnostics);
        if (_cut && (layer.Reference is AzurePipelinesVariableRef cut ? cut.Name : LayerNames.Of(layer)) is { } subject && _partWarnings.Add("\0" + subject))
        {
            diagnostics.Warn(string.Create(CultureInfo.InvariantCulture, $"{subject} is linked or set in more Azure Pipelines stages, jobs and variables than the trace follows for one step ({MaxSteps:N0}), so not all of them were followed; the steps listed are marked inferred and sensitive."));
        }

        return found;
    }

    private List<ProvenanceStep> Find(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        if (layer.Reference is AzurePipelinesVariableRef pipelineRef)
        {
            // A pipeline variable whose whole value is $(Other) gets its value from Other, as its stage or job sees it.
            if (!_defined.TryGetValue((pipelineRef.File, pipelineRef.Line, pipelineRef.Name), out var defined)
                || AzureDevOpsTraceOptions.MacroTarget(defined.Value) is not { } target)
            {
                return [];
            }

            // The value seen where the trace started, when this step came from such a trace, passes through the reference.
            var seen = _seen.TryGetValue(layer, out var started) ? started : null;
            return Chain(target, null, defined, options.ScopeOf(defined), seen, diagnostics)
                .Select(c => Step(c, layer, $"{defined.File} line {defined.Line} sets {defined.Name} from $({target})", inferred: false, seen))
                .ToList();
        }

        if (layer.Reference is ExternalSecretRef or AzureDevOpsVariableRef || LayerNames.Of(layer) is not { } name)
        {
            return [];
        }

        if (options.ExplicitLink(name) is { } explicitLink)
        {
            return Linked(name, explicitLink, default, layer, diagnostics);
        }

        var units = Units(name);
        if (units.Count == 1)
        {
            return Linked(name, units[0].Mapping!, units[0].Scope, layer, diagnostics);
        }

        if (units.Count > 1)
        {
            return Alternatives(name, units, layer, diagnostics);
        }

        if (!options.MatchByName)
        {
            return [];
        }

        return Chain(name, null, null, default, layer.Value, diagnostics, byEnvironmentName: true).Select(c => Step(c, layer, How(c, name), inferred: true, layer.Value)).ToList();
    }

    private List<ProvenanceStep> Linked(string name, AzurePipelinesMapping mapping, PipelineScope scope, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var linked = Chain(mapping.VariableName, mapping.Group, null, scope, layer.Value, diagnostics);
        if (linked.Count == 0)
        {
            WarnMissing(name, mapping, diagnostics);
            return [];
        }

        return linked.Select(c => Step(c, layer, How(mapping), inferred: false, layer.Value)).ToList();
    }

    // Several stages or jobs may set name. Each one is followed as its own stage or job sees it; when they all come to
    // the same value it is one certain link, and otherwise each one is an inferred alternative and a warning names
    // them. Every alternative is then sensitive when any of them may be, since the tracer redacts by the one it marks
    // effective.
    private List<ProvenanceStep> Alternatives(string name, List<Unit> units, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        // Past the bound on what one step looks at, the stages and jobs not followed yet are left out.
        var followed = new List<(Unit Unit, List<Candidate> Chain)>();
        foreach (var unit in units.TakeWhile(_ => !_cut))
        {
            followed.Add((unit, Follow(name, unit, layer.Value, diagnostics)));
        }

        var ends = followed.Select(f => f.Chain.Count == 0 || f.Chain[^1].Alternative ? null : Comes(f.Chain[^1], f.Unit.Scope, 0)).ToList();
        var sensitive = _cut || followed.Any(f => MaySecret(f.Unit.Mapping?.VariableName ?? name) || f.Chain.Any(MaySecret));
        if (!_cut && ends.All(e => e is not null && Equals(e, ends[0])))
        {
            // The links that agree may still include one from a variable that may be secret.
            var first = followed[0];
            return first.Chain.Select(c => Step(c with { Sensitive = c.Sensitive || sensitive }, layer, How(c, name, first.Unit), inferred: first.Unit.Mapping is null, layer.Value)).ToList();
        }

        WarnParts($"{(units.Any(u => u.Mapping is null) ? "link or set" : "link")} {name} to {(!_cut && ends.All(e => e is string) ? "different values" : "values that cannot be compared")}", units.Select(u => u.Scope), diagnostics);
        var steps = new List<(bool Matches, List<ProvenanceStep> Steps)>();
        for (var i = 0; i < followed.Count; i++)
        {
            var (unit, chain) = followed[i];
            if (chain.Count == 0)
            {
                WarnMissing(name, unit.Mapping!, diagnostics);
            }

            var found = chain.Select(c => Step(c with { Alternative = true, Sensitive = sensitive }, layer, How(c, name, unit), inferred: unit.Mapping is null, layer.Value)).ToList();
            var value = ends[i] as string ?? (chain.Count == 0 ? null : chain[^1].Value);
            steps.Add((layer.Value is not null && string.Equals(value, layer.Value, StringComparison.Ordinal), found));
        }

        // The tracer marks the last step effective, so one that comes to the value seen here goes last.
        return steps.OrderBy(s => s.Matches).SelectMany(s => s.Steps).ToList();
    }

    private static string How(Candidate candidate, string name) =>
        string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
            ? "matched by name"
            : $"matched by name: scripts see {candidate.Name} as {name}";

    private static string How(Candidate candidate, string name, Unit unit) => unit.Mapping is { } mapping ? How(mapping) : How(candidate, name);

    private static string How(AzurePipelinesMapping mapping) =>
        mapping.Where is null ? "linked explicitly" : $"{mapping.Where} sets {mapping.Name} from $({mapping.VariableName})";

    private static void WarnMissing(string name, AzurePipelinesMapping mapping, TraceDiagnostics diagnostics) =>
        diagnostics.Warn($"{name} is linked to Azure Pipelines variable {(mapping.Group is null ? string.Empty : mapping.Group + "/")}{mapping.VariableName}{(mapping.Where is null ? string.Empty : $" ({mapping.Where})")}, but no such variable was loaded.");

    // What may set name: each pipeline file link, in stages, jobs and at the top level alike, and, once there is a link,
    // each stage or job without a link for name where a pipeline variable, or a group variable that is not secret (a
    // secret one reaches scripts only through env:), gives scripts name.
    private List<Unit> Units(string name)
    {
        var units = new List<Unit>();
        foreach (var (mapping, scope) in _links[name])
        {
            Examined++;
            units.Add(new Unit(mapping, scope));
        }

        if (units.Count == 0)
        {
            return units;
        }

        var linked = units.Select(u => u.Scope).ToHashSet();
        var scopes = Yaml(name, byEnvironmentName: true, Everywhere).Select(options.ScopeOf);
        foreach (var (variable, _) in GroupCandidates(name, byEnvironmentName: true).Where(g => !g.Variable.IsSensitive))
        {
            var links = _groupLinks[variable.Group].ToList();
            Examined += links.Count;
            scopes = scopes.Concat(links.Count == 0 ? [default] : links.Select(options.ScopeOf));
        }

        foreach (var scope in scopes)
        {
            Examined++;
            if (linked.Add(scope))
            {
                units.Add(new Unit(null, scope));
            }
        }

        return units;
    }

    private List<Candidate> Follow(string name, Unit unit, string? seen, TraceDiagnostics diagnostics) =>
        unit.Mapping is { } mapping
            ? Chain(mapping.VariableName, mapping.Group, null, unit.Scope, seen, diagnostics)
            : Chain(name, null, null, unit.Scope, seen, diagnostics, byEnvironmentName: true);

    // Every group and pipeline variable that sets name, lowest priority first: the job's win over the stage's and the
    // stage's over the pipeline's, a group or variable listed later at the same level wins, and groups no pipeline file
    // links come first. A job gets the pipeline's variables and
    // groups, its stage's and its own, never another stage's or job's, so view is the stage or job whose variables
    // count, or the pipeline itself for all of them when the trace cannot tell which.
    // What a variable that refers to itself leads to in the file is only inferred, since it is not certain which one
    // Azure Pipelines expands it to.
    // A chain cut short at the bound may have lost the one that wins, and a secret with it.
    private List<Candidate> Chain(string name, string? group, AzurePipelinesVariable? except, PipelineScope view, string? seen, TraceDiagnostics diagnostics, bool byEnvironmentName = false)
    {
        var chain = MarkAlternatives(Candidates(name, group, except, view, byEnvironmentName)!, view, seen, diagnostics);
        return _cut ? chain.Select(c => c with { Alternative = true, Sensitive = true }).ToList() : chain;
    }

    // The candidates for name in view, in priority order, or null when there are more than limit to look at. When except
    // refers to name, a pipeline variable that refers back to name cannot supply a value, so it is left out: Version:
    // $(Version) in every stage would otherwise lead from each stage to all the others, for each level followed.
    private List<Candidate>? Candidates(string name, string? group, AzurePipelinesVariable? except, PipelineScope view, bool byEnvironmentName, int? limit = null)
    {
        var looked = 0;
        var fromGroups = new List<Candidate>();
        foreach (var (variable, index) in GroupCandidates(name, byEnvironmentName))
        {
            Examined++;
            if (++looked > limit)
            {
                return null;
            }

            if (group is not null && !string.Equals(variable.Group, group, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A group that no pipeline file links counts everywhere; a linked one where a line that links it applies, and
            // the first such line gives its place. For the pipeline, every line applies, and they are in file order.
            var applies = view.IsPipeline
                ? _groupLinks[variable.Group].FirstOrDefault()
                : VisibleKeys(view).SelectMany(k => _groupLinksByScope[GroupKey(variable.Group, k)])
                    .Where(l => Visible(options.ScopeOf(l), view))
                    .MinBy(l => l.Order);
            if (applies is not null || !_groupLinks.Contains(variable.Group))
            {
                fromGroups.Add(new Candidate(variable.Name, variable, null, applies?.Order ?? -1, index, applies is null ? default : options.ScopeOf(applies)));
            }
        }

        var self = except is not null && string.Equals(except.Name, name, StringComparison.OrdinalIgnoreCase);
        var fromYaml = new List<Candidate>();
        if (group is null)
        {
            // Looked at from the narrowest scope and from the end, so that the bound keeps the ones that win, as the
            // tracer's own bound keeps the end of a chain.
            // A variable that refers back to name, left out below when except is set, is not even looked at.
            foreach (var variable in VisibleKeys(view).Reverse().SelectMany(k => YamlBackward(name, byEnvironmentName, k, leaveOutSelf: except is not null)))
            {
                Examined++;
                if (++looked > limit)
                {
                    return null;
                }

                var scope = options.ScopeOf(variable);
                if (ReferenceEquals(variable, except) || !Visible(scope, view)
                    || (except is not null && string.Equals(AzureDevOpsTraceOptions.MacroTarget(variable.Value), name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Without a limit of its own, what is kept counts toward the bound for the step.
                if (limit is null && _room <= 0)
                {
                    _cut = true;
                    break;
                }

                if (limit is null)
                {
                    _room--;
                }

                fromYaml.Add(new Candidate(variable.Name, null, variable, variable.Order, 0, scope, self));
            }
        }

        // A job's variables win over its stage's, and a stage's over the pipeline's, wherever they are written; file order
        // decides only within one of them.
        var candidates = fromGroups.Concat(fromYaml).OrderBy(c => Level(c.Scope)).ThenBy(c => c.Order).ThenBy(c => c.Index).ToList();

        // The groups that apply count toward the bound too. Past it the first ones go, groups no file links among them.
        if (limit is null)
        {
            _room -= fromGroups.Count;
            if (_room < 0)
            {
                _cut = true;
                candidates.RemoveRange(0, Math.Min(-_room, candidates.Count));
                _room = 0;
            }
        }

        return candidates;
    }

    // When the trace cannot tell which stage or job it is about, the stages and jobs below view that set the name to
    // values that come to different things are alternatives: each is inferred, none is compared with the value seen
    // here, and all are sensitive when any may be. A stage's variable counts for the stage's jobs that do not set it
    // again, so a stage and one of its jobs can be alternatives too. A warning names them.
    private List<Candidate> MarkAlternatives(List<Candidate> chain, PipelineScope view, string? seen, TraceDiagnostics diagnostics)
    {
        if (view.Job is not null)
        {
            return chain;
        }

        var units = chain.Where(c => !c.Scope.IsPipeline && (c.Scope == view || !c.Scope.Covers(view))).ToList();
        var last = units.GroupBy(c => c.Scope).Select(g => g.Last()).ToList();
        var ends = last.Select(c => Comes(c, view, 0)).ToList();
        if (last.Count < 2 || ends.All(e => e is not null && Equals(e, ends[0])))
        {
            return chain;
        }

        WarnParts(ends.All(e => e is string) ? $"set {last[0].Name} differently" : $"set {last[0].Name} to values that cannot be compared", last.Select(c => c.Scope), diagnostics);
        var sensitive = units.Any(MaySecret);
        var alternatives = new HashSet<Candidate>(units, ReferenceEqualityComparer.Instance);
        var marked = chain.Select(c => alternatives.Contains(c) ? c with { Alternative = true, Sensitive = sensitive } : c).ToList();

        // The tracer marks the last step effective, so among the alternatives one with the value seen here goes last,
        // in the place of the last alternative.
        var slots = Enumerable.Range(0, marked.Count).Where(i => marked[i].Alternative).ToList();
        var ordered = slots.Select(i => marked[i]).OrderBy(c => seen is not null && string.Equals(c.Value, seen, StringComparison.Ordinal)).ToList();
        for (var i = 0; i < slots.Count; i++)
        {
            marked[slots[i]] = ordered[i];
        }

        return marked;
    }

    private void WarnParts(string what, IEnumerable<PipelineScope> parts, TraceDiagnostics diagnostics)
    {
        if (!_partWarnings.Add(what))
        {
            return;
        }

        var names = parts.Distinct().Select(p => p.ToString()).ToList();
        var list = string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : string.Empty);
        diagnostics.Warn($"Azure Pipelines {list} {what}, and the trace cannot tell which one ran this, so their steps are marked inferred.");
    }

    // What a candidate comes to, to compare it with others, in view or in its own stage or job when that is below view:
    // the value, when it refers to no other variable and may not be secret; for a whole-value reference, what the
    // reference comes to there; for a value that may be secret, the variable itself, the same only as itself. Null when
    // that cannot be told cheaply: a value with a reference inside it, a variable that refers to itself, or a reference
    // that stages or jobs below may set again.
    private object? Comes(Candidate candidate, PipelineScope view, int depth)
    {
        var at = view.Covers(candidate.Scope) ? candidate.Scope : view;
        if (candidate.Variable is { } variable)
        {
            return IsSensitive(variable) || variable.Value is null ? new Undecided(variable)
                : variable.Value.Contains('$', StringComparison.Ordinal) ? null
                : variable.Value;
        }

        var yaml = candidate.Yaml!;
        if (AzureDevOpsTraceOptions.MacroTarget(yaml.Value) is not { } target)
        {
            return SensitiveName().IsMatch(yaml.Name) ? new Undecided(yaml) : yaml.Value.Contains('$', StringComparison.Ordinal) ? null : yaml.Value;
        }

        if (depth >= MaxReferences || string.Equals(target, yaml.Name, StringComparison.OrdinalIgnoreCase)
            || Candidates(target, null, yaml, at, byEnvironmentName: false, MaxCandidates) is not { Count: > 0 } found
            || found.Any(c => at.Covers(c.Scope) && !c.Scope.Covers(at)))
        {
            return null;
        }

        return Comes(found[^1], at, depth + 1);
    }

    // The keys that find what view sees: everything for the pipeline, or the pipeline's, the stage's and the job's own.
    // A job outside stages sees the pipeline's and its own, and passes on what that job sets in any stage.
    private static IEnumerable<string> VisibleKeys(PipelineScope view) => view switch
    {
        { IsPipeline: true } => [Everywhere],
        { Stage: null } => [Exactly(default), Job(view.Job)],
        { Job: null } => [Exactly(default), Stage(view.Stage)],
        _ => [Exactly(default), Exactly(new PipelineScope(view.Stage, null)), Exactly(view), Exactly(new PipelineScope(null, view.Job))],
    };

    private const string Everywhere = "*";

    private static string Exactly(PipelineScope scope) => $"={scope.Stage}\u0001{scope.Job}";

    private static string Stage(string? stage) => $"+{stage}";

    private static string Job(string? job) => $"-{job}";

    private static string Key(string name, string scopeKey) => $"{name}\u0001{scopeKey}";

    private static string GroupKey(string group, string scopeKey) => $"{group}\u0001{scopeKey}";

    // The keys a pipeline variable or group link in scope is found by.
    private static IEnumerable<string> ScopeKeys(PipelineScope scope) => scope switch
    {
        { Job: null, Stage: null } => [Everywhere, Exactly(scope)],
        { Job: null } => [Everywhere, Exactly(scope), Stage(scope.Stage)],
        { Stage: null } => [Everywhere, Exactly(scope), Job(scope.Job)],
        _ => [Everywhere, Exactly(scope), Stage(scope.Stage), Job(scope.Job)],
    };

    private IEnumerable<AzurePipelinesVariable> YamlBackward(string name, bool byEnvironmentName, string scopeKey, bool leaveOutSelf)
    {
        var exact = Backward((leaveOutSelf ? _byScopeNotSelf : _byScope)[Key(name, scopeKey)]);
        return byEnvironmentName
            ? exact.Concat(Backward(_byEnvironmentScope[Key(name, scopeKey)]).Where(v => !string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            : exact;
    }

    private static IEnumerable<T> Backward<T>(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            yield return list[i];
        }
    }

    // In file order, although the lookups keep each level together.
    private IEnumerable<AzurePipelinesVariable> Yaml(string name, bool byEnvironmentName, string scopeKey)
    {
        var exact = _byScope[Key(name, scopeKey)].OrderBy(v => v.Order);
        return byEnvironmentName
            ? exact.Concat(_byEnvironmentScope[Key(name, scopeKey)].Where(v => !string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.Order))
            : exact;
    }

    private IEnumerable<(AzureDevOpsVariable Variable, int Index)> GroupCandidates(string name, bool byEnvironmentName)
    {
        var exact = _groupsByName[name];
        return byEnvironmentName
            ? exact.Concat(_groupsByEnvironmentName[name].Where(g => !string.Equals(g.Variable.Name, name, StringComparison.OrdinalIgnoreCase))).OrderBy(g => g.Index)
            : exact;
    }

    private static int Level(PipelineScope scope) => scope.Job is not null ? 2 : scope.Stage is not null ? 1 : 0;

    // Whether a job in view gets what is set in scope, or view passes it on to its jobs.
    private static bool Visible(PipelineScope scope, PipelineScope view) => scope.Covers(view) || view.Covers(scope);

    private bool MaySecret(Candidate candidate) =>
        candidate.Variable is { } variable
            ? IsSensitive(variable)
            : SensitiveName().IsMatch(candidate.Yaml!.Name) || (AzureDevOpsTraceOptions.MacroTarget(candidate.Yaml.Value) is { } target && MaySecret(target));

    private bool MaySecret(string name) => _maySecret.Contains(name) || MaySecretItself(name);

    private bool MaySecretItself(string name) => SensitiveName().IsMatch(name) || _secretVariables.Contains(name);

    // The variables that may hold a secret, as far as the variable groups and pipeline files show: by name, because a
    // group variable with that name is sensitive, or because a pipeline file sets it, anywhere, from one that may be.
    // They are found all at once, by following the references back from the secret ones, so that no answer depends on
    // which setting is traced first.
    private HashSet<string> MaySecretNames()
    {
        var setFrom = options.Variables
            .Select(v => (v.Name, Target: AzureDevOpsTraceOptions.MacroTarget(v.Value)))
            .Where(r => r.Target is not null)
            .ToLookup(r => r.Target!, r => r.Name, StringComparer.OrdinalIgnoreCase);
        var secret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var name in options.Variables.Select(v => v.Name).Concat(setFrom.Select(g => g.Key)))
        {
            if (MaySecretItself(name) && secret.Add(name))
            {
                queue.Enqueue(name);
            }
        }

        while (queue.TryDequeue(out var name))
        {
            foreach (var setter in setFrom[name].Where(secret.Add))
            {
                queue.Enqueue(setter);
            }
        }

        return secret;
    }

    // The pipeline variables, group links and links are indexed once, and again only when a pipeline was added.
    private void Index()
    {
        if (_indexed == options.Version)
        {
            return;
        }

        // Lowest level first, so that looking at a key from the end looks at what wins first.
        var all = options.Variables;
        var ranked = all.OrderBy(v => Level(options.ScopeOf(v))).ToList();
        _byScope = ranked.SelectMany(v => ScopeKeys(options.ScopeOf(v)), (v, k) => (v, k)).ToLookup(p => Key(p.v.Name, p.k), p => p.v, StringComparer.OrdinalIgnoreCase);
        _byScopeNotSelf = ranked.Where(v => !string.Equals(AzureDevOpsTraceOptions.MacroTarget(v.Value), v.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(v => ScopeKeys(options.ScopeOf(v)), (v, k) => (v, k)).ToLookup(p => Key(p.v.Name, p.k), p => p.v, StringComparer.OrdinalIgnoreCase);
        _byEnvironmentScope = ranked.SelectMany(v => ScopeKeys(options.ScopeOf(v)), (v, k) => (v, k))
            .ToLookup(p => Key(AzureDevOpsVariableGroups.ToEnvironmentName(p.v.Name), p.k), p => p.v, StringComparer.OrdinalIgnoreCase);
        _groupLinks = options.GroupLinks.ToLookup(g => g.Group, StringComparer.OrdinalIgnoreCase);
        _groupLinksByScope = options.GroupLinks.SelectMany(g => ScopeKeys(options.ScopeOf(g)), (g, k) => (g, k)).ToLookup(p => GroupKey(p.g.Group, p.k), p => p.g, StringComparer.OrdinalIgnoreCase);
        _links = options.FileLinks().ToLookup(l => l.Mapping.Name, StringComparer.OrdinalIgnoreCase);
        _defined = [];
        foreach (var variable in all)
        {
            _defined.TryAdd((variable.File, variable.Line, variable.Name), variable);
        }

        _maySecret = MaySecretNames();
        _indexed = options.Version;
    }

    // A pipeline variable step is followed again for its reference, so it keeps the value seen where the trace started.
    private ProvenanceStep Step(Candidate candidate, ProvenanceStep layer, string how, bool inferred, string? seen)
    {
        var step = ToStep(candidate, layer, how, inferred);
        if (seen is not null && candidate.Yaml is not null)
        {
            _seen.AddOrUpdate(step, seen);
        }

        return step;
    }

    private ProvenanceStep ToStep(Candidate candidate, ProvenanceStep layer, string how, bool inferred)
    {
        var notes = new List<string> { how };
        string? value;
        if (candidate.Variable is { } variable)
        {
            if (variable.GroupType == AzureDevOpsGroupType.AzureKeyVault)
            {
                notes.Add($"the value comes from secret {variable.Name} in Azure Key Vault {variable.KeyVaultName ?? "(unknown vault)"}");
            }
            else if (variable.IsSecret)
            {
                notes.Add("Azure DevOps does not return secret values");
            }

            if (variable.IsSensitive && inferred)
            {
                notes.Add("secret variables reach scripts only when mapped in an env: block");
            }

            if (options.GroupLinks.Count > 0 && !_groupLinks.Contains(variable.Group))
            {
                notes.Add("no pipeline file read links this group");
            }

            value = variable.Value;
        }
        else
        {
            value = candidate.Yaml!.Value;
        }

        var macro = AzureDevOpsTraceOptions.MacroTarget(value) is not null || AzureDevOpsTraceOptions.MacroTarget(layer.Value) is not null;
        if (layer.Value is not null && value is not null && !macro && !candidate.Alternative && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the Azure DevOps value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        if (candidate.Variable is { } v)
        {
            return new ProvenanceStep
            {
                Kind = LayerKind.AzureDevOps,
                Source = $"Azure DevOps variable group {v.Group} {(v.IsSensitive ? "secret" : "variable")} {v.Name}",
                Location = Location(v),
                Value = v.Value,
                IsSensitive = IsSensitive(v) || candidate.Sensitive,
                IsInferred = inferred || candidate.Alternative,
                Note = string.Join("; ", notes),
                Reference = Reference(v),
            };
        }

        var yaml = candidate.Yaml!;
        return new ProvenanceStep
        {
            Kind = LayerKind.AzureDevOps,
            Source = $"Azure Pipelines variable {yaml.Name}{(candidate.Scope.IsPipeline ? string.Empty : $" ({candidate.Scope})")}",
            Location = $"{yaml.File} line {yaml.Line}",
            Value = yaml.Value,
            IsSensitive = SensitiveName().IsMatch(yaml.Name) || candidate.Sensitive,
            IsInferred = inferred || candidate.Inferred || candidate.Alternative,
            Note = string.Join("; ", notes),
            Reference = new AzurePipelinesVariableRef(yaml.File, yaml.Line, yaml.Name),
        };
    }

    // Azure DevOps hides a value only when it is marked secret, so a variable named like a secret is treated as secret too.
    private static bool IsSensitive(AzureDevOpsVariable variable) => variable.IsSensitive || SensitiveName().IsMatch(variable.Name);

    private static string Location(AzureDevOpsVariable variable) =>
        variable.GroupType == AzureDevOpsGroupType.AzureKeyVault && variable.KeyVaultName is not null ? variable.KeyVaultName : variable.Group;

    private AzureDevOpsVariableRef Reference(AzureDevOpsVariable variable) => new(groups.Project, variable.Group, variable.Name);

    // Scope: the stage or job it is set or linked in. Inferred: what a self reference leads to in the file. Alternative:
    // one of several that stages or jobs disagree on, which is inferred and never compared to the value seen here.
    // Sensitive: an alternative while another one may be secret.
    private sealed record Candidate(
        string Name,
        AzureDevOpsVariable? Variable,
        AzurePipelinesVariable? Yaml,
        int Order,
        int Index,
        PipelineScope Scope = default,
        bool Inferred = false,
        bool Alternative = false,
        bool Sensitive = false)
    {
        public string? Value => Variable is { } variable ? variable.Value : Yaml!.Value;
    }

    // One of the stages or jobs that may set a name: a pipeline file link in Scope, or, when Mapping is null, what Scope
    // itself gives scripts under that name.
    private readonly record struct Unit(AzurePipelinesMapping? Mapping, PipelineScope Scope);

    // A value the trace cannot see or compare, such as a secret's: the same only as itself.
    private sealed record Undecided(object Source);

    // The default rule a trace uses for configuration keys. A resolver is built without the trace, so it cannot see the
    // trace's own SensitiveKeyPattern.
    [GeneratedRegex(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();
}
