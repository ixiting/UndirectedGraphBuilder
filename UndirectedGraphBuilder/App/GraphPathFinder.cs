using UndirectedGraphBuilder.App.Models;

namespace UndirectedGraphBuilder.App {

    public class GraphPathFinder {

        private Dictionary<long, GraphVertexDto> _vertices;
        private Dictionary<long, List<(long neighborId, double weight)>> _connections;

        public void Initialize(List<GraphVertexDto> vertices, List<GraphEdgeDto> edges) {
            _vertices = vertices.ToDictionary(v => v.Id);
            _connections = new Dictionary<long, List<(long, double)>>();

            foreach (var edge in edges) {
                if (!_vertices.ContainsKey(edge.StartVertexId) || !_vertices.ContainsKey(edge.EndVertexId))
                    continue;

                AddConnection(edge.StartVertexId, edge.EndVertexId, edge.Length);
                AddConnection(edge.EndVertexId, edge.StartVertexId, edge.Length);
            }
        }

        public List<long> FindShortestPath(long startId, long endId) {
            ValidateVerticesExist(startId, endId);

            var distances = new Dictionary<long, double>();
            var previous = new Dictionary<long, long>();
            var unvisited = new HashSet<long>();

            foreach (var vertexId in _vertices.Keys) {
                distances[vertexId] = double.MaxValue;
                previous[vertexId] = -1;
                unvisited.Add(vertexId);
            }

            distances[startId] = 0;

            while (unvisited.Count > 0) {
                var currentId = GetClosestUnvisitedVertex(unvisited, distances);
                if (currentId == -1) break;

                unvisited.Remove(currentId);

                if (currentId == endId) break;

                UpdateNeighborDistances(currentId, distances, previous, unvisited);
            }

            return BuildPath(previous, endId);
        }

        public bool VertexExists(long vertexId) => _vertices.ContainsKey(vertexId);

        #region Private Methods

        private void AddConnection(long fromId, long toId, double weight) {
            if (!_connections.ContainsKey(fromId))
                _connections[fromId] = new List<(long, double)>();

            _connections[fromId].Add((toId, weight));
        }

        private void ValidateVerticesExist(long startId, long endId) {
            if (!VertexExists(startId))
                throw new ArgumentException($"Вершина {startId} не найдена в графе");
            if (!VertexExists(endId))
                throw new ArgumentException($"Вершина {endId} не найдена в графе");
        }

        private long GetClosestUnvisitedVertex(HashSet<long> unvisited, Dictionary<long, double> distances) {
            var minDistance = double.MaxValue;
            var closestId = -1L;

            foreach (var id in unvisited) {
                if (distances[id] < minDistance) {
                    minDistance = distances[id];
                    closestId = id;
                }
            }

            return closestId;
        }

        private void UpdateNeighborDistances(long currentId, Dictionary<long, double> distances,
            Dictionary<long, long> previous, HashSet<long> unvisited) {
            if (!_connections.ContainsKey(currentId))
                return;

            foreach (var (neighborId, weight) in _connections[currentId]) {
                if (!unvisited.Contains(neighborId))
                    continue;

                var tentativeDistance = distances[currentId] + weight;
                if (tentativeDistance < distances[neighborId]) {
                    distances[neighborId] = tentativeDistance;
                    previous[neighborId] = currentId;
                }
            }
        }

        private List<long> BuildPath(Dictionary<long, long> previous, long endId) {
            var path = new List<long>();
            var currentId = endId;

            while (currentId != -1) {
                path.Add(currentId);
                currentId = previous[currentId];
            }

            path.Reverse();
            return path;
        }

        #endregion

    }

}