using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;

namespace Nami.Interop;

/// <summary>
/// A fully transparent cursor as a WinUI <see cref="InputCursor"/>, for hiding the pointer over the
/// player via <c>UIElement.ProtectedCursor</c> (the supported WinUI way; Win32 ShowCursor/SetCursor
/// do not reach WinUI's input pipeline). Built with CreateCursor and converted through the
/// IInputCursorStaticsInterop factory interface.
/// </summary>
internal static unsafe class EmptyCursor
{
    // IInputCursorStaticsInterop : IInspectable { HRESULT CreateFromHCursor(HCURSOR, IInputCursor**); }
    private static readonly Guid IID_IInputCursorStaticsInterop = new("ac6f5065-90c4-46ce-beb7-05e138e54117");

    public static InputCursor? Create()
    {
        try
        {
            // 32x32, AND mask all 1 (screen shows through), XOR mask all 0 → nothing drawn.
            const int size = 32;
            byte* and = stackalloc byte[size * size / 8];
            byte* xor = stackalloc byte[size * size / 8];
            new Span<byte>(and, size * size / 8).Fill(0xFF);
            new Span<byte>(xor, size * size / 8).Clear();
            var hcursor = PInvoke.CreateCursor(PInvoke.GetModuleHandle((PCWSTR)null), 0, 0, size, size, and, xor);
            if (hcursor.IsNull) { App.Log("cursor: CreateCursor failed"); return null; }

            HSTRING cls;
            fixed (char* name = "Microsoft.UI.Input.InputCursor")
                PInvoke.WindowsCreateString(name, (uint)"Microsoft.UI.Input.InputCursor".Length, &cls).ThrowOnFailure();
            void* factory;
            try
            {
                fixed (Guid* iid = &IID_IInputCursorStaticsInterop)
                    PInvoke.RoGetActivationFactory(cls, iid, &factory).ThrowOnFailure();
            }
            finally { PInvoke.WindowsDeleteString(cls); }

            try
            {
                // vtable: IUnknown (0-2), IInspectable (3-5), CreateFromHCursor (6)
                var vtbl = *(void***)factory;
                var createFromHCursor = (delegate* unmanaged[Stdcall]<void*, nint, nint*, int>)vtbl[6];
                nint abi;
                int hr = createFromHCursor(factory, (nint)hcursor.Value, &abi);
                if (hr < 0) { App.Log($"cursor: CreateFromHCursor failed 0x{hr:X8}"); return null; }
                return InputCursor.FromAbi(abi);
            }
            finally { Marshal.Release((nint)factory); }
        }
        catch (Exception ex)
        {
            App.Log("cursor: empty cursor unavailable: " + ex.Message);
            return null;
        }
    }
}
