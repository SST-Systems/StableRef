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
    [StableTypeId("stableref-tests.alpha")]
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
    [StableTypeId("stableref-tests.beta")]
    public class BetaThing : ITestThing
    {
        public int B;
    }

    [Serializable]
    [StableTypeId("stableref-tests.struct")]
    public struct StructThing : ITestThing
    {
        public int S;
    }

    [StableTypeId("stableref-tests.mono")]
    public class MonoThing : MonoBehaviour, ITestThing { }

    [Serializable]
    [StableTypeId("stableref-tests.noctor")]
    public class NoCtorThing : ITestThing
    {
        public NoCtorThing(int _) { }
    }

    public class TestHolder : ScriptableObject
    {
        public StableRef<ITestThing> Ref = new();
        public StableRefList<ITestThing> List = new();
    }
}
