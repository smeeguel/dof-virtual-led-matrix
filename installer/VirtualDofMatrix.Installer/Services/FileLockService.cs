using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualDofMatrix.Installer.Services;

/// <summary>
/// Uses the Windows Restart Manager API to find which processes are holding a file open.
/// </summary>
internal static class FileLockService
{
    public static IReadOnlyList<string> GetLockingProcessNames(string filePath)
    {
        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out var session, 0, key) != 0)
            return Array.Empty<string>();

        try
        {
            if (RmRegisterResources(session, 1, new[] { filePath }, 0, null!, 0, null!) != 0)
                return Array.Empty<string>();

            uint needed = 0, count = 0, rebootReasons = 0;
            RmGetList(session, out needed, ref count, null!, ref rebootReasons);

            if (needed == 0)
                return Array.Empty<string>();

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, infos, ref rebootReasons) != 0)
                return Array.Empty<string>();

            return infos
                .Take((int)count)
                .Select(p => p.strAppName.Trim())
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    // --- P/Invoke ---

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint dwSessionHandle,
        uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[] rgApplications,
        uint nServices, string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public int TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }
}
