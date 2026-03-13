using System.Collections.Generic;

namespace FactionColonies.SupplyChain
{
    public static class SupplyRouteModifierRegistry
    {
        private static readonly List<ISupplyRouteModifier> _modifiers = new List<ISupplyRouteModifier>();

        public static IReadOnlyList<ISupplyRouteModifier> Modifiers
        {
            get { return _modifiers; }
        }

        public static void Register(ISupplyRouteModifier modifier)
        {
            if (!_modifiers.Contains(modifier))
                _modifiers.Add(modifier);
        }

        public static void Unregister(ISupplyRouteModifier modifier)
        {
            _modifiers.Remove(modifier);
        }

        public static void ClearAll()
        {
            _modifiers.Clear();
        }
    }
}
