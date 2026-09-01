using System.Runtime.InteropServices;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.App.Presentation.Edge;

/// <summary>
/// Owns the small physical input footprint of the fallback Edge launcher.
/// The full-height WinUI window remains a visual-only transparent surface while
/// this redirection-free HWND receives pointer input over the embedded lens.
/// </summary>
internal sealed class EdgeLauncherInputHost : IDisposable
{
    private const int WindowLongUserData = -21;
    private const uint WindowStylePopup = 0x80000000;
    private const uint ExtendedStyleTopMost = 0x00000008;
    private const uint ExtendedStyleToolWindow = 0x00000080;
    private const uint ExtendedStyleNoRedirectionBitmap = 0x00200000;
    private const uint ExtendedStyleNoActivate = 0x08000000;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const int ShowWindowHide = 0;
    private const int ShowWindowNoActivate = 4;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessageNcCreate = 0x0081;
    private const uint WindowMessageNcDestroy = 0x0082;
    private const uint WindowMessageMouseMove = 0x0200;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint WindowMessageMouseLeave = 0x02A3;
    private const uint TrackMouseLeave = 0x00000002;
    private const int RegionAnd = 1;
    private const int ErrorClassAlreadyExists = 1410;

    internal const double HostWidthDip = 40;
    internal const double HostHeightDip = 56;
    internal const double HorizontalRadiusDip = 30;
    internal const double VerticalRadiusDip = 26;
    internal const double NotificationHorizontalGrowthDip = 24;
    internal const double NotificationVerticalGrowthDip = 8;

    private static readonly IntPtr TopMostWindow = new(-1);
    private static readonly WindowProcedureDelegate WindowProcedure = StaticWindowProcedure;
    private static readonly object RegistrationGate = new();
    private static bool _windowClassRegistered;

    private GCHandle _selfHandle;
    private readonly IntPtr _windowHandle;
    private bool _isShown;
    private bool _pointerInside;
    private bool _windowDestroyed;
    private bool _disposed;
    private DisplayRect _workArea;
    private uint _dpi;
    private EdgeSide _side;
    private int _notificationBucket = -1;

    public EdgeLauncherInputHost()
    {
        EnsureWindowClass();
        _selfHandle = GCHandle.Alloc(this);
        _windowHandle = CreateWindowEx(
            ExtendedStyleTopMost |
            ExtendedStyleToolWindow |
            ExtendedStyleNoActivate |
            ExtendedStyleNoRedirectionBitmap,
            WindowClassName,
            string.Empty,
            WindowStylePopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            GCHandle.ToIntPtr(_selfHandle));
        if (_windowHandle == IntPtr.Zero)
        {
            ReleaseSelfHandle();
            throw new InvalidOperationException(
                $"Could not create the Edge launcher input window ({Marshal.GetLastWin32Error()}).");
        }
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerExited;

    public event EventHandler? Clicked;

    public event EventHandler? SecondaryClicked;

    public bool OwnsWindow(IntPtr windowHandle) =>
        !_disposed && !_windowDestroyed && windowHandle == _windowHandle;

    public void UpdateBounds(
        DisplayRect workArea,
        uint dpi,
        EdgeSide side,
        double notificationExpansion)
    {
        ThrowIfDisposed();
        notificationExpansion = Math.Clamp(notificationExpansion, 0, 1);
        var notificationBucket = (int)Math.Round(notificationExpansion * 30);
        if (_workArea == workArea &&
            _dpi == dpi &&
            _side == side &&
            _notificationBucket == notificationBucket)
        {
            return;
        }

        _workArea = workArea;
        _dpi = dpi;
        _side = side;
        _notificationBucket = notificationBucket;

        var scale = dpi > 0 ? dpi / 96d : 1d;
        var width = Math.Max(
            1,
            (int)Math.Ceiling(
                (HostWidthDip + (NotificationHorizontalGrowthDip * notificationExpansion)) * scale));
        var height = Math.Max(
            1,
            (int)Math.Ceiling(
                (HostHeightDip +
                 (NotificationVerticalGrowthDip * 2 * notificationExpansion)) * scale));
        var x = side == EdgeSide.Right
            ? workArea.X + workArea.Width - width
            : workArea.X;
        var y = workArea.Y + ((workArea.Height - height) / 2);

        _ = SetWindowPos(
            _windowHandle,
            TopMostWindow,
            x,
            y,
            width,
            height,
            SetWindowPositionNoActivate);
        ApplyInputRegion(width, height, scale, side, notificationExpansion);
    }

    public void ShowWithoutActivation()
    {
        ThrowIfDisposed();
        if (_isShown)
        {
            return;
        }

        _isShown = true;
        _ = ShowWindow(_windowHandle, ShowWindowNoActivate);
        PlaceAboveVisualWindow();
    }

    public void PlaceAboveVisualWindow()
    {
        if (_disposed || !_isShown)
        {
            return;
        }

        _ = SetWindowPos(
            _windowHandle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            SetWindowPositionNoActivate |
            SetWindowPositionShowWindow |
            SetWindowPositionNoMove |
            SetWindowPositionNoSize);
    }

    public void Hide()
    {
        if (_disposed || !_isShown)
        {
            return;
        }

        _isShown = false;
        var wasPointerInside = _pointerInside;
        _pointerInside = false;
        _ = ShowWindow(_windowHandle, ShowWindowHide);
        if (wasPointerInside)
        {
            // Hiding a HWND does not guarantee WM_MOUSELEAVE. Emit the same
            // semantic signal so the existing grace timer can reconcile the
            // pointer against the newly interactive WinUI bloom.
            PointerExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ShowWindow(_windowHandle, ShowWindowHide);
        if (_windowHandle != IntPtr.Zero &&
            !_windowDestroyed &&
            IsWindow(_windowHandle) &&
            !DestroyWindow(_windowHandle))
        {
            // Retain the GCHandle if destruction failed. GWLP_USERDATA still
            // points at it and freeing it would turn a later native message
            // into a use-after-free. WM_NCDESTROY will release it if the HWND
            // is torn down later by its owner thread.
            return;
        }

        ReleaseSelfHandle();
    }

    private void ApplyInputRegion(
        int width,
        int height,
        double scale,
        EdgeSide side,
        double notificationExpansion)
    {
        var horizontalRadius = Math.Max(
            1,
            (int)Math.Ceiling(
                (HorizontalRadiusDip +
                 (NotificationHorizontalGrowthDip * notificationExpansion)) * scale));
        var verticalRadius = Math.Max(
            1,
            (int)Math.Ceiling(
                (VerticalRadiusDip +
                 (NotificationVerticalGrowthDip * notificationExpansion)) * scale));
        var centerX = side == EdgeSide.Right ? width : 0;
        var centerY = height / 2;
        var region = CreateEllipticRegion(
            centerX - horizontalRadius,
            centerY - verticalRadius,
            centerX + horizontalRadius,
            centerY + verticalRadius);
        if (region == IntPtr.Zero)
        {
            return;
        }

        var clip = CreateRectRegion(0, 0, width, height);
        if (clip == IntPtr.Zero)
        {
            _ = DeleteObject(region);
            return;
        }

        _ = CombineRegions(region, region, clip, RegionAnd);
        _ = DeleteObject(clip);
        if (SetWindowRegion(_windowHandle, region, redraw: false) == 0)
        {
            _ = DeleteObject(region);
        }
        // After a successful SetWindowRgn, the system owns the region handle.
    }

    private IntPtr HandleWindowMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        switch (message)
        {
            case WindowMessageEraseBackground:
                return new IntPtr(1);
            case WindowMessageMouseMove:
                if (!_pointerInside)
                {
                    _pointerInside = true;
                    var tracking = new TrackMouseEventData
                    {
                        Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                        Flags = TrackMouseLeave,
                        WindowHandle = windowHandle,
                    };
                    _ = TrackMouseEvent(ref tracking);
                    PointerEntered?.Invoke(this, EventArgs.Empty);
                }
                return IntPtr.Zero;
            case WindowMessageMouseLeave:
                if (_pointerInside)
                {
                    _pointerInside = false;
                    PointerExited?.Invoke(this, EventArgs.Empty);
                }
                return IntPtr.Zero;
            case WindowMessageLeftButtonUp:
                Clicked?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            case WindowMessageRightButtonUp:
                SecondaryClicked?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            default:
                return DefWindowProc(windowHandle, message, wParam, lParam);
        }
    }

    private void OnNativeWindowDestroyed()
    {
        _windowDestroyed = true;
        _isShown = false;
        _pointerInside = false;
        ReleaseSelfHandle();
    }

    private void ReleaseSelfHandle()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private static void EnsureWindowClass()
    {
        lock (RegistrationGate)
        {
            if (_windowClassRegistered)
            {
                return;
            }

            var registration = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                Instance = GetModuleHandle(null),
                Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                ClassName = WindowClassName,
            };
            if (RegisterClassEx(ref registration) == 0 &&
                Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"Could not register the Edge launcher input class ({Marshal.GetLastWin32Error()}).");
            }
            _windowClassRegistered = true;
        }
    }

    private static IntPtr StaticWindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (message == WindowMessageNcCreate)
        {
            var creation = Marshal.PtrToStructure<CreateStructure>(lParam);
            _ = SetWindowLongPointer(windowHandle, WindowLongUserData, creation.CreateParameters);
            return new IntPtr(1);
        }

        var userData = GetWindowLongPointer(windowHandle, WindowLongUserData);
        EdgeLauncherInputHost? host = null;
        if (userData != IntPtr.Zero)
        {
            var handle = GCHandle.FromIntPtr(userData);
            host = handle.Target as EdgeLauncherInputHost;
        }

        if (message == WindowMessageNcDestroy)
        {
            var result = host is null
                ? DefWindowProc(windowHandle, message, wParam, lParam)
                : host.HandleWindowMessage(windowHandle, message, wParam, lParam);
            _ = SetWindowLongPointer(windowHandle, WindowLongUserData, IntPtr.Zero);
            host?.OnNativeWindowDestroyed();
            return result;
        }

        return host is null
            ? DefWindowProc(windowHandle, message, wParam, lParam)
            : host.HandleWindowMessage(windowHandle, message, wParam, lParam);
    }

    private static IntPtr GetWindowLongPointer(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));

    private static IntPtr SetWindowLongPointer(IntPtr windowHandle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private const string WindowClassName = "NyKurEdge.LauncherInputHost";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStructure
    {
        public IntPtr CreateParameters;
        public IntPtr Instance;
        public IntPtr Menu;
        public IntPtr Parent;
        public int Height;
        public int Width;
        public int Y;
        public int X;
        public int Style;
        public IntPtr Name;
        public IntPtr Class;
        public uint ExtendedStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr WindowHandle;
        public uint HoverTime;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData eventData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("gdi32.dll", EntryPoint = "CreateEllipticRgn")]
    private static extern IntPtr CreateEllipticRegion(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
    private static extern IntPtr CreateRectRegion(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "CombineRgn")]
    private static extern int CombineRegions(
        IntPtr destination,
        IntPtr sourceOne,
        IntPtr sourceTwo,
        int combineMode);

    [DllImport("user32.dll", EntryPoint = "SetWindowRgn")]
    private static extern int SetWindowRegion(
        IntPtr windowHandle,
        IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
