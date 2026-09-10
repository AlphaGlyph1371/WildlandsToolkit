using System.Windows;

namespace Wildlands.Toolkit;

public partial class IndexSetupWindow : Window
{
    public IndexSetupWindow(bool armoryReady, bool skeletonReady, bool includeArmory = true)
    {
        InitializeComponent();

        if (!includeArmory)
        {
            ArmoryPanel.Visibility = Visibility.Collapsed;
            SkeletonTitle.Text = "Skeleton index";
            Height = 330;
        }

        ArmoryText.Text = armoryReady
            ? "Ready. It stores confirmed game database and language data for the Armory editor."
            : "Required for the Armory editor. It reads the game's attachment, weapon, gear and language data once so Gunsmith editing does not need a full archive search.";
        SkeletonText.Text = skeletonReady
            ? "Ready. It helps rigged mesh exports find their matching skeleton."
            : "Recommended for rigged mesh exports. This is the larger scan and can take several minutes, especially on a hard drive.";

        if ((!includeArmory || armoryReady) && skeletonReady)
            PrepareButton.Content = "Close";
    }

    void Prepare_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
