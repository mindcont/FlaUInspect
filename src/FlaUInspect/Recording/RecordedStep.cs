using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FlaUInspect.Recording;

public enum RecordedAction
{
    Invoke,
    SetValue,
    Select,
    WaitUntil
}

public sealed record UiElementDescriptor(
    string? AutomationId,
    string? Name,
    string? ControlType,
    string? ClassName,
    string? ParentPath);

public sealed record RecordedStep(
    int Index,
    RecordedAction Action,
    UiElementDescriptor Target,
    string? Value,
    DateTimeOffset Timestamp,
    string? Screenshot)
{
    /// <summary>面向业务人员的中文步骤标题，可由录制器生成后人工润色。</summary>
    public string? Title { get; init; }

    /// <summary>步骤的中文业务说明。</summary>
    public string? Description { get; init; }

    public UiElementSnapshot? Before { get; init; }
    public UiElementSnapshot? After { get; init; }
    public IReadOnlyList<UiAssertionCandidate> AssertionCandidates { get; init; } = Array.Empty<UiAssertionCandidate>();
}

public sealed record RecordingSession(
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<RecordedStep> Steps)
{
    [JsonIgnore]
    public int StepCount => Steps.Count;
}
