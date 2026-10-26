using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public partial class TweakItemVm : ObservableObject
{
    public TweakItemVm(TweakDefinition def) => Def = def;

    public TweakDefinition Def { get; }

    public string Name => Def.Name;
    public string Category => Def.Category;
    public string Description => Def.Description;
    public string EvidenceNote => Def.EvidenceNote;
    public string Warning => Def.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Def.Warning);

    public RiskLevel Risk => Def.Risk;
    public EvidenceRating Evidence => Def.Evidence;
    public string RiskText => Def.Risk.ToString();
    public string EvidenceText => Def.Evidence == EvidenceRating.Marginal ? "Placebo / Marginal" : Def.Evidence.ToString();

    /// <summary>Plain-English line shown first on the card (falls back to the technical description).</summary>
    public string Simple => string.IsNullOrWhiteSpace(Def.Simple) ? Def.Description : Def.Simple;

    /// <summary>
    /// Beginner verdict combining risk and evidence into one word, so nobody has to know what
    /// "Situational" means: 0 Recommended, 1 Worth a try, 2 Only if it fits your PC,
    /// 3 Skip unless curious, 4 Experts only.
    /// </summary>
    public int VerdictLevel => (Def.Risk, Def.Evidence) switch
    {
        (RiskLevel.Advanced, _) => 4,
        (_, EvidenceRating.Marginal) => 3,
        (RiskLevel.Safe, EvidenceRating.Proven) => 0,
        (RiskLevel.Safe, _) or (RiskLevel.Moderate, EvidenceRating.Proven) => 1,
        _ => 2
    };

    public string Verdict => VerdictLevel switch
    {
        0 => "Recommended",
        1 => "Worth a try",
        2 => "Only if it fits your PC",
        3 => "Skip unless curious",
        _ => "Experts only"
    };

    public string Flags
    {
        get
        {
            var parts = new List<string>();
            if (Def.RebootRequired) parts.Add("restart required");
            if (Def.SignOutRequired) parts.Add("sign out required");
            return string.Join(", ", parts);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _isApplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    public string ActionText => IsBusy ? "Working..." : IsApplied ? "Revert" : "Apply";
}

public partial class TweaksViewModel : ObservableObject
{
    public TweaksViewModel()
    {
        foreach (var def in AppServices.Catalog.Tweaks.Where(TweakEngine.IsApplicable))
            Items.Add(new TweakItemVm(def));

        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakItemVm.Category)));
        View.SortDescriptions.Add(new SortDescription(nameof(TweakItemVm.Category), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(TweakItemVm.Name), ListSortDirection.Ascending));
        View.Filter = o => o is TweakItemVm t && Matches(t);

        // Keep the toggles honest when something else (Quick Optimize, Change Log) changes state.
        AppServices.ChangeLog.Changed += (_, _) => UiThread.Post(async () => await RefreshStatesAsync());
    }

    public ObservableCollection<TweakItemVm> Items { get; } = new();
    public ICollectionView View { get; }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _notice = "";

    partial void OnSearchChanged(string value) => View.Refresh();

    private bool Matches(TweakItemVm t) =>
        string.IsNullOrWhiteSpace(Search) ||
        t.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
        t.Simple.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
        t.Category.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
        t.Description.Contains(Search, StringComparison.OrdinalIgnoreCase);

    public async Task RefreshStatesAsync()
    {
        foreach (var item in Items.ToList())
        {
            if (item.IsBusy) continue;
            item.IsApplied = await AppServices.Engine.IsAppliedAsync(item.Def);
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(TweakItemVm? item)
    {
        if (item == null) return;
        if (await ToggleItemAsync(item) is { } notice) Notice = notice;
    }

    /// <summary>
    /// Apply or revert one tweak with confirmation. Shared by every page that shows tweak cards
    /// (also the FPS Boost Center's runtime tweaks). Returns a notice to show, or null.
    /// </summary>
    public static async Task<string?> ToggleItemAsync(TweakItemVm item)
    {
        if (item.IsBusy) return null;

        var def = item.Def;
        var applying = !item.IsApplied;

        if (applying && !Confirm(item)) return null;

        item.IsBusy = true;
        try
        {
            var result = applying
                ? await AppServices.Engine.ApplyAsync(def)
                : await AppServices.Engine.RevertAsync(def);

            string? notice = null;
            if (!result.Success)
            {
                MessageBox.Show(result.Message, "Vanta", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                var notes = new List<string>();
                if (result.RebootRequired) notes.Add($"Restart Windows to finish: {def.Name}.");
                if (result.SignOutRequired) notes.Add($"Sign out and back in for '{def.Name}' to fully take effect.");
                if (applying && AppServices.Engine.LastRestorePointMessage is { } rp) notes.Add(rp);
                notice = string.Join("  ", notes);
            }

            item.IsApplied = await AppServices.Engine.IsAppliedAsync(def);
            return notice;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Toggle {def.Id} failed", ex);
            MessageBox.Show("Something went wrong: " + ex.Message, "Vanta", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static bool Confirm(TweakItemVm item)
    {
        var def = item.Def;
        var needsPrompt = def.Risk != RiskLevel.Safe || !string.IsNullOrWhiteSpace(def.Warning);
        if (!needsPrompt) return true;

        var text =
            $"Apply \"{def.Name}\"?\n\n" +
            $"{def.Description}\n\n" +
            $"Risk: {def.Risk}    Evidence: {item.EvidenceText}\n" +
            (string.IsNullOrWhiteSpace(def.EvidenceNote) ? "" : $"\n{def.EvidenceNote}\n") +
            (string.IsNullOrWhiteSpace(def.Warning) ? "" : $"\nWARNING: {def.Warning}\n") +
            (item.Flags.Length > 0 ? $"\nNote: {item.Flags}.\n" : "") +
            "\nA restore point and registry backup are created first. You can undo this any time.";

        var icon = def.Risk == RiskLevel.Advanced ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var defaultResult = def.Risk == RiskLevel.Advanced ? MessageBoxResult.No : MessageBoxResult.Yes;

        return MessageBox.Show(text, "Confirm change", MessageBoxButton.YesNo, icon, defaultResult) == MessageBoxResult.Yes;
    }
}
