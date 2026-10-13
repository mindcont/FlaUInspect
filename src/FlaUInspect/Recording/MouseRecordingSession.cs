using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using XplorePlane.UiTests.Recording;

internal sealed class MouseRecordingSession : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkBack = 0x08;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const uint PmRemove = 0x0001;

    // 默认使用 UIA3；UIA2 由录制器实时诊断保留用于对比。
    private readonly AutomationBase _automation;
    private readonly bool _ownsAutomation;
    private readonly AutomationElement _desktop;
    private readonly UiInteractionRecorder _recorder;
    private readonly int? _processId;
    private readonly LowLevelMouseProc _callback;
    private readonly LowLevelKeyboardProc _keyboardCallback;
    private readonly StringBuilder _textBuffer = new();
    private readonly ConcurrentQueue<AutomationElement> _manualTargets = new();
    private IntPtr _hook;
    private IntPtr _keyboardHook;
    private AutomationElement? _activeTextElement;
    private UiElementSnapshot? _activeTextBefore;
    private RecordedStep? _pendingStep;
    private UiElementSnapshot? _pendingBefore;
    private Point _pendingPoint;
    private volatile bool _stopRequested;
    private bool _disposed;

    public MouseRecordingSession(string name, string outputDirectory, int? processId, AutomationBase? automation = null)
    {
        if (automation is not null)
        {
            _automation = automation;
            _ownsAutomation = false;
        }
        else
        {
            _automation = new UIA3Automation();
            _ownsAutomation = true;
        }

        _desktop = _automation.GetDesktop();
        _recorder = new UiInteractionRecorder(_desktop, name, outputDirectory);
        _processId = processId;
        _callback = OnMouse;
        _keyboardCallback = OnKeyboard;
    }

    public string Run(TimeSpan duration)
    {
        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"无法安装鼠标钩子，Win32 错误码：{Marshal.GetLastWin32Error()}");

        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            throw new InvalidOperationException($"无法安装键盘钩子，Win32 错误码：{Marshal.GetLastWin32Error()}");
        }

        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _stopRequested = true;
        };
        Console.CancelKeyPress += cancelHandler;

        Console.WriteLine($"开始录制，持续 {duration.TotalSeconds:0} 秒。请操作目标应用；按 Ctrl+C 停止并保存。\n");
        var deadline = DateTime.UtcNow + duration;
        try
        {
            while (!_stopRequested && DateTime.UtcNow < deadline)
            {
                while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }

                PollPendingObservation();
                DrainManualTargets();
                Thread.Sleep(10);
            }

            FlushText();
            return _recorder.Save();
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            UnhookWindowsHookEx(_hook);
            UnhookWindowsHookEx(_keyboardHook);
            _hook = IntPtr.Zero;
            _keyboardHook = IntPtr.Zero;
        }
    }

    public void Stop() => _stopRequested = true;

    public void RequestLockedInvoke(AutomationElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _manualTargets.Enqueue(target);
    }

    private void DrainManualTargets()
    {
        while (_manualTargets.TryDequeue(out var target))
        {
            try
            {
                _recorder.RecordInvoke(target);
                Console.WriteLine($"锁定控件：{target.AutomationId} | {target.Name}");
            }
            catch (Exception ex) when (ex is FlaUI.Core.Exceptions.ElementNotAvailableException
                || ex is FlaUI.Core.Exceptions.PropertyNotSupportedException
                || ex is COMException)
            {
                Console.Error.WriteLine($"锁定控件失败：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        if (_keyboardHook != IntPtr.Zero)
            UnhookWindowsHookEx(_keyboardHook);
        _recorder.Dispose();
        if (_ownsAutomation)
            _automation.Dispose();
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == (IntPtr)WmLButtonUp
            || message == (IntPtr)WmRButtonUp
            || message == (IntPtr)WmMButtonUp
            || message == (IntPtr)WmMouseWheel))
        {
            var info = Marshal.PtrToStructure<MsllHookStruct>(data);
            try
            {
                var point = new Point(info.Point.X, info.Point.Y);
                var element = FindDeepestElementAtPoint(_automation.FromPoint(point), point);
                // Telerik Ribbon 在纯悬停时常返回外层 Window，但点击后会把真实按钮暴露为焦点元素。
                // 录制点击优先采用 UIA3 FocusedElement，坐标命中作为备用。
                var focused = SafeFocusedElement();
                if (focused is not null && BelongsToTargetProcess(focused) && IsStableInteractiveElement(focused))
                    element = focused;
                if (element is null || !BelongsToTargetProcess(element))
                    return CallNextHookEx(_hook, code, message, data);

                FlushText();
                if (TryGetSelectionTarget(element, out var selectionTarget, out var selectionValue))
                {
                    _recorder.RecordSelect(selectionTarget, selectionValue);
                    Console.WriteLine($"记录选择：{selectionTarget.AutomationId} <- {selectionValue}");
                }
                else
                {
                    var observed = UiSnapshotCapture.Capture(element);
                    _pendingBefore = observed;
                    _pendingStep = _recorder.RecordInvoke(element, observed, null);
                    _pendingPoint = new Point(info.Point.X, info.Point.Y);
                    Console.WriteLine($"记录点击：{element.AutomationId} | {element.Name} | ({info.Point.X},{info.Point.Y})");
                }

                _activeTextElement = IsTextElement(element) ? element : null;
                if (_activeTextElement is not null)
                {
                    _textBuffer.Clear();
                    _activeTextBefore = UiSnapshotCapture.Capture(_activeTextElement);
                    try { _textBuffer.Append(_activeTextElement.AsTextBox().Text ?? string.Empty); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                // 低级钩子回调中的异常不能冒泡到 Win32 消息分发；
                // UIA 元素可能在点击同时被销毁，单次事件失败不应终止录制。
                Console.Error.WriteLine($"跳过一次无法解析的 UIA 事件：{ex.Message}");
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && (message == (IntPtr)WmKeyDown || message == (IntPtr)WmSysKeyDown))
            {
                var virtualKey = Marshal.ReadInt32(data);
                if (virtualKey == VkReturn || virtualKey == VkEscape)
                {
                    FlushText();
                }
                else if (_activeTextElement is not null)
                {
                    if (virtualKey == VkBack)
                    {
                        if (_textBuffer.Length > 0)
                            _textBuffer.Remove(_textBuffer.Length - 1, 1);
                    }
                    else
                    {
                        var text = TranslateKey((uint)virtualKey);
                        if (!string.IsNullOrEmpty(text))
                            _textBuffer.Append(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 键盘钩子同样不能把异常返回给 user32。
            Console.Error.WriteLine($"跳过一次无法解析的键盘事件：{ex.Message}");
        }

        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void FlushText()
    {
        if (_activeTextElement is null)
            return;

        if (_textBuffer.Length > 0)
        {
            var after = UiSnapshotCapture.Capture(_activeTextElement);
            _recorder.RecordSetValue(
                _activeTextElement,
                _textBuffer.ToString(),
                _activeTextBefore,
                after);
            Console.WriteLine($"记录输入：{_activeTextElement.AutomationId} | {_textBuffer}");
        }

        _activeTextElement = null;
        _activeTextBefore = null;
        _textBuffer.Clear();
    }

    private void PollPendingObservation()
    {
        if (_pendingStep is null || _pendingBefore is null)
            return;

        try
        {
            // 点击可能打开位于另一处的弹窗，原点击坐标此时仍落在旧按钮上。
            // 优先使用点击后的 UIA 焦点；只有焦点不可用时才回退到坐标命中。
            var target = SafeFocusedElement();
            if (target is null || !BelongsToTargetProcess(target))
                target = _automation.FromPoint(_pendingPoint);
            if (target is null)
                return;

            var after = UiSnapshotCapture.Capture(target);
            if (after != _pendingBefore)
            {
                _recorder.UpdateAfter(_pendingStep, target);
                _pendingStep = null;
                _pendingBefore = null;
            }
        }
        catch
        {
            // 控件被销毁或窗口切换时，保留已有步骤，不中断录制。
        }
    }


    private static bool IsTextElement(AutomationElement element)
    {
        try
        {
            var type = element.ControlType.ToString();
            return type is "Edit" or "Document" or "ComboBox";
        }
        catch { return false; }
    }

    private static AutomationElement? FindDeepestElementAtPoint(AutomationElement? root, Point point)
    {
        if (root is null)
            return null;

        AutomationElement? best = root;
        try
        {
            foreach (var child in root.FindAllDescendants())
            {
                try
                {
                    var bounds = child.BoundingRectangle;
                    if (bounds.Contains(point) && IsMoreSpecific(child, best))
                        best = child;
                }
                catch
                {
                    // A virtualized or destroyed child is ignored for this click.
                }
            }
        }
        catch
        {
            // Keep the element returned by FromPoint when descendants are unavailable.
        }

        return best;
    }

    private static bool IsMoreSpecific(AutomationElement candidate, AutomationElement current)
    {
        try
        {
            var candidateBounds = candidate.BoundingRectangle;
            var currentBounds = current.BoundingRectangle;
            var candidateArea = Math.Max(1, candidateBounds.Width) * Math.Max(1, candidateBounds.Height);
            var currentArea = Math.Max(1, currentBounds.Width) * Math.Max(1, currentBounds.Height);
            return candidateArea < currentArea;
        }
        catch { return false; }
    }

    private static bool TryGetSelectionTarget(
        AutomationElement element,
        out AutomationElement target,
        out string value)
    {
        target = element;
        value = string.Empty;
        try
        {
            var type = element.ControlType.ToString();
            if (type is not ("ListItem" or "ComboBoxItem" or "DataItem"))
                return false;

            value = element.Name;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            target = element.Parent ?? element;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TranslateKey(uint virtualKey)
    {
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
            return string.Empty;

        var scanCode = MapVirtualKey(virtualKey, 0);
        var buffer = new StringBuilder(4);
        var result = ToUnicode(virtualKey, scanCode, keyboardState, buffer, buffer.Capacity, 0);
        return result > 0 ? buffer.ToString() : string.Empty;
    }

    private bool BelongsToTargetProcess(AutomationElement element)
    {
        if (!_processId.HasValue)
            return true;

        try
        {
            return element.Properties.ProcessId.Value == _processId.Value;
        }
        catch
        {
            return false;
        }
    }

    private AutomationElement? SafeFocusedElement()
    {
        try { return _automation.FocusedElement(); }
        catch (Exception ex) when (ex is TimeoutException || ex is COMException || ex is InvalidOperationException)
        { return null; }
    }

    private static bool IsStableInteractiveElement(AutomationElement element)
    {
        try
        {
            return element.ControlType != ControlType.Window
                && (!string.IsNullOrWhiteSpace(element.AutomationId)
                    || !string.IsNullOrWhiteSpace(element.Name));
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointStruct Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr HWnd;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public PointStruct Point;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);


    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] keyboardState);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint virtualKey, uint scanCode, byte[] keyboardState,
        [Out] StringBuilder receivingBuffer, int bufferSize, uint flags);
}
