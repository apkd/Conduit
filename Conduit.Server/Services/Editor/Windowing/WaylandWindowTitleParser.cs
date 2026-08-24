using System.Text.Json;

namespace Conduit;

static class WaylandWindowTitleParser
{
    internal static IReadOnlyList<UnityWindowTitleSignal> ReadHyprlandClientsWindowTitles(
        string? json,
        int processId)
    {
        var titles = new List<UnityWindowTitleSignal>(2);
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return titles;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return titles;

            foreach (var client in document.RootElement.EnumerateArray())
            {
                if (client.ValueKind != JsonValueKind.Object || !JsonElementPidMatches(client, processId))
                    continue;

                var isFocused = TryReadIntProperty(client, "focusHistoryID") == 0;
                AddJsonStringTitle(titles, client, "title", isFocused, "hyprland");
                AddJsonStringTitle(titles, client, "initialTitle", isFocused, "hyprland");
            }
        }
        catch { }

        return titles;
    }

    internal static IReadOnlyList<UnityWindowTitleSignal> ReadSwayTreeWindowTitles(
        string? json,
        int processId)
    {
        var titles = new List<UnityWindowTitleSignal>(2);
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return titles;

        try
        {
            using var document = JsonDocument.Parse(json);
            AddSwayNodeWindowTitles(titles, document.RootElement, processId);
        }
        catch { }

        return titles;
    }

    internal static IReadOnlyList<UnityWindowTitleSignal> ReadNiriWindowsWindowTitles(
        string? json,
        int processId)
    {
        var titles = new List<UnityWindowTitleSignal>(2);
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return titles;

        try
        {
            using var document = JsonDocument.Parse(json);
            AddNiriWindowTitles(titles, document.RootElement, processId);
        }
        catch { }

        return titles;
    }

    static void AddSwayNodeWindowTitles(List<UnityWindowTitleSignal> titles, JsonElement node, int processId)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (JsonElementPidMatches(node, processId))
        {
            var isFocused = TryReadBoolProperty(node, "focused");
            AddJsonStringTitle(titles, node, "name", isFocused, "sway");
            if (node.TryGetProperty("window_properties", out var windowProperties)
                && windowProperties.ValueKind == JsonValueKind.Object)
                AddJsonStringTitle(titles, windowProperties, "title", isFocused, "sway");
        }

        AddSwayChildWindowTitles(titles, node, "nodes", processId);
        AddSwayChildWindowTitles(titles, node, "floating_nodes", processId);
    }

    static void AddSwayChildWindowTitles(
        List<UnityWindowTitleSignal> titles,
        JsonElement node,
        string propertyName,
        int processId)
    {
        if (!node.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        foreach (var child in children.EnumerateArray())
            AddSwayNodeWindowTitles(titles, child, processId);
    }

    static void AddNiriWindowTitles(List<UnityWindowTitleSignal> titles, JsonElement root, int processId)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Ok", out var ok)
            && ok.ValueKind == JsonValueKind.Object
            && ok.TryGetProperty("Windows", out var socketWindows))
        {
            AddNiriWindowTitles(titles, socketWindows, processId);
            return;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return;

        foreach (var window in root.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || !JsonElementPidMatches(window, processId))
                continue;

            AddJsonStringTitle(titles, window, "title", TryReadBoolProperty(window, "is_focused"), "niri");
        }
    }

    static bool JsonElementPidMatches(JsonElement element, int processId)
    {
        if (!element.TryGetProperty("pid", out var pidElement) || pidElement.ValueKind != JsonValueKind.Number)
            return false;

        return pidElement.TryGetInt32(out var nodeProcessId) && nodeProcessId == processId;
    }

    static void AddJsonStringTitle(
        List<UnityWindowTitleSignal> titles,
        JsonElement element,
        string propertyName,
        bool isFocused,
        string source)
    {
        if (TryReadStringProperty(element, propertyName) is { } title)
            UnityWindowTitleProbe.AddTitle(titles, title, isFocused, source);
    }

    static string? TryReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }

    static int? TryReadIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            return null;

        return property.TryGetInt32(out var value) ? value : null;
    }

    static bool TryReadBoolProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.True;
}
