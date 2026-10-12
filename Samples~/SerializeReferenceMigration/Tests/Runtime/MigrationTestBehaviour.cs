using UnityEngine;

namespace SST.StableRef.Tests
{
    public class MigrationTestBehaviour : MonoBehaviour
    {
        public StableRef<IMigrationThing> Single = new();
        public StableRef<IMigrationThing> Other = new();
        public StableRefList<IMigrationThing> List = new();
    }
}
