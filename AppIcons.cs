using System;
using System.Drawing;

namespace TopViewport;

public enum AppIconState
{
    Idle,
    Active,
    Recovering,
    Error
}

/// <summary>Application-owned icons shared by the window and notification area.</summary>
public static class AppIcons
{
    private static readonly Lazy<Icon> idle = new(() => Load("idle"));
    private static readonly Lazy<Icon> active = new(() => Load("active"));
    private static readonly Lazy<Icon> recovering = new(() => Load("recovering"));
    private static readonly Lazy<Icon> error = new(() => Load("error"));

    /// <summary>Returns a cached icon. Callers must not dispose the returned object.</summary>
    public static Icon Get(AppIconState state) => state switch
    {
        AppIconState.Active => active.Value,
        AppIconState.Recovering => recovering.Value,
        AppIconState.Error => error.Value,
        _ => idle.Value
    };

    private static Icon Load(string state)
    {
        string resource = $"TopViewport.assets.topviewport-{state}.ico";
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing application icon resource: {resource}");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
