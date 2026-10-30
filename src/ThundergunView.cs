using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The local player, cheaply: the game world's main player in a raid (and the hideout), else the hideout player
    /// found by a scene search at most every few seconds.
    /// </summary>
    internal static class LocalPlayer
    {
        private static Player _cached;
        private static float _nextSearch;

        public static Player Get()
        {
            if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer is Player main && main != null)
                return main;
            if (_cached != null)
                return _cached;
            if (Time.unscaledTime < _nextSearch)
                return null;
            _nextSearch = Time.unscaledTime + 3f;
            return _cached = Object.FindObjectsOfType<Player>().FirstOrDefault(p => p.IsYourPlayer);
        }
    }

    /// <summary>
    /// Twice a second: a Zeus Cannon in the hands tops up any canister it hasn't yet. The models themselves get their
    /// <see cref="ThundergunView"/> as the game builds them (<see cref="ThundergunCartridge"/>'s factory hooks); the
    /// hands model is also checked here, in case it came from a path those hooks don't see (a pooled model, say).
    /// </summary>
    internal class ThundergunTracker : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.5f;
            Player player = LocalPlayer.Get();
            ThundergunHandling.Update(player);
            // A raid puts a bought wall gun back on sale for the next hideout visit
            if (player != null && !(player is HideoutPlayer))
                ThundergunStations.RaidSeen();
            if (!(player?.HandsController is Player.FirearmController hands) || !(hands.Item is Weapon held) || !ThundergunIds.Is(held))
                return;
            ThundergunCharges.CheckZeus(held);
            if (hands._weaponPrefab != null && hands._weaponPrefab.GetComponentInChildren<ThundergunView>(true) == null)
            {
                ThundergunPlugin.Log.LogInfo("Thundergun hands model had no view; attaching it from the firearm controller");
                Attach(hands._weaponPrefab.gameObject);
            }
        }

        /// <summary>A Thundergun model just built: the view goes on its animator (the weapon root).</summary>
        public static void Attach(GameObject model)
        {
            Animator animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;
            // A pooled model the menu froze (ThundergunMenuPose) animates again wherever it's used next; any other
            // model keeps whatever the game set (PaP's display gun stays still)
            if (ThundergunMenuPose.Frozen.Remove(animator))
                animator.enabled = true;
            if (animator.GetComponent<ThundergunView>() == null)
                animator.gameObject.AddComponent<ThundergunView>();
        }
    }

    /// <summary>
    /// How a bulb card looks. Lit: glowing. Idle: dark, showing its green grille (there, but not usable: the plain
    /// gun's outer two). Spent: not drawn at all (the cards are cut-out materials, so a cutoff above 1 clips every
    /// pixel), leaving the dark drum behind it, like the half of the drum without cards.
    /// </summary>
    internal static class Bulb
    {
        private static readonly int Cutoff = Shader.PropertyToID("_Cutoff");
        private static readonly int Emission = Shader.PropertyToID("_EmissionColor");

        public static void Show(Material m, bool spent, Color glow)
        {
            m.SetFloat(Cutoff, spent ? 1.01f : 0f);
            m.SetColor(Emission, spent ? Color.black : glow);
        }
    }

    /// <summary>
    /// The ammo lights on the back of the gun, like BO1's: one per charge left. Unpowered (no charge, mid-reload,
    /// before the tech beep) the drum is dark all round. Powered, the plain Thundergun lights its two middle bulbs and
    /// shows the outer two idle; the Zeus Cannon lights all four with a purple cast. A shot puts one out.
    /// </summary>
    internal class ThundergunView : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<WeaponPrefab, Weapon> WeaponData = AccessTools.FieldRefAccess<WeaponPrefab, Weapon>("_weaponData");

        /// <summary>Live views, so a Pack-a-Punch change can refresh them at once.</summary>
        public static readonly HashSet<ThundergunView> Live = new HashSet<ThundergunView>();

        // Fallbacks for a model without the section map
        private static readonly Color Orange = new Color(1f, 0.3f, 0.05f) * 2f;
        private static readonly Color Purple = new Color(0.65f, 0.2f, 1f) * 2f;
        // Multiply the orange section map: kept just above 1 so it glows without blowing out to white under bloom
        private static readonly Color Plain = new Color(1f, 0.58f, 0.4f) * 1.15f; // deeper, redder orange than the map's own
        private static readonly Color OriginsBlue = new Color(0.25f, 0.6f, 1f) * 1.3f;
        private static readonly Color ZeusTint = new Color(1.1f, 0.7f, 1.6f) * 1.1f;

        // glow_1..4: this gun's own copies of the four bulb materials, and every renderer slot that should draw them
        private readonly Material[] _lights = new Material[4];
        private readonly List<(Renderer renderer, int index, int bulb)> _slots = new List<(Renderer, int, int)>();
        private Texture _lightMap;
        private WeaponPrefab _prefab;
        private bool _shownInfused;
        private float _nextCheck;

        private void Awake() => Live.Add(this);

        private void OnDestroy() => Live.Remove(this);

        private void Start()
        {
            // Only the gun's own cards (meshes under its tag_bulb bones): the cartridge model the game hangs, hidden,
            // in the magazine slot carries the same materials, and has its own lights (CartridgeLights)
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.transform.parent == null || !r.transform.parent.name.StartsWith("tag_bulb"))
                    continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    for (int n = 1; n <= 4 && mats[i] != null; n++)
                    {
                        if (mats[i].name.StartsWith($"tg_light_{n}"))
                        {
                            if (_lights[n - 1] == null)
                                _lights[n - 1] = new Material(mats[i]) { name = $"tg_light_{n} (Thundergun)" };
                            _slots.Add((r, i, n - 1));
                        }
                    }
                }
            }
            if (_slots.Count == 0)
                ThundergunPlugin.Log.LogWarning($"Thundergun model on {name}: no bulb cards under tag_bulb bones");
            EnsureMaterials();
            _lightMap = _lights.FirstOrDefault(m => m != null)?.GetTexture("_EmissionMap");
            _prefab = GetComponentInParent<WeaponPrefab>();
            // A model built from the pool (the inspect window) could still wear camo from before a strip
            if (_prefab != null)
                ThundergunPaP.ApplyCamo(_prefab.gameObject, ShownWeapon());

            // The container's sound player: at the F12 levels, and it carries BO3's shot for Pack-a-Punch
            WeaponSoundPlayer sounds = _prefab?.GetComponent<WeaponSoundPlayer>();
            ThundergunSounds.Register(sounds);
            AudioClip shot = sounds?.AdditionalSounds
                .FirstOrDefault(s => s.EventName == "SndTgPapShot")?.SoundClips?.FirstOrDefault();
            ThundergunPaP.RegisterShot(shot);
            ThundergunPlugin.Log.LogInfo($"Thundergun model on {name}: {_lights.Count(m => m != null)}/4 ammo lights on {_slots.Count} card(s), " +
                                         $"weapon prefab {(_prefab != null ? "found" : "missing")}, PaP shot {(shot != null ? "found" : "missing")}");
        }

        /// <summary>
        /// Puts this gun's bulb materials back wherever something swapped them out (a material refresh on the
        /// model). True if any had to go back, so the next pass rewrites every bulb.
        /// </summary>
        private bool EnsureMaterials()
        {
            bool fixedAny = false;
            foreach (var group in _slots.GroupBy(s => s.renderer))
            {
                Renderer r = group.Key;
                if (r == null)
                    continue;
                Material[] mats = r.sharedMaterials;
                bool changed = false;
                foreach (var (_, index, bulb) in group)
                {
                    if (index < mats.Length && mats[index] != _lights[bulb])
                    {
                        mats[index] = _lights[bulb];
                        changed = true;
                    }
                }
                if (changed)
                {
                    r.sharedMaterials = mats;
                    fixedAny = true;
                }
            }
            return fixedAny;
        }

        /// <summary>Everything is rewritten next frame (after a Pack-a-Punch change, say).</summary>
        public void Refresh() => _primed = false;

        private Animator _animator;

        private Animator Anim => _animator != null ? _animator : _animator = GetComponent<Animator>();

        private static readonly HashSet<int> ReloadStates = new HashSet<int>(new[]
        {
            "RELOAD START", "RELOAD OUT", "RELOAD IN", "RELOAD START EMPTY", "RELOAD OUT ALL", "INV TO RELOAD",
        }.Select(Animator.StringToHash));

        /// <summary>
        /// True while the gun's Hands layer plays a reload up to the canister locking home (the end segment, with
        /// the beep, is left to the arming timer).
        /// </summary>
        private bool Reloading() =>
            Anim != null && Anim.layerCount > 1 && ReloadStates.Contains(Anim.GetCurrentAnimatorStateInfo(1).shortNameHash);

        private Renderer[] _drums;
        private float _emptySince = -1f;
        private bool _drumsShown = true;

        /// <summary>
        /// The canister drums are part of the gun model (the reload animates them), so with no canister loaded they're
        /// hidden. A reload takes the old one out before the new one goes in: only an empty gun that stays empty for a
        /// moment drops them, so they don't blink mid-reload. Any other model (the inspect view) follows at once.
        /// </summary>
        private void ShowDrums(bool loaded, bool inHands)
        {
            if (_drums == null)
                _drums = GetComponentsInChildren<Renderer>(true)
                    .Where(r => r.transform.GetComponentsInParent<Transform>(true).Any(t => t.name.StartsWith("tag_ammo_drum_")))
                    .ToArray();
            if (loaded)
                _emptySince = -1f;
            else if (_emptySince < 0f)
                _emptySince = Time.time;
            bool show = loaded || inHands && Time.time - _emptySince < 1.5f;
            if (show == _drumsShown)
                return;
            _drumsShown = show;
            foreach (Renderer r in _drums)
                r.enabled = show;
        }

        private float _nextLookup;
        private Weapon _held;

        /// <summary>
        /// The gun this model shows in the local player's hands, or null if it isn't the hands model. The firearm
        /// controller keeps the model it drives, so the match is exact (in the hideout's weapons mode the hands
        /// hold a copy of the gun, and the copy is what fires). Re-checked four times a second, cached in between.
        /// </summary>
        private Weapon HeldWeapon()
        {
            if (Time.unscaledTime < _nextLookup)
                return _held;
            _nextLookup = Time.unscaledTime + 0.25f;
            _held = _prefab != null && LocalPlayer.Get()?.HandsController is Player.FirearmController hands && hands._weaponPrefab == _prefab
                    && hands.Item is Weapon held && ThundergunIds.Is(held)
                ? held
                : null;
            return _held;
        }

        // Lit first to last: the drum's four bulbs run left to right; the middle pair (2 and 3) are the plain gun's two charges
        private static readonly int[] LightOrder = { 1, 2, 0, 3 };

        /// <summary>The weapon prefab this view sits under (null until Start).</summary>
        public WeaponPrefab Prefab => _prefab;

        /// <summary>
        /// The gun this model shows. The hands hold their own item (in the hideout's weapons mode a copy of the
        /// profile's gun, the one that actually fires); the prefab's item may be the original, which never changes.
        /// The hands win.
        /// </summary>
        public Weapon ShownWeapon() => HeldWeapon() ?? (_prefab != null ? WeaponData(_prefab) : null);

        private void Update()
        {
            Weapon held = HeldWeapon();
            Weapon weapon = ShownWeapon();
            if (weapon != _logged && weapon != null)
            {
                _logged = weapon;
                ThundergunPlugin.Log.LogInfo($"Thundergun lights on {name}: tracking {weapon.Id}{(held != null ? " (in hands)" : "")}");
            }
            if (weapon == null || !ThundergunIds.Is(weapon))
                return;

            if (Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 0.5f;
                if (EnsureMaterials())
                    _primed = false;
            }

            ShowDrums(weapon.GetCurrentMagazine() != null, held != null);
            if (held != null)
                TrackArming(held);

            // Powered: charges in, not mid-reload, and past the tech beep (warm rises over it)
            bool reloading = Reloading();
            int charges = reloading ? 0 : ThundergunCharges.Count(weapon);
            float warm = held != null ? ThundergunCharges.LightLevel(held) : 1f;
            bool powered = charges > 0 && warm > 0f;
            bool infused = ThundergunPaP.IsInfused(weapon);
            _origins = infused && ThundergunPaP.IsOrigins(weapon);
            ApplyOriginsLook();
            int capacity = infused ? ThundergunCharges.ZeusCharges : ThundergunCharges.PlainCharges;
            // BO3's orange section glow; the Zeus Cannon's carries a purple tint through it
            Color on = _origins ? OriginsBlue : _lightMap != null ? Plain : Orange; // the Zeus Cannon's glow stays orange (blue from Origins)

            for (int i = 0; i < LightOrder.Length; i++)
            {
                int bulb = LightOrder[i];
                Material m = _lights[bulb];
                if (m == null)
                    continue;
                bool lit = powered && i < Mathf.Min(charges, capacity);
                // A spent charge snaps out; a fresh canister follows its beep
                float level = lit
                    ? Mathf.Min(Mathf.MoveTowards(_levels[i], 1f, Time.deltaTime / FadeIn), warm)
                    : Mathf.MoveTowards(_levels[i], 0f, Time.deltaTime / FadeOut);
                // The plain gun's outer pair shows idle while it's powered; anything else unlit is dark
                bool idle = powered && i >= capacity;
                bool spent = !lit && !idle && level <= 0f;
                if (level == _levels[i] && spent == _spent[i] && infused == _shownInfused && _origins == _shownOrigins && _primed)
                    continue;
                _levels[i] = level;
                _spent[i] = spent;
                if (m.GetTexture("_EmissionMap") != _lightMap)
                    m.SetTexture("_EmissionMap", _lightMap);
                Bulb.Show(m, spent, on * (level * level));
                SetBulb(bulb, level, infused, held != null);
            }
            _shownInfused = infused;
            _shownOrigins = _origins;
            _primed = true;
        }

        private Weapon _logged;
        private bool _primed;

        private const float FadeIn = 0.25f;
        private const float FadeOut = 0.06f;
        private readonly float[] _levels = new float[4];
        private readonly bool[] _spent = new bool[4];

        // ---------- Arming ----------

        private static readonly HashSet<int> EndStates = new HashSet<int>(new[] { "RELOAD END", "RELOAD CHARGE END" }.Select(Animator.StringToHash));

        private string _armingGun;
        private string _armingCanister;
        private int _lastState;

        /// <summary>
        /// A new canister disarms the gun; the reload's end segment then times its beep (lights on with it, the gun
        /// armed at its end). Without the end segment showing (a swap some other way), a whole reload's worth of time.
        /// </summary>
        private void TrackArming(Weapon weapon)
        {
            string canister = weapon.GetCurrentMagazine()?.Id;
            if (_armingGun != weapon.Id)
            {
                // First look at this gun: whatever it holds is already armed
                _armingGun = weapon.Id;
                _armingCanister = canister;
                _lastState = 0;
                return;
            }
            if (canister != _armingCanister)
            {
                _armingCanister = canister;
                if (canister != null)
                    ThundergunCharges.ArmAfterBeep(weapon.Id, ThundergunCharges.FallbackLightsDelay);
            }

            if (Anim == null || Anim.layerCount < 2)
                return;
            int state = Anim.GetCurrentAnimatorStateInfo(1).shortNameHash;
            if (state != _lastState && EndStates.Contains(state) && canister != null)
                ThundergunCharges.ArmAfterBeep(weapon.Id, ThundergunCharges.BeepStart);
            _lastState = state;
        }

        // ---------- Bulbs ----------

        // A real light behind each grille section on the gun in the hands: a soft glow on the drum and the grille
        // around it, where the glow card alone is a flat picture
        private Light[] _bulbs;
        private bool _origins, _shownOrigins;

        // The lamp at the hub (the "_glo" material) is orange, blue on an Origins gun; the control box's gauge and lamp
        // swap to their blue copies (carried in the weapon bundle on tg_origins_materials)
        private List<Material> _glo;
        private bool _gloOrigins = true;
        private readonly Dictionary<Renderer, Material[]> _plainMats = new Dictionary<Renderer, Material[]>();
        private bool _swappedOrigins;

        private static string Bare(string name) => name.EndsWith(" (Instance)") ? name.Substring(0, name.Length - " (Instance)".Length) : name;

        private void ApplyOriginsLook()
        {
            if (_glo == null)
            {
                _glo = new List<Material>();
                foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null && Bare(mats[i].name).EndsWith("_glo"))
                        {
                            var copy = new Material(mats[i]);
                            mats[i] = copy;
                            _glo.Add(copy);
                            changed = true;
                        }
                    }
                    if (changed)
                        r.sharedMaterials = mats;
                }
            }
            if (_gloOrigins != _origins)
            {
                _gloOrigins = _origins;
                Color lamp = _origins ? new Color(0.25f, 0.6f, 1f) : new Color(1f, 0.45f, 0.1f);
                foreach (Material m in _glo.Where(m => m != null))
                {
                    m.color = lamp;
                    m.SetColor("_EmissionColor", lamp * 1.4f);
                    m.EnableKeyword("_EMISSION");
                }
            }
            if (_swappedOrigins == _origins)
                return;
            Renderer[] all = GetComponentsInChildren<Renderer>(true);
            Renderer holder = all.FirstOrDefault(r => r.name == "tg_origins_materials");
            if (holder == null)
                return;
            _swappedOrigins = _origins;
            Dictionary<string, Material> blues = holder.sharedMaterials.Where(m => m != null).GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
            foreach (Renderer r in all)
            {
                if (r == holder)
                    continue;
                Material[] mats = r.sharedMaterials;
                if (_origins)
                {
                    bool changed = false;
                    Material[] plain = (Material[])mats.Clone();
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null && blues.TryGetValue(Bare(mats[i].name) + "_blue", out Material blue))
                        {
                            mats[i] = blue;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        _plainMats[r] = plain;
                        r.sharedMaterials = mats;
                    }
                }
                else if (_plainMats.TryGetValue(r, out Material[] plain))
                {
                    r.sharedMaterials = plain;
                }
            }
            if (!_origins)
                _plainMats.Clear();
        }
        private static readonly Color BulbOrange = new Color(1f, 0.35f, 0.08f);
        private static readonly Color BulbZeus = new Color(1f, 0.35f, 0.08f);
        private static readonly Color BulbBlue = new Color(0.3f, 0.65f, 1f);
        private const float BulbIntensity = 3.5f;
        private const float BulbRange = 0.16f;

        private void SetBulb(int bulb, float level, bool infused, bool inHands)
        {
            if (!inHands)
                return;
            if (_bulbs == null)
            {
                _bulbs = new Light[4];
                Transform[] all = GetComponentsInChildren<Transform>(true);
                for (int n = 0; n < 4; n++)
                {
                    Transform tag = all.FirstOrDefault(t => t.name == $"tag_bulb{n + 1}");
                    if (tag == null)
                        continue;
                    var go = new GameObject($"tg_bulb_light_{n + 1}");
                    go.transform.SetParent(tag, false);
                    Light light = go.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.range = BulbRange;
                    light.shadows = LightShadows.None;
                    light.renderMode = LightRenderMode.ForcePixel;
                    light.intensity = 0f;
                    light.enabled = false;
                    _bulbs[n] = light;
                }
                ThundergunPlugin.Log.LogInfo($"Thundergun bulbs: {_bulbs.Count(b => b != null)}/4 lights placed");
            }
            Light l = _bulbs[bulb];
            if (l == null)
                return;
            l.color = _origins ? BulbBlue : infused ? BulbZeus : BulbOrange;
            l.intensity = BulbIntensity * level * level;
            l.enabled = level > 0.01f;
        }
    }
}
