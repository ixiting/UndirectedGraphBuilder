using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using Teigha.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using UndirectedGraphBuilder.CadObjects;
using UndirectedGraphBuilder.CadCommands.Services;
using UndirectedGraphBuilder.Utils;

namespace UndirectedGraphBuilder.CadCommands {

    public static class Commands {

        internal const string APP_NAME = "UGB_APP";
        internal const string VERTEX_APP = "UGB_VERTEX";
        internal const string EDGE_APP = "UGB_EDGE";
        internal const string GRAPH_APP = "UGB_GRAPH";

        internal static bool _isBuilding = false;
        internal static List<ObjectId> _currentVertices = new();
        internal static ObjectId _lastVertex = ObjectId.Null;
        internal static Dictionary<ObjectId, GraphVertex> _vertexModel = new();
        internal static List<GraphEdge> _edgeModel = new();
        internal static List<ObjectId> _highlightedPath = new();
        internal static System.Windows.Media.Color _pathHighlightColor = System.Windows.Media.Color.FromRgb(255, 165, 0);

        internal static ObjectId CreateVertexEntity(Database db, Transaction tr, Point3d center, bool isCircle, string label) {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            if (isCircle) {
                var circle = new Circle(center, Vector3d.ZAxis, GeometryUtils.VertexRadius);
                btr.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
                try { AttachLabelXData(tr, db, circle.ObjectId, label); } catch { }
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
                try { AttachLabelXData(tr, db, tri.ObjectId, label); } catch { }
                return tri.ObjectId;
            }
        }

        internal static ObjectId CreateEdgeEntity(Database db, Transaction tr, ObjectId startId, ObjectId endId, List<Point3d>? intermediatePoints = null) {
            var startPt = GetEntityCenter(tr, startId);
            var endPt = GetEntityCenter(tr, endId);
            if (startPt == null || endPt == null) return ObjectId.Null;

            EnsureEdgeLayer(tr, db);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(startPt.Value.X, startPt.Value.Y), 0, 0, 0);
            int idx = 1;
            if (intermediatePoints != null) {
                foreach (var ip in intermediatePoints) pl.AddVertexAt(idx++, new Point2d(ip.X, ip.Y), 0, 0, 0);
            }
            pl.AddVertexAt(idx, new Point2d(endPt.Value.X, endPt.Value.Y), 0, 0, 0);
            pl.Closed = false;

            try { pl.Color = Teigha.Colors.Color.FromRgb(0, 0, 0); pl.Normal = Vector3d.ZAxis; pl.Layer = "UGB_EDGES"; } catch { }

            btr.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            try { AttachEdgeXData(tr, db, pl.ObjectId, startId, endId); } catch { }

            var dto = new GraphEdge();
            dto.DrawingObjectId = pl.ObjectId;
            dto.SetVertices(startId, endId);
            if (intermediatePoints != null) foreach (var ip in intermediatePoints) dto.AddIntermediatePoint(ip);

            _edgeModel.Add(dto);

            if (_vertexModel.TryGetValue(startId, out var sv)) sv.AddAttachedEdge(pl.ObjectId);
            if (_vertexModel.TryGetValue(endId, out var ev)) ev.AddAttachedEdge(pl.ObjectId);

            return pl.ObjectId;
        }

        internal static void SplitEdgeAtPoint(Database db, Transaction tr, GraphEdge edge, Point3d splitPoint, ObjectId connectingVertexId) {
            if (edge == null || edge.DrawingObjectId == ObjectId.Null) return;

            var startCenter = GetEntityCenter(tr, edge.StartVertexId);
            var endCenter = GetEntityCenter(tr, edge.EndVertexId);
            if (startCenter == null || endCenter == null) return;

            var pts = new List<Point3d> { startCenter.Value };
            if (edge.IntermediatePoints != null) foreach (var ip in edge.IntermediatePoints) pts.Add(ip);
            pts.Add(endCenter.Value);

            double bestDist = double.MaxValue;
            Point3d bestProj = splitPoint;
            int bestSeg = -1;

            for (int i = 0; i < pts.Count - 1; i++) {
                var p0 = pts[i];
                var p1 = pts[i + 1];
                var vx = p1.X - p0.X;
                var vy = p1.Y - p0.Y;
                var wx = splitPoint.X - p0.X;
                var wy = splitPoint.Y - p0.Y;
                var denom = vx * vx + vy * vy;
                double t = 0.0;
                if (denom > 1e-12) t = (wx * vx + wy * vy) / denom;
                if (t < 0) t = 0; if (t > 1) t = 1;
                var proj = new Point3d(p0.X + t * vx, p0.Y + t * vy, p0.Z + t * (p1.Z - p0.Z));
                var d = proj.DistanceTo(splitPoint);
                if (d < bestDist) { bestDist = d; bestProj = proj; bestSeg = i; }
            }

            if (bestSeg < 0) return;

            // Create new vertex at projection
            var newVertexId = CreateVertexEntity(db, tr, bestProj, true, string.Empty);
            if (newVertexId == ObjectId.Null) return;

            try {
                var newDto = new GraphVertex(bestProj, false) { DrawingObjectId = newVertexId, Label = string.Empty };
                _vertexModel[newVertexId] = newDto;
                if (!_currentVertices.Contains(newVertexId)) _currentVertices.Add(newVertexId);
                try { var ent = (Entity)tr.GetObject(newVertexId, OpenMode.ForWrite); ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255); } catch { }

                // Split intermediate points into left and right lists
                var leftList = new List<Point3d>();
                var rightList = new List<Point3d>();
                if (edge.IntermediatePoints != null) {
                    int m = edge.IntermediatePoints.Count;
                    for (int i = 0; i < m; i++) {
                        int ptsIndex = i + 1; // intermediates map to pts[1..m]
                        if (ptsIndex - 1 < bestSeg) leftList.Add(edge.IntermediatePoints[i]);
                        else rightList.Add(edge.IntermediatePoints[i]);
                    }
                }

                leftList.Add(bestProj);
                rightList.Insert(0, bestProj);

                // Erase old edge and update models
                try { var oldEnt = (Entity)tr.GetObject(edge.DrawingObjectId, OpenMode.ForWrite); oldEnt.Erase(); } catch { }
                var oldStart = edge.StartVertexId;
                var oldEnd = edge.EndVertexId;
                _edgeModel.Remove(edge);
                if (_vertexModel.TryGetValue(oldStart, out var sv)) sv.RemoveAttachedEdge(edge.DrawingObjectId);
                if (_vertexModel.TryGetValue(oldEnd, out var ev)) ev.RemoveAttachedEdge(edge.DrawingObjectId);

                // Create new edges
                var leftEdgeId = CreateEdgeEntity(db, tr, oldStart, newVertexId, leftList);
                var rightEdgeId = CreateEdgeEntity(db, tr, newVertexId, oldEnd, rightList);

                // Connect to provided connecting vertex if any
                if (connectingVertexId != ObjectId.Null && connectingVertexId != newVertexId) {
                    CreateEdgeEntity(db, tr, connectingVertexId, newVertexId, null);
                }
            } catch { }
        }

        internal static void AttachLabelXData(Transaction tr, Database db, ObjectId objId, string label, string? uid = null) {
            RegisterApplications(tr, db);
            if (string.IsNullOrEmpty(uid)) uid = Guid.NewGuid().ToString();

            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, APP_NAME),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, label ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, uid)
            );

            var vertexMarker = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, VERTEX_APP));
            var combined = new ResultBuffer(vertexMarker.AsArray().Concat(rb.AsArray()).ToArray());
            var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
            ent.XData = combined;
            if (_vertexModel.TryGetValue(objId, out var dto)) dto.Uid = uid;
        }

        internal static void AttachEdgeXData(Transaction tr, Database db, ObjectId objId, ObjectId startId, ObjectId endId) {
            RegisterApplications(tr, db);
            var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, EDGE_APP),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, startId.Handle.Value.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, endId.Handle.Value.ToString())
            );
            ent.XData = rb;
        }

        private static void UpdateEdgeGeometry(GraphEdge edge, Transaction tr, Database db) {
            if (edge == null || edge.DrawingObjectId == ObjectId.Null) return;

            var pl = tr.GetObject(edge.DrawingObjectId, OpenMode.ForRead) as Polyline;
            if (pl == null) return;

            var startCenter = GetEntityCenter(tr, edge.StartVertexId);
            var endCenter = GetEntityCenter(tr, edge.EndVertexId);
            if (startCenter == null || endCenter == null) return;

            try {
                // Create replacement polyline
                var newPl = new Polyline();
                newPl.AddVertexAt(0, new Point2d(startCenter.Value.X, startCenter.Value.Y), 0, 0, 0);
                int idx = 1;
                if (edge.IntermediatePoints != null) {
                    foreach (var ip in edge.IntermediatePoints) newPl.AddVertexAt(idx++, new Point2d(ip.X, ip.Y), 0, 0, 0);
                }
                newPl.AddVertexAt(idx, new Point2d(endCenter.Value.X, endCenter.Value.Y), 0, 0, 0);
                newPl.Closed = false;

                try { newPl.Color = pl.Color; newPl.Layer = pl.Layer; newPl.Normal = pl.Normal; } catch { }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                btr.AppendEntity(newPl);
                tr.AddNewlyCreatedDBObject(newPl, true);

                try { AttachEdgeXData(tr, db, newPl.ObjectId, edge.StartVertexId, edge.EndVertexId); } catch { }

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
            } catch { }
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
                        sumX += v.X; sumY += v.Y;
                    }
                    return new Point3d(sumX / p.NumberOfVertices, sumY / p.NumberOfVertices, 0);
                }
            }
            return null;
        }

        private static void EnsureEdgeLayer(Transaction tr, Database db) {
            try {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has("UGB_EDGES")) {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord { Name = "UGB_EDGES" };
                    try { ltr.Color = Teigha.Colors.Color.FromRgb(0, 0, 0); } catch { }
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
            } catch { }
        }

        internal static void ClearPathHighlight(Transaction tr) {
            foreach (var objId in _highlightedPath) {
                try {
                    var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                    ent.ColorIndex = 256;
                } catch { }
            }
            _highlightedPath.Clear();
        }

        internal static void HighlightEntity(Transaction tr, ObjectId objId) {
            try {
                var ent = (Entity)tr.GetObject(objId, OpenMode.ForWrite);
                ent.Color = Teigha.Colors.Color.FromRgb(_pathHighlightColor.R, _pathHighlightColor.G, _pathHighlightColor.B);
            } catch { }
        }

        internal static void UndoLastOperation() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            if (_lastVertex != ObjectId.Null) {
                using (var tr = db.TransactionManager.StartTransaction()) {
                    try {
                        var ent = tr.GetObject(_lastVertex, OpenMode.ForWrite) as Entity;
                        ent?.Erase();
                    } catch { }
                    try { _vertexModel.Remove(_lastVertex); } catch { }
                    try { _currentVertices.Remove(_lastVertex); } catch { }
                    tr.Commit();
                }

                _lastVertex = ObjectId.Null;
            }
        }

        internal static void ProcessPoint(Point3d point, bool isCircle) {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            using (var tr = db.TransactionManager.StartTransaction()) {
            string label = string.Empty;
            var vertexId = CreateVertexEntity(db, tr, point, isCircle, label);

                if (vertexId != ObjectId.Null) {
                    try {
                        var ent = (Entity)tr.GetObject(vertexId, OpenMode.ForWrite);
                        ent.Color = Teigha.Colors.Color.FromRgb(0, 0, 255);
                    } catch { }

                    var dto = new GraphVertex(point, false) { DrawingObjectId = vertexId, Label = label };
                    _vertexModel[vertexId] = dto;
                    _currentVertices.Add(vertexId);

                    if (_lastVertex != ObjectId.Null) {
                        try { CreateEdgeEntity(db, tr, _lastVertex, vertexId); } catch { }
                    }

                    _lastVertex = vertexId;
                }

                tr.Commit();
            }
        }

        internal static void RegisterApplications(Transaction tr, Database db) {
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

        internal static Database _subscribedDb = default!;

        internal static void SubscribeToDatabaseEvents(Database db) {
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
                        if (Math.Abs(oldCenter.X - center.Value.X) > 1e-6 || Math.Abs(oldCenter.Y - center.Value.Y) > 1e-6) {
                            _vertexModel[id].Center = center.Value;
                            var attached = _vertexModel[id].AttachedEdgeIds.ToArray();
                            foreach (var edgeId in attached) {
                                var edge = _edgeModel.FirstOrDefault(x => x.DrawingObjectId == edgeId);
                                if (edge != null) {
                                    UpdateEdgeGeometry(edge, tr, db);
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
                                if (foundLabel == null) { foundLabel = tv.Value.ToString(); continue; }
                                if (foundUid == null) { foundUid = tv.Value.ToString(); break; }
                            }
                        }

                        if (!string.IsNullOrEmpty(foundUid)) {
                            var pair = _vertexModel.FirstOrDefault(kv => kv.Value.Uid == foundUid);
                            if (!pair.Equals(default(KeyValuePair<ObjectId, GraphVertex>))) {
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
                                        try { UpdateEdgeGeometry(edge, tr, db); AttachEdgeXData(tr, db, edge.DrawingObjectId, edge.StartVertexId, edge.EndVertexId); } catch { }
                                    }
                                }
                            }
                        }
                    }
                    tr.Commit();
                }
            } catch { }
        }

        internal static void CleanupBuildProcess() {
            _isBuilding = false;
            _currentVertices.Clear();
            _lastVertex = ObjectId.Null;
        }

        internal static void AddNewVertex() {
            VertexCommands.CreateOrSelectVertex();
        }

        internal static void ChangeColor() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var targetOpts = new PromptKeywordOptions("\nИзменить цвет [Рёбра(E)/Вершина(V)]: ");
            targetOpts.Keywords.Add("E");
            targetOpts.Keywords.Add("V");
            targetOpts.AllowNone = false;
            var targetRes = ed.GetKeywords(targetOpts);
            if (targetRes.Status != PromptStatus.OK) return;

            var colorOpts = new PromptKeywordOptions("\nВыберите цвет [Синий/Красный/Зелёный/Чёрный]: ");
            colorOpts.Keywords.Add("Синий");
            colorOpts.Keywords.Add("Красный");
            colorOpts.Keywords.Add("Зелёный");
            colorOpts.Keywords.Add("Чёрный");
            colorOpts.AllowNone = false;
            var colorRes = ed.GetKeywords(colorOpts);
            if (colorRes.Status != PromptStatus.OK) return;

            Teigha.Colors.Color col = Teigha.Colors.Color.FromRgb(0, 0, 0);
            switch (colorRes.StringResult) {
                case "Синий": col = Teigha.Colors.Color.FromRgb(0, 0, 255); break;
                case "Красный": col = Teigha.Colors.Color.FromRgb(255, 0, 0); break;
                case "Зелёный": col = Teigha.Colors.Color.FromRgb(0, 255, 0); break;
                case "Чёрный": col = Teigha.Colors.Color.FromRgb(0, 0, 0); break;
            }

            using (var tr = db.TransactionManager.StartTransaction()) {
                if (targetRes.StringResult == "E") {
                    foreach (var edge in _edgeModel.ToArray()) {
                        try {
                            var ent = tr.GetObject(edge.DrawingObjectId, OpenMode.ForWrite) as Entity;
                            if (ent != null) ent.Color = col;
                        } catch { }
                    }
                } else {
                    var selOpts = new PromptEntityOptions("\nВыберите вершину: ");
                    selOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
                    selOpts.AddAllowedClass(typeof(Circle), false);
                    selOpts.AddAllowedClass(typeof(Polyline), false);
                    var selRes = ed.GetEntity(selOpts);
                    if (selRes.Status != PromptStatus.OK) { tr.Commit(); return; }
                    var id = selRes.ObjectId;
                    try {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null) ent.Color = col;
                    } catch { }
                }
                tr.Commit();
            }
        }

        internal static void ChangeShape() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var selOpts = new PromptEntityOptions("\nВыберите вершину для изменения формы: ");
            selOpts.SetRejectMessage("\nВыбранный объект не поддержуется.");
            selOpts.AddAllowedClass(typeof(Circle), false);
            selOpts.AddAllowedClass(typeof(Polyline), false);
            var selRes = ed.GetEntity(selOpts);
            if (selRes.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var oldId = selRes.ObjectId;
                if (!_vertexModel.ContainsKey(oldId)) { tr.Commit(); return; }

                var center = GetEntityCenter(tr, oldId);
                if (center == null) { tr.Commit(); return; }

                // Read existing label/uid from XData
                string? oldUid = null;
                string? oldLabel = null;
                try {
                    var ent = tr.GetObject(oldId, OpenMode.ForRead) as Entity;
                    if (ent != null && ent.XData != null) {
                        bool isVertex = false;
                        foreach (TypedValue tv in ent.XData) {
                            if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName && tv.Value.ToString() == VERTEX_APP) { isVertex = true; continue; }
                            if (isVertex && tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString) {
                                if (oldLabel == null) { oldLabel = tv.Value.ToString(); continue; }
                                if (oldUid == null) { oldUid = tv.Value.ToString(); break; }
                            }
                        }
                    }
                } catch { }

                // Choose new style
                var styleOpts = new PromptKeywordOptions("\nВыберите форму [Синий круг/Красный треугольник]: ");
                styleOpts.Keywords.Add("Синий круг");
                styleOpts.Keywords.Add("Красный треугольник");
                styleOpts.AllowNone = false;
                var styleRes = ed.GetKeywords(styleOpts);
                if (styleRes.Status != PromptStatus.OK) { tr.Commit(); return; }

                bool isCircle = styleRes.StringResult == "Синий круг";

                // Create replacement vertex
                var newId = CreateVertexEntity(db, tr, center.Value, isCircle, oldLabel ?? string.Empty);
                if (newId == ObjectId.Null) { tr.Commit(); return; }

                // Overwrite UID in XData to preserve mapping
                try { AttachLabelXData(tr, db, newId, oldLabel ?? string.Empty, oldUid); } catch { }

                // Preserve color
                try { var newEnt = tr.GetObject(newId, OpenMode.ForWrite) as Entity; var oldEnt = tr.GetObject(oldId, OpenMode.ForRead) as Entity; if (newEnt != null && oldEnt != null) newEnt.Color = oldEnt.Color; } catch { }

                // Update model: replace id and update edges
                var dto = _vertexModel[oldId];
                dto.DrawingObjectId = newId;
                dto.IsErased = false;
                _vertexModel.Remove(oldId);
                _vertexModel[newId] = dto;
                if (_currentVertices.Contains(oldId)) { _currentVertices.Remove(oldId); _currentVertices.Add(newId); }

                var attached = dto.AttachedEdgeIds.ToArray();
                foreach (var edgeId in attached) {
                    var edge = _edgeModel.FirstOrDefault(x => x.DrawingObjectId == edgeId);
                    if (edge != null) {
                        if (edge.StartVertexId == oldId) edge.SetVertices(newId, edge.EndVertexId);
                        if (edge.EndVertexId == oldId) edge.SetVertices(edge.StartVertexId, newId);
                        try { UpdateEdgeGeometry(edge, tr, db); AttachEdgeXData(tr, db, edge.DrawingObjectId, edge.StartVertexId, edge.EndVertexId); } catch { }
                    }
                }

                // Erase old entity
                try { var oldEnt2 = tr.GetObject(oldId, OpenMode.ForWrite) as Entity; oldEnt2?.Erase(); } catch { }

                tr.Commit();
            }
        }

        internal static void DeleteElement() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            var selOpts = new PromptEntityOptions("\nВыберите элемент для удаления: ");
            selOpts.SetRejectMessage("\nВыбранный объект не поддерживается.");
            selOpts.AddAllowedClass(typeof(Circle), false);
            selOpts.AddAllowedClass(typeof(Polyline), false);
            var selRes = ed.GetEntity(selOpts);
            if (selRes.Status != PromptStatus.OK) return;

            using (var tr = db.TransactionManager.StartTransaction()) {
                var id = selRes.ObjectId;
                if (_vertexModel.ContainsKey(id)) {
                    // remove attached edges
                    var v = _vertexModel[id];
                    var attached = v.AttachedEdgeIds.ToArray();
                    foreach (var edgeId in attached) {
                        try {
                            var ent = tr.GetObject(edgeId, OpenMode.ForWrite) as Entity;
                            ent?.Erase();
                        } catch { }
                        var edgeObj = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == edgeId);
                        if (edgeObj != null) _edgeModel.Remove(edgeObj);
                    }

                    // remove vertex
                    try { var vent = tr.GetObject(id, OpenMode.ForWrite) as Entity; vent?.Erase(); } catch { }
                    _vertexModel.Remove(id);
                    _currentVertices.Remove(id);
                } else {
                    var edgeObj = _edgeModel.FirstOrDefault(e => e.DrawingObjectId == id);
                    if (edgeObj != null) {
                        try { var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity; ent?.Erase(); } catch { }
                        if (_vertexModel.TryGetValue(edgeObj.StartVertexId, out var sv)) sv.RemoveAttachedEdge(id);
                        if (_vertexModel.TryGetValue(edgeObj.EndVertexId, out var ev)) ev.RemoveAttachedEdge(id);
                        _edgeModel.Remove(edgeObj);
                    }
                }

                tr.Commit();
            }
        }

        [CommandMethod("UGB_VERTEX")]
        public static void CreateVertexCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null) SubscribeToDatabaseEvents(doc.Database);
            VertexCommands.CreateOrSelectVertex();
        }

        [CommandMethod("UGB_BUILD")]
        public static void BuildGraphCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null) SubscribeToDatabaseEvents(doc.Database);
            BuildCommands.StartBuildMode();
        }

        [CommandMethod("UGB_EDIT")]
        public static void EditGraphCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null) SubscribeToDatabaseEvents(doc.Database);
            EditCommands.OpenEditMenu();
        }

        [CommandMethod("UGB_PATH")]
        public static void FindPathCommand() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null) SubscribeToDatabaseEvents(doc.Database);
            PathCommands.FindAndHighlightPath();
        }
    }
}
