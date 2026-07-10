#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    public sealed class StableRefSelectorWindow : EditorWindow
    {
        private const float ToolbarH = 22f;
        private const float RowH = 18f;
        private const float Indent = 10f;
        private const float TextX = 18f;

        private const string PrefW = "StableRefSelectorWindow_W";
        private const string PrefH = "StableRefSelectorWindow_H";
        private const string PrefShowCategory = "StableRefSelectorWindow_ShowCategory";

        private const float DefW = 280f;
        private const float DefH = 350f;
        internal const float MinW = 160f;
        internal const float MinH = 120f;
        private const float MaxW = 800f;
        private const float MaxH = 800f;

        internal static float SavedW
        {
            get => EditorPrefs.GetFloat(PrefW, DefW);
            set => EditorPrefs.SetFloat(PrefW, Mathf.Clamp(value, MinW, MaxW));
        }
        internal static float SavedH
        {
            get => EditorPrefs.GetFloat(PrefH, DefH);
            set => EditorPrefs.SetFloat(PrefH, Mathf.Clamp(value, MinH, MaxH));
        }

        public static bool ShowCategoryInLabel
        {
            get => EditorPrefs.GetBool(PrefShowCategory, true);
            set => EditorPrefs.SetBool(PrefShowCategory, value);
        }

        private SerializedProperty _property;
        private StableRefPropertyUtils.TypeEntry[] _entries;
        private string _search = "";
        private int _hoveredIndex = -1;
        private Vector2 _scroll;
        private bool _doFocusSearch = true;
        private float _contentH;
        private float _contentW;
        private Rect _anchorScreen;

        private readonly HashSet<string> _collapsed = new();
        private readonly List<RowItem> _rows = new();
        private readonly Dictionary<string, (List<StableRefPropertyUtils.TypeEntry> items, int depth)> _pendingDirectItems = new();

        private static readonly GUIContent _tempContent = new();

        private struct RowItem
        {
            public bool IsHeader;
            public bool IsNone;
            public string Label;
            public string CategoryPath;
            public StableRefPropertyUtils.TypeEntry Entry;
            public int Depth;
        }

        private static GUIStyle _toolbarSearch;
        private static GUIStyle _toolbarSearchCancel;
        private static GUIStyle _toolbarSearchCancelEmpty;
        private static GUIStyle _selectionRect;
        private static GUIStyle _prDisabledLabel;
        private static GUIStyle _greyBorder;
        private static GUIContent _gearContent;
        private static Color _borderColor;
        private static Color _separatorColor;
        private static Color _headerLineColor;
        private static Color _currentColor;
        private static bool _stylesReady;

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _toolbarSearch = GUI.skin.FindStyle("ToolbarSearchTextField")
                ?? GUI.skin.FindStyle("ToolbarSeachTextField")
                ?? EditorStyles.toolbarSearchField;

            _toolbarSearchCancel = GUI.skin.FindStyle("ToolbarSearchCancelButton")
                ?? GUI.skin.FindStyle("ToolbarSeachCancelButton")
                ?? GUIStyle.none;

            _toolbarSearchCancelEmpty = GUI.skin.FindStyle("ToolbarSearchCancelButtonEmpty")
                ?? GUI.skin.FindStyle("ToolbarSeachCancelButtonEmpty")
                ?? GUIStyle.none;

            _selectionRect = GUI.skin.FindStyle("SelectionRect") ?? GUI.skin.box;
            _prDisabledLabel = GUI.skin.FindStyle("PR DisabledLabel") ?? EditorStyles.centeredGreyMiniLabel;
            _greyBorder = GUI.skin.FindStyle("grey_border") ?? GUIStyle.none;
            _gearContent = EditorGUIUtility.IconContent("d_Settings") ?? new GUIContent("=");

            _borderColor = new Color(0.10f, 0.10f, 0.10f, 1.00f);
            _separatorColor = new Color(0.00f, 0.00f, 0.00f, 0.30f);
            _currentColor = new Color(1.00f, 1.00f, 1.00f, 0.08f);
            _headerLineColor = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.10f)
                : new Color(0f, 0f, 0f, 0.15f);
        }

        public static void Show(Rect btnRect, SerializedProperty property, StableRefPropertyUtils.TypeEntry[] entries)
        {
            var win = CreateInstance<StableRefSelectorWindow>();
            win._property = property;
            win._entries = entries;
            win.RebuildRows();

            float w = SavedW;
            float h = SavedH;
            var screen = GUIUtility.GUIToScreenPoint(new Vector2(btnRect.x, btnRect.yMax));
            win._anchorScreen = new Rect(screen.x, screen.y, w, 0f);
            win.wantsMouseMove = true;
            win.ShowAsDropDown(win._anchorScreen, new Vector2(w, h));
        }

        private void OnGUI()
        {
            if (_property == null) { Close(); return; }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            { Close(); Event.current.Use(); return; }

            EnsureStyles();
            DrawToolbar();
            DrawItems();

            if (Event.current.type == EventType.Repaint)
            {
                float w = position.width, h = position.height;
                _greyBorder.Draw(new Rect(0, 0, w, h), false, false, false, false);
                EditorGUI.DrawRect(new Rect(0, 0, w, 1), _borderColor);
                EditorGUI.DrawRect(new Rect(0, h - 1, w, 1), _borderColor);
                EditorGUI.DrawRect(new Rect(0, 0, 1, h), _borderColor);
                EditorGUI.DrawRect(new Rect(w - 1, 0, 1, h), _borderColor);
            }

            if (Event.current.type == EventType.MouseMove)
                Repaint();
        }

        private void DrawToolbar()
        {
            GUI.Box(new Rect(0, 0, position.width, ToolbarH), GUIContent.none, EditorStyles.toolbar);

            const float GearW = 20f;
            float cancelW = Mathf.Max(_toolbarSearchCancel.fixedWidth, 14f);
            var gear = new Rect(position.width - GearW - 2f, 2f, GearW, ToolbarH - 4f);
            var cancel = new Rect(gear.x - cancelW, 2f, cancelW, ToolbarH - 4f);
            var field = new Rect(4f, 2f, cancel.x - 6f, ToolbarH - 4f);

            if (GUI.Button(gear, _gearContent, EditorStyles.iconButton))
            {
                var screenPos = GUIUtility.GUIToScreenPoint(new Vector2(gear.x, gear.yMax));
                StableRefSelectorSettingsPopup.Show(new Rect(screenPos, Vector2.zero), this);
            }

            GUI.SetNextControlName("StableRefSearch");
            EditorGUI.BeginChangeCheck();
            string next = GUI.TextField(field, _search, _toolbarSearch);
            if (EditorGUI.EndChangeCheck())
            {
                _search = next;
                _hoveredIndex = -1;
                _scroll = Vector2.zero;
                RebuildRows();
                Repaint();
            }

            bool empty = string.IsNullOrEmpty(_search);
            if (GUI.Button(cancel, GUIContent.none, empty ? _toolbarSearchCancelEmpty : _toolbarSearchCancel) && !empty)
            {
                _search = "";
                _hoveredIndex = -1;
                _scroll = Vector2.zero;
                RebuildRows();
                GUI.FocusControl("StableRefSearch");
                Repaint();
            }

            if (_doFocusSearch && Event.current.type == EventType.Repaint)
            {
                _doFocusSearch = false;
                EditorApplication.delayCall += () =>
                {
                    if (this && focusedWindow == this) GUI.FocusControl("StableRefSearch");
                };
            }
        }

        private void DrawItems()
        {
            float listY = ToolbarH + 1f;
            const float Border = 1f;
            float listH = position.height - listY - Border;
            float viewW = position.width - Border;

            float vertScrollW = GUI.skin.verticalScrollbar.fixedWidth + 2f;
            float horizScrollH = GUI.skin.horizontalScrollbar.fixedHeight + 2f;

            bool needV = _contentH > listH;
            float availW = viewW - (needV ? vertScrollW : 0f);
            bool needH = _contentW > availW;
            float availH = listH - (needH ? horizScrollH : 0f);
            needV = _contentH > availH;
            availW = viewW - (needV ? vertScrollW : 0f);

            float contentW = Mathf.Max(_contentW, availW);
            float contentH = Mathf.Max(_contentH, availH);

            EditorGUI.DrawRect(new Rect(0, listY - 1, position.width, 1), _separatorColor);

            _scroll = GUI.BeginScrollView(
                new Rect(0, listY, viewW, listH),
                _scroll,
                new Rect(0, listY, contentW, contentH),
                needH ? GUI.skin.horizontalScrollbar : GUIStyle.none,
                needV ? GUI.skin.verticalScrollbar : GUIStyle.none);

            int clicked = -1;
            float y = listY;
            var mp = Event.current.mousePosition;
            Type currentType = _property.managedReferenceValue?.GetType();

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                DrawRow(ref row, i, new Rect(0, y, contentW, RowH), mp, currentType, ref clicked);
                y += RowH;
            }

            GUI.EndScrollView();

            if (clicked >= 0) SelectRow(_rows[clicked]);
            HandleKeyboard();
        }

        private void DrawRow(ref RowItem row, int index, Rect r, Vector2 mp, Type currentType, ref int clicked)
        {
            if (row.IsHeader)
            {
                EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), _headerLineColor);

                float ix = r.x + 6f + row.Depth * Indent;
                bool collapsed = _collapsed.Contains(row.CategoryPath);
                EditorGUI.LabelField(new Rect(ix, r.y + 1f, 14f, r.height - 1f), collapsed ? "►" : "▼", _prDisabledLabel);
                EditorGUI.LabelField(new Rect(ix + 14f, r.y + 1f, r.width - ix - 14f, r.height - 1f), row.Label, _prDisabledLabel);

                if (r.Contains(mp) && Event.current.type == EventType.MouseDown)
                {
                    bool recursive = Event.current.alt;

                    if (collapsed)
                    {
                        _collapsed.Remove(row.CategoryPath);
                        if (recursive) ExpandRecursive(row.CategoryPath);
                    }
                    else
                    {
                        _collapsed.Add(row.CategoryPath);
                        if (recursive) CollapseRecursive(row.CategoryPath);
                    }

                    RebuildRows();
                    Event.current.Use();
                    Repaint();
                }
                return;
            }

            bool inR = r.Contains(mp);
            bool current = !row.IsNone && currentType != null && currentType == row.Entry?.Type;

            if (inR) _hoveredIndex = index;

            if (Event.current.type == EventType.Repaint)
            {
                if (inR) _selectionRect.Draw(r, false, false, true, true);
                else if (current) EditorGUI.DrawRect(r, _currentColor);
            }

            EditorGUI.LabelField(
                new Rect(r.x + TextX + row.Depth * Indent, r.y, r.width - TextX - row.Depth * Indent - 2f, r.height),
                row.Label, (inR || current) ? EditorStyles.whiteLabel : EditorStyles.label);

            if (inR && Event.current.type == EventType.MouseDown)
            { clicked = index; Event.current.Use(); }
        }

        internal void ApplySettings(float w, float h)
        {
            SavedW = w;
            SavedH = h;
            ResizeWindow(w, h);
        }

        internal void ResetSettings()
        {
            SavedW = DefW;
            SavedH = DefH;
            ResizeWindow(DefW, DefH);
        }

        private void ResizeWindow(float w, float h)
        {
            var anchor = new Rect(_anchorScreen.x, _anchorScreen.y, w, 0f);
            var property = _property;
            var entries = _entries;
            var search = _search;
            var collapsed = new HashSet<string>(_collapsed);

            EditorApplication.delayCall += () =>
            {
                Close();
                var win = CreateInstance<StableRefSelectorWindow>();
                win._property = property;
                win._entries = entries;
                win._search = search;
                win._anchorScreen = anchor;
                win._collapsed.UnionWith(collapsed);
                win.wantsMouseMove = true;
                win.RebuildRows();
                win.ShowAsDropDown(anchor, new Vector2(w, h));
            };
        }

        private void HandleKeyboard()
        {
            if (Event.current.type != EventType.KeyDown) return;

            int count = _rows.Count;
            int cur = -1;
            int selCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (_rows[i].IsHeader) continue;
                if (i == _hoveredIndex) cur = selCount;
                selCount++;
            }

            if (selCount == 0) return;

            int nextSel;
            switch (Event.current.keyCode)
            {
                case KeyCode.DownArrow: nextSel = Mathf.Clamp(cur + 1, 0, selCount - 1); break;
                case KeyCode.UpArrow: nextSel = cur < 0 ? selCount - 1 : Mathf.Clamp(cur - 1, 0, selCount - 1); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_hoveredIndex >= 0 && _hoveredIndex < count) SelectRow(_rows[_hoveredIndex]);
                    Event.current.Use();
                    return;
                default: return;
            }

            int found = 0;
            for (int i = 0; i < count; i++)
            {
                if (_rows[i].IsHeader) continue;
                if (found == nextSel) { _hoveredIndex = i; break; }
                found++;
            }

            EnsureVisible(_hoveredIndex);
            Event.current.Use();
            Repaint();
        }

        private void EnsureVisible(int index)
        {
            float y = index * RowH;
            float listH = position.height - ToolbarH - 1f;
            _scroll.y = Mathf.Clamp(_scroll.y, y - listH + RowH, y);
        }

        private void SelectRow(RowItem row)
        {
            if (row.IsHeader) return;

            string path = _property.propertyPath;
            bool expand = !row.IsNone;

            foreach (var target in _property.serializedObject.targetObjects)
            {
                var so = new SerializedObject(target);
                so.Update();
                var prop = so.FindProperty(path);
                if (prop == null) continue;
                prop.managedReferenceValue = row.IsNone ? null : Activator.CreateInstance(row.Entry.Type);
                prop.isExpanded = expand;
                so.ApplyModifiedProperties();
            }

            Close();
        }

        private void RebuildRows()
        {
            _rows.Clear();
            _rows.Add(new RowItem { IsNone = true, Label = "None" });

            if (_entries == null) { _contentH = RowH; _contentW = 0f; return; }

            if (!string.IsNullOrWhiteSpace(_search))
            {
                string q = _search.ToLowerInvariant();
                foreach (var e in _entries)
                    if (e.FullPathLower.Contains(q))
                        _rows.Add(new RowItem { Entry = e, Label = e.Name });
            }
            else
            {
                var byCat = new SortedDictionary<string, List<StableRefPropertyUtils.TypeEntry>>(StringComparer.Ordinal);
                var noCat = new List<StableRefPropertyUtils.TypeEntry>();

                foreach (var e in _entries)
                {
                    if (string.IsNullOrEmpty(e.Category)) noCat.Add(e);
                    else
                    {
                        if (!byCat.TryGetValue(e.Category, out var list))
                            byCat[e.Category] = list = new List<StableRefPropertyUtils.TypeEntry>();
                        list.Add(e);
                    }
                }

                foreach (var (cat, list) in byCat)
                {
                    string[] segs = cat.Split('/');
                    bool parentCollapsed = false;

                    for (int d = 0; d < segs.Length; d++)
                    {
                        if (parentCollapsed) break;
                        string segPath = d == 0 ? segs[0] : cat.Substring(0, IndexOfNthSlash(cat, d + 1));

                        bool dup = _rows.Exists(r => r.IsHeader && r.CategoryPath == segPath);
                        if (!dup)
                            _rows.Add(new RowItem { IsHeader = true, Label = segs[d], Depth = d, CategoryPath = segPath });

                        if (_collapsed.Contains(segPath)) parentCollapsed = true;
                    }

                    if (!parentCollapsed && !_collapsed.Contains(cat))
                    {
                        bool hasSubcategories = false;
                        foreach (var otherCat in byCat.Keys)
                            if (otherCat != cat && otherCat.StartsWith(cat + "/"))
                            { hasSubcategories = true; break; }

                        if (hasSubcategories)
                        {
                            _pendingDirectItems[cat] = (list, segs.Length);
                        }
                        else
                        {
                            foreach (var e in list)
                                _rows.Add(new RowItem { Entry = e, Label = e.Name, Depth = segs.Length });

                            string parentCat = segs.Length > 1 ? cat.Substring(0, cat.LastIndexOf('/')) : null;
                            if (parentCat != null && _pendingDirectItems.TryGetValue(parentCat, out var pending))
                            {
                                foreach (var e in pending.items)
                                    _rows.Add(new RowItem { Entry = e, Label = e.Name, Depth = pending.depth });
                                _pendingDirectItems.Remove(parentCat);
                            }
                        }
                    }
                }

                foreach (var (_, pending) in _pendingDirectItems)
                    foreach (var e in pending.items)
                        _rows.Add(new RowItem { Entry = e, Label = e.Name, Depth = pending.depth });
                _pendingDirectItems.Clear();

                foreach (var e in noCat)
                    _rows.Add(new RowItem { Entry = e, Label = e.Name, Depth = 0 });
            }

            _contentH = 0f;
            _contentW = 0f;
            for (int i = 0; i < _rows.Count; i++)
            {
                _contentH += RowH;
                _tempContent.text = _rows[i].Label;
                float textX = _rows[i].IsHeader ? 6f + _rows[i].Depth * Indent + 14f : TextX + _rows[i].Depth * Indent;
                float w = textX + EditorStyles.label.CalcSize(_tempContent).x + 4f;
                if (w > _contentW) _contentW = w;
            }
        }

        private void CollapseRecursive(string root)
        {
            if (_entries == null) return;
            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.Category)) continue;
                string cat = e.Category;
                if (cat != root && !cat.StartsWith(root + "/", StringComparison.Ordinal)) continue;

                string[] segs = cat.Split('/');
                for (int d = 0; d < segs.Length; d++)
                {
                    string segPath = d == 0 ? segs[0] : cat.Substring(0, IndexOfNthSlash(cat, d + 1));
                    if (segPath == root || segPath.StartsWith(root + "/", StringComparison.Ordinal))
                        _collapsed.Add(segPath);
                }
            }
        }

        private void ExpandRecursive(string root)
        {
            _collapsed.RemoveWhere(p =>
                p == root || p.StartsWith(root + "/", StringComparison.Ordinal));
        }

        private static int IndexOfNthSlash(string s, int n)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '/' && ++count == n) return i;
            return s.Length;
        }
    }
}
#endif