namespace FactionColonies.SupplyChain;

public class WorldObjectCompProperties_SettlementNeeds : WorldObjectCompProperties
{
    public WorldObjectCompProperties_SettlementNeeds()
    {
        compClass = typeof(WorldObjectComp_SettlementNeeds);
    }
}

/*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
/* Settlement needs comp.                                                 */
/*                                                                        */
/* Owns the settlement's need states (consumption demands vs. fulfilled   */
/* amounts) and translates shortfalls/surplus into faction stat           */
/* modifiers. Split out of WorldObjectComp_SupplyChain: needs are a       */
/* distinct concern from the resource ledger (stockpile/allocation/       */
/* tithe), and the resolver already passes the stockpile in separately.   */
/*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
public class WorldObjectComp_SettlementNeeds : WorldObjectComp, IStatModifierProvider, ISettlementPostLoadInit
{
    private List<NeedState> needStates = [];
    private bool hasAnyShortfall;

    private WorldSettlementFC cachedSettlement;

    public WorldSettlementFC WorldSettlement
    {
        get
        {
            cachedSettlement ??= parent as WorldSettlementFC;
            return cachedSettlement;
        }
    }

    // --- Needs ---

    public List<NeedState> NeedStates => needStates;

    public void SetNeedStates(List<NeedState> states)
    {
        needStates = states ?? [];
        UpdateHasAnyShortfall();
        statModsDirty = true;
    }

    private NeedState FindNeedState(string needId)
    {
        foreach (NeedState state in needStates)
        {
            if (state.needId == needId)
                return state;
        }
        return null;
    }

    /// <summary>
    /// Fully rebuilds needStates from current settlement state — base, building, and
    /// comp-provided needs — while preserving fulfilled values from the last tax resolution.
    /// Called on settlement creation, load, worker changes, building changes, and upgrades.
    /// </summary>
    public void RebuildNeedStates()
    {
        WorldSettlementFC ws = WorldSettlement;
        FactionFC faction = FindFC.FactionComp;
        if (ws is null || faction is null) return;

        // Preserve the satisfaction ratio (not the absolute fulfilled amount) and surplus
        // ratios from the last resolution. A rebuild can change `demanded` (an upgrade raises
        // the per-level need, a worker change raises the per-worker need); rescaling fulfilled
        // by the preserved ratio keeps a previously-met need shown as met, instead of pinning a
        // now-too-small absolute amount against the higher demand and flashing a false shortfall
        // (and its stat penalty) until the next daily resolution redraws.
        Dictionary<string, double> prevSatisfaction = [];
        Dictionary<string, double> prevSurplusRatio = [];
        foreach(NeedState state in needStates)
        {
            if (state.demanded > 0)
                prevSatisfaction[state.needId] = state.fulfilled / state.demanded;
            prevSurplusRatio[state.needId] = state.surplusRatio;
        }

        List<NeedState> newStates = [];

        // 1. Base settlement needs (from SettlementNeedDefs)
        foreach (SettlementNeedDef needDef in SupplyChainCache.AllNeedDefs)
        {
            if (!needDef.IsActiveForSettlement(ws)) continue;

            needDef.BuildNeedStates(ws, faction, 0.0, delegate(NeedState ns)
            {
                // Absent (brand-new need) => ratio 0 => unfulfilled until the first resolution.
                prevSatisfaction.TryGetValue(ns.needId, out double ratio);
                prevSurplusRatio.TryGetValue(ns.needId, out double prevSurplus);
                ns.fulfilled = ratio * ns.demanded;
                ns.surplusRatio = prevSurplus;
                newStates.Add(ns);
            });
        }

        // Building inputs are not needs here — they drive per-building dormancy
        // (BuildingFC.active), settled daily by NeedResolver.ResolveBuildingDormancy.

        // 2. Comp-provided needs (from INeedProvider)
        NeedResolver.ForEachProviderNeeds(ws, delegate(INeedProvider provider, List<NeedEntry> compNeeds)
        {
            foreach (NeedEntry entry in compNeeds)
            {
                prevSatisfaction.TryGetValue(entry.needId, out double ratio);
                prevSurplusRatio.TryGetValue(entry.needId, out double prevSurplus);
                NeedState ns = new(entry.needId, entry.resource, entry.amount, ratio * entry.amount,
                    entry.label, NeedCategory.Comp, entry.penalties,
                    entry.surplusBonuses, entry.maxSurplusRatio);
                ns.surplusRatio = prevSurplus;
                newStates.Add(ns);
            }
        });

        needStates = newStates;
        UpdateHasAnyShortfall();
        statModsDirty = true;
    }

    private void UpdateHasAnyShortfall()
    {
        hasAnyShortfall = false;
        foreach (NeedState state in needStates)
        {
            if (state.IsShort)
            {
                hasAnyShortfall = true;
                return;
            }
        }
    }

    // --- IStatModifierProvider (needs slice) ---

    private Dictionary<FCStatDef, double> cachedStatMods;
    private bool statModsDirty = true;

    public double GetStatModifier(FCStatDef stat)
    {
        if (statModsDirty || cachedStatMods is null)
        {
            if (cachedStatMods is null)
                cachedStatMods = [];
            else
                cachedStatMods.Clear();
            statModsDirty = false;
        }

        if (cachedStatMods.TryGetValue(stat, out double val))
            return val;

        val = ComputeStatModifier(stat);
        cachedStatMods[stat] = val;
        return val;
    }

    private double ComputeStatModifier(FCStatDef stat)
    {
        double value = stat.IdentityValue;

        if (stat.aggregation == FCStatAggregation.Additive)
        {
            // 0. Suppress natural stat stabilization when any need is unmet
            if (hasAnyShortfall)
            {
                if (stat == FCStatDefOf.happinessGainedBase)
                    value -= FCSettings.happinessBaseGain;
                else if (stat == FCStatDefOf.loyaltyGainedBase)
                    value -= FCSettings.loyaltyBaseGain;
                else if (stat == FCStatDefOf.unrestLostBase)
                    value -= FCSettings.unrestBaseLost;
            }

            // 1. Penalties for unmet needs
            foreach (NeedState state in needStates)
            {
                if (state.penalties is null || !state.IsShort)
                    continue;
                double shortfall = state.demanded - state.fulfilled;
                foreach (NeedPenalty penalty in state.penalties)
                {
                    if (penalty.stat == stat)
                        value += penalty.penaltyPerUnit * shortfall;
                }
            }

            // 2. Surplus bonuses
            foreach (NeedState state in needStates)
            {
                if (state.surplusBonuses is null || state.surplusRatio <= 0)
                    continue;
                double fraction = SurplusFraction(state);
                foreach (NeedSurplusBonus bonus in state.surplusBonuses)
                {
                    if (bonus.stat == stat)
                        value += bonus.maxBonus * fraction;
                }
            }
        }
        else // Multiplicative
        {
            // Tax efficiency: 1.0 + 0.20 * averageSatisfaction
            FCStatDef taxEffStat = SCStatDefOf.SC_TaxEfficiency;
            if (stat == taxEffStat && TryGetAverageSatisfaction(out double avgSat))
                value = FormulaUtil.TaxEfficiency(avgSat);
        }

        return value;
    }

    // How far a need's surplus is toward its full bonus, from 0 to 1. A need with no max surplus
    // ratio set reaches the full bonus at twice its demand.
    private static double SurplusFraction(NeedState state)
    {
        double maxSR = state.maxSurplusRatio > 0 ? state.maxSurplusRatio : 2.0;
        return Math.Min(1.0, state.surplusRatio / maxSR);
    }

    // Average satisfaction over the needs with a positive demand; false when there are none.
    private bool TryGetAverageSatisfaction(out double average)
    {
        double sum = 0;
        int count = 0;
        foreach (NeedState state in needStates)
        {
            if (state.demanded > 0) { sum += state.Satisfaction; count++; }
        }
        average = count > 0 ? sum / count : 0;
        return count > 0;
    }

    // Unrest is displayed inverted from the other daily-drift stats: a rise in unrest is
    // bad (shown as a positive number) whereas a rise in happiness/loyalty/prosperity is
    // good. Both ColorizePenalty and the surplus line branch on this.
    private static bool IsUnrestStat(FCStatDef stat)
    {
        return stat == FCStatDefOf.unrestGainedBase || stat == FCStatDefOf.unrestGainedMultiplier
            || stat == FCStatDefOf.unrestLostBase   || stat == FCStatDefOf.unrestLostMultiplier;
    }

    // A need penalty (and a suppressed base-drift) is always detrimental, so it renders
    // red. Happiness/loyalty losses show as a negative number; unrest is inverted, so a
    // rise in unrest shows as a positive number. `magnitude` is a positive quantity.
    private static string ColorizePenalty(double magnitude, FCStatDef stat)
    {
        return IsUnrestStat(stat)
            ? TextUtil.ColorizeAdditiveBonus(magnitude, invert: true)      // "+mag" red
            : TextUtil.ColorizeAdditiveBonus(magnitude, hardinvert: true); // "-mag" red
    }

    public string GetStatModifierDesc(FCStatDef stat)
    {
        string desc = null;

        // Stabilization suppression description. Shown as a real "<value> - <label>" line
        // that visibly counteracts the natural daily drift ComputeStatModifier cancels.
        // The suppression modifies the gain/loss-drift stat, but we describe it under the
        // stat the unmet-need penalties land on so it groups with them in the tooltip.
        // magnitude = the base drift that gets cancelled.
        if (hasAnyShortfall)
        {
            double magnitude = 0;
            if (stat == FCStatDefOf.happinessLostBase) magnitude = FCSettings.happinessBaseGain;
            else if (stat == FCStatDefOf.loyaltyLostBase) magnitude = FCSettings.loyaltyBaseGain;
            else if (stat == FCStatDefOf.unrestGainedBase) magnitude = FCSettings.unrestBaseLost;

            // .Resolve() flattens to a string while keeping the colored value's <color> tag;
            // raw "string + TaggedString" concatenation would StripTags() and drop the color.
            if (magnitude != 0)
                desc = "SC_StabilizationSuppressed".Translate(ColorizePenalty(magnitude, stat)).Resolve();
        }

        // Penalty descriptions
        foreach (NeedState state in needStates)
        {
            if (state.penalties is null || !state.IsShort)
                continue;
            double shortfall = state.demanded - state.fulfilled;
            foreach (NeedPenalty penalty in state.penalties)
            {
                if (penalty.stat != stat) continue;
                double val = penalty.penaltyPerUnit * shortfall;
                if (val <= 0) continue;
                val = Math.Round(val, 2);

                // .Resolve() flattens the TaggedString to a string while preserving the <color> tag;
                // the implicit string conversion would instead StripTags() and drop the color.
                string line = "SC_UnmetNeedPenalty".Translate(state.label, ColorizePenalty(val, stat)).Resolve();
                desc = desc is null ? line : $"{desc}\n{line}";
            }
        }

        // Surplus bonus descriptions
        foreach (NeedState state in needStates)
        {
            if (state.surplusBonuses is null || state.surplusRatio <= 0)
                continue;
            double fraction = SurplusFraction(state);
            foreach (NeedSurplusBonus bonus in state.surplusBonuses)
            {
                if (bonus.stat != stat) continue;
                double val = bonus.maxBonus * fraction;
                if (val <= 0) continue;

                // Surplus bonuses are always beneficial. Unrest surplus raises "unrest lost"
                // (a net reduction) and must render as a green negative; the other stats are
                // raised directly and render as a green positive.
                bool unrestStat = IsUnrestStat(bonus.stat);
                string line = TextUtil.AdditiveBonusLine(val, (bonus.label ?? state.label).CapitalizeFirst(),
                    invert: unrestStat, hardinvert: unrestStat);
                desc = desc is null ? line : $"{desc}\n{line}";
            }
        }

        // Tax efficiency description
        FCStatDef taxEffStat = SCStatDefOf.SC_TaxEfficiency;
        if (stat == taxEffStat && TryGetAverageSatisfaction(out double avgSat))
        {
            double mult = FormulaUtil.TaxEfficiency(avgSat);
            string line = "SC_TaxEfficiencyDesc".Translate(
                (avgSat * 100).ToString("F0"), (mult * 100).ToString("F0"));
            desc = desc is null ? line : $"{desc}\n{line}";
        }

        return desc;
    }

    // --- Save/Load ---

    public override void PostExposeData()
    {
        base.PostExposeData();
        Scribe_Collections.Look(ref needStates, "needStates", LookMode.Deep);
        needStates ??= [];
        UpdateHasAnyShortfall();
    }

    // --- ISettlementPostLoadInit ---

    public void PostSettlementLoadInit(WorldSettlementFC settlement)
    {
        if (settlement is null)
        {
            LogSC.Warning($"PostSettlementLoadInit (needs) encountered null settlement");
            return;
        }
        RebuildNeedStates();
    }
}
