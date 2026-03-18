using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace FactionColonies.SupplyChain
{
    public static class SupplyChainCache
    {
        private static WorldComponent_SupplyChain _comp;

        // BuildingFCDef -> BuildingNeedExtension lookup cache (defs don't change mid-game)
        private static Dictionary<BuildingFCDef, BuildingNeedExtension> _buildingExtCache
            = new Dictionary<BuildingFCDef, BuildingNeedExtension>();

        // Settlement -> WorldObjectComp_SupplyChain lookup cache
        private static Dictionary<WorldSettlementFC, WorldObjectComp_SupplyChain> _compCache
            = new Dictionary<WorldSettlementFC, WorldObjectComp_SupplyChain>();

        // Cached def lists (defs don't change mid-game)
        private static List<SettlementNeedDef> _cachedNeedDefs;
        private static List<ResourceTypeDef> _cachedResourceTypeDefs;

        public static WorldComponent_SupplyChain Comp
        {
            get
            {
                if (_comp == null)
                    _comp = Find.World?.GetComponent<WorldComponent_SupplyChain>();
                return _comp;
            }
        }

        public static List<SettlementNeedDef> AllNeedDefs
        {
            get
            {
                if (_cachedNeedDefs == null)
                    _cachedNeedDefs = DefDatabase<SettlementNeedDef>.AllDefsListForReading;
                return _cachedNeedDefs;
            }
        }

        public static List<ResourceTypeDef> AllResourceTypeDefs
        {
            get
            {
                if (_cachedResourceTypeDefs == null)
                    _cachedResourceTypeDefs = DefDatabase<ResourceTypeDef>.AllDefsListForReading;
                return _cachedResourceTypeDefs;
            }
        }

        /// <summary>
        /// Cached lookup for BuildingNeedExtension on BuildingFCDefs.
        /// Avoids repeated linear scans of modExtensions lists.
        /// </summary>
        public static BuildingNeedExtension GetBuildingNeedExt(BuildingFCDef def)
        {
            if (def == null) return null;

            BuildingNeedExtension ext;
            if (!_buildingExtCache.TryGetValue(def, out ext))
            {
                ext = def.GetModExtension<BuildingNeedExtension>();
                _buildingExtCache[def] = ext;
            }
            return ext;
        }

        /// <summary>
        /// Cached lookup for the SupplyChain comp on a settlement.
        /// Avoids repeated linear scans of AllComps.
        /// </summary>
        public static WorldObjectComp_SupplyChain GetSettlementComp(WorldSettlementFC settlement)
        {
            if (settlement == null) return null;

            WorldObjectComp_SupplyChain cached;
            if (!_compCache.TryGetValue(settlement, out cached))
            {
                cached = null;
                foreach (WorldObjectComp comp in settlement.AllComps)
                {
                    WorldObjectComp_SupplyChain sc = comp as WorldObjectComp_SupplyChain;
                    if (sc != null)
                    {
                        cached = sc;
                        break;
                    }
                }
                _compCache[settlement] = cached;
            }
            return cached;
        }

        public static void ClearCompCache()
        {
            _compCache.Clear();
        }

        public static void InvalidateCache()
        {
            _comp = null;
            _buildingExtCache.Clear();
            _compCache.Clear();
            _cachedNeedDefs = null;
            _cachedResourceTypeDefs = null;
        }
    }
}
