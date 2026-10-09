namespace ArLegacyPostingReconciliation;

/// <summary>
/// The tool's command line: a command, then <c>--name value</c> options and <c>--switch</c>es.
/// Names are exact and lower-case. An unknown, mis-cased, repeated or value-less option, or a
/// stray argument, is an error with a message saying what is allowed: a typo must never be
/// ignored (for example <c>--Execute</c> silently running a dry-run, or the reverse).
/// </summary>
public sealed class CommandLine
{
    private static readonly Dictionary<string, (string[] Options, string[] Switches)> Commands = new(StringComparer.Ordinal)
    {
        ["hash"] = (["csv"], []),
        ["list"] = (["out", "tenant", "confirm-database"], []),
        ["reconcile"] = (["csv", "log", "sha256", "operator", "confirm-database", "tenant"], ["execute"]),
    };

    public string Command { get; }
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _switches;

    private CommandLine(string command, Dictionary<string, string> options, HashSet<string> switches)
    {
        Command = command;
        _options = options;
        _switches = switches;
    }

    public string? this[string name] => _options.TryGetValue(name, out var value) ? value : null;
    public bool Has(string @switch) => _switches.Contains(@switch);

    public string Required(string name) =>
        string.IsNullOrWhiteSpace(this[name]) ? throw new ArgumentException($"--{name} is required for '{Command}'.") : this[name]!;

    public IReadOnlyCollection<string> List(string name) =>
        (this[name] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <exception cref="ArgumentException">The arguments are not valid; the message says why.</exception>
    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("A command is required: hash, list or reconcile.");
        var command = args[0];
        if (!Commands.TryGetValue(command, out var allowed))
        {
            var hint = Commands.Keys.FirstOrDefault(k => string.Equals(k, command, StringComparison.OrdinalIgnoreCase));
            throw new ArgumentException(hint != null
                ? $"Unknown command '{command}'; commands are lower-case: did you mean '{hint}'?"
                : $"Unknown command '{command}'; commands are hash, list and reconcile.");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                throw new ArgumentException($"Unexpected argument '{arg}': options are written --name value.");
            var name = arg[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                inline = name[(eq + 1)..];
                name = name[..eq];
            }

            var isOption = allowed.Options.Contains(name);
            var isSwitch = allowed.Switches.Contains(name);
            if (!isOption && !isSwitch)
            {
                var all = allowed.Options.Concat(allowed.Switches).ToList();
                var hint = all.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
                throw new ArgumentException(hint != null
                    ? $"Unknown option --{name}; option names are lower-case: did you mean --{hint}?"
                    : $"Unknown option --{name} for '{command}'. Allowed: {string.Join(", ", all.Select(a => "--" + a))}." +
                      (name.StartsWith("MongoDb", StringComparison.OrdinalIgnoreCase)
                          ? " Database settings come from the environment (MongoDb__ConnectionString, MongoDb__DatabaseName, MongoDb__UseTenantScoping) or appsettings.json."
                          : string.Empty));
            }
            if (options.ContainsKey(name) || switches.Contains(name))
                throw new ArgumentException($"--{name} is given more than once.");

            if (isSwitch)
            {
                if (inline != null)
                    throw new ArgumentException($"--{name} takes no value.");
                switches.Add(name);
                continue;
            }

            var value = inline;
            if (value == null)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"--{name} needs a value.");
                value = args[++i];
            }
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"--{name} needs a value.");
            options[name] = value;
        }
        return new CommandLine(command, options, switches);
    }
}
