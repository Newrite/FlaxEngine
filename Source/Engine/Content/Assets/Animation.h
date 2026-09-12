// Copyright (c) Wojciech Figat. All rights reserved.

#pragma once

#include "../BinaryAsset.h"
#include "Engine/Core/Collections/Dictionary.h"
#include "Engine/Animations/AnimationData.h"
#include "Engine/Content/AssetReference.h"

class SkinnedModel;
class AnimEvent;

/// <summary>
/// Asset that contains an animation spline represented by a set of keyframes, each representing an endpoint of a linear curve.
/// </summary>
API_CLASS(NoSpawn) class FLAXENGINE_API Animation : public BinaryAsset
{
    DECLARE_BINARY_ASSET_HEADER(Animation, 1);

    /// <summary>
    /// Contains basic information about the animation asset contents.
    /// </summary>
    API_STRUCT() struct FLAXENGINE_API InfoData
    {
        DECLARE_SCRIPTING_TYPE_NO_SPAWN(InfoData);

        /// <summary>
        /// Length of the animation in seconds.
        /// </summary>
        API_FIELD() float Length;

        /// <summary>
        /// Amount of animation frames (some curve tracks may use less keyframes).
        /// </summary>
        API_FIELD() int32 FramesCount;

        /// <summary>
        /// Amount of animation channel tracks.
        /// </summary>
        API_FIELD() int32 ChannelsCount;

        /// <summary>
        /// The total amount of keyframes in the animation tracks.
        /// </summary>
        API_FIELD() int32 KeyframesCount;

        /// <summary>
        /// The estimated memory usage (in bytes) of the animation (all tracks and keyframes size in memory).
        /// </summary>
        API_FIELD() int32 MemoryUsage;
    };

    /// <summary>
    /// Contains <see cref="AnimEvent"/> instance.
    /// </summary>
    struct FLAXENGINE_API AnimEventData
    {
        float Duration = 0.0f;
        AnimEvent* Instance = nullptr;
#if USE_EDITOR
        StringAnsi TypeName;
#endif
    };

    /// <summary>
    /// Contains <see cref="AnimEvent"/> instance.
    /// </summary>
    struct FLAXENGINE_API NestedAnimData
    {
        float Time = 0.0f;
        float Duration = 0.0f;
        float Speed = 1.0f;
        float StartTime = 0.0f;
        bool Enabled = false;
        bool Loop = false;
        AssetReference<Animation> Anim;
    };

private:
#if USE_EDITOR
    bool _registerForScriptingReload = false;
    bool _registeredForScriptingReload = false;
    void OnScriptsReloadStart();
#endif

public:
    /// <summary>
    /// The animation data.
    /// </summary>
    AnimationData Data;

    /// <summary>
    /// The animation events (keyframes per named track).
    /// </summary>
    Array<Pair<String, StepCurve<AnimEventData>>> Events;

    /// <summary>
    /// The nested animations (animation per named track).
    /// </summary>
    Array<Pair<String, NestedAnimData>> NestedAnims;

public:
    /// <summary>
    /// Gets the length of the animation (in seconds).
    /// </summary>
    API_PROPERTY() float GetLength() const
    {
        return IsLoaded() ? Data.GetLength() : 0.0f;
    }

    /// <summary>
    /// Gets the duration of the animation (in frames).
    /// </summary>
    API_PROPERTY() float GetDuration() const
    {
        return (float)Data.Duration;
    }

    /// <summary>
    /// Gets the amount of the animation frames per second.
    /// </summary>
    API_PROPERTY() float GetFramesPerSecond() const
    {
        return (float)Data.FramesPerSecond;
    }

    /// <summary>
    /// Gets the animation clip info.
    /// </summary>
    API_PROPERTY() InfoData GetInfo() const;

    /// <summary>
    /// Initializes a virtual animation with the given metadata and clears any channels and events it
    /// already had. Use it before <see cref="AddChannel"/> to build an animation at runtime, for
    /// example when importing a clip from a format the engine does not read itself.
    /// </summary>
    /// <remarks>This can only be used by virtual assets.</remarks>
    /// <param name="duration">The animation duration, in FRAMES.</param>
    /// <param name="framesPerSecond">The animation sampling rate.</param>
    /// <param name="rootMotionFlags">What the root node channel carries, for root motion extraction.</param>
    /// <param name="rootNodeName">The node to take root motion from. Empty means the first channel.</param>
    /// <returns><c>true</c> if failed; otherwise, <c>false</c>.</returns>
    API_FUNCTION() bool Init(float duration, float framesPerSecond, AnimationRootMotionFlags rootMotionFlags = AnimationRootMotionFlags::None, const StringView& rootNodeName = StringView::Empty);

    /// <summary>
    /// Adds one node animation channel to a virtual animation, replacing any channel with the same node
    /// name. Keyframe times are in FRAMES, like the rest of <see cref="Animation"/>. A channel given a
    /// single keyframe holds that value for the whole animation; an empty one leaves the node alone.
    /// </summary>
    /// <remarks>This can only be used by virtual assets, and only after <see cref="Init"/>.</remarks>
    /// <param name="nodeName">The skeleton node name to animate. Matched case-insensitively.</param>
    /// <param name="positionTimes">Position keyframe times, in frames, ascending.</param>
    /// <param name="positions">Position keyframe values. Must be as long as positionTimes.</param>
    /// <param name="rotationTimes">Rotation keyframe times, in frames, ascending.</param>
    /// <param name="rotations">Rotation keyframe values. Must be as long as rotationTimes.</param>
    /// <param name="scaleTimes">Scale keyframe times, in frames, ascending.</param>
    /// <param name="scales">Scale keyframe values. Must be as long as scaleTimes.</param>
    /// <returns><c>true</c> if failed; otherwise, <c>false</c>.</returns>
    API_FUNCTION() bool AddChannel(const StringView& nodeName, const Span<float>& positionTimes, const Span<Float3>& positions, const Span<float>& rotationTimes, const Span<Quaternion>& rotations, const Span<float>& scaleTimes, const Span<Float3>& scales);

    /// <summary>
    /// Adds one event to a virtual animation, creating the named event track if it does not exist yet,
    /// and returns the instance it created so the caller can fill in the event's own fields. The
    /// instance is created from the type name exactly as a cooked animation does it and is owned by the
    /// animation, which deletes it on unload.
    /// </summary>
    /// <remarks>This can only be used by virtual assets.</remarks>
    /// <param name="trackName">The event track name.</param>
    /// <param name="time">The event time, in FRAMES - the same unit as the channel keyframes, because
    /// that is what the evaluator compares it against.</param>
    /// <param name="duration">The event duration in FRAMES; zero for a one-shot <see cref="AnimEvent"/>.
    /// A duration of one frame or less is treated as a one-shot.</param>
    /// <param name="typeName">The full name of the <see cref="AnimEvent"/> type to instantiate.</param>
    /// <param name="json">Optional event state as JSON, the same shape a cooked animation stores. Setting
    /// the returned instance's fields directly is simpler and does not depend on the type being
    /// JSON-serializable.</param>
    /// <returns>The event instance, or <c>null</c> if it could not be added.</returns>
    API_FUNCTION() AnimEvent* AddEvent(const StringView& trackName, float time, float duration, const StringAnsiView& typeName, const StringAnsiView& json = StringAnsiView::Empty);

#if USE_EDITOR
    /// <summary>
    /// Gets the animation as serialized timeline data. Used to show it in Editor.
    /// </summary>
    /// <param name="result">The output timeline data container. Empty if failed to load.</param>
    API_FUNCTION() void LoadTimeline(API_PARAM(Out) BytesContainer& result) const;

    /// <summary>
    /// Saves the serialized timeline data to the asset as animation.
    /// </summary>
    /// <remarks>This cannot be used by virtual assets.</remarks>
    /// <param name="data">The timeline data container.</param>
    /// <returns><c>true</c> failed to save data; otherwise, <c>false</c>.</returns>
    API_FUNCTION() bool SaveTimeline(BytesContainer& data);
#endif

private:
#if USE_EDITOR
    friend class ImportModel;
    static bool SaveHeader(const class ModelData& modelData, WriteStream& stream, int32 animIndex);
#endif

public:
    // [BinaryAsset]
#if USE_EDITOR
    void GetReferences(Array<Guid>& assets, Array<String>& files) const override;
    bool Save(const StringView& path = StringView::Empty) override;
#endif
    uint64 GetMemoryUsage() const override;
    void OnScriptingDispose() override;

protected:
    // [BinaryAsset]
    LoadResult load() override;
    void unload(bool isReloading) override;
#if USE_EDITOR
    void onLoaded_MainThread() override;
#endif
    AssetChunksFlag getChunksToPreload() const override;
};
