using System;

namespace SST.StableRef.Tests
{
    public interface IMigrationThing { }

    [Serializable, RefTypeId("stableref-tests.migration.thing-a")]
    public class MigrationThingA : IMigrationThing
    {
        public int Number;
        public StableRef<IMigrationThing> Child = new();
    }

    [Serializable, RefTypeId("stableref-tests.migration.thing-b")]
    public class MigrationThingB : IMigrationThing
    {
        public string Name;
    }

    [Serializable]
    public class MigrationNested
    {
        public StableRefList<IMigrationThing> Items = new();
    }
}
