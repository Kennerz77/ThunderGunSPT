using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>A tuned value fixed in the mod (the user's playtested numbers), read like a setting.</summary>
    internal sealed class Fixed<T>
    {
        public readonly T Value;

        public Fixed(T value) => Value = value;
    }

    /// <summary>
    /// The Thundergun's numbers, fixed at the values tuned in playtesting (2026-10-27). Only the volumes are in the F12
    /// menu.
    /// </summary>
    internal static class ThundergunSettings
    {
        public static readonly Fixed<float> KillRange = new Fixed<float>(12f), KillCone = new Fixed<float>(70f), PushRange = new Fixed<float>(14f);
        public static readonly Fixed<float> ZeusKillRange = new Fixed<float>(16f), ZeusKillCone = new Fixed<float>(90f), ZeusPushRange = new Fixed<float>(18f);
        public static readonly Fixed<float> FlingSpeed = new Fixed<float>(8f), FlingLift = new Fixed<float>(7f), StunSeconds = new Fixed<float>(3f);
        public static readonly Fixed<float> MovementPenalty = new Fixed<float>(15f), TurnPenalty = new Fixed<float>(12f);
        public static readonly Fixed<bool> ShowWave = new Fixed<bool>(true);

        public static ConfigEntry<float> ShotVolume, FoleyVolume, PurchaseVolume, WallBuyVolume, DenyVolume;

        public static void Bind(ConfigFile config)
        {
            const string audio = "Volume";
            ShotVolume = config.Bind(audio, "Shot", 1f, Volume("The Thundergun's blast. (The Zeus Cannon's BO3 shot on top is Pack-a-Punch's \"Pack-a-Punch shot\" slider.)"));
            FoleyVolume = config.Bind(audio, "Reload and handling", 1f, Volume("Drawing, reloading, the tech beep, the dry click and the gun's rattle."));
            PurchaseVolume = config.Bind(audio, "Purchase", 0.8f, Volume("BO3's \"cha-ching\" when you buy from the wall or the ammo box."));
            WallBuyVolume = config.Bind(audio, "Wall buy", 0.8f, Volume("The sound of the gun coming out of the chalk wall."));
            DenyVolume = config.Bind(audio, "Deny", 0.8f, Volume("BO3's buzz when a purchase is refused (not enough money, no room)."));
            DropOldEntries(config);
        }

        /// <summary>
        /// Settings from earlier builds (blast tuning, menu pose, station buttons) stay in the file as orphans; they're
        /// dropped so the file, and F12, hold the volumes only.
        /// </summary>
        private static void DropOldEntries(ConfigFile config)
        {
            try
            {
                if (AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) is System.Collections.IDictionary orphans && orphans.Count > 0)
                {
                    orphans.Clear();
                    config.Save();
                }
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log?.LogWarning($"Couldn't tidy old settings: {e.Message}");
            }
        }

        private static ConfigDescription Volume(string description) => new ConfigDescription(description, new AcceptableValueRange<float>(0f, 1f));
    }

    /// <summary>
    /// What the Thundergun does when it fires. There's no bullet (the charge has no speed, so the game fires none):
    /// instead every enemy in a cone in front of the gun, in clear line of sight, is checked at once. Close enough, it
    /// dies, credited to the player and the gun like any kill (stats, quests), and its body is thrown backwards and up;
    /// a little further out, it's stunned. The player and their group are never touched. The visible wave is played
    /// along the shot from a template in the gun's own bundle.
    /// </summary>
    internal static class ThundergunBlast
    {
        private static readonly AccessTools.FieldRef<Player, Corpse> CorpseOf = AccessTools.FieldRefAccess<Player, Corpse>("Corpse");

        // Bodies the blast just killed, and how fast to throw them, until the game makes their corpse
        private static readonly Dictionary<Player, (Vector3 velocity, float at)> ToFling = new Dictionary<Player, (Vector3, float)>();

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)),
                postfix: new HarmonyMethod(Method(nameof(ShotPostfix))));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.ApplyCorpseImpulse)),
                new HarmonyMethod(Method(nameof(CorpseImpulsePrefix))));
        }

        private static MethodInfo Method(string name) => typeof(ThundergunBlast).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // Runs for the local player's shots only (enemies never carry a Thundergun)
        private static void ShotPostfix(Player.FirearmController __instance, IWeapon weapon, Vector3 shotPosition, Vector3 shotDirection)
        {
            try
            {
                Player player = __instance._player;
                if (!(weapon?.Item is Weapon gun) || !ThundergunIds.Is(gun) || player == null || !player.IsYourPlayer)
                    return;
                Fire(player, __instance, gun, shotPosition, shotDirection.normalized);
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogError($"Thundergun blast failed: {e}");
            }
        }

        private static void Fire(Player player, Player.FirearmController hands, Weapon gun, Vector3 origin, Vector3 forward)
        {
            bool zeus = ThundergunPaP.IsInfused(gun);
            float killRange = zeus ? ThundergunSettings.ZeusKillRange.Value : ThundergunSettings.KillRange.Value;
            float cone = zeus ? ThundergunSettings.ZeusKillCone.Value : ThundergunSettings.KillCone.Value;
            float pushRange = Mathf.Max(killRange, zeus ? ThundergunSettings.ZeusPushRange.Value : ThundergunSettings.PushRange.Value);

            if (ThundergunSettings.ShowWave.Value)
                PlayWave(hands, origin, forward, zeus);
            if (!Singleton<GameWorld>.Instantiated)
                return;
            GameWorld world = Singleton<GameWorld>.Instance;

            int killed = 0, stunned = 0;
            foreach (Player target in world.AllAlivePlayersList.ToList())
            {
                if (target == null || target == player || !target.HealthController.IsAlive || Spared(player, target))
                    continue;
                Vector3 chest = Chest(target);
                Vector3 to = chest - origin;
                float distance = to.magnitude;
                // Point blank the cone is wider: the muzzle is ahead of the body it's pressed against
                float halfAngle = (distance < 1.5f ? Mathf.Max(cone, 140f) : cone) * 0.5f;
                if (distance > pushRange || Vector3.Angle(forward, to) > halfAngle)
                    continue;
                // Walls and cover stop the air; fences, grates, glass and the like let it through
                if (Blocked(origin, chest))
                    continue;

                if (distance <= killRange)
                {
                    Kill(player, target, gun, origin, forward, chest);
                    killed++;
                }
                else
                {
                    target.ActiveHealthController?.DoStun(ThundergunSettings.StunSeconds.Value, 1f);
                    target.ActiveHealthController?.DoContusion(ThundergunSettings.StunSeconds.Value, 1f);
                    stunned++;
                }
            }
            if (killed + stunned > 0)
                ThundergunPlugin.Log.LogInfo($"{(zeus ? ThundergunIds.UpgradedName : ThundergunIds.Name)} blast: {killed} killed, {stunned} stunned");
        }

        // What a rush of air goes through or past: open mesh, glass, plants, cloth, paper, thin plastic, and bodies
        // (so it carries on to whoever's behind). Anything else in the way stops it
        private static readonly HashSet<MaterialType> AirPasses = new HashSet<MaterialType>
        {
            MaterialType.Chainfence, MaterialType.Grate, MaterialType.Glass, MaterialType.GlassShattered, MaterialType.GlassVisor,
            MaterialType.GrassHigh, MaterialType.GrassLow, MaterialType.Fabric, MaterialType.Cardboard, MaterialType.GarbagePaper,
            MaterialType.Plastic, MaterialType.GenericSoft, MaterialType.Body, MaterialType.BodyArmor, MaterialType.Helmet,
            MaterialType.HelmetRicochet, MaterialType.Water, MaterialType.WaterPuddle,
        };

        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        /// <summary>True if something solid stands between the gun and the target, by the surfaces bullets see.</summary>
        private static bool Blocked(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float distance = dir.magnitude;
            int count = Physics.RaycastNonAlloc(from, dir / distance, Hits, distance, BallisticsCalculatorConstants.StaticObjectsHitMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = Hits[i].collider;
                if (collider == null)
                    continue;
                BallisticCollider surface = collider.GetComponent<BaseBallistic>()?.Get(Hits[i].point);
                // Unmarked colliders are scenery: solid
                if (surface == null || !AirPasses.Contains(surface.TypeOfMaterial))
                    return true;
            }
            return false;
        }

        /// <summary>The shooter's own group (and any other human player) are left alone.</summary>
        private static bool Spared(Player shooter, Player target)
        {
            if (!target.IsAI)
                return true;
            string group = shooter.Profile?.Info?.GroupId;
            return !string.IsNullOrEmpty(group) && target.Profile?.Info?.GroupId == group;
        }

        private static Vector3 Chest(Player target) =>
            target.PlayerBones != null && target.PlayerBones.BodyPartCollidersDictionary.TryGetValue(EBodyPartColliderType.RibcageUp, out BodyPartCollider part) && part?.Collider != null
                ? part.Collider.bounds.center
                : target.Position + Vector3.up * 1.3f;

        /// <summary>
        /// Through the game's own damage, from the player with the Thundergun, so the kill counts like any other. The
        /// chest takes it all; anything that somehow survives that is killed outright, still credited (the last hit
        /// was ours).
        /// </summary>
        private static void Kill(Player shooter, Player target, Weapon gun, Vector3 origin, Vector3 forward, Vector3 chest)
        {
            Vector3 throwDirection = (forward + Vector3.up * 0.25f).normalized;
            BodyPartCollider part = null;
            target.PlayerBones?.BodyPartCollidersDictionary.TryGetValue(EBodyPartColliderType.RibcageUp, out part);
            var damage = new DamageInfo
            {
                DamageType = EDamageType.Bullet,
                Damage = 10000f,
                PenetrationPower = 0f,
                ArmorDamage = 0f,
                HitCollider = part?.Collider,
                Direction = throwDirection,
                HitPoint = chest,
                MasterOrigin = origin,
                HitNormal = -forward,
                Player = Singleton<GameWorld>.Instance.GetAlivePlayerBridgeByProfileID(shooter.ProfileId),
                Weapon = gun,
                IsForwardHit = true,
                BodyPartColliderType = EBodyPartColliderType.RibcageUp,
                SourceId = gun.TemplateId.ToString(),
            };
            // Thrown back along the blast, and a bit up; the corpse hook picks this up
            Vector3 flat = new Vector3(forward.x, 0f, forward.z).normalized;
            // Kept until the corpse is made (the death can land a frame later); a stale one is ignored after 3 s
            ToFling[target] = (flat * ThundergunSettings.FlingSpeed.Value + Vector3.up * ThundergunSettings.FlingLift.Value, Time.time);

            target.ApplyDamageInfo(damage, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, 0f);
            if (target.HealthController.IsAlive)
                target.ActiveHealthController?.Kill(EDamageType.Bullet);
        }

        // ---------- Fling ----------

        /// <summary>
        /// The game pushes a fresh corpse at the one bone that was hit; a blast victim's whole body is thrown instead,
        /// every bone at the same speed, so it flies rather than folds.
        /// </summary>
        private static bool CorpseImpulsePrefix(Player __instance)
        {
            if (!ToFling.TryGetValue(__instance, out var fling))
                return true;
            ToFling.Remove(__instance);
            Corpse corpse = CorpseOf(__instance);
            if (corpse?.Ragdoll == null || Time.time - fling.at > 3f)
                return true;
            ThundergunPlugin.Runner.StartCoroutine(Fling(corpse.Ragdoll, fling.velocity));
            return false;
        }

        /// <summary>
        /// The ragdoll's own bones only (the corpse also carries item bodies, the dropped gun's among them, which stay
        /// kinematic). Their velocity is set rather than added to, over the first physics steps: the ragdoll starts by
        /// clamping every bone to 2 m/s, and the throw has to win. Held for <see cref="FlingSteps"/> steps (about 0.16 s)
        /// rather than just long enough to win, so the joints and the ground don't bleed the speed off at once and the
        /// body carries it further: the same launch speed, a stronger throw.
        /// </summary>
        private const int FlingSteps = 8;

        private static IEnumerator Fling(CorpseRagdoll ragdoll, Vector3 velocity)
        {
            int flung = 0;
            for (int step = 0; step < FlingSteps; step++)
            {
                flung = 0;
                foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners ?? new RigidbodySpawner[0])
                {
                    Rigidbody body = spawner?.Rigidbody;
                    if (body == null || body.isKinematic)
                        continue;
                    body.velocity = velocity;
                    flung++;
                }
                yield return new WaitForFixedUpdate();
            }
            if (flung == 0)
                ThundergunPlugin.Log.LogWarning("Thundergun fling: the corpse had no physical bones to throw");
        }

        // ---------- The wave ----------

        private static readonly Dictionary<WeaponPrefab, Transform> Templates = new Dictionary<WeaponPrefab, Transform>();
        private static readonly int TintColor = Shader.PropertyToID("_TintColor");
        private static readonly Color ZeusAir = new Color(0.8f, 0.55f, 1f, 0.55f);

        /// <summary>A copy of the gun's wave template, set loose in the world along the shot (it doesn't follow the gun).</summary>
        private static void PlayWave(Player.FirearmController hands, Vector3 origin, Vector3 forward, bool zeus)
        {
            WeaponPrefab prefab = hands._weaponPrefab;
            if (prefab == null)
                return;
            if (!Templates.TryGetValue(prefab, out Transform template) || template == null)
            {
                template = prefab.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "tg_blast_wave");
                Templates[prefab] = template;
                if (template == null)
                {
                    ThundergunPlugin.Log.LogWarning("Thundergun model has no blast wave (rebuild the bundle)");
                    return;
                }
            }
            Vector3 from = template.parent != null ? template.parent.position : origin;
            GameObject wave = Object.Instantiate(template.gameObject, from, Quaternion.LookRotation(forward));
            foreach (Transform t in wave.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = 0; // the world's layer, not the first-person weapon's
            if (zeus)
            {
                wave.transform.localScale = Vector3.one * 1.3f;
                Renderer air = wave.transform.Find("air")?.GetComponent<Renderer>();
                if (air != null)
                    air.material.SetColor(TintColor, ZeusAir);
            }
            wave.SetActive(true);
            Object.Destroy(wave, 2f);
        }
    }
}
