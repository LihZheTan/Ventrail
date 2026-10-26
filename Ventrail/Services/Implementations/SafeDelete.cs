using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Week 7: moves a file out of the way without destroying it, before "Discard changes"
/// overwrites or removes it. On normal (fixed) drives it goes to the Windows Recycle Bin.
/// USB sticks and network drives have no Recycle Bin (Windows would delete for good), so
/// there the file is copied to %LocalAppData%\Ventrail\Discarded\... first.
/// </summary>
public static class SafeDelete
{
    public const string RecycleBin = "the Recycle Bin";

    /// <summary>Returns where the file went ("the Recycle Bin" or a backup folder path).</summary>
    public static string MoveAway(string fullPath, string relativePath)
    {
        // Fail early with the normal "being used by another process" error if another program
        // has the file open, instead of half-finishing.
        using (new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        if (IsFixedDrive(fullPath) && TrySendToRecycleBin(fullPath))
            return RecycleBin;

        string backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ventrail", "Discarded", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        string backup = Path.Combine(backupDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(fullPath, backup, overwrite: true);
        File.Delete(fullPath);
        return backupDir;
    }

    private static bool IsFixedDrive(string fullPath)
    {
        try
        {
            string? root = Path.GetPathRoot(fullPath);
            return root != null && !root.StartsWith(@"\\") && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException) { return false; }
    }

    // ---- Shell API: SHFileOperation with FOF_ALLOWUNDO = "delete to Recycle Bin" ----

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private static bool TrySendToRecycleBin(string fullPath)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = fullPath + "\0\0",   // list of paths, double-null terminated
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(fullPath);
    }
}
