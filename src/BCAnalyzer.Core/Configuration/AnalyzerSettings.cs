namespace BCAnalyzer.Core.Configuration;

/// <summary>Veškerá konfigurace analyzéru — servery, thresholdy, cesty, credentials.</summary>
public class AnalyzerSettings
{
    // ── Servery ──────────────────────────────────────────────────────────
    public string NavServer { get; set; } = "B-S-W-NAV-01";
    public string SqlServer { get; set; } = "B-S-W-SQL-01";
    public string DatabaseName { get; set; } = "NAV-LIVE";
    public string CompanyPrefix { get; set; } = "AXIMA";

    // ── Credentials ──────────────────────────────────────────────────────
    /// <summary>True = aktuální Windows identity. False = impersonace jiným účtem.</summary>
    public bool UseIntegratedSecurity { get; set; } = true;
    /// <summary>DOMAIN\user nebo user@domain.</summary>
    public string Username { get; set; } = "";
    /// <summary>Heslo — jen v paměti.</summary>
    public string Password { get; set; } = "";

    /// <summary>Parsuje domain a username.</summary>
    public (string domain, string user) ParseCredentials()
    {
        if (string.IsNullOrEmpty(Username)) return ("", "");
        var bs = Username.IndexOf('\\');
        if (bs > 0) return (Username[..bs], Username[(bs + 1)..]);
        var at = Username.IndexOf('@');
        if (at > 0) return (Username[(at + 1)..], Username[..at]);
        return ("", Username);
    }

    // ── Event Log ────────────────────────────────────────────────────────
    public string EventLogName { get; set; } = "Application";
    public string EventSource { get; set; } = "MicrosoftDynamicsNavServer$NAV-LIVE";
    public int EventId { get; set; } = 705;
    public int EventLevel { get; set; } = 3;
    public int LookbackHours { get; set; } = 24;
    public int MinExecutionTimeMs { get; set; } = 1000;

    // ── SQL Connection — vždy Windows Auth, impersonace řeší identitu ────
    public string ConnectionString =>
        $"Server={SqlServer};Database={DatabaseName};Integrated Security=true;" +
        "TrustServerCertificate=true;Connect Timeout=30;Command Timeout=120;";

    // ── Thresholdy ───────────────────────────────────────────────────────
    public int SlowSqlThresholdMs { get; set; } = 1000;
    public int CriticalSqlThresholdMs { get; set; } = 5000;
    public double IoLatencyWarningMs { get; set; } = 20.0;
    public double IoLatencyCriticalMs { get; set; } = 40.0;
    public double FragmentationWarningPct { get; set; } = 30.0;
    public double StaleStatsModPct { get; set; } = 20.0;
    public int RecordLinkMaxRows { get; set; } = 1_000_000;

    // ── Cesty ────────────────────────────────────────────────────────────
    public string OutputPath { get; set; } = @".\Output";
    public string HistoryPath { get; set; } = @".\History";
    public string LogPath { get; set; } = @".\Logs";
    public string QueriesPath { get; set; } = @".\Queries";
    public int HistoryRetentionDays { get; set; } = 90;
}
