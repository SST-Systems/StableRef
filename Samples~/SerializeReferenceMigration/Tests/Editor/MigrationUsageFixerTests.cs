using System.Collections.Generic;
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
            Assert.AreEqual("o.effect.Value.Apply();", Fix("o.effect.Apply();", "CS1061", "Apply", NoMember));
            Assert.AreEqual("o.effect.Value?.Apply();", Fix("o.effect?.Apply();", "CS1061", "?.", NoMember, 1),
                "x?.M is reported at the dot");
            Assert.AreEqual("o.effects.Count", Fix("o.effects.Length", "CS1061", "Length",
                "'StableRefList<IEffect>' does not contain a definition for 'Length'"));
        }

        [Test]
        public void Read_GetsValue_ConditionalBranchesToo()
        {
            const string convert = "Cannot implicitly convert type 'SST.StableRef.StableRef<IEffect>' to 'IEffect'";
            Assert.AreEqual("IEffect e = o.effect.Value;", Fix("IEffect e = o.effect;", "CS0029", "o.effect", convert));
            Assert.AreEqual("IEffect e = f && g ? o.effect.Value : null;",
                Fix("IEffect e = f && g ? o.effect : null;", "CS0029", "f &&", convert), "reported at the condition");
            Assert.AreEqual("Use(f ? new Burn() : o.effect.Value);",
                Fix("Use(f ? new Burn() : o.effect);", "CS1503", "f ?",
                    "Argument 1: cannot convert from 'target-typed conditional expression' to 'IEffect'"));
            Assert.AreEqual("var b = (Burn)o.effect.Value;", Fix("var b = (Burn)o.effect;", "CS0030", "(Burn)",
                "Cannot convert type 'SST.StableRef.StableRef<IEffect>' to 'Burn'"));
            Assert.AreEqual("IEffect[] a = o.effects.ToArray();", Fix("IEffect[] a = o.effects;", "CS0029", "o.effects",
                "Cannot implicitly convert type 'SST.StableRef.StableRefList<IEffect>' to 'IEffect[]'"));
        }

        [Test]
        public void LocalizedMessage_IsRead()
        {
            Assert.AreEqual("o.effect.Value.Apply();", Fix("o.effect.Apply();", "CS1061", "Apply",
                "\"StableRef<IEffect>\" не содержит определения \"Apply\""));
        }

        [Test]
        public void Patterns_FixTheSubject_NotThePattern()
        {
            const string pattern = "An expression of type 'StableRef<IEffect>' cannot be handled by a pattern of type 'Burn'.";
            Assert.AreEqual("if (o.effect.Value is not Burn b) { }", Fix("if (o.effect is not Burn b) { }", "CS8121", "Burn", pattern));
            Assert.AreEqual("if (o.effect.Value is Freeze or Burn) { }", Fix("if (o.effect is Freeze or Burn) { }", "CS8121", "Burn", pattern),
                "a later pattern of an `or` finds the same subject");
            Assert.AreEqual("switch (o.effect.Value) { case Freeze: break; case Burn x: break; }",
                Fix("switch (o.effect) { case Freeze: break; case Burn x: break; }", "CS8121", "Burn", pattern));
            Assert.AreEqual("int p = o.effect.Value switch { Freeze => 1, Burn => 2, _ => 0 };",
                Fix("int p = o.effect switch { Freeze => 1, Burn => 2, _ => 0 };", "CS8121", "Burn", pattern));
        }

        [Test]
        public void Assignment_UsesSet_OrValue_OrWrapsInitializer()
        {
            const string convert = "Cannot implicitly convert type 'Burn' to 'SST.StableRef.StableRef<IEffect>'";
            Assert.AreEqual("if (f) o.effect.Set(new Burn());", Fix("if (f) o.effect = new Burn();", "CS0029", "new", convert));
            Assert.AreEqual("o.effect.Value = f\n    ? new Burn()\n    : null;",
                Fix("o.effect = f\n    ? new Burn()\n    : null;", "CS0029", "new", convert), "a value over several lines isn't wrapped");
            Assert.AreEqual("new Owner { effect = new(new Burn()) }", Fix("new Owner { effect = new Burn() }", "CS0029", "new Burn", convert));
            Assert.AreEqual("o.effects = new();", Fix("o.effects = new List<IEffect>();", "CS0029", "new",
                "Cannot implicitly convert type 'System.Collections.Generic.List<IEffect>' to 'SST.StableRef.StableRefList<IEffect>'"));
        }

        [Test]
        public void CommentsAndStrings_AreNotCode_InterpolationHolesAre()
        {
            Assert.AreEqual("var s = $\"{o.effect.Value.Power:D2}\"; // o.effect.Power",
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
                              "void M() { if (effect.Value != null && null != this.effect.Value && effect.Value is not null && effect.Value is { }) { } var t = effect.Value.GetType(); effect.Value = null; if (effects != null) { } } }";
            Assert.AreEqual(expected, Silent(code, Fields(own: true, unique: false), out var review));
            Assert.IsEmpty(review);
        }

        [Test]
        public void SilentChecks_Ambiguous_AreListed_NotChanged()
        {
            string shadowed = "class A { StableRef<IEffect> effect = new(); void M(IEffect effect) { if (effect != null) { } } }";
            Assert.AreEqual(shadowed, Silent(shadowed, Fields(own: true, unique: true), out var review));
            Assert.AreEqual(1, review.Count, "a parameter of the same name: can't tell");

            string qualified = "class B { void M(A a) { if (a.effect != null) { } } }";
            Assert.AreEqual(qualified, Silent(qualified, Fields(own: false, unique: false), out review));
            Assert.AreEqual(1, review.Count, "another member has the name");
            Assert.AreEqual("class B { void M(A a) { if (a.effect.Value != null) { } } }",
                Silent(qualified, Fields(own: false, unique: true), out review));

            string otherFile = "class B { void M(IEffect effect) { if (effect != null) { } } }";
            Assert.AreEqual(otherFile, Silent(otherFile, Fields(own: false, unique: true), out review));
            Assert.IsEmpty(review, "an unqualified name in another file is not the field");
        }

        [Test]
        public void SharedMember_Value_ReachesTheValue()
        {
            string code = "class A { StableRef<Holder> effect = new(); int M() => effect.Value + effect.HasValue; }";
            Assert.AreEqual("class A { StableRef<Holder> effect = new(); int M() => effect.Value.Value + effect.Value.HasValue; }",
                Silent(code, Fields(own: true, unique: false, "Value", "HasValue"), out _));
        }
    }
}
