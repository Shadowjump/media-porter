using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MediaPorter
{
    /// <summary>The modern Explorer-style folder chooser - the same dialog "Save as"
    /// opens, with the address bar, the sidebar and a Select Folder button.
    ///
    /// .NET Framework's FolderBrowserDialog can only show the old "Browse For Folder"
    /// tree (the Explorer-style upgrade landed in .NET Core's WinForms, not 4.x), so
    /// this calls the shell's IFileDialog directly with the pick-folders flag. If any
    /// of that fails - an ancient Windows, a locked-down shell - it falls back to the
    /// old dialog rather than leaving you with nothing.</summary>
    public static class FolderPicker
    {
        public static string Pick(IntPtr owner, string startFolder, string title)
        {
            try
            {
                return PickModern(owner, startFolder, title);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(AppPaths.LogFile,
                        "[" + DateTime.Now + "] modern folder dialog unavailable: " +
                        ex.Message + Environment.NewLine);
                }
                catch { }
                return PickLegacy(startFolder, title);
            }
        }

        // -----------------------------------------------------------------
        //  IFileDialog with FOS_PICKFOLDERS
        // -----------------------------------------------------------------
        const uint FOS_PICKFOLDERS = 0x00000020;
        const uint FOS_FORCEFILESYSTEM = 0x00000040;
        const uint FOS_PATHMUSTEXIST = 0x00000800;
        const uint FOS_NOCHANGEDIR = 0x00000008;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        static string PickModern(IntPtr owner, string startFolder, string title)
        {
            var dialog = (IFileDialog)new FileOpenDialogRcw();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM |
                                  FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);

                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                dialog.SetOkButtonLabel("Use this folder");

                if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
                {
                    try
                    {
                        object item;
                        Guid shellItemGuid = typeof(IShellItem).GUID;
                        SHCreateItemFromParsingName(startFolder, IntPtr.Zero, ref shellItemGuid, out item);
                        dialog.SetFolder((IShellItem)item);
                    }
                    catch { }   // just means it opens somewhere else
                }

                int hr = dialog.Show(owner);
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                IShellItem result;
                dialog.GetResult(out result);

                IntPtr pathPtr;
                result.GetDisplayName(SIGDN_FILESYSPATH, out pathPtr);
                try
                {
                    string path = Marshal.PtrToStringUni(pathPtr);
                    Marshal.ReleaseComObject(result);
                    return string.IsNullOrEmpty(path) ? null : path;
                }
                finally { Marshal.FreeCoTaskMem(pathPtr); }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        static string PickLegacy(string startFolder, string title)
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = title;
                    dlg.ShowNewFolderButton = true;
                    if (Directory.Exists(startFolder)) dlg.SelectedPath = startFolder;
                    return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
                        ? dlg.SelectedPath : null;
                }
            }
            catch { return null; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object shellItem);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        class FileOpenDialogRcw { }

        // Method order below IS the COM vtable - do not reorder or remove entries.
        // The ones this code never calls are declared only to hold their slot.
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            // IModalWindow
            [PreserveSig]
            int Show(IntPtr parent);

            // IFileDialog
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdnName, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
