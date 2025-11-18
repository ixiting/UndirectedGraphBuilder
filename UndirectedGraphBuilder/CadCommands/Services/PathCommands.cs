using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;

using UndirectedGraphBuilder.App;
using UndirectedGraphBuilder.App.Models;
using UndirectedGraphBuilder.Utils;

namespace UndirectedGraphBuilder.CadCommands.Services {
    internal static class PathCommands {
        internal static void FindAndHighlightPath() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var startOpts = new PromptEntityOptions("\nВыберите начальную вершину: ");
            startOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            startOpts.AddAllowedClass(typeof(Circle), false);
            startOpts.AddAllowedClass(typeof(Polyline), false);
            var startResult = ed.GetEntity(startOpts);
            if (startResult.Status != PromptStatus.OK) return;

            var endOpts = new PromptEntityOptions("\nВыберите конечную вершину: ");
            endOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            endOpts.AddAllowedClass(typeof(Circle), false);
            endOpts.AddAllowedClass(typeof(Polyline), false);
            var endResult = ed.GetEntity(endOpts);
            if (endResult.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                Commands.ClearPathHighlight(tr);

                var vertices = new List<GraphVertexDto>();
                var edges = new List<GraphEdgeDto>();
                long vertexIdCounter = 1;
                var vertexIdMap = new Dictionary<ObjectId, long>();

                foreach (var vertex in Commands._vertexModel) {
                    vertexIdMap[vertex.Key] = vertexIdCounter;
                    vertices.Add(new GraphVertexDto(vertexIdCounter,
                        vertex.Value.Center.X,
                        vertex.Value.Center.Y,
                        vertex.Value.Center.Z));
                    vertexIdCounter++;
                }

                long edgeIdCounter = 1;
                foreach (var edge in Commands._edgeModel) {
                    if (!vertexIdMap.ContainsKey(edge.StartVertexId) ||
                        !vertexIdMap.ContainsKey(edge.EndVertexId))
                        continue;

                    var startVertex = Commands._vertexModel[edge.StartVertexId];
                    var endVertex = Commands._vertexModel[edge.EndVertexId];
                    var length = GeometryUtils.CalculateDistance(startVertex.Center, endVertex.Center);

                    edges.Add(new GraphEdgeDto(
                        edgeIdCounter++,
                        vertexIdMap[edge.StartVertexId],
                        vertexIdMap[edge.EndVertexId],
                        length));
                }

                var pathFinder = new GraphPathFinder();
                pathFinder.Initialize(vertices, edges);

                try {
                    var path = pathFinder.FindShortestPath(
                        vertexIdMap[startResult.ObjectId],
                        vertexIdMap[endResult.ObjectId]);

                    if (path != null && path.Count > 1) {
                        for (int i = 0; i < path.Count - 1; i++) {
                            var currentVertexId = vertexIdMap.FirstOrDefault(x => x.Value == path[i]).Key;
                            var nextVertexId = vertexIdMap.FirstOrDefault(x => x.Value == path[i + 1]).Key;

                            var edge = Commands._edgeModel.FirstOrDefault(e =>
                                (e.StartVertexId == currentVertexId && e.EndVertexId == nextVertexId) ||
                                (e.StartVertexId == nextVertexId && e.EndVertexId == currentVertexId));

                            if (edge != null) {
                                Commands.HighlightEntity(tr, edge.DrawingObjectId);
                                Commands._highlightedPath.Add(edge.DrawingObjectId);
                            }
                        }

                        ed.WriteMessage("\nПуть найден и выделен.");
                    } else {
                        ed.WriteMessage("\nПуть между вершинами не найден.");
                    }
                } catch (System.Exception ex) {
                    ed.WriteMessage($"\nОшибка при поиске пути: {ex.Message}");
                }

                tr.Commit();
            }
        }
    }
}
