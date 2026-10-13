using System;
using System.Collections.Generic;
using FlaUI.Core.AutomationElements;

namespace FlaUInspect.Recording;

public sealed record UiElementSnapshot(
    bool Exists,
    string? AutomationId,
    string? Name,
    string? ControlType,
    string? Text,
    bool? IsEnabled,
    bool? IsSelected);

public sealed record UiAssertionCandidate(
    string Kind,
    UiElementDescriptor Target,
    string Property,
    string? ExpectedValue,
    string Reason,
    bool RequiresReview = true);

public static class UiSnapshotCapture
{
    public static UiElementSnapshot Capture(AutomationElement? element)
    {
        if (element is null)
            return new UiElementSnapshot(false, null, null, null, null, null, null);

        return new UiElementSnapshot(
            true,
            Safe(() => element.AutomationId),
            Safe(() => element.Name),
            Safe(() => element.ControlType.ToString()),
            Safe(() => ReadText(element)),
            SafeBool(() => element.Properties.IsEnabled.Value),
            SafeBool(() => element.Patterns.SelectionItem.Pattern?.IsSelected.Value ?? false));
    }

    private static string ReadText(AutomationElement element)
    {
        try
        {
            return element.AsTextBox().Text ?? element.Name;
        }
        catch
        {
            return element.Name;
        }
    }

    private static string? Safe(Func<string> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private static bool? SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return null; }
    }
}

public static class UiAssertionCandidateBuilder
{
    public static IReadOnlyList<UiAssertionCandidate> Compare(
        UiElementDescriptor target,
        UiElementSnapshot before,
        UiElementSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var candidates = new List<UiAssertionCandidate>();
        if (!before.Exists && after.Exists)
        {
            candidates.Add(new UiAssertionCandidate(
                "exists", target, "Exists", "true", "操作后控件出现"));
        }
        else if (before.Exists && !after.Exists)
        {
            candidates.Add(new UiAssertionCandidate(
                "exists", target, "Exists", "false", "操作后控件消失"));
        }

        if (!string.Equals(before.Text, after.Text, StringComparison.Ordinal)
            && after.Text is not null)
        {
            candidates.Add(new UiAssertionCandidate(
                "text", target, "Text", after.Text, "控件文本发生变化"));
        }

        if (before.IsEnabled != after.IsEnabled && after.IsEnabled.HasValue)
        {
            candidates.Add(new UiAssertionCandidate(
                "enabled", target, "IsEnabled", after.IsEnabled.Value.ToString().ToLowerInvariant(), "控件启用状态发生变化"));
        }

        if (before.IsSelected != after.IsSelected && after.IsSelected.HasValue)
        {
            candidates.Add(new UiAssertionCandidate(
                "selected", target, "IsSelected", after.IsSelected.Value.ToString().ToLowerInvariant(), "控件选中状态发生变化"));
        }

        return candidates;
    }
}
