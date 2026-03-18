using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace FactionColonies.SupplyChain
{
    public static class RouteOverlayUtil
    {
        private const float PlanetRadius = 100f;
        private const float LineAlt = 0.08f;
        private const float ArrowAlt = 0.09f;

        // --- Lazy-init materials (must not load resources in static ctor) ---

        private static Material routeLineMat;
        public static Material RouteLineMat
        {
            get
            {
                if (routeLineMat == null)
                    routeLineMat = MaterialPool.MatFrom(
                        GenDraw.LineTexPath, ShaderDatabase.WorldOverlayTransparent,
                        new Color(0.2f, 0.8f, 0.2f, 0.7f), 3590);
                return routeLineMat;
            }
        }

        private static Material routeArrowMat;
        public static Material RouteArrowMat
        {
            get
            {
                if (routeArrowMat == null)
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
        /// Pattern from DebugWorldLine.Draw().
        /// </summary>
        public static void DrawWorldLineOnSurface(Vector3 a, Vector3 b, Material mat, float widthFactor)
        {
            float dist = Vector3.Distance(a, b);
            if (dist < 0.001f) return;

            float tileSize = Find.WorldGrid.AverageTileSize;
            int segments = Mathf.Max(Mathf.RoundToInt(dist / tileSize), 1);

            for (int i = 0; i < segments; i++)
            {
                Vector3 from = Vector3.Lerp(a, b, (float)i / segments);
                Vector3 to = Vector3.Lerp(a, b, (float)(i + 1) / segments);
                from = from.normalized * (PlanetRadius + LineAlt);
                to = to.normalized * (PlanetRadius + LineAlt);
                GenDraw.DrawWorldLineBetween(from, to, mat, widthFactor);
            }
        }

        /// <summary>
        /// Draw a direction arrow at 60% along the route (closer to destination).
        /// Uses DrawQuadTangentialToPlanet with rotation to face source→dest direction.
        /// </summary>
        public static void DrawDirectionArrow(Vector3 source, Vector3 dest)
        {
            Vector3 arrowPos = Vector3.Lerp(source, dest, 0.6f);
            arrowPos = arrowPos.normalized * (PlanetRadius + ArrowAlt);

            Vector3 normal = arrowPos.normalized;
            Vector3 defaultFwd = Vector3.Cross(normal, Vector3.up).normalized;
            if (defaultFwd.sqrMagnitude < 0.01f)
                defaultFwd = Vector3.Cross(normal, Vector3.right).normalized;

            WorldRendererUtility.GetTangentialVectorFacing(arrowPos, dest, out Vector3 desiredFwd, out Vector3 _);
            float angle = Vector3.SignedAngle(defaultFwd, desiredFwd, normal);

            float size = Find.WorldGrid.AverageTileSize * 0.6f;
            WorldRendererUtility.DrawQuadTangentialToPlanet(arrowPos, size, 0.01f, RouteArrowMat, angle);
        }

        /// <summary>
        /// Draw a complete route: surface-following line + direction arrow.
        /// </summary>
        public static void DrawRoute(SupplyRoute route, WorldGrid grid)
        {
            Vector3 posA = grid.GetTileCenter(route.source.Tile);
            Vector3 posB = grid.GetTileCenter(route.destination.Tile);

            DrawWorldLineOnSurface(posA, posB, RouteLineMat, 1.2f);
            DrawDirectionArrow(posA, posB);
        }

        /// <summary>
        /// Draw a GUI text label for a route at its midpoint (call during OnGUI).
        /// Returns false if the label is not visible (behind globe or off-screen).
        /// </summary>
        public static bool DrawRouteLabel(SupplyRoute route, WorldGrid grid)
        {
            Vector3 mid = Vector3.Lerp(
                grid.GetTileCenter(route.source.Tile),
                grid.GetTileCenter(route.destination.Tile), 0.4f);
            mid = mid.normalized * PlanetRadius;

            if (!IsVisibleToCamera(mid)) return false;

            Vector2 screenPos = GenWorldUI.WorldToUIPosition(mid);
            Rect screen = new Rect(0f, 0f, UI.screenWidth, UI.screenHeight);
            if (!screen.Contains(screenPos)) return false;

            string label = route.amountPerPeriod.ToString("F0") + " " + route.resource.label;
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
        /// Check if a world position is on the visible side of the globe (not behind the planet).
        /// </summary>
        public static bool IsVisibleToCamera(Vector3 worldPos)
        {
            Vector3 camFwd = Find.WorldCamera.transform.forward;
            return Vector3.Dot(worldPos.normalized, -camFwd) >= 0f;
        }
    }
}
