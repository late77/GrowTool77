namespace GrowTool77.Aura;

// In-memory activity log for the [AURA] tab. Never written to disk and never
// given packet contents from the game (login text, tokens, chat).
public sealed class AuraLog
{
    private const int MaxLines = 600;
    private readonly LinkedList<string> _lines = new();
    private readonly object _lock = new();

    public event Action<string>? LineAdded;

    public void Info(string message)  => Add("    ", message);
    public void Warn(string message)  => Add("[!] ", message);
    public void Error(string message) => Add("[x] ", message);

    private void Add(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {level}{message}";
        lock (_lock)
        {
            _lines.AddLast(line);
            while (_lines.Count > MaxLines) _lines.RemoveFirst();
        }
        LineAdded?.Invoke(line);
    }

    public string[] Lines()
    {
        lock (_lock) return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_lock) _lines.Clear();
    }
}
