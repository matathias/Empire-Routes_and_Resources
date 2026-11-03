using System;
using System.Collections.Generic;
using System.Threading;
using RimWorld.Planet;
using Verse;

namespace FactionColonies.SupplyChain
{
    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    /* Recomputes supply-route travel times / paths (the expensive A* world pathfind) OFF the main    */
    /* thread, mirroring the base mod's FCRoadQueue: a generation-numbered background Thread computes  */
    /* results from an immutable snapshot and publishes them for the main thread to merge on its next  */
    /* tick. Safe because WorldPathPool access is globally locked (WorldPathPoolPatches) and the       */
    /* worker only READS world state and returns plain data — all route mutation happens on the main  */
    /* thread in the merge. A user setting forces synchronous (main-thread) computation instead.       */
    /*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*-*/
    public class SupplyRouteWarmer
    {
        private struct Job
        {
            public SupplyRoute route;
            public PlanetTile from;
            public PlanetTile to;
        }

        private struct Result
        {
            public int travelTicks;
            public List<PlanetTile> path;
        }

        // currentGeneration advances on every kick and on every world-change Invalidate(); a worker's
        // results are discarded unless its captured generation still matches when it finishes.
        private volatile int currentGeneration;
        private volatile int completedGeneration = -1;
        private volatile bool workerActive;

        // Written by the worker, read/cleared by the main thread. Guarded by the volatile
        // completedGeneration (release on write / acquire on read), same as FCRoadQueue's pendingNewEdges.
        private Dictionary<SupplyRoute, Result> pendingResults;

        // Log messages the worker wants surfaced. The worker NEVER calls the logger itself (off-thread Log
        // calls crash the dev log window / kill the thread); it buffers here and the main thread flushes.
        private List<string> pendingLog;

        /// <summary>Discard any in-flight computation — call after a world change (roads/research) re-dirties routes.</summary>
        public void Invalidate()
        {
            currentGeneration++;
        }

        /// <summary>
        /// Main thread, once per WorldComponentTick: merge any finished results, then (if idle) snapshot the
        /// path-dirty routes and start a new generation — on a background thread, or synchronously if the
        /// user disabled threading.
        /// </summary>
        public void Tick(List<SupplyRoute> routes)
        {
            MergeCompleted();

            if (workerActive || routes == null) return;

            List<Job> jobs = null;
            for (int i = 0; i < routes.Count; i++)
            {
                SupplyRoute r = routes[i];
                if (r.PathReady || !r.IsValid()) continue;
                if (jobs is null) jobs = new List<Job>();
                jobs.Add(new Job { route = r, from = r.source.Tile, to = r.destination.Tile });
            }
            if (jobs is null) return;

            int gen = ++currentGeneration;
            workerActive = true;

            if (SupplyChainSettings.useThreadedRouteComputation)
            {
                Thread thread = new Thread(() => Compute(jobs, gen)) { IsBackground = true };
                thread.Start();
            }
            else
            {
                Compute(jobs, gen);
                MergeCompleted();
            }
        }

        private void Compute(List<Job> jobs, int gen)
        {
            // Silence the base mod's incidental logging (e.g. TravelUtil's verbose messages) on THIS thread,
            // so nothing this worker calls touches Verse.Log off the main thread. Thread-static; restored below.
            bool prevSuppress = LogUtil.SuppressOnThisThread;
            LogUtil.SuppressOnThisThread = true;
            try
            {
                Dictionary<SupplyRoute, Result> results = new Dictionary<SupplyRoute, Result>(jobs.Count);
                List<string> log = null;

                try
                {
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        // Bail if superseded by a newer generation or the game is being torn down (publish nothing).
                        if (gen != currentGeneration || Current.Game is null) return;

                        Job job = jobs[i];
                        try
                        {
                            List<PlanetTile> path;
                            int ticks = TravelUtil.ReturnTicksToArrive(job.from, job.to, out path);
                            results[job.route] = new Result { travelTicks = ticks, path = path };
                        }
                        catch (Exception e)
                        {
                            // Leave this route dirty so it retries; buffer the message (no logger call here).
                            if (log is null) log = new List<string>();
                            log.Add($"SupplyRouteWarmer pathfind threw (route left dirty): {e}");
                        }
                    }
                }
                catch (Exception e)
                {
                    // Backstop: the worker must never die from an unhandled exception. Buffer and still
                    // publish whatever was computed so warming isn't permanently stalled.
                    if (log is null) log = new List<string>();
                    log.Add($"SupplyRouteWarmer background computation failed: {e}");
                }

                if (gen == currentGeneration)
                {
                    pendingResults = results;      // non-volatile writes...
                    pendingLog = log;
                    completedGeneration = gen;     // ...published by this volatile write (release)
                }
            }
            finally
            {
                LogUtil.SuppressOnThisThread = prevSuppress;
                workerActive = false;
            }
        }

        private void MergeCompleted()
        {
            if (completedGeneration != currentGeneration) return;  // nothing fresh, or superseded
            Dictionary<SupplyRoute, Result> results = pendingResults;
            List<string> log = pendingLog;
            if (results is null) return;
            pendingResults = null;
            pendingLog = null;

            foreach (KeyValuePair<SupplyRoute, Result> kv in results)
                kv.Key.ApplyPathResult(kv.Value.travelTicks, kv.Value.path);

            // Flush any buffered worker messages here — on the main thread, where logging is safe.
            if (log != null)
            {
                foreach (string msg in log)
                    LogSC.Error(msg);
            }
        }
    }
}
