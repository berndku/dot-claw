namespace DotClaw.Session;

using System.Text.Json;

/// <summary>
/// Persists conversation history to ~/.dotclaw/sessions/ as JSONL files.
/// </summary>
public class SessionManager
{
    private static readonly string SessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dotclaw", "sessions");

    private readonly string _path;

    public SessionManager(string sessionKey)
    {
        Directory.CreateDirectory(SessionsDir);
        _path = Path.Combine(SessionsDir, $"{sessionKey}.jsonl");

        if (!File.Exists(_path))
        {
            var meta = new { session_key = sessionKey, created_at = DateTime.UtcNow.ToString("o") };
            File.WriteAllText(_path, JsonSerializer.Serialize(meta) + "\n");
        }
    }

    public List<JsonElement> Load(int n = 20)
    {
        var lines = File.ReadAllLines(_path);
        var messages = new List<JsonElement>();

        foreach (var line in lines.Skip(1)) // skip metadata
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                messages.Add(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        }

        return messages.TakeLast(n).ToList();
    }

    public void Append(IEnumerable<object> messages)
    {
        var ts = DateTime.UtcNow.ToString("o");
        using var writer = File.AppendText(_path);
        foreach (var msg in messages)
        {
            // Serialize the message and add timestamp
            var json = JsonSerializer.Serialize(msg);
            var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            dict["timestamp"] = ts;
            writer.WriteLine(JsonSerializer.Serialize(dict));
        }
    }
}
