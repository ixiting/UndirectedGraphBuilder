namespace UndirectedGraphBuilder.App.Models {

    public class GraphEdgeDto {

        public long Id { get; set; }

        public long StartVertexId { get; set; }

        public long EndVertexId { get; set; }

        public double Length { get; set; }

        public GraphEdgeDto(long id, long startVertexId, long endVertexId, double length) {
            Id = id;
            StartVertexId = startVertexId;
            EndVertexId = endVertexId;
            Length = length;
        }

    }

}