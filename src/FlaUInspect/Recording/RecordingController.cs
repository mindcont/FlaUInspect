using System.IO;
using FlaUI.Core;
using XplorePlane.UiTests.Recording;

namespace FlaUInspect.Recording;

internal sealed class RecordingController : IDisposable
{
    private readonly object _gate = new();
    private MouseRecordingSession? _session;
    private Task? _task;

    public bool IsRecording => _session is not null;

    public event Action<string>? StatusChanged;
    public event Action<string>? Completed;

    public void Start(AutomationBase automation, int processId, string name, string outputDirectory, TimeSpan duration)
    {
        lock (_gate)
        {
            if (_session is not null)
                return;

            var session = new MouseRecordingSession(name, outputDirectory, processId, automation);
            _session = session;
            StatusChanged?.Invoke($"正在录制 XplorePlane (PID={processId})…");
            _task = Task.Run(() =>
            {
                try
                {
                    var path = session.Run(duration);
                    Completed?.Invoke($"录制 JSON 已保存：{path}。请由 XplorePlane.UiTests 解析并执行。");
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"录制失败：{ex.Message}");
                }
                finally
                {
                    lock (_gate)
                    {
                        session.Dispose();
                        if (ReferenceEquals(_session, session))
                            _session = null;
                    }
                }
            });
        }
    }

    public void Stop()
    {
        lock (_gate)
            _session?.Stop();
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
            _session?.Dispose();
    }
}
