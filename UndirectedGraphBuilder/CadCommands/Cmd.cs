using HostMgd.ApplicationServices;
using HostMgd.EditorInput;

using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;

using UndirectedGraphBuilder.App;
using UndirectedGraphBuilder.App.Models;
using UndirectedGraphBuilder.Utils;

using Application = HostMgd.ApplicationServices.Application;

namespace UndirectedGraphBuilder.CadCommands {

    public class Commands {

        private const string APP_NAME = "UGB_APP";
        private const string GRAPH_APP = "UGB_GRAPH";
        private const string VERTEX_APP = "UGB_VERTEX";
        private const string EDGE_APP = "UGB_EDGE";


        private static List<ObjectId> _currentVertices = new();
        private static ObjectId _lastVertex = ObjectId.Null;
        private static ObjectId _lastHighlightedEntity = ObjectId.Null;
        private static bool _isBuilding;

        private static Dictionary<ObjectId, CadObjects.GraphVertex> _vertexModel = new();
        private static List<CadObjects.GraphEdge> _edgeModel = new();

        static Commands() {
            var dm = Application.DocumentManager;
            dm.DocumentCreated += OnDocumentCreated;
            dm.DocumentToBeActivated += OnDocumentToBeActivated;

            try {
                var active = Application.DocumentManager.MdiActiveDocument;
                if (active != null) {
                    var ed = active.Editor;
                    ed.WriteMessage("\n[UGB] Plugin loaded, initializing...\n");


                    using (var tr = active.Database.TransactionManager.StartTransaction()) {
                        RegisterApplications(tr, active.Database);
                        tr.Commit();
                    }

                    SubscribeToDatabaseEvents(active.Database);

                    RestoreGraphFromDrawing(active);

                    ed.WriteMessage($"\n[UGB] Initialized: {_vertexModel.Count} vertices, {_edgeModel.Count} edges\n");
                }
            } catch (System.Exception ex) {
                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[UGB] Init error: {ex.Message}\n");
            }
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) {
            RestoreGraphFromDrawing(e.Document);
        }

        private static void OnDocumentToBeActivated(object sender, DocumentCollectionEventArgs e) {
            RestoreGraphFromDrawing(e.Document);
        }

        private static List<ObjectId> _highlightedPath = new();

        private static System.Windows.Media.Color _pathHighlightColor = System.Windows.Media.Color.FromRgb(255, 165, 0);


        [CommandMethod("UGB_VERTEX")]
        public static void CreateVertexCommand() {
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

                _currentVertices.Add(selectResult.ObjectId);
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
                var vertexId = CreateVertexEntity(db, tr, pointResult.Value, isCircle, label);

                if (vertexId != ObjectId.Null) {
                    var ent = (Entity)tr.GetObject(vertexId, OpenMode.ForWrite);

                    try {
                        if (styleResult.StringResult == "Синий круг") ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                        else ent.Color = Teigha.Colors.Color.FromRgb(255, 0, 0);
                    } catch { }

                    _currentVertices.Add(vertexId);
                    var dto = new CadObjects.GraphVertex(pointResult.Value, !isCircle) {
                        DrawingObjectId = vertexId,
                        Label = label
                    };
                    _vertexModel[vertexId] = dto;

                    AttachLabelXData(tr, db, vertexId, label);

                    if (_currentVertices.Count > 1) {
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
                                if (_vertexModel.ContainsKey(targetResult.ObjectId)) {
                                    CreateEdgeEntity(db, tr, vertexId, targetResult.ObjectId);
                                } else {
                                    var existingEdge = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == targetResult.ObjectId);
                                    if (existingEdge != null) {
                                        var peo = new PromptPointOptions("\nУкажите точку на ребре для разделения: ");
                                        var pRes = ed.GetPoint(peo);
                                        if (pRes.Status == PromptStatus.OK) {
                                            SplitEdgeAtPoint(db, tr, existingEdge, pRes.Value, vertexId);
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

        [CommandMethod("UGB_BUILD")]
        public static void BuildGraphCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            _isBuilding = true;
            _currentVertices.Clear();
            _lastVertex = ObjectId.Null;

            ed.WriteMessage("\nНачало построения графа. ESC - завершить");

            while (_isBuilding) {
                var ppo = new PromptPointOptions("\nУкажите точку или выберите вершину [Отменить(U)]: ");
                ppo.Keywords.Add("U");
                var result = ed.GetPoint(ppo);

                if (result.Status == PromptStatus.Cancel) break;
                if (result.Status == PromptStatus.Keyword) {
                    if (result.StringResult == "U") UndoLastOperation();
                    continue;
                }

                if (result.Status == PromptStatus.OK) {
                    ProcessPoint(result.Value);
                }
            }

            CleanupBuildProcess();
        }

        [CommandMethod("UGB_EDIT")]
        public static void EditGraphCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var opts = new PromptKeywordOptions("\nВыберите действие [Добавить_вершину(A)/Изменить_цвет(C)/Изменить_форму(F)/Удалить(D)]: ");
            opts.Keywords.Add("A");
            opts.Keywords.Add("C");
            opts.Keywords.Add("F");
            opts.Keywords.Add("D");
            opts.AllowNone = false;

            var result = ed.GetKeywords(opts);
            if (result.Status != PromptStatus.OK) return;

            switch (result.StringResult) {
                case "A":
                    AddNewVertex();
                    break;
                case "C":
                    ChangeColor();
                    break;
                case "F":
                    ChangeShape();
                    break;
                case "D":
                    DeleteElement();
                    break;
            }
        }

        private static void AddNewVertex() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var typeOpts = new PromptKeywordOptions("\nВыберите тип вершины [Круг/Треугольник]: ");
            typeOpts.Keywords.Add("Круг");
            typeOpts.Keywords.Add("Треугольник");
            typeOpts.AllowNone = false;
            var typeResult = ed.GetKeywords(typeOpts);
            if (typeResult.Status != PromptStatus.OK) return;

            var pointOpts = new PromptPointOptions("\nУкажите точку размещения вершины: ");
            var pointResult = ed.GetPoint(pointOpts);
            if (pointResult.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                bool isCircle = typeResult.StringResult == "Круг";
                string label = string.Empty;
                var vertexId = CreateVertexEntity(db, tr, pointResult.Value, isCircle, label);

                if (vertexId != ObjectId.Null) {
                    _currentVertices.Add(vertexId);
                    var dto = new CadObjects.GraphVertex(pointResult.Value, !isCircle) {
                        DrawingObjectId = vertexId,
                        Label = label
                    };
                    _vertexModel[vertexId] = dto;
                    AttachLabelXData(tr, db, vertexId, label);


                    var connectOpts = new PromptKeywordOptions("\nСоединить с существующими вершинами? [Да/Нет]: ");
                    connectOpts.Keywords.Add("Да");
                    connectOpts.Keywords.Add("Нет");
                    connectOpts.AllowNone = false;

                    var connectResult = ed.GetKeywords(connectOpts);
                    if (connectResult.Status == PromptStatus.OK && connectResult.StringResult == "Y") {
                        while (true) {
                            var selOpts = new PromptEntityOptions("\nВыберите вершину для соединения (Enter для завершения): ");
                            selOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
                            selOpts.AddAllowedClass(typeof(Circle), false);
                            selOpts.AddAllowedClass(typeof(Polyline), false);
                            selOpts.AllowNone = true;

                            var selResult = ed.GetEntity(selOpts);
                            if (selResult.Status != PromptStatus.OK) break;

                            TempHighlight(selResult.ObjectId, tr);
                            var confirmOpts = new PromptKeywordOptions("\nПодтвердить соединение? [Да/Нет]: ");
                            confirmOpts.Keywords.Add("Да");
                            confirmOpts.Keywords.Add("Нет");
                            confirmOpts.AllowNone = false;
                            var confirmResult = ed.GetKeywords(confirmOpts);
                            if (confirmResult.Status == PromptStatus.OK && confirmResult.StringResult == "Y") {
                                var existingEdge = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == selResult.ObjectId);
                                if (existingEdge != null) {
                                    var peo = new PromptPointOptions("\nУкажите точку на ребре для разделения: ");
                                    var pRes = ed.GetPoint(peo);
                                    if (pRes.Status == PromptStatus.OK) {
                                        SplitEdgeAtPoint(db, tr, existingEdge, pRes.Value, vertexId);
                                    }
                                } else {
                                    CreateEdgeEntity(db, tr, vertexId, selResult.ObjectId);
                                }
                            }

                            ClearTempHighlight(tr);
                        }
                    }
                }

                tr.Commit();
            }
        }

        private static void ChangeColor() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var opts = new PromptEntityOptions("\nВыберите элемент для изменения цвета: ");
            opts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            opts.AddAllowedClass(typeof(Circle), false);
            opts.AddAllowedClass(typeof(Polyline), false);
            var result = ed.GetEntity(opts);
            if (result.Status != PromptStatus.OK) return;

            var colorOpts = new PromptIntegerOptions("\nВыберите цвет (1-Красный, 2-Зеленый, 3-Синий, 4-Желтый): ");
            colorOpts.AllowZero = false;
            colorOpts.AllowNegative = false;
            colorOpts.UseDefaultValue = false;
            var colorResult = ed.GetInteger(colorOpts);
            if (colorResult.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var ent = (Entity)tr.GetObject(result.ObjectId, OpenMode.ForWrite);
                switch (colorResult.Value) {
                    case 1:
                        ent.Color = Teigha.Colors.Color.FromRgb(255, 0, 0);
                        break;
                    case 2:
                        ent.Color = Teigha.Colors.Color.FromRgb(0, 255, 0);
                        break;
                    case 3:
                        ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                        break;
                    case 4:
                        ent.Color = Teigha.Colors.Color.FromRgb(255, 255, 0);
                        break;
                    default:
                        ent.ColorIndex = 256;
                        break;
                }

                tr.Commit();
            }
        }

        private static void ChangeShape() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var opts = new PromptEntityOptions("\nВыберите вершину для изменения формы: ");
            opts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            opts.AddAllowedClass(typeof(Circle), false);
            opts.AddAllowedClass(typeof(Polyline), false);
            var result = ed.GetEntity(opts);
            if (result.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var ent = tr.GetObject(result.ObjectId, OpenMode.ForRead);
                var center = ent is Circle circle ? circle.Center :
                    ent is Polyline poly ? new Point3d(poly.GetPoint2dAt(0).X, poly.GetPoint2dAt(0).Y, 0) : Point3d.Origin;

                var label = string.Empty;
                if (_vertexModel.TryGetValue(result.ObjectId, out var vertex)) {
                    label = vertex.Label;
                }

                ent.UpgradeOpen();
                ent.Erase();

                bool wasCircle = ent is Circle;
                var newId = CreateVertexEntity(db, tr, center, !wasCircle, label);

                if (_vertexModel.ContainsKey(result.ObjectId)) {
                    var dto = _vertexModel[result.ObjectId];
                    dto.DrawingObjectId = newId;
                    dto.IsTriangle = !wasCircle;
                    _vertexModel.Remove(result.ObjectId);
                    _vertexModel[newId] = dto;

                    var affectedEdges = _edgeModel.Where(e => e.StartVertexId == result.ObjectId || e.EndVertexId == result.ObjectId).ToList();
                    foreach (var edge in affectedEdges) {
                        if (edge.StartVertexId == result.ObjectId)
                            edge.SetVertices(newId, edge.EndVertexId);
                        if (edge.EndVertexId == result.ObjectId)
                            edge.SetVertices(edge.StartVertexId, newId);

                        UpdateEdgeGeometry(edge, tr);
                        try {
                            AttachEdgeXData(tr, db, edge.DrawingObjectId, edge.StartVertexId, edge.EndVertexId);
                        } catch { }
                    }
                }

                tr.Commit();
            }
        }

        private static void DeleteElement() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var opts = new PromptEntityOptions("\nВыберите элемент для удаления: ");
            opts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            opts.AddAllowedClass(typeof(Circle), false);
            opts.AddAllowedClass(typeof(Polyline), false);
            var result = ed.GetEntity(opts);
            if (result.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var ent = tr.GetObject(result.ObjectId, OpenMode.ForWrite);

                if (_vertexModel.ContainsKey(result.ObjectId)) {
                    var edgesToRemove = _edgeModel
                        .Where(e => e.StartVertexId == result.ObjectId || e.EndVertexId == result.ObjectId)
                        .ToList();

                    foreach (var edge in edgesToRemove) {
                        if (edge.DrawingObjectId != ObjectId.Null) {
                            var edgeEnt = tr.GetObject(edge.DrawingObjectId, OpenMode.ForWrite);
                            edgeEnt.Erase();
                        }

                        _edgeModel.Remove(edge);
                    }

                    _vertexModel.Remove(result.ObjectId);
                    _currentVertices.Remove(result.ObjectId);
                }
                else {
                    var edge = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == result.ObjectId);
                    if (edge != null) {
                        _edgeModel.Remove(edge);
                    }
                }

                ent.Erase();
                tr.Commit();
            }
        }

        [CommandMethod("UGB_PATH")]
        public static void FindPathCommand() {
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
                ClearPathHighlight(tr);

                var vertices = new List<GraphVertexDto>();
                var edges = new List<GraphEdgeDto>();
                long vertexIdCounter = 1;
                var vertexIdMap = new Dictionary<ObjectId, long>();

                foreach (var vertex in _vertexModel) {
                    vertexIdMap[vertex.Key] = vertexIdCounter;
                    vertices.Add(new GraphVertexDto(vertexIdCounter,
                        vertex.Value.Center.X,
                        vertex.Value.Center.Y,
                        vertex.Value.Center.Z));
                    vertexIdCounter++;
                }

                long edgeIdCounter = 1;
                foreach (var edge in _edgeModel) {
                    if (!vertexIdMap.ContainsKey(edge.StartVertexId) ||
                        !vertexIdMap.ContainsKey(edge.EndVertexId))
                        continue;

                    var startVertex = _vertexModel[edge.StartVertexId];
                    var endVertex = _vertexModel[edge.EndVertexId];
                    var length = GeometryUtils.CalculateDistance(startVertex.Center, endVertex.Center);

                    edges.Add(new GraphEdgeDto(
                        edgeIdCounter++,
                        vertexIdMap[edge.StartVertexId],
                        vertexIdMap[edge.EndVertexId],
                        length));
                }

                // Find path
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

                            var edge = _edgeModel.FirstOrDefault(e =>
                                (e.StartVertexId == currentVertexId && e.EndVertexId == nextVertexId) ||
                                (e.StartVertexId == nextVertexId && e.EndVertexId == currentVertexId));

                            if (edge != null) {
                                HighlightEntity(tr, edge.DrawingObjectId);
                                _highlightedPath.Add(edge.DrawingObjectId);
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

        [CommandMethod("UGB_REFRESH")]
        public static void RefreshGraphCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            using (var tr = db.TransactionManager.StartTransaction()) {
                int count = 0;
                foreach (var edge in _edgeModel.ToList()) {
                    try {
                        UpdateEdgeGeometry(edge, tr);
                        count++;
                    } catch { }
                }

                ed.WriteMessage($"\n[UGB] Refreshed {count} edges.\n");
                tr.Commit();
            }
        }

        [CommandMethod("UGB_DUMPSTATE")]
        public static void DumpStateCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            ed.WriteMessage("\n[UGB] Dumping in-memory graph state:\n");
            ed.WriteMessage($"Vertices count: {_vertexModel.Count}\n");
            foreach (var kv in _vertexModel) {
                var id = kv.Key;
                var v = kv.Value;
                ed.WriteMessage($"Vertex: ObjId={id}, UID={v.Uid}, Center=({v.Center.X},{v.Center.Y}), AttachedEdges={v.AttachedEdgeIds.Count}\n");
            }

            ed.WriteMessage($"Edges count: {_edgeModel.Count}\n");
            foreach (var e in _edgeModel) {
                ed.WriteMessage($"Edge: ObjId={e.DrawingObjectId}, Start={e.StartVertexId}, End={e.EndVertexId}, Intermediate={e.IntermediatePoints.Count}\n");
            }
        }

        private static void ProcessPoint(Point3d point) {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var selectOpts = new PromptKeywordOptions("\nВыберите действие [Новая_вершина(N)/Выбрать_существующую(S)]: ");
                selectOpts.Keywords.Add("N");
                selectOpts.Keywords.Add("S");
                selectOpts.AllowNone = false;
                var selectResult = ed.GetKeywords(selectOpts);
                if (selectResult.Status != PromptStatus.OK) return;

                ObjectId vertexId;
                if (selectResult.StringResult == "S") {
                    var options = new PromptEntityOptions("\nВыберите существующую вершину: ");
                    options.SetRejectMessage("\nВыбранный объект не поддерживается.");
                    options.AddAllowedClass(typeof(Circle), false);
                    options.AddAllowedClass(typeof(Polyline), false);

                    var entResult = ed.GetEntity(options);
                    if (entResult.Status != PromptStatus.OK) return;
                    vertexId = entResult.ObjectId;
                } else {
                    var styleOpts = new PromptKeywordOptions("\nВыберите стиль вершины [Синий круг/Красный треугольник]: ");
                    styleOpts.Keywords.Add("Синий круг");
                    styleOpts.Keywords.Add("Красный треугольник");
                    styleOpts.AllowNone = false;
                    var styleResult = ed.GetKeywords(styleOpts);
                    if (styleResult.Status != PromptStatus.OK) return;

                    bool isCircle = styleResult.StringResult == "Синий круг";
                    string label = string.Empty;
                    vertexId = CreateVertexEntity(db, tr, point, isCircle, label);

                    if (vertexId != ObjectId.Null) {
                        var ent = (Entity)tr.GetObject(vertexId, OpenMode.ForWrite);
                        try {
                            if (isCircle) ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                            else ent.Color = Teigha.Colors.Color.FromRgb(255, 0, 0);
                        } catch { }

                        _currentVertices.Add(vertexId);
                        var dto = new CadObjects.GraphVertex(point, !isCircle) {
                            DrawingObjectId = vertexId,
                            Label = label
                        };
                        _vertexModel[vertexId] = dto;
                        AttachLabelXData(tr, db, vertexId, label);
                    }
                }

                if (vertexId != ObjectId.Null) {
                    while (true) {
                        var connectOpts = new PromptKeywordOptions("\nСоединить с вершиной? [Да/Нет]: ");
                        connectOpts.Keywords.Add("Да");
                        connectOpts.Keywords.Add("Нет");
                        connectOpts.AllowNone = false;

                        var connectResult = ed.GetKeywords(connectOpts);
                        if (connectResult.Status != PromptStatus.OK || connectResult.StringResult == "Нет")
                            break;

                        var targetOpts = new PromptEntityOptions("\nВыберите вершину для соединения: ");
                        targetOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
                        targetOpts.AddAllowedClass(typeof(Circle), false);
                        targetOpts.AddAllowedClass(typeof(Polyline), false);
                        var targetResult = ed.GetEntity(targetOpts);

                        if (targetResult.Status == PromptStatus.OK && targetResult.ObjectId != vertexId) {
                            TempHighlight(targetResult.ObjectId, tr);
                            var confirmOpts = new PromptKeywordOptions("\nПодтвердить соединение? [Да(Y)/Нет(N)]: ");
                            confirmOpts.Keywords.Add("Y");
                            confirmOpts.Keywords.Add("N");
                            confirmOpts.AllowNone = false;
                            var confirmResult = ed.GetKeywords(confirmOpts);
                            if (confirmResult.Status == PromptStatus.OK && confirmResult.StringResult == "Y") {
                                if (_vertexModel.ContainsKey(targetResult.ObjectId)) {
                                    CreateEdgeEntity(db, tr, vertexId, targetResult.ObjectId);
                                } else {
                                    var existingEdge = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == targetResult.ObjectId);
                                    if (existingEdge != null) {
                                        var peo = new PromptPointOptions("\nУкажите точку на ребре для разделения: ");
                                        var pRes = ed.GetPoint(peo);
                                        if (pRes.Status == PromptStatus.OK) {
                                            SplitEdgeAtPoint(db, tr, existingEdge, pRes.Value, vertexId);
                                        }
                                    }
                                }
                            }

                            ClearTempHighlight(tr);
                        }
                    }
                }

                tr.Commit();
            }
        }

        private static ObjectId CreateVertexEntity(Database db, Transaction tr, Point3d center, bool isCircle, string label) {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            if (isCircle) {
                var circle = new Circle(center, Vector3d.ZAxis, GeometryUtils.VertexRadius);
                btr.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
                try {
                    AttachLabelXData(tr, db, circle.ObjectId, label);
                } catch { }

                return circle.ObjectId;
            } else {
                var tri = new Polyline();
                var pts = GeometryUtils.GetTrianglePointsByRadius(center, GeometryUtils.VertexRadius);
                tri.AddVertexAt(0, new Point2d(pts.A.X, pts.A.Y), 0, 0, 0);
                tri.AddVertexAt(1, new Point2d(pts.B.X, pts.B.Y), 0, 0, 0);
                tri.AddVertexAt(2, new Point2d(pts.C.X, pts.C.Y), 0, 0, 0);
                tri.Closed = true;
                btr.AppendEntity(tri);
                tr.AddNewlyCreatedDBObject(tri, true);
                try {
                    AttachLabelXData(tr, db, tri.ObjectId, label);
                } catch { }

                return tri.ObjectId;
            }
        }

        private static void ClearPathHighlight(Transaction tr) {
            foreach (var objId in _highlightedPath) {
                var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                ent.ColorIndex = 256;
            }

            _highlightedPath.Clear();
        }

        private static void HighlightEntity(Transaction tr, ObjectId objId) {
            var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
            ent.Color = Teigha.Colors.Color.FromRgb(_pathHighlightColor.R, _pathHighlightColor.G, _pathHighlightColor.B);
        }

        private static Dictionary<ObjectId, (short colorIndex, Teigha.Colors.Color color)> _originalColors = new();

        private static Database _subscribedDb = default!;

        private static void SubscribeToDatabaseEvents(Database db) {
            try {
                if (_subscribedDb != null) {
                    _subscribedDb.ObjectModified -= OnDatabaseObjectModified;
                    _subscribedDb.ObjectErased -= OnDatabaseObjectErased;
                    _subscribedDb.ObjectAppended -= OnDatabaseObjectAppended;
                }
            } catch { }

            _subscribedDb = db;
            try {
                _subscribedDb.ObjectModified += OnDatabaseObjectModified;
                _subscribedDb.ObjectErased += OnDatabaseObjectErased;
                _subscribedDb.ObjectAppended += OnDatabaseObjectAppended;
            } catch { }
        }

        private static void TempHighlight(ObjectId objId, Transaction tr) {
            if (objId == ObjectId.Null) return;
            try {
                var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                if (ent == null) return;

                if (!_originalColors.ContainsKey(objId)) {
                    _originalColors[objId] = ((short)ent.ColorIndex, ent.Color);
                    ent.Color = Teigha.Colors.Color.FromRgb(0, 255, 255); // cyan
                    _lastHighlightedEntity = objId;
                }
            } catch { }
        }

        private static void ClearTempHighlight(Transaction tr) {
            if (_lastHighlightedEntity == ObjectId.Null) return;
            try {
                var objId = _lastHighlightedEntity;
                if (_originalColors.TryGetValue(objId, out var orig)) {
                    var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                    if (ent != null) {
                        ent.ColorIndex = orig.colorIndex;
                        ent.Color = orig.color;
                    }

                    _originalColors.Remove(objId);
                }
            } catch { }

            _lastHighlightedEntity = ObjectId.Null;
        }


        private static void RestoreGraphFromDrawing(Document doc) {
            if (doc == null) return;

            _currentVertices.Clear();
            _vertexModel.Clear();
            _edgeModel.Clear();
            _lastVertex = ObjectId.Null;
            _isBuilding = false;

            var db = doc.Database;
            using (var tr = db.TransactionManager.StartTransaction()) {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                using (var tr2 = db.TransactionManager.StartTransaction()) {
                    RegisterApplications(tr2, db);
                    tr2.Commit();
                }

                var regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                bool hasAnyApp = regAppTable.Has(VERTEX_APP) || regAppTable.Has(EDGE_APP);

                if (!hasAnyApp) {
                    doc.Editor.WriteMessage("\n[UGB] No graph objects found in drawing.\n");
                    tr.Commit();
                    return;
                }

                foreach (ObjectId id in btr) {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var xdata = ent.XData;
                    if (xdata == null) continue;

                    var isVertex = false;
                    string? label = null;
                    string? uid = null;

                    try {
                        var arr = xdata.AsArray();

                        var diag = string.Join(", ", arr.Select(tv => $"{tv.TypeCode}:{tv.Value}"));
                        doc.Editor.WriteMessage($"\n[UGB] Entity XData diag {id}: {diag}\n");

                        for (int i = 0; i < arr.Length; i++) {
                            var tv = arr[i];
                            if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName && tv.Value.ToString() == VERTEX_APP) {
                                isVertex = true;
                                int j = i + 1;
                                if (j < arr.Length && arr[j].TypeCode == (int)DxfCode.ExtendedDataRegAppName && arr[j].Value.ToString() == APP_NAME) {
                                    j++;
                                    if (j < arr.Length && arr[j].TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                                        label = arr[j].Value.ToString();
                                        j++;
                                    }

                                    if (j < arr.Length && arr[j].TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                                        uid = arr[j].Value.ToString();
                                    }
                                }

                                break;
                            }
                        }
                        
                        if (!isVertex) {
                            for (int i = 0; i < arr.Length; i++) {
                                var tv = arr[i];
                                if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName && tv.Value.ToString() == APP_NAME) {
                                    isVertex = true;
                                    int j = i + 1;
                                    if (j < arr.Length && arr[j].TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                                        label = arr[j].Value.ToString();
                                        j++;
                                    }

                                    if (j < arr.Length && arr[j].TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                                        uid = arr[j].Value.ToString();
                                    }

                                    break;
                                }
                            }
                        }
                    } catch (System.Exception ex) {
                        doc.Editor.WriteMessage($"\n[UGB] XData parse error for {id}: {ex.Message}\n");
                    }

                    doc.Editor.WriteMessage($"\n[UGB] Found entity {id}, isVertex={isVertex}, label={label}, uid={uid}\n");
                    if (isVertex && (ent is Circle || (ent is Polyline poly && poly.NumberOfVertices == 3))) {
                        Point3d center;
                        bool isTriangle;

                        if (ent is Circle circle) {
                            center = circle.Center;
                            isTriangle = false;
                        } else if (ent is Polyline poly3 && poly3.NumberOfVertices == 3) {
                            var p1 = poly3.GetPoint2dAt(0);
                            var p2 = poly3.GetPoint2dAt(1);
                            var p3 = poly3.GetPoint2dAt(2);
                            center = new Point3d(
                                (p1.X + p2.X + p3.X) / 3,
                                (p1.Y + p2.Y + p3.Y) / 3,
                                0
                            );
                            isTriangle = true;
                        } else {
                            continue;
                        }

                        var vertex = new UndirectedGraphBuilder.CadObjects.GraphVertex(center, isTriangle) {
                            DrawingObjectId = id,
                            Label = label ?? string.Empty,
                            Uid = uid ?? Guid.NewGuid().ToString()
                        };
                        _vertexModel[id] = vertex;
                        _currentVertices.Add(id);
                        doc.Editor.WriteMessage($"\n[UGB] Restored vertex at {center.X},{center.Y} with ID {id}\n");
                    }
                }


                foreach (ObjectId id in btr) {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || !(ent is Polyline)) continue;

                    var xdata = ent.XData;
                    if (xdata == null) continue;


                    var isEdge = false;
                    ObjectId startId = ObjectId.Null;
                    ObjectId endId = ObjectId.Null;

                    foreach (TypedValue value in xdata) {
                        if (value.TypeCode == (int)DxfCode.ExtendedDataRegAppName && value.Value.ToString() == EDGE_APP) {
                            isEdge = true;
                            continue;
                        } else if (isEdge && value.TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                            var handleString = value.Value.ToString();
                            if (long.TryParse(handleString, out long hVal)) {
                                var handle = new Handle(hVal);
                                try {
                                    var objId = db.GetObjectId(false, handle, 0);
                                    if (startId == ObjectId.Null)
                                        startId = objId;
                                    else
                                        endId = objId;
                                } catch { }
                            }
                        }
                    }

                    doc.Editor.WriteMessage($"\n[UGB] Found polyline {id}, isEdge={isEdge}, start={startId}, end={endId}\n");
                    if (isEdge && startId != ObjectId.Null && endId != ObjectId.Null && _vertexModel.ContainsKey(startId) && _vertexModel.ContainsKey(endId)) {
                        var edge = new CadObjects.GraphEdge();
                        edge.DrawingObjectId = id;
                        edge.SetVertices(startId, endId);
                        _edgeModel.Add(edge);


                        if (_vertexModel.ContainsKey(startId))
                            _vertexModel[startId].AddAttachedEdge(id);
                        if (_vertexModel.ContainsKey(endId))
                            _vertexModel[endId].AddAttachedEdge(id);
                        doc.Editor.WriteMessage($"\n[UGB] Restored edge {id} connecting {startId}->{endId}\n");
                    }
                }

                tr.Commit();
            }


            try {
                SubscribeToDatabaseEvents(db);
            } catch { }
        }


        [CommandMethod("UGB_ATTACH")]
        public static void AttachEventsCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            SubscribeToDatabaseEvents(doc.Database);
            doc.Editor.WriteMessage("\n[UGB] Event handlers attached to current document.\n");
        }

        [CommandMethod("UGB_ENSUREUID")]
        public static void EnsureUidsCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            using (var tr = db.TransactionManager.StartTransaction()) {
                int written = 0;
                foreach (var kv in _vertexModel.ToList()) {
                    var id = kv.Key;
                    var v = kv.Value;
                    if (string.IsNullOrEmpty(v.Uid) || v.Uid.Trim() == string.Empty) {
                        try {
                            AttachLabelXData(tr, db, id, v.Label);
                            written++;
                        } catch { }
                    }
                }

                tr.Commit();
                ed.WriteMessage($"\n[UGB] Ensured UIDs for {written} vertices.\n");
            }
        }

        private static void OnDatabaseObjectModified(object sender, ObjectEventArgs e) {
            try {
                var db = sender as Database;
                if (db == null) return;

                var id = e.DBObject?.ObjectId ?? ObjectId.Null;
                if (id == ObjectId.Null) return;


                if (!_vertexModel.ContainsKey(id)) return;

                using (var tr = db.TransactionManager.StartTransaction()) {
                    var center = GetEntityCenter(tr, id);

                    if (center != null) {
                        var oldCenter = _vertexModel[id].Center;
                        if (Math.Abs(oldCenter.X - center.Value.X) > 1e-6 || Math.Abs(oldCenter.Y - center.Value.Y) > 1e-6 || Math.Abs(oldCenter.Z - center.Value.Z) > 1e-6) {
                            _vertexModel[id].Center = center.Value;
                            var attached = _vertexModel[id].AttachedEdgeIds.ToArray();
                            foreach (var edgeId in attached) {
                                var edge = _edgeModel.FirstOrDefault(x => x.DrawingObjectId == edgeId);
                                if (edge != null) {
                                    UpdateEdgeGeometry(edge, tr);
                                }
                            }
                        }
                    }

                    tr.Commit();
                }
            } catch { }
        }

        private static void OnDatabaseObjectErased(object sender, ObjectErasedEventArgs e) {
            try {
                var db = sender as Database;
                if (db == null) return;

                var id = e.DBObject?.ObjectId ?? ObjectId.Null;
                if (id == ObjectId.Null) return;


                if (!e.Erased) return;

                using (var tr = db.TransactionManager.StartTransaction()) {
                    if (_vertexModel.ContainsKey(id)) {
                        var v = _vertexModel[id];
                        v.IsErased = true;

                        _currentVertices.Remove(id);
                    }


                    var edgeObj = _edgeModel.FirstOrDefault(ev => ev.DrawingObjectId == id);
                    if (edgeObj != null) {
                        if (_vertexModel.TryGetValue(edgeObj.StartVertexId, out var sv)) sv.RemoveAttachedEdge(id);
                        if (_vertexModel.TryGetValue(edgeObj.EndVertexId, out var evv)) evv.RemoveAttachedEdge(id);
                        _edgeModel.Remove(edgeObj);
                    }

                    tr.Commit();
                }
            } catch { }
        }

        private static void UpdateEdgeGeometry(CadObjects.GraphEdge edge, Transaction tr) {
            if (edge == null || edge.DrawingObjectId == ObjectId.Null) return;

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\n[UGB] UpdateEdgeGeometry called for edge {edge.DrawingObjectId}\n");

            var pl = tr.GetObject(edge.DrawingObjectId, OpenMode.ForWrite) as Polyline;
            if (pl == null) {
                ed?.WriteMessage($"\n[UGB] UpdateEdgeGeometry: polyline {edge.DrawingObjectId} not found\n");
                return;
            }

            var startCenter = GetEntityCenter(tr, edge.StartVertexId);
            var endCenter = GetEntityCenter(tr, edge.EndVertexId);
            if (startCenter == null || endCenter == null) {
                ed?.WriteMessage($"\n[UGB] UpdateEdgeGeometry: missing start or end center for edge {edge.DrawingObjectId}\n");
                return;
            }
            
            try {
                pl.Closed = false;
            } catch { }
            
            try {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var db2 = doc.Database;
                var bt = (BlockTable)tr.GetObject(db2.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var newPl = new Polyline();
                newPl.AddVertexAt(0, new Point2d(startCenter.Value.X, startCenter.Value.Y), 0, 0, 0);
                int idx = 1;
                if (edge.IntermediatePoints != null && edge.IntermediatePoints.Count > 0) {
                    foreach (var ip in edge.IntermediatePoints) {
                        newPl.AddVertexAt(idx++, new Point2d(ip.X, ip.Y), 0, 0, 0);
                    }
                }

                newPl.AddVertexAt(idx, new Point2d(endCenter.Value.X, endCenter.Value.Y), 0, 0, 0);
                newPl.Closed = false;
                try {
                    newPl.Color = pl.Color;
                    newPl.Layer = pl.Layer;
                    newPl.Normal = pl.Normal;
                } catch { }

                btr.AppendEntity(newPl);
                tr.AddNewlyCreatedDBObject(newPl, true);

                try {
                    AttachEdgeXData(tr, db2, newPl.ObjectId, edge.StartVertexId, edge.EndVertexId);
                } catch { }

                var oldId = edge.DrawingObjectId;
                edge.DrawingObjectId = newPl.ObjectId;

                if (_vertexModel.TryGetValue(edge.StartVertexId, out var sv)) {
                    sv.RemoveAttachedEdge(oldId);
                    sv.AddAttachedEdge(newPl.ObjectId);
                }

                if (_vertexModel.TryGetValue(edge.EndVertexId, out var ev)) {
                    ev.RemoveAttachedEdge(oldId);
                    ev.AddAttachedEdge(newPl.ObjectId);
                }

                try {
                    var oldEnt = (Entity)tr.GetObject(oldId, OpenMode.ForWrite);
                    oldEnt.Erase();
                } catch { }

                ed?.WriteMessage($"\n[UGB] Replaced edge {oldId} with {newPl.ObjectId}. Vertices={newPl.NumberOfVertices}\n");
            } catch (System.Exception ex) {
                ed?.WriteMessage($"\n[UGB] UpdateEdgeGeometry REPLACE EXCEPTION for edge {edge.DrawingObjectId}: {ex.Message}\n{ex}\n");
            }

            try {
                var coords = new List<string>();
                for (int i = 0; i < pl.NumberOfVertices; i++) {
                    var p = pl.GetPoint2dAt(i);
                    coords.Add($"({p.X:0.##},{p.Y:0.##})");
                }

                ed?.WriteMessage($"\n[UGB] Edge {edge.DrawingObjectId} updated. Vertices={pl.NumberOfVertices}: {string.Join(" -> ", coords)}\n");
            } catch { }

            try {
                Application.DocumentManager.MdiActiveDocument?.Editor.Regen();
            } catch { }
        }

        private static void AttachLabelXData(Transaction tr, Database db, ObjectId objId, string label, string? uid = null) {
            RegisterApplications(tr, db);
            if (string.IsNullOrEmpty(uid)) uid = Guid.NewGuid().ToString();

            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, APP_NAME),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, label ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, uid)
            );
            var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);

            var vertexMarker = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, VERTEX_APP)
            );

            var combinedRb = new ResultBuffer(vertexMarker.AsArray().Concat(rb.AsArray()).ToArray());
            ent.XData = combinedRb;
            if (_vertexModel.TryGetValue(objId, out var dto)) dto.Uid = uid;
        }

        private static void OnDatabaseObjectAppended(object sender, ObjectEventArgs e) {
            try {
                var db = sender as Database;
                if (db == null) return;

                var appendedId = e.DBObject?.ObjectId ?? ObjectId.Null;
                if (appendedId == ObjectId.Null) return;

                using (var tr = db.TransactionManager.StartTransaction()) {
                    var ent = tr.GetObject(appendedId, OpenMode.ForRead) as Entity;
                    if (ent != null && ent.XData != null) {
                        string? foundUid = null;
                        string? foundLabel = null;
                        bool isVertex = false;
                        foreach (TypedValue tv in ent.XData) {
                            if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName && tv.Value.ToString() == VERTEX_APP) {
                                isVertex = true;
                                continue;
                            }

                            if (isVertex && tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                                if (foundLabel == null) {
                                    foundLabel = tv.Value.ToString();
                                    continue;
                                }

                                if (foundUid == null) {
                                    foundUid = tv.Value.ToString();
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(foundUid)) {
                            var pair = _vertexModel.FirstOrDefault(kv => kv.Value.Uid == foundUid);
                            if (!pair.Equals(default(KeyValuePair<ObjectId, CadObjects.GraphVertex>))) {
                                var oldId = pair.Key;
                                var newId = appendedId;

                                var dto = pair.Value;
                                dto.DrawingObjectId = newId;
                                dto.IsErased = false;
                                _vertexModel.Remove(oldId);
                                _vertexModel[newId] = dto;
                                if (!_currentVertices.Contains(newId)) _currentVertices.Add(newId);

                                var attached = dto.AttachedEdgeIds.ToArray();
                                foreach (var edgeId in attached) {
                                    var edge = _edgeModel.FirstOrDefault(x => x.DrawingObjectId == edgeId);
                                    if (edge != null) {
                                        if (edge.StartVertexId == oldId) edge.SetVertices(newId, edge.EndVertexId);
                                        if (edge.EndVertexId == oldId) edge.SetVertices(edge.StartVertexId, newId);
                                        try {
                                            UpdateEdgeGeometry(edge, tr);
                                            AttachEdgeXData(tr, db, edge.DrawingObjectId, edge.StartVertexId, edge.EndVertexId);
                                        } catch { }
                                    }
                                }
                            }
                        }
                    }

                    tr.Commit();
                }
            } catch { }
        }

        private static void AttachEdgeXData(Transaction tr, Database db, ObjectId edgeId, ObjectId startId, ObjectId endId) {
            RegisterApplications(tr, db);

            var ent = (Entity)tr.GetObject(edgeId, OpenMode.ForWrite);

            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, EDGE_APP),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, startId.Handle.Value.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, endId.Handle.Value.ToString())
            );
            ent.XData = rb;
        }

        private static void RegisterApplications(Transaction tr, Database db) {
            var regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);

            var appsToRegister = new[] { APP_NAME, GRAPH_APP, VERTEX_APP, EDGE_APP };

            foreach (var appName in appsToRegister) {
                if (!regAppTable.Has(appName)) {
                    regAppTable.UpgradeOpen();
                    var regApp = new RegAppTableRecord { Name = appName };
                    regAppTable.Add(regApp);
                    tr.AddNewlyCreatedDBObject(regApp, true);
                }
            }
        }

        private static ObjectId CreateEdgeEntity(Database db, Transaction tr, ObjectId startId, ObjectId endId, List<Point3d>? intermediatePoints = null) {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var startPt = GetEntityCenter(tr, startId);
            var endPt = GetEntityCenter(tr, endId);
            if (startPt == null || endPt == null) return ObjectId.Null;

            EnsureEdgeLayer(tr, db);

            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(startPt.Value.X, startPt.Value.Y), 0, 0, 0);
            int idx = 1;
            if (intermediatePoints != null && intermediatePoints.Count > 0) {
                foreach (var ip in intermediatePoints) {
                    pl.AddVertexAt(idx++, new Point2d(ip.X, ip.Y), 0, 0, 0);
                }
            }

            pl.AddVertexAt(idx, new Point2d(endPt.Value.X, endPt.Value.Y), 0, 0, 0);
            pl.Closed = false;

            try {
                pl.Color = Teigha.Colors.Color.FromRgb(0, 0, 0);
                pl.Normal = Vector3d.ZAxis;
                pl.Layer = "UGB_EDGES";
            } catch { }

            btr.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            AttachEdgeXData(tr, db, pl.ObjectId, startId, endId);

            var dto = new CadObjects.GraphEdge();
            dto.DrawingObjectId = pl.ObjectId;
            dto.SetVertices(startId, endId);
            if (intermediatePoints != null && intermediatePoints.Count > 0) {
                foreach (var ip in intermediatePoints) dto.AddIntermediatePoint(ip);
            }

            _edgeModel.Add(dto);

            if (_vertexModel.TryGetValue(startId, out var sv)) sv.AddAttachedEdge(pl.ObjectId);
            if (_vertexModel.TryGetValue(endId, out var ev)) ev.AddAttachedEdge(pl.ObjectId);

            try {
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                var verts = new List<string> { $"({startPt.Value.X:0.##},{startPt.Value.Y:0.##})", $"({endPt.Value.X:0.##},{endPt.Value.Y:0.##})" };
                ed?.WriteMessage($"\n[UGB] Created edge {pl.ObjectId} from {startId} to {endId}. Vertices={pl.NumberOfVertices}: {string.Join(" -> ", verts)}\n");
                try {
                    Application.DocumentManager.MdiActiveDocument?.Editor.Regen();
                } catch { }
            } catch { }

            return pl.ObjectId;
        }

        private static Point3d? GetEntityCenter(Transaction tr, ObjectId id) {
            if (id == ObjectId.Null) return null;
            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent is Circle c) return c.Center;
            if (ent is Polyline p) {
                if (p.NumberOfVertices > 0) {
                    double sumX = 0, sumY = 0;
                    for (int i = 0; i < p.NumberOfVertices; i++) {
                        var v = p.GetPoint2dAt(i);
                        sumX += v.X;
                        sumY += v.Y;
                    }

                    return new Point3d(sumX / p.NumberOfVertices, sumY / p.NumberOfVertices, 0);
                }
            }

            return null;
        }

        private static void SplitEdgeAtPoint(Database db, Transaction tr, CadObjects.GraphEdge edge, Point3d splitPoint, ObjectId connectingVertexId) {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;

            try {
                var startCenter = GetEntityCenter(tr, edge.StartVertexId);
                var endCenter = GetEntityCenter(tr, edge.EndVertexId);
                if (startCenter == null || endCenter == null) {
                    ed?.WriteMessage($"\n[UGB] SplitEdgeAtPoint: cannot find start or end centers for edge {edge.DrawingObjectId}\n");
                    return;
                }

                var pts = new List<Point3d> { startCenter.Value };
                if (edge.IntermediatePoints != null && edge.IntermediatePoints.Count > 0) pts.AddRange(edge.IntermediatePoints);
                pts.Add(endCenter.Value);

                int bestSeg = -1;
                double bestDist = double.MaxValue;
                Point3d projectedSplitPoint = splitPoint;
                for (int i = 0; i < pts.Count - 1; i++) {
                    var a = pts[i];
                    var b = pts[i + 1];
                    var ax = a.X;
                    var ay = a.Y;
                    var bx = b.X;
                    var by = b.Y;
                    var sx = splitPoint.X;
                    var sy = splitPoint.Y;
                    var dx = bx - ax;
                    var dy = by - ay;
                    var segLen2 = dx * dx + dy * dy;
                    double t = segLen2 > 0 ? ((sx - ax) * dx + (sy - ay) * dy) / segLen2 : 0.0;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    var projX = ax + t * dx;
                    var projY = ay + t * dy;
                    var dist2 = (projX - sx) * (projX - sx) + (projY - sy) * (projY - sy);
                    if (dist2 < bestDist) {
                        bestDist = dist2;
                        bestSeg = i;
                        projectedSplitPoint = new Point3d(projX, projY, splitPoint.Z);
                    }
                }

                if (bestSeg < 0) {
                    ed?.WriteMessage($"\n[UGB] SplitEdgeAtPoint: cannot determine split segment for edge {edge.DrawingObjectId}\n");
                    return;
                }

                ed?.WriteMessage($"\n[UGB] Split point: original=({splitPoint.X:F2},{splitPoint.Y:F2}), projected=({projectedSplitPoint.X:F2},{projectedSplitPoint.Y:F2})\n");

                List<Point3d>? firstIntermediates = null;
                if (bestSeg >= 1) firstIntermediates = pts.Skip(1).Take(bestSeg).ToList();
                List<Point3d>? secondIntermediates = null;
                if (bestSeg + 1 <= pts.Count - 2) secondIntermediates = pts.Skip(bestSeg + 1).Take(pts.Count - 2 - bestSeg).ToList();

                var newVertexId = CreateVertexEntity(db, tr, projectedSplitPoint, true, "");
                if (newVertexId == ObjectId.Null) {
                    ed?.WriteMessage($"\n[UGB] SplitEdgeAtPoint: failed to create new vertex\n");
                    return;
                }

                try {
                    var ent = (Entity)tr.GetObject(newVertexId, OpenMode.ForWrite);
                    ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                } catch { }

                var newVertexDto = new CadObjects.GraphVertex(projectedSplitPoint, false) { DrawingObjectId = newVertexId, Label = string.Empty };
                _vertexModel[newVertexId] = newVertexDto;
                _currentVertices.Add(newVertexId);
                AttachLabelXData(tr, db, newVertexId, "");

                if (_vertexModel.TryGetValue(edge.StartVertexId, out var sv)) sv.RemoveAttachedEdge(edge.DrawingObjectId);
                if (_vertexModel.TryGetValue(edge.EndVertexId, out var ev)) ev.RemoveAttachedEdge(edge.DrawingObjectId);

                var firstEdgeId = CreateEdgeEntity(db, tr, edge.StartVertexId, newVertexId, firstIntermediates);
                var secondEdgeId = CreateEdgeEntity(db, tr, newVertexId, edge.EndVertexId, secondIntermediates);

                try {
                    var oldEnt = (Entity)tr.GetObject(edge.DrawingObjectId, OpenMode.ForWrite);
                    oldEnt.Erase();
                } catch { }

                _edgeModel.Remove(edge);

                try {
                    if (connectingVertexId != ObjectId.Null) CreateEdgeEntity(db, tr, connectingVertexId, newVertexId);
                } catch { }

                ed?.WriteMessage($"\n[UGB] Split edge into {firstEdgeId} and {secondEdgeId} with new vertex {newVertexId}\n");
            } catch (System.Exception ex) {
                ed?.WriteMessage($"\n[UGB] SplitEdgeAtPoint EX: {ex.Message}\n{ex}\n");
            }
        }

        private static void EnsureEdgeLayer(Transaction tr, Database db) {
            try {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has("UGB_EDGES")) {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = "UGB_EDGES" };
                    try {
                        ltr.Color = Teigha.Colors.Color.FromRgb(0, 0, 0);
                    } catch { }

                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
            } catch { }
        }

        private static void UndoLastOperation() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            if (_lastVertex != ObjectId.Null) {
                using (var tr = db.TransactionManager.StartTransaction()) {
                    var ent = tr.GetObject(_lastVertex, OpenMode.ForWrite) as Entity;
                    ent?.Erase();
                    tr.Commit();
                }

                _lastVertex = ObjectId.Null;
            }
        }

        [CommandMethod("UGB_STYLE")]
        public static void StyleVertexCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var opts = new PromptEntityOptions("\nВыберите вершину для изменения стиля: ");
            opts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            opts.AddAllowedClass(typeof(Circle), false);
            opts.AddAllowedClass(typeof(Polyline), false);
            var res = ed.GetEntity(opts);
            if (res.Status != PromptStatus.OK) return;

            var objId = res.ObjectId;
            if (!_vertexModel.ContainsKey(objId)) {
                ed.WriteMessage("\nВыбранный объект не является вершиной плагина.");
                return;
            }

            var shapeOpts = new PromptKeywordOptions("\nФорма [Круг(C)/Треугольник(T)]: ");
            shapeOpts.Keywords.Add("C");
            shapeOpts.Keywords.Add("T");
            shapeOpts.AllowNone = false;
            var shapeRes = ed.GetKeywords(shapeOpts);
            if (shapeRes.Status != PromptStatus.OK) return;

            var colorOpts = new PromptKeywordOptions("\nЦвет [Синий(B)/Красный(R)]: ");
            colorOpts.Keywords.Add("B");
            colorOpts.Keywords.Add("R");
            colorOpts.AllowNone = false;
            var colorRes = ed.GetKeywords(colorOpts);
            if (colorRes.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var dto = _vertexModel[objId];
                var uid = dto.Uid;
                var label = dto.Label;
                var center = dto.Center;
                bool makeCircle = shapeRes.StringResult == "C";

                try {
                    var oldEnt = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                    oldEnt.Erase();
                } catch { }

                var newId = CreateVertexEntity(db, tr, center, makeCircle, label);
                if (newId == ObjectId.Null) {
                    tr.Commit();
                    return;
                }

                try {
                    var ent = (Entity)tr.GetObject(newId, OpenMode.ForWrite);
                    if (colorRes.StringResult == "B") ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                    else ent.Color = Teigha.Colors.Color.FromRgb(255, 0, 0);
                } catch { }

                try {
                    AttachLabelXData(tr, db, newId, label, uid);
                } catch { }

                dto.DrawingObjectId = newId;
                _vertexModel.Remove(objId);
                _vertexModel[newId] = dto;

                var attachments = dto.AttachedEdgeIds.ToArray();
                foreach (var edgeId in attachments) {
                    var edge = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == edgeId);
                    if (edge != null) {
                        if (edge.StartVertexId == objId) edge.SetVertices(newId, edge.EndVertexId);
                        if (edge.EndVertexId == objId) edge.SetVertices(edge.StartVertexId, newId);
                        try {
                            UpdateEdgeGeometry(edge, tr);
                            AttachEdgeXData(tr, db, edge.DrawingObjectId, edge.StartVertexId, edge.EndVertexId);
                        } catch { }
                    }
                }

                tr.Commit();
            }
        }

        [CommandMethod("UGB_LIST_XDATA")]
        public static void ListXDataCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in btr) {
                    try {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var xdata = ent.XData;
                        if (xdata == null) continue;
                        ed.WriteMessage($"\n[UGB] Entity {id} XData:");
                        foreach (TypedValue tv in xdata) {
                            ed.WriteMessage($" type={tv.TypeCode} value={tv.Value}");
                        }
                    } catch { }
                }

                tr.Commit();
            }
        }

        private static void CleanupBuildProcess() {
            _isBuilding = false;
            _currentVertices.Clear();
            _lastVertex = ObjectId.Null;
        }
    }
}