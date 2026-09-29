using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Accessibility;

namespace HSAEnhanced
{
    // In-game messages (the mailbox, news, patch notes, Mercenaries messages). HSA's reader knows the
    // message contents of an older game (TextMessageContent, ShopMessageContent); the game now builds
    // data models for its popup (TextMessageContentDataModel, Launch..., Change..., Mercenary..., the
    // empty mailbox), so HSA skipped every message and the popup closed at once. The message is read
    // here from the data models the popup shows: its title and texts, and the items it lists.
    // Enter goes on to the next message as before (HSA's own reader).
    static class Mail
    {
        static readonly HashSet<string> Skip = new HashSet<string> { "IconType", "SubLayout", "Url", "DataModelDisplayName", "ImageType" };

        // start of HSA's MessagePopupDisplay.ReadMessage(data); true: read here
        internal static bool Read(object popup, object data)
        {
            var ui = popup as AccessibleUI;
            if (ui == null || data == null) return false;
            var factory = typeof(Hearthstone.InGameMessage.UI.MessagePopupDisplay).Assembly.GetType("Hearthstone.InGameMessage.UI.MessageDataModelFactory");
            var create = factory == null ? null : factory.GetMethod("CreateDataModel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var models = create == null ? null : create.Invoke(null, new[] { data }) as IEnumerable;
            if (models == null) return false;
            var lines = new List<string>();
            foreach (var model in models) Collect(model, lines, 0);
            Log.Info("message: " + (lines.Count == 0 ? "nothing to read" : string.Join(" | ", lines.ToArray())));
            if (lines.Count == 0) return false;
            AccessibilityMgr.ShowUI(ui);
            AccessibilityMgr.Output(ui, LocalizedText.UI_POPUP, true);
            foreach (var line in lines) AccessibilityMgr.Output(ui, line);
            return true;
        }

        // the texts of a data model, then those of the models it holds (the items, a launch effect)
        static void Collect(object model, List<string> lines, int depth)
        {
            if (model == null || depth > 3) return;
            foreach (var p in model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.GetIndexParameters().Length > 0 || Skip.Contains(p.Name)) continue;
                object v;
                try { v = p.GetValue(model, null); } catch { continue; }
                if (v == null) continue;
                if (v is string)
                {
                    var s = Str.Clean((string)v);
                    if (Ui.HasWords(s) && !lines.Contains(s)) lines.Add(s);
                }
                else if (v is Hearthstone.UI.IDataModel) Collect(v, lines, depth + 1);
                else if (v is IEnumerable && !(v is string))
                    foreach (var item in (IEnumerable)v) if (item is Hearthstone.UI.IDataModel) Collect(item, lines, depth + 1);
            }
        }
    }
}
