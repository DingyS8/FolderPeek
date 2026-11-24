using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderPeek.Native;

// Declarações COM mínimas do shell. A ORDEM dos métodos importa (é a vtable);
// métodos que nunca chamamos aparecem só para ocupar o slot.

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }
    [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In] object index);
    [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
    void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
    void RegisterPending(int lThreadId, [In] ref object pvarloc, [In] ref object pvarlocRoot, int swClass, out int plCookie);
    void Revoke(int lCookie);
    void OnNavigate(int lCookie, [In] ref object pvarLoc);
    void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW([In] ref object pvarLoc, [In] ref object pvarLocRoot, int swClass, out int pHWND, int swfwOptions);
    void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
    void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool fAttach);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleServiceProvider
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    // IOleWindow
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    // IShellBrowser
    [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
    [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
    [PreserveSig] int EnableModelessSB(int fEnable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
    [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
    [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
    [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
    [PreserveSig] int OnViewWindowActive(IntPtr pshv);
    [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
}

[ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    [PreserveSig] int GetSpacing(ref POINT ppt);
    [PreserveSig] int GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
}

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    // Só os dois primeiros slots são usados; o resto da vtable não precisa ser declarado.
    [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    [PreserveSig] int GetIDList(out IntPtr ppidl);
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass { }

internal static class ShellConst
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IID_IShellBrowser = typeof(IShellBrowser).GUID;

    public const int CSIDL_DESKTOP = 0;
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const uint SVGIO_ALLVIEW = 2;
}
