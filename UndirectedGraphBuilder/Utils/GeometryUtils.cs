using Teigha.Geometry;

namespace UndirectedGraphBuilder.Utils {

    public static class GeometryUtils {

        public const double VertexRadius = 200.0;

        public static (Point3d A, Point3d B, Point3d C) GetTrianglePointsByRadius(Point3d center, double radius) {
            const double angleA = Math.PI / 2;
            const double angleB = 7 * Math.PI / 6;
            const double angleC = 11 * Math.PI / 6;

            var a = new Point3d(
                center.X + radius * Math.Cos(angleA),
                center.Y + radius * Math.Sin(angleA),
                center.Z
            );

            var b = new Point3d(
                center.X + radius * Math.Cos(angleB),
                center.Y + radius * Math.Sin(angleB),
                center.Z
            );

            var c = new Point3d(
                center.X + radius * Math.Cos(angleC),
                center.Y + radius * Math.Sin(angleC),
                center.Z
            );

            return (a, b, c);
        }

        public static double CalculateDistance(Point3d point1, Point3d point2) {
            return point1.DistanceTo(point2);
        }

    }

}