namespace TuneUp.Models;

/// <summary>
/// One low-level change. Everything is data-driven so new tweaks are just JSON entries.
/// Kind = "registry": writes a registry value.
/// Kind = "service" : sets a Windows service start type (Disabled / Manual / Automatic)
///                    and stops/starts it. Stored in the registry under
///                    HKLM\SYSTEM\CurrentControlSet\Services\NAME\Start.
/// All values are strings in JSON and parsed according to ValueType
/// (DWord, QWord, String, ExpandString, Binary[base64]).
/// </summary>
public sealed class TweakOperation
{
    public string Kind { get; set; } = "registry";

    // registry
    public string Hive { get; set; } = "HKCU";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string ValueType { get; set; } = "DWord";

    /// <summary>Value written when the tweak is applied.</summary>
    public string ApplyValue { get; set; } = "";

    /// <summary>
    /// Windows default, used only when there is no recorded original (e.g. tweak was already
    /// active before Vanta touched it). null = delete the value on revert.
    /// </summary>
    public string? DefaultValue { get; set; }
}

/// <summary>A tweak as described in the JSON catalog.</summary>
public sealed class TweakDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";

    /// <summary>One sentence in plain English for people who are not PC enthusiasts.</summary>
    public string Simple { get; set; } = "";

    public string Description { get; set; } = "";

    public RiskLevel Risk { get; set; } = RiskLevel.Safe;
    public EvidenceRating Evidence { get; set; } = EvidenceRating.Situational;

    /// <summary>Honest note about what the evidence actually is.</summary>
    public string EvidenceNote { get; set; } = "";

    /// <summary>Shown in the confirmation dialog and on the card (security / anti-cheat caveats).</summary>
    public string Warning { get; set; } = "";

    public bool RebootRequired { get; set; }
    public bool SignOutRequired { get; set; }

    public WindowsTarget AppliesTo { get; set; } = WindowsTarget.Both;

    /// <summary>
    /// Candidate for the Dashboard's "Quick Optimize". The engine additionally requires
    /// Risk == Safe AND Evidence == Proven, so a typo here can never sneak something risky in.
    /// </summary>
    public bool QuickOptimize { get; set; }

    public List<TweakOperation> Operations { get; set; } = new();
}

public sealed class TweakCatalog
{
    public List<TweakDefinition> Tweaks { get; set; } = new();
}
