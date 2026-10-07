#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Draws the fields of a value inside the StableRef type selector, in place of the default one
    /// <c>EditorGUI.PropertyField</c> per child. Install it through <see cref="StableRefDrawing.ChildrenDrawer"/> to
    /// route the children through another inspector framework, so its attributes work inside StableRef values too.
    /// </summary>
    /// <remarks>
    /// Called for an expanded field holding a value of a single type — never for an empty, missing or mixed
    /// (multi-object, different types) field; the selector, the missing-entry state, multi-object editing and the
    /// context menu stay with StableRef. <paramref name="valueProperty"/> is the managed reference (the <c>Value</c>
    /// of a <see cref="StableRef{T}"/> entry, or a <see cref="RefSelectorAttribute"/> field); draw its children, not
    /// the property itself. <c>EditorGUI.indentLevel</c> is already raised by one. Both methods run on every repaint,
    /// and the height must match what <see cref="DrawChildren"/> uses. When the height changes because of the
    /// drawer's own state (its foldouts, tabs), call <see cref="StableRefDrawing.InvalidateLayout"/> so StableRef
    /// lists measure their elements again. Edit values through the property (<c>EditorGUI</c> controls) so the edit
    /// sets <c>GUI.changed</c>: that is what makes StableRef refresh the entry's recovery snapshot — a value changed
    /// behind the GUI's back (written to the object directly) keeps the old snapshot until the next change or
    /// Resync.
    /// </remarks>
    public interface IStableRefChildrenDrawer
    {
        /// <summary>Total height of the children of <paramref name="valueProperty"/>.</summary>
        float GetChildrenHeight(SerializedProperty valueProperty);

        /// <summary>Draws the children of <paramref name="valueProperty"/> inside <paramref name="position"/>.</summary>
        void DrawChildren(Rect position, SerializedProperty valueProperty);
    }
}
#endif
