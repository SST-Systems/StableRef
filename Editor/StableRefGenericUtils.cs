#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace SST.StableRef
{
    /// <summary>
    /// Closes open generic definitions to fit a closed generic element type so they can be offered in
    /// the StableRef selector (e.g. <c>All&lt;&gt;</c> → <c>All&lt;Unit&gt;</c> for <c>ICondition&lt;Unit&gt;</c>).
    /// </summary>
    public static class StableRefGenericUtils
    {
        /// <summary>Type name without the generic arity marker: "All`1" -> "All".</summary>
        public static string DisplayName(Type t)
        {
            if (t == null) return "";
            var n = t.Name;
            int tick = n.IndexOf('`');
            return tick >= 0 ? n.Substring(0, tick) : n;
        }

        /// <summary>Concrete open generic definitions closed so they are assignable to <paramref name="elementBaseType"/>.</summary>
        public static List<Type> CollectClosedGenericCandidates(Type elementBaseType)
        {
            var result = new List<Type>();
            if (elementBaseType == null || !elementBaseType.IsGenericType || elementBaseType.IsGenericTypeDefinition)
                return result;

            var seen = new HashSet<Type>();
            foreach (var def in TypeCache.GetTypesDerivedFrom(elementBaseType.GetGenericTypeDefinition()))
            {
                if (!def.IsGenericTypeDefinition || def.IsAbstract || def.IsInterface) continue;
                if (TryClose(def, elementBaseType, out var closed) && seen.Add(closed))
                    result.Add(closed);
            }
            return result;
        }

        /// <summary>
        /// Closes <paramref name="openDef"/> so it is assignable to <paramref name="closedTarget"/>, inferring
        /// its type arguments (handles indirect, reordered and fixed params). False if impossible.
        /// </summary>
        public static bool TryClose(Type openDef, Type closedTarget, out Type closed)
        {
            closed = null;
            if (openDef == null || !openDef.IsGenericTypeDefinition) return false;
            if (closedTarget == null || !closedTarget.IsGenericType || closedTarget.IsGenericTypeDefinition) return false;

            var targetDef = closedTarget.GetGenericTypeDefinition();
            var targetArgs = closedTarget.GetGenericArguments();

            var impl = FindImplemented(openDef, targetDef);
            if (impl == null) return false;

            var implArgs = impl.GetGenericArguments();
            if (implArgs.Length != targetArgs.Length) return false;

            var inferred = new Type[openDef.GetGenericArguments().Length];
            for (int i = 0; i < implArgs.Length; i++)
                if (!TryUnify(implArgs[i], targetArgs[i], inferred)) return false;

            for (int i = 0; i < inferred.Length; i++)
                if (inferred[i] == null) return false;

            try { closed = openDef.MakeGenericType(inferred); }
            catch { return false; }

            if (!closedTarget.IsAssignableFrom(closed)) { closed = null; return false; }
            return true;
        }

        private static Type FindImplemented(Type def, Type targetDef)
        {
            if (targetDef.IsInterface)
            {
                foreach (var itf in def.GetInterfaces())
                    if (itf.IsGenericType && itf.GetGenericTypeDefinition() == targetDef) return itf;
                return null;
            }

            for (var t = def; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == targetDef) return t;
            return null;
        }

        private static bool TryUnify(Type implArg, Type target, Type[] inferred)
        {
            if (implArg.IsGenericParameter)
            {
                if (implArg.DeclaringMethod != null) return false;
                int pos = implArg.GenericParameterPosition;
                if (pos < 0 || pos >= inferred.Length) return false;
                if (inferred[pos] == null) { inferred[pos] = target; return true; }
                return inferred[pos] == target;
            }

            if (!implArg.ContainsGenericParameters)
                return implArg == target;

            if (implArg.IsGenericType && target.IsGenericType
                && implArg.GetGenericTypeDefinition() == target.GetGenericTypeDefinition())
            {
                var ia = implArg.GetGenericArguments();
                var ta = target.GetGenericArguments();
                if (ia.Length != ta.Length) return false;
                for (int i = 0; i < ia.Length; i++)
                    if (!TryUnify(ia[i], ta[i], inferred)) return false;
                return true;
            }

            if (implArg.IsArray && target.IsArray && implArg.GetArrayRank() == target.GetArrayRank())
                return TryUnify(implArg.GetElementType(), target.GetElementType(), inferred);

            return false;
        }
    }
}
#endif