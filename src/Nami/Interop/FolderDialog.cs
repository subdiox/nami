using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Nami.Interop;

/// <summary>The native Windows folder dialog (IFileOpenDialog with FOS_PICKFOLDERS), modal to its owner.</summary>
internal static unsafe class FolderDialog
{
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private const int ErrorCancelled = unchecked((int)0x800704C7);   // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    /// <summary>Returns the chosen folder, or null when cancelled.</summary>
    public static string? Pick(nint owner, string title, string? initialFolder = null)
    {
        IFileOpenDialog* dlg = null;
        IShellItem* item = null;
        IShellItem* initial = null;
        try
        {
            fixed (Guid* clsid = &CLSID_FileOpenDialog)
            {
                Guid iid = IFileOpenDialog.IID_Guid;
                PInvoke.CoCreateInstance(clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&dlg).ThrowOnFailure();
            }
            // CsWin32 (no marshaling) throws on failed HRESULTs for these methods.
            FILEOPENDIALOGOPTIONS opts;
            dlg->GetOptions(&opts);
            dlg->SetOptions(opts | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);
            fixed (char* t = title) dlg->SetTitle(t);
            if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
            {
                Guid itemIid = IShellItem.IID_Guid;
                fixed (char* p = initialFolder)
                    if (PInvoke.SHCreateItemFromParsingName(p, null, &itemIid, (void**)&initial).Succeeded) dlg->SetFolder(initial);
            }

            try { dlg->Show((HWND)owner); }
            catch (COMException ex) when (ex.HResult == ErrorCancelled) { return null; }

            dlg->GetResult(&item);
            PWSTR name;
            item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &name);
            try { return name.ToString(); }
            finally { PInvoke.CoTaskMemFree(name); }
        }
        catch (Exception ex)
        {
            App.Log("folder dialog: " + ex.Message);
            return null;
        }
        finally
        {
            if (initial != null) initial->Release();
            if (item != null) item->Release();
            if (dlg != null) dlg->Release();
        }
    }
}
