using System.Text;
using System.Web;
using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Core.Services;

/// <summary>Generuje HTML report ve stylu IBM Plex Sans s kartami a KPI.</summary>
public class HtmlReportGenerator
{
    private readonly AnalyzerSettings _cfg;
    private readonly AnalyzerLogger _log;

    public HtmlReportGenerator(AnalyzerSettings cfg, AnalyzerLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public string Generate(AnalysisSnapshot snapshot, ComparisonResult? comparison)
    {
        _log.Info("Generuji HTML report...");

        var sb = new StringBuilder(64_000);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"cs\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine($"<title>BC Performance Analysis — {snapshot.ServerName} / {snapshot.DatabaseName}</title>");
        AppendStyles(sb);
        sb.AppendLine("</head><body>");
        sb.AppendLine("<div class=\"container\">");

        // Header
        AppendHeader(sb, snapshot);

        // KPI Cards
        AppendKpiCards(sb, snapshot, comparison);

        // Comparison section
        if (comparison?.Previous != null)
            AppendComparison(sb, comparison);

        // Table analysis (critical + warning)
        AppendTableAnalysis(sb, snapshot);

        // Server Health
        if (snapshot.PerfSummary != null)
            AppendServerHealth(sb, snapshot.PerfSummary);

        // Wait Stats
        if (snapshot.WaitStats.Count > 0)
            AppendWaitStats(sb, snapshot.WaitStats);

        // I/O Latency
        if (snapshot.IoLatency.Count > 0)
            AppendIoLatency(sb, snapshot.IoLatency);

        // Hourly Distribution
        if (snapshot.HourlyDistribution.Count > 0)
            AppendHourlyDistribution(sb, snapshot.HourlyDistribution);

        // Recommendations
        AppendRecommendations(sb, snapshot);

        // Positive findings
        AppendPositiveFindings(sb, snapshot);

        // Footer
        AppendFooter(sb, snapshot);

        sb.AppendLine("</div></body></html>");

        var html = sb.ToString();
        var dir = Path.GetFullPath(_cfg.OutputPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"BCAnalysis_{snapshot.RunTime:yyyyMMdd_HHmmss}.html");
        File.WriteAllText(path, html, Encoding.UTF8);

        _log.Success($"HTML report: {path}");
        return path;
    }

    // ═══════════════════════════════════════════════════════════════════
    // SECTIONS
    // ═══════════════════════════════════════════════════════════════════

    private void AppendHeader(StringBuilder sb, AnalysisSnapshot snap)
    {
        sb.AppendLine("<div class=\"report-header\">");
        sb.AppendLine($"<h1>BC Performance Analysis</h1>");
        sb.AppendLine($"<div class=\"report-subtitle\">{E(snap.ServerName)} / {E(snap.DatabaseName)} &mdash; {snap.RunTime:dd.MM.yyyy HH:mm}</div>");
        sb.AppendLine("</div>");
    }

    private void AppendKpiCards(StringBuilder sb, AnalysisSnapshot snap, ComparisonResult? cmp)
    {
        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Přehled</h2>");
        sb.AppendLine("<div class=\"kpi-grid\">");

        KpiCard(sb, "Slow SQL eventů", $"{snap.TotalSlowSqlEvents:N0}",
            cmp?.Previous != null ? TrendBadge(cmp.EventCountChangePct, true) : "", "events");
        KpiCard(sb, "Celkový ztracený čas", $"{snap.TotalSlowSqlTimeMs / 1000.0:N1} s",
            cmp?.Previous != null ? TrendBadge(cmp.TotalTimeChangePct, true) : "", "time");
        KpiCard(sb, "Max dotaz", $"{snap.MaxExecutionTimeMs:N0} ms", "", "max");
        KpiCard(sb, "Problémové tabulky",
            $"{snap.Tables.Count(t => t.Severity == "CRITICAL")} kritických, {snap.Tables.Count(t => t.Severity == "WARNING")} varování",
            "", "tables");

        sb.AppendLine("</div></div>");
    }

    private void AppendComparison(StringBuilder sb, ComparisonResult cmp)
    {
        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Porovnání s předchozím během</h2>");
        sb.AppendLine($"<p class=\"section-desc\">Předchozí: {cmp.Previous!.RunTime:dd.MM.yyyy HH:mm} ({cmp.Previous.TotalSlowSqlEvents} eventů, {cmp.Previous.TotalSlowSqlTimeMs / 1000.0:N1} s)</p>");

        // Comparison bars
        sb.AppendLine("<div class=\"comp-grid\">");
        CompBar(sb, "Počet eventů",
            cmp.Previous.TotalSlowSqlEvents, cmp.Current.TotalSlowSqlEvents,
            cmp.EventCountChangePct);
        CompBar(sb, "Celkový čas (s)",
            (int)(cmp.Previous.TotalSlowSqlTimeMs / 1000),
            (int)(cmp.Current.TotalSlowSqlTimeMs / 1000),
            cmp.TotalTimeChangePct);
        sb.AppendLine("</div>");

        if (cmp.Improvements.Count > 0)
        {
            sb.AppendLine("<div class=\"detail-card ok\"><div class=\"detail-card-title\">Zlepšení</div><div class=\"detail-card-body\"><ul>");
            foreach (var imp in cmp.Improvements)
                sb.AppendLine($"<li>{E(imp)}</li>");
            sb.AppendLine("</ul></div></div>");
        }

        if (cmp.Regressions.Count > 0)
        {
            sb.AppendLine("<div class=\"detail-card critical\"><div class=\"detail-card-title\">Zhoršení</div><div class=\"detail-card-body\"><ul>");
            foreach (var reg in cmp.Regressions)
                sb.AppendLine($"<li>{E(reg)}</li>");
            sb.AppendLine("</ul></div></div>");
        }

        sb.AppendLine("</div>");
    }

    private void AppendTableAnalysis(StringBuilder sb, AnalysisSnapshot snap)
    {
        var important = snap.Tables.Where(t => t.Severity != "OK").OrderByDescending(t => t.SlowSqlCount).ToList();
        if (important.Count == 0) return;

        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Analýza problémových tabulek</h2>");
        sb.AppendLine($"<p class=\"section-desc\">{important.Count} tabulek vyžaduje pozornost z celkových {snap.Tables.Count}.</p>");
        sb.AppendLine("<div class=\"detail-grid\">");

        foreach (var ta in important)
        {
            var cssClass = ta.Severity == "CRITICAL" ? "critical" : "warning";
            sb.AppendLine($"<div class=\"detail-card {cssClass}\">");
            sb.AppendLine($"<div class=\"detail-card-title\">{E(ta.TableName)} <span class=\"badge badge-{cssClass}\">{ta.Severity}</span></div>");
            sb.AppendLine("<div class=\"detail-card-body\">");

            // Findings
            foreach (var f in ta.Findings)
                sb.AppendLine($"<p class=\"finding\">{E(f)}</p>");

            // Indexes summary
            if (ta.Indexes.Count > 0)
            {
                sb.AppendLine($"<p class=\"finding\">Indexy: {ta.Indexes.Count} " +
                    $"(clustered: {ta.Indexes.Count(i => i.IndexType == "CLUSTERED")}, " +
                    $"nonclustered: {ta.Indexes.Count(i => i.IndexType == "NONCLUSTERED")})</p>");
            }

            // Recommendations
            if (ta.Recommendations.Count > 0)
            {
                sb.AppendLine("<div class=\"rec-box\">");
                sb.AppendLine("<strong>Doporučení:</strong>");
                foreach (var r in ta.Recommendations)
                    sb.AppendLine($"<div class=\"code-block\">{E(r)}</div>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div></div>");
        }

        sb.AppendLine("</div></div>");
    }

    private void AppendServerHealth(StringBuilder sb, PerfMonitoringSummary perf)
    {
        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Zdraví serveru — Performance Monitoring</h2>");
        sb.AppendLine($"<p class=\"section-desc\">Období {perf.PeriodStart:dd.MM.yyyy} — {perf.PeriodEnd:dd.MM.yyyy} ({perf.TotalSnapshots:N0} snapshotů, 5min interval).</p>");
        sb.AppendLine("<div class=\"health-grid\">");

        HealthCard(sb, "CPU SQL Server", $"{perf.AvgCpuSql:F1} %",
            $"Idle {perf.AvgCpuIdle:F1} % — CPU není úzké hrdlo",
            perf.AvgCpuSql, 100, perf.AvgCpuSql < 30 ? "green" : perf.AvgCpuSql < 70 ? "amber" : "red");

        HealthCard(sb, "Page Life Expectancy", $"{perf.AvgPLE:N0} s",
            $"Min {perf.MinPLE:N0} s — stránky vydrží v cache",
            Math.Min(perf.AvgPLE / 500, 100), 100, perf.AvgPLE > 300 ? "green" : perf.AvgPLE > 60 ? "amber" : "red");

        HealthCard(sb, "I/O Latence čtení dat", $"{perf.AvgIoReadLatency:F1} ms",
            $"Max {perf.MaxIoReadLatency:F1} ms — limit <20 ms",
            Math.Min(perf.AvgIoReadLatency / 50 * 100, 100), 100,
            perf.AvgIoReadLatency < 20 ? "green" : perf.AvgIoReadLatency < 40 ? "amber" : "red");

        HealthCard(sb, "I/O Latence zápisu dat", $"{perf.AvgIoWriteLatency:F1} ms",
            $"Max {perf.MaxIoWriteLatency:F1} ms",
            Math.Min(perf.AvgIoWriteLatency / 20 * 100, 100), 100,
            perf.AvgIoWriteLatency < 5 ? "green" : perf.AvgIoWriteLatency < 15 ? "amber" : "red");

        HealthCard(sb, "Blokované sessions", $"avg {perf.AvgBlockedSessions:F1}, max {perf.MaxBlockedSessions}",
            $"{perf.BlockedSnapshotCount} snapshotů s blokováním z {perf.TotalSnapshots}",
            perf.AvgBlockedSessions / 10 * 100, 100,
            perf.MaxBlockedSessions < 3 ? "green" : perf.MaxBlockedSessions < 10 ? "amber" : "red");

        HealthCard(sb, "Kritické stavy", $"{perf.CriticalPct:F1} %",
            $"{perf.CriticalCount} z {perf.TotalSnapshots} snapshotů",
            perf.CriticalPct, 100,
            perf.CriticalPct < 5 ? "green" : perf.CriticalPct < 15 ? "amber" : "red");

        sb.AppendLine("</div></div>");
    }

    private void AppendWaitStats(StringBuilder sb, List<WaitStatsInfo> waits)
    {
        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Wait Statistics — Top 10</h2>");
        sb.AppendLine("<table class=\"data-table\"><thead><tr>");
        sb.AppendLine("<th>Wait Type</th><th>Count</th><th>Wait Time (s)</th><th>Avg Wait (ms)</th>");
        sb.AppendLine("</tr></thead><tbody>");

        foreach (var w in waits.Take(10))
        {
            sb.AppendLine($"<tr><td><code>{E(w.WaitType)}</code></td>" +
                          $"<td class=\"num\">{w.WaitCount:N0}</td>" +
                          $"<td class=\"num\">{w.WaitTimeSeconds:N0}</td>" +
                          $"<td class=\"num\">{w.AvgWaitMs:N1}</td></tr>");
        }

        sb.AppendLine("</tbody></table></div>");
    }

    private void AppendIoLatency(StringBuilder sb, List<IoLatencyInfo> ios)
    {
        var dbIo = ios.Where(i => i.DatabaseName.Equals(_cfg.DatabaseName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (dbIo.Count == 0) dbIo = ios.Take(6).ToList();

        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">I/O Latence — databázové soubory</h2>");
        sb.AppendLine("<table class=\"data-table\"><thead><tr>");
        sb.AppendLine("<th>Databáze</th><th>Typ</th><th>Read (ms)</th><th>Write (ms)</th><th>Reads</th><th>Writes</th><th>Velikost</th>");
        sb.AppendLine("</tr></thead><tbody>");

        foreach (var io in dbIo)
        {
            var readClass = io.ReadLatencyMs > 40 ? "val-red" : io.ReadLatencyMs > 20 ? "val-amber" : "val-green";
            sb.AppendLine($"<tr><td>{E(io.DatabaseName)}</td><td>{E(io.FileType)}</td>" +
                          $"<td class=\"num {readClass}\">{io.ReadLatencyMs:F1}</td>" +
                          $"<td class=\"num\">{io.WriteLatencyMs:F1}</td>" +
                          $"<td class=\"num\">{io.NumReads:N0}</td>" +
                          $"<td class=\"num\">{io.NumWrites:N0}</td>" +
                          $"<td class=\"num\">{io.SizeMB:N0} MB</td></tr>");
        }

        sb.AppendLine("</tbody></table></div>");
    }

    private void AppendHourlyDistribution(StringBuilder sb, List<HourlyDistribution> hours)
    {
        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Distribuce po hodinách</h2>");
        sb.AppendLine("<div class=\"hourly-chart\">");

        var maxCount = hours.Max(h => h.Count);
        for (int h = 0; h < 24; h++)
        {
            var hd = hours.FirstOrDefault(x => x.Hour == h);
            var count = hd?.Count ?? 0;
            var widthPct = maxCount > 0 ? count * 100 / maxCount : 0;
            var color = count == 0 ? "#eee" : count > maxCount * 0.7 ? "var(--red)" :
                        count > maxCount * 0.3 ? "var(--amber)" : "var(--green)";
            sb.AppendLine($"<div class=\"hour-row\">");
            sb.AppendLine($"<span class=\"hour-label\">{h:D2}:00</span>");
            sb.AppendLine($"<div class=\"hour-bar-bg\"><div class=\"hour-bar\" style=\"width:{widthPct}%;background:{color}\"></div></div>");
            sb.AppendLine($"<span class=\"hour-count\">{count}</span>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div></div>");
    }

    private void AppendRecommendations(StringBuilder sb, AnalysisSnapshot snap)
    {
        var allRecs = snap.Tables
            .Where(t => t.Severity != "OK" && t.Recommendations.Count > 0)
            .OrderByDescending(t => t.Severity == "CRITICAL" ? 1 : 0)
            .ThenByDescending(t => t.SlowSqlCount)
            .ToList();

        if (allRecs.Count == 0) return;

        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Doporučení</h2>");
        sb.AppendLine("<div class=\"rec-list\">");

        int idx = 1;
        foreach (var ta in allRecs)
        {
            sb.AppendLine("<div class=\"rec-item\">");
            sb.AppendLine($"<div class=\"rec-number\">{idx++}</div>");
            sb.AppendLine("<div class=\"rec-content\">");
            sb.AppendLine($"<h4>{E(ta.TableName)}</h4>");
            foreach (var r in ta.Recommendations)
                sb.AppendLine($"<p><code>{E(r)}</code></p>");
            sb.AppendLine("</div></div>");
        }

        sb.AppendLine("</div></div>");
    }

    private void AppendPositiveFindings(StringBuilder sb, AnalysisSnapshot snap)
    {
        var positives = new List<(string Title, string Desc)>();

        if (snap.PerfSummary is { } p)
        {
            if (p.AvgCpuSql < 20)
                positives.Add(("CPU zatížení je nízké", $"SQL Server využívá průměrně {p.AvgCpuSql:F1} % CPU. Idle {p.AvgCpuIdle:F1} %."));
            if (p.AvgPLE > 1000)
                positives.Add(("Paměť a cache fungují dobře", $"PLE průměr {p.AvgPLE:N0} s. Datové stránky zůstávají v buffer pool dostatečně dlouho."));
            if (p.MaxBlockedSessions < 5)
                positives.Add(("Minimum blokování sessions", $"Max {p.MaxBlockedSessions} blokovaných sessions. Zamykání funguje dobře."));
        }

        if (positives.Count == 0) return;

        sb.AppendLine("<div class=\"section\"><h2 class=\"section-title\">Pozitivní zjištění</h2>");
        sb.AppendLine("<div class=\"detail-grid\">");
        foreach (var (title, desc) in positives)
        {
            sb.AppendLine("<div class=\"detail-card ok\">");
            sb.AppendLine($"<div class=\"detail-card-title\">{E(title)}</div>");
            sb.AppendLine($"<div class=\"detail-card-body\">{E(desc)}</div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div></div>");
    }

    private void AppendFooter(StringBuilder sb, AnalysisSnapshot snap)
    {
        sb.AppendLine("<div class=\"report-footer\">");
        sb.AppendLine($"<span>BC Performance Analysis — {E(snap.ServerName)} / {E(snap.DatabaseName)}</span>");
        sb.AppendLine($"<span>Generováno: {snap.RunTime:dd.MM.yyyy HH:mm} &middot; BCAnalyzer v2.0 &middot; AXIMA, spol. s r.o.</span>");
        sb.AppendLine("</div>");
    }

    // ═══════════════════════════════════════════════════════════════════
    // COMPONENT HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static void KpiCard(StringBuilder sb, string label, string value, string badge, string icon)
    {
        sb.AppendLine("<div class=\"kpi-card\">");
        sb.AppendLine($"<div class=\"kpi-label\">{label} {badge}</div>");
        sb.AppendLine($"<div class=\"kpi-value\">{value}</div>");
        sb.AppendLine("</div>");
    }

    private static void HealthCard(StringBuilder sb, string label, string value, string desc, double barPct, double barMax, string color)
    {
        sb.AppendLine("<div class=\"health-card\">");
        sb.AppendLine($"<div class=\"health-label\">{label}</div>");
        sb.AppendLine($"<div class=\"health-value\" style=\"color:var(--{color})\">{value}</div>");
        sb.AppendLine($"<div class=\"health-bar\"><div class=\"health-bar-fill\" style=\"width:{Math.Min(barPct, 100):F0}%;background:var(--{color})\"></div></div>");
        sb.AppendLine($"<div class=\"health-desc\">{desc}</div>");
        sb.AppendLine("</div>");
    }

    private static void CompBar(StringBuilder sb, string label, int prev, int curr, double changePct)
    {
        var max = Math.Max(prev, curr);
        var prevW = max > 0 ? prev * 100 / max : 0;
        var currW = max > 0 ? curr * 100 / max : 0;
        var color = changePct < -10 ? "green" : changePct > 10 ? "red" : "amber";

        sb.AppendLine("<div class=\"comp-item\">");
        sb.AppendLine($"<div class=\"comp-label\">{label}</div>");
        sb.AppendLine($"<div class=\"comp-bars\">");
        sb.AppendLine($"<div class=\"comp-bar-row\"><span class=\"comp-tag\">Předchozí</span><div class=\"comp-bar-bg\"><div class=\"comp-bar\" style=\"width:{prevW}%;background:#999\"></div></div><span class=\"comp-val\">{prev:N0}</span></div>");
        sb.AppendLine($"<div class=\"comp-bar-row\"><span class=\"comp-tag\">Aktuální</span><div class=\"comp-bar-bg\"><div class=\"comp-bar\" style=\"width:{currW}%;background:var(--{color})\"></div></div><span class=\"comp-val\">{curr:N0}</span></div>");
        sb.AppendLine($"</div>");
        sb.AppendLine($"<div class=\"comp-change\" style=\"color:var(--{color})\">{changePct:+0;-0;0} %</div>");
        sb.AppendLine("</div>");
    }

    private static string TrendBadge(double pct, bool lowerIsBetter)
    {
        if (Math.Abs(pct) < 3) return "";
        var isGood = lowerIsBetter ? pct < 0 : pct > 0;
        var color = isGood ? "green" : "red";
        var arrow = pct < 0 ? "▼" : "▲";
        return $"<span class=\"badge badge-{color}\">{arrow} {Math.Abs(pct):F0}%</span>";
    }

    private static string E(string s) => HttpUtility.HtmlEncode(s);

    // ═══════════════════════════════════════════════════════════════════
    // CSS STYLES (IBM Plex Sans)
    // ═══════════════════════════════════════════════════════════════════

    private static void AppendStyles(StringBuilder sb)
    {
        sb.AppendLine(@"<style>
@import url('https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@300;400;500;600;700&family=IBM+Plex+Mono:wght@400;500&display=swap');
:root {
  --bg: #f4f5f7; --card: #ffffff; --border: #e2e4e9;
  --text: #1a1a2e; --text2: #5a5d6b; --text3: #8b8fa3;
  --green: #22c55e; --amber: #f59e0b; --red: #ef4444; --blue: #3b82f6;
  --green-bg: #f0fdf4; --amber-bg: #fffbeb; --red-bg: #fef2f2; --blue-bg: #eff6ff;
}
* { margin:0; padding:0; box-sizing:border-box; }
body { font-family:'IBM Plex Sans',sans-serif; background:var(--bg); color:var(--text); line-height:1.6; }
.container { max-width:1200px; margin:0 auto; padding:24px; }
code, .code-block { font-family:'IBM Plex Mono',monospace; font-size:0.82em; }

/* Header */
.report-header { padding:32px 0 24px; border-bottom:3px solid var(--blue); margin-bottom:28px; }
.report-header h1 { font-size:2em; font-weight:700; color:var(--text); }
.report-subtitle { color:var(--text2); font-size:1em; margin-top:4px; }

/* Sections */
.section { margin-bottom:32px; }
.section-title { font-size:1.25em; font-weight:600; margin-bottom:8px; border-bottom:2px solid var(--border); padding-bottom:6px; }
.section-desc { color:var(--text2); font-size:0.9em; margin-bottom:16px; }

/* KPI Grid */
.kpi-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); gap:16px; }
.kpi-card { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:20px; }
.kpi-label { font-size:0.85em; color:var(--text2); font-weight:500; }
.kpi-value { font-size:1.6em; font-weight:700; margin-top:4px; }

/* Detail Cards */
.detail-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(350px,1fr)); gap:16px; }
.detail-card { background:var(--card); border-radius:10px; padding:20px; border-left:4px solid var(--border); }
.detail-card.critical { border-left-color:var(--red); background:var(--red-bg); }
.detail-card.warning { border-left-color:var(--amber); background:var(--amber-bg); }
.detail-card.ok { border-left-color:var(--green); background:var(--green-bg); }
.detail-card-title { font-weight:600; font-size:1em; margin-bottom:8px; }
.detail-card-body { font-size:0.9em; color:var(--text2); }
.detail-card-body ul { margin-left:16px; }
.finding { margin-bottom:6px; font-size:0.88em; }
.rec-box { margin-top:12px; padding:10px; background:rgba(0,0,0,0.03); border-radius:6px; }
.code-block { background:rgba(0,0,0,0.05); padding:6px 10px; border-radius:4px; margin:4px 0; font-size:0.82em; word-break:break-all; }

/* Badges */
.badge { display:inline-block; padding:2px 8px; border-radius:12px; font-size:0.75em; font-weight:600; vertical-align:middle; margin-left:6px; }
.badge-critical, .badge-red { background:var(--red-bg); color:var(--red); }
.badge-warning, .badge-amber { background:var(--amber-bg); color:var(--amber); }
.badge-ok, .badge-green { background:var(--green-bg); color:var(--green); }

/* Health Grid */
.health-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:16px; }
.health-card { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px; text-align:center; }
.health-label { font-size:0.82em; color:var(--text2); font-weight:500; }
.health-value { font-size:1.5em; font-weight:700; margin:4px 0; }
.health-bar { height:6px; background:var(--border); border-radius:3px; margin:8px 0; }
.health-bar-fill { height:100%; border-radius:3px; transition:width 0.5s; }
.health-desc { font-size:0.78em; color:var(--text3); }

/* Comparison */
.comp-grid { display:grid; gap:16px; margin-bottom:16px; }
.comp-item { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px; display:grid; grid-template-columns:120px 1fr 80px; align-items:center; gap:12px; }
.comp-label { font-weight:600; font-size:0.9em; }
.comp-bars { display:flex; flex-direction:column; gap:4px; }
.comp-bar-row { display:flex; align-items:center; gap:8px; }
.comp-tag { font-size:0.72em; color:var(--text3); width:60px; text-align:right; }
.comp-bar-bg { flex:1; height:14px; background:var(--bg); border-radius:3px; overflow:hidden; }
.comp-bar { height:100%; border-radius:3px; }
.comp-val { font-size:0.82em; font-weight:600; min-width:50px; }
.comp-change { font-size:1.2em; font-weight:700; text-align:center; }

/* Data Table */
.data-table { width:100%; border-collapse:collapse; font-size:0.88em; background:var(--card); border-radius:10px; overflow:hidden; }
.data-table th { background:var(--bg); font-weight:600; text-align:left; padding:10px 12px; border-bottom:2px solid var(--border); }
.data-table td { padding:8px 12px; border-bottom:1px solid var(--border); }
.data-table .num { text-align:right; font-family:'IBM Plex Mono',monospace; font-size:0.92em; }
.val-red { color:var(--red); font-weight:600; }
.val-amber { color:var(--amber); font-weight:600; }
.val-green { color:var(--green); }

/* Hourly Chart */
.hourly-chart { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px; }
.hour-row { display:flex; align-items:center; gap:8px; margin:2px 0; }
.hour-label { font-family:'IBM Plex Mono',monospace; font-size:0.8em; color:var(--text2); width:40px; text-align:right; }
.hour-bar-bg { flex:1; height:16px; background:var(--bg); border-radius:3px; overflow:hidden; }
.hour-bar { height:100%; border-radius:3px; }
.hour-count { font-family:'IBM Plex Mono',monospace; font-size:0.8em; width:30px; text-align:right; color:var(--text2); }

/* Recommendations */
.rec-list { display:flex; flex-direction:column; gap:12px; }
.rec-item { display:flex; gap:16px; background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px; }
.rec-number { width:36px; height:36px; background:var(--blue); color:#fff; border-radius:50%; display:flex; align-items:center; justify-content:center; font-weight:700; flex-shrink:0; }
.rec-content h4 { font-size:0.95em; margin-bottom:4px; }
.rec-content p { font-size:0.85em; color:var(--text2); }

/* Footer */
.report-footer { margin-top:40px; padding:16px 0; border-top:2px solid var(--border); display:flex; justify-content:space-between; font-size:0.8em; color:var(--text3); }

@media (max-width:768px) {
  .container { padding:12px; }
  .kpi-grid, .health-grid, .detail-grid { grid-template-columns:1fr; }
  .comp-item { grid-template-columns:1fr; }
  .report-footer { flex-direction:column; gap:4px; }
}
</style>");
    }
}
