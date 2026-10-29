using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// Themes are just a handful of colors. Built-ins are below; users can add their own by dropping
/// a JSON file into %AppData%\Ember\themes (see the generated example file).
/// </summary>
public sealed class ThemeService
{
    public static readonly ThemeDefinition[] BuiltIn =
    [
        new() { Name = "Ember" },
        new()
        {
            Name = "Midnight",
            Background = "#0B1020", Surface = "#121A2E", SurfaceAlt = "#18223A", Stroke = "#223049",
            Text = "#E8EEFC", TextMuted = "#7C8AA8", Accent = "#4CC9F0", OnAccent = "#06121A",
        },
        new()
        {
            Name = "Aurora",
            Background = "#0E0B16", Surface = "#161224", SurfaceAlt = "#1E1830", Stroke = "#2A2240",
            Text = "#EEEAF8", TextMuted = "#8C84A3", Accent = "#A78BFA", OnAccent = "#140D24",
        },
        new()
        {
            Name = "Graphite",
            Background = "#0E0E0E", Surface = "#171717", SurfaceAlt = "#202020", Stroke = "#2A2A2A",
            Text = "#FAFAFA", TextMuted = "#8C8C8C", Accent = "#EDEDED", OnAccent = "#111111",
        },
        new()
        {
            Name = "Daylight", Dark = false,
            Background = "#F4F4F6", Surface = "#FFFFFF", SurfaceAlt = "#EEEEF2", Stroke = "#E2E2E8",
            Text = "#15151A", TextMuted = "#6B6B78", Accent = "#F2561D", OnAccent = "#FFFFFF",
            Good = "#12A66A", Warn = "#D98A00", Hot = "#E03131",
        },
    ];

    public List<ThemeDefinition> All { get; } = new();

    public ThemeService() => Reload();

    public void Reload()
    {
        All.Clear();
        All.AddRange(BuiltIn);
        LoadCustom();
    }

    public void ApplyByName(string? name)
    {
        var theme = All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? BuiltIn[0];
        Apply(theme);
    }

    public static void Apply(ThemeDefinition t)
    {
        var app = Application.Current;
        if (app is null) return;

        var fallback = BuiltIn[0];
        var r = app.Resources;

        r["Bg"] = Brush(t.Background, fallback.Background);
        r["Surface"] = Brush(t.Surface, fallback.Surface);
        r["SurfaceAlt"] = Brush(t.SurfaceAlt, fallback.SurfaceAlt);
        r["Stroke"] = Brush(t.Stroke, fallback.Stroke);
        r["Text"] = Brush(t.Text, fallback.Text);
        r["TextMuted"] = Brush(t.TextMuted, fallback.TextMuted);
        r["Good"] = Brush(t.Good, fallback.Good);
        r["Warn"] = Brush(t.Warn, fallback.Warn);
        r["Hot"] = Brush(t.Hot, fallback.Hot);

        var accent = Parse(t.Accent, fallback.Accent);
        r["Accent"] = new SolidColorBrush(accent);
        r["AccentSoft"] = new SolidColorBrush(accent, 0.16);

        // Make Fluent's built-in controls (toggles, accent buttons, focus rings) follow the theme.
        r["SystemAccentColor"] = accent;
        var onAccent = Brush(t.OnAccent, fallback.OnAccent);
        r["AccentButtonForeground"] = onAccent;
        r["AccentButtonForegroundPointerOver"] = onAccent;
        r["AccentButtonForegroundPressed"] = onAccent;

        app.RequestedThemeVariant = t.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void LoadCustom()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.ThemesDir);
            var files = Directory.GetFiles(SettingsService.ThemesDir, "*.json");
            if (files.Length == 0)
            {
                WriteExample();
                files = Directory.GetFiles(SettingsService.ThemesDir, "*.json");
            }

            foreach (var file in files)
            {
                try
                {
                    var theme = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(file), SettingsService.Json);
                    if (theme is null || string.IsNullOrWhiteSpace(theme.Name)) continue;
                    All.RemoveAll(x => string.Equals(x.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
                    All.Add(theme);
                }
                catch
                {
                    // Skip a broken theme file rather than failing startup.
                }
            }
        }
        catch
        {
            // AppData not writable: built-ins still work.
        }
    }

    private static void WriteExample()
    {
        var example = new ThemeDefinition
        {
            Name = "Sakura",
            Background = "#141014", Surface = "#1C161C", SurfaceAlt = "#251D25", Stroke = "#33283A",
            Text = "#F7EEF3", TextMuted = "#9C8A95", Accent = "#FF7EB6", OnAccent = "#1A0A12",
        };
        File.WriteAllText(Path.Combine(SettingsService.ThemesDir, "sakura.json"),
            JsonSerializer.Serialize(example, SettingsService.Json));
    }

    private static Color Parse(string? hex, string fallback) =>
        hex is not null && Color.TryParse(hex, out var c) ? c : Color.Parse(fallback);

    private static SolidColorBrush Brush(string? hex, string fallback) => new(Parse(hex, fallback));
}
