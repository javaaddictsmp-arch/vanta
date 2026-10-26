namespace TuneUp.Models;

/// <summary>How much can go wrong if the tweak is applied.</summary>
public enum RiskLevel
{
    Safe,
    Moderate,
    Advanced
}

/// <summary>
/// Honest rating of how much real-world evidence there is that a tweak helps.
/// Proven      = measurable on most systems.
/// Situational = helps on specific hardware / games / setups.
/// Marginal    = placebo-level or tiny; included for completeness, never in Quick Optimize.
/// </summary>
public enum EvidenceRating
{
    Proven,
    Situational,
    Marginal
}

public enum WindowsTarget
{
    Both,
    Win10,
    Win11
}
