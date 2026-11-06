namespace DeziroJoy.App.ViewModels;

/// <summary>
/// How the leader key engages. Press swaps on each new press, so the pad returns to the
/// first profile on the next press; Hold swaps only while the key is physically held, so
/// the second profile is a momentary layer.
/// </summary>
public enum LeaderMode
{
    Press,
    Hold,
}

/// <summary>Which of the two leader profiles is currently applied.</summary>
public enum LeaderSlot
{
    Primary,
    Alternate,
}

/// <summary>A leader mode as the settings combo box shows it.</summary>
public sealed record LeaderModeOption(LeaderMode Value, string Label);
