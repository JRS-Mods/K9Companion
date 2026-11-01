using EFT;
using EFT.Ballistics;

namespace K9Companion
{
    // Bullets from players and AI reach any BallisticCollider on the HitCollider layer; Body material gives the usual blood impact.
    internal sealed class DogHitbox : BallisticCollider
    {
        private Companion owner;
        private float multiplier;

        internal void Init(Companion companion, float damageMultiplier)
        {
            owner = companion; multiplier = damageMultiplier;
            TypeOfMaterial = MaterialType.Body;
            PenetrationChance = 0; RicochetChance = 0; FragmentationChance = 0; TrajectoryDeviationChance = 0;
        }

        public override PlayerHitInfo ApplyHit(DamageInfo damageInfo, ShotId shotID)
        {
            base.ApplyHit(damageInfo, shotID);
            if (owner != null) owner.TakeHit(damageInfo, multiplier);
            return null;
        }
    }
}
