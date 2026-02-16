using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Core.Services;

/// <summary>
/// READ-ONLY: Čte BC Slow SQL eventy z Application Event Logu vzdáleného serveru.
/// Identita je řízena impersonací na úrovni vlákna (ImpersonationHelper).
/// </summary>
public class EventLogCollector
{
    private readonly AnalyzerSettings _cfg;
    private readonly AnalyzerLogger _log;
    public event Action<int, int>? OnProgress;

    public EventLogCollector(AnalyzerSettings cfg, AnalyzerLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public List<SlowSqlEvent> Collect(CancellationToken ct = default)
    {
        var events = new List<SlowSqlEvent>();
        _log.Info($"Event Log: {_cfg.NavServer} | {_cfg.EventSource} | ID {_cfg.EventId} | {_cfg.LookbackHours}h");

        try
        {
            // Impersonace je už aktivní na úrovni vlákna — stačí jednoduchý constructor
            var session = new EventLogSession(_cfg.NavServer);

            var xpath =
                $"*[System[Provider[@Name='{_cfg.EventSource}'] " +
                $"and (EventID={_cfg.EventId}) " +
                $"and (Level={_cfg.EventLevel}) " +
                $"and TimeCreated[timediff(@SystemTime) <= {_cfg.LookbackHours * 3600 * 1000}]]]";

            var query = new EventLogQuery(_cfg.EventLogName, PathType.LogName, xpath)
            {
                Session = session,
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            int ok = 0, skip = 0;
            EventRecord? rec;

            while ((rec = reader.ReadEvent()) != null)
            {
                ct.ThrowIfCancellationRequested();
                using (rec)
                {
                    var evt = ParseRecord(rec);
                    if (evt != null && evt.ExecutionTimeMs >= _cfg.MinExecutionTimeMs)
                    {
                        events.Add(evt);
                        ok++;
                    }
                    else skip++;
                }

                if ((ok + skip) % 50 == 0)
                    OnProgress?.Invoke(ok, ok + skip);
            }

            _log.Info($"Event Log: {ok} eventů načteno, {skip} přeskočeno");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error($"Přístup odepřen k Event Logu na {_cfg.NavServer}: {ex.Message}");
            _log.Error("Tip: Účet potřebuje oprávnění 'Event Log Readers' na vzdáleném serveru.");
            throw;
        }
        catch (OperationCanceledException) { _log.Warning("Sběr eventů zrušen."); throw; }
        catch (Exception ex) { _log.Error($"Chyba Event Log: {ex.Message}"); throw; }

        return events;
    }

    private SlowSqlEvent? ParseRecord(EventRecord rec)
    {
        if (rec.TimeCreated == null) return null;
        var p = rec.Properties;
        if (p == null || p.Count < 7) return null;

        var evt = new SlowSqlEvent
        {
            EventTime = rec.TimeCreated.Value,
            ExecutionTimeMs = ToInt(p, 0),
            ThresholdMs = ToInt(p, 1),
            OverThresholdMs = ToInt(p, 2),
            ServerInstance = ToStr(p, 3),
            DatabaseName = ToStr(p, 4),
            CompanyName = ToStr(p, 5),
            SqlStatement = ToStr(p, 6),
            ALCallStack = ToStr(p, 7),
            AppObjectType = ToStr(p, 8),
            AppObjectId = ToInt(p, 9)
        };
        evt.TableName = ExtractTableName(evt.SqlStatement);
        if (string.IsNullOrEmpty(evt.CompanyName))
            evt.CompanyName = _cfg.CompanyPrefix;
        return evt;
    }

    private static string ExtractTableName(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return "";
        var m = Regex.Match(sql, @"FROM\s+""[^""]*""\s*\.\s*dbo\s*\.\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(sql, @"FROM\s+dbo\s*\.\s*""([^""]+)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string ToStr(IList<EventProperty> p, int i) =>
        i < p.Count && p[i]?.Value != null ? p[i].Value.ToString() ?? "" : "";

    private static int ToInt(IList<EventProperty> p, int i) =>
        i < p.Count && p[i]?.Value != null && int.TryParse(p[i].Value.ToString(), out var v) ? v : 0;
}
