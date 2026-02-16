# BC SQL Performance Analyzer v2.0

Diagnostický nástroj pro analýzu výkonu Microsoft Dynamics NAV / Business Central.

## Co dělá

1. **Čte BC Slow SQL eventy** z Event Logu vzdáleného serveru (B-S-W-NAV-01)
2. **Spouští SQL diagnostiku** na SQL Serveru (B-S-W-SQL-01) — velikosti tabulek, indexy, fragmentace, missing indexes, statistiky, lock contention, wait stats, I/O latence, Performance Monitoring
3. **Koreluje data** — propojuje event log s SQL diagnostikou, počítá severity, generuje findings a doporučení
4. **Porovnává s předchozím během** — JSON snapshoty, trend analýza
5. **Generuje HTML report** — IBM Plex Sans styl, KPI karty, per-table analýza, server health, doporučení

## DŮLEŽITÉ

**Aplikace je čistě READ-ONLY. Žádné modifikace dat ani struktury databáze.**

## Struktura

- **BCAnalyzer.Core** — class library, veškerá logika
  - `Configuration/AnalyzerSettings.cs` — servery, thresholdy
  - `Models/Models.cs` — datové modely
  - `Services/EventLogCollector.cs` — čtení vzdáleného Event Logu
  - `Services/SqlAnalyzer.cs` — SQL diagnostické dotazy (SELECT only)
  - `Services/DataCorrelator.cs` — agregace, korelace, vyhodnocení
  - `Services/HistoryManager.cs` — JSON snapshoty, porovnání
  - `Services/HtmlReportGenerator.cs` — HTML report s IBM Plex stylem
  - `Services/AnalysisOrchestrator.cs` — orchestrace celého workflow
  - `Logging/AnalyzerLogger.cs` — logger s region timing
- **BCAnalyzer.Gui** — WPF aplikace
  - `Views/MainWindow.xaml` — UI s progress, logem, tlačítky
  - `appsettings.json` — konfigurace

## Požadavky

- .NET 8 SDK
- Windows (WPF + Event Log)
- Event Log Readers oprávnění na B-S-W-NAV-01
- VIEW SERVER STATE oprávnění na B-S-W-SQL-01
- Spuštění jako Administrator (pro Event Log)

## Build a spuštění

```
dotnet restore
dotnet build
```

Spuštění: `dotnet run --project src\BCAnalyzer.Gui` nebo F5 ve Visual Studiu.

## Konfigurace

Editovat `appsettings.json` vedle exe, nebo přímo v GUI.

## Výstupy

- `Output/` — HTML reporty
- `History/` — JSON snapshoty pro porovnání
- `Logs/` — textové logy běhů
