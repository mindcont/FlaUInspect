using System;
using System.Collections.Generic;

namespace FlaUInspect.Recording;

public sealed class UiInteractionNormalizer
{
    public IReadOnlyList<RecordedStep> Normalize(IEnumerable<RecordedStep> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new List<RecordedStep>();
        foreach (var step in source)
        {
            if (step is null)
                continue;

            if (result.Count == 0)
            {
                result.Add(step with { Index = 1 });
                continue;
            }

            var previous = result[^1];
            if (SameTarget(previous, step)
                && previous.Action == RecordedAction.SetValue
                && step.Action == RecordedAction.SetValue)
            {
                // 连续键盘输入只保留最终值。
                result[^1] = step with { Index = previous.Index };
                continue;
            }

            if (SameTarget(previous, step)
                && previous.Action == step.Action
                && (step.Action == RecordedAction.WaitUntil
                    || step.Action == RecordedAction.Invoke)
                && string.Equals(previous.Value, step.Value, StringComparison.Ordinal))
            {
                // 重复等待或重复点击不生成重复步骤。
                continue;
            }

            result.Add(step with { Index = result.Count + 1 });
        }

        for (var i = 0; i < result.Count; i++)
            result[i] = result[i] with { Index = i + 1 };

        return result;
    }

    private static bool SameTarget(RecordedStep left, RecordedStep right)
    {
        return string.Equals(left.Target.AutomationId, right.Target.AutomationId, StringComparison.Ordinal)
            && string.Equals(left.Target.Name, right.Target.Name, StringComparison.Ordinal)
            && string.Equals(left.Target.ControlType, right.Target.ControlType, StringComparison.Ordinal);
    }
}
