using System.Runtime.InteropServices;

namespace FrameTrail.App;

internal static class ProjectDeletion
{
    internal static void Recycle(string directory)
    {
        // Revalidate the exact project, never its parent/storage directory.
        directory = ProjectLocations.DeletionDirectory(directory);
        for (string? ancestor = directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能删除经过目录联接或符号链接的项目。");
        var operation = new FileOperation { Operation = 3, From = directory + "\0\0", Flags = 0x40 | 0x10 | 0x4 | 0x4000 };
        int result = SHFileOperation(ref operation);
        if (operation.Aborted) throw new OperationCanceledException();
        if (result != 0 || Directory.Exists(directory)) throw new IOException($"项目未能移到回收站（Windows 错误 {result}），请确认没有其他程序占用素材。");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Owner; public uint Operation;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint Mappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref FileOperation operation);
}
