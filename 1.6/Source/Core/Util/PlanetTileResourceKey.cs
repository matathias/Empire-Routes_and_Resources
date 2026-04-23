using System;
using RimWorld.Planet;

namespace FactionColonies.SupplyChain
{
    /// <summary>
    /// Compound dictionary key pairing a <see cref="PlanetTile"/> with a
    /// resource def index. Used by the supply-chain flow cache to track
    /// per-settlement, per-resource flow breakdowns. Keying by PlanetTile
    /// (rather than tileId) preserves layer awareness so that orbital and
    /// surface settlements at the same tileId don't collide.
    /// </summary>
    public struct PlanetTileResourceKey : IEquatable<PlanetTileResourceKey>
    {
        public readonly PlanetTile tile;
        public readonly ushort resourceIndex;

        public PlanetTileResourceKey(PlanetTile tile, ushort resourceIndex)
        {
            this.tile = tile;
            this.resourceIndex = resourceIndex;
        }

        public bool Equals(PlanetTileResourceKey other)
            => tile.Equals(other.tile) && resourceIndex == other.resourceIndex;

        public override bool Equals(object obj)
            => obj is PlanetTileResourceKey k && Equals(k);

        public override int GetHashCode()
            => (tile.GetHashCode() * 397) ^ resourceIndex;
    }
}
