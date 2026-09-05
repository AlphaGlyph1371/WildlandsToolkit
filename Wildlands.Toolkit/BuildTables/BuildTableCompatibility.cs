using System;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public enum BuildTableSlot
{
    Unknown, Barrel, Magazine, Optic, Underbarrel, Muzzle, Stock, SideRail, Attachment,
    Headgear, Vest, Backpack, Pants, Top, Gloves, Footwear, Facewear, Hair, Beard, Holster, Character,
}

public enum BuildTableAssetRole { Unknown, VariantSelector, Resource, AssetGroup }
public enum BuildTableCompatibilityLevel { Unverified }

public sealed record BuildTableCandidate(
    BuildTableTarget Target,
    BuildTableCompatibilityLevel Level,
    string Reason,
    int Score)
{
    public string Name => Target.Name;
    public string DisplayName => Target.Name;
    public string Location => Target.Location;
    public string Details => Target.Details;
    public string Compatibility => "Unverified";
}

public static class BuildTableCompatibility
{
    public static BuildTableCandidate Evaluate(string tableName,
        BuildTableReference reference, BuildTableTarget? current, BuildTableTarget candidate)
    {
        string reason;
        int score = 0;
        if (candidate.Id == 0)
            reason = "Empty reference; not a replacement asset.";
        else if (RoleOf(reference) != BuildTableAssetRole.Unknown
            && RoleOf(candidate) != RoleOf(reference))
            reason = "Different binary reference role.";
        else if (current is { ClassHash: not 0 } && candidate.ClassHash != 0
            && current.ClassHash != candidate.ClassHash)
            reason = "Different binary resource class.";
        else
        {
            reason = "The binary role matches, but no gameplay-database link confirms this replacement.";
            score = 1;
        }
        return new BuildTableCandidate(candidate, BuildTableCompatibilityLevel.Unverified, reason, score);
    }

    public static BuildTableAssetRole RoleOf(BuildTableReference reference)
    {
        if (reference.Path == "associated entity builder"
            || reference.Path.StartsWith("additional table ", StringComparison.Ordinal)
            || reference.Path.StartsWith("sub-table ", StringComparison.Ordinal))
            return BuildTableAssetRole.AssetGroup;

        return reference.Kind switch
        {
            BuildTableReferenceKind.Handle => BuildTableAssetRole.VariantSelector,
            BuildTableReferenceKind.FileReference => BuildTableAssetRole.Resource,
            BuildTableReferenceKind.Table or BuildTableReferenceKind.ObjectPointer => BuildTableAssetRole.AssetGroup,
            _ => BuildTableAssetRole.Unknown,
        };
    }

    public static BuildTableAssetRole RoleOf(BuildTableTarget target) => target.Type switch
    {
        "LODSelector" => BuildTableAssetRole.VariantSelector,
        "BuildTable" => BuildTableAssetRole.AssetGroup,
        "" => BuildTableAssetRole.Unknown,
        _ => BuildTableAssetRole.Resource,
    };

    public static BuildTableSlot DetectSlot(string value) => BuildTableSlot.Unknown;
    public static bool CouldBelongToSlot(string tableName, BuildTableTarget target) => false;
    public static string SlotName(BuildTableSlot slot) => "this BuildTable field";
}
