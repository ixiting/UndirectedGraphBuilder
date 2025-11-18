using HostMgd.ApplicationServices;
using HostMgd.EditorInput;
using Teigha.DatabaseServices;

namespace UndirectedGraphBuilder.CadCommands.Services {
    internal static class BuildCommands {
        internal static void StartBuildMode() {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            // Ask for default vertex style for build mode
            var styleOpts = new PromptKeywordOptions("\nВыберите стиль создаваемых вершин в режиме построения [Синий круг/Красный треугольник]: ");
            styleOpts.Keywords.Add("Синий круг");
            styleOpts.Keywords.Add("Красный треугольник");
            styleOpts.AllowNone = false;
            var styleRes = ed.GetKeywords(styleOpts);
            bool defaultIsCircle = true;
            if (styleRes.Status == PromptStatus.OK) defaultIsCircle = styleRes.StringResult == "Синий круг";

            Commands._isBuilding = true;
            Commands._currentVertices.Clear();
            Commands._lastVertex = ObjectId.Null;

            ed.WriteMessage("\nНачало построения графа. ESC - завершить");

            while (Commands._isBuilding) {
                var ppo = new PromptPointOptions("\nУкажите точку или выберите вершину [Отменить(U)]: ");
                ppo.Keywords.Add("U");
                var result = ed.GetPoint(ppo);

                if (result.Status == PromptStatus.Cancel) break;
                if (result.Status == PromptStatus.Keyword) {
                    if (result.StringResult == "U") Commands.UndoLastOperation();
                    continue;
                }

                if (result.Status == PromptStatus.OK) {
                    Commands.ProcessPoint(result.Value, defaultIsCircle);
                }
            }

            Commands.CleanupBuildProcess();
        }
    }
}
