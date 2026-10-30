using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Renaissance.Vst3;

/// <summary>
/// IPlugView: embeds the Renaissance app window into the host's editor window (HWND).
/// Coordinates are physical pixels on Windows (VST3 spec), matching the embedded app's DPI mode.
/// </summary>
internal sealed unsafe class Vst3View
{
    private static readonly void** Vtbl = Com.Vtable(
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)&QI,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&AddRefThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&ReleaseThunk,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)&IsPlatformTypeSupported,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)&Attached,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&Removed,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, float, int>)&OnWheel,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ushort, short, short, int>)&OnKey,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ushort, short, short, int>)&OnKey,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int*, int>)&GetSize,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int*, int>)&OnSize,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, int>)&OnFocus,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&SetFrame,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&CanResize,
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int*, int>)&CheckSizeConstraint);

    private static readonly ConcurrentDictionary<IntPtr, Vst3View> Timers = new();

    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    private readonly Vst3Component _owner;
    private GCHandle _self;
    private int _refs;
    private IntPtr _frame;
    private IntPtr _parent;
    private int _width, _height;

    public Vst3View(Vst3Component owner)
    {
        _owner = owner;
        _width = owner.Link.EditorWidth;
        _height = owner.Link.EditorHeight;
        _self = GCHandle.Alloc(this);
        Block = Com.NewBlock(Vtbl, _self);
    }

    public IntPtr Block { get; }

    public uint AddRef() => (uint)Interlocked.Increment(ref _refs);

    public uint Release()
    {
        var n = Interlocked.Decrement(ref _refs);
        if (n == 0)
        {
            StopTimer();
            if (_frame != IntPtr.Zero) Com.Release(_frame);
            _frame = IntPtr.Zero;
            NativeMemory.Free((void*)Block);
            if (_self.IsAllocated) _self.Free();
        }
        return (uint)Math.Max(0, n);
    }

    private void StopTimer()
    {
        if (_parent == IntPtr.Zero) return;
        KillTimer(_parent, (UIntPtr)1);
        Timers.TryRemove(_parent, out _);
    }

    // The host's UI thread is the only one allowed to call IPlugFrame::resizeView. VST3 has no idle callback on
    // Windows, so a timer on the host's own window delivers pending resize requests from the embedded app.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TimerProc(IntPtr hwnd, uint message, UIntPtr id, uint time)
    {
        try
        {
            if (Timers.TryGetValue(hwnd, out var view)) view.ApplyPendingResize();
        }
        catch
        {
        }
    }

    private void ApplyPendingResize()
    {
        var w = _owner.PendingWidth;
        var h = _owner.PendingHeight;
        if (w <= 0 || h <= 0 || _frame == IntPtr.Zero) return;
        _owner.PendingWidth = 0;
        _owner.PendingHeight = 0;
        var rect = stackalloc int[4] { 0, 0, w, h };
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int*, int>)Com.VtableOf(_frame)[3])(_frame, Block, rect);
        _width = w;
        _height = h;
        _owner.Link.ResizeEditorWindow(w, h);
    }

    // ---------------------------------------------------------------- thunks

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QI(IntPtr self, byte* iid, IntPtr* obj)
    {
        var v = Com.Owner<Vst3View>(self);
        if (Tuids.Eq(iid, Tuids.FUnknown) || Tuids.Eq(iid, Tuids.IPlugView))
        {
            v.AddRef();
            *obj = v.Block;
            return R.Ok;
        }
        *obj = IntPtr.Zero;
        return R.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint AddRefThunk(IntPtr self) => Com.Owner<Vst3View>(self).AddRef();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReleaseThunk(IntPtr self) => Com.Owner<Vst3View>(self).Release();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsPlatformTypeSupported(IntPtr self, byte* type) =>
        Marshal.PtrToStringAnsi((IntPtr)type) == "HWND" ? R.Ok : R.False;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Attached(IntPtr self, IntPtr parent, byte* type)
    {
        try
        {
            var v = Com.Owner<Vst3View>(self);
            if (Marshal.PtrToStringAnsi((IntPtr)type) != "HWND") return R.False;
            v._owner.EnsureStarted();
            v._parent = parent;
            v._owner.Link.AttachEditor(parent);
            Timers[parent] = v;
            SetTimer(parent, (UIntPtr)1, 100, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, UIntPtr, uint, void>)&TimerProc);
            return R.Ok;
        }
        catch (Exception ex)
        {
            Log.Write("attached failed: " + ex);
            return R.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Removed(IntPtr self)
    {
        var v = Com.Owner<Vst3View>(self);
        v.StopTimer();
        v._parent = IntPtr.Zero;
        v._owner.Link.DetachEditor();
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnWheel(IntPtr self, float distance) => R.False;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnKey(IntPtr self, ushort key, short keyCode, short modifiers) => R.False;

    // ViewRect: int32 left, top, right, bottom
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetSize(IntPtr self, int* rect)
    {
        var v = Com.Owner<Vst3View>(self);
        rect[0] = 0;
        rect[1] = 0;
        rect[2] = v._width;
        rect[3] = v._height;
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSize(IntPtr self, int* rect)
    {
        var v = Com.Owner<Vst3View>(self);
        v._width = rect[2] - rect[0];
        v._height = rect[3] - rect[1];
        v._owner.Link.ResizeEditorWindow(v._width, v._height);
        return R.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnFocus(IntPtr self, byte state) => R.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetFrame(IntPtr self, IntPtr frame)
    {
        var v = Com.Owner<Vst3View>(self);
        if (frame != IntPtr.Zero) Com.AddRef(frame);
        if (v._frame != IntPtr.Zero) Com.Release(v._frame);
        v._frame = frame;
        return R.Ok;
    }

    // Plugin windows are fixed-size; Renaissance asks the host to resize instead (see ApplyPendingResize).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanResize(IntPtr self) => R.False;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CheckSizeConstraint(IntPtr self, int* rect) => R.Ok;
}
