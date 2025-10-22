using Teigha.Geometry;
using Teigha.DatabaseServices;

namespace UndirectedGraphBuilder.CadObjects {

    public class GraphEdge {

        public ObjectId DrawingObjectId { get; set; } = ObjectId.Null;

        public ObjectId StartVertexId { get; private set; } = ObjectId.Null;

        public ObjectId EndVertexId { get; private set; } = ObjectId.Null;

        public List<Point3d> IntermediatePoints { get; } = new();

        public GraphEdge() { }

        public void SetVertices(ObjectId startVertexId, ObjectId endVertexId) {
            StartVertexId = startVertexId;
            EndVertexId = endVertexId;
        }

        public void AddIntermediatePoint(Point3d point) {
            IntermediatePoints.Add(point);
        }

    }

}