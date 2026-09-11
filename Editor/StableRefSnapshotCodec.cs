#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace SST.StableRef
{
    /// <summary>
    /// Captures and restores the value snapshot a StableRef entry keeps next to its managed reference
    /// (<see cref="StableRefBase.ValuesData"/>, <see cref="StableRefBase.ObjectRefs"/>,
    /// <see cref="StableRefBase.ObjectRefPaths"/>). Single implementation behind
    /// <see cref="StableRefBackup"/> and <see cref="StableRefEntry"/>.
    /// </summary>
    /// <remarks>
    /// The v2 format ("#v2" header line) walks the whole value subtree: nested structs, arrays and
    /// lists (sizes precede elements in document order, so restore resizes before writing), hidden
    /// serialized fields, and the metadata fields of nested StableRef entries — everything except the
    /// contents of nested managed references themselves, which are recovered through their own entry's
    /// snapshot by the fix tooling. Legacy headerless data (flat fields, enums stored by index) is
    /// still restored. Not captured: AnimationCurve, Gradient, Hash128, ExposedReference and fixed
    /// buffers — such fields come back as the freshly created instance's defaults.
    /// </remarks>
    internal static class StableRefSnapshotCodec
    {
        private const string VersionHeader = "#v2";
        private const int MaxRelPathDepth = 48;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void Capture(SerializedProperty entryProp, SerializedProperty valueProp)
        {
            if (valueProp.managedReferenceValue == null) return;

            var objectRefsProp = entryProp.FindPropertyRelative("ObjectRefs");
            var objectRefPathsProp = entryProp.FindPropertyRelative("ObjectRefPaths");
            var valuesDataProp = entryProp.FindPropertyRelative("ValuesData");
            if (objectRefsProp == null || objectRefPathsProp == null || valuesDataProp == null) return;

            objectRefsProp.ClearArray();
            objectRefPathsProp.ClearArray();

            var lines = new List<string>();
            string basePath = valueProp.propertyPath;
            var child = valueProp.Copy();
            var end = valueProp.GetEndProperty();
            bool warnedDepth = false;

            if (child.Next(true))
            {
                while (!SerializedProperty.EqualContents(child, end))
                {
                    if (!child.propertyPath.StartsWith(basePath, StringComparison.Ordinal)) break;

                    string relPath = child.propertyPath.Substring(basePath.Length).TrimStart('.');
                    bool enter = false;

                    switch (child.propertyType)
                    {
                        case SerializedPropertyType.Generic:
                            if (RelPathDepth(relPath) <= MaxRelPathDepth)
                            {
                                enter = true;
                            }
                            else if (!warnedDepth)
                            {
                                warnedDepth = true;
                                Debug.LogWarning(
                                    $"[StableRef] Snapshot of '{basePath}' stopped descending at depth {MaxRelPathDepth} ('{relPath}').");
                            }
                            break;

                        case SerializedPropertyType.ManagedReference:
                            break;

                        case SerializedPropertyType.ObjectReference:
                            int objIdx = objectRefsProp.arraySize;
                            objectRefsProp.arraySize++;
                            objectRefsProp.GetArrayElementAtIndex(objIdx).objectReferenceValue = child.objectReferenceValue;
                            objectRefPathsProp.arraySize++;
                            objectRefPathsProp.GetArrayElementAtIndex(objIdx).stringValue = relPath;
                            break;

                        default:
                            EmitLine(lines, relPath, child);
                            break;
                    }

                    if (!child.Next(enter)) break;
                }
            }

            valuesDataProp.stringValue = lines.Count > 0
                ? VersionHeader + "\n" + string.Join("\n", lines)
                : VersionHeader;
        }

        internal static void Restore(SerializedProperty entryProp, SerializedProperty valueProp)
        {
            var objectRefsProp = entryProp.FindPropertyRelative("ObjectRefs");
            var objectRefPathsProp = entryProp.FindPropertyRelative("ObjectRefPaths");
            var valuesDataProp = entryProp.FindPropertyRelative("ValuesData");
            if (valuesDataProp == null) return;

            string data = valuesDataProp.stringValue;
            bool v2 = data == VersionHeader || data.StartsWith(VersionHeader + "\n", StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(data))
            {
                var linesToApply = v2 ? data.Substring(Math.Min(data.Length, VersionHeader.Length + 1)) : data;
                foreach (var line in linesToApply.Split('\n'))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    int sep = line.IndexOf('=');
                    if (sep < 0) continue;

                    string relPath = Decode(line.Substring(0, sep));
                    string val = Decode(line.Substring(sep + 1));
                    var targetProp = valueProp.FindPropertyRelative(relPath);
                    if (targetProp == null) continue;

                    try { ApplyLine(targetProp, val, v2); }
                    catch { /* ignore per-field restore errors */ }
                }
            }

            if (objectRefsProp == null || objectRefPathsProp == null) return;

            int refCount = Math.Min(objectRefPathsProp.arraySize, objectRefsProp.arraySize);
            for (int i = 0; i < refCount; i++)
            {
                string relPath = objectRefPathsProp.GetArrayElementAtIndex(i).stringValue;
                var obj = objectRefsProp.GetArrayElementAtIndex(i).objectReferenceValue;
                var targetProp = valueProp.FindPropertyRelative(relPath);
                if (targetProp != null && targetProp.propertyType == SerializedPropertyType.ObjectReference)
                    targetProp.objectReferenceValue = obj;
            }
        }

        private static void EmitLine(List<string> lines, string relPath, SerializedProperty child)
        {
            switch (child.propertyType)
            {
                case SerializedPropertyType.Integer:
                    lines.Add(Encode(relPath, child.longValue.ToString(Inv))); break;
                case SerializedPropertyType.Boolean:
                    lines.Add(Encode(relPath, child.boolValue ? "1" : "0")); break;
                case SerializedPropertyType.Float:
                    lines.Add(Encode(relPath, child.doubleValue.ToString("R", Inv))); break;
                case SerializedPropertyType.String:
                    if (!string.IsNullOrEmpty(child.stringValue))
                        lines.Add(Encode(relPath, child.stringValue));
                    break;
                case SerializedPropertyType.Character:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.Enum:
                    lines.Add(Encode(relPath, child.intValue.ToString(Inv))); break;
                case SerializedPropertyType.Color:
                {
                    var c = child.colorValue;
                    lines.Add(Encode(relPath, Join(c.r, c.g, c.b, c.a)));
                    break;
                }
                case SerializedPropertyType.Vector2:
                {
                    var v = child.vector2Value;
                    lines.Add(Encode(relPath, Join(v.x, v.y)));
                    break;
                }
                case SerializedPropertyType.Vector3:
                {
                    var v = child.vector3Value;
                    lines.Add(Encode(relPath, Join(v.x, v.y, v.z)));
                    break;
                }
                case SerializedPropertyType.Vector4:
                {
                    var v = child.vector4Value;
                    lines.Add(Encode(relPath, Join(v.x, v.y, v.z, v.w)));
                    break;
                }
                case SerializedPropertyType.Quaternion:
                {
                    var q = child.quaternionValue;
                    lines.Add(Encode(relPath, Join(q.x, q.y, q.z, q.w)));
                    break;
                }
                case SerializedPropertyType.Rect:
                {
                    var r = child.rectValue;
                    lines.Add(Encode(relPath, Join(r.x, r.y, r.width, r.height)));
                    break;
                }
                case SerializedPropertyType.Bounds:
                {
                    var b = child.boundsValue;
                    lines.Add(Encode(relPath, Join(b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z)));
                    break;
                }
                case SerializedPropertyType.Vector2Int:
                {
                    var v = child.vector2IntValue;
                    lines.Add(Encode(relPath, Join(v.x, v.y)));
                    break;
                }
                case SerializedPropertyType.Vector3Int:
                {
                    var v = child.vector3IntValue;
                    lines.Add(Encode(relPath, Join(v.x, v.y, v.z)));
                    break;
                }
                case SerializedPropertyType.RectInt:
                {
                    var r = child.rectIntValue;
                    lines.Add(Encode(relPath, Join(r.x, r.y, r.width, r.height)));
                    break;
                }
                case SerializedPropertyType.BoundsInt:
                {
                    var b = child.boundsIntValue;
                    lines.Add(Encode(relPath, Join(b.position.x, b.position.y, b.position.z, b.size.x, b.size.y, b.size.z)));
                    break;
                }
            }
        }

        private static void ApplyLine(SerializedProperty targetProp, string val, bool v2)
        {
            switch (targetProp.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (TryParseLong(val, out long lv)) targetProp.longValue = lv; break;
                case SerializedPropertyType.Boolean:
                    targetProp.boolValue = val == "1"; break;
                case SerializedPropertyType.Float:
                    if (TryParseDouble(val, out double dv)) targetProp.doubleValue = dv; break;
                case SerializedPropertyType.String:
                    targetProp.stringValue = val; break;
                case SerializedPropertyType.Enum:
                    if (TryParseInt(val, out int ev))
                    {
                        if (v2) targetProp.intValue = ev;
                        else targetProp.enumValueIndex = ev;
                    }
                    break;
                case SerializedPropertyType.Character:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                    if (TryParseInt(val, out int iv)) targetProp.intValue = iv; break;
                case SerializedPropertyType.Color:
                {
                    if (TryParseFloats(val, 4, out var p))
                        targetProp.colorValue = new Color(p[0], p[1], p[2], p[3]);
                    break;
                }
                case SerializedPropertyType.Vector2:
                {
                    if (TryParseFloats(val, 2, out var p))
                        targetProp.vector2Value = new Vector2(p[0], p[1]);
                    break;
                }
                case SerializedPropertyType.Vector3:
                {
                    if (TryParseFloats(val, 3, out var p))
                        targetProp.vector3Value = new Vector3(p[0], p[1], p[2]);
                    break;
                }
                case SerializedPropertyType.Vector4:
                {
                    if (TryParseFloats(val, 4, out var p))
                        targetProp.vector4Value = new Vector4(p[0], p[1], p[2], p[3]);
                    break;
                }
                case SerializedPropertyType.Quaternion:
                {
                    if (TryParseFloats(val, 4, out var p))
                        targetProp.quaternionValue = new Quaternion(p[0], p[1], p[2], p[3]);
                    break;
                }
                case SerializedPropertyType.Rect:
                {
                    if (TryParseFloats(val, 4, out var p))
                        targetProp.rectValue = new Rect(p[0], p[1], p[2], p[3]);
                    break;
                }
                case SerializedPropertyType.Bounds:
                {
                    if (TryParseFloats(val, 6, out var p))
                        targetProp.boundsValue = new Bounds(new Vector3(p[0], p[1], p[2]), new Vector3(p[3], p[4], p[5]));
                    break;
                }
                case SerializedPropertyType.Vector2Int:
                {
                    if (TryParseInts(val, 2, out var p))
                        targetProp.vector2IntValue = new Vector2Int(p[0], p[1]);
                    break;
                }
                case SerializedPropertyType.Vector3Int:
                {
                    if (TryParseInts(val, 3, out var p))
                        targetProp.vector3IntValue = new Vector3Int(p[0], p[1], p[2]);
                    break;
                }
                case SerializedPropertyType.RectInt:
                {
                    if (TryParseInts(val, 4, out var p))
                        targetProp.rectIntValue = new RectInt(p[0], p[1], p[2], p[3]);
                    break;
                }
                case SerializedPropertyType.BoundsInt:
                {
                    if (TryParseInts(val, 6, out var p))
                        targetProp.boundsIntValue = new BoundsInt(new Vector3Int(p[0], p[1], p[2]), new Vector3Int(p[3], p[4], p[5]));
                    break;
                }
            }
        }

        private static int RelPathDepth(string relPath)
        {
            int depth = 0;
            for (int i = 0; i < relPath.Length; i++)
                if (relPath[i] == '.') depth++;
            return depth;
        }

        private static string Join(params float[] parts)
        {
            var strings = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                strings[i] = parts[i].ToString("R", Inv);
            return string.Join("|", strings);
        }

        private static string Join(params int[] parts)
        {
            var strings = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                strings[i] = parts[i].ToString(Inv);
            return string.Join("|", strings);
        }

        private static bool TryParseInt(string s, out int result)
            => int.TryParse(s, NumberStyles.Integer, Inv, out result) || int.TryParse(s, out result);

        private static bool TryParseLong(string s, out long result)
            => long.TryParse(s, NumberStyles.Integer, Inv, out result) || long.TryParse(s, out result);

        private static bool TryParseFloat(string s, out float result)
            => float.TryParse(s, NumberStyles.Float, Inv, out result) || float.TryParse(s, out result);

        private static bool TryParseDouble(string s, out double result)
            => double.TryParse(s, NumberStyles.Float, Inv, out result) || double.TryParse(s, out result);

        private static bool TryParseFloats(string s, int count, out float[] result)
        {
            result = null;
            var parts = s.Split('|');
            if (parts.Length != count) return false;
            var parsed = new float[count];
            for (int i = 0; i < count; i++)
                if (!TryParseFloat(parts[i], out parsed[i])) return false;
            result = parsed;
            return true;
        }

        private static bool TryParseInts(string s, int count, out int[] result)
        {
            result = null;
            var parts = s.Split('|');
            if (parts.Length != count) return false;
            var parsed = new int[count];
            for (int i = 0; i < count; i++)
                if (!TryParseInt(parts[i], out parsed[i])) return false;
            result = parsed;
            return true;
        }

        private static string Encode(string key, string value)
            => $"{EscapeField(key)}={EscapeField(value)}";

        private static string EscapeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("%", "%25").Replace("\n", "%0A").Replace("=", "%3D");
        }

        private static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("%3D", "=").Replace("%0A", "\n").Replace("%25", "%");
        }
    }
}
#endif
