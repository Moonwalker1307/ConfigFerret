using ConfigFerret.Fuzz;
using SharpFuzz;

// Usage:
//   ConfigFerret.Fuzz <target>                  run under libFuzzer (started by libfuzzer-dotnet)
//   ConfigFerret.Fuzz <target> <file|dir>...    replay inputs without libFuzzer, for example a crash file
//   ConfigFerret.Fuzz --list                    list the targets
//   ConfigFerret.Fuzz --instrument <dll>...     add SharpFuzz coverage instrumentation to assemblies, in place
if (args is ["--instrument", .. var assemblies])
{
    foreach (var assembly in assemblies)
    {
        var types = Fuzzer.Instrument(assembly, _ => true, Options.Value);
        Console.WriteLine($"Instrumented {types.Count()} types in {Path.GetFileName(assembly)}.");
    }

    return 0;
}

if (args.Length == 0 || args[0] is "--list" or "-h" or "--help")
{
    Console.WriteLine("Targets:");
    foreach (var (name, target) in Targets.All)
    {
        Console.WriteLine($"  {name,-14} {target.Description}");
    }

    return args.Length == 0 ? 2 : 0;
}

if (!Targets.All.TryGetValue(args[0], out var selected))
{
    Console.Error.WriteLine($"Unknown target '{args[0]}'. Run with --list to see the targets.");
    return 2;
}

if (args.Length == 1)
{
    Fuzzer.LibFuzzer.Run(span => selected.Run(span));
    return 0;
}

var files = args.Skip(1)
    .SelectMany<string, string>(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [p])
    .ToList();
var failures = 0;
foreach (var file in files)
{
    try
    {
        selected.Run(File.ReadAllBytes(file));
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {file}");
        Console.Error.WriteLine(ex);
    }
}

Console.WriteLine($"{files.Count - failures} of {files.Count} inputs passed for target '{args[0]}'.");
return failures == 0 ? 0 : 1;
