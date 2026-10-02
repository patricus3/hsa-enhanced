#if WITHOUT_HSA
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HSAEnhanced.Core
{
    // Without Hearthstone Access: the game's dialog on screen (read by Generic)
    static class Dialogs
    {
        static readonly FieldInfo Current = Ref.Field(typeof(DialogManager), "m_currentDialog");

        internal static DialogBase Shown
        {
            get
            {
                var mgr = DialogManager.Get();
                var d = mgr == null || Current == null ? null : Current.GetValue(mgr) as DialogBase;
                return d != null && d.gameObject.activeInHierarchy && d.IsShown() ? d : null;
            }
        }
    }
}
#endif
