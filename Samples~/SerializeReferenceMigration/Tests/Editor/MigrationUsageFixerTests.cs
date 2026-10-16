using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SST.StableRef.Migration;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Uses of retyped fields fixed as text: by compiler diagnostics placed where the C# compiler reports them, and the
    /// silent ones the compiler accepts.
    /// </summary>
    public class MigrationUsageFixerTests
    {
        private static readonly string[] Names = { "effect", "effects" };

        /// <summary>Fixes <paramref name="code"/> by one diagnostic reported at the first <paramref name="anchor"/>.</summary>
        private static string Fix(string code, string id, string anchor, string message, int anchorOffset = 0)
        {
            int at = code.IndexOf(anchor, System.StringComparison.Ordinal) + anchorOffset;
            Assert.GreaterOrEqual(at, 0, anchor);
            int line = 1, column = 1;
            for (int i = 0; i < at; i++)
            {
                if (code[i] == '\n') { line++; column = 1; }
                else column++;
            }
            var unfixed = new List<MigrationUsageFixer.Diagnostic>();
            string result = MigrationUsageFixer.FixDiagnostics(code, new[] { new MigrationUsageFixer.Diagnostic(id, line, column, message) },
                Names, new List<string>(), unfixed, out _);
            Assert.IsEmpty(unfixed, "left unfixed");
            return result;
        }

        private const string NoMember = "'StableRef<IEffect>' does not contain a definition for 'Apply'";

        [Test]
        public void MemberAccess_GetsValue()
        {
            Assert.AreEqual("o.effect.Reference.Apply();", Fix("o.effect.Apply();", "CS1061", "Apply", NoMember));
            Assert.AreEqual("o.effect.Reference?.Apply();", Fix("o.effect?.Apply();", "CS1061", "?.", NoMember, 1),
                "x?.M is reported at the dot");
            Assert.AreEqual("o.effects.Count", Fix("o.effects.Length", "CS1061", "Length",
                "'StableRefList<IEffect>' does not contain a definition for 'Length'"));
        }

        [Test]
        public void Read_GetsValue_ConditionalBranchesToo()
        {
            const string convert = "Cannot implicitly convert type 'SST.StableRef.StableRef<IEffect>' to 'IEffect'";
            Assert.AreEqual("IEffect e = o.effect.Reference;", Fix("IEffect e = o.effect;", "CS0029", "o.effect", convert));
            Assert.AreEqual("IEffect e = f && g ? o.effect.Reference : null;",
                Fix("IEffect e = f && g ? o.effect : null;", "CS0029", "f &&", convert), "reported at the condition");
            Assert.AreEqual("Use(f ? new Burn() : o.effect.Reference);",
                Fix("Use(f ? new Burn() : o.effect);", "CS1503", "f ?",
                    "Argument 1: cannot convert from 'target-typed conditional expression' to 'IEffect'"));
            Assert.AreEqual("var b = (Burn)o.effect.Reference;", Fix("var b = (Burn)o.effect;", "CS0030", "(Burn)",
                "Cannot convert type 'SST.StableRef.StableRef<IEffect>' to 'Burn'"));
            Assert.AreEqual("IEffect[] a = o.effects.ToArray();", Fix("IEffect[] a = o.effects;", "CS0029", "o.effects",
                "Cannot implicitly convert type 'SST.StableRef.StableRefList<IEffect>' to 'IEffect[]'"));
            Assert.AreEqual("IEffect[] a = f is Mode.Win\n    ? o.effects.ToArray()\n    : null;",
                Fix("IEffect[] a = f is Mode.Win\n    ? o.effects\n    : null;", "CS0029", "f is",
                    "Cannot implicitly convert type 'SST.StableRef.StableRefList<IEffect>' to 'IEffect[]'"),
                "a conditional converted as a whole is reported at its condition: the branch converts");
        }

        [Test]
        public void ExtensionOfTheValue_CalledOnWrapper_GetsValue()
        {
            const string extension = "'StableRef<IEffect>' does not contain a definition for 'Inject' and the best extension method " +
                                     "overload 'EffectExtensions.Inject(IEffect, Container)' requires a receiver of type 'IEffect'";
            Assert.AreEqual("o.effect.Reference.Inject(c);", Fix("o.effect.Inject(c);", "CS1929", "o.effect", extension),
                "reported at the start of the receiver");
            Assert.AreEqual("Get(o).effect.Reference?.Inject<Foo>(c).Inject(d);", Fix("Get(o).effect?.Inject<Foo>(c).Inject(d);", "CS1929", "Get", extension),
                "the first call along the chain");
        }

        [Test]
        public void PropertyPattern_OnWrapper()
        {
            Assert.AreEqual("if (o.effects is { Count: > 0 }) { }", Fix("if (o.effects is { Length: > 0 }) { }", "CS0117", "Length",
                "'StableRefList<IEffect>' does not contain a definition for 'Length'"));
            Assert.AreEqual("if (o.effect.Reference is { Power: > 0 }) { }", Fix("if (o.effect is { Power: > 0 }) { }", "CS0117", "Power",
                "'StableRef<IEffect>' does not contain a definition for 'Power'"));
        }

        [Test]
        public void LocalizedMessage_IsRead()
        {
            Assert.AreEqual("o.effect.Reference.Apply();", Fix("o.effect.Apply();", "CS1061", "Apply",
                "\"StableRef<IEffect>\" не содержит определения \"Apply\""));
        }

        [Test]
        public void Patterns_FixTheSubject_NotThePattern()
        {
            const string pattern = "An expression of type 'StableRef<IEffect>' cannot be handled by a pattern of type 'Burn'.";
            Assert.AreEqual("if (o.effect.Reference is not Burn b) { }", Fix("if (o.effect is not Burn b) { }", "CS8121", "Burn", pattern));
            Assert.AreEqual("if (o.effect.Reference is Freeze or Burn) { }", Fix("if (o.effect is Freeze or Burn) { }", "CS8121", "Burn", pattern),
                "a later pattern of an `or` finds the same subject");
            Assert.AreEqual("switch (o.effect.Reference) { case Freeze: break; case Burn x: break; }",
                Fix("switch (o.effect) { case Freeze: break; case Burn x: break; }", "CS8121", "Burn", pattern));
            Assert.AreEqual("int p = o.effect.Reference switch { Freeze => 1, Burn => 2, _ => 0 };",
                Fix("int p = o.effect switch { Freeze => 1, Burn => 2, _ => 0 };", "CS8121", "Burn", pattern));
        }

        [Test]
        public void Assignment_UsesSet_OrValue_OrWrapsInitializer()
        {
            const string convert = "Cannot implicitly convert type 'Burn' to 'SST.StableRef.StableRef<IEffect>'";
            Assert.AreEqual("if (f) o.effect.Set(new Burn());", Fix("if (f) o.effect = new Burn();", "CS0029", "new", convert));
            Assert.AreEqual("o.effect.Reference = f\n    ? new Burn()\n    : null;",
                Fix("o.effect = f\n    ? new Burn()\n    : null;", "CS0029", "new", convert), "a value over several lines isn't wrapped");
            Assert.AreEqual("new Owner { effect = new(new Burn()) }", Fix("new Owner { effect = new Burn() }", "CS0029", "new Burn", convert));
            Assert.AreEqual("o.effects = new();", Fix("o.effects = new List<IEffect>();", "CS0029", "new",
                "Cannot implicitly convert type 'System.Collections.Generic.List<IEffect>' to 'SST.StableRef.StableRefList<IEffect>'"));
            Assert.AreEqual("new Owner { effects = new(new IEffect[]\n{\n    new Burn()\n}) }",
                Fix("new Owner { effects = new IEffect[]\n{\n    new Burn()\n} }", "CS0029", "new IEffect",
                    "Cannot implicitly convert type 'IEffect[]' to 'SST.StableRef.StableRefList<IEffect>'"),
                "a list value over several lines is wrapped as it is");
        }

        [Test]
        public void CommentsAndStrings_AreNotCode_InterpolationHolesAre()
        {
            Assert.AreEqual("var s = $\"{o.effect.Reference.Power:D2}\"; // o.effect.Power",
                Fix("var s = $\"{o.effect.Power:D2}\"; // o.effect.Power", "CS1061", "Power",
                    "'StableRef<IEffect>' does not contain a definition for 'Power'"));
        }

        [Test]
        public void OtherDiagnostics_AreLeftAlone()
        {
            var unfixed = new List<MigrationUsageFixer.Diagnostic>();
            string code = "int x = \"a\";";
            string result = MigrationUsageFixer.FixDiagnostics(code,
                new[] { new MigrationUsageFixer.Diagnostic("CS0029", 1, 9, "Cannot implicitly convert type 'string' to 'int'") },
                Names, new List<string>(), unfixed, out _);
            Assert.AreSame(code, result);
            Assert.IsEmpty(unfixed, "not about StableRef: not reported either");
        }

        private static Dictionary<string, MigrationUsageFixer.Field> Fields(bool own, bool unique, params string[] shared) => new()
        {
            ["effect"] = new MigrationUsageFixer.Field { Name = "effect", OwnFile = own, UniqueName = unique, SharedMembers = new HashSet<string>(shared) },
            ["effects"] = new MigrationUsageFixer.Field { Name = "effects", IsList = true, OwnFile = own, UniqueName = unique }
        };

        private static string Silent(string code, Dictionary<string, MigrationUsageFixer.Field> fields, out List<string> review)
        {
            review = new List<string>();
            return MigrationUsageFixer.FixSilentUses(code, fields, new List<string>(), review);
        }

        [Test]
        public void SilentChecks_InOwnFile_GetValue()
        {
            string code = "class A { StableRef<IEffect> effect = new(); StableRefList<IEffect> effects = new();\n" +
                          "void M() { if (effect != null && null != this.effect && effect is not null && effect is { }) { } var t = effect.GetType(); effect = null; if (effects != null) { } } }";
            string expected = "class A { StableRef<IEffect> effect = new(); StableRefList<IEffect> effects = new();\n" +
                              "void M() { if (effect.Reference != null && null != this.effect.Reference && effect.Reference is not null && effect.Reference is { }) { } var t = effect.Reference.GetType(); effect.Reference = null; if (effects != null) { } } }";
            Assert.AreEqual(expected, Silent(code, Fields(own: true, unique: false), out var review));
            Assert.IsEmpty(review);
        }

        [Test]
        public void SilentChecks_Ambiguous_AreListed_NotChanged()
        {
            string qualified = "class B { void M(A a) { if (a.effect != null) { } } }";
            Assert.AreEqual(qualified, Silent(qualified, Fields(own: false, unique: false), out var review));
            Assert.AreEqual(1, review.Count, "another member has the name");
            Assert.AreEqual("class B { void M(A a) { if (a.effect.Reference != null) { } } }",
                Silent(qualified, Fields(own: false, unique: true), out review));

            string otherFile = "class B { void M(IEffect effect) { if (effect != null) { } } }";
            Assert.AreEqual(otherFile, Silent(otherFile, Fields(own: false, unique: true), out review));
            Assert.IsEmpty(review, "an unqualified name in another file is not the field");
        }

        [Test]
        public void LocalOfTheSameName_HidesTheField_InItsScopeOnly()
        {
            string code = "class A { StableRef<IEffect> effect = new();\n" +
                          "void Init(IEffect effect) { if (effect != null) { } }\n" +
                          "void Show() { if (effect == null) { } foreach (var effect in list) { if (effect != null) { } } }\n" +
                          "void Close() => Use(x => x.effect, effect is null); }";
            string expected = "class A { StableRef<IEffect> effect = new();\n" +
                              "void Init(IEffect effect) { if (effect != null) { } }\n" +
                              "void Show() { if (effect.Reference == null) { } foreach (var effect in list) { if (effect != null) { } } }\n" +
                              "void Close() => Use(x => x.effect, effect.Reference is null); }";
            Assert.AreEqual(expected, Silent(code, Fields(own: true, unique: false), out var review));
            Assert.IsEmpty(review, "a parameter or local of the same name is no field");
        }

        [Test]
        public void MoreSilentUses_GetValue()
        {
            string code = "class A { StableRef<IEffect> effect = new(); void M(object o) {\n" +
                          "var a = effect ?? o; bool b = effect == o; bool c = o != effect; int h = effect.GetHashCode();\n" +
                          "int d = effect switch { null => 0, _ => 1 }; switch (effect) { case null: break; } } }";
            string expected = "class A { StableRef<IEffect> effect = new(); void M(object o) {\n" +
                              "var a = effect.Reference ?? o; bool b = effect.Reference == o; bool c = o != effect.Reference; int h = effect.Reference.GetHashCode();\n" +
                              "int d = effect.Reference switch { null => 0, _ => 1 }; switch (effect.Reference) { case null: break; } } }";
            Assert.AreEqual(expected, Silent(code, Fields(own: true, unique: false), out _));
        }

        private static MigrationTypeModel Model() => new(new[] { (typeof(SrOwner), "effect") },
            new[] { typeof(MigrationUsageFixerTests).Assembly, typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(List<>).Assembly });

        /// <summary>The silent-use check for a field named <c>effect</c> that neither this file declares nor is unique: only types tell.</summary>
        private static string Typed(string code, out List<string> review)
        {
            review = new List<string>();
            var fields = new Dictionary<string, MigrationUsageFixer.Field> { ["effect"] = new() { Name = "effect" } };
            return MigrationUsageFixer.FixSilentUses(code, fields, new List<string>(), review, Model());
        }

        private const string Usings = "using System.Collections.Generic; using System.Linq; using SST.StableRef.Tests;\n";

        [Test]
        public void ReceiverType_TellsTheField_FromAnotherOfTheSameName()
        {
            string code = Usings + "class C { void M(SrOwner a, SrOther b) { if (a.effect != null && b.effect != null) { } } }";
            Assert.AreEqual(Usings + "class C { void M(SrOwner a, SrOther b) { if (a.effect.Reference != null && b.effect != null) { } } }",
                Typed(code, out var review));
            Assert.IsEmpty(review, "b.effect is another type's field");
        }

        [Test]
        public void ReceiverType_IsReadFromLocalsLoopsLambdasMembersAndCalls()
        {
            string code = Usings + "class C { void M(SrHolder h, List<SrOwner> list) {\n" +
                          "var x = h.Owner; if (x.effect == null) { }\n" +
                          "foreach (var o in h.owners) if (o.effect is SrBurn) { }\n" +
                          "bool any = list.Where(w => w.effect != null).Skip(1).Any(v => v.effect is null);\n" +
                          "float sum = list.Aggregate(0f, (acc, s) => s.effect == null ? acc : acc + 1);\n" +
                          "if (h.Owner.Next().effect is null || list[0].effect != null || (h.Owner as SrOwner).effect == null) { }\n" +
                          "if (h.Find(out SrOwner found) && found.effect != null) { } } }";
            string expected = Usings + "class C { void M(SrHolder h, List<SrOwner> list) {\n" +
                              "var x = h.Owner; if (x.effect.Reference == null) { }\n" +
                              "foreach (var o in h.owners) if (o.effect.Reference is SrBurn) { }\n" +
                              "bool any = list.Where(w => w.effect.Reference != null).Skip(1).Any(v => v.effect.Reference is null);\n" +
                              "float sum = list.Aggregate(0f, (acc, s) => s.effect.Reference == null ? acc : acc + 1);\n" +
                              "if (h.Owner.Next().effect.Reference is null || list[0].effect.Reference != null || (h.Owner as SrOwner).effect.Reference == null) { }\n" +
                              "if (h.Find(out SrOwner found) && found.effect.Reference != null) { } } }";
            Assert.AreEqual(expected, Typed(code, out var review));
            Assert.IsEmpty(review);
        }

        [Test]
        public void ReceiverType_IsReadFromTheEnclosingType_ThroughAGenericBase()
        {
            string code = Usings + "class SrProcessor : SrProcessorBase<SrOwner> { bool M() => Data.effect?.GetType() == this.Data.effect?.GetType(); }";
            Assert.AreEqual(Usings + "class SrProcessor : SrProcessorBase<SrOwner> { bool M() => Data.effect.Reference?.GetType() == this.Data.effect.Reference?.GetType(); }",
                Typed(code, out _));
        }

        [Test]
        public void Argument_GetsValue_UnlessTheParameterTakesTheWrapper()
        {
            string code = Usings + "class C { void M(SrOwner a) { SrSink.Take(a.effect); SrSink.Generic(a.effect); SrSink.Typed(a.effect); new SrBox(a.effect); Elsewhere.Call(a.effect); SrSink.Wrapper(a.effect); } }";
            Assert.AreEqual(Usings + "class C { void M(SrOwner a) { SrSink.Take(a.effect.Reference); SrSink.Generic(a.effect.Reference); SrSink.Typed(a.effect.Reference); new SrBox(a.effect.Reference); Elsewhere.Call(a.effect.Reference); SrSink.Wrapper(a.effect); } }",
                Typed(code, out var review));
            Assert.IsEmpty(review, "a call whose parameter can't be read took the value before the retype");
        }

        [Test]
        public void EveryRead_GetsValue_WritesAndWrapperMembersDoNot()
        {
            string code = Usings + "class C { ISrEffect M(SrOwner a) {\n" +
                          "a.effect.Inject(c); var s = a.effect?.State; ISrEffect e = a.effect; var f = flag ? a.effect : null;\n" +
                          "a.effect = new SrBurn(); a.effect.Set(e); var id = a.effect.TypeId; var n = nameof(a.effect);\n" +
                          "Use(effect: a.effect); return a.effect; } }\n" +
                          "class SrOwner { [field: SerializeReference] public ISrEffect effect { get; private set; } }\n" +
                          "class SrOwner { public StableRef<effect> effect { get; } = new(); effect effect2; ISrEffect Get() => effect; ISrEffect Or(ISrEffect o) => o ?? effect; }";
            string expected = Usings + "class C { ISrEffect M(SrOwner a) {\n" +
                              "a.effect.Reference.Inject(c); var s = a.effect.Reference?.State; ISrEffect e = a.effect.Reference; var f = flag ? a.effect.Reference : null;\n" +
                              "a.effect = new SrBurn(); a.effect.Set(e); var id = a.effect.TypeId; var n = nameof(a.effect);\n" +
                              "Use(effect: a.effect.Reference); return a.effect.Reference; } }\n" +
                              "class SrOwner { [field: SerializeReference] public ISrEffect effect { get; private set; } }\n" +
                              "class SrOwner { public StableRef<effect> effect { get; } = new(); effect effect2; ISrEffect Get() => effect.Reference; ISrEffect Or(ISrEffect o) => o ?? effect.Reference; }";
            Assert.AreEqual(expected, Typed(code, out var review));
            Assert.IsEmpty(review);
        }

        [Test]
        public void BreakingRead_OfAnUnknownName_IsLeftToTheCompiler()
        {
            string code = "class B { void M(A a) { a.effect.Apply(); if (a.effect != null) { } } }";
            Assert.AreEqual(code, Silent(code, Fields(own: false, unique: false), out var review));
            Assert.AreEqual(1, review.Count, "only the silent use is listed: the compiler reports the other");
        }

        [Test]
        public void ReceiverType_IsReadFromDeconstruction()
        {
            string code = Usings + "class C { void M(Dictionary<int, SrOwner> map, List<(int, SrOwner)> pairs) {\n" +
                          "var (level, owner) = map.First(); if (owner.effect != null) { }\n" +
                          "foreach (var (i, o) in pairs) if (o.effect is null) { } } }";
            string expected = Usings + "class C { void M(Dictionary<int, SrOwner> map, List<(int, SrOwner)> pairs) {\n" +
                              "var (level, owner) = map.First(); if (owner.effect.Reference != null) { }\n" +
                              "foreach (var (i, o) in pairs) if (o.effect.Reference is null) { } } }";
            Assert.AreEqual(expected, Typed(code, out var review));
            Assert.IsEmpty(review);
        }

        [Test]
        public void ReceiverType_IsReadFromIndexersAndSwitchArms()
        {
            string code = Usings + "class C { int M(Dictionary<int, SrOwner> map, object x) {\n" +
                          "if (map[1].effect != null) { }\n" +
                          "return x switch { SrOwner so when so.effect is SrBurn => 1, SrOwner other => other.effect == null ? 2 : 3, _ => 0 }; } }";
            string expected = Usings + "class C { int M(Dictionary<int, SrOwner> map, object x) {\n" +
                              "if (map[1].effect.Reference != null) { }\n" +
                              "return x switch { SrOwner so when so.effect.Reference is SrBurn => 1, SrOwner other => other.effect.Reference == null ? 2 : 3, _ => 0 }; } }";
            Assert.AreEqual(expected, Typed(code, out var review));
            Assert.IsEmpty(review);
        }

        [Test]
        public void PropertyPattern_OnTheRetypedField_MatchesTheValue()
        {
            string code = Usings + "class C { int M(SrOwner a, SrOther b, SrHolder h, object x) {\n" +
                          "if (a is { effect: null } || a is not { effect: not null, effect: SrBurn } || x is SrOwner { effect: null }) { }\n" +
                          "if (h is { Owner: { effect: null } } || b is { effect: null }) { }\n" +
                          "switch (a) { case { effect: null }: return 1; }\n" +
                          "return a switch { { effect: null } => 1, _ => 0 }; } }";
            string expected = Usings + "class C { int M(SrOwner a, SrOther b, SrHolder h, object x) {\n" +
                              "if (a is { effect: { Reference: null } } || a is not { effect: { Reference: not null }, effect: { Reference: SrBurn } } || x is SrOwner { effect: { Reference: null } }) { }\n" +
                              "if (h is { Owner: { effect: { Reference: null } } } || b is { effect: null }) { }\n" +
                              "switch (a) { case { effect: { Reference: null } }: return 1; }\n" +
                              "return a switch { { effect: { Reference: null } } => 1, _ => 0 }; } }";
            Assert.AreEqual(expected, Typed(code, out var review));
            Assert.IsEmpty(review, "b is SrOther: its effect is another field");
        }

        [Test]
        public void PropertyPattern_OfAnUnknownType_IsListed()
        {
            string code = "class B { void M(A a) { if (a is { effect: null }) { } } }";
            Assert.AreEqual(code, Silent(code, Fields(own: false, unique: false), out var review));
            Assert.AreEqual(1, review.Count);
        }

        [Test]
        public void AnonymousTypeMember_KeepsTheFieldsName()
        {
            string code = Usings + "class C { void M(SrOwner a) { var x = new { a.effect, N = 1 }; var y = new[] { a.effect }; } }";
            Assert.AreEqual(Usings + "class C { void M(SrOwner a) { var x = new { effect = a.effect.Reference, N = 1 }; var y = new[] { a.effect.Reference }; } }",
                Typed(code, out _));
        }

        [Test]
        public void LocalFromTheField_HoldsTheValue()
        {
            string code = Usings + "class C { void M(SrOwner a) { var r = a.effect; if (r == null || r.GetType() == null) { } } }";
            Assert.AreEqual(Usings + "class C { void M(SrOwner a) { var r = a.effect.Reference; if (r == null || r.GetType() == null) { } } }",
                Typed(code, out _));
        }

        [Test]
        public void SharedMember_Reference_ReachesTheValue()
        {
            string code = "class A { StableRef<Holder> effect = new(); int M() => effect.Reference + effect.HasValue; }";
            Assert.AreEqual("class A { StableRef<Holder> effect = new(); int M() => effect.Reference.Reference + effect.Reference.HasValue; }",
                Silent(code, Fields(own: true, unique: false, "Reference", "HasValue"), out _));
        }
    }

    // Types the silent-use tests read with reflection; SrOwner.effect is the retyped field.
    public interface ISrEffect { }
    public class SrBurn : ISrEffect { }
    public class SrOwner
    {
        public ISrEffect effect;
        public SrOwner Next() => null;
    }
    public class SrOther { public ISrEffect effect; }
    public class SrHolder
    {
        public SrOwner Owner { get; set; }
        public SrOwner[] owners;
        public bool Find(out SrOwner owner) { owner = null; return false; }
    }
    public class SrProcessorBase<TData> { protected TData Data; }
    public class SrProcessor : SrProcessorBase<SrOwner> { }
    public static class SrSink
    {
        public static void Take(object value) { }
        public static void Generic<T>(T value) { }
        public static void Typed(ISrEffect effect) { }
        public static void Wrapper(StableRef<ISrEffect> effect) { }
    }
    public class SrBox { public SrBox(object value) { } }
}
