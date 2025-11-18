namespace UndirectedGraphBuilder.App.Models {

    public class GraphVertexDto {

        public long Id { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public GraphVertexDto(long id, double x, double y, double z = 0) {
            Id = id;
            X = x;
            Y = y;
            Z = z;
        }

        public override int GetHashCode() {
            return Id.GetHashCode();
        }

    }

}