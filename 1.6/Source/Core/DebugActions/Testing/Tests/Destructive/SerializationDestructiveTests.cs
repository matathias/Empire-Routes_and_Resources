using System.IO;
using System.Linq;
using Verse;

namespace FactionColonies.SupplyChain
{
    /* DESTRUCTIVE: drives a real Scribe save/load cycle through a temp file to verify serialization
       round-trips for the reference-free POD holders (SellOrder, NeedState). It touches global Scribe
       state, so it is confined to the destructive tier and hard-resets Scribe (ForceStop) plus deletes
       the temp file in a finally block. Reference-bearing types (SupplyRoute / PendingDelivery) are NOT
       covered here — their source/destination ILoadReferenceable settlements need a full game save
       cycle to resolve, which this isolated harness can't provide. */
    public static class SerializationDestructiveTests
    {
        [EmpireDestructiveTest("SC.Destructive.Serialization")]
        public static void SellOrder_RoundTrips()
        {
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");

            SellOrder original = new SellOrder(r, 37.5);
            SellOrder loaded = ScribeRoundTrip(original, "sellOrder");

            TestAssert.IsNotNull(loaded, "Round-tripped sell order should not be null");
            TestAssert.IsTrue(loaded.resource == r, "Resource def survives the round trip");
            TestAssert.AreEqual(37.5, loaded.amountPerPeriod, 0.001, "amountPerPeriod survives the round trip");
        }

        [EmpireDestructiveTest("SC.Destructive.Serialization")]
        public static void NeedState_RoundTrips()
        {
            ResourceTypeDef r = SupplyChainCache.AllResourceTypeDefs.FirstOrDefault();
            if (r is null) TestAssert.Skip("No resource types defined");

            NeedState original = new NeedState("need.test", r, 20.0, 8.0, "Test Need", NeedCategory.Base);
            NeedState loaded = ScribeRoundTrip(original, "needState");

            TestAssert.IsNotNull(loaded, "Round-tripped need state should not be null");
            TestAssert.AreEqual((object)"need.test", loaded.needId, "needId survives");
            TestAssert.IsTrue(loaded.resource == r, "resource def survives");
            TestAssert.AreEqual(20.0, loaded.demanded, 0.001, "demanded survives");
            TestAssert.AreEqual(8.0, loaded.fulfilled, 0.001, "fulfilled survives");
            TestAssert.IsTrue(loaded.category == NeedCategory.Base, "category survives");
        }

        /// <summary>
        /// Deep-saves <paramref name="obj"/> to a temp file and loads it back into a fresh instance.
        /// Only valid for IExposable types with no cross-references. Hard-resets Scribe and deletes the
        /// temp file afterwards so a failure can't leave global save state dirty.
        /// </summary>
        private static T ScribeRoundTrip<T>(T obj, string label) where T : IExposable, new()
        {
            string path = Path.Combine(GenFilePaths.TempFolderPath, "EmpireSC_scribe_" + label + ".xml");
            T loaded = default(T);
            try
            {
                Scribe.saver.InitSaving(path, "root");
                Scribe_Deep.Look(ref obj, label);
                Scribe.saver.FinalizeSaving();

                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref loaded, label);
                Scribe.loader.FinalizeLoading();
            }
            finally
            {
                Scribe.ForceStop();
                try { if (File.Exists(path)) File.Delete(path); }
                catch { /* best-effort cleanup */ }
            }
            return loaded;
        }
    }
}
