using Verse;

namespace FactionColonies.SupplyChain
{
    public static class SupplyChainCache
    {
        private static WorldComponent_SupplyChain _comp;

        public static WorldComponent_SupplyChain Comp
        {
            get
            {
                if (_comp == null)
                    _comp = Find.World?.GetComponent<WorldComponent_SupplyChain>();
                return _comp;
            }
        }

        public static void InvalidateCache()
        {
            _comp = null;
        }
    }
}
