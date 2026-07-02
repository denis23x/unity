// Concrete IPlayerService: a MonoBehaviour on the player GameObject in the
// bootstrap scene. RootLifetimeScope discovers it via RegisterComponentInHierarchy
// so consumers just [Inject] IPlayerService, wherever they live (bootstrap
// services or per-chunk NPCs).
//
// Deliberately thin — grow it (Health, Inventory, OnDeath, …) as concrete needs
// appear. Do not accumulate unused fields "in case".

using UnityEngine;

namespace ProjectName.World
{
    public sealed class PlayerService : MonoBehaviour, IPlayerService
    {
        public Transform Transform => transform;
    }
}
