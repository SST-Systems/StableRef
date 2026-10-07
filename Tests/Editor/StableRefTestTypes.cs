using System;
using System.Collections.Generic;
using SST.StableRef;
using SST.StableRef.Tests;
using UnityEngine;

[assembly: RefTypeIdFor(typeof(MappedThing), "stableref-tests.mapped")]
[assembly: RefTypeIdFor(typeof(MappedGeneric<>), "stableref-tests.mapped-generic")]

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

    /// <summary>Derives from a [RefTypeId] type without an id of its own, in a shared file — so it has no stable id.</summary>
    [Serializable]
    public class AlphaChild : AlphaThing
    {
        public int Extra;
    }

    /// <summary>No [RefTypeId], shares a file — its id comes from [assembly: RefTypeIdFor].</summary>
    [Serializable]
    public class MappedThing : ITestThing
    {
        public int M;
    }

    [Serializable]
    public class MappedGeneric<TArg> : ITestThing
    {
        public TArg Arg;
    }

    [Serializable]
    [RefTypeId("stableref-tests.meta")]
    public class MetaThing : ITestThing { }

    public class TestMetadataProvider : IRefTypeMetadataProvider
    {
        public int Order => 0;

        public bool TryGetMetadata(Type type, out RefTypeMetadata metadata)
        {
            metadata = default;
            if (type != typeof(MetaThing)) return false;
            metadata = new RefTypeMetadata { DisplayName = "Pretty Meta", Category = "Meta/Sub", Tooltip = "tip", Color = Color.red, SortOrder = -5 };
            return true;
        }
    }

    [Serializable]
    public class PlainValueHolder
    {
        [SerializeReference, RefSelector] public ITestThing Value;
    }

    public class TestHolder : ScriptableObject
    {
        public StableRef<ITestThing> Ref = new();
        public StableRefList<ITestThing> List = new();
        [SerializeReference, RefSelector] public ITestThing Plain;
        public List<StableRef<ITestThing>> RawList = new();
        public StableRef<ITestThing>[] Arr = new StableRef<ITestThing>[0];
        [SerializeReference, RefSelector] public List<ITestThing> PlainList = new();
        public List<PlainValueHolder> Holders = new();
    }
}
