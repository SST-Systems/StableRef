using System;
using System.Collections.Generic;
using UnityEngine;

namespace SST.StableRef.Tests
{
    public interface ITestThing { }

    public enum TestKind
    {
        A = 0,
        B = 5,
        C = 9
    }

    [Serializable]
    public class NestedData
    {
        public string Name;
        public float[] Weights;
        public UnityEngine.Object Ref;
    }

    [Serializable]
    [RefTypeId("stableref-tests.alpha")]
    public class AlphaThing : ITestThing
    {
        public int Number;
        public long BigNumber;
        public double Precise;
        public string Text;
        public TestKind Kind;
        public Color Tint;
        public Vector3 Pos;
        public List<int> Ints = new();
        public NestedData Nested = new();
        public UnityEngine.Object Direct;
        public StableRef<ITestThing> Inner = new();
    }

    [Serializable]
    [RefTypeId("stableref-tests.beta")]
    public class BetaThing : ITestThing
    {
        public int B;
    }

    [Serializable]
    [RefTypeId("stableref-tests.struct")]
    public struct StructThing : ITestThing
    {
        public int S;
    }

    [RefTypeId("stableref-tests.mono")]
    public class MonoThing : MonoBehaviour, ITestThing { }

    [Serializable]
    [RefTypeId("stableref-tests.noctor")]
    public class NoCtorThing : ITestThing
    {
        public NoCtorThing(int _) { }
    }

    [Serializable]
    public class NoIdThing : ITestThing
    {
        public int N;
    }

    public class TestHolder : ScriptableObject
    {
        public StableRef<ITestThing> Ref = new();
        public StableRefList<ITestThing> List = new();
        [SerializeReference, RefSelector] public ITestThing Plain;
    }
}
