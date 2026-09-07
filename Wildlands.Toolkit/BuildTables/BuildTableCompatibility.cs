using System.Text.RegularExpressions;
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
    static readonly Regex WordBoundary = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    static readonly Regex Separator = new("[^A-Za-z0-9]+", RegexOptions.Compiled);

    public static BuildTableCandidate Evaluate(string tableName, BuildTableReference reference, BuildTableTarget? current, BuildTableTarget candidate)
    {
        string reason;
        int score = 0;
        if (candidate.Id == 0)
            reason = "Empty reference; not a replacement asset.";
        else if (RoleOf(reference) != BuildTableAssetRole.Unknown && RoleOf(candidate) != RoleOf(reference))
            reason = "Different binary reference role.";
        else if (current is { ClassHash: not 0 } && candidate.ClassHash != 0 && current.ClassHash != candidate.ClassHash)
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

    public static BuildTableSlot DetectSlot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BuildTableSlot.Unknown;

        string separated = WordBoundary.Replace(value, " ");
        string[] words = Separator.Split(separated)
            .Where(word => word.Length > 0)
            .Select(word => word.ToLowerInvariant())
            .ToArray();

        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            if (word == "no")
            {
                i++;
                continue;
            }

            string next = i + 1 < words.Length ? words[i + 1] : "";
            if (word is "underbarrel" or "underbarrels" or "foregrip" or "foregrips" || word == "under" && next is "barrel" or "barrels")
                return BuildTableSlot.Underbarrel;
            if (word is "siderail" or "siderails" || word == "side" && next is "rail" or "rails")
                return BuildTableSlot.SideRail;
            if (word is "flashhider" or "flashhiders" || word == "flash" && next is "hider" or "hiders" || word is "muzzle" or "muzzles" or "suppressor" or "suppressors" or "silencer" or "silencers")
                return BuildTableSlot.Muzzle;
            if (word is "magazine" or "magazines" or "mag")
                return BuildTableSlot.Magazine;
            if (word is "scope" or "scopes" or "optic" or "optics" or "sight" or "sights" or "ironsight" or "ironsights" || word == "iron" && next is "sight" or "sights")
                return BuildTableSlot.Optic;
            if (word is "stock" or "stocks")
                return BuildTableSlot.Stock;
            if (word is "barrel" or "barrels")
                return BuildTableSlot.Barrel;
            if (word is "headgear" or "headwear" or "helmet" or "helmets" or "hat" or "hats")
                return BuildTableSlot.Headgear;
            if (word is "facewear" or "eyewear" or "mask" or "masks" or "glasses" or "goggles")
                return BuildTableSlot.Facewear;
            if (word is "hair" or "hairstyle" or "hairstyles")
                return BuildTableSlot.Hair;
            if (word is "beard" or "beards" or "facialhair" || word == "facial" && next == "hair")
                return BuildTableSlot.Beard;
            if (word is "backpack" or "backpacks" or "rucksack" or "rucksacks" or "bag" or "bags")
                return BuildTableSlot.Backpack;
            if (word is "glove" or "gloves")
                return BuildTableSlot.Gloves;
            if (word is "pants" or "pant" or "trousers" or "trouser")
                return BuildTableSlot.Pants;
            if (word is "footwear" or "boot" or "boots" or "shoe" or "shoes")
                return BuildTableSlot.Footwear;
            if (word is "holster" or "holsters")
                return BuildTableSlot.Holster;
            if (word is "vest" or "vests" or "tacvest" or "tacticalvest" || word is "tac" or "tactical" && next is "vest" or "vests")
                return BuildTableSlot.Vest;
            if (word is "top" or "tops" or "torso" or "shirt" or "shirts" or "jacket" or "jackets")
                return BuildTableSlot.Top;
            if (word is "character" or "characters" or "body" or "bodies")
                return BuildTableSlot.Character;
            if (word is "attachment" or "attachments" or "parts")
                return BuildTableSlot.Attachment;
        }

        return BuildTableSlot.Unknown;
    }
}
