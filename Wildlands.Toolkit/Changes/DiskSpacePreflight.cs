using System.IO;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public static class DiskSpacePreflight
{
    public const long SafetyMargin = 1L << 30;

    public static IReadOnlyList<DriveSpaceRequirement> Calculate(
        IReadOnlyList<ArchiveWork> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var requirements = new List<DriveSpaceRequirement>();

        foreach (IGrouping<string, ArchiveWork> perDrive in plans.GroupBy(
                     work => Path.GetPathRoot(Path.GetFullPath(work.Path))
                         ?? throw new InvalidDataException($"{work.Path} has no drive."),
                     StringComparer.OrdinalIgnoreCase))
        {
            long consumed = 0;
            long peak = 0;
            foreach (ArchiveWork work in perDrive)
            {
                if (!ArchiveBackup.Exists(work.Path))
                {
                    consumed = checked(consumed + work.Size);
                    peak = Math.Max(peak, consumed);
                }

                if (work.NeedsRebuild)
                {
                    peak = Math.Max(peak, checked(consumed + work.OutputSize
                        + work.IntermediateSize));
                    consumed = checked(consumed + work.OutputSize - work.Size);
                }
            }

            if (peak == 0)
                continue;

            var drive = new DriveInfo(perDrive.Key);
            requirements.Add(new DriveSpaceRequirement(drive.Name,
                checked(peak + SafetyMargin), drive.AvailableFreeSpace));
        }

        return requirements;
    }

    public static void Validate(IReadOnlyList<ArchiveWork> plans)
    {
        List<DriveSpaceRequirement> insufficient = Calculate(plans)
            .Where(requirement => !requirement.HasEnoughSpace)
            .ToList();
        if (insufficient.Count == 0)
            return;

        string details = string.Join(Environment.NewLine, insufficient.Select(requirement =>
            $"{requirement.DriveName}: {FormatBytes(requirement.RequiredBytes)} required, "
            + $"{FormatBytes(requirement.AvailableBytes)} available"));
        throw new IOException("There is not enough free disk space to apply these changes safely."
            + Environment.NewLine + Environment.NewLine + details
            + Environment.NewLine + Environment.NewLine
            + "The requirement includes backups, temporary rebuild files, archive growth, and a 1 GB safety margin.");
    }

    public static string FormatBytes(long bytes)
    {
        const long gib = 1L << 30;
        const long mib = 1L << 20;
        return bytes >= gib
            ? $"{bytes / (double)gib:0.0} GB"
            : $"{bytes / (double)mib:0} MB";
    }
}
