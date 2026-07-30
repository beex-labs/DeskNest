using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BeexWrite.Services;

/// <summary>
/// Applies the light/dark Bx resource dictionaries to BeexWrite windows.
/// Embedded in DeskNest the effective theme always follows the host theme
/// (<see cref="WriteHost.IsHostDark"/>); the mode argument kept for API
/// compatibility is ignored. Dictionaries are merged per-window (not into
/// Application resources) so DeskNest's own implicit styles stay untouched.
/// </summary>
public sealed class ThemeService
{
    private const string ThemeUriPrefix = "pack://application:,,,/Write/Themes/";

    private static readonly List<WeakReference<Window>> Attached = new();

    public string Mode { get; private set; } = "system";

    /// <summary>Raised with the effective theme ("light"/"dark") after it changes.</summary>
    public event Action<string>? EffectiveThemeChanged;

    public string EffectiveTheme => ResolveEffective();

    public void Apply(string mode)
    {
        Mode = string.IsNullOrWhiteSpace(mode) ? "system" : mode.ToLowerInvariant();
        var effective = ResolveEffective();
        SwapDictionaries(effective);
        EffectiveThemeChanged?.Invoke(effective);
    }

    /// <summary>Re-apply when the DeskNest theme changes.</summary>
    public void OnHostThemeChanged() => Apply(Mode);

    private static string ResolveEffective() =>
        WriteHost.IsHostDark?.Invoke() == true ? "dark" : "light";

    /// <summary>Merges the control styles + current palette into a window's resources.
    /// Call before InitializeComponent-dependent rendering (e.g. in the constructor).</summary>
    public static void Attach(Window window)
    {
        var merged = window.Resources.MergedDictionaries;
        if (!merged.Any(d => d.Source != null && d.Source.OriginalString.EndsWith("Controls.xaml", StringComparison.OrdinalIgnoreCase)))
        {
            merged.Add(new ResourceDictionary { Source = new Uri(ThemeUriPrefix + (ResolveEffective() == "dark" ? "Dark" : "Light") + ".xaml") });
            merged.Add(new ResourceDictionary { Source = new Uri(ThemeUriPrefix + "Controls.xaml") });
        }
        Attached.RemoveAll(r => !r.TryGetTarget(out _));
        if (!Attached.Any(r => r.TryGetTarget(out var w) && ReferenceEquals(w, window)))
        {
            Attached.Add(new WeakReference<Window>(window));
            window.Closed += (_, _) => Attached.RemoveAll(r => !r.TryGetTarget(out var w) || ReferenceEquals(w, window));
        }
    }

    private static void SwapDictionaries(string effective)
    {
        var uri = new Uri(ThemeUriPrefix + (effective == "dark" ? "Dark" : "Light") + ".xaml");
        foreach (var weak in Attached.ToList())
        {
            if (!weak.TryGetTarget(out var window)) continue;
            var merged = window.Resources.MergedDictionaries;
            var existing = merged.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                 d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)));
            if (existing != null)
            {
                if (existing.Source == uri) continue;
                merged[merged.IndexOf(existing)] = new ResourceDictionary { Source = uri };
            }
            else
            {
                merged.Insert(0, new ResourceDictionary { Source = uri });
            }
        }
    }
}
