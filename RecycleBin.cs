using System.IO;
using System.Runtime.InteropServices;

namespace QuickCrop;

internal static class RecycleBin
{
    private const uint Silent = 0x0004, NoConfirmation = 0x0010, NoErrorUi = 0x0400;
    private const uint RecycleOnDelete = 0x00080000, AddUndoRecord = 0x20000000;
    private const uint DeleteRecycleIfPossible = 0x0080;
    private const int AccessDenied = unchecked((int)0x80070005);

    internal static bool IsRecycling(uint transferFlags) => (transferFlags & DeleteRecycleIfPossible) != 0;

    public static void Move(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("图片不存在，未执行删除。", path);
        IFileOperation? operation = null;
        IShellItem? item = null;
        var guard = new RecycleGuard();
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!)!;
            operation.SetOperationFlags(Silent | NoConfirmation | NoErrorUi | RecycleOnDelete | AddUndoRecord);
            Guid itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            operation.DeleteItem(item, guard);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (guard.RefusedPermanentDelete) throw new IOException("该图片无法移入回收站，已保留原文件，不会永久删除。");
            Marshal.ThrowExceptionForHR(guard.DeleteResult);
            if (aborted || !guard.Completed || File.Exists(path)) throw new IOException("移入回收站未完成，未执行永久删除。");
        }
        catch (COMException ex)
        {
            throw new IOException(guard.RefusedPermanentDelete ? "该图片无法移入回收站，已保留原文件，不会永久删除。" : $"Windows 无法将图片移入回收站：{ex.Message}", ex);
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            GC.KeepAlive(guard);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid interfaceId, out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Preserve the complete native vtable even though only deletion is exposed by this helper.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog([MarshalAs(UnmanagedType.IUnknown)] object dialog);
        void SetProperties([MarshalAs(UnmanagedType.IUnknown)] object properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleGuard : IFileOperationProgressSink
    {
        public bool RefusedPermanentDelete { get; private set; }
        public bool Completed { get; private set; }
        public int DeleteResult { get; private set; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            // Without this callback guard, the Shell can silently fall back to permanent deletion.
            if (IsRecycling(flags)) return 0;
            RefusedPermanentDelete = true; return AccessDenied;
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
        { DeleteResult = result; Completed = result >= 0; return 0; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string? template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
