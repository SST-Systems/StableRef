#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    internal sealed class StableRefSelectorSettingsPopup : EditorWindow
    {
        private StableRefSelectorWindow _owner;
        private float _w;
        private float _h;

        private static readonly Color _borderColor = new(0.1f, 0.1f, 0.1f, 1f);

        internal static void Show(Rect screenPos, StableRefSelectorWindow owner)
        {
            var win = CreateInstance<StableRefSelectorSettingsPopup>();
            win._owner = owner;
            win._w = StableRefSelectorWindow.SavedW;
            win._h = StableRefSelectorWindow.SavedH;
            win.ShowAsDropDown(screenPos, new Vector2(180f, 116f));
        }

        private void OnGUI()
        {
            const int Pad = 8;
            GUILayout.BeginArea(new Rect(Pad, Pad, position.width - Pad * 2, position.height - Pad * 2));

            EditorGUIUtility.labelWidth = 52f;
            _w = Mathf.Max(StableRefSelectorWindow.MinW, EditorGUILayout.FloatField("Width", _w));
            _h = Mathf.Max(StableRefSelectorWindow.MinH, EditorGUILayout.FloatField("Height", _h));

            GUILayout.Space(4f);
            EditorGUIUtility.labelWidth = 120f;
            StableRefSelectorWindow.ShowCategoryInLabel = EditorGUILayout.Toggle(
                "Category in Label", StableRefSelectorWindow.ShowCategoryInLabel);

            GUILayout.Space(6f);
            EditorGUIUtility.labelWidth = 52f;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", EditorStyles.miniButton)) { _owner?.ApplySettings(_w, _h); Close(); }
            if (GUILayout.Button("Reset", EditorStyles.miniButton)) { _owner?.ResetSettings(); Close(); }
            EditorGUILayout.EndHorizontal();

            GUILayout.EndArea();

            if (Event.current.type == EventType.Repaint)
            {
                float w = position.width, h = position.height;
                EditorGUI.DrawRect(new Rect(0, 0, w, 1), _borderColor);
                EditorGUI.DrawRect(new Rect(0, h - 1, w, 1), _borderColor);
                EditorGUI.DrawRect(new Rect(0, 0, 1, h), _borderColor);
                EditorGUI.DrawRect(new Rect(w - 1, 0, 1, h), _borderColor);
            }
        }
    }
}
#endif