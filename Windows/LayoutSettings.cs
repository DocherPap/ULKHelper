using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;

namespace ULKHelper.Windows;

internal static class LayoutSettings
{
    private static readonly HashSet<uint> TableIds = new();
    public static void RememberTable(string id) => TableIds.Add(ImGui.GetID(id));
    // Restore/capture only this plugin's registered tables, never another plugin's layout.
    public static void Capture(SettingsStore settings)
    {
        var sections = Sections(settings.Get("layout.tables", "")).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Last().Text);
        var io = ImGui.GetIO(); var requestedSave = io.WantSaveIniSettings;
        var ini = ImGui.SaveIniSettingsToMemory(); io.WantSaveIniSettings = requestedSave;
        foreach (var section in Sections(ini))
            if (TableIds.Contains(section.Id)) sections[section.Id] = section.Text;
        settings.Put("layout.tables", string.Join("\n", sections.Values));
    }
    private static IEnumerable<(uint Id, string Text)> Sections(string ini)
    {
        foreach (var section in Regex.Split(ini, @"(?m)(?=^\[)").Where(s => s.StartsWith("[Table]", StringComparison.Ordinal)))
        {
            var match = Regex.Match(section, @"^\[Table\]\[0x([0-9A-Fa-f]+),");
            if (match.Success && uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var id)) yield return (id, section);
        }
    }
    public static void Restore(SettingsStore settings)
    {
        var ini = settings.Get("layout.tables", "");
        if (ini.Length > 0) ImGui.LoadIniSettingsFromMemory(ini);
    }
}
