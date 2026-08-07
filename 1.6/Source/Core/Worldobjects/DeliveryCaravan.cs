using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    /* A lightweight world object that visualises an in-transit supply delivery by physically      */
    /* following the route's overland path (roads/terrain) at caravan pace. When it reaches the    */
    /* destination it credits the goods and removes its PendingDelivery, so it — not a formula —    */
    /* drives arrival for road-routed deliveries. No pawns/needs are ticked, so per-object cost is  */
    /* one path-follow step per tick plus one quad per frame.                                       */
    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    public class DeliveryCaravan : WorldObject
    {
        // Movement state (serialized so a mid-flight delivery resumes correctly on load).
        public List<PlanetTile> pathTiles;   // forward order: source -> destination
        private int curNodeIndex;            // index of the tile we are currently on
        private float nextTileCostLeft;      // ticks of movement cost left before entering the next tile
        private float nextTileCostTotal = 1f;

        // Label / display data (serialized).
        public int arrivalTick;
        private ResourceTypeDef resource;
        private double amount;
        private string sourceLabel;
        private string destLabel;

        // Serialized cross-reference back to the delivery this caravan is carrying.
        private PendingDelivery linkedDelivery;
        public PendingDelivery LinkedDelivery => linkedDelivery;

        // Runtime only.
        [Unsaved] private int cachedTicksPerMove = -1;
        [Unsaved] private bool arrivedHandled;

        private int TicksPerMove
        {
            get
            {
                if (cachedTicksPerMove < 0)
                {
                    cachedTicksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(null);
                }
                return cachedTicksPerMove;
            }
        }

        /// <summary>
        /// Spawns a caravan for a road-routed delivery. Returns null (no object) for deliveries without a
        /// usable overland path — those keep the straight-line, formula-based arrival.
        /// </summary>
        public static DeliveryCaravan Spawn(PendingDelivery d)
        {
            if (d is null || d.pathTiles is null || d.pathTiles.Count < 2) return null;

            DeliveryCaravan obj = (DeliveryCaravan)WorldObjectMaker.MakeWorldObject(SCWorldObjectDefOf.SC_DeliveryCaravan);
            obj.pathTiles = new List<PlanetTile>(d.pathTiles);
            obj.arrivalTick = d.arrivalTick;
            obj.resource = d.resource;
            obj.amount = d.amount;
            obj.sourceLabel = d.source?.Name;
            obj.destLabel = d.destination?.Name;
            obj.Tile = d.pathTiles[0];
            obj.SetFaction(Faction.OfPlayer);
            obj.linkedDelivery = d;
            obj.InitFollower();
            Find.WorldObjects.Add(obj);
            d.caravan = obj;
            return obj;
        }

        private void InitFollower()
        {
            curNodeIndex = 0;
            SetupMoveIntoNextTile();
        }

        private void SetupMoveIntoNextTile()
        {
            if (pathTiles is null || curNodeIndex + 1 >= pathTiles.Count)
            {
                nextTileCostLeft = 0f;
                nextTileCostTotal = 1f;
                return;
            }
            int cost = Caravan_PathFollower.CostToMove(TicksPerMove, pathTiles[curNodeIndex], pathTiles[curNodeIndex + 1]);
            nextTileCostTotal = cost;
            nextTileCostLeft = cost;
        }

        private float CostToPayThisTick()
        {
            float num = DebugSettings.fastCaravans ? 100f : 1f;
            if (num < nextTileCostTotal / 30000f)
            {
                num = nextTileCostTotal / 30000f;
            }
            return num;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (pathTiles is null || pathTiles.Count < 2)
            {
                Arrived();
                return;
            }
            if (nextTileCostLeft > 0f)
            {
                nextTileCostLeft -= CostToPayThisTick() * delta;
            }
            else
            {
                TryEnterNextTile();
            }
        }

        private void TryEnterNextTile()
        {
            curNodeIndex++;
            if (curNodeIndex >= pathTiles.Count - 1)
            {
                Tile = pathTiles[pathTiles.Count - 1];
                Arrived();
                return;
            }
            Tile = pathTiles[curNodeIndex];
            SetupMoveIntoNextTile();
        }

        private void Arrived()
        {
            if (arrivedHandled) return;
            arrivedHandled = true;

            WorldComponent_SupplyChain comp = Find.World.GetComponent<WorldComponent_SupplyChain>();
            comp?.CompleteDelivery(linkedDelivery);
            if (!Destroyed) Destroy();
        }

        public override void Destroy()
        {
            base.Destroy();
            if (linkedDelivery != null && linkedDelivery.caravan == this)
            {
                linkedDelivery.caravan = null;
            }
        }

        // Smoothly interpolate the drawn position between the current tile and the next tile, matching how
        // a vanilla caravan tweens along its path (progress = fraction of the current segment traversed).
        public override Vector3 DrawPos
        {
            get
            {
                if (pathTiles is null || pathTiles.Count == 0) return base.DrawPos;
                int i = Mathf.Clamp(curNodeIndex, 0, pathTiles.Count - 1);
                Vector3 from = Find.WorldGrid.GetTileCenter(pathTiles[i]);
                if (i + 1 >= pathTiles.Count) return from;
                Vector3 to = Find.WorldGrid.GetTileCenter(pathTiles[i + 1]);
                float progress = nextTileCostTotal > 0f ? Mathf.Clamp01(1f - nextTileCostLeft / nextTileCostTotal) : 0f;
                return to * progress + from * (1f - progress);
            }
        }

        // Draw the remaining route when selected, like a player caravan (WorldPath.DrawPath), but sourced
        // from our own pathTiles since we don't keep a live pooled WorldPath.
        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (pathTiles is null || pathTiles.Count == 0) return;

            const float lift = 0.08f;
            WorldGrid grid = Find.WorldGrid;
            int next = Mathf.Min(curNodeIndex + 1, pathTiles.Count - 1);

            // Segment from the caravan's current position to the next node.
            Vector3 drawPos = DrawPos;
            Vector3 nextCenter = grid.GetTileCenter(pathTiles[next]);
            drawPos += drawPos.normalized * lift;
            nextCenter += nextCenter.normalized * lift;
            if ((drawPos - nextCenter).sqrMagnitude > 0.005f)
                GenDraw.DrawWorldLineBetween(drawPos, nextCenter);

            // Remaining node-to-node segments to the destination.
            for (int i = next; i < pathTiles.Count - 1; i++)
            {
                Vector3 a = grid.GetTileCenter(pathTiles[i]);
                Vector3 b = grid.GetTileCenter(pathTiles[i + 1]);
                a += a.normalized * lift;
                b += b.normalized * lift;
                GenDraw.DrawWorldLineBetween(a, b);
            }
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!baseStr.NullOrEmpty()) sb.Append(baseStr);

            if (!sourceLabel.NullOrEmpty() && !destLabel.NullOrEmpty())
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("SC_DeliveryCaravanRoute".Translate(sourceLabel, destLabel));
            }
            if (resource != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("SC_DeliveryCaravanCargo".Translate(resource.LabelCap, Mathf.RoundToInt((float)amount)));
            }
            int remaining = Mathf.Max(0, arrivalTick - Find.TickManager.TicksGame);
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("SC_DeliveryCaravanETA".Translate(remaining.ToStringTicksToPeriod()));
            return sb.ToString();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pathTiles, "pathTiles", LookMode.Value);
            Scribe_Values.Look(ref curNodeIndex, "curNodeIndex", 0);
            Scribe_Values.Look(ref nextTileCostLeft, "nextTileCostLeft", 0f);
            Scribe_Values.Look(ref nextTileCostTotal, "nextTileCostTotal", 1f);
            Scribe_Values.Look(ref arrivalTick, "arrivalTick", 0);
            Scribe_Defs.Look(ref resource, "resource");
            Scribe_Values.Look(ref amount, "amount", 0.0);
            Scribe_Values.Look(ref sourceLabel, "sourceLabel");
            Scribe_Values.Look(ref destLabel, "destLabel");
            Scribe_References.Look(ref linkedDelivery, "linkedDelivery");
        }
    }

    [DefOf]
    public static class SCWorldObjectDefOf
    {
        public static WorldObjectDef SC_DeliveryCaravan;

        static SCWorldObjectDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SCWorldObjectDefOf));
        }
    }
}
