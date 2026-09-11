using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public partial class MainWindow
{
    void ShowAnimation(BrowserItem item, List<string> lines)
    {
        if (item.Resource is null)
            return;

        try
        {
            _previewAnimation = Animation.Read(EffectiveData(item));
        }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add($"       animation unreadable: {ex.Message}");
            return;
        }

        _previewAnimationName = item.Resource.Name;
        lines.Add($"duration   {_previewAnimation.Duration:0.###} s "
            + $"({_previewAnimation.Duration * 30:0.#} frames at 30 fps)");
        lines.Add($"bone set   0x{_previewAnimation.BoneSet:X8}");
        lines.Add($"tracks     {_previewAnimation.Tracks.Count}");
        lines.Add($"keys       {_previewAnimation.Tracks.Sum(track => track.KeyCount)}");

        var channels = _previewAnimation.Tracks
            .GroupBy(track => AnimationValues.ChannelOf(track.ValueFormat))
            .OrderByDescending(group => group.Count());
        foreach (var group in channels)
            lines.Add($"  {group.Key.ToString().ToLowerInvariant(),-12} {group.Count(),4} track(s)");

        int readable = _previewAnimation.Tracks.Count(track => track.TimeFormat != 2
            && AnimationValues.CanRead(track.ValueFormat));
        lines.Add($"readable   {readable} of {_previewAnimation.Tracks.Count} track(s)");
        OpenAnimationButton.Visibility = Visibility.Visible;
    }

    void OpenAnimationViewer()
    {
        if (_previewAnimation is null)
            return;

        var wanted = _previewAnimation.Tracks.Select(track => track.Bone).ToHashSet();
        List<SkeletonBone>? bones = FindAnimationSkeleton(wanted, out string name);
        if (bones is null)
        {
            if (MessageBox.Show(this, "No skeleton for this animation was found in this container, "
                    + "and the skeleton index either is not built or holds no matching bones.\n\n"
                    + "Pick the data file holding the skeleton yourself?", "Animation",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            bones = SkeletonPicker.Choose(this, out name);
            if (bones is null || bones.Count == 0)
                return;
        }

        new AnimationWindow(_previewAnimation, _previewAnimationName, bones, name).Show();
    }

    List<SkeletonBone>? FindAnimationSkeleton(HashSet<uint> wanted, out string name)
    {
        name = "";
        List<SkeletonBone>? best = null;
        int bestOverlap = 0;
        foreach (Resource candidate in _previewSiblings.Where(item => item.ClassHash == Skeleton.ClassHash))
        {
            List<SkeletonBone> bones;
            try
            {
                bones = Skeleton.Read(candidate.Data);
            }
            catch
            {
                continue;
            }

            int overlap = bones.Count(bone => wanted.Contains(bone.Name));
            if (overlap <= bestOverlap)
                continue;
            bestOverlap = overlap;
            best = bones;
            name = candidate.Name;
        }

        if (best is not null)
            return best;

        List<SkeletonBone>? indexed = _skeletonIndex?.FindBest(wanted);
        if (indexed is null || indexed.All(bone => bone.LocalRotation == default))
            return null;

        name = "skeleton index";
        return indexed;
    }
}
