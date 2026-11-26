using System.Text;

namespace DeckWrench.Core.Detection;

/// <summary>
/// Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf, config.vdf,
/// localconfig.vdf): quoted keys, quoted values or nested { } blocks, // comments.
/// Keys are case-insensitive, as Steam treats them.
/// </summary>
public sealed class KeyValues
{
    private readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public IReadOnlyList<string> Keys => _order;

    public string? Str(string key) => _items.TryGetValue(key, out var v) ? v as string : null;

    public KeyValues? Map(string key) => _items.TryGetValue(key, out var v) ? v as KeyValues : null;

    /// <summary>Follows a path of nested blocks ("Software", "Valve", "Steam").</summary>
    public KeyValues? Path(params string[] keys)
    {
        KeyValues? cur = this;
        foreach (var k in keys)
        {
            cur = cur?.Map(k);
        }
        return cur;
    }

    /// <summary>Child blocks in file order, with their keys.</summary>
    public IEnumerable<(string Key, KeyValues Value)> Maps() =>
        _order.Where(k => _items[k] is KeyValues).Select(k => (k, (KeyValues)_items[k]));

    private void Set(string key, object value)
    {
        if (_items.ContainsKey(key))
        {
            // Keep the spelling and position already in the file.
            key = _order.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _order.Add(key);
        }
        _items[key] = value;
    }

    /// <summary>Sets a string value (adds it at the end when new).</summary>
    public void SetStr(string key, string value) => Set(key, value);

    /// <summary>The child block <paramref name="key"/>, created when missing.</summary>
    public KeyValues GetOrAddMap(string key)
    {
        if (Map(key) is { } existing)
        {
            return existing;
        }
        var map = new KeyValues();
        Set(key, map);
        return map;
    }

    /// <summary>Follows (and creates) a path of nested blocks.</summary>
    public KeyValues GetOrAddPath(params string[] keys)
    {
        var cur = this;
        foreach (var k in keys)
        {
            cur = cur.GetOrAddMap(k);
        }
        return cur;
    }

    /// <summary>Removes a key (value or block). True if it was there.</summary>
    public bool Remove(string key)
    {
        if (!_items.Remove(key))
        {
            return false;
        }
        _order.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Writes the document the way Steam does: tab-indented, quoted keys and values.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        Write(sb, 0);
        return sb.ToString();
    }

    private void Write(StringBuilder sb, int depth)
    {
        var indent = new string('\t', depth);
        foreach (var key in _order)
        {
            switch (_items[key])
            {
                case KeyValues child:
                    sb.Append(indent).Append(Quote(key)).Append('\n');
                    sb.Append(indent).Append("{\n");
                    child.Write(sb, depth + 1);
                    sb.Append(indent).Append("}\n");
                    break;
                case string value:
                    sb.Append(indent).Append(Quote(key)).Append("\t\t").Append(Quote(value)).Append('\n');
                    break;
            }
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Writes the document to <paramref name="path"/> through a temporary file, so a crash never leaves half a file.</summary>
    public void SaveFile(string path)
    {
        var tmp = path + ".deckwrench-tmp";
        File.WriteAllText(tmp, Serialize());
        File.Move(tmp, path, overwrite: true);
    }

    public static KeyValues ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses a document. The top level is a block (usually holding one named root block).</summary>
    public static KeyValues Parse(string text)
    {
        var pos = 0;
        var root = ReadBlock(text, ref pos, topLevel: true);
        return root;
    }

    private static KeyValues ReadBlock(string s, ref int pos, bool topLevel)
    {
        var kv = new KeyValues();
        while (true)
        {
            var key = Next(s, ref pos);
            if (key == null)
            {
                if (!topLevel)
                {
                    throw new InvalidDataException("Unexpected end of file inside a block.");
                }
                return kv;
            }
            if (key is { Token: "}", Quoted: false })
            {
                if (topLevel)
                {
                    throw new InvalidDataException("Unexpected '}'.");
                }
                return kv;
            }
            var value = Next(s, ref pos) ?? throw new InvalidDataException($"Missing value for \"{key.Value.Token}\".");
            if (value is { Token: "{", Quoted: false })
            {
                kv.Set(key.Value.Token, ReadBlock(s, ref pos, topLevel: false));
            }
            else
            {
                kv.Set(key.Value.Token, value.Token);
                SkipConditional(s, ref pos);
            }
        }
    }

    /// <summary>Optional "[$WIN32]" style conditions after a value are ignored.</summary>
    private static void SkipConditional(string s, ref int pos)
    {
        var save = pos;
        SkipSpace(s, ref pos);
        if (pos < s.Length && s[pos] == '[')
        {
            var end = s.IndexOf(']', pos);
            pos = end < 0 ? s.Length : end + 1;
            return;
        }
        pos = save;
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            if (char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }
            else if (s[pos] == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n')
                {
                    pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private static (string Token, bool Quoted)? Next(string s, ref int pos)
    {
        SkipSpace(s, ref pos);
        if (pos >= s.Length)
        {
            return null;
        }
        var c = s[pos];
        if (c is '{' or '}')
        {
            pos++;
            return (c.ToString(), false);
        }
        var sb = new StringBuilder();
        if (c == '"')
        {
            pos++;
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    pos++;
                    sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', var o => o });
                }
                else
                {
                    sb.Append(s[pos]);
                }
                pos++;
            }
            pos++;  // closing quote
            return (sb.ToString(), true);
        }
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"'))
        {
            sb.Append(s[pos++]);
        }
        return (sb.ToString(), false);
    }
}
