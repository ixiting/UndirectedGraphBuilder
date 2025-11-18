using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;

namespace UndirectedGraphBuilder.CadCommands.Services {
    internal static class VertexCommands {
        internal static void CreateOrSelectVertex() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var actionOpts = new PromptKeywordOptions("\nВыберите действие [Новая_вершина(N)/Выбрать_существующую(S)]: ");
            actionOpts.Keywords.Add("N");
            actionOpts.Keywords.Add("S");
            actionOpts.AllowNone = false;

            var actionResult = ed.GetKeywords(actionOpts);
            if (actionResult.Status != PromptStatus.OK) return;

            if (actionResult.StringResult == "S") {
                var selectOpts = new PromptEntityOptions("\nВыберите существующую вершину: ");
                selectOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
                selectOpts.AddAllowedClass(typeof(Circle), false);
                selectOpts.AddAllowedClass(typeof(Polyline), false);
                var selectResult = ed.GetEntity(selectOpts);
                if (selectResult.Status != PromptStatus.OK) return;

                Commands._currentVertices.Add(selectResult.ObjectId);
                return;
            }

            var styleOpts = new PromptKeywordOptions("\nВыберите стиль вершины [Синий круг/Красный треугольник]: ");
            styleOpts.Keywords.Add("Синий круг");
            styleOpts.Keywords.Add("Красный треугольник");
            styleOpts.AllowNone = false;
            var styleResult = ed.GetKeywords(styleOpts);
            if (styleResult.Status != PromptStatus.OK) return;

            var pointOpts = new PromptPointOptions("\nУкажите точку размещения вершины: ");
            var pointResult = ed.GetPoint(pointOpts);
            if (pointResult.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                bool isCircle = styleResult.StringResult == "Синий круг";
                string label = string.Empty;
                var vertexId = Commands.CreateVertexEntity(db, tr, pointResult.Value, isCircle, label);

                if (vertexId != ObjectId.Null) {
                    var ent = (Entity)tr.GetObject(vertexId, OpenMode.ForWrite);

                    try {
                        if (styleResult.StringResult == "Синий круг") ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                        else ent.Color = Teigha.Colors.Color.FromRgb(255, 0, 0);
                    } catch { }

                    Commands._currentVertices.Add(vertexId);
                    var dto = new CadObjects.GraphVertex(pointResult.Value, !isCircle) {
                        DrawingObjectId = vertexId,
                        Label = label
                    };
                    Commands._vertexModel[vertexId] = dto;

                    Commands.AttachLabelXData(tr, db, vertexId, label);

                    if (Commands._currentVertices.Count > 1) {
                        while (true) {
                            var connectOpts = new PromptKeywordOptions("\nСоединить с существующей вершиной? [Да(Y)/Нет(N)]: ");
                            connectOpts.Keywords.Add("Y");
                            connectOpts.Keywords.Add("N");
                            connectOpts.AllowNone = false;

                            var connectResult = ed.GetKeywords(connectOpts);
                            if (connectResult.Status != PromptStatus.OK || connectResult.StringResult == "N")
                                break;

                            var targetOpts = new PromptEntityOptions("\nВыберите вершину для соединения: ");
                            targetOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
                            targetOpts.AddAllowedClass(typeof(Circle), false);
                            targetOpts.AddAllowedClass(typeof(Polyline), false);
                            var targetResult = ed.GetEntity(targetOpts);

                            if (targetResult.Status == PromptStatus.OK && targetResult.ObjectId != vertexId) {
                                if (Commands._vertexModel.ContainsKey(targetResult.ObjectId)) {
                                    Commands.CreateEdgeEntity(db, tr, vertexId, targetResult.ObjectId);
                                } else {
                                    var existingEdge = Commands._edgeModel.FirstOrDefault(e => e.DrawingObjectId == targetResult.ObjectId);
                                    if (existingEdge != null) {
                                        var peo = new PromptPointOptions("\nУкажите точку на ребре для разделения: ");
                                        var pRes = ed.GetPoint(peo);
                                        if (pRes.Status == PromptStatus.OK) {
                                            Commands.SplitEdgeAtPoint(db, tr, existingEdge, pRes.Value, vertexId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                tr.Commit();
            }
        }
    }
}
