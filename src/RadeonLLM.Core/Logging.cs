namespace RadeonLLM.Core;

/// <summary>Minimal rolling file log. One instance per channel (app, runtime, inference, api).</summary>
public sealed class LogChannel
{
    readonly string _file;
    readonly long _maxBytes;
    readonly int _keep;
    readonly object _lock = new();
    readonly Queue<string> _tail = new();

    public string Name { get; }
    public event Action<string>? LineWritten;

    public LogChannel(AppPaths paths, string name, long maxBytes = 2_000_000, int keep = 3)
    {
        Name = name;
        Directory.CreateDirectory(paths.Logs);
        _file = Path.Combine(paths.Logs, name + ".log");
        _maxBytes = maxBytes;
        _keep = keep;
    }

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex.Message}");
    /// <summary>Raw line from a child process (no level prefix).</summary>
    public void Raw(string line) => Write(null, line);

    public string[] Tail(int n = 300) { lock (_lock) return _tail.TakeLast(n).ToArray(); }

    void Write(string? level, string msg)
    {
        var line = level is null ? msg : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (_lock)
        {
            try
            {
                Roll();
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
            _tail.Enqueue(line);
            while (_tail.Count > 1000) _tail.Dequeue();
        }
        LineWritten?.Invoke(line);
    }

    void Roll()
    {
        var fi = new FileInfo(_file);
        if (!fi.Exists || fi.Length < _maxBytes) return;
        for (int i = _keep - 1; i >= 1; i--)
        {
            var src = $"{_file}.{i}";
            if (File.Exists(src)) File.Move(src, $"{_file}.{i + 1}", true);
        }
        File.Move(_file, _file + ".1", true);
    }
}

public sealed class Logs
{
    public LogChannel App { get; }
    public LogChannel Runtime { get; }
    public LogChannel Inference { get; }
    public LogChannel Api { get; }
    public Logs(AppPaths p)
    {
        App = new(p, "app"); Runtime = new(p, "runtime"); Inference = new(p, "inference"); Api = new(p, "api");
    }
}
