using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SST.StableRef.Migration;

namespace SST.StableRef.Tests
{
    /// <summary>Declarations of <c>[SerializeReference]</c> fields retyped in place to StableRef.</summary>
    public class MigrationCodeRewriterTests
    {
        private interface ICandidate { }
        private class Candidate : ICandidate { }
        private abstract class AbstractCandidate : ICandidate { }
        private class CandidateWithoutDefaultCtor : ICandidate { public CandidateWithoutDefaultCtor(int value) { } }
        private struct StructCandidate : ICandidate { }
        private class GenericCandidate<T> : ICandidate { }

        private static List<string> Rewrite(string name, params string[] source) => Rewrite(name, false, false, source);

        private static List<string> Rewrite(string name, bool isProperty, params string[] source) => Rewrite(name, isProperty, false, source);

        private static List<string> Rewrite(string name, bool isProperty, bool addInitializer, params string[] source)
        {
            var lines = source.ToList();
            Assert.IsTrue(MigrationCodeRewriter.TryRewriteDeclaration(lines, name, isProperty, addInitializer,
                out var declaration, out string problem), problem);
            lines.RemoveRange(declaration.Start, declaration.End - declaration.Start);
            lines.InsertRange(declaration.Start, declaration.Lines);
            return lines;
        }

        [Test]
        public void PublicField_IsRetyped_AttributeRemoved()
        {
            CollectionAssert.AreEqual(new[] { "    public StableRef<IFoo> Effect;" },
                Rewrite("Effect", "    [SerializeReference] public IFoo Effect;"));
        }

        [Test]
        public void PrivateField_GetsSerializeField()
        {
            CollectionAssert.AreEqual(new[] { "[SerializeField] private StableRef<IFoo> effect;" },
                Rewrite("effect", "[SerializeReference] private IFoo effect;"));
            CollectionAssert.AreEqual(new[] { "[SerializeField] private StableRef<IFoo> effect;" },
                Rewrite("effect", "[SerializeField, SerializeReference] private IFoo effect;"), "an existing [SerializeField] is kept, not doubled");
            CollectionAssert.AreEqual(new[] { "[UnityEngine.SerializeField] StableRef<IFoo> effect;" },
                Rewrite("effect", "[UnityEngine.SerializeReference] IFoo effect;"), "no modifier is private; the qualifier stays");
        }

        [Test]
        public void AttributeOnTheLineAbove_IsReplacedThere()
        {
            CollectionAssert.AreEqual(new[] { "    [SerializeField] // why", "    [Tooltip(\"t\")] protected StableRef<IFoo> effect;" },
                Rewrite("effect", "    [SerializeReference] // why", "    [Tooltip(\"t\")] protected IFoo effect;"));
        }

        [Test]
        public void ListField_KeepsAttributes_DropsArrayDrawers_KeepsComment()
        {
            CollectionAssert.AreEqual(new[]
            {
                "        [Tooltip(\"x, [y]\")]",
                "        [SerializeField] private StableRefList<IFoo> _items = new(); // note"
            }, Rewrite("_items",
                "        [Tooltip(\"x, [y]\")]",
                "        [SerializeReference, ListDrawerSettings(ShowFoldout = true)] private List<IFoo> _items = new List<IFoo>(); // note"));
        }

        [Test]
        public void ArrayField_BecomesList_KeepsVisibility()
        {
            CollectionAssert.AreEqual(new[] { "[SerializeField] protected StableRefList<IFoo> array;" },
                Rewrite("array", "[SerializeReference] protected IFoo[] array;"));
        }

        [Test]
        public void ListWithValues_CopiesThemIn()
        {
            Assert.AreEqual("public StableRefList<IFoo> Steps = new(new List<IFoo> { new Foo() });",
                Rewrite("Steps", "[SerializeReference] public List<IFoo> Steps = new List<IFoo> { new Foo() };")[0]);
        }

        [Test]
        public void ListWithTargetTypedInitializer_FillsTheListItself()
        {
            Assert.AreEqual("public StableRefList<IFoo> Steps = new() { new Foo() };",
                Rewrite("Steps", "[SerializeReference] public List<IFoo> Steps = new() { new Foo() };")[0]);
        }

        [Test]
        public void TypeIdCandidates_AreTheSelectableClasses()
        {
            var warnings = new List<string>();
            CollectionAssert.AreEquivalent(new[] { typeof(Candidate) },
                MigrationCodeRewriter.TypeIdCandidates(new[] { typeof(ICandidate) }, warnings));
            CollectionAssert.IsEmpty(warnings);

            CollectionAssert.IsEmpty(MigrationCodeRewriter.TypeIdCandidates(new[] { typeof(object) }, warnings),
                "a field of type object would put [RefTypeId] on every class of the project");
            Assert.AreEqual(1, warnings.Count);
        }

        [Test]
        public void AutoProperty_IsRetyped_BackingFieldGetsSerializeField()
        {
            CollectionAssert.AreEqual(new[]
            {
                "    [field: SerializeField, Tooltip(\"t\")] [JsonIgnore] public StableRef<IFoo> Prop { get; private set; }"
            }, Rewrite("Prop", true,
                "    [field: SerializeReference, Tooltip(\"t\")] [JsonIgnore] public IFoo Prop { get; private set; }"));
        }

        [Test]
        public void Initializer_IsWrapped_OtherAttributesStay()
        {
            CollectionAssert.AreEqual(new[] { "[JsonProperty(\"e\")] public StableRef<IFoo> Json = new(new Foo());" },
                Rewrite("Json", "[SerializeReference, JsonProperty(\"e\")] public IFoo Json = new Foo();"));
        }

        [Test]
        public void FieldWithoutSerializeReference_IsNotTouched()
        {
            var lines = new List<string> { "[SerializeField] public IFoo Effect;" };
            Assert.IsFalse(MigrationCodeRewriter.TryRewriteDeclaration(lines, "Effect", false, false, out _, out _));
        }

        [Test]
        public void PlainClassField_GetsAnInitializer()
        {
            CollectionAssert.AreEqual(new[] { "public StableRef<IFoo> Effect = new(); // note" },
                Rewrite("Effect", false, true, "[SerializeReference] public IFoo Effect; // note"));
            CollectionAssert.AreEqual(new[] { "[SerializeField] private StableRefList<IFoo> items = new();" },
                Rewrite("items", false, true, "[SerializeReference] private List<IFoo> items;"));
            CollectionAssert.AreEqual(new[] { "[field: SerializeField] public StableRef<IFoo> Prop { get; set; } = new(); // }" },
                Rewrite("Prop", true, true, "[field: SerializeReference] public IFoo Prop { get; set; } // }"));
            CollectionAssert.AreEqual(new[] { "public StableRef<IFoo> Effect = new(new Foo());" },
                Rewrite("Effect", false, true, "[SerializeReference] public IFoo Effect = new Foo();"), "an existing initializer is wrapped, not doubled");
        }

        [Test]
        public void Initializer_IsAddedOnlyWhereUnityWouldLeaveTheFieldNull()
        {
            Assert.IsTrue(MigrationCodeRewriter.NeedsInitializer(typeof(Candidate)), "a plain class made with new");
            Assert.IsFalse(MigrationCodeRewriter.NeedsInitializer(typeof(MigrationTestAsset)), "Unity creates it for a ScriptableObject");
            Assert.IsFalse(MigrationCodeRewriter.NeedsInitializer(typeof(StructCandidate)), "a struct can't have field initializers in C# 9");
        }

        [Test]
        public void Usings_GoAfterTheLastOneOutsideIf()
        {
            var lines = new List<string> { "using System;", "#if UNITY_EDITOR", "using UnityEditor;", "#endif", "", "namespace A {}" };
            MigrationCodeRewriter.EnsureUsings(lines, "SST.StableRef", "System");
            CollectionAssert.AreEqual(
                new[] { "using System;", "using SST.StableRef;", "#if UNITY_EDITOR", "using UnityEditor;", "#endif", "", "namespace A {}" },
                lines);
        }

        [Test]
        public void Usings_InsideIfOnly_AreAddedOutside()
        {
            var lines = new List<string> { "using System;", "#if UNITY_EDITOR", "using SST.StableRef;", "#endif", "", "namespace A {}" };
            MigrationCodeRewriter.EnsureUsings(lines, "SST.StableRef");
            CollectionAssert.AreEqual(
                new[] { "using System;", "using SST.StableRef;", "#if UNITY_EDITOR", "using SST.StableRef;", "#endif", "", "namespace A {}" },
                lines);
        }

        [Test]
        public void AssemblyReference_IsAddedByNameOrGuid_Once()
        {
            const string Guid = "901692b1b79fb4440b8cf9ba20ae6957";
            string byName = MigrationCodeRewriter.AddReference("{\n    \"name\": \"A\",\n    \"references\": [\n        \"B\"\n    ],\n}", "SST.StableRef", Guid);
            StringAssert.Contains("\"B\",\n        \"SST.StableRef\"\n    ]", byName);

            string byGuid = MigrationCodeRewriter.AddReference("{\n    \"references\": [\"GUID:abc\"]\n}", "SST.StableRef", Guid);
            StringAssert.Contains("\"GUID:" + Guid + "\"", byGuid);

            string empty = MigrationCodeRewriter.AddReference("{\n    \"references\": []\n}", "SST.StableRef", Guid);
            StringAssert.Contains("\"SST.StableRef\"", empty);

            Assert.AreEqual(byName, MigrationCodeRewriter.AddReference(byName, "SST.StableRef", Guid));
        }
    }
}
