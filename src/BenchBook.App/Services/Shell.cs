using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BenchBook.App.Services;

/// <summary>Explorer, the Recycle Bin and Windows' own formatting.</summary>
internal static partial class Shell
{
    /// <summary>Opens Explorer with the file or folder selected.</summary>
    public static void ShowInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>Opens a folder in Explorer.</summary>
    public static void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>Moves a file or folder to the Recycle Bin. Throws if it can't.</summary>
    public static void MoveToRecycleBin(string path)
    {
        var operation = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"“{Path.GetFileName(path)}” couldn't be moved to the Recycle Bin (error {result}).");
    }

    /// <summary>A size as Explorer shows it, e.g. "1.20 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        var buffer = new char[64];
        unsafe
        {
            fixed (char* text = buffer)
            {
                if (StrFormatByteSizeEx((ulong)Math.Max(0, bytes), SFBS_FLAGS_ROUND_TO_NEAREST_DISPLAYED_DIGIT, text, (uint)buffer.Length) == 0)
                    return new string(text);
            }
        }
        return $"{bytes:N0} bytes";
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const int SFBS_FLAGS_ROUND_TO_NEAREST_DISPLAYED_DIGIT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

#pragma warning disable SYSLIB1054 // The struct holds strings, which LibraryImport can't marshal by reference.
    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);
#pragma warning restore SYSLIB1054

    [LibraryImport("shlwapi.dll")]
    private static unsafe partial int StrFormatByteSizeEx(ulong size, int flags, char* buffer, uint bufferSize);
}
