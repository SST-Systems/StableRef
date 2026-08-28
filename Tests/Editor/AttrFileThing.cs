using System;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Lives in its own file (name matches the class) so it resolves through its MonoScript GUID,
    /// while also carrying an explicit id — used to verify the attribute id stays canonical.
    /// </summary>
    [Serializable]
    [StableTypeId("stableref-tests.attr-file")]
    public class AttrFileThing : ITestThing
    {
        public int Payload;
    }
}
