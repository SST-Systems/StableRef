using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace SST.StableRef.Tests
{
    /// <summary>Fields that were <c>[SerializeReference]</c> before and are StableRef now, in every supported place.</summary>
    public class MigrationTestAsset : ScriptableObject
    {
        public StableRef<IMigrationThing> Single = new();
        public StableRef<IMigrationThing> Empty = new();
        public StableRefList<IMigrationThing> List = new();
        [FormerlySerializedAs("oldRenamed")] public StableRefList<IMigrationThing> Renamed = new();
        public StableRefList<IMigrationThing> NoItems = new();
        public MigrationNested Nested = new();
        public List<MigrationNested> NestedArray = new();
        /// <summary>Still a plain <c>[SerializeReference]</c> field; its value holds a StableRef.</summary>
        [SerializeReference] public IMigrationThing Plain;
        [field: SerializeField] public StableRefList<IMigrationThing> Property { get; private set; } = new();
    }
}
