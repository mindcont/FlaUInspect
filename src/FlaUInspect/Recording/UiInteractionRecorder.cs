using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace XplorePlane.UiTests.Recording;

/// <summary>
/// 第一阶段的半自动录制器：由测试或人工录制工具明确调用 Record* 方法，
/// 保存稳定控件描述、步骤值、时间戳和截图。它不监听全局鼠标/键盘，也不生成测试代码。
/// </summary>
public sealed class UiInteractionRecorder : IDisposable
{
    private readonly AutomationElement _root;
    private readonly string _outputDirectory;
    private readonly List<RecordedStep> _steps = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _saved;

    public UiInteractionRecorder(AutomationElement root, string name, string? outputDirectory = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        Name = string.IsNullOrWhiteSpace(name) ? "recording" : name.Trim();
        _outputDirectory = outputDirectory
            ?? Environment.GetEnvironmentVariable("XPLOREPLANE_UI_RECORDINGS")
            ?? Path.Combine(AppContext.BaseDirectory, "artifacts", "recordings");
    }

    public string Name { get; }

    public IReadOnlyList<RecordedStep> Steps => _steps;

    public void UpdateAfter(RecordedStep step, AutomationElement target)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(target);

        var index = _steps.FindIndex(item => ReferenceEquals(item, step));
        if (index < 0)
            return;

        var after = UiSnapshotCapture.Capture(target);
        var current = _steps[index];
        _steps[index] = current with
        {
            After = after,
            AssertionCandidates = current.Before is not null
                ? UiAssertionCandidateBuilder.Compare(current.Target, current.Before, after)
                : Array.Empty<UiAssertionCandidate>()
        };
    }

    public RecordedStep RecordInvoke(
        AutomationElement target,
        UiElementSnapshot? before = null,
        UiElementSnapshot? after = null)
        => Add(RecordedAction.Invoke, target, null, before, after);

    public RecordedStep RecordSetValue(
        AutomationElement target,
        string value,
        UiElementSnapshot? before = null,
        UiElementSnapshot? after = null)
        => Add(RecordedAction.SetValue, target, value, before, after);

    public RecordedStep RecordSelect(AutomationElement target, string value)
        => Add(RecordedAction.Select, target, value);

    public RecordedStep RecordWaitUntil(AutomationElement target, string expectedValue)
        => Add(RecordedAction.WaitUntil, target, expectedValue);

    public string Save()
    {
        if (_saved)
            throw new InvalidOperationException("此录制会话已经保存。每个会话只能保存一次。");

        Directory.CreateDirectory(_outputDirectory);
        var session = new RecordingSession(Name, _startedAt, DateTimeOffset.Now, _steps.ToArray());
        var fileName = $"{SanitizeFileName(Name)}-{_startedAt:yyyyMMdd-HHmmssfff}.json";
        var path = Path.Combine(_outputDirectory, fileName);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(session, options));
        _saved = true;
        return path;
    }

    public void Dispose()
    {
        // 录制失败时不隐式写入不完整用例，调用方应在 finally 中决定是否保存。
    }

    private RecordedStep Add(
        RecordedAction action,
        AutomationElement target,
        string? value,
        UiElementSnapshot? before = null,
        UiElementSnapshot? after = null)
    {
        var descriptor = UiElementDescriptorFactory.Create(target);
        var screenshot = CaptureScreenshot(target, _steps.Count + 1);
        var step = new RecordedStep(
            _steps.Count + 1,
            action,
            descriptor,
            value,
            DateTimeOffset.Now,
            screenshot)
        {
            Title = ChineseStepText.Title(action, descriptor),
            Description = ChineseStepText.Description(action, descriptor, value),
            Before = before,
            After = after,
            AssertionCandidates = before is not null && after is not null
                ? UiAssertionCandidateBuilder.Compare(descriptor, before, after)
                : Array.Empty<UiAssertionCandidate>()
        };
        _steps.Add(step);
        return step;
    }

    private static class ChineseStepText
    {
        public static string Title(RecordedAction action, UiElementDescriptor target)
            => action switch
            {
                RecordedAction.Invoke => $"点击{DisplayName(target)}",
                RecordedAction.SetValue => $"输入{DisplayName(target)}",
                RecordedAction.Select => $"选择{DisplayName(target)}",
                RecordedAction.WaitUntil => $"等待{DisplayName(target)}",
                _ => "执行界面操作"
            };

        public static string Description(RecordedAction action, UiElementDescriptor target, string? value)
            => action switch
            {
                RecordedAction.SetValue => $"向{DisplayName(target)}输入“{value ?? string.Empty}”",
                RecordedAction.Select => $"在{DisplayName(target)}中选择“{value ?? string.Empty}”",
                RecordedAction.WaitUntil => $"等待{DisplayName(target)}满足“{value ?? string.Empty}”",
                _ => $"对{DisplayName(target)}执行{Title(action, target)}"
            };

        private static string DisplayName(UiElementDescriptor target)
            => !string.IsNullOrWhiteSpace(target.Name) ? target.Name!
                : !string.IsNullOrWhiteSpace(target.AutomationId) ? target.AutomationId!
                : target.ControlType ?? "控件";
    }

    private string? CaptureScreenshot(AutomationElement target, int index)
    {
        try
        {
            var screenshotDirectory = Path.Combine(_outputDirectory, "screenshots");
            Directory.CreateDirectory(screenshotDirectory);
            var fileName = $"{SanitizeFileName(Name)}-step-{index:000}.png";
            var path = Path.Combine(screenshotDirectory, fileName);
            using var capture = Capture.Element(target, new CaptureSettings());
            capture.ToFile(path);
            return Path.GetRelativePath(_outputDirectory, path);
        }
        catch
        {
            // 截图只是辅助证据，不能导致人工操作记录失败。
            return null;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}
