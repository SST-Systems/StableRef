#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// The result tree of the tool windows (Find Usages, Fix Missing Types): a virtualized foldout list — only the rows
    /// in view are drawn, at a fixed height, from a flat list of visible rows that is rebuilt only when expansion, the
    /// filter or the results change. Mouse: click selects (the arrow or a double-click toggles, Alt — recursively);
    /// keyboard as in the type selector, see <see cref="HandleKeyboard"/>.
    /// </summary>
    internal sealed class StableRefResultTree
    {
        public enum NodeKind { Group, Asset, GameObject, Component, Item }

        public class Node
        {
            public NodeKind Kind;
            public string Label;
            public Texture Icon;
            public bool Expanded;
            /// <summary>Drawn in a dimmed text color.</summary>
            public bool Muted;
            public UnityEngine.Object PingTarget;
            public readonly List<Node> Children = new();
            internal GUIContent Content;
            internal string LabelLower;
        }

        private struct Row
        {
            public Node Node;
            public int Depth;
            /// <summary>Placeholder text of an expanded group with nothing to show (then <see cref="Node"/> is null).</summary>
            public string Message;
        }

        private const float Indent = 14f;
        private const float RowH = 20f;
        private const float HeaderBgAlpha = 0.08f;
        private const double TypingDelay = 0.2;

        /// <summary>A node was clicked (click count) or activated with Enter (1).</summary>
        public Action<Node, int> Clicked;
        /// <summary>Icon of a node resolved when it is first drawn; <see cref="Node.Icon"/> when not set or null.</summary>
        public Func<Node, Texture> ResolveIcon;
        /// <summary>
        /// Lower-case text the search filter matches besides a node's label (e.g. the script declaring its type), or
        /// null; return a string shared between nodes rather than a new one per call.
        /// </summary>
        public Func<Node, string> ExtraSearchText;
        /// <summary>Shown under an expanded group that has no results at all.</summary>
        public string EmptyGroupText = "Nothing found";

        private List<Node> _roots = new();
        private readonly List<Row> _rows = new();
        private readonly HashSet<Node> _matching = new();
        private readonly HashSet<Node> _collapsedWhileFiltering = new();
        private bool _rowsDirty = true;
        private string _filter = "";
        private string _typedFilter;
        private double _typedFilterDue;
        private Node _selected;
        private bool _revealSelected;
        private Vector2 _scroll;
        private float _viewHeight;
        private int _controlId;

        private static GUIStyle _labelStyle;
        private static GUIStyle _selectedLabelStyle;
        private static GUIStyle _mutedLabelStyle;
        private static GUIStyle _headerLabelStyle;
        private static bool _stylesProSkin;
        private static readonly GUIContent _messageContent = new();

        public string Filter
        {
            get => _filter;
            set
            {
                value ??= "";
                _typedFilter = null;
                if (value == _filter) return;
                _filter = value;
                _collapsedWhileFiltering.Clear();
                _rowsDirty = true;
                _revealSelected = true;
            }
        }

        private bool Filtering => _filter.Length > 0;

        /// <summary>
        /// Sets <see cref="Filter"/> once typing pauses for <see cref="TypingDelay"/> seconds, so a large tree isn't
        /// matched again on every keystroke; the owner calls <see cref="ApplyTypedFilter"/> from <c>Update</c>.
        /// </summary>
        public void SetFilterDelayed(string value)
        {
            _typedFilter = value ?? "";
            _typedFilterDue = EditorApplication.timeSinceStartup + TypingDelay;
        }

        /// <summary>Applies a filter typed with <see cref="SetFilterDelayed"/> once due; true when the rows changed.</summary>
        public bool ApplyTypedFilter()
        {
            if (_typedFilter == null || EditorApplication.timeSinceStartup < _typedFilterDue) return false;
            Filter = _typedFilter;
            return true;
        }

        /// <summary>Matches the filter again — after <see cref="ExtraSearchText"/> started returning more for some nodes.</summary>
        public void RefreshFilter()
        {
            if (Filtering) _rowsDirty = true;
        }

        public void SetRoots(List<Node> roots)
        {
            _roots = roots ?? new List<Node>();
            _selected = null;
            _collapsedWhileFiltering.Clear();
            _rowsDirty = true;
        }

        /// <summary>Draws the tree into <paramref name="rect"/> (window coordinates).</summary>
        public void OnGUI(Rect rect)
        {
            EnsureStyles();
            if (_rowsDirty) RebuildRows();

            int controlId = _controlId = GUIUtility.GetControlID(FocusType.Keyboard, rect);
            if (Event.current.type != EventType.Layout) _viewHeight = rect.height;
            if (_revealSelected && Event.current.type == EventType.Repaint) RevealSelected();

            var content = new Rect(0, 0, rect.width, _rows.Count * RowH);
            if (content.height > rect.height) content.width -= GUI.skin.verticalScrollbar.fixedWidth;

            _scroll = GUI.BeginScrollView(rect, _scroll, content);
            EditorGUIUtility.SetIconSize(new Vector2(16, 16));

            int first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / RowH));
            int last = Mathf.Min(_rows.Count - 1, Mathf.FloorToInt((_scroll.y + rect.height) / RowH));
            for (int i = first; i <= last; i++)
                DrawRow(new Rect(0, i * RowH, content.width, RowH), _rows[i], controlId);

            EditorGUIUtility.SetIconSize(Vector2.zero);
            GUI.EndScrollView();
        }

        private void RebuildRows()
        {
            _rowsDirty = false;
            _rows.Clear();

            if (Filtering)
            {
                _matching.Clear();
                string query = _filter.ToLowerInvariant();
                foreach (var root in _roots)
                    CollectMatching(root, ancestorMatches: false, query);
            }

            foreach (var root in _roots)
                AddRows(root, 0);
        }

        private bool CollectMatching(Node node, bool ancestorMatches, string query)
        {
            bool matches = ancestorMatches || (node.Kind != NodeKind.Group && Matches(node, query));
            bool any = matches;
            foreach (var child in node.Children)
                if (CollectMatching(child, matches, query)) any = true;
            if (any) _matching.Add(node);
            return any;
        }

        /// <summary>
        /// Whether <paramref name="node"/>'s label or its <see cref="ExtraSearchText"/> contains the lower-case
        /// <paramref name="query"/>. The lower-case label is made once per node, and the extra text is shared, so
        /// matching again allocates nothing.
        /// </summary>
        private bool Matches(Node node, string query)
        {
            node.LabelLower ??= node.Label.ToLowerInvariant();
            if (node.LabelLower.IndexOf(query, StringComparison.Ordinal) >= 0) return true;
            return ExtraSearchText?.Invoke(node) is { } extra && extra.IndexOf(query, StringComparison.Ordinal) >= 0;
        }

        private void AddRows(Node node, int depth)
        {
            if (Filtering && node.Kind != NodeKind.Group && !_matching.Contains(node)) return;

            _rows.Add(new Row { Node = node, Depth = depth });
            if (!IsOpen(node)) return;

            int before = _rows.Count;
            foreach (var child in node.Children)
                AddRows(child, depth + 1);

            if (node.Kind == NodeKind.Group && _rows.Count == before)
                _rows.Add(new Row { Depth = depth + 1, Message = node.Children.Count == 0 ? EmptyGroupText : "No matches" });
        }

        private static bool HasChildren(Node node) => node.Kind == NodeKind.Group || node.Children.Count > 0;

        private bool IsOpen(Node node)
        {
            if (!HasChildren(node)) return false;
            return Filtering && node.Kind != NodeKind.Group ? !_collapsedWhileFiltering.Contains(node) : node.Expanded;
        }

        private void SetOpen(Node node, bool open, bool recursive)
        {
            if (Filtering && node.Kind != NodeKind.Group)
            {
                if (open) _collapsedWhileFiltering.Remove(node);
                else _collapsedWhileFiltering.Add(node);
            }
            else node.Expanded = open;

            if (recursive)
                foreach (var child in node.Children)
                    SetOpen(child, open, recursive: true);
            _rowsDirty = true;
        }

        private void DrawRow(Rect row, Row r, int controlId)
        {
            var ev = Event.current;
            float x = row.x + 2f + r.Depth * Indent;

            if (r.Node == null)
            {
                if (ev.type != EventType.Repaint) return;
                _messageContent.text = r.Message;
                _mutedLabelStyle.Draw(new Rect(x + StableRefEditorUtility.ArrowW, row.y, row.xMax - x, row.height),
                    _messageContent, false, false, false, false);
                return;
            }

            var node = r.Node;
            bool hasChildren = HasChildren(node);
            bool open = IsOpen(node);
            var arrowRect = new Rect(x, row.y, StableRefEditorUtility.ArrowW, row.height);
            var labelRect = new Rect(arrowRect.xMax, row.y, Mathf.Max(0f, row.xMax - arrowRect.xMax), row.height);

            if (ev.type == EventType.MouseDown && ev.button == 0 && row.Contains(ev.mousePosition))
            {
                GUIUtility.keyboardControl = controlId;
                _selected = node;
                if (hasChildren && (arrowRect.Contains(ev.mousePosition) || ev.clickCount == 2))
                    SetOpen(node, !open, ev.alt);
                if (!arrowRect.Contains(ev.mousePosition))
                    Clicked?.Invoke(node, ev.clickCount);
                ev.Use();
                return;
            }

            if (ev.type != EventType.Repaint) return;

            bool selected = node == _selected;
            if (selected)
                EditorGUI.DrawRect(row, StableRefEditorUtility.SelectionColor);
            else if (node.Kind == NodeKind.Group)
                EditorGUI.DrawRect(row, new Color(0.5f, 0.5f, 0.5f, HeaderBgAlpha));

            if (hasChildren)
            {
                float h = EditorGUIUtility.singleLineHeight;
                EditorStyles.foldout.Draw(new Rect(arrowRect.x, row.y + (row.height - h) * 0.5f, arrowRect.width, h),
                    GUIContent.none, false, false, open, false);
            }

            var content = node.Content ??= new GUIContent(node.Label, ResolveIcon?.Invoke(node) ?? node.Icon);
            var style = selected ? _selectedLabelStyle
                : node.Kind == NodeKind.Group ? _headerLabelStyle
                : node.Muted ? _mutedLabelStyle
                : _labelStyle;

            var prevColor = GUI.color;
            if (node.Kind == NodeKind.Item && node.Children.Count > 0 && content.image == null)
                GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, prevColor.a * 0.55f);
            style.Draw(labelRect, content, false, false, false, false);
            GUI.color = prevColor;
        }

        /// <summary>
        /// Keyboard navigation, the same as in the type selector. The owner calls it at the start of <c>OnGUI</c>,
        /// before its search field is drawn so the field cannot swallow the keys; it works while the tree or the search
        /// field (<paramref name="searchFocused"/>) has keyboard focus. Up / Down / PageUp / PageDown / Home / End move
        /// over the rows; Right expands a collapsed node, otherwise jumps down to the next node with children; Left
        /// collapses an expanded node, otherwise jumps to the parent (or, at the top level, to the previous node with
        /// children); Alt makes expand / collapse recursive. Enter toggles a node with children and acts like a click on
        /// any other. While the focused search field holds text (<paramref name="searchHasText"/>), Left / Right /
        /// Home / End stay with the field. Returns true when the key was used — the owner repaints.
        /// </summary>
        public bool HandleKeyboard(bool searchFocused, bool searchHasText)
        {
            var ev = Event.current;
            if (ev.type != EventType.KeyDown) return false;
            if (!searchFocused && (_controlId == 0 || GUIUtility.keyboardControl != _controlId)) return false;
            if (_rowsDirty) RebuildRows();
            if (_rows.Count == 0) return false;

            bool textKeys = searchFocused && searchHasText;
            int index = IndexOfSelected();
            int page = Mathf.Max(1, Mathf.FloorToInt(_viewHeight / RowH) - 1);
            Node node = index >= 0 ? _rows[index].Node : null;

            switch (ev.keyCode)
            {
                case KeyCode.DownArrow: Move(index, 1); break;
                case KeyCode.UpArrow: Move(index, -1); break;
                case KeyCode.PageDown: Move(index, page); break;
                case KeyCode.PageUp: Move(index, -page); break;
                case KeyCode.Home when !textKeys: Select(Step(-1, +1)); break;
                case KeyCode.End when !textKeys: Select(Step(_rows.Count, -1)); break;
                case KeyCode.RightArrow when !textKeys: ExpandOrNext(index, ev.alt); break;
                case KeyCode.LeftArrow when !textKeys: CollapseOrParent(index, ev.alt); break;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (node == null) break;
                    if (HasChildren(node)) SetOpen(node, !IsOpen(node), ev.alt);
                    else Clicked?.Invoke(node, 1);
                    break;

                default:
                    return false;
            }

            ev.Use();
            return true;
        }

        private void Move(int index, int delta)
        {
            if (index < 0)
            {
                Select(delta > 0 ? Step(-1, +1) : Step(_rows.Count, -1));
                return;
            }

            int dir = delta > 0 ? 1 : -1;
            int target = Mathf.Clamp(index + delta, 0, _rows.Count - 1);
            int found = Step(target - dir, dir);
            Select(found >= 0 ? found : Step(target + dir, -dir));
        }

        private void ExpandOrNext(int index, bool recursive)
        {
            if (index < 0) return;

            var node = _rows[index].Node;
            if (HasChildren(node) && !IsOpen(node))
            {
                SetOpen(node, true, recursive);
                return;
            }

            for (int i = index + 1; i < _rows.Count; i++)
                if (_rows[i].Node is { } next && HasChildren(next)) { Select(i); return; }
        }

        private void CollapseOrParent(int index, bool recursive)
        {
            if (index < 0) return;

            var row = _rows[index];
            if (IsOpen(row.Node))
            {
                SetOpen(row.Node, false, recursive);
                return;
            }

            if (row.Depth > 0)
            {
                Select(ParentRow(index));
                return;
            }

            for (int i = index - 1; i >= 0; i--)
                if (_rows[i].Node is { } prev && HasChildren(prev)) { Select(i); return; }
        }

        private int IndexOfSelected()
        {
            if (_selected == null) return -1;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Node == _selected) return i;
            return -1;
        }

        /// <summary>The next row with a node from <paramref name="from"/> in direction <paramref name="dir"/>, or -1.</summary>
        private int Step(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < _rows.Count; i += dir)
                if (_rows[i].Node != null) return i;
            return -1;
        }

        private int ParentRow(int index)
        {
            if (index < 0) return -1;
            int depth = _rows[index].Depth;
            for (int i = index - 1; i >= 0; i--)
                if (_rows[i].Node != null && _rows[i].Depth < depth) return i;
            return -1;
        }

        private void Select(int index)
        {
            if (index < 0) return;
            _selected = _rows[index].Node;
            _revealSelected = true;
        }

        private void RevealSelected()
        {
            _revealSelected = false;
            int index = IndexOfSelected();
            if (index < 0 || _viewHeight <= 0f) return;

            float top = index * RowH;
            if (top < _scroll.y) _scroll.y = top;
            else if (top + RowH > _scroll.y + _viewHeight) _scroll.y = top + RowH - _viewHeight;
        }

        private static void EnsureStyles()
        {
            if (_labelStyle != null && _stylesProSkin == EditorGUIUtility.isProSkin) return;
            _stylesProSkin = EditorGUIUtility.isProSkin;

            _labelStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
            _selectedLabelStyle = new GUIStyle(_labelStyle);
            _selectedLabelStyle.normal.textColor = StableRefEditorUtility.SelectionTextColor;
            _mutedLabelStyle = new GUIStyle(_labelStyle);
            _mutedLabelStyle.normal.textColor = _stylesProSkin ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.4f, 0.4f, 0.4f);
            _headerLabelStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft };
        }
    }
}
#endif
