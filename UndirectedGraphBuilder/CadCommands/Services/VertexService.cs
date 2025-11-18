using System;

using HostMgd.ApplicationServices;

namespace UndirectedGraphBuilder.CadCommands.Services {
    internal static class VertexService {

        internal static void EnsureUidForAll() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            using (var tr = db.TransactionManager.StartTransaction()) {
                int written = 0;
                foreach (var kv in Commands._vertexModel.ToList()) {
                    var id = kv.Key;
                    var v = kv.Value;
                    if (string.IsNullOrEmpty(v.Uid) || v.Uid.Trim() == string.Empty) {
                        try {
                            Commands.AttachLabelXData(tr, db, id, v.Label);
                            written++;
                        } catch { }
                    }
                }

                tr.Commit();
                ed.WriteMessage($"\n[UGB] Ensured UIDs for {written} vertices.\n");
            }
        }
    }
}
