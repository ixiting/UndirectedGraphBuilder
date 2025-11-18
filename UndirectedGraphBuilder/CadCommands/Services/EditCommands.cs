using HostMgd.ApplicationServices;
using HostMgd.EditorInput;

namespace UndirectedGraphBuilder.CadCommands.Services {
    internal static class EditCommands {
        internal static void OpenEditMenu() {
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
                    Commands.AddNewVertex();
                    break;
                case "C":
                    Commands.ChangeColor();
                    break;
                case "F":
                    Commands.ChangeShape();
                    break;
                case "D":
                    Commands.DeleteElement();
                    break;
            }
        }
    }
}
