using System.Globalization;
using System.IO;
using System.Windows;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public partial class AddAssetWindow : Window
{
    readonly bool _container;

    public string AssetName => NameBox.Text.Trim();
    public ulong AssetId { get; private set; }
    public Resource? ResourceTemplate => (TemplateBox.SelectedItem as SetupChoice)?.Resource;
    public ForgeEntry? EntryTemplate => (TemplateBox.SelectedItem as SetupChoice)?.Entry;

    public AddAssetWindow(ResourceAdditionSource source, string target, IReadOnlyList<Resource> templates, Resource? preferred, ulong suggestedId)
    {
        InitializeComponent();
        Title = "Add resource";
        HeadingText.Text = $"Add {source.Type} resource";
        SourceText.Text = Path.GetFileName(source.Path);
        TargetText.Text = target;
        NameBox.Text = source.Name;
        IdBox.Text = $"0x{suggestedId:X16}";
        SetChoices(templates.Select(resource => new SetupChoice(resource)), choice => choice.Resource == preferred);
    }

    public AddAssetWindow(ContainerAdditionSource source, string target, IReadOnlyList<ForgeEntry> templates, ForgeEntry? preferred)
    {
        InitializeComponent();
        _container = true;
        Title = "Add .data container";
        HeadingText.Text = "Add .data container";
        SourceText.Text = Path.GetFileName(source.Path);
        TargetText.Text = target;
        NameBox.Text = source.Name;
        IdBox.Text = $"0x{source.Id:X16}";
        IdLabel.Visibility = Visibility.Collapsed;
        IdBox.Visibility = Visibility.Collapsed;
        TemplateRow.Visibility = Visibility.Collapsed;
        ErrorPanel.Margin = new Thickness(0, 12, 0, 0);
        Height = 320;
        MinHeight = 300;
        SetChoices(templates.Select(entry => new SetupChoice(entry)), choice => choice.Entry == preferred);
    }

    void SetChoices(IEnumerable<SetupChoice> choices, Func<SetupChoice, bool> preferred)
    {
        List<SetupChoice> list = choices.ToList();
        TemplateBox.ItemsSource = list;
        TemplateBox.SelectedItem = list.FirstOrDefault(preferred) ?? list.FirstOrDefault();
        if (list.Count == 0)
        {
            TemplateBox.IsEnabled = false;
            ErrorText.Text = _container
                ? "No compatible .data container was found in this archive."
                : "No compatible resource was found in this container.";
            AddButton.IsEnabled = false;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (AssetName.Length == 0)
        {
            ErrorText.Text = "Enter a name for the new asset.";
            return;
        }
        if (!TryId(IdBox.Text, out ulong id))
        {
            ErrorText.Text = "Enter a hexadecimal asset ID, for example 0x000000E012345678.";
            return;
        }
        if (TemplateBox.SelectedItem is null)
        {
            ErrorText.Text = "Choose a compatible existing asset.";
            return;
        }

        AssetId = id;
        DialogResult = true;
    }

    static bool TryId(string value, out ulong id)
    {
        value = value.Trim().Replace("_", "", StringComparison.Ordinal);
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public sealed class SetupChoice
    {
        public SetupChoice(Resource resource)
        {
            Resource = resource;
            Display = resource.Name;
        }

        public SetupChoice(ForgeEntry entry)
        {
            Entry = entry;
            Display = entry.Name;
        }

        public string Display { get; }
        public Resource? Resource { get; }
        public ForgeEntry? Entry { get; }
    }
}
