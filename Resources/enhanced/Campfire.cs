using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HSAEnhanced.Core;
#if !WITHOUT_HSA
using Accessibility;
#endif
using Hearthstone;
using Hearthstone.DataModels;
using Hearthstone.UI;
using UnityEngine;

namespace HSAEnhanced
{
    // The Mercenaries village Campfire (the visitors' task board): its tasks are cards whose texts the
    // screen reader never reached (the fallback menu had only the board's title and tip). Each task is
    // read from the data model its card shows: the visitor, the task, its text, the progress, the time
    // left of an event, the rewards; Enter opens its details as a click on the card does.
    static class Campfire
    {
        // the tasks of a task board under `root`; null when there is none
        internal static List<GameButton> Tasks(GameObject root)
        {
            var board = root == null ? null : root.GetComponentInChildren<LettuceVillageTaskBoard>();
            if (board == null) return null;
            var found = new List<GameButton>();
            foreach (var card in root.GetComponentsInChildren<LettuceVillageTaskItemCard>(false))
            {
                var widget = card.GetComponentInParent<WidgetTemplate>();
                var task = widget == null ? null : TaskOf(widget);
                if (task == null || string.IsNullOrEmpty(task.Title)) continue;
                var label = Describe(task);
                if (label.Length == 0) continue;
                var click = widget.GetComponentInChildren<Clickable>();
                var press = click != null ? Ui.ClickOf(click) : null;
                found.Add(new GameButton { Target = card, Label = label, Click = press ?? (() => { }) });     // (the label is what is read)
            }
            Ui.SortByScreen(found);
            // no card gave its task: the board's own list, read only
            var model = found.Count > 0 ? null : Ref.Get<MercenaryVillageTaskBoardDataModel>(board, "m_dataModel");
            if (model != null && model.TaskListRow != null)
                foreach (var row in model.TaskListRow)
                    if (row != null && row.TaskList != null)
                        foreach (var task in row.TaskList)
                        {
                            if (task == null || string.IsNullOrEmpty(task.Title)) continue;
                            var label = Describe(task);
                            if (label.Length > 0) found.Add(new GameButton { Target = board, Label = label, Click = () => { } });
                        }
            Log.Once("campfire: " + found.Count + " tasks");
            return found;
        }

        // the task data model the card's widget is bound to
        static MercenaryVillageTaskItemDataModel TaskOf(WidgetTemplate widget)
        {
            try
            {
                foreach (var m in widget.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.Name != "GetDataModel") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2 || ps[0].ParameterType != typeof(int) || !ps[1].IsOut) continue;
                    var args = new object[] { 309, null };     // MercenaryVillageTaskItemDataModel.DataModelId
                    m.Invoke(widget, args);
                    return args[1] as MercenaryVillageTaskItemDataModel;
                }
            }
            catch (Exception e) { Log.Error(e); }
            return null;
        }

        // as the card shows it: the visitor, the task, its text, the progress, time left, the rewards
        internal static string Describe(MercenaryVillageTaskItemDataModel task)
        {
            var progress = !string.IsNullOrEmpty(task.ProgressMessage) ? Str.Clean(task.ProgressMessage)
                         : task.ProgressNeeded > 0 ? task.Progress + "/" + task.ProgressNeeded : null;
            var rewards = new List<string>();
            if (task.RewardList != null) Mail.Collect(task.RewardList, rewards, 0);
            return Str.Join(Str.Clean(task.MercenaryName), Str.Clean(task.Title), Str.Clean(task.Description), progress,
                task.IsTimedEvent ? Str.Clean(task.RemainingEventTime) : null, rewards.Count == 0 ? null : Str.Join(rewards.ToArray()));
        }
    }
}
