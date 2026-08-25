namespace Lanshu.Presenter.Cli;

/// <summary>Minimal GNU-style parser: --key value, --flag, and positional arguments.</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();

    public CommandLine(IEnumerable<string> arguments)
    {
        string? pending = null;

        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    Add(pending, "true");
                }

                var body = argument[2..];
                var equals = body.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    Add(body[..equals], body[(equals + 1)..]);
                    pending = null;
                }
                else
                {
                    pending = body;
                }

                continue;
            }

            if (pending is not null)
            {
                Add(pending, argument);
                pending = null;
                continue;
            }

            _positional.Add(argument);
        }

        if (pending is not null)
        {
            Add(pending, "true");
        }
    }

    private void Add(string key, string value)
    {
        if (!_options.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _options[key] = list;
        }

        list.Add(value);
    }

    public IReadOnlyList<string> Positional => _positional;

    public string? Value(string key) => _options.TryGetValue(key, out var list) ? list[^1] : null;

    public IReadOnlyList<string> Values(string key) =>
        _options.TryGetValue(key, out var list) ? list : Array.Empty<string>();

    public bool Flag(string key)
    {
        var value = Value(key);
        return value is not null && value is not ("false" or "0" or "no");
    }

    public double Number(string key, double fallback) =>
        double.TryParse(Value(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public int Integer(string key, int fallback) =>
        int.TryParse(Value(key), out var parsed) ? parsed : fallback;

    public bool Has(string key) => _options.ContainsKey(key);
}
