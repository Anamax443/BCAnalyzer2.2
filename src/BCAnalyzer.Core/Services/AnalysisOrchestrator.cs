using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Core.Services;

/// <summary>
/// Orchestruje celý analytický proces pod správnou identitou:
/// 1) Sběr eventů z Event Logu
/// 2) SQL diagnostika
/// 3) Korelace dat
/// 4) Porovnání s historií
/// 5) Generování HTML reportu
/// </summary>
public class AnalysisOrchestrator
{
    private readonly AnalyzerSettings _cfg;
    private readonly AnalyzerLogger _log;
    public event Action<string, int>? OnPhaseChanged;

    public AnalysisOrchestrator(AnalyzerSettings cfg, AnalyzerLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public AnalysisResult Run(CancellationToken ct = default)
    {
        // Celý workflow běží pod impersonovanou identitou (pokud jsou zadané explicitní credentials)
        return ImpersonationHelper.RunAs(_cfg, () => RunInternal(ct));
    }

    private AnalysisResult RunInternal(CancellationToken ct)
    {
        _log.StartRegion(10000, "Celková analýza");

        var authInfo = _cfg.UseIntegratedSecurity
            ? $"Windows (Integrated Security)"
            : $"Impersonace jako {_cfg.Username}";
        _log.Info($"Autentizace: {authInfo}");

        var snapshot = new AnalysisSnapshot
        {
            RunTime = DateTime.Now,
            ServerName = _cfg.NavServer,
            DatabaseName = _cfg.DatabaseName
        };

        try
        {
            // ── FÁZE 1: Sběr eventů z Event Logu ─────────────────────────
            OnPhaseChanged?.Invoke("Fáze 1/5: Sběr eventů z Event Logu...", 5);
            _log.StartRegion(10100, "Event Log sběr");

            var collector = new EventLogCollector(_cfg, _log);
            var events = collector.Collect(ct);

            snapshot.TotalSlowSqlEvents = events.Count;
            snapshot.TotalSlowSqlTimeMs = events.Sum(e => (long)e.ExecutionTimeMs);
            snapshot.MaxExecutionTimeMs = events.Count > 0 ? events.Max(e => e.ExecutionTimeMs) : 0;

            _log.EndRegion(10100);
            ct.ThrowIfCancellationRequested();

            // ── FÁZE 2: SQL diagnostika ───────────────────────────────────
            OnPhaseChanged?.Invoke("Fáze 2/5: SQL diagnostika...", 25);
            _log.StartRegion(10200, "SQL diagnostika");

            var sql = new SqlAnalyzer(_cfg, _log);
            var correlator = new DataCorrelator(_cfg, _log);

            var tables = correlator.AggregateByTable(events);
            var tableNames = tables.Select(t => t.TableName).ToList();

            var sizes = sql.GetTableSizes(tableNames, ct);
            var indexes = sql.GetIndexes(tableNames, ct);
            ct.ThrowIfCancellationRequested();

            OnPhaseChanged?.Invoke("Fáze 2/5: Fragmentace a statistiky...", 40);
            var fragmentation = sql.GetFragmentation(tableNames, ct);
            var missingIndexes = sql.GetMissingIndexes(ct);
            var staleStats = sql.GetStaleStatistics(tableNames, ct);
            var lockContention = sql.GetLockContention(tableNames, ct);
            ct.ThrowIfCancellationRequested();

            OnPhaseChanged?.Invoke("Fáze 2/5: Wait stats, I/O, Performance Monitoring...", 55);
            var waitStats = sql.GetWaitStats(ct);
            var ioLatency = sql.GetIoLatency(ct);
            var perfSummary = sql.GetPerfMonitoringSummary(ct);

            _log.EndRegion(10200);

            // ── FÁZE 3: Korelace a vyhodnocení ───────────────────────────
            OnPhaseChanged?.Invoke("Fáze 3/5: Korelace a vyhodnocení...", 65);
            _log.StartRegion(10300, "Korelace dat");

            correlator.EnrichWithSqlData(tables, sizes, indexes, fragmentation, missingIndexes, staleStats, lockContention);
            correlator.Evaluate(tables, events.Count);
            var hourly = correlator.GetHourlyDistribution(events);

            snapshot.Tables = tables;
            snapshot.WaitStats = waitStats;
            snapshot.IoLatency = ioLatency;
            snapshot.PerfSummary = perfSummary;
            snapshot.HourlyDistribution = hourly;

            _log.EndRegion(10300);

            // ── FÁZE 4: Historie a porovnání ─────────────────────────────
            OnPhaseChanged?.Invoke("Fáze 4/5: Porovnání s předchozím během...", 75);
            _log.StartRegion(10400, "Historie");

            var history = new HistoryManager(_cfg, _log);
            var previous = history.LoadPrevious();
            var comparison = history.Compare(snapshot, previous);

            _log.EndRegion(10400);

            // ── FÁZE 5: HTML report ──────────────────────────────────────
            OnPhaseChanged?.Invoke("Fáze 5/5: Generování HTML reportu...", 85);
            _log.StartRegion(10500, "HTML report");

            var reportGen = new HtmlReportGenerator(_cfg, _log);
            var htmlPath = reportGen.Generate(snapshot, comparison);
            snapshot.HtmlReportPath = htmlPath;

            _log.EndRegion(10500);

            // ── Uložení snapshotu ────────────────────────────────────────
            OnPhaseChanged?.Invoke("Ukládám snapshot...", 95);
            history.Save(snapshot);
            history.CleanupOld();

            _log.EndRegion(10000);
            OnPhaseChanged?.Invoke("Hotovo!", 100);

            _log.Success($"Analýza dokončena: {events.Count} eventů, {tables.Count} tabulek, report: {htmlPath}");
            _log.Info($"Celkový čas: {_log.TotalElapsed}");

            return new AnalysisResult
            {
                Success = true,
                Message = $"Analýza dokončena: {events.Count} eventů, {tables.Count(t => t.Severity != "OK")} problémových tabulek",
                HtmlReportPath = htmlPath,
                Snapshot = snapshot,
                Comparison = comparison
            };
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Analýza zrušena uživatelem.");
            return new AnalysisResult { Success = false, Message = "Zrušeno uživatelem." };
        }
        catch (Exception ex)
        {
            _log.Error($"CHYBA: {ex.Message}\n{ex.StackTrace}");
            return new AnalysisResult { Success = false, Message = ex.Message, Error = ex };
        }
    }
}
