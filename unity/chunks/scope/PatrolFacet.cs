// Reference implementation of IPatrolFacet. Author places this component under
// _Logic in a chunk scene that has a patrol route, and drags the two waypoint
// Transforms into the inspector.
//
// Serves as the template for future facets: MonoBehaviour + facet interface +
// inspector-authored data, discovered by ChunkLifetimeScope via
// GetComponentInChildren.

using UnityEngine;

namespace ProjectName.World
{
    public sealed class PatrolFacet : MonoBehaviour, IPatrolFacet
    {
        [SerializeField] Transform pointA;
        [SerializeField] Transform pointB;

        public Transform PointA => pointA;
        public Transform PointB => pointB;
    }
}
