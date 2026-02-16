using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BCAnalyzer.Core.Configuration;

namespace BCAnalyzer.Core.Services;

/// <summary>
/// Windows impersonace — přihlásí se jako zadaný doménový uživatel
/// a spustí akci pod jeho identitou (Event Log i SQL Server pak vidí tohoto uživatele).
/// </summary>
public static class ImpersonationHelper
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9; // síťový přístup pod jiným účtem
    private const int LOGON32_PROVIDER_WINNT50 = 3;

    /// <summary>
    /// Spustí akci pod zadaným uživatelem, nebo přímo pokud UseIntegratedSecurity.
    /// </summary>
    public static T RunAs<T>(AnalyzerSettings cfg, Func<T> action)
    {
        if (cfg.UseIntegratedSecurity || string.IsNullOrEmpty(cfg.Username))
            return action();

        var (domain, user) = cfg.ParseCredentials();

        if (!LogonUser(user, domain, cfg.Password,
                LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out var token))
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Impersonace selhala pro {domain}\\{user}: Win32 error {err} — " +
                $"{new Win32Exception(err).Message}");
        }

        try
        {
            return WindowsIdentity.RunImpersonated(
                new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(token),
                () => action());
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Void varianta.</summary>
    public static void RunAs(AnalyzerSettings cfg, Action action)
    {
        RunAs(cfg, () => { action(); return 0; });
    }
}
