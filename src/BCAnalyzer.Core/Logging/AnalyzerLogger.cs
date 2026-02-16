using System.Diagnostics;
using System.Text;

namespace BCAnalyzer.Core.Logging;

public class AnalyzerLogger : IDisposable
{
    private readonly StreamWriter? _w;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Dictionary<int, (string Name, Stopwatch Sw)> _regions = new();
    private readonly List<(int Id, string Name, TimeSpan Dur)> _done = new();
    public event Action<string, string>? OnLog;

    public AnalyzerLogger(string? logPath = null)
    {
        if (!string.IsNullOrEmpty(logPath))
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _w = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
        }
    }

    public void Info(string m) => Log("INFO", m);
    public void Warning(string m) => Log("WARNING", m);
    public void Error(string m) => Log("ERROR", m);
    public void Success(string m) => Log("OK", m);

    public void StartRegion(int id, string name)
    {
        _regions[id] = (name, Stopwatch.StartNew());
        Info($"Region {id} [{name}]: START");
    }

    public void EndRegion(int id)
    {
        if (_regions.TryGetValue(id, out var r))
        {
            r.Sw.Stop();
            _done.Add((id, r.Name, r.Sw.Elapsed));
            Info($"Region {id} [{r.Name}]: {r.Sw.Elapsed}");
            _regions.Remove(id);
        }
    }

    public TimeSpan TotalElapsed => _sw.Elapsed;
    public IReadOnlyList<(int Id, string Name, TimeSpan Dur)> CompletedRegions => _done;

    private void Log(string level, string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level,-7}] {msg}";
        _w?.WriteLine(line);
        OnLog?.Invoke(level, line);
    }

    public void Dispose() => _w?.Dispose();
}
