using System.Runtime.InteropServices;

namespace FrameTrail.App;

/// <summary>Native Common Item Dialog. SetFolder always opens the actual project location, overriding unrelated picker history.</summary>
internal sealed class ProjectFolderPicker : IDisposable
{
    private readonly IFileDialog dialog;
    internal ProjectFolderPicker(string initialDirectory, string title = "打开 FrameTrail 项目", string confirm = "打开项目")
    {
        dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true)!)!;
        try {
            dialog.GetOptions(out uint options); dialog.SetOptions(options | 0x20 | 0x40 | 0x800 | 0x8);
            dialog.SetTitle(title); dialog.SetOkButtonLabel(confirm);
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(Path.GetFullPath(initialDirectory), 0, ref iid, out var folder);
            try { dialog.SetFolder(folder); } finally { Marshal.FinalReleaseComObject(folder); }
        } catch { Dispose(); throw; }
    }
    internal string InitialDirectory { get { dialog.GetFolder(out var folder); try { return FilePath(folder); } finally { Marshal.FinalReleaseComObject(folder); } } }
    internal string? Pick(nint owner)
    {
        int result = dialog.Show(owner);
        if (result == unchecked((int)0x800704C7)) return null;
        Marshal.ThrowExceptionForHR(result);
        dialog.GetResult(out var item);
        try { return FilePath(item); } finally { Marshal.FinalReleaseComObject(item); }
    }
    private static string FilePath(IShellItem item)
    {
        item.GetDisplayName(0x80058000, out var text);
        try { return Marshal.PtrToStringUni(text) ?? throw new IOException("无法读取选择的文件夹。"); }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    public void Dispose() => Marshal.FinalReleaseComObject(dialog);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItem item);

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, nint filters); void SetFileTypeIndex(uint index); void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie); void Unadvise(uint cookie);
        void SetOptions(uint options); void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder); void SetFolder(IShellItem folder); void GetFolder(out IShellItem folder); void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title); void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item); void AddPlace(IShellItem item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension); void Close(int result); void SetClientGuid(ref Guid guid); void ClearClientData(); void SetFilter(nint filter);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result); void GetParent(out IShellItem parent);
        void GetDisplayName(uint name, out nint text); void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem item, uint hint, out int order);
    }
}
