namespace Kirin_Tool.Cli;

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}

/// <summary>One documented command parameter (also feeds the MCP input schema).</summary>
internal sealed record CliParam(
    string Name,
    string Type,
    string Description,
    bool Required = false,
    bool Positional = false,
    IReadOnlyList<string>? Enum = null,
    string? Default = null);

internal sealed class ParsedArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = new();

    /// <summary>Options that are not registered anywhere; only reachable with <c>--lenient-args</c>.</summary>
    public IReadOnlyList<string> UnknownOptions => _unknown;
    private readonly List<string> _unknown = new();
    public void AddUnknown(string name)
    {
        if (!_unknown.Contains(name, StringComparer.OrdinalIgnoreCase)) _unknown.Add(name);
    }

    public IReadOnlyDictionary<string, string> Values => _values;
    public bool HasFlag(string name) => _flags.Contains(name);
    public bool HasValue(string name) => _values.ContainsKey(name);
    public string? Value(string name) => _values.TryGetValue(name, out string? value) ? value : null;
    public void SetFlag(string name) => _flags.Add(name);
    public void SetValue(string name, string value) => _values[name] = value;
}

internal static class ArgParser
{
    /// <summary>Options every command accepts, whatever its own <see cref="CliParam"/> list says.</summary>
    private static readonly string[] GlobalFlags = { "json", "yes", "help", "lenient-args" };

    /// <summary>
    /// Minimal, predictable parser: <c>--name value</c>, <c>--name=value</c>, short aliases
    /// (-o output, -y yes, -j json, -h help) and bare positionals.
    /// <para>
    /// An option that is neither a registered value nor a registered flag is a <b>usage error</b>
    /// (audit S12): a misspelled <c>--require-backup</c>/<c>--strict-names</c> used to be swallowed
    /// as a harmless flag, which silently turned a gate off. <c>--lenient-args</c> restores the old
    /// accept-anything behaviour and is reported back through
    /// <see cref="ParsedArgs.UnknownOptions"/> so the caller can warn.
    /// </para>
    /// </summary>
    /// <param name="flagOptions">Names the command declares as booleans (its <see cref="CliParam"/> flags).</param>
    public static ParsedArgs Parse(IReadOnlyList<string> args, IEnumerable<string> valueOptions, IEnumerable<string>? flagOptions = null)
    {
        var parsed = new ParsedArgs();
        var values = new HashSet<string>(valueOptions, StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        foreach (string flag in (flagOptions ?? Array.Empty<string>()).Concat(GlobalFlags)) known.Add(flag);

        // "kirintool flash-xml --help" must print usage even next to a misspelled option.
        bool helpRequested = args.Any(token => token is "--help" or "-h" or "--h" or "-help");

        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (token == "--")
            {
                for (int j = i + 1; j < args.Count; j++) parsed.Positionals.Add(args[j]);
                break;
            }

            if (token.Length > 1 && token[0] == '-')
            {
                string name = token.StartsWith("--", StringComparison.Ordinal) ? token[2..] : token[1..];
                string? inline = null;
                int equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    inline = name[(equals + 1)..];
                    name = name[..equals];
                }

                name = name.ToLowerInvariant() switch
                {
                    "o" => "output",
                    "y" => "yes",
                    "j" => "json",
                    "h" => "help",
                    _ => name,
                };

                if (!known.Contains(name)) parsed.AddUnknown(name);

                if (inline != null)
                {
                    parsed.SetValue(name, inline);
                    continue;
                }

                if (!values.Contains(name))
                {
                    parsed.SetFlag(name);
                    continue;
                }

                if (i + 1 >= args.Count)
                    throw new CliUsageException($"option --{name} requires a value");
                parsed.SetValue(name, args[++i]);
                continue;
            }

            parsed.Positionals.Add(token);
        }

        if (parsed.UnknownOptions.Count > 0 && !parsed.HasFlag("lenient-args") && !helpRequested)
        {
            string list = string.Join(", ", parsed.UnknownOptions.Select(option => "--" + option));
            throw new CliUsageException(
                $"unknown option{(parsed.UnknownOptions.Count > 1 ? "s" : string.Empty)} {list}; " +
                "a misspelled option used to be accepted as a flag, which can silently disable a gate. " +
                "Run with --help to see the accepted options, or pass --lenient-args to accept it as a flag anyway.");
        }

        return parsed;
    }
}
