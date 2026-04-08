using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    [StaticConstructorOnStartup]
    public static class RouteOverlayUtil
    {
        private const float LineAlt = 0.08f;
        private const float ArrowAlt = 0.09f;
        private const int MaxSegments = 25;
        private const int FlowArrowCount = 3;
        private const float FlowSpeed = 0.4f;

        // --- Lazy-init materials (must not load resources in static ctor) ---

        private static Material routeLineMat;
        public static Material RouteLineMat
        {
            get
            {
                if (routeLineMat is null)
                    routeLineMat = MaterialPool.MatFrom(
                        GenDraw.LineTexPath, ShaderDatabase.WorldOverlayTransparent,
                        new Color(0.4f, 1f, 0.4f, 1f), 3590);
                return routeLineMat;
            }
        }

        private static Material orbitalRouteLineMat;
        public static Material OrbitalRouteLineMat
        {
            get
            {
                if (orbitalRouteLineMat is null)
                    orbitalRouteLineMat = MaterialPool.MatFrom(
                        GenDraw.LineTexPath, ShaderDatabase.WorldOverlayTransparent,
                        new Color(0.6f, 1f, 0.6f, 1f), 3590);
                return orbitalRouteLineMat;
            }
        }

        private static Material routeArrowMat;
        public static Material RouteArrowMat
        {
            get
            {
                if (routeArrowMat is null)
                    routeArrowMat = MaterialPool.MatFrom(
                        "UI/Widgets/ArrowRight", ShaderDatabase.WorldOverlayTransparent,
                        new Color(0.2f, 0.8f, 0.2f, 0.9f), 3591);
                return routeArrowMat;
            }
        }

        // --- Drawing Methods ---

        /// <summary>
        /// Draw a line between two world positions that curves along the planet surface.
        /// Subdivides into short segments to prevent clipping through the globe.
        /// </summary>
        public static void DrawWorldLineOnSurface(Vector3 a, Vector3 b, Material mat, float widthFactor, float sphereRadius)
        {
            float dist = Vector3.Distance(a, b);
            if (dist < 0.001f) return;

            float tileSize = Find.WorldGrid.AverageTileSize;
            int segments = Mathf.Min(Mathf.Max(Mathf.RoundToInt(dist / (tileSize * 2f)), 1), MaxSegments);

            for (int i = 0; i < segments; i++)
            {
                Vector3 from = Vector3.Lerp(a, b, (float)i / segments);
                Vector3 to = Vector3.Lerp(a, b, (float)(i + 1) / segments);
                from = from.normalized * (sphereRadius + LineAlt);
                to = to.normalized * (sphereRadius + LineAlt);
                GenDraw.DrawWorldLineBetween(from, to, mat, widthFactor);
            }
        }

        /// <summary>
        /// Draw a direction arrow at 60% along the route (closer to destination).
        /// </summary>
        public static void DrawDirectionArrow(Vector3 source, Vector3 dest, float sphereRadius, float sizeMultiplier = 0.6f)
        {
            Vector3 arrowPos = Vector3.Lerp(source, dest, 0.6f);
            arrowPos = arrowPos.normalized * (sphereRadius + ArrowAlt);

            Vector3 normal = arrowPos.normalized;
            Vector3 defaultFwd = Vector3.Cross(normal, Vector3.up).normalized;
            if (defaultFwd.sqrMagnitude < 0.01f)
                defaultFwd = Vector3.Cross(normal, Vector3.right).normalized;

            WorldRendererUtility.GetTangentialVectorFacing(arrowPos, dest, out Vector3 desiredFwd, out Vector3 _);
            float angle = Vector3.SignedAngle(defaultFwd, desiredFwd, normal) - 90f;

            float size = Find.WorldGrid.AverageTileSize * sizeMultiplier;
            WorldRendererUtility.DrawQuadTangentialToPlanet(arrowPos, size, 0.01f, RouteArrowMat, angle);
        }

        /// <summary>
        /// Draw multiple arrows flowing from source to destination.
        /// </summary>
        public static void DrawFlowArrows(Vector3 source, Vector3 dest, float sphereRadius, float sizeMultiplier = 0.5f)
        {
            float time = Time.time * FlowSpeed;
            float arrowSize = Find.WorldGrid.AverageTileSize * sizeMultiplier;

            for (int i = 0; i < FlowArrowCount; i++)
            {
                float t = (time + (float)i / FlowArrowCount) % 1.0f;
                float tClamped = 0.1f + t * 0.8f;

                Vector3 arrowPos = Vector3.Lerp(source, dest, tClamped);
                arrowPos = arrowPos.normalized * (sphereRadius + ArrowAlt);

                Vector3 normal = arrowPos.normalized;
                Vector3 defaultFwd = Vector3.Cross(normal, Vector3.up).normalized;
                if (defaultFwd.sqrMagnitude < 0.01f)
                    defaultFwd = Vector3.Cross(normal, Vector3.right).normalized;

                WorldRendererUtility.GetTangentialVectorFacing(arrowPos, dest, out Vector3 fwd, out Vector3 _);
                float angle = Vector3.SignedAngle(defaultFwd, fwd, normal) - 90f;

                WorldRendererUtility.DrawQuadTangentialToPlanet(arrowPos, arrowSize, 0.01f, RouteArrowMat, angle);
            }
        }

        /// <summary>
        /// Draw a complete route: surface-following line + direction arrow(s).
        /// Culls routes where both endpoints are behind the globe.
        /// </summary>
        public static void DrawRoute(SupplyRoute route, WorldGrid grid)
        {
            if (route.source.Tile.Layer != route.destination.Tile.Layer) return;
            if (route.source.Tile.Layer != PlanetLayer.Selected) return;

            Vector3 posA = grid.GetTileCenter(route.source.Tile);
            Vector3 posB = grid.GetTileCenter(route.destination.Tile);

            // Cull routes entirely behind the globe
            if (!IsVisibleToCamera(posA) && !IsVisibleToCamera(posB)) return;

            bool isOrbital = route.source.Tile.Layer != Find.WorldGrid.PlanetLayers[0];
            Material mat = isOrbital ? OrbitalRouteLineMat : RouteLineMat;
            float width = isOrbital ? 10.0f : 1.2f;

            float sphereRadius = posA.magnitude;
            DrawWorldLineOnSurface(posA, posB, mat, width, sphereRadius);

            if (SupplyChainSettings.animateRouteArrows)
                DrawFlowArrows(posA, posB, sphereRadius, isOrbital ? 2.5f : 0.5f);
            else
                DrawDirectionArrow(posA, posB, sphereRadius, isOrbital ? 3f : 0.6f);
        }

        /// <summary>
        /// Draw a GUI text label for a route at its arrow position (call during OnGUI).
        /// Label text is provided by the caller (pre-built combined label per settlement pair).
        /// </summary>
        public static bool DrawRouteLabel(SupplyRoute route, WorldGrid grid, string label)
        {
            if (label is null) return false;
            if (route.source.Tile.Layer != route.destination.Tile.Layer) return false;
            if (route.source.Tile.Layer != PlanetLayer.Selected) return false;

            Vector3 posA = grid.GetTileCenter(route.source.Tile);
            Vector3 mid = Vector3.Lerp(posA,
                grid.GetTileCenter(route.destination.Tile), 0.6f);
            mid = mid.normalized * posA.magnitude;

            if (!IsVisibleToCamera(mid)) return false;

            Vector2 screenPos = GenWorldUI.WorldToUIPosition(mid);
            Rect screen = new Rect(0f, 0f, UI.screenWidth, UI.screenHeight);
            if (!screen.Contains(screenPos)) return false;

            Vector2 labelSize = Text.CalcSize(label);
            Rect labelRect = new Rect(
                screenPos.x - labelSize.x / 2f,
                screenPos.y - labelSize.y / 2f,
                labelSize.x, labelSize.y);

            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            Widgets.Label(labelRect, label);
            return true;
        }

        /// <summary>
        /// Check if a world position is on the visible side of the globe.
        /// </summary>
        public static bool IsVisibleToCamera(Vector3 worldPos)
        {
            Vector3 camFwd = Find.WorldCamera.transform.forward;
            return Vector3.Dot(worldPos.normalized, -camFwd) >= 0f;
        }

        /// <summary>
        /// Returns true if labels should be rendered.
        /// Per-route layer filtering is handled by DrawRouteLabel.
        /// </summary>
        public static bool ShouldDrawLabels()
        {
            return true;
        }
    }
}
