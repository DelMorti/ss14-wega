using Content.Shared.NPC;
using Content.Shared.Damage;
using Robust.Shared.Physics;

namespace Content.Server.NPC.Pathfinding;

public sealed partial class PathfindingSystem
{
    /*
     * Code that is common to all pathfinding methods.
     */

    /// <summary>
    /// Maximum amount of nodes we're allowed to expand.
    /// </summary>
    private const int NodeLimit = 512;

    private sealed class PathComparer : IComparer<ValueTuple<float, PathPoly>>
    {
        public int Compare((float, PathPoly) x, (float, PathPoly) y)
        {
            return y.Item1.CompareTo(x.Item1);
        }
    }

    private static readonly PathComparer PathPolyComparer = new();

    private List<PathPoly> ReconstructPath(Dictionary<PathPoly, PathPoly> path, PathPoly currentNodeRef)
    {
        var running = new List<PathPoly> { currentNodeRef };
        while (path.ContainsKey(currentNodeRef))
        {
            var previousCurrent = currentNodeRef;
            currentNodeRef = path[currentNodeRef];
            path.Remove(previousCurrent);
            running.Add(currentNodeRef);
        }

        running.Reverse();
        return running;
    }

    private float GetTileCost(PathRequest request, PathPoly start, PathPoly end)
    {
        var modifier = 1f;

        // TODO
        if ((end.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0x0)
        {
            return 0f;
        }

        // Corvax-Wega-Add-start
        if ((request.Flags & PathFlags.IgnoreStepTriggers) == 0x0 && !end.Equals(start))
        {
            var ents = request.LookupScratch;
            ents.Clear();
            _lookup.GetLocalEntitiesIntersecting(end.GraphUid, end.Box, ents);

            foreach (var ent in ents)
            {
                if (_stepTriggerQuery.HasComponent(ent))
                    return 2f * OctileDistance(end, start);
            }
        }
        // Corvax-Wega-Add-end

        var hasCollision = (request.CollisionLayer & end.Data.CollisionMask) != 0x0 ||
                           (request.CollisionMask & end.Data.CollisionLayer) != 0x0;

        var isDoor = (end.Data.Flags & PathfindingBreadcrumbFlag.Door) != 0x0;
        var isAccess = (end.Data.Flags & PathfindingBreadcrumbFlag.Access) != 0x0;
        if (hasCollision &&
            ((request.Flags & PathFlags.Smashing) != 0x0 || (isDoor && isAccess)))
        {
            request.LookupScratch.Clear();
            _lookup.GetLocalEntitiesIntersecting(end.GraphUid, end.Box, request.LookupScratch);
        }

        if (hasCollision)
        {
            var isClimb = (end.Data.Flags & PathfindingBreadcrumbFlag.Climb) != 0x0;

            if (isDoor)
            {
                var requiresPrying = isAccess && !CanAccessDoor(request);
                if (requiresPrying)
                {
                    if (!request.AllowLockedDoorPrying || (request.Flags & PathFlags.Prying) == 0x0)
                        return 0f;

                    // Retry locked doors only after ordinary routes fail.
                    modifier += (end.Data.Flags & PathfindingBreadcrumbFlag.PoweredDoor) != 0x0
                        ? 100000f
                        : 20f;
                }
                else if ((request.Flags & PathFlags.Interact) != 0x0)
                    modifier += 0.5f;
                else
                    // Last ditch—try to bump the door if it's the only feasible option.
                    modifier += 20f;
            }
            else if ((request.Flags & PathFlags.Smashing) != 0x0 && end.Data.SmashCost > 0f &&
                     CanDamageObstacle(request, request.LookupScratch))
            {
                modifier += end.Data.SmashCost;
            }
            else if (isClimb && (request.Flags & PathFlags.Climbing) != 0x0)
            {
                modifier += 0.5f;
            }
            else
            {
                return 0f;
            }
        }

        return modifier * OctileDistance(end, start);
    }

    private bool CanAccessDoor(PathRequest request)
    {
        foreach (var entity in request.LookupScratch)
        {
            if (_doorQuery.HasComponent(entity) &&
                _accessReaderQuery.TryComp(entity, out var reader) &&
                _access.IsAllowed(request.AccessTags, request.StationRecordKeys, entity, reader))
                return true;
        }

        return false;
    }

    private bool CanDamageObstacle(PathRequest request, HashSet<EntityUid> entities)
    {
        if (request.SmashDamage is not { } damage || !damage.AnyPositive())
            return false;

        foreach (var entity in entities)
        {
            if (request.SmashCapabilityCache.TryGetValue(entity, out var canDamage))
            {
                if (canDamage)
                    return true;
                continue;
            }

            if (!_destructibleQuery.HasComponent(entity) ||
                !_injurableQuery.TryComp(entity, out var injurable) ||
                !_fixturesQuery.TryComp(entity, out var fixtures) ||
                !CanBlockPath(request, fixtures))
            {
                request.SmashCapabilityCache[entity] = false;
                continue;
            }

            var applicableDamage = new DamageSpecifier();
            foreach (var (type, amount) in damage.DamageDict)
            {
                if (amount <= 0 || !_damageable.CanBeDamagedBy((entity, injurable), type))
                    continue;

                applicableDamage.DamageDict[type] = amount;
            }

            if (applicableDamage.Empty)
            {
                request.SmashCapabilityCache[entity] = false;
                continue;
            }

            if (!_damageableQuery.TryComp(entity, out var damageable) ||
                _damageable.GetDamageModifierSet((entity, damageable)) is not { } modifierSet ||
                !DamageSpecifier.ApplyModifierSet(applicableDamage, modifierSet).Empty)
            {
                request.SmashCapabilityCache[entity] = true;
                return true;
            }

            request.SmashCapabilityCache[entity] = false;
        }

        return false;
    }

    private static bool CanBlockPath(PathRequest request, FixturesComponent fixtures)
    {
        foreach (var fixture in fixtures.Fixtures.Values)
        {
            if (!fixture.Hard)
                continue;

            if ((request.CollisionLayer & fixture.CollisionMask) != 0 ||
                (request.CollisionMask & fixture.CollisionLayer) != 0)
                return true;
        }

        return false;
    }

    #region Simplifier

    public List<PathPoly> Simplify(List<PathPoly> vertices, float tolerance = 0)
    {
        // TODO: Needs more work
        if (vertices.Count <= 3)
            return vertices;

        var simplified = new List<PathPoly>();

        for (var i = 0; i < vertices.Count; i++)
        {
            // No wraparound for negative sooooo
            var prev = vertices[i == 0 ? vertices.Count - 1 : i - 1];
            var current = vertices[i];
            var next = vertices[(i + 1) % vertices.Count];

            var prevData = prev.Data;
            var currentData = current.Data;
            var nextData = next.Data;

            // If they collinear, continue
            if (i != 0 && i != vertices.Count - 1 &&
                prevData.Equals(currentData) &&
                currentData.Equals(nextData) &&
                IsCollinear(prev, current, next, tolerance))
            {
                continue;
            }

            simplified.Add(current);
        }

        // Farseer didn't seem to handle straight lines and nuked all points
        if (simplified.Count == 0)
        {
            simplified.Add(vertices[0]);
            simplified.Add(vertices[^1]);
        }

        // Check LOS and cut out more nodes
        // TODO: Grid cast
        // https://github.com/recastnavigation/recastnavigation/blob/c5cbd53024c8a9d8d097a4371215e3342d2fdc87/Detour/Source/DetourNavMeshQuery.cpp#L2455
        // Essentially you just do a raycast but a specialised version.

        return simplified;
    }

    private bool IsCollinear(PathPoly prev, PathPoly current, PathPoly next, float tolerance)
    {
        return FloatInRange(Area(prev, current, next), -tolerance, tolerance);
    }

    private float Area(PathPoly a, PathPoly b, PathPoly c)
    {
        var (ax, ay) = a.Box.Center;
        var (bx, by) = b.Box.Center;
        var (cx, cy) = c.Box.Center;

        return ax * (by - cy) + bx * (cy - ay) + cx * (ay - by);
    }

    private bool FloatInRange(float value, float min, float max)
    {
        return (value >= min && value <= max);
    }

    #endregion
}
