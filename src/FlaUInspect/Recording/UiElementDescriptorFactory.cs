using System;
using System.Collections.Generic;
using FlaUI.Core.AutomationElements;

namespace FlaUInspect.Recording;

internal static class UiElementDescriptorFactory
{
    public static UiElementDescriptor Create(AutomationElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return new UiElementDescriptor(
            Read(() => element.AutomationId),
            Read(() => element.Name),
            Read(() => element.ControlType.ToString()),
            Read(() => element.ClassName),
            BuildParentPath(element));
    }

    private static string? BuildParentPath(AutomationElement element)
    {
        var names = new List<string>();
        var current = element.Parent;
        var depth = 0;

        while (current is not null && depth++ < 6)
        {
            var id = Read(() => current.AutomationId);
            var name = Read(() => current.Name);
            if (!string.IsNullOrWhiteSpace(id))
                names.Add($"id:{id}");
            else if (!string.IsNullOrWhiteSpace(name))
                names.Add($"name:{name}");

            current = current.Parent;
        }

        names.Reverse();
        return names.Count == 0 ? null : string.Join("/", names);
    }

    private static string? Read(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (
            ex is FlaUI.Core.Exceptions.ElementNotAvailableException
            || ex is FlaUI.Core.Exceptions.PropertyNotSupportedException
            || ex is System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
