using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Queries;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Utilises a <see cref="UtilityQueryPrototype"/> to determine the best target and sets it to the Key.
/// </summary>
public sealed partial class UtilityOperator : HTNOperator
{
    [Dependency] private IEntityManager _entManager = default!;
    [Dependency] private NPCUtilitySystem _npcUtilitySystem = default!;

    [DataField]
    public string Key = "Target";

    /// <summary>
    /// Keep the current target while it remains among the query's valid results.
    /// </summary>
    [DataField("retainCurrentTarget")]
    public bool RetainCurrentTarget;

    /// <summary>
    /// Minimum score advantage a new target needs before replacing a valid current target.
    /// </summary>
    [DataField("currentTargetScoreMargin")]
    public float CurrentTargetScoreMargin;

    [DataField]
    public ReturnTypeResult ReturnType = ReturnTypeResult.Highest;

    /// <summary>
    /// The EntityCoordinates of the specified target.
    /// </summary>
    [DataField]
    public string KeyCoordinates = "TargetCoordinates";

    [DataField("proto", required: true)]
    public ProtoId<UtilityQueryPrototype> Prototype;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var result = _npcUtilitySystem.GetEntities(blackboard, Prototype);
        Dictionary<string, object> effects;

        switch (ReturnType)
        {
            case ReturnTypeResult.Highest:
                var target = result.GetHighest();

                if (RetainCurrentTarget &&
                    blackboard.TryGetValue<EntityUid>(Key, out var currentTarget, _entManager) &&
                    result.Entities.TryGetValue(currentTarget, out var currentScore) &&
                    result.Entities.TryGetValue(target, out var bestScore) &&
                    bestScore - currentScore < CurrentTargetScoreMargin)
                {
                    target = currentTarget;
                }

                if (!target.IsValid())
                {
                    return (false, new Dictionary<string, object>());
                }

                effects = new Dictionary<string, object>()
                {
                    {Key, target},
                    {KeyCoordinates, new EntityCoordinates(target, Vector2.Zero)},
                };

                return (true, effects);

            case ReturnTypeResult.EnumerableDescending:
                var targetList = result.GetEnumerable();

                effects = new Dictionary<string, object>()
                {
                    {"TargetList", targetList},
                };

                return (true, effects);

            default:
                throw new NotImplementedException();
        }
    }

    public enum ReturnTypeResult
    {
        Highest,
        EnumerableDescending
    }
}
