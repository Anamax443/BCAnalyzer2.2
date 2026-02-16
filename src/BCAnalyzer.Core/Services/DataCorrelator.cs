using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Core.Services;

/// <summary>
/// Propojuje data z Event Logu se SQL diagnostikou.
/// Agreguje per-table, počítá severity, generuje findings a doporučení.
/// </summary>
public class DataCorrelator
{
    private readonly AnalyzerSettings _cfg;
    private readonly AnalyzerLogger _log;

    public DataCorrelator(AnalyzerSettings cfg, AnalyzerLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Agregace slow SQL eventů po tabulkách.</summary>
    public List<TableAnalysis> AggregateByTable(List<SlowSqlEvent> events)
    {
        _log.Info($"Agregace {events.Count} eventů po tabulkách...");

        var grouped = events
            .Where(e => !string.IsNullOrEmpty(e.TableName))
            .GroupBy(e => e.TableName)
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                // Nejčastější volající objekt
                var topCaller = g
                    .Where(e => e.AppObjectId > 0)
                    .GroupBy(e => $"{e.AppObjectType} {e.AppObjectId}")
                    .OrderByDescending(cg => cg.Count())
                    .FirstOrDefault();

                return new TableAnalysis
                {
                    TableName = g.Key,
                    SlowSqlCount = g.Count(),
                    MaxExecutionTimeMs = g.Max(e => e.ExecutionTimeMs),
                    SumExecutionTimeMs = g.Sum(e => (long)e.ExecutionTimeMs),
                    AvgExecutionTimeMs = g.Average(e => e.ExecutionTimeMs),
                    TopCallerObject = topCaller?.Key ?? "",
                    TopCallerCount = topCaller?.Count() ?? 0
                };
            })
            .ToList();

        _log.Info($"Agregováno do {grouped.Count} tabulek.");
        return grouped;
    }

    /// <summary>Obohacení table analýzy o SQL diagnostická data.</summary>
    public void EnrichWithSqlData(
        List<TableAnalysis> tables,
        List<TableInfo> sizes,
        List<IndexInfo> indexes,
        List<FragmentationInfo> fragmentation,
        List<MissingIndexInfo> missingIndexes,
        List<StaleStatisticsInfo> staleStats,
        List<LockContentionInfo> lockContention)
    {
        _log.Info("Obohacuji table analýzy o SQL data...");

        foreach (var ta in tables)
        {
            var sqlName = NormalizeSqlName(ta.TableName);

            // Size
            ta.Size = sizes.FirstOrDefault(s =>
                s.TableName.Contains(sqlName, StringComparison.OrdinalIgnoreCase));

            // Indexes
            ta.Indexes = indexes
                .Where(i => i.TableName.Contains(sqlName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Fragmentation
            ta.Fragmentation = fragmentation
                .Where(f => f.TableName.Contains(sqlName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Missing Indexes
            ta.MissingIndexes = missingIndexes
                .Where(m => m.TableName.Contains(sqlName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Stale Statistics
            ta.StaleStats = staleStats
                .Where(s => s.TableName.Contains(sqlName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Lock Contention
            ta.LockContention = lockContention
                .FirstOrDefault(l => l.TableName.Contains(sqlName, StringComparison.OrdinalIgnoreCase));
        }

        _log.Info("Obohacení dokončeno.");
    }

    /// <summary>Vyhodnocení severity, findings a doporučení pro každou tabulku.</summary>
    public void Evaluate(List<TableAnalysis> tables, int totalEvents)
    {
        _log.Info("Vyhodnocuji severity a doporučení...");

        foreach (var ta in tables)
        {
            ta.Findings.Clear();
            ta.Recommendations.Clear();

            // ── Severity ──
            var pctOfAll = totalEvents > 0 ? ta.SlowSqlCount * 100.0 / totalEvents : 0;
            if (ta.MaxExecutionTimeMs > _cfg.CriticalSqlThresholdMs || pctOfAll > 20)
                ta.Severity = "CRITICAL";
            else if (ta.SlowSqlCount > 10 || ta.MaxExecutionTimeMs > _cfg.SlowSqlThresholdMs * 3)
                ta.Severity = "WARNING";
            else
                ta.Severity = "OK";

            // ── Findings ──
            ta.Findings.Add($"{ta.SlowSqlCount} slow SQL eventů ({pctOfAll:F1}% z celku), " +
                            $"max {ta.MaxExecutionTimeMs:N0} ms, avg {ta.AvgExecutionTimeMs:N0} ms");

            if (!string.IsNullOrEmpty(ta.TopCallerObject))
                ta.Findings.Add($"Hlavní volající: {ta.TopCallerObject} ({ta.TopCallerCount}×)");

            if (ta.Size != null)
                ta.Findings.Add($"Velikost: {ta.Size.RowCount:N0} řádků, {ta.Size.TotalSizeMB:N1} MB " +
                                $"(data {ta.Size.DataSizeMB:N1} MB, indexy {ta.Size.IndexSizeMB:N1} MB)");

            // Fragmentace
            var highFrag = ta.Fragmentation.Where(f => f.FragmentationPct > _cfg.FragmentationWarningPct).ToList();
            if (highFrag.Count > 0)
            {
                ta.Findings.Add($"Fragmentace >30%: {string.Join(", ", highFrag.Select(f => $"{f.IndexName} ({f.FragmentationPct:F0}%)"))}");
                ta.Recommendations.Add($"ALTER INDEX [{highFrag[0].IndexName}] ON [{ta.Size?.TableName ?? NormalizeSqlName(ta.TableName)}] REBUILD WITH (ONLINE = ON)");
            }

            // Missing indexes
            foreach (var mi in ta.MissingIndexes.Take(2))
            {
                ta.Findings.Add($"Missing index: ({mi.EqualityColumns}) — impact score {mi.ImpactScore:N0}");
                ta.Recommendations.Add(mi.CreateStatement);
            }

            // Stale stats
            var badStats = ta.StaleStats.Where(s => s.ModificationPct > _cfg.StaleStatsModPct).ToList();
            if (badStats.Count > 0)
            {
                ta.Findings.Add($"Zastaralé statistiky: {badStats.Count} ({string.Join(", ", badStats.Take(3).Select(s => $"{s.StatisticsName}: {s.ModificationPct:F0}%"))})");
                ta.Recommendations.Add($"UPDATE STATISTICS [{ta.Size?.TableName ?? NormalizeSqlName(ta.TableName)}] WITH FULLSCAN");
            }

            // Lock contention
            if (ta.LockContention != null && ta.LockContention.RowLockWaitMs > 1000)
            {
                ta.Findings.Add($"Lock contention: {ta.LockContention.RowLockWaitCount:N0} waits, {ta.LockContention.RowLockWaitMs:N0} ms celkem");
            }

            // Record Link specifics
            if (ta.TableName.Contains("Record Link", StringComparison.OrdinalIgnoreCase) &&
                ta.Size?.RowCount > _cfg.RecordLinkMaxRows)
            {
                ta.Findings.Add($"Record Link má {ta.Size.RowCount:N0} řádků — doporučeno čištění");
                ta.Recommendations.Insert(0, "Spustit Report 299 (Delete Old Record Links) nebo DELETE FROM [Record Link] WHERE Created < DATEADD(YEAR,-1,GETDATE())");
            }
        }

        _log.Info("Vyhodnocení dokončeno.");
    }

    /// <summary>Distribuce eventů po hodinách.</summary>
    public List<HourlyDistribution> GetHourlyDistribution(List<SlowSqlEvent> events)
    {
        return events
            .GroupBy(e => e.EventTime.Hour)
            .Select(g => new HourlyDistribution
            {
                Hour = g.Key,
                Count = g.Count(),
                SumMs = g.Sum(e => (long)e.ExecutionTimeMs)
            })
            .OrderBy(h => h.Hour)
            .ToList();
    }

    private static string NormalizeSqlName(string bcTableName)
    {
        var idx = bcTableName.IndexOf('$');
        return idx >= 0 ? bcTableName[(idx + 1)..] : bcTableName;
    }
}
