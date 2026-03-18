# Empire - Supply Chain: Implementation Plan

## Brainstorm Accuracy

**The brainstorm document (Ideas-Submods/SupplyRoutes-Brainstorm.md) is 100% accurate.** All 12 listed hooks exist and work as documented. No base mod changes needed. Verified:

- `ITaxTickParticipant` (4 hooks) — invoked from `FactionFC.AddTax()` and `WorldSettlementFC.CreateTax()`
- `SetStockpileAllocation(key, amount, onEvicted)` on ResourceFC — diverts production from taxes
- `ISettlementWindowOverview`, `IMainTabWindowOverview` + `MainTableRegistry` — UI extension
- `IStatModifierProvider`, `IResourceProductionModifier` — comp-based stat/production injection
- `FCEventCategoryDef` + event filtering — extensible categories with UI toggle
- All 9 registries confirmed
- Production formula unchanged: `rawTotal - stockpileAllocation = effectiveRaw → taxes/tithes`

---

## Design Decisions

- **Simple mode stockpile model**: Faction-level — one shared stockpile all settlements deposit into and draw from
- **Simple mode caps**: Sum of settlement warehouse caps (keeps building investment meaningful)
- **Overflow**: Auto-sell at penalty rate (incentivizes management without total waste)
- **Mode switching**: Supported mid-game
  - Simple → Complex: distribute faction stockpile proportionally to settlements by production share; routes start empty
  - Complex → Simple: sum all settlement stockpiles into faction stockpile; routes preserved but dormant (reactivate on switch back)

---

## Architecture

### Core Classes

```
WorldComponent_SupplyChain : WorldComponent, ITaxTickParticipant
├── Mode: enum { Simple, Complex }
├── FactionStockpile: Dict<ResourceTypeDef, double>     (Simple mode — the shared stockpile)
├── FactionStockpileCap: double                          (Simple mode — sum of settlement caps)
├── SupplyRoutes: List<SupplyRoute>                      (Complex mode)
├── DormantRoutes: List<SupplyRoute>                     (preserved when switching Complex→Simple)
├── ISupplyRouteModifier registry                        (Complex mode extensibility)
│
├── PreTaxResolution(faction):
│   if Simple:
│     1. AccumulateToFactionStockpile() — credit allocated units, respect cap
│     2. AutoSellOverflow() — excess → silver at penalty rate via oneTimeSilverIncome
│     3. ResolveBuildingInputs(faction stockpile) — check SCBuildingExtension, apply penalties
│     4. ResolveSellOrders(faction stockpile)
│     5. ResolveColonyShipments(faction stockpile)
│   if Complex:
│     1. AccumulateToLocalStockpiles() — credit to per-settlement stockpiles
│     2. ResolveRoutes() — pathfinding, efficiency, transfers between settlements
│     3. ResolveBuildingInputs(local stockpiles)
│     4. AutoSellOverflow() per settlement
│     5. ResolveSellOrders(local stockpiles)
│     6. ResolveColonyShipments(local stockpiles)
│
├── SwitchMode(newMode):
│   Simple→Complex: distribute faction stockpile by production share; restore dormant routes
│   Complex→Simple: merge local stockpiles; stash routes as dormant
│
└── ExposeData(): persist mode, faction stockpile, routes, dormant routes

WorldObjectComp_SupplyChain : WorldObjectComp, ISettlementWindowOverview, IStatModifierProvider
├── StockpileAllocations: Dict<ResourceTypeDef, double>   (both modes)
├── LocalStockpile: Dict<ResourceTypeDef, double>          (Complex mode only)
├── LocalCap: double                                        (derived from base + warehouse buildings)
├── SellOrders: List<SellOrder>                             (both modes)
├── StandingShipments: List<StandingShipment>               (both modes)
│
├── PostSpawnSetup(): register allocations with SetStockpileAllocation()
├── DrawOverviewTab():
│   if Simple → allocation sliders, sell/shipment orders (stockpile shown on faction tab)
│   if Complex → allocation sliders + local stockpile bars + routes + sell/shipment orders
│
└── ExposeData(): persist allocations, local stockpile, orders
```

### Shared Systems (Mode-Agnostic)

- **`SCBuildingExtension : DefModExtension`** on `BuildingFCDef` — XML-declared input requirements + penalty stat + penalty per unit. Identical in both modes; only the stockpile source differs.
- **Sell orders** — convert stockpile resources to silver via `oneTimeSilverIncome`. Same data structure, different source stockpile.
- **Colony shipments** — generate Things from stockpile, create `FCEvent` with `TravelUtil.ReturnTicksToArrive()` travel time. Same flow, different source stockpile.
- **`FCEventCategoryDef`** — submod defines `EC_SupplyRoute` for supply-related events. Filtered in events tab.
- **`SetStockpileAllocation`** registration — identical per-settlement per-resource registration in both modes.

### Stockpile Abstraction

To avoid duplicating "draw from stockpile" logic, use a simple interface:

```csharp
interface IStockpile
{
    double GetAmount(ResourceTypeDef resource);
    double Draw(ResourceTypeDef resource, double amount); // returns actual drawn
    void Credit(ResourceTypeDef resource, double amount);
    double GetCap(ResourceTypeDef resource);
}
```

- `FactionStockpile` wraps the WorldComponent's faction dict (Simple mode)
- `LocalStockpile` wraps the WorldObjectComp's local dict (Complex mode)
- Building input resolution, sell orders, colony shipments all take `IStockpile` — zero branching in the shared code

### Mode-Specific Code

**Simple mode only:**
- Faction stockpile accumulation (sum all settlements' allocated units into one dict)
- Faction cap calculation (sum of settlement caps, recalculated on building change)
- Faction-level UI tab (via `MainTableRegistry`) showing stockpile levels, caps, global sell/shipment controls

**Complex mode only:**
- `SupplyRoute` data class (source/dest tiles, resource, amount, cached path/efficiency)
- Route resolution logic (pathfinding, efficiency, `ISupplyRouteModifier` hooks)
- Route management UI on settlement tab
- Cached pathfinding results

### UI Layout

**Simple Mode — Faction Tab** (`IMainTabWindowOverview`):
- Resource bars showing faction stockpile levels vs caps
- Mode toggle button
- Global sell order management
- Colony shipment controls (pick resource + amount, shows travel time)

**Simple Mode — Settlement Tab** (`ISettlementWindowOverview`):
- Per-resource allocation sliders ("% of production → faction stockpile")
- Shows this settlement's contribution to faction cap

**Complex Mode — Settlement Tab** (`ISettlementWindowOverview`):
- Per-resource allocation sliders ("% of production → local stockpile")
- Local stockpile bars with caps
- Outgoing/incoming route list with efficiency
- Sell order and colony shipment controls

**Complex Mode — Faction Tab** (`IMainTabWindowOverview`):
- Mode toggle button
- Empire-wide route overview (all routes visualized)
- Summary of all settlement stockpile levels

---

## Comparison Table

| Aspect | Simple Mode | Complex Mode |
|--------|-------------|--------------|
| Storage | Faction-level shared stockpile | Per-settlement local stockpiles |
| Accumulation | All allocated production → faction stockpile | Allocated production → local stockpile |
| Building inputs | Draw from faction stockpile | Draw from local stockpile |
| Transfers | N/A (shared stockpile) | Route system with efficiency/pathfinding |
| Colony shipments | Draw from faction stockpile | Draw from local stockpile |
| Sell orders | Draw from faction stockpile | Draw from local stockpile |
| Caps | Sum of all settlement caps | Per-settlement caps |
| Overflow | Auto-sell at penalty | Auto-sell at penalty |
| UI focus | Faction tab (stockpile overview) | Settlement tab (local management + routes) |

---

## Code Reuse Estimate

~70% shared between modes:
- `SCBuildingExtension` + input/penalty resolution
- `SetStockpileAllocation` registration
- Colony shipment generation (Thing creation, FCEvent, travel time)
- Sell order silver conversion
- Event category + filtering
- `WorldObjectComp` allocation persistence
- `IStockpile` abstraction eliminates branching in shared mechanics

Mode-specific (~30%):
- Simple: faction stockpile data + faction-level UI
- Complex: route data structures, pathfinding, efficiency, route resolution, route UI

---

## Open Questions (from original brainstorm, still open)

1. **Settlement-to-settlement travel time model**: immediate efficiency loss (A) vs delayed delivery queue (B)? Start with A, add B later?
2. **Standing order priority**: when multiple orders exceed available stockpile — player-assigned priority, proportional share, or routes-first-then-sells?
3. **One-time shipments timing**: execute immediately (outside tax cycle) or queue for next tax period?
4. **Event batching**: one event per route, or batch deliveries to same destination? Batch with tooltip breakdown seems best.
5. **Penalty stat type**: use `FCStatDef` reference, or directly name the settlement field (prosperity/happiness/loyalty/unrest)?
