namespace BCAnalyzer.Core.Models;

// ═══════════════════════════════════════════════════════════════════════
// EVENT LOG MODELS
// ═══════════════════════════════════════════════════════════════════════

public class SlowSqlEvent
{
    public DateTime EventTime { get; set; }
    public int ExecutionTimeMs { get; set; }
    public int ThresholdMs { get; set; }
    public int OverThresholdMs { get; set; }
    public string ServerInstance { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string AppObjectType { get; set; } = "";
    public int AppObjectId { get; set; }
    public string ALCallStack { get; set; } = "";
    public string SqlStatement { get; set; } = "";
    public string TaskId { get; set; } = "";
    public int SessionId { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
// SQL ANALYSIS MODELS
// ═══════════════════════════════════════════════════════════════════════

public class TableInfo
{
    public string SchemaName { get; set; } = "dbo";
    public string TableName { get; set; } = "";
    public long RowCount { get; set; }
    public double TotalSizeMB { get; set; }
    public double DataSizeMB { get; set; }
    public double IndexSizeMB { get; set; }
}

public class IndexInfo
{
    public string TableName { get; set; } = "";
    public string IndexName { get; set; } = "";
    public string IndexType { get; set; } = "";
    public bool IsUnique { get; set; }
    public bool IsDisabled { get; set; }
    public string KeyColumns { get; set; } = "";
    public string? IncludeColumns { get; set; }
    public int FillFactor { get; set; }
}

public class IndexUsageInfo
{
    public string TableName { get; set; } = "";
    public string IndexName { get; set; } = "";
    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public long UserLookups { get; set; }
    public long UserUpdates { get; set; }
    public DateTime? LastUserSeek { get; set; }
    public DateTime? LastUserScan { get; set; }
}

public class FragmentationInfo
{
    public string TableName { get; set; } = "";
    public string IndexName { get; set; } = "";
    public double FragmentationPct { get; set; }
    public long PageCount { get; set; }
    public string Recommendation { get; set; } = "";
}

public class MissingIndexInfo
{
    public string TableName { get; set; } = "";
    public string EqualityColumns { get; set; } = "";
    public string? InequalityColumns { get; set; }
    public string? IncludeColumns { get; set; }
    public double ImpactScore { get; set; }
    public long UserSeeks { get; set; }
    public string CreateStatement { get; set; } = "";
}

public class StaleStatisticsInfo
{
    public string TableName { get; set; } = "";
    public string StatisticsName { get; set; } = "";
    public DateTime? LastUpdated { get; set; }
    public long RowCount { get; set; }
    public long ModificationCount { get; set; }
    public double ModificationPct { get; set; }
}

public class LockContentionInfo
{
    public string TableName { get; set; } = "";
    public string IndexName { get; set; } = "";
    public long RowLockWaitCount { get; set; }
    public double RowLockWaitMs { get; set; }
    public long PageLockWaitCount { get; set; }
    public double PageLockWaitMs { get; set; }
}

public class WaitStatsInfo
{
    public string WaitType { get; set; } = "";
    public long WaitCount { get; set; }
    public double WaitTimeSeconds { get; set; }
    public double SignalWaitTimeSeconds { get; set; }
    public double AvgWaitMs { get; set; }
}

public class IoLatencyInfo
{
    public string DatabaseName { get; set; } = "";
    public string FileType { get; set; } = "";
    public double ReadLatencyMs { get; set; }
    public double WriteLatencyMs { get; set; }
    public long NumReads { get; set; }
    public long NumWrites { get; set; }
    public double SizeMB { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
// PERFORMANCE MONITORING (from PerformanceMonitoring_Master table)
// ═══════════════════════════════════════════════════════════════════════

public class PerfMonitoringSummary
{
    public int TotalSnapshots { get; set; }
    public int CriticalCount { get; set; }
    public double CriticalPct { get; set; }
    public double AvgCpuSql { get; set; }
    public double MaxCpuSql { get; set; }
    public double AvgCpuIdle { get; set; }
    public double AvgPLE { get; set; }
    public double MinPLE { get; set; }
    public double AvgMemoryUsedMB { get; set; }
    public double AvgIoReadLatency { get; set; }
    public double MaxIoReadLatency { get; set; }
    public double AvgIoWriteLatency { get; set; }
    public double MaxIoWriteLatency { get; set; }
    public double AvgBlockedSessions { get; set; }
    public int MaxBlockedSessions { get; set; }
    public int BlockedSnapshotCount { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
// AGGREGATED / CORRELATION MODELS
// ═══════════════════════════════════════════════════════════════════════

public class TableAnalysis
{
    public string TableName { get; set; } = "";
    public int SlowSqlCount { get; set; }
    public int MaxExecutionTimeMs { get; set; }
    public long SumExecutionTimeMs { get; set; }
    public double AvgExecutionTimeMs { get; set; }
    public string TopCallerObject { get; set; } = "";
    public int TopCallerCount { get; set; }
    public TableInfo? Size { get; set; }
    public List<IndexInfo> Indexes { get; set; } = new();
    public List<FragmentationInfo> Fragmentation { get; set; } = new();
    public List<MissingIndexInfo> MissingIndexes { get; set; } = new();
    public List<StaleStatisticsInfo> StaleStats { get; set; } = new();
    public LockContentionInfo? LockContention { get; set; }
    public string Severity { get; set; } = "OK"; // OK, WARNING, CRITICAL
    public List<string> Findings { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class HourlyDistribution
{
    public int Hour { get; set; }
    public int Count { get; set; }
    public long SumMs { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
// SNAPSHOT / HISTORY
// ═══════════════════════════════════════════════════════════════════════

public class AnalysisSnapshot
{
    public DateTime RunTime { get; set; } = DateTime.Now;
    public string ServerName { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public int TotalSlowSqlEvents { get; set; }
    public long TotalSlowSqlTimeMs { get; set; }
    public int MaxExecutionTimeMs { get; set; }
    public List<TableAnalysis> Tables { get; set; } = new();
    public List<WaitStatsInfo> WaitStats { get; set; } = new();
    public List<IoLatencyInfo> IoLatency { get; set; } = new();
    public PerfMonitoringSummary? PerfSummary { get; set; }
    public List<HourlyDistribution> HourlyDistribution { get; set; } = new();
    public string? HtmlReportPath { get; set; }
}

public class ComparisonResult
{
    public AnalysisSnapshot Current { get; set; } = new();
    public AnalysisSnapshot? Previous { get; set; }
    public double EventCountChangePct { get; set; }
    public double TotalTimeChangePct { get; set; }
    public List<string> Improvements { get; set; } = new();
    public List<string> Regressions { get; set; } = new();
}

// ═══════════════════════════════════════════════════════════════════════
// ORCHESTRATOR RESULT
// ═══════════════════════════════════════════════════════════════════════

public class AnalysisResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? HtmlReportPath { get; set; }
    public AnalysisSnapshot? Snapshot { get; set; }
    public ComparisonResult? Comparison { get; set; }
    public Exception? Error { get; set; }
}
