using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class AnimationBoneTracks
{
    public AnimationTrack? Rotation { get; set; }
    public AnimationTrack? Translation { get; set; }
}

public sealed class AnimationPlayer
{
    readonly List<SkeletonBone> _bones;
    readonly AnimationBoneTracks[] _tracks;
    readonly Matrix4x4[] _world;

    public AnimationPlayer(AnimationAsset animation, List<SkeletonBone> bones)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(bones);
        _bones = bones;
        _tracks = new AnimationBoneTracks[bones.Count];
        _world = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
            _tracks[i] = new AnimationBoneTracks();

        Duration = animation.Duration;
        var byName = new Dictionary<uint, int>();
        for (int i = 0; i < bones.Count; i++)
            byName.TryAdd(bones[i].Name, i);

        foreach (AnimationTrack track in animation.Tracks)
        {
            if (track.TimeFormat == 2 || !AnimationValues.CanRead(track.ValueFormat))
            {
                Unreadable++;
                continue;
            }

            if (!byName.TryGetValue(track.Bone, out int bone))
            {
                Unmatched++;
                continue;
            }

            AnimationChannel channel = AnimationValues.ChannelOf(track.ValueFormat);
            if (channel == AnimationChannel.Rotation)
                _tracks[bone].Rotation = track;
            else if (channel == AnimationChannel.Translation)
                _tracks[bone].Translation = track;
            else
                continue;
            Matched++;
        }
    }

    public float Duration { get; }

    public int Matched { get; }

    public int Unmatched { get; }

    public int Unreadable { get; }

    public int BoneCount => _bones.Count;

    public IReadOnlyList<Matrix4x4> Evaluate(float seconds)
    {
        for (int i = 0; i < _bones.Count; i++)
        {
            SkeletonBone bone = _bones[i];
            Quaternion rotation = _tracks[i].Rotation is { } track
                ? SampleRotation(track, seconds)
                : bone.LocalRotation;
            Vector3 position = _tracks[i].Translation is { } moving
                ? SampleTranslation(moving, seconds)
                : bone.LocalPosition;

            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(rotation);
            local.Translation = position;
            _world[i] = bone.ParentIndex >= 0 && bone.ParentIndex < i
                ? local * _world[bone.ParentIndex]
                : local;
        }

        return _world;
    }

    static Quaternion SampleRotation(AnimationTrack track, float seconds)
    {
        var (first, second, blend) = Bracket(track, seconds);
        Quaternion left = AnimationValues.ReadRotation(
            track.Values.AsSpan(first * track.Stride, track.Stride), track.ValueFormat);
        if (first == second)
            return left;
        Quaternion right = AnimationValues.ReadRotation(
            track.Values.AsSpan(second * track.Stride, track.Stride), track.ValueFormat);
        return Quaternion.Normalize(Quaternion.Slerp(left, right, blend));
    }

    static Vector3 SampleTranslation(AnimationTrack track, float seconds)
    {
        var (first, second, blend) = Bracket(track, seconds);
        Vector3 left = AnimationValues.ReadTranslation(
            track.Values.AsSpan(first * track.Stride, track.Stride), track.ValueFormat);
        if (first == second)
            return left;
        Vector3 right = AnimationValues.ReadTranslation(
            track.Values.AsSpan(second * track.Stride, track.Stride), track.ValueFormat);
        return Vector3.Lerp(left, right, blend);
    }

    static (int First, int Second, float Blend) Bracket(AnimationTrack track, float seconds)
    {
        if (track.KeyCount == 1)
            return (0, 0, 0f);

        IReadOnlyList<int> times = AnimationValues.ReadTimes(track);
        float wanted = seconds * AnimationValues.TimeUnitsPerSecond;
        if (wanted <= times[0])
            return (0, 0, 0f);
        for (int i = 1; i < times.Count; i++)
        {
            if (wanted > times[i])
                continue;
            int span = times[i] - times[i - 1];
            return (i - 1, i, span <= 0 ? 0f : (wanted - times[i - 1]) / span);
        }

        return (times.Count - 1, times.Count - 1, 0f);
    }
}
