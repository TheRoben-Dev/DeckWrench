using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeckWrench.Core.Launch;

public enum PresetKind
{
    Env,
    Wrapper,
    Arg,
}

/// <summary>A ready-made launch option (data/launch-presets.json).</summary>
public sealed record LaunchPreset
{
    public required string Id { get; init; }
    public string Category { get; init; } = "";
    public PresetKind Kind { get; init; }
    public string? Key { get; init; }
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";

    /// <summary>Presets in the same group replace each other (DX11 / DX12, frame caps).</summary>
    public string? Group { get; init; }

    /// <summary>The text it adds: "KEY=VALUE", a wrapper command or a game argument.</summary>
    public string Token => Kind == PresetKind.Env ? $"{Key}={Value}" : Value;

    private static IReadOnlyList<LaunchPreset>? _all;

    public static IReadOnlyList<LaunchPreset> All => _all ??= Load();

    private static IReadOnlyList<LaunchPreset> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("launch-presets.json")
            ?? throw new InvalidOperationException("launch-presets.json is missing from the build.");
        using var doc = JsonDocument.Parse(s);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        return doc.RootElement.GetProperty("presets").Deserialize<List<LaunchPreset>>(opts)!;
    }

    public static LaunchPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// A game's Steam launch options split into parts: environment variables, wrapper commands, then
/// %command%, then the game's own arguments ("PROTON_LOG=1 gamemoderun %command% -dx11").
/// Without %command%, everything is a game argument (Steam appends it). Tokens keep the exact
/// text the player typed (quotes included), so parsing and rebuilding never changes their options.
/// </summary>
public sealed partial class LaunchOptions
{
    public const string CommandToken = "%command%";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvToken();

    public List<string> Env { get; } = new();
    public List<string> Wrappers { get; } = new();
    public List<string> Args { get; } = new();

    /// <summary>True when the text had %command% (or needs it: anything before the game).</summary>
    public bool HasCommand { get; private set; }

    public static LaunchOptions Parse(string? text)
    {
        var o = new LaunchOptions();
        var tokens = Tokenize(text ?? "");
        var cmd = tokens.IndexOf(CommandToken);
        if (cmd < 0)
        {
            o.Args.AddRange(tokens);
            return o;
        }
        o.HasCommand = true;
        var before = tokens.Take(cmd).ToList();
        var i = 0;
        // Leading KEY=VALUE pairs are environment; the rest before %command% are wrappers
        // (a wrapper's own KEY=VALUE arguments stay with the wrapper).
        while (i < before.Count && EnvToken().IsMatch(before[i]))
        {
            o.Env.Add(before[i++]);
        }
        o.Wrappers.AddRange(before.Skip(i));
        o.Args.AddRange(tokens.Skip(cmd + 1));
        return o;
    }

    /// <summary>Splits on whitespace outside quotes; quotes stay in the token.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }
        return tokens;
    }

    public override string ToString()
    {
        if (Env.Count == 0 && Wrappers.Count == 0 && !HasCommand)
        {
            return string.Join(' ', Args);
        }
        return string.Join(' ', Env.Concat(Wrappers).Append(CommandToken).Concat(Args));
    }

    private static string EnvKey(string token) => token[..token.IndexOf('=')];

    /// <summary>True when the options contain the preset exactly (same key and value).</summary>
    public bool Has(LaunchPreset p) => p.Kind switch
    {
        PresetKind.Env => Env.Contains(p.Token),
        PresetKind.Wrapper => Wrappers.Contains(p.Value),
        _ => Args.Contains(p.Value),
    };

    /// <summary>Adds the preset, replacing the same variable or another preset of its group.</summary>
    public void Add(LaunchPreset p)
    {
        foreach (var other in LaunchPreset.All.Where(x => x.Group != null && x.Group == p.Group && x.Id != p.Id))
        {
            Remove(other);
        }
        switch (p.Kind)
        {
            case PresetKind.Env:
                Env.RemoveAll(t => EnvKey(t) == p.Key);
                Env.Add(p.Token);
                HasCommand = true;
                break;
            case PresetKind.Wrapper:
                if (!Wrappers.Contains(p.Value))
                {
                    Wrappers.Add(p.Value);
                }
                HasCommand = true;
                break;
            default:
                if (!Args.Contains(p.Value))
                {
                    Args.Add(p.Value);
                }
                break;
        }
    }

    public void Remove(LaunchPreset p)
    {
        switch (p.Kind)
        {
            case PresetKind.Env:
                Env.Remove(p.Token);
                break;
            case PresetKind.Wrapper:
                Wrappers.Remove(p.Value);
                break;
            default:
                Args.Remove(p.Value);
                break;
        }
        if (Env.Count == 0 && Wrappers.Count == 0 && Args.Count == 0)
        {
            HasCommand = false;
        }
    }

    public void Toggle(LaunchPreset p)
    {
        if (Has(p))
        {
            Remove(p);
        }
        else
        {
            Add(p);
        }
    }

    /// <summary>Value of an environment variable set in the options, or null.</summary>
    public string? EnvValue(string key) => Env.FirstOrDefault(t => EnvKey(t) == key) is { } t ? t[(t.IndexOf('=') + 1)..] : null;
}
