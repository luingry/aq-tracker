namespace AqTracker.Core;

public enum BackdropNonClientRendering { Disabled, Enabled }
public enum BackdropCornerPreference { DoNotRound, Round }

public readonly record struct BackdropComposition(
    BackdropNonClientRendering NonClientRendering,
    BackdropCornerPreference CornerPreference);

public static class BackdropCompositionPolicy
{
    public static BackdropComposition ForMode(WidgetVisualMode mode) => mode == WidgetVisualMode.Compact
        ? new(BackdropNonClientRendering.Disabled, BackdropCornerPreference.DoNotRound)
        : new(BackdropNonClientRendering.Enabled, BackdropCornerPreference.Round);
}
