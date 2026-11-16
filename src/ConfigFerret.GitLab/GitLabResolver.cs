using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ConfigFerret.Sources;

namespace ConfigFerret.GitLab;

/// <summary>Links environment variables, Kubernetes env entries and ConfigMap/Secret keys to GitLab CI/CD variables.</summary>
internal sealed partial class GitLabResolver(GitLabVariables variables, GitLabTraceOptions options) : IOriginResolver
{
    private const string Workflow = "workflow";

    // How far what a variable comes to is followed, through references and through the definitions of each one, before
    // the trace takes the values it compares to differ.
    private const int MaxReferences = 8;
    private const int MaxDefinitions = 32;

    // The most CI file entries one trace step looks at: the tracer keeps no more upstream steps than this per setting.
    private const int MaxSteps = 1_000;

    private readonly List<string> _fileWarnings = options.ReadConfiguredCiFiles();
    private readonly HashSet<string> _scopeWarnings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _jobWarnings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secretVariables = variables.Entries.Where(IsSensitive).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<GitLabVariable>> _effective = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ProvenanceStep, string> _seen = new();
    private ILookup<string, GitLabCiVariable> _byName = Array.Empty<GitLabCiVariable>().ToLookup(v => v.Name);
    private ILookup<string, GitLabCiVariable> _common = Array.Empty<GitLabCiVariable>().ToLookup(v => v.Name);
    private ILookup<string, GitLabCiVariable> _jobs = Array.Empty<GitLabCiVariable>().ToLookup(v => v.Name);
    private ILookup<(string Name, string? Job), GitLabCiVariable> _byJob = Array.Empty<GitLabCiVariable>().ToLookup(v => (v.Name, v.Job));
    private Dictionary<(string File, int Line, string Name), GitLabCiVariable> _defined = [];
    private ILookup<string, (GitLabMapping Mapping, string? Job)> _links = Array.Empty<(GitLabMapping Mapping, string? Job)>().ToLookup(l => l.Mapping.Name);
    private HashSet<string> _leftOut = [];
    private HashSet<string> _maySecret = [];
    private (int Version, string? Environment)? _indexed;
    private int _room;
    private bool _cut;
    private bool _warned;

    /// <summary>How many CI file entries the resolver looked at, so a test can check that the work grows with the file.</summary>
    internal long Examined { get; private set; }

    /// <summary>One entry per variable and environment scope, matching the steps' references.</summary>
    public IEnumerable<SourceEntry> Entries() => variables.Entries.Select(v => new SourceEntry(
        v.Key,
        Reference(v),
        IsSensitive(v),
        Detail(v),
        Location(v)));

    public IReadOnlyList<ProvenanceStep> Resolve(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_warned)
        {
            _warned = true;
            foreach (var warning in variables.Warnings.Concat(_fileWarnings))
            {
                diagnostics.Warn(warning);
            }
        }

        Index();
        _room = MaxSteps;
        _cut = false;
        var found = Find(layer, diagnostics);
        if (_cut && (layer.Reference is GitLabCiVariableRef cut ? cut.Name : LayerNames.Of(layer)) is { } subject && _jobWarnings.Add("\0" + subject))
        {
            diagnostics.Warn(string.Create(CultureInfo.InvariantCulture, $"{subject} is linked or set in more GitLab CI jobs and definitions than the trace follows for one step ({MaxSteps:N0}), so not all of them were followed; the steps listed are marked inferred and sensitive."));
        }

        return found;
    }

    private List<ProvenanceStep> Find(ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        if (layer.Reference is GitLabCiVariableRef ciRef)
        {
            // A CI file variable whose whole value is $OTHER gets its value from OTHER, as the job it is in sees it.
            if (!_defined.TryGetValue((ciRef.File, ciRef.Line, ciRef.Name), out var defined) || GitLabTraceOptions.ReferenceTarget(defined.Value) is not { } target)
            {
                return [];
            }

            // The value seen where the trace started, when this step came from such a trace, passes through the reference.
            var seen = _seen.TryGetValue(layer, out var started) ? started : null;
            var from = $"{defined.File} line {defined.Line} sets {defined.Name} from ${target}";
            var chain = Chain(target, View(defined.Job), defined, seen, diagnostics);
            return chain.Count == 0 ? MaskedInOtherScopes(target, from) : chain.Select(c => Step(c, layer, from, inferred: false, seen)).ToList();
        }

        if (layer.Reference is ExternalSecretRef or GitLabVariableRef || LayerNames.Of(layer) is not { } name)
        {
            return [];
        }

        if (options.ExplicitLink(name) is { } explicitLink)
        {
            return Linked(name, explicitLink, null, layer, diagnostics);
        }

        var units = Units(name);
        if (units.Count == 1)
        {
            return Linked(name, units[0].Mapping!, units[0].Job, layer, diagnostics);
        }

        if (units.Count > 1)
        {
            return Alternatives(name, units, layer, diagnostics);
        }

        return options.MatchByName
            ? Chain(name, null, null, layer.Value, diagnostics).Select(c => Step(c, layer, "matched by name", inferred: true, layer.Value)).ToList()
            : [];
    }

    private List<ProvenanceStep> Linked(string name, GitLabMapping mapping, string? job, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        var linked = Chain(mapping.Key, View(job), null, layer.Value, diagnostics);
        var how = How(mapping);
        if (linked.Count == 0)
        {
            WarnMissing(name, mapping, diagnostics);
            return MaskedInOtherScopes(mapping.Key, how);
        }

        return linked.Select(c => Step(c, layer, how, inferred: false, layer.Value)).ToList();
    }

    // Several jobs may set name. Each one is followed as its own job sees it; when they all come to the same value it
    // is one certain link, and otherwise each one is an inferred alternative and a warning names the jobs. Every
    // alternative is then sensitive when any of them may be, since the tracer redacts by the one it marks effective.
    private List<ProvenanceStep> Alternatives(string name, List<Unit> units, ProvenanceStep layer, TraceDiagnostics diagnostics)
    {
        // Past the bound on what one step looks at, the jobs not followed yet are left out.
        var followed = new List<(Unit Unit, List<Candidate> Chain)>();
        foreach (var unit in units.TakeWhile(_ => !_cut))
        {
            followed.Add((unit, Follow(name, unit, layer.Value, diagnostics)));
        }

        var ends = followed.Select(f => End(f.Chain, View(f.Unit.Job))).ToList();
        var sensitive = _cut || followed.Any(f => MaySecret(f.Unit.Mapping?.Key ?? name) || f.Chain.Any(c => c.Variable is { } v ? IsSensitive(v) : MaySecret(c.File!)));
        if (!_cut && ends.All(e => e is not null && Equals(e, ends[0])))
        {
            // The links that agree may still include one from a variable that may be secret.
            return followed[0].Chain.Select(c => Step(c with { Sensitive = c.Sensitive || sensitive }, layer, How(name, followed[0].Unit), inferred: false, layer.Value)).ToList();
        }

        var what = $"{(units.Any(u => u.Mapping is null) ? "link or set" : "link")} {name} to {(!_cut && ends.All(Known) ? "different values" : "values that cannot be compared")}";
        WarnJobs(what, units.Select(u => u.Job ?? "(top level)"), diagnostics);
        var steps = new List<(bool Matches, List<ProvenanceStep> Steps)>();
        for (var i = 0; i < followed.Count; i++)
        {
            var (unit, chain) = followed[i];
            var how = How(name, unit);
            if (chain.Count == 0)
            {
                WarnMissing(name, unit.Mapping!, diagnostics);
            }

            var found = chain.Count == 0
                ? MaskedInOtherScopes(unit.Mapping!.Key, how).Select(s => s with { IsInferred = true }).ToList()
                : chain.Select(c => Step(c with { Alternative = true, Sensitive = sensitive }, layer, how, inferred: true, layer.Value)).ToList();
            var value = ends[i] as string ?? (chain.Count == 0 ? null : chain[^1].Value);
            steps.Add((layer.Value is not null && string.Equals(value, layer.Value, StringComparison.Ordinal), found));
        }

        // The tracer marks the last step effective, so one that comes to the value seen here goes last.
        return steps.OrderBy(s => s.Matches).SelectMany(s => s.Steps).ToList();
    }

    private static string How(GitLabMapping mapping) =>
        mapping.Where is null ? "linked explicitly" : $"{mapping.Where} sets {mapping.Name} from ${mapping.Key}";

    private static string How(string name, Unit unit) => unit.Mapping is { } mapping ? How(mapping) : "matched by name";

    private static void WarnMissing(string name, GitLabMapping mapping, TraceDiagnostics diagnostics) =>
        diagnostics.Warn($"{name} is linked to GitLab variable {mapping.Key}{(mapping.Where is null ? string.Empty : $" ({mapping.Where})")}, but no such variable applies.");

    // What may set name in the jobs that count: each CI file link, in jobs, hidden jobs and outside jobs alike, and, once
    // there is a link, each job that sets name in variables: and has no link for it.
    private List<Unit> Units(string name)
    {
        var units = new List<Unit>();
        foreach (var (mapping, job) in _links[name])
        {
            Examined++;
            if (Counts(job))
            {
                units.Add(new Unit(mapping, job));
            }
        }

        if (units.Count == 0)
        {
            return units;
        }

        var linked = units.Select(u => u.Job).ToHashSet();
        foreach (var variable in _byName[name])
        {
            Examined++;
            if (Counts(variable.Job) && linked.Add(variable.Job))
            {
                units.Add(new Unit(null, variable.Job));
            }
        }

        return units;
    }

    // A link is followed as its job sees it. A job that sets name itself gets its own definitions and the GitLab
    // variables, which win over them.
    private List<Candidate> Follow(string name, Unit unit, string? seen, TraceDiagnostics diagnostics)
    {
        if (unit.Mapping is { } mapping)
        {
            return Chain(mapping.Key, View(unit.Job), null, seen, diagnostics);
        }

        var own = new List<Candidate>();
        foreach (var variable in Backward(_byJob[(name, unit.Job)]))
        {
            Examined++;
            if (--_room < 0)
            {
                _cut = true;
                break;
            }

            own.Add(new Candidate(null, variable));
        }

        own.Reverse();
        return own.Concat(Effective(name).Select(v => new Candidate(v, null))).ToList();
    }

    // What a chain comes to, to compare it with others: the GitLab variable that wins, or the CI file variable that wins
    // as its job sees it. Null when that cannot be told, which counts as different from everything.
    private object? End(List<Candidate> chain, string? view) =>
        chain.Count == 0 || chain[^1].Alternative ? null : (object?)chain[^1].Variable ?? Comes(chain[^1].File!, view, 0);

    // The text of a CI file variable that refers to no other variable, or what its whole-value reference comes to in the
    // job view (its own job when view is null). Null when that cannot be told cheaply: a value with a reference inside it
    // such as db-$SUFFIX, a variable that refers to itself, or a reference that leads to several definitions.
    private object? Comes(GitLabCiVariable variable, string? view, int depth)
    {
        // A value written in a variable that may be secret is never taken to agree with another. A reference is
        // followed whatever its name: the GitLab variable it comes to is the same only as itself, and links that agree
        // stay sensitive when any of them may be.
        if (GitLabTraceOptions.ReferenceTarget(variable.Value) is not { } target)
        {
            return MaySecretItself(variable.Name) || variable.Value.Contains('$', StringComparison.Ordinal) ? null : variable.Value;
        }

        return depth >= MaxReferences || string.Equals(target, variable.Name, StringComparison.Ordinal)
            ? null
            : ComesTo(target, view ?? View(variable.Job), variable, depth + 1);
    }

    private object? ComesTo(string key, string? view, GitLabCiVariable except, int depth)
    {
        if (Effective(key) is { Count: > 0 } effective)
        {
            return effective[^1];
        }

        if (Definitions(key, view, except, MaxDefinitions) is not { Count: > 0 } definitions)
        {
            return null;
        }

        var top = definitions.Max(v => Rank(v.Job));
        var winners = definitions.Where(v => Rank(v.Job) == top).ToList();
        return winners.Any(v => v.Job != winners[0].Job || v.Job == Workflow) ? null : Comes(winners[^1], view, depth);
    }

    // A link to a variable that is masked but set only for environment scopes that do not apply, for example because
    // Environment is not set: the layer may still hold the masked value, so keep a step that makes the layer sensitive.
    private List<ProvenanceStep> MaskedInOtherScopes(string key, string how)
    {
        if (!variables.Find(key).Any(v => v.IsSensitive))
        {
            return [];
        }

        return
        [
            new ProvenanceStep
            {
                Kind = LayerKind.GitLabCi,
                Source = $"GitLab variable {key}",
                IsSensitive = true,
                Note = $"{how}; masked, and set only for environment scopes that do not apply",
            },
        ];
    }

    // Lowest priority first, as GitLab documents it: CI file variables (global, then job), then instance, then each
    // group from the outermost in, then the project. A job gets the global variables and its own, never another job's,
    // so view is the job whose variables count, or null for every job that counts when the trace cannot tell which.
    // What a self reference leads to in the file is only inferred, since it is not certain which definition GitLab
    // expands the reference to.
    private List<Candidate> Chain(string key, string? view, GitLabCiVariable? except, string? seen, TraceDiagnostics diagnostics)
    {
        var self = except is not null && string.Equals(except.Name, key, StringComparison.Ordinal);
        var definitions = Definitions(key, view, except)!;
        var alternatives = Disagreeing(key, definitions, view, diagnostics);

        // A workflow rule's variables apply only when the rule matches, so they and the global ones they would replace are
        // alternatives.
        if (definitions.Any(v => v.Job == Workflow))
        {
            alternatives.UnionWith(definitions.Where(v => GitLabTraceOptions.IsShared(v.Job)));
        }

        var sensitive = alternatives.Any(MaySecret);

        // The tracer marks the last step effective, so among alternatives one with the value seen here goes last.
        var fromFiles = definitions
            .Select(v => alternatives.Contains(v)
                ? new Candidate(null, v, self, Alternative: true, Sensitive: sensitive)
                : new Candidate(null, v, self))
            .OrderBy(c => Rank(c.File!.Job))
            .ThenBy(c => c.Alternative && seen is not null && string.Equals(c.File!.Value, seen, StringComparison.Ordinal));
        var effective = Effective(key);
        if (effective.Count == 0 && !_scopeWarnings.Contains(key) && variables.Find(key) is { Count: > 0 } scoped)
        {
            _scopeWarnings.Add(key);
            var scopes = string.Join(", ", scoped.Select(v => v.EnvironmentScope).Distinct(StringComparer.Ordinal));
            diagnostics.Warn($"GitLab variable {key} is set only for environment scopes {scopes}, and none applies to {(options.Environment is null ? "a job with no environment" : $"environment {options.Environment}")}.");
        }

        var chain = fromFiles.Concat(effective.Select(v => new Candidate(v, null)));

        // A chain cut short at the bound may have lost the one that wins, and a secret with it.
        return (_cut ? chain.Select(c => c with { Alternative = true, Sensitive = true }) : chain).ToList();
    }

    // The CI file definitions of key that the job view sees, or that every job that counts sees when view is null: the
    // global ones, the hidden jobs' (extends: is not read, so they count for every job) and the job's own. When except
    // refers to key, a definition of key that refers back to key is left out, since it cannot supply a value: VERSION:
    // $VERSION in every job would otherwise lead from each job to all the others, for each level followed. Null when
    // there are more than limit to look at.
    private List<GitLabCiVariable>? Definitions(string key, string? view, GitLabCiVariable? except, int? limit = null)
    {
        // Looked at in the order they win, from the end: the jobs' own, then the hidden jobs', then the global ones, so
        // that the bound keeps the ones that win, as the tracer's own bound keeps the end of a chain.
        IEnumerable<GitLabCiVariable> jobs = view is null ? Backward(_jobs[key]) : GitLabTraceOptions.IsHidden(view) ? [] : Backward(_byJob[(key, view)]);
        var definitions = new List<GitLabCiVariable>();
        var looked = 0;
        foreach (var variable in jobs.Concat(Backward(_common[key])))
        {
            Examined++;
            if (++looked > limit)
            {
                return null;
            }

            if (ReferenceEquals(variable, except) || !Counts(variable.Job)
                || (except is not null && GitLabTraceOptions.ReferenceTarget(variable.Value) == key))
            {
                continue;
            }

            // Without a limit of its own, what is kept counts toward the bound for the step.
            if (limit is null && --_room < 0)
            {
                _cut = true;
                break;
            }

            definitions.Add(variable);
        }

        definitions.Reverse();
        return definitions;
    }

    private static IEnumerable<T> Backward<T>(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            yield return list[i];
        }
    }

    // The definitions that are alternatives: when the trace cannot tell which job it is about, the jobs that set key
    // to values that come to different things (or, when no job sets it, the hidden jobs that do). A job's own
    // definition wins over hidden jobs' in that job. A warning names the jobs.
    private HashSet<GitLabCiVariable> Disagreeing(string key, List<GitLabCiVariable> definitions, string? view, TraceDiagnostics diagnostics)
    {
        var none = new HashSet<GitLabCiVariable>(ReferenceEqualityComparer.Instance);
        if (view is not null && definitions.Any(v => v.Job == view))
        {
            return none;
        }

        var jobs = definitions.Where(v => !GitLabTraceOptions.IsShared(v.Job) && !GitLabTraceOptions.IsHidden(v.Job)).ToList();
        var pool = jobs.Count > 0 ? jobs : definitions.Where(v => GitLabTraceOptions.IsHidden(v.Job)).ToList();
        var last = pool.GroupBy(v => v.Job).Select(g => g.Last()).ToList();
        var ends = last.Select(v => Comes(v, View(v.Job), 0)).ToList();
        if (last.Count < 2 || ends.All(e => e is not null && Equals(e, ends[0])))
        {
            return none;
        }

        WarnJobs(ends.All(Known) ? $"set {key} differently" : $"set {key} to values that cannot be compared", last.Select(v => v.Job!), diagnostics);
        none.UnionWith(pool);
        return none;
    }

    private void WarnJobs(string what, IEnumerable<string> jobs, TraceDiagnostics diagnostics)
    {
        if (!_jobWarnings.Add(what))
        {
            return;
        }

        var names = jobs.Distinct(StringComparer.Ordinal).ToList();
        var list = string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : string.Empty);
        var hint = options.Environment is null ? " Set Environment to leave out the jobs for other environments." : string.Empty;
        diagnostics.Warn($"GitLab CI jobs {list} {what}, and the trace cannot tell which job deployed this, so their steps are marked inferred.{hint}");
    }

    // Whether a CI file variable may hold a secret: by its name, or by what its whole-value reference leads to.
    private bool MaySecret(GitLabCiVariable variable) =>
        SensitiveName().IsMatch(variable.Name) || (GitLabTraceOptions.ReferenceTarget(variable.Value) is { } target && MaySecret(target));

    private bool MaySecret(string key) => _maySecret.Contains(key) || MaySecretItself(key);

    private bool MaySecretItself(string key) => SensitiveName().IsMatch(key) || _secretVariables.Contains(key);

    // The variables that may hold a secret, as far as the GitLab variables and the CI files show: by name, because a
    // GitLab variable with that key is sensitive, or because a CI file sets it, in any job, from one that may be. They
    // are found all at once, by following the references back from the secret ones, so that no answer depends on which
    // setting is traced first.
    private HashSet<string> MaySecretKeys()
    {
        var setFrom = options.Variables
            .Select(v => (v.Name, Target: GitLabTraceOptions.ReferenceTarget(v.Value)))
            .Where(r => r.Target is not null)
            .ToLookup(r => r.Target!, r => r.Name, StringComparer.Ordinal);
        var secret = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var key in options.Variables.Select(v => v.Name).Concat(setFrom.Select(g => g.Key)))
        {
            if (MaySecretItself(key) && secret.Add(key))
            {
                queue.Enqueue(key);
            }
        }

        while (queue.TryDequeue(out var key))
        {
            foreach (var name in setFrom[key].Where(secret.Add))
            {
                queue.Enqueue(name);
            }
        }

        return secret;
    }

    // What the trace counts and finds is read once, and again only when a CI file was added or Environment changed.
    private void Index()
    {
        if (_indexed == (options.Version, options.Environment))
        {
            return;
        }

        var all = options.Variables;
        _byName = all.ToLookup(v => v.Name, StringComparer.Ordinal);
        _byJob = all.ToLookup(v => (v.Name, v.Job));
        _leftOut = options.JobsForOtherEnvironments(options.Environment).ToHashSet(StringComparer.Ordinal);

        // The global and hidden definitions with the hidden ones last, and the jobs' that count, so that looking at each
        // from the end looks at them in the order they win.
        _common = all.Where(v => Rank(v.Job) < 2).OrderBy(v => Rank(v.Job)).ToLookup(v => v.Name, StringComparer.Ordinal);
        _jobs = all.Where(v => Rank(v.Job) == 2 && Counts(v.Job)).ToLookup(v => v.Name, StringComparer.Ordinal);
        _defined = [];
        foreach (var variable in all)
        {
            _defined.TryAdd((variable.File, variable.Line, variable.Name), variable);
        }

        _links = options.FileLinks().ToLookup(l => l.Mapping.Name, StringComparer.Ordinal);
        _effective.Clear();
        _maySecret = MaySecretKeys();
        _indexed = (options.Version, options.Environment);
    }

    private IReadOnlyList<GitLabVariable> Effective(string key)
    {
        if (!_effective.TryGetValue(key, out var effective))
        {
            effective = variables.Effective(key, options.Environment);
            _effective[key] = effective;
        }

        return effective;
    }

    // Whether what job sets counts: everything outside jobs and in hidden jobs does, and a job unless its own
    // environment: names another environment than Environment.
    private bool Counts(string? job) => job is null || !_leftOut.Contains(job);

    // The job a definition or link in job sees, or null for every job when it is outside jobs.
    private static string? View(string? job) => GitLabTraceOptions.IsShared(job) ? null : job;

    private static int Rank(string? job) => GitLabTraceOptions.IsShared(job) ? 0 : GitLabTraceOptions.IsHidden(job) ? 1 : 2;

    // Whether what a chain comes to is a value the trace compared: never one it could not tell, nor a secret it cannot see.
    private static bool Known(object? end) => end is string || end is GitLabVariable { Value: not null } variable && !IsSensitive(variable);

    // A CI file step is followed again for its reference, so it keeps the value seen where the trace started.
    private ProvenanceStep Step(Candidate candidate, ProvenanceStep layer, string how, bool inferred, string? seen)
    {
        var step = ToStep(candidate, layer, how, inferred);
        if (seen is not null && candidate.File is not null)
        {
            _seen.AddOrUpdate(step, seen);
        }

        return step;
    }

    private static ProvenanceStep ToStep(Candidate candidate, ProvenanceStep layer, string how, bool inferred)
    {
        inferred |= candidate.Inferred || candidate.Alternative;
        var notes = new List<string> { how };
        string? value;
        if (candidate.Variable is { } variable)
        {
            if (variable.EnvironmentScope != "*")
            {
                notes.Add($"environment scope {variable.EnvironmentScope}");
            }

            if (variable.IsHidden)
            {
                notes.Add("hidden: GitLab does not return its value");
            }
            else if (variable.IsMasked)
            {
                notes.Add(variable.Value is null ? "masked: its value was not read" : "masked");
            }

            if (variable.IsProtected)
            {
                notes.Add("protected: only pipelines on protected branches and tags get it");
            }

            if (variable.IsFile)
            {
                notes.Add("a file variable: the job gets the path of a temporary file that holds the value");
            }

            value = variable.Value;
        }
        else
        {
            value = candidate.File!.Value;
        }

        var reference = GitLabTraceOptions.ReferenceTarget(value) is not null || GitLabTraceOptions.ReferenceTarget(layer.Value) is not null;
        if (layer.Value is not null && value is not null && !reference && !candidate.Alternative && candidate.Variable is not { IsFile: true }
            && !string.Equals(layer.Value, value, StringComparison.Ordinal))
        {
            // Never print either value here: notes are not redacted.
            notes.Add("the GitLab value differs from the value seen here, so the deployment may be out of date or the value was transformed");
        }

        if (candidate.Variable is { } v)
        {
            return new ProvenanceStep
            {
                Kind = LayerKind.GitLabCi,
                Source = $"GitLab {GitLabVariables.Describe(v.Level)} variable {v.Key}",
                Location = Location(v),
                Value = v.Value,
                IsSensitive = IsSensitive(v) || candidate.Sensitive,
                IsInferred = inferred,
                Note = string.Join("; ", notes),
                Reference = Reference(v),
            };
        }

        var file = candidate.File!;
        return new ProvenanceStep
        {
            Kind = LayerKind.GitLabCi,
            Source = $"GitLab CI variable {file.Name}{(file.Job is null ? string.Empty : $" (job {file.Job})")}",
            Location = $"{file.File} line {file.Line}",
            Value = file.Value,
            IsSensitive = SensitiveName().IsMatch(file.Name) || candidate.Sensitive,
            IsInferred = inferred,
            Note = string.Join("; ", notes),
            Reference = new GitLabCiVariableRef(file.File, file.Line, file.Name),
        };
    }

    // GitLab masks a value only when asked to and cannot mask one with several lines, so a variable named like a secret
    // and a file variable, which often holds a kubeconfig or a key, are treated as secret too.
    private static bool IsSensitive(GitLabVariable variable) => variable.IsSensitive || variable.IsFile || SensitiveName().IsMatch(variable.Key);

    private static string Location(GitLabVariable variable) =>
        variable.EnvironmentScope == "*" ? variable.Owner : $"{variable.Owner}, environment {variable.EnvironmentScope}";

    private static string Detail(GitLabVariable variable)
    {
        var parts = new List<string> { $"{GitLabVariables.Describe(variable.Level)} variable", $"environment scope {variable.EnvironmentScope}" };
        parts.AddRange(new[] { (variable.IsMasked, "masked"), (variable.IsHidden, "hidden"), (variable.IsProtected, "protected"), (variable.IsFile, "file") }
            .Where(f => f.Item1)
            .Select(f => f.Item2));
        return string.Join(", ", parts);
    }

    private static GitLabVariableRef Reference(GitLabVariable variable) => new(variable.Level, variable.Owner, variable.Key, variable.EnvironmentScope);

    // Inferred: what a self reference leads to in the file. Alternative: one of several that jobs disagree on, which is
    // inferred and never compared to the value seen here. Sensitive: an alternative while another one may be secret.
    private sealed record Candidate(GitLabVariable? Variable, GitLabCiVariable? File, bool Inferred = false, bool Alternative = false, bool Sensitive = false)
    {
        public string? Value => Variable is { } variable ? variable.Value : File!.Value;
    }

    // One of the jobs that may set a name: a CI file link in Job, or, when Mapping is null, Job's own definitions.
    private readonly record struct Unit(GitLabMapping? Mapping, string? Job);

    // The default rule a trace uses for configuration keys. A resolver is built without the trace, so it cannot see the
    // trace's own SensitiveKeyPattern.
    [GeneratedRegex(TraceOptions.DefaultSensitiveKeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveName();
}
