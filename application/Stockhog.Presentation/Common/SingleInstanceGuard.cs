using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Stockhog.Presentation.Common;

/// <summary>
/// Lets one copy of the app run per Windows session. Two copies would both run the autopilot, the order
/// reconciliation and the risk guards against the same account and database, so a second start only brings the
/// running window to the front.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultName = @"Local\Stockhog.App.SingleInstance";

    private readonly Mutex _mutex;

    public SingleInstanceGuard(string name = DefaultName)
    {
        _mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>Whether no other copy held the name when this one started.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>Restores and focuses the main window of another running copy, if there is one.</summary>
    /// <returns>Whether a window was found.</returns>
    public static bool ActivateOtherInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                    continue;

                if (IsIconic(other.MainWindowHandle))
                    ShowWindow(other.MainWindowHandle, RestoreCommand);
                SetForegroundWindow(other.MainWindowHandle);
                return true;
            }
        }

        return false;
    }

    public void Dispose() => _mutex.Dispose();

    private const int RestoreCommand = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
