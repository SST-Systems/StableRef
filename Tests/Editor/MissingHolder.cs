using UnityEngine;

namespace SST.StableRef.Tests
{
    /// <summary>
    /// Lives in its own file so it can be loaded from a test asset — the asset references classes that don't
    /// exist, which is the only way to get Unity's native missing-type records in a test.
    /// </summary>
    public class MissingHolder : ScriptableObject
    {
        [SerializeReference] public ITestThing A;
        [SerializeReference] public ITestThing B;
        public StableRef<ITestThing> Ref = new();
    }
}
