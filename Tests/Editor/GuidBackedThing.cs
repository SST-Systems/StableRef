using System;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Lives in its own file and has no [StableTypeId] — its stable id is the MonoScript GUID.
    /// </summary>
    [Serializable]
    public class GuidBackedThing : ITestThing
    {
        public int G;
    }
}
