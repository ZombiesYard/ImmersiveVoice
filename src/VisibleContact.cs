using EFT;
using EFT.Interactive;
using UnityEngine;

namespace ImmersiveVoice
{
    internal sealed class VisibleContact
    {
        public string Id;
        public Player Player;
        public Collider Collider;
        public Transform Anchor;
        public bool Alive, Ally, Corpse;
        public float Distance;
    }

    internal sealed class ViewProbe
    {
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        // Same solid/body layers the game uses, plus doors and corpse colliders.
        private readonly int _mask = (int)LayersMaskController.HighPolyWithTerrainMask
            | (int)LayersMaskController.HitColliderMask | (int)LayersMaskController.PlayerMask
            | (int)LayersMaskController.PlayerStaticDoorMask | (1 << LayersMaskController.DeadbodyLayer);
        public string LastReason { get; private set; }

        public VisibleContact Gaze(Player local, float range, FikaBridge fika)
        {
            return Trace(local, local.InteractionRay, range, fika);
        }

        public VisibleContact Track(Player local, VisibleContact known, float range, FikaBridge fika)
        {
            if (known == null) { LastReason = "NoTrackedTarget"; return null; }
            bool colliderActive = known.Collider != null && known.Collider.enabled && known.Collider.gameObject.activeInHierarchy;
            if (!colliderActive && known.Anchor == null) { LastReason = "TargetAnchorGone"; return null; }
            Ray view = local.InteractionRay;
            // Body colliders can be disabled/replaced as the character becomes a
            // ragdoll. A cached pelvis remains a probe destination, never death evidence.
            Vector3 point = colliderActive ? known.Collider.bounds.center : known.Anchor.position;
            Vector3 delta = point - view.origin;
            if (delta.sqrMagnitude > range * range) { LastReason = "OutsideVisualRange"; return null; }
            if (delta.sqrMagnitude < 0.01f || Vector3.Dot(view.direction, delta.normalized) < 0.7071f)
            { LastReason = "OutsideViewCone"; return null; } // 45 degrees, known targets only
            VisibleContact hit = Trace(local, new Ray(view.origin, delta.normalized), delta.magnitude + 0.5f, fika);
            if (hit != null && hit.Id != known.Id) LastReason = "DifferentVisibleTarget";
            return hit != null && hit.Id == known.Id ? hit : null;
        }

        // Budgeted by SquadObserver. No contact object is allocated by this probe.
        public bool TeamVisible(Player local, Player teammate, float range, bool requireFacing = true)
        {
            Ray view = local.InteractionRay;
            Vector3 delta = teammate.Transform.position + Vector3.up * 1.1f - view.origin;
            float distance = delta.magnitude;
            if (distance < 0.1f || distance > range || (requireFacing && Vector3.Dot(view.direction, delta / distance) < 0.25f)) return false;
            int count = Physics.RaycastNonAlloc(new Ray(view.origin, delta / distance), _hits,
                distance + 0.5f, _mask, QueryTriggerInteraction.Collide);
            if (count == _hits.Length) return false;
            float nearest = float.MaxValue;
            Player seen = null;
            for (int i = 0; i < count; i++)
            {
                Collider collider = _hits[i].collider;
                if (collider == null) continue;
                var part = collider.GetComponent<BodyPartCollider>();
                Player owner = part?.Player as Player ?? collider.GetComponentInParent<Player>();
                if (owner == local || part != null && part.PlayerProfileID == local.ProfileId) continue;
                if (_hits[i].distance < nearest) { nearest = _hits[i].distance; seen = owner; }
            }
            return seen == teammate;
        }

        private VisibleContact Trace(Player local, Ray ray, float range, FikaBridge fika)
        {
            int count = Physics.RaycastNonAlloc(ray, _hits, range, _mask, QueryTriggerInteraction.Collide);
            if (count == _hits.Length) { LastReason = "RayBufferFull"; return null; }
            float nearest = float.MaxValue;
            Collider first = null;
            for (int i = 0; i < count; i++)
            {
                Collider collider = _hits[i].collider;
                if (collider == null) continue;
                Player owner = collider.GetComponentInParent<Player>();
                if (owner == local) continue;
                var body = collider.GetComponent<BodyPartCollider>();
                if (body != null && body.PlayerProfileID == local.ProfileId) continue;
                if (_hits[i].distance < nearest) { nearest = _hits[i].distance; first = collider; }
            }
            if (first == null) { LastReason = "NoRayHit"; return null; }
            // Check corpse first: death may retain a pooled Player component.
            var corpse = first.GetComponentInParent<Corpse>();
            if (corpse != null)
            {
                LastReason = "VisibleCorpse";
                return new VisibleContact { Id = corpse.PlayerProfileID, Collider = first, Anchor = first.transform,
                    Alive = false, Corpse = true, Distance = nearest };
            }
            var part = first.GetComponent<BodyPartCollider>();
            Player target = part?.Player as Player ?? first.GetComponentInParent<Player>();
            if (target == null || target == local || target.HealthController == null)
            { LastReason = "OccludedOrUnidentified"; return null; }
            if (fika.Downed(target)) { LastReason = "RevivableDowned"; return null; }
            LastReason = target.HealthController.IsAlive ? "VisibleLiving" : "VisibleDeadPlayer";
            return new VisibleContact { Id = target.ProfileId, Player = target, Collider = first,
                Anchor = target.PlayerBones?.Pelvis?.Original ?? first.transform,
                Alive = target.HealthController.IsAlive, Ally = IsAlly(local, target), Distance = nearest };
        }

        public static bool RecognizableEnemy(Player local, VisibleContact seen)
        {
            Player target = seen?.Player;
            if (target == null || !seen.Alive) return false;
            WildSpawnType? role = target.Profile?.Info?.Settings?.Role;
            bool ordinaryScav = role == WildSpawnType.assault || role == WildSpawnType.cursedAssault || role == WildSpawnType.marksman;
            return VisualRules.RecognizableEnemy(seen.Ally, target.IsAI,
                local.Side == EPlayerSide.Bear || local.Side == EPlayerSide.Usec,
                target.Side == EPlayerSide.Bear || target.Side == EPlayerSide.Usec, ordinaryScav);
        }

        internal static bool IsAlly(Player local, IPlayer other)
        {
            string group = local.GroupId;
            return other != null && !string.IsNullOrEmpty(group) && group != "0"
                && string.Equals(group, other.GroupId, System.StringComparison.Ordinal);
        }
    }
}
