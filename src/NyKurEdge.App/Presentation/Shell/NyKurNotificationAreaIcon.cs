using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace NyKurEdge.App.Presentation.Shell;

/// <summary>
/// Owns NyKur Edge's notification-area icon and its small native context menu.
/// The ambient Edge remains a separate no-activation surface; this host exists
/// only so the app always has a conventional, discoverable Open/Exit affordance.
/// </summary>
internal sealed class NyKurNotificationAreaIcon : IDisposable
{
    private const uint WindowMessageApp = 0x8000;
    private const uint WindowMessageNcCreate = 0x0081;
    private const uint WindowMessageNcDestroy = 0x0082;
    private const uint WindowMessageNull = 0x0000;
    private const uint WindowMessageContextMenu = 0x007B;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageLeftButtonDoubleClick = 0x0203;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint NotifyIconSelect = 0x0400;
    private const uint NotifyIconKeySelect = 0x0401;
    private const uint CallbackMessage = WindowMessageApp + 0x4E;
    private const uint NotifyIconAdd = 0x00000000;
    private const uint NotifyIconDelete = 0x00000002;
    private const uint NotifyIconSetVersion = 0x00000004;
    private const uint NotifyIconFlagMessage = 0x00000001;
    private const uint NotifyIconFlagIcon = 0x00000002;
    private const uint NotifyIconFlagTip = 0x00000004;
    private const uint NotifyIconFlagShowTip = 0x00000080;
    private const uint NotifyIconVersionFour = 4;
    private const uint MenuString = 0x00000000;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackMenuRightButton = 0x0002;
    private const uint TrackMenuReturnCommand = 0x0100;
    private const uint ExtendedStyleToolWindow = 0x00000080;
    private const uint WindowStylePopup = 0x80000000;
    private const uint ImageIcon = 1;
    private const uint LoadImageDefaultSize = 0x00000040;
    private const uint LoadImageFromFile = 0x00000010;
    private const int WindowLongUserData = -21;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint IconIdentifier = 1;
    private const uint OpenSettingsCommand = 1001;
    private const uint ExitCommand = 1002;

    private static readonly WindowProcedureDelegate WindowProcedure = StaticWindowProcedure;
    private static readonly object RegistrationGate = new();
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private static bool _windowClassRegistered;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private GCHandle _selfHandle;
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private bool _iconAdded;
    private bool _windowDestroyed;
    private bool _disposed;

    public NyKurNotificationAreaIcon(Action openSettings, Action exitApplication)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ??
                           throw new InvalidOperationException("A UI dispatcher is required for the notification-area icon.");

        EnsureWindowClass();
        _iconHandle = LoadApplicationIcon(out _ownsIconHandle);
        _selfHandle = GCHandle.Alloc(this);
        _windowHandle = CreateWindowEx(
            ExtendedStyleToolWindow,
            WindowClassName,
            "NyKur Edge notification area host",
            WindowStylePopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            GCHandle.ToIntPtr(_selfHandle));
        if (_windowHandle == IntPtr.Zero)
        {
            ReleaseResourcesAfterFailedCreation();
            throw new InvalidOperationException(
                $"Could not create the NyKur Edge notification-area host ({Marshal.GetLastWin32Error()}).");
        }

        if (!AddIcon())
        {
            Dispose();
            throw new InvalidOperationException(
                $"Could not add NyKur Edge to the notification area ({Marshal.GetLastWin32Error()}).");
        }

        Debug.WriteLine(
            $"NyKur Edge notification-area icon is ready (HWND 0x{_windowHandle.ToInt64():X}).");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();
        ReleaseIconHandle();

        if (_windowHandle != IntPtr.Zero &&
            !_windowDestroyed &&
            IsWindow(_windowHandle) &&
            !DestroyWindow(_windowHandle))
        {
            // GWLP_USERDATA still references the GCHandle. Retain it until a
            // later WM_NCDESTROY rather than risking native use-after-free.
            return;
        }

        ReleaseSelfHandle();
        GC.SuppressFinalize(this);
    }

    private bool AddIcon()
    {
        if (_disposed || _windowHandle == IntPtr.Zero || _iconHandle == IntPtr.Zero)
        {
            return false;
        }

        var data = CreateIconData(
            NotifyIconFlagMessage |
            NotifyIconFlagIcon |
            NotifyIconFlagTip |
            NotifyIconFlagShowTip);
        if (!ShellNotifyIcon(NotifyIconAdd, ref data))
        {
            return false;
        }

        _iconAdded = true;
        var versionData = CreateIconData(0);
        versionData.TimeoutOrVersion = NotifyIconVersionFour;
        _ = ShellNotifyIcon(NotifyIconSetVersion, ref versionData);
        return true;
    }

    private void RestoreAfterExplorerRestart()
    {
        if (_disposed)
        {
            return;
        }

        _iconAdded = false;
        if (!AddIcon())
        {
            Debug.WriteLine(
                $"NyKur Edge could not restore its notification-area icon ({Marshal.GetLastWin32Error()}).");
        }
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateIconData(0);
        _ = ShellNotifyIcon(NotifyIconDelete, ref data);
        _iconAdded = false;
    }

    private NotifyIconData CreateIconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Identifier = IconIdentifier,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = "NyKur Edge",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void HandleCallback(IntPtr lParam)
    {
        var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
        switch (notification)
        {
            case WindowMessageLeftButtonUp:
            case WindowMessageLeftButtonDoubleClick:
            case NotifyIconSelect:
            case NotifyIconKeySelect:
                Enqueue(_openSettings);
                break;
            case WindowMessageContextMenu:
            case WindowMessageRightButtonUp:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        if (_disposed || !GetCursorPosition(out var cursor))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MenuString, OpenSettingsCommand, "Open NyKur Edge settings");
            _ = AppendMenu(menu, MenuSeparator, 0, null);
            _ = AppendMenu(menu, MenuString, ExitCommand, "Exit NyKur Edge");

            _ = SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenu(
                menu,
                TrackMenuRightButton | TrackMenuReturnCommand,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                IntPtr.Zero);
            _ = PostMessage(_windowHandle, WindowMessageNull, IntPtr.Zero, IntPtr.Zero);

            if (command == OpenSettingsCommand)
            {
                Enqueue(_openSettings);
            }
            else if (command == ExitCommand)
            {
                Enqueue(_exitApplication);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void Enqueue(Action action)
    {
        if (_disposed)
        {
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

    private IntPtr HandleWindowMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (TaskbarCreatedMessage != 0 && message == TaskbarCreatedMessage)
        {
            RestoreAfterExplorerRestart();
            return IntPtr.Zero;
        }

        if (message == CallbackMessage)
        {
            HandleCallback(lParam);
            return IntPtr.Zero;
        }

        return DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void OnNativeWindowDestroyed()
    {
        _windowDestroyed = true;
        _windowHandle = IntPtr.Zero;
        ReleaseSelfHandle();
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
                ClassName = WindowClassName,
            };
            if (RegisterClassEx(ref registration) == 0 &&
                Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"Could not register the NyKur Edge notification-area class ({Marshal.GetLastWin32Error()}).");
            }

            _windowClassRegistered = true;
        }
    }

    private static IntPtr LoadApplicationIcon(out bool ownsHandle)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var icon = LoadImage(
            IntPtr.Zero,
            iconPath,
            ImageIcon,
            0,
            0,
            LoadImageFromFile | LoadImageDefaultSize);
        if (icon != IntPtr.Zero)
        {
            ownsHandle = true;
            return icon;
        }

        ownsHandle = false;
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private void ReleaseResourcesAfterFailedCreation()
    {
        ReleaseIconHandle();
        ReleaseSelfHandle();
    }

    private void ReleaseIconHandle()
    {
        if (_ownsIconHandle && _iconHandle != IntPtr.Zero)
        {
            _ = DestroyIcon(_iconHandle);
        }

        _iconHandle = IntPtr.Zero;
        _ownsIconHandle = false;
    }

    private void ReleaseSelfHandle()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
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
        NyKurNotificationAreaIcon? host = null;
        if (userData != IntPtr.Zero)
        {
            host = GCHandle.FromIntPtr(userData).Target as NyKurNotificationAreaIcon;
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

    private const string WindowClassName = "NyKurEdge.NotificationAreaHost";

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
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Identifier;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosition(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        IntPtr menu,
        uint flags,
        uint identifier,
        string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr windowHandle,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
