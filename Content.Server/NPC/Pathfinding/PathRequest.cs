using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Access;
using Content.Shared.Damage;
using Content.Shared.NPC;
using Content.Shared.StationRecords;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.NPC.Pathfinding;

/// <summary>
/// Stores the in-progress data of a pathfinding request.
/// </summary>
public abstract class PathRequest
{
    public EntityCoordinates Start;

    public Task<PathResult> Task => Tcs.Task;
    public readonly TaskCompletionSource<PathResult> Tcs;

    public List<PathPoly> Polys = new();

    // Each request is processed by one pathfinding worker at a time.
    public readonly HashSet<EntityUid> LookupScratch = new();
    public readonly Dictionary<EntityUid, bool> SmashCapabilityCache = new();
    public ICollection<ProtoId<AccessLevelPrototype>> AccessTags = new HashSet<ProtoId<AccessLevelPrototype>>();
    public ICollection<StationRecordKey> StationRecordKeys = new HashSet<StationRecordKey>();
    public bool AllowLockedDoorPrying;

    public bool Started = false;

    #region Pathfinding state

    public readonly Stopwatch Stopwatch = new();
    public PriorityQueue<ValueTuple<float, PathPoly>> Frontier = default!;
    public readonly Dictionary<PathPoly, float> CostSoFar = new();
    public readonly Dictionary<PathPoly, PathPoly> CameFrom = new();

    #endregion

    #region Data

    public readonly PathFlags Flags;
    public readonly int CollisionLayer;
    public readonly int CollisionMask;
    public readonly DamageSpecifier? SmashDamage;

    #endregion

    public PathRequest(EntityCoordinates start, PathFlags flags, int layer, int mask, CancellationToken cancelToken, DamageSpecifier? smashDamage = null)
    {
        Start = start;
        Flags = flags;
        CollisionLayer = layer;
        CollisionMask = mask;
        SmashDamage = smashDamage;
        Tcs = new TaskCompletionSource<PathResult>(cancelToken);
    }
}

public sealed class AStarPathRequest : PathRequest
{
    public EntityCoordinates End;

    /// <summary>
    /// How close we need to be to the end node to be considered as arrived.
    /// </summary>
    public float Distance;

    public AStarPathRequest(
        EntityCoordinates start,
        EntityCoordinates end,
        PathFlags flags,
        float distance,
        int layer,
        int mask,
        CancellationToken cancelToken,
        DamageSpecifier? smashDamage = null) : base(start, flags, layer, mask, cancelToken, smashDamage)
    {
        Distance = distance;
        End = end;
    }
}

public sealed class BFSPathRequest : PathRequest
{
    /// <summary>
    /// How far away we're allowed to expand in distance.
    /// </summary>
    public float ExpansionRange;

    /// <summary>
    /// How many nodes we're allowed to expand
    /// </summary>
    public int ExpansionLimit;

    public BFSPathRequest(
        float expansionRange,
        int expansionLimit,
        EntityCoordinates start,
        PathFlags flags,
        int layer,
        int mask,
        CancellationToken cancelToken,
        DamageSpecifier? smashDamage = null) : base(start, flags, layer, mask, cancelToken, smashDamage)
        {
            ExpansionRange = expansionRange;
            ExpansionLimit = expansionLimit;
        }
}

/// <summary>
/// Stores the final result of a pathfinding request
/// </summary>
public sealed class PathResultEvent
{
    public PathResult Result;
    public readonly List<PathPoly> Path;

    public PathResultEvent(PathResult result, List<PathPoly> path)
    {
        Result = result;
        Path = path;
    }
}
