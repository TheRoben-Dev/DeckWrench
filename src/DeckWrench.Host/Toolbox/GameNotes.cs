using System.Text.Json;
using DeckWrench.Core;

namespace DeckWrench.Host.Toolbox;

/// <summary>
/// The player's own notes per game ("needs PROTON_USE_WINED3D=1", "GE 9-20 fixes cutscenes"),
/// kept in notes.json in the config folder by game key (steam:1245620, lutris:skyrim).
/// </summary>
public sealed class GameNotes
{
    public const int MaxLength = 500;

    private static readonly char[] LineBreaks = ['\r', '\n', '\t'];

    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, string>? _notes;

    public GameNotes(string file) => _file = file;

    public string FilePath => _file;

    public string? Get(string key)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(key);
        }
    }

    /// <summary>Saves the note (trimmed, one line, at most <see cref="MaxLength"/> characters); empty removes it.</summary>
    public void Set(string key, string? text)
    {
        var note = string.Join(' ', (text ?? "").Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        if (note.Length > MaxLength)
        {
            note = note[..MaxLength];
        }
        lock (_gate)
        {
            var notes = Load();
            if (note.Length == 0)
            {
                notes.Remove(key);
            }
            else
            {
                notes[key] = note;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new SortedDictionary<string, string>(notes), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_notes != null)
        {
            return _notes;
        }
        try
        {
            _notes = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"notes: {_file} unreadable ({e.Message}); starting empty, the file is kept as .bad");
            try
            {
                File.Copy(_file, _file + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }
            _notes = [];
        }
        return _notes;
    }
}
