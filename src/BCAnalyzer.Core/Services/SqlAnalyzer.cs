using Microsoft.Data.SqlClient;
using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Core.Services;

/// <summary>
/// READ-ONLY diagnostické dotazy na SQL Serveru.
/// ╔═══════════════════════════════════════════════════════════════╗
/// ║  POUZE SELECT dotazy — žádné INSERT/UPDATE/DELETE/ALTER/     ║
/// ║  CREATE/DROP/TRUNCATE/REBUILD/REORGANIZE.                   ║
/// ║  Aplikace nikdy nemodifikuje data ani strukturu databáze.    ║
/// ╚═══════════════════════════════════════════════════════════════╝
/// </summary>
public class SqlAnalyzer
{
    private readonly AnalyzerSettings _cfg;
    private readonly AnalyzerLogger _log;

    public SqlAnalyzer(AnalyzerSettings cfg, AnalyzerLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    // ── Veřejné API ──────────────────────────────────────────────────────

    public List<TableInfo> GetTableSizes(IEnumerable<string> tableNames, CancellationToken ct = default)
        => Safe("TableSizes", () => QueryTableSizes(tableNames), ct);

    public List<IndexInfo> GetIndexes(IEnumerable<string> tableNames, CancellationToken ct = default)
        => Safe("Indexes", () => QueryIndexes(tableNames), ct);

    public List<IndexUsageInfo> GetIndexUsage(IEnumerable<string> tableNames, CancellationToken ct = default)
        => Safe("IndexUsage", () => QueryIndexUsage(tableNames), ct);

    public List<FragmentationInfo> GetFragmentation(IEnumerable<string> tableNames, CancellationToken ct = default)
        => Safe("Fragmentation", () => QueryFragmentation(tableNames), ct);

    public List<MissingIndexInfo> GetMissingIndexes(CancellationToken ct = default)
        => Safe("MissingIndexes", () => QueryMissingIndexes(), ct);

    public List<StaleStatisticsInfo> GetStaleStatistics(IEnumerable<string> tableNames, CancellationToken ct = default)
        => Safe("StaleStatistics", () => QueryStaleStatistics(tableNames), ct);

    public List<LockContentionInfo> GetLockContention(IEnumerable<string> tableNames, CancellationToken ct = default)
        => Safe("LockContention", () => QueryLockContention(tableNames), ct);

    public List<WaitStatsInfo> GetWaitStats(CancellationToken ct = default)
        => Safe("WaitStats", () => QueryWaitStats(), ct);

    public List<IoLatencyInfo> GetIoLatency(CancellationToken ct = default)
        => Safe("IoLatency", () => QueryIoLatency(), ct);

    public PerfMonitoringSummary? GetPerfMonitoringSummary(CancellationToken ct = default)
        => Safe("PerfMonitoring", () => QueryPerfMonitoringSummary(), ct);

    // ── Implementace dotazů ──────────────────────────────────────────────

    private List<TableInfo> QueryTableSizes(IEnumerable<string> tableNames)
    {
        var results = new List<TableInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT 
                s.name AS SchemaName, t.name AS TableName,
                p.rows AS RowCount,
                CAST(ROUND((SUM(a.total_pages) * 8) / 1024.0, 2) AS DECIMAL(18,2)) AS TotalSizeMB,
                CAST(ROUND((SUM(a.data_pages) * 8) / 1024.0, 2) AS DECIMAL(18,2)) AS DataSizeMB,
                CAST(ROUND(((SUM(a.total_pages) - SUM(a.data_pages)) * 8) / 1024.0, 2) AS DECIMAL(18,2)) AS IndexSizeMB
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.indexes i ON t.object_id = i.object_id
            INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
            INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
            WHERE i.index_id <= 1
            GROUP BY s.name, t.name, p.rows
            ORDER BY SUM(a.total_pages) DESC";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        var tableSet = new HashSet<string>(tableNames.Select(NormalizeSqlName), StringComparer.OrdinalIgnoreCase);

        while (rdr.Read())
        {
            var name = rdr.GetString(1);
            if (tableSet.Count > 0 && !tableSet.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new TableInfo
            {
                SchemaName = rdr.GetString(0),
                TableName = name,
                RowCount = rdr.GetInt64(2),
                TotalSizeMB = (double)rdr.GetDecimal(3),
                DataSizeMB = (double)rdr.GetDecimal(4),
                IndexSizeMB = (double)rdr.GetDecimal(5)
            });
        }
        return results;
    }

    private List<IndexInfo> QueryIndexes(IEnumerable<string> tableNames)
    {
        var results = new List<IndexInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT 
                t.name AS TableName, i.name AS IndexName, i.type_desc AS IndexType,
                i.is_unique, i.is_disabled, i.fill_factor,
                STUFF((SELECT ', ' + c.name
                    FROM sys.index_columns ic
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                    ORDER BY ic.key_ordinal
                    FOR XML PATH('')), 1, 2, '') AS KeyColumns,
                STUFF((SELECT ', ' + c.name
                    FROM sys.index_columns ic
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                    ORDER BY ic.key_ordinal
                    FOR XML PATH('')), 1, 2, '') AS IncludeColumns
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            WHERE i.type > 0
            ORDER BY t.name, i.index_id";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        var tableSet = new HashSet<string>(tableNames.Select(NormalizeSqlName), StringComparer.OrdinalIgnoreCase);

        while (rdr.Read())
        {
            var name = rdr.GetString(0);
            if (tableSet.Count > 0 && !tableSet.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new IndexInfo
            {
                TableName = name,
                IndexName = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                IndexType = rdr.GetString(2),
                IsUnique = rdr.GetBoolean(3),
                IsDisabled = rdr.GetBoolean(4),
                FillFactor = rdr.GetInt32(5),
                KeyColumns = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                IncludeColumns = rdr.IsDBNull(7) ? null : rdr.GetString(7)
            });
        }
        return results;
    }

    private List<IndexUsageInfo> QueryIndexUsage(IEnumerable<string> tableNames)
    {
        var results = new List<IndexUsageInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT t.name, i.name,
                ISNULL(us.user_seeks, 0), ISNULL(us.user_scans, 0),
                ISNULL(us.user_lookups, 0), ISNULL(us.user_updates, 0),
                us.last_user_seek, us.last_user_scan
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            LEFT JOIN sys.dm_db_index_usage_stats us
                ON i.object_id = us.object_id AND i.index_id = us.index_id
                AND us.database_id = DB_ID()
            WHERE i.type > 0
            ORDER BY t.name, i.index_id";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        var tableSet = new HashSet<string>(tableNames.Select(NormalizeSqlName), StringComparer.OrdinalIgnoreCase);

        while (rdr.Read())
        {
            var name = rdr.GetString(0);
            if (tableSet.Count > 0 && !tableSet.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new IndexUsageInfo
            {
                TableName = name,
                IndexName = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                UserSeeks = rdr.GetInt64(2),
                UserScans = rdr.GetInt64(3),
                UserLookups = rdr.GetInt64(4),
                UserUpdates = rdr.GetInt64(5),
                LastUserSeek = rdr.IsDBNull(6) ? null : rdr.GetDateTime(6),
                LastUserScan = rdr.IsDBNull(7) ? null : rdr.GetDateTime(7)
            });
        }
        return results;
    }

    private List<FragmentationInfo> QueryFragmentation(IEnumerable<string> tableNames)
    {
        var results = new List<FragmentationInfo>();
        using var conn = OpenConnection();
        // LIMITED mode je rychlý a neinvazivní
        var sql = @"
            SELECT t.name AS TableName, i.name AS IndexName,
                ps.avg_fragmentation_in_percent, ps.page_count
            FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ps
            INNER JOIN sys.indexes i ON ps.object_id = i.object_id AND ps.index_id = i.index_id
            INNER JOIN sys.tables t ON ps.object_id = t.object_id
            WHERE ps.index_id > 0 AND ps.page_count > 100
            ORDER BY ps.avg_fragmentation_in_percent DESC";

        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 300; // fragmentace může trvat déle
        using var rdr = cmd.ExecuteReader();
        var tableSet = new HashSet<string>(tableNames.Select(NormalizeSqlName), StringComparer.OrdinalIgnoreCase);

        while (rdr.Read())
        {
            var name = rdr.GetString(0);
            if (tableSet.Count > 0 && !tableSet.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            var fragPct = rdr.GetDouble(2);
            results.Add(new FragmentationInfo
            {
                TableName = name,
                IndexName = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                FragmentationPct = fragPct,
                PageCount = rdr.GetInt64(3),
                Recommendation = fragPct switch
                {
                    > 30 => "REBUILD (ONLINE=ON)",
                    > 10 => "REORGANIZE",
                    _ => "OK"
                }
            });
        }
        return results;
    }

    private List<MissingIndexInfo> QueryMissingIndexes()
    {
        var results = new List<MissingIndexInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT TOP 30
                OBJECT_NAME(d.object_id) AS TableName,
                ISNULL(d.equality_columns, '') AS EqualityColumns,
                d.inequality_columns, d.included_columns,
                CAST(s.avg_total_user_cost * s.avg_user_impact * (s.user_seeks + s.user_scans) AS DECIMAL(18,2)) AS ImpactScore,
                s.user_seeks,
                'CREATE NONCLUSTERED INDEX [IX_' + OBJECT_NAME(d.object_id) + '_missing_'
                    + CAST(d.index_handle AS VARCHAR) + '] ON '
                    + d.statement + ' (' + ISNULL(d.equality_columns, '')
                    + CASE WHEN d.inequality_columns IS NOT NULL
                        THEN CASE WHEN d.equality_columns IS NOT NULL THEN ', ' ELSE '' END + d.inequality_columns
                        ELSE '' END + ')'
                    + CASE WHEN d.included_columns IS NOT NULL
                        THEN ' INCLUDE (' + d.included_columns + ')' ELSE '' END AS CreateStatement
            FROM sys.dm_db_missing_index_details d
            INNER JOIN sys.dm_db_missing_index_groups g ON d.index_handle = g.index_handle
            INNER JOIN sys.dm_db_missing_index_group_stats s ON g.index_group_handle = s.group_handle
            WHERE d.database_id = DB_ID()
            ORDER BY ImpactScore DESC";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new MissingIndexInfo
            {
                TableName = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                EqualityColumns = rdr.GetString(1),
                InequalityColumns = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                IncludeColumns = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                ImpactScore = (double)rdr.GetDecimal(4),
                UserSeeks = rdr.GetInt64(5),
                CreateStatement = rdr.GetString(6)
            });
        }
        return results;
    }

    private List<StaleStatisticsInfo> QueryStaleStatistics(IEnumerable<string> tableNames)
    {
        var results = new List<StaleStatisticsInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT t.name AS TableName, s.name AS StatName,
                STATS_DATE(s.object_id, s.stats_id) AS LastUpdated,
                p.rows AS RowCount,
                ISNULL(sp.modification_counter, 0) AS ModCount,
                CASE WHEN p.rows > 0
                    THEN CAST(ISNULL(sp.modification_counter, 0) * 100.0 / p.rows AS DECIMAL(10,2))
                    ELSE 0 END AS ModPct
            FROM sys.stats s
            INNER JOIN sys.tables t ON s.object_id = t.object_id
            INNER JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id <= 1
            CROSS APPLY sys.dm_db_stats_properties(s.object_id, s.stats_id) sp
            WHERE sp.modification_counter > 0
            ORDER BY ModPct DESC";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        var tableSet = new HashSet<string>(tableNames.Select(NormalizeSqlName), StringComparer.OrdinalIgnoreCase);

        while (rdr.Read())
        {
            var name = rdr.GetString(0);
            if (tableSet.Count > 0 && !tableSet.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new StaleStatisticsInfo
            {
                TableName = name,
                StatisticsName = rdr.GetString(1),
                LastUpdated = rdr.IsDBNull(2) ? null : rdr.GetDateTime(2),
                RowCount = rdr.GetInt64(3),
                ModificationCount = rdr.GetInt64(4),
                ModificationPct = (double)rdr.GetDecimal(5)
            });
        }
        return results;
    }

    private List<LockContentionInfo> QueryLockContention(IEnumerable<string> tableNames)
    {
        var results = new List<LockContentionInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT OBJECT_NAME(os.object_id) AS TableName,
                i.name AS IndexName,
                os.row_lock_wait_count, os.row_lock_wait_in_ms,
                os.page_lock_wait_count, os.page_lock_wait_in_ms
            FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) os
            INNER JOIN sys.indexes i ON os.object_id = i.object_id AND os.index_id = i.index_id
            WHERE (os.row_lock_wait_count > 0 OR os.page_lock_wait_count > 0)
            ORDER BY os.row_lock_wait_in_ms DESC";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        var tableSet = new HashSet<string>(tableNames.Select(NormalizeSqlName), StringComparer.OrdinalIgnoreCase);

        while (rdr.Read())
        {
            var name = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
            if (tableSet.Count > 0 && !tableSet.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new LockContentionInfo
            {
                TableName = name,
                IndexName = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                RowLockWaitCount = rdr.GetInt64(2),
                RowLockWaitMs = rdr.GetDouble(3),
                PageLockWaitCount = rdr.GetInt64(4),
                PageLockWaitMs = rdr.GetDouble(5)
            });
        }
        return results;
    }

    private List<WaitStatsInfo> QueryWaitStats()
    {
        var results = new List<WaitStatsInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT TOP 15
                wait_type,
                waiting_tasks_count,
                CAST(wait_time_ms / 1000.0 AS DECIMAL(18,2)) AS WaitTimeSec,
                CAST(signal_wait_time_ms / 1000.0 AS DECIMAL(18,2)) AS SignalWaitSec,
                CASE WHEN waiting_tasks_count > 0
                    THEN CAST(wait_time_ms * 1.0 / waiting_tasks_count AS DECIMAL(18,2))
                    ELSE 0 END AS AvgWaitMs
            FROM sys.dm_os_wait_stats
            WHERE wait_type NOT IN (
                'CLR_SEMAPHORE','LAZYWRITER_SLEEP','RESOURCE_QUEUE','SQLTRACE_BUFFER_FLUSH',
                'SLEEP_TASK','SLEEP_SYSTEMTASK','WAITFOR','HADR_FILESTREAM_IOMGR_IOCOMPLETION',
                'CHECKPOINT_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT','XE_DISPATCH_WAIT',
                'BROKER_TO_FLUSH','BROKER_TASK_STOP','CLR_MANUAL_EVENT','CLR_AUTO_EVENT',
                'DISPATCHER_QUEUE_SEMAPHORE','FT_IFTS_SCHEDULER_IDLE_WAIT','XE_LIVE_TARGET_TVF',
                'LOGMGR_QUEUE','ONDEMAND_TASK_QUEUE','WAIT_FOR_RESULTS','BROKER_EVENTHANDLER',
                'TRACEWRITE','FT_IFTSHC_MUTEX','SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
                'BROKER_RECEIVE_WAITFOR','DIRTY_PAGE_POLL','SP_SERVER_DIAGNOSTICS_SLEEP',
                'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
                'QDS_SHUTDOWN_QUEUE'
            )
            AND waiting_tasks_count > 0
            ORDER BY wait_time_ms DESC";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new WaitStatsInfo
            {
                WaitType = rdr.GetString(0),
                WaitCount = rdr.GetInt64(1),
                WaitTimeSeconds = (double)rdr.GetDecimal(2),
                SignalWaitTimeSeconds = (double)rdr.GetDecimal(3),
                AvgWaitMs = (double)rdr.GetDecimal(4)
            });
        }
        return results;
    }

    private List<IoLatencyInfo> QueryIoLatency()
    {
        var results = new List<IoLatencyInfo>();
        using var conn = OpenConnection();
        var sql = @"
            SELECT DB_NAME(fs.database_id) AS DatabaseName,
                mf.type_desc AS FileType,
                CASE WHEN fs.num_of_reads > 0
                    THEN CAST(fs.io_stall_read_ms * 1.0 / fs.num_of_reads AS DECIMAL(18,2))
                    ELSE 0 END AS ReadLatencyMs,
                CASE WHEN fs.num_of_writes > 0
                    THEN CAST(fs.io_stall_write_ms * 1.0 / fs.num_of_writes AS DECIMAL(18,2))
                    ELSE 0 END AS WriteLatencyMs,
                fs.num_of_reads, fs.num_of_writes,
                CAST(fs.size_on_disk_bytes / 1048576.0 AS DECIMAL(18,2)) AS SizeMB
            FROM sys.dm_io_virtual_file_stats(NULL, NULL) fs
            INNER JOIN sys.master_files mf ON fs.database_id = mf.database_id AND fs.file_id = mf.file_id
            ORDER BY ReadLatencyMs DESC";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            results.Add(new IoLatencyInfo
            {
                DatabaseName = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                FileType = rdr.GetString(1),
                ReadLatencyMs = (double)rdr.GetDecimal(2),
                WriteLatencyMs = (double)rdr.GetDecimal(3),
                NumReads = rdr.GetInt64(4),
                NumWrites = rdr.GetInt64(5),
                SizeMB = (double)rdr.GetDecimal(6)
            });
        }
        return results;
    }

    private PerfMonitoringSummary? QueryPerfMonitoringSummary()
    {
        using var conn = OpenConnection();
        // Zkusíme, jestli tabulka PerformanceMonitoring_Master existuje
        var checkSql = @"SELECT COUNT(*) FROM sys.tables WHERE name = 'PerformanceMonitoring_Master'";
        using var checkCmd = new SqlCommand(checkSql, conn);
        var exists = (int)checkCmd.ExecuteScalar() > 0;
        if (!exists)
        {
            _log.Warning("Tabulka PerformanceMonitoring_Master neexistuje — sekce PerfMonitoring přeskočena.");
            return null;
        }

        var sql = @"
            DECLARE @Start DATETIME2 = DATEADD(DAY, -30, GETDATE());
            SELECT
                COUNT(*) AS TotalSnapshots,
                COUNT(CASE WHEN Overall_Status = 'CRITICAL' THEN 1 END) AS CriticalCount,
                CAST(COUNT(CASE WHEN Overall_Status = 'CRITICAL' THEN 1 END) * 100.0 / NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS CriticalPct,
                AVG(CAST(SQL_CPU AS FLOAT)) AS AvgCpuSql,
                MAX(CAST(SQL_CPU AS FLOAT)) AS MaxCpuSql,
                AVG(CAST(System_Idle AS FLOAT)) AS AvgCpuIdle,
                AVG(CAST(Page_Life_Expectancy AS FLOAT)) AS AvgPLE,
                MIN(CAST(Page_Life_Expectancy AS FLOAT)) AS MinPLE,
                AVG(CAST(Memory_Used_MB AS FLOAT)) AS AvgMemoryUsedMB,
                AVG(CAST(Data_Read_Latency_ms AS FLOAT)) AS AvgIoReadLat,
                MAX(CAST(Data_Read_Latency_ms AS FLOAT)) AS MaxIoReadLat,
                AVG(CAST(Data_Write_Latency_ms AS FLOAT)) AS AvgIoWriteLat,
                MAX(CAST(Data_Write_Latency_ms AS FLOAT)) AS MaxIoWriteLat,
                AVG(CAST(Blocked_Sessions AS FLOAT)) AS AvgBlocked,
                MAX(CAST(Blocked_Sessions AS INT)) AS MaxBlocked,
                COUNT(CASE WHEN CAST(Blocked_Sessions AS INT) > 0 THEN 1 END) AS BlockedSnapshots,
                MIN(SnapshotTime) AS PeriodStart,
                MAX(SnapshotTime) AS PeriodEnd
            FROM dbo.PerformanceMonitoring_Master WITH (NOLOCK)
            WHERE SnapshotTime >= @Start";

        using var cmd = new SqlCommand(sql, conn);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read() || rdr.GetInt32(0) == 0) return null;

        return new PerfMonitoringSummary
        {
            TotalSnapshots = rdr.GetInt32(0),
            CriticalCount = rdr.GetInt32(1),
            CriticalPct = rdr.IsDBNull(2) ? 0 : (double)rdr.GetDecimal(2),
            AvgCpuSql = rdr.IsDBNull(3) ? 0 : rdr.GetDouble(3),
            MaxCpuSql = rdr.IsDBNull(4) ? 0 : rdr.GetDouble(4),
            AvgCpuIdle = rdr.IsDBNull(5) ? 0 : rdr.GetDouble(5),
            AvgPLE = rdr.IsDBNull(6) ? 0 : rdr.GetDouble(6),
            MinPLE = rdr.IsDBNull(7) ? 0 : rdr.GetDouble(7),
            AvgMemoryUsedMB = rdr.IsDBNull(8) ? 0 : rdr.GetDouble(8),
            AvgIoReadLatency = rdr.IsDBNull(9) ? 0 : rdr.GetDouble(9),
            MaxIoReadLatency = rdr.IsDBNull(10) ? 0 : rdr.GetDouble(10),
            AvgIoWriteLatency = rdr.IsDBNull(11) ? 0 : rdr.GetDouble(11),
            MaxIoWriteLatency = rdr.IsDBNull(12) ? 0 : rdr.GetDouble(12),
            AvgBlockedSessions = rdr.IsDBNull(13) ? 0 : rdr.GetDouble(13),
            MaxBlockedSessions = rdr.IsDBNull(14) ? 0 : rdr.GetInt32(14),
            BlockedSnapshotCount = rdr.GetInt32(15),
            PeriodStart = rdr.GetDateTime(16),
            PeriodEnd = rdr.GetDateTime(17)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private SqlConnection OpenConnection()
    {
        var conn = new SqlConnection(_cfg.ConnectionString);
        conn.Open();
        return conn;
    }

    private T Safe<T>(string name, Func<T> action, CancellationToken ct = default) where T : new()
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            _log.Info($"SQL [{name}]: START");
            var result = action();
            var count = result is System.Collections.ICollection col ? col.Count : 1;
            _log.Info($"SQL [{name}]: OK ({count} záznamů)");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"SQL [{name}]: CHYBA — {ex.Message}");
            return new T();
        }
    }

    /// <summary>Normalizuje BC table name pro SQL lookup — odstraní company prefix.</summary>
    private static string NormalizeSqlName(string bcTableName)
    {
        // "CURRENTCOMPANY$Sales Line" → "Sales Line"
        // "Record Link" → "Record Link"
        var idx = bcTableName.IndexOf('$');
        return idx >= 0 ? bcTableName[(idx + 1)..] : bcTableName;
    }
}
