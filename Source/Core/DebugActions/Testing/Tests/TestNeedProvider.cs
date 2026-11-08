namespace FactionColonies.SupplyChain;

/// <summary>
/// A scripted <see cref="INeedProvider"/> comp for tests. It hands out a fixed list of needs, can be
/// told to throw partway through collection, and records what it was asked and told. Tests attach it
/// to a settlement's <see cref="WorldObject.AllComps"/> and remove it again when done.
/// </summary>
public class TestNeedProvider : WorldObjectComp, INeedProvider
{
    /// <summary>Needs added on every <see cref="INeedProvider.CollectNeeds"/> call, in order.</summary>
    public List<NeedEntry> needs = [];

    /// <summary>When true, collection throws after adding <see cref="needs"/>.</summary>
    public bool throwOnCollect;

    /// <summary>When true, <see cref="INeedProvider.OnNeedsResolved"/> throws after recording.</summary>
    public bool throwOnResolved;

    public int collectCalls;
    public int resolvedCalls;
    public List<NeedResolution> lastResolved;

    void INeedProvider.CollectNeeds(WorldSettlementFC settlement, List<NeedEntry> into)
    {
        collectCalls++;
        into.AddRange(needs);
        if (throwOnCollect)
            throw new InvalidOperationException("TestNeedProvider was told to throw while collecting");
    }

    void INeedProvider.OnNeedsResolved(List<NeedResolution> resolvedNeeds)
    {
        resolvedCalls++;
        lastResolved = [.. resolvedNeeds];
        if (throwOnResolved)
            throw new InvalidOperationException("TestNeedProvider was told to throw on resolution");
    }

    /// <summary>A need for <paramref name="amount"/> of <paramref name="resource"/>, with an empty
    /// (non-null) surplus-bonus list so the resolver computes its surplus ratio.</summary>
    public static NeedEntry Need(string needId, ResourceTypeDef resource, double amount) => new()
    {
        needId = needId,
        label = needId,
        resource = resource,
        amount = amount,
        surplusBonuses = [],
        maxSurplusRatio = 2.0,
    };
}

/// <summary>A comp that is not an <see cref="INeedProvider"/>, to check that need collection skips it.</summary>
public class TestPlainComp : WorldObjectComp
{
}
