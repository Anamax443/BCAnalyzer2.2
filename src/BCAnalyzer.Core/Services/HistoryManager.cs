using System.Text.Json;
using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Core.Services;

/// <summary>Ukládá snapshoty do JSON, načítá předchozí, porovnává.</summary>
public class HistoryManager
{
    private readonly AnalyzerSettings _cfg;
    private readonly AnalyzerLogger _log;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public HistoryManager(AnalyzerSettings cfg, AnalyzerLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Uloží snapshot do JSON souboru.</summary>
    public string Save(AnalysisSnapshot snapshot)
    {
        var dir = Path.GetFullPath(_cfg.HistoryPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var fileName = $"snapshot_{snapshot.RunTime:yyyyMMdd_HHmmss}.json";
        var path = Path.Combine(dir, fileName);
        var json = JsonSerializer.Serialize(snapshot, _jsonOpts);
        File.WriteAllText(path, json);

        _log.Info($"Snapshot uložen: {path}");
        return path;
    }

    /// <summary>Načte poslední uložený snapshot (pro porovnání).</summary>
    public AnalysisSnapshot? LoadPrevious()
    {
        var dir = Path.GetFullPath(_cfg.HistoryPath);
        if (!Directory.Exists(dir)) return null;

        var files = Directory.GetFiles(dir, "snapshot_*.json")
            .OrderByDescending(f => f)
            .Skip(1) // přeskočíme ten právě uložený (pokud existuje)
            .ToList();

        if (files.Count == 0)
        {
            // Zkusíme i bez skip — možná se ještě nic neuložilo v tomto běhu
            files = Directory.GetFiles(dir, "snapshot_*.json")
                .OrderByDescending(f => f)
                .ToList();
            if (files.Count == 0) return null;
        }

        try
        {
            var json = File.ReadAllText(files[0]);
            var snapshot = JsonSerializer.Deserialize<AnalysisSnapshot>(json, _jsonOpts);
            _log.Info($"Načten předchozí snapshot: {files[0]}");
            return snapshot;
        }
        catch (Exception ex)
        {
            _log.Warning($"Nelze načíst předchozí snapshot: {ex.Message}");
            return null;
        }
    }

    /// <summary>Porovná aktuální snapshot s předchozím.</summary>
    public ComparisonResult Compare(AnalysisSnapshot current, AnalysisSnapshot? previous)
    {
        var result = new ComparisonResult { Current = current, Previous = previous };

        if (previous == null)
        {
            _log.Info("Žádný předchozí snapshot — porovnání nedostupné.");
            return result;
        }

        // Event count change
        if (previous.TotalSlowSqlEvents > 0)
            result.EventCountChangePct = (current.TotalSlowSqlEvents - previous.TotalSlowSqlEvents) * 100.0 / previous.TotalSlowSqlEvents;

        // Total time change
        if (previous.TotalSlowSqlTimeMs > 0)
            result.TotalTimeChangePct = (current.TotalSlowSqlTimeMs - previous.TotalSlowSqlTimeMs) * 100.0 / previous.TotalSlowSqlTimeMs;

        // Per-table changes
        var prevTables = previous.Tables.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
        foreach (var ct in current.Tables)
        {
            if (prevTables.TryGetValue(ct.TableName, out var pt))
            {
                var countChange = ct.SlowSqlCount - pt.SlowSqlCount;
                var avgChange = ct.AvgExecutionTimeMs - pt.AvgExecutionTimeMs;

                if (countChange < -5 || avgChange < -500)
                    result.Improvements.Add($"{ct.TableName}: {pt.SlowSqlCount}→{ct.SlowSqlCount} eventů, avg {pt.AvgExecutionTimeMs:N0}→{ct.AvgExecutionTimeMs:N0} ms");
                else if (countChange > 5 || avgChange > 500)
                    result.Regressions.Add($"{ct.TableName}: {pt.SlowSqlCount}→{ct.SlowSqlCount} eventů, avg {pt.AvgExecutionTimeMs:N0}→{ct.AvgExecutionTimeMs:N0} ms");
            }
            else if (ct.SlowSqlCount > 5)
            {
                result.Regressions.Add($"{ct.TableName}: NOVÁ problémová tabulka ({ct.SlowSqlCount} eventů)");
            }
        }

        _log.Info($"Porovnání: eventy {result.EventCountChangePct:+0;-0;0}%, čas {result.TotalTimeChangePct:+0;-0;0}%, " +
                  $"{result.Improvements.Count} zlepšení, {result.Regressions.Count} zhoršení");

        return result;
    }

    /// <summary>Vyčistí staré snapshoty dle retention policy.</summary>
    public void CleanupOld()
    {
        var dir = Path.GetFullPath(_cfg.HistoryPath);
        if (!Directory.Exists(dir)) return;

        var cutoff = DateTime.Now.AddDays(-_cfg.HistoryRetentionDays);
        var deleted = 0;

        foreach (var file in Directory.GetFiles(dir, "snapshot_*.json"))
        {
            if (File.GetCreationTime(file) < cutoff)
            {
                File.Delete(file);
                deleted++;
            }
        }

        if (deleted > 0)
            _log.Info($"Vyčištěno {deleted} starých snapshotů (>{_cfg.HistoryRetentionDays} dní).");
    }
}
