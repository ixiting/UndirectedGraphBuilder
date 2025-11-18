using HostMgd.ApplicationServices;

using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace UndirectedGraphBuilder.CadCommands.Services {
    internal static class GraphController {

        internal static void InitializeForDocument(Document doc) {
            try {
                if (doc == null) return;

                RestoreGraphFromDrawing(doc);
            } catch (Exception) {
                // ignored
            }
        }

        internal static void RestoreGraphFromDrawing(Document doc) {
            if (doc == null) return;

            Commands._currentVertices.Clear();
            Commands._vertexModel.Clear();
            Commands._edgeModel.Clear();
            Commands._lastVertex = ObjectId.Null;
            Commands._isBuilding = false;

            var db = doc.Database;
            using (var tr = db.TransactionManager.StartTransaction()) {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                using (var tr2 = db.TransactionManager.StartTransaction()) {
                    Commands.RegisterApplications(tr2, db);
                    tr2.Commit();
                }

                var regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                bool hasAnyApp = regAppTable.Has("UGB_VERTEX") || regAppTable.Has("UGB_EDGE");

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
                            if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName && tv.Value.ToString() == "UGB_VERTEX") {
                                isVertex = true;
                                int j = i + 1;
                                if (j < arr.Length && arr[j].TypeCode == (int)DxfCode.ExtendedDataRegAppName && arr[j].Value.ToString() == "UGB_APP") {
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
                                if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName && tv.Value.ToString() == "UGB_APP") {
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
                        Commands._vertexModel[id] = vertex;
                        Commands._currentVertices.Add(id);
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
                        if (value.TypeCode == (int)DxfCode.ExtendedDataRegAppName && value.Value.ToString() == "UGB_EDGE") {
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
                    if (isEdge && startId != ObjectId.Null && endId != ObjectId.Null && Commands._vertexModel.ContainsKey(startId) && Commands._vertexModel.ContainsKey(endId)) {
                        var edge = new CadObjects.GraphEdge();
                        edge.DrawingObjectId = id;
                        edge.SetVertices(startId, endId);
                        Commands._edgeModel.Add(edge);


                        if (Commands._vertexModel.ContainsKey(startId))
                            Commands._vertexModel[startId].AddAttachedEdge(id);
                        if (Commands._vertexModel.ContainsKey(endId))
                            Commands._vertexModel[endId].AddAttachedEdge(id);
                        doc.Editor.WriteMessage($"\n[UGB] Restored edge {id} connecting {startId}->{endId}\n");
                    }
                }

                tr.Commit();
            }


            try {
                Commands.SubscribeToDatabaseEvents(db);
            } catch { }
        }
    }
}
