using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;

namespace ULKHelper.Windows;

internal static class LayoutSettings
{
    private static readonly HashSet<uint> TableIds = new();
    private static Dictionary<uint, string> pendingTables = new();

    public static void RememberTable(string id)
    {
        var tableId = ImGui.GetID(id);
        TableIds.Add(tableId);
        if (pendingTables.Remove(tableId, out var section)) RestoreTable(tableId, section);
    }
    // Restore/capture only this plugin's registered tables, never another plugin's layout.
    public static void Capture(SettingsStore settings)
    {
        var sections = Sections(settings.Get("layout.tables", "")).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Last().Text);
        var io = ImGui.GetIO();
        var context = ImGui.GetCurrentContext();
        var requestedSave = io.WantSaveIniSettings;
        var dirtyTimer = context.SettingsDirtyTimer;
        var ini = ImGui.SaveIniSettingsToMemory();
        io.WantSaveIniSettings = requestedSave;
        context.SettingsDirtyTimer = dirtyTimer;
        foreach (var section in Sections(ini))
            if (TableIds.Contains(section.Id)) sections[section.Id] = section.Text;
        settings.Put("layout.tables", string.Join("\n", sections.Values));
    }
    private static IEnumerable<(uint Id, string Text)> Sections(string ini)
    {
        foreach (var section in Regex.Split(ini, @"(?m)(?=^\[)").Where(s => s.StartsWith("[Table]", StringComparison.Ordinal)))
        {
            var match = Regex.Match(section, @"^\[Table\]\[0x([0-9A-Fa-f]+),");
            if (match.Success && uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var id))
                yield return (id, section.TrimEnd());
        }
    }
    public static void Restore(SettingsStore settings)
    {
        pendingTables = Sections(settings.Get("layout.tables", "")).GroupBy(s => s.Id)
            .ToDictionary(group => group.Key, group => group.Last().Text);
    }

    private static unsafe void RestoreTable(uint id, string section)
    {
        var context = ImGui.GetCurrentContext();
        var handlers = context.SettingsHandlers;
        for (var i = 0; i < handlers.Size; i++)
        {
            var handler = handlers.Data + i;
            if (Marshal.PtrToStringUTF8((nint)handler->TypeName) != "Table") continue;
            if (handler->ReadOpenFn == null || handler->ReadLineFn == null) return;
            // Load only the registered table. The global ini loader also reapplies every window's settings.
            var lines = section.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var name = lines[0].AsSpan("[Table][".Length).TrimEnd(']');
            var header = Encoding.UTF8.GetBytes(name.ToString() + "\0");
            var readOpen = (delegate* unmanaged[Cdecl]<ImGuiContext*, ImGuiSettingsHandler*, byte*, void*>)handler->ReadOpenFn;
            var readLine = (delegate* unmanaged[Cdecl]<ImGuiContext*, ImGuiSettingsHandler*, void*, byte*, void>)handler->ReadLineFn;
            void* entry;
            fixed (byte* headerPtr = header) entry = readOpen(context.Handle, handler, headerPtr);
            if (entry == null) return;
            foreach (var line in lines.Skip(1))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\0");
                fixed (byte* linePtr = bytes) readLine(context.Handle, handler, entry, linePtr);
            }
            var tables = context.Tables.Buf;
            for (var tableIndex = 0; tableIndex < tables.Size; tableIndex++)
            {
                var table = tables.Data + tableIndex;
                if (table->ID != id) continue;
                table->SettingsOffset = -1;
                table->IsSettingsRequestLoad = 1;
                break;
            }
            return;
        }
    }
}
