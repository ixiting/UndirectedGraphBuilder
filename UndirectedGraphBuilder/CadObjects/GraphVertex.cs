using Teigha.Geometry;
using Teigha.DatabaseServices;

namespace UndirectedGraphBuilder.CadObjects {

    public class GraphVertex {

        public ObjectId DrawingObjectId { get; set; } = ObjectId.Null;

        public Point3d Center { get; set; }

        public string Uid { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public bool IsTriangle { get; set; }

        public List<ObjectId> AttachedEdgeIds { get; } = new();

        public bool IsErased { get; set; }

        public GraphVertex(Point3d center, bool isTriangle = false) {
            Center = center;
            IsTriangle = isTriangle;
        }

        public void AddAttachedEdge(ObjectId edgeId) {
            if (edgeId == ObjectId.Null) return;
            if (!AttachedEdgeIds.Contains(edgeId)) AttachedEdgeIds.Add(edgeId);
        }

        public void RemoveAttachedEdge(ObjectId edgeId) {
            AttachedEdgeIds.Remove(edgeId);
        }

    }

}