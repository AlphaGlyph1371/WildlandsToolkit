using System.IO;
using System.Windows;

namespace Wildlands.Toolkit;

public partial class ModInstallWindow : Window
{
    public ModInstallWindow(ModPackageInfo package, string? workspaceNotice)
    {
        InitializeComponent();

        NameText.Text = package.Name;
        AuthorText.Text = string.IsNullOrWhiteSpace(package.Author)
            ? "Unknown author" : package.Author;
        VersionText.Text = string.IsNullOrWhiteSpace(package.Version)
            ? "Unspecified" : package.Version;
        PackageText.Text = $"{Path.GetFileName(package.Path)} ({FormatSize(package.Size)})";
        PackageText.ToolTip = package.Path;

        List<ModPackageChangeRow> rows = BuildRows(package.Operations);
        ChangeList.ItemsSource = rows;
        int archives = package.Operations.Select(operation => operation.Archive)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        ContentsText.Text = $"{Amount(rows.Count, "change")} · " +
                            $"{Amount(package.Operations.Count, "resource operation")} · " +
                            $"{Amount(archives, "archive")} · package format {package.FormatVersion}";

        WorkspaceNotice.Text = workspaceNotice ?? "";
        WorkspaceNotice.Visibility = string.IsNullOrWhiteSpace(workspaceNotice)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    static List<ModPackageChangeRow> BuildRows(IReadOnlyList<ModOperation> operations) =>
        operations.GroupBy(operation => string.IsNullOrWhiteSpace(operation.OperationGroupId)
                ? operation.Id : operation.OperationGroupId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModPackageChangeRow(group.ToList()))
            .ToList();

    static string Amount(int count, string noun) => count == 1
        ? $"1 {noun}"
        : $"{count} {noun}s";

    static string FormatSize(long bytes) => bytes >= 1L << 20
        ? $"{bytes / (double)(1L << 20):0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    void Install_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class ModPackageChangeRow
{
    public ModPackageChangeRow(IReadOnlyList<ModOperation> operations)
    {
        Label = operations.Select(operation => operation.OperationLabel)
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
            ?? Describe(operations[0].Kind);
        Kind = SummarizeKinds(operations);
        Target = Summarize(operations.Select(operation =>
            operation.ResourceName.Default(operation.EntryName)), "items");
        Archive = Summarize(operations.Select(operation =>
            Path.GetFileName(operation.Archive)), "archives");
    }

    public string Label { get; }
    public string Kind { get; }
    public string Target { get; }
    public string Archive { get; }

    static string SummarizeKinds(IReadOnlyList<ModOperation> operations)
    {
        string[] kinds = operations.Select(operation => Describe(operation.Kind))
            .Distinct(StringComparer.Ordinal).ToArray();
        return kinds.Length == 1 ? kinds[0] : "Mixed";
    }

    static string Describe(ModOperationKind kind) => kind switch
    {
        ModOperationKind.ReplaceResource => "Replace",
        ModOperationKind.AddResource => "Add resource",
        ModOperationKind.AddForgeEntry => "Add container",
        _ => "Change",
    };

    static string Summarize(IEnumerable<string> values, string plural)
    {
        string[] distinct = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length switch
        {
            0 => "—",
            1 => distinct[0],
            _ => $"{distinct.Length} {plural}",
        };
    }
}
