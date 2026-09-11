using System.IO;
using System.Windows;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public enum InstallMode
{
    Addon,
    InPlace,
}

public partial class InstallModeWindow : Window
{
    InstallModeWindow(IReadOnlyList<ArchiveWork> plans, string summary)
    {
        InitializeComponent();
        SummaryText.Text = summary;

        bool canAddon = AddonArchiveService.CanWrite(plans, out string reason);
        AddonOption.IsEnabled = canAddon;
        if (canAddon)
        {
            AddonSizeText.Text = Describe(AddonArchiveService.EstimateSize(plans));
            AddonTargetText.Text = "Creates " + string.Join(", ", plans
                .Select(work => AddonArchiveService.FamilyOf(work.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(family => $"{Path.GetFileName(family)}_patch_{AddonArchiveService.NextSlot(family + ".forge"):00}.forge"));
        }
        else
        {
            AddonBlockedText.Text = reason;
            AddonBlockedText.Visibility = Visibility.Visible;
            InPlaceOption.IsChecked = true;
        }

        string archives = string.Join(", ", plans.Select(work => work.Name));
        long backup = plans.Where(work => !ArchiveBackup.Exists(work.Path)).Sum(work => work.Size);
        InPlaceSizeText.Text = backup > 0
            ? $"Rewrites {archives} and backs up {Describe(backup)} first."
            : $"Rewrites {archives}. A backup of every one of them already exists.";
    }

    public InstallMode Mode { get; private set; } = InstallMode.Addon;

    public static InstallMode? Ask(Window owner, IReadOnlyList<ArchiveWork> plans, string summary)
    {
        var window = new InstallModeWindow(plans, summary) { Owner = owner };
        return window.ShowDialog() == true ? window.Mode : null;
    }

    static string Describe(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20
            ? $"{bytes / (double)(1L << 20):0.0} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Mode = AddonOption.IsChecked == true ? InstallMode.Addon : InstallMode.InPlace;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
