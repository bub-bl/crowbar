using System.Runtime.InteropServices;

namespace Crowbar.Editor;

/// <summary>
/// Opens the Windows <c>GetOpenFileName</c> common dialog to pick a file, so the
/// editor can select a game project (<c>.crproj</c>) with the native
/// Explorer window. It runs modally over the editor window (the owner is
/// disabled for the duration) and returns the selected file's full path, or
/// null when the user cancels.
/// </summary>
internal static class NativeFileDialog
{
    // OPENFILENAME is a fixed native struct (larger on x64 because of the
    // trailing pointer/alignment); the C# layout below mirrors the x64 layout.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private const uint OFN_FILEMUSTEXIST = 0x00001000;
    private const uint OFN_PATHMUSTEXIST = 0x00000800;
    private const uint OFN_NOCHANGEDIR = 0x00000008;
    private const uint OFN_EXPLORER = 0x00080000;
    private const uint OFN_LONGNAMES = 0x00200000;
    private const int MaxPath = 32768;

    /// <summary>
    /// Shows the open-file dialog. <paramref name="owner"/> is the editor
    /// window's HWND (the dialog is modal to it); <paramref name="initialDirectory"/>
    /// seeds the dialog location, or null to keep the last one. Returns the
    /// selected file's full path, or null when cancelled.
    /// </summary>
    public static string? PickCrproj(nint owner, string? initialDirectory)
    {
        return Pick(owner, initialDirectory,
            "Crowbar project (*.crproj)\0*.crproj\0All files (*.*)\0*.*\0\0",
            "Open a game project");
    }

    public static string? PickEnvironment(nint owner, string? initialDirectory) =>
        Pick(owner, initialDirectory,
            "HDR environments (*.hdr;*.exr)\0*.hdr;*.exr\0Radiance HDR (*.hdr)\0*.hdr\0OpenEXR (*.exr)\0*.exr\0\0",
            "Import an HDR environment");

    private static string? Pick(nint owner, string? initialDirectory, string filter, string title)
    {

        // The returned path is written back into this buffer; 32768 chars covers
        // the longest legal path plus the "long path" tail.
        var fileBuffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        for (var i = 0; i < MaxPath; i++)
            Marshal.WriteInt16(fileBuffer, i * sizeof(short), 0);

        var filterPtr = Marshal.StringToHGlobalUni(filter);
        var titlePtr = Marshal.StringToHGlobalUni(title);
        nint initialDirPtr = 0;
        if (!string.IsNullOrEmpty(initialDirectory))
            initialDirPtr = Marshal.StringToHGlobalUni(initialDirectory);

        try
        {
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterPtr,
                lpstrFile = fileBuffer,
                nMaxFile = MaxPath,
                lpstrTitle = titlePtr,
                lpstrInitialDir = initialDirPtr,
                Flags = (int)(OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER | OFN_LONGNAMES)
            };

            if (!GetOpenFileNameW(ref ofn))
            {
                var error = CommDlgExtendedError();
                if (error != 0)
                    Console.WriteLine($"[Dialog] GetOpenFileName failed (CDERR {error}).");
                return null;
            }

            return Marshal.PtrToStringUni(fileBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(titlePtr);
            if (initialDirPtr != 0)
                Marshal.FreeHGlobal(initialDirPtr);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();
}
