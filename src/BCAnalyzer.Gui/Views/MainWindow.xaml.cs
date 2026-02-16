using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using BCAnalyzer.Core.Configuration;
using BCAnalyzer.Core.Logging;
using BCAnalyzer.Core.Services;
using BCAnalyzer.Core.Models;

namespace BCAnalyzer.Gui.Views;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private string? _lastReportPath;

    public MainWindow()
    {
        InitializeComponent();
        TxtCurrentUser.Text = $"({Environment.UserDomainName}\\{Environment.UserName})";
    }

    // ── Credentials UI ───────────────────────────────────────────────────

    private void ChkIntegrated_Changed(object sender, RoutedEventArgs e)
    {
        if (PnlCredentials == null) return;
        PnlCredentials.Visibility = ChkIntegrated.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ── Connection Test (s impersonací) ──────────────────────────────────

    private async void BtnTestConn_Click(object sender, RoutedEventArgs e)
    {
        BtnTestConn.IsEnabled = false;
        BtnTestConn.Content = "⏳ Testuji...";
        LstLog.Items.Clear();

        var cfg = BuildSettings();
        var results = new List<string>();

        await Task.Run(() =>
        {
            // Celý test běží pod impersonovanou identitou
            ImpersonationHelper.RunAs(cfg, () =>
            {
                // Test 1: SQL Server (Integrated Security pod impersonovaným účtem)
                try
                {
                    using var conn = new SqlConnection(cfg.ConnectionString);
                    conn.Open();
                    using var cmd = new SqlCommand("SELECT @@SERVERNAME, SYSTEM_USER", conn);
                    using var rdr = cmd.ExecuteReader();
                    if (rdr.Read())
                        results.Add($"✅ SQL Server: {rdr.GetString(0)} — přihlášen jako {rdr.GetString(1)}");
                }
                catch (Exception ex)
                {
                    results.Add($"❌ SQL Server ({cfg.SqlServer}): {ex.Message}");
                }

                // Test 2: Event Log
                try
                {
                    var session = new System.Diagnostics.Eventing.Reader.EventLogSession(cfg.NavServer);
                    var xpath = $"*[System[Provider[@Name='{cfg.EventSource}'] and (EventID={cfg.EventId})]]";
                    var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                        cfg.EventLogName,
                        System.Diagnostics.Eventing.Reader.PathType.LogName, xpath)
                    {
                        Session = session,
                        ReverseDirection = true
                    };
                    using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
                    var rec = reader.ReadEvent();
                    var msg = rec != null
                        ? $"✅ Event Log: {cfg.NavServer} — připojení OK (nalezeny eventy)"
                        : $"⚠️ Event Log: {cfg.NavServer} — připojení OK, ale žádné eventy nenalezeny";
                    rec?.Dispose();
                    results.Add(msg);
                }
                catch (UnauthorizedAccessException ex)
                {
                    results.Add($"❌ Event Log ({cfg.NavServer}): Přístup odepřen — {ex.Message}");
                    results.Add("   Tip: Účet potřebuje 'Event Log Readers' na vzdáleném serveru.");
                }
                catch (Exception ex)
                {
                    results.Add($"❌ Event Log ({cfg.NavServer}): {ex.Message}");
                }
            });
        });

        foreach (var r in results)
        {
            var item = new ListBoxItem { Content = r };
            if (r.StartsWith("✅")) item.Foreground = Brushes.Green;
            else if (r.StartsWith("❌")) item.Foreground = Brushes.Red;
            else if (r.StartsWith("⚠️")) item.Foreground = Brushes.DarkOrange;
            LstLog.Items.Add(item);
        }

        BtnTestConn.Content = "🔗 Test";
        BtnTestConn.IsEnabled = true;
    }

    // ── Run Analysis ─────────────────────────────────────────────────────

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        BtnRun.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        BtnOpenReport.IsEnabled = false;
        LstLog.Items.Clear();
        Prog.Value = 0;
        _cts = new CancellationTokenSource();

        var cfg = BuildSettings();

        var logDir = Path.GetFullPath(cfg.LogPath);
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"BCAnalyzer_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        AnalysisResult? result = null;

        using (var log = new AnalyzerLogger(logFile))
        {
            log.OnLog += (level, msg) => Dispatcher.Invoke(() =>
            {
                var item = new ListBoxItem { Content = msg };
                if (level == "ERROR") item.Foreground = Brushes.Red;
                else if (level == "WARNING") item.Foreground = Brushes.DarkOrange;
                else if (level == "OK") item.Foreground = Brushes.Green;
                LstLog.Items.Add(item);
                LstLog.ScrollIntoView(LstLog.Items[^1]);
            });

            var orch = new AnalysisOrchestrator(cfg, log);
            orch.OnPhaseChanged += (desc, pct) => Dispatcher.Invoke(() =>
            {
                Prog.Value = pct;
                TxtStatus.Text = desc;
            });

            // Orchestrátor interně používá ImpersonationHelper.RunAs
            result = await Task.Run(() => orch.Run(_cts.Token));
        }

        if (result != null)
        {
            TxtStatus.Text = result.Success
                ? $"✅ {result.Message}"
                : $"❌ {result.Message}";

            if (result.Success && result.HtmlReportPath != null)
            {
                _lastReportPath = result.HtmlReportPath;
                BtnOpenReport.IsEnabled = true;
                try { Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true }); }
                catch { }
            }
        }

        BtnRun.IsEnabled = true;
        BtnCancel.IsEnabled = false;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TxtStatus.Text = "Ruším...";
        BtnCancel.IsEnabled = false;
    }

    private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath != null && File.Exists(_lastReportPath))
        {
            try { Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"Nelze otevřít report: {ex.Message}"); }
        }
    }

    // ── Build Settings from GUI ──────────────────────────────────────────

    private AnalyzerSettings BuildSettings()
    {
        var cfg = LoadSettings();
        cfg.NavServer = TxtNavServer.Text.Trim();
        cfg.SqlServer = TxtSqlServer.Text.Trim();
        cfg.DatabaseName = TxtDatabase.Text.Trim();
        if (int.TryParse(TxtHours.Text.Trim(), out var h)) cfg.LookbackHours = h;

        cfg.UseIntegratedSecurity = ChkIntegrated.IsChecked == true;
        if (!cfg.UseIntegratedSecurity)
        {
            cfg.Username = TxtUsername.Text.Trim();
            cfg.Password = TxtPassword.Password;
        }

        return cfg;
    }

    private AnalyzerSettings LoadSettings()
    {
        var cfg = new AnalyzerSettings();
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                var config = new ConfigurationBuilder()
                    .AddJsonFile(configPath, optional: true)
                    .Build();

                var section = config.GetSection("Analyzer");
                if (section.Exists())
                {
                    cfg.NavServer = section["NavServer"] ?? cfg.NavServer;
                    cfg.SqlServer = section["SqlServer"] ?? cfg.SqlServer;
                    cfg.DatabaseName = section["DatabaseName"] ?? cfg.DatabaseName;
                    cfg.CompanyPrefix = section["CompanyPrefix"] ?? cfg.CompanyPrefix;
                    cfg.EventSource = section["EventSource"] ?? cfg.EventSource;
                    cfg.OutputPath = section["OutputPath"] ?? cfg.OutputPath;
                    cfg.HistoryPath = section["HistoryPath"] ?? cfg.HistoryPath;
                    cfg.LogPath = section["LogPath"] ?? cfg.LogPath;
                    if (int.TryParse(section["LookbackHours"], out var lh)) cfg.LookbackHours = lh;
                    if (int.TryParse(section["EventId"], out var eid)) cfg.EventId = eid;
                }
            }
        }
        catch { }
        return cfg;
    }
}
