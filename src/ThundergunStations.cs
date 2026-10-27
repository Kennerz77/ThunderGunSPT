using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Communications;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Utils;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The mod's own way to get the Thundergun, no other mod needed: a chalk outline of it on a hideout wall (BO's wall
    /// buy) sells the gun, and a green ammo case beside it sells full pressure cartridges, both for roubles from the
    /// stash. A bought gun sits in its outline until the next raid; the wall offers it again on the first hideout visit
    /// after one. The two stations are root objects named <see cref="WallName"/> and <see cref="BoxName"/>; aim + F11
    /// brings them to the player in turn and saves where they are (BepInEx\config), and UnityExplorer can fine-tune them.
    /// </summary>
    internal class ThundergunStations : MonoBehaviour
    {
        public const string WallName = "Thundergun_WallBuy";
        public const string BoxName = "Thundergun_AmmoBox";
        private const string AmmoCaseTpl = "5aafbde786f774389d0cbc0f"; // the green "Ammunition case"
        private const string BuyAction = "ThundergunBuy";
        // Shown in the prompts; the server's config.json has the prices it charges (keep them in step)
        private const int WeaponPrice = 935_000, CartridgePrice = 10_000;
        private const float UseDistance = 2.5f;

        private static readonly string PositionsFile = Path.Combine(BepInEx.Paths.ConfigPath, ThundergunPlugin.PluginGuid + ".stations.json");

        private static HideoutPlayerOwner _owner;
        private static float _ownerSince;
        private static bool _bought, _raidSinceBuy, _busy;

        private readonly ThundergunInteraction _wallMenu = new ThundergunInteraction();
        private readonly ThundergunInteraction _boxMenu = new ThundergunInteraction();
        private Transform _wall, _box;
        private GameObject _wallGun;

        public static void Enable(Harmony harmony, GameObject host)
        {
            harmony.Patch(AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.Init)),
                postfix: new HarmonyMethod(typeof(ThundergunStations).GetMethod(nameof(OwnerInitPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            host.AddComponent<ThundergunStations>();
        }

        private bool _bringBoxNext;

        /// <summary>The station in front of the player, facing them: the wall at chest height, the box on the floor.</summary>
        private static void MoveHere(Transform station, bool wall, HideoutPlayer player)
        {
            Transform body = ((Component)player).transform;
            Vector3 ahead = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
            Vector3 at = body.position + ahead * (wall ? 1.5f : 1f) + Vector3.up * (wall ? 1.3f : 0f);
            station.SetPositionAndRotation(at, Quaternion.LookRotation(-ahead, Vector3.up));
            NotificationManager.DisplayMessageNotification($"{station.name} moved here.");
        }

        // Every hideout visit: a gun bought before the last raid is on sale again
        private static void OwnerInitPostfix(HideoutPlayerOwner __instance)
        {
            _owner = __instance;
            _ownerSince = Time.unscaledTime;
            if (_raidSinceBuy)
            {
                _bought = false;
                _raidSinceBuy = false;
            }
        }

        /// <summary>Told by the tracker while the player is in a raid.</summary>
        public static void RaidSeen()
        {
            if (_bought)
                _raidSinceBuy = true;
        }

        private void Update()
        {
            HideoutPlayerOwner owner = _owner;
            if (owner == null || owner.HideoutPlayer == null)
            {
                _wallMenu.Reset();
                _boxMenu.Reset();
                return;
            }
            // Only once the hideout's side room is open; shut again (a guest's hideout, say), the stations go quiet
            bool open = SideRoomOpen();
            if (_wall != null && _wall.gameObject.activeSelf != open)
            {
                _wall.gameObject.SetActive(open);
                _box.gameObject.SetActive(open);
            }
            // A moment for the player to be placed before the stations are set out the first time
            if (_wall == null && open && Time.unscaledTime - _ownerSince > 2f)
                SetUp(owner);
            if (_wall == null || !open)
            {
                _wallMenu.Reset();
                _boxMenu.Reset();
                return;
            }
            // Quick placement: aim (right mouse) + F11 brings the chalk wall, the next press the ammo box, in turn,
            // and keeps where they now are
            if (Input.GetKeyDown(KeyCode.F11) && Input.GetMouseButton(1))
            {
                MoveHere(_bringBoxNext ? _box : _wall, wall: !_bringBoxNext, owner.HideoutPlayer);
                _bringBoxNext = !_bringBoxNext;
                Save();
            }
            ShowWallGun(_bought);
            _wallMenu.Tick(owner, _wall, UseDistance, WallOptions);
            _boxMenu.Tick(owner, _box, UseDistance, BoxOptions);
        }

        private const int SideRoomLevel = 6; // the Emergency Wall level that opens the side room

        /// <summary>The player's own hideout has its Emergency Wall upgraded to the side room opening.</summary>
        private static bool SideRoomOpen()
        {
            try
            {
                if (!Singleton<HideoutRepresentation>.Instantiated)
                    return false;
                AreaData wall = Singleton<HideoutRepresentation>.Instance.AreaDatas.FirstOrDefault(a => a.Template.Type == EAreaType.EmergencyWall);
                return wall != null && !wall.IsGuestData && wall.CurrentLevel >= SideRoomLevel;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IEnumerable<(string, Action)> WallOptions()
        {
            if (_bought)
                yield return ("Thundergun bought", null);
            else
                yield return ($"Buy Thundergun ({WeaponPrice:N0} ₽)", _busy ? (Action)null : () => Buy("weapon"));
        }

        private IEnumerable<(string, Action)> BoxOptions()
        {
            yield return ($"Buy Thundergun cartridge ({CartridgePrice:N0} ₽)", _busy ? (Action)null : () => Buy("cartridge"));
        }

        // ---------- Buying ----------

        private void Buy(string what)
        {
            ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
            if (session == null)
                return;
            _busy = true;
            string[] emptyBefore = what == "weapon" ? ThundergunDraw.EmptySlots(_owner) : null;
            session.SendOperationRightNow(new BuyOperation { Action = BuyAction, item = what }, result =>
            {
                _busy = false;
                if (result == null || result.Failed)
                {
                    StationSounds.Play("deny", ThundergunSettings.DenyVolume.Value);
                    NotificationManager.DisplayWarningNotification(string.IsNullOrEmpty(result?.Error) ? "The purchase didn't go through." : result.Error);
                    return;
                }
                StationSounds.Play("purchase_accept", ThundergunSettings.PurchaseVolume.Value);
                if (what == "weapon")
                {
                    // BO's wall buy: the cha-ching and the gun coming off the wall together (the wall sound runs longer)
                    StationSounds.Play("wallbuy", ThundergunSettings.WallBuyVolume.Value);
                    _bought = true;
                    _raidSinceBuy = false;
                    _slideOut = true;
                    // The server equips it when a primary slot is free (otherwise it goes to the stash): then it's drawn
                    if (!ThundergunDraw.TryDrawBought(_owner, emptyBefore))
                        NotificationManager.DisplayMessageNotification("Thundergun bought.");
                }
                else
                {
                    NotificationManager.DisplayMessageNotification("Pressure cartridge bought. It's in your stash.");
                }
            });
        }

        /// <summary>Item event body; field names match the server's ThundergunBuyRequest.</summary>
        private class BuyOperation
        {
            public string Action;
            public string item;
        }

        // ---------- Setting out ----------

        private void SetUp(HideoutPlayerOwner owner)
        {
            Transform player = ((Component)owner.HideoutPlayer).transform;
            Dictionary<string, (Vector3 position, Quaternion rotation)> saved = Load();
            // Unplaced: in front of the player, facing them (move them with UnityExplorer and save)
            Vector3 ahead = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            Quaternion facing = Quaternion.LookRotation(-ahead, Vector3.up);
            (Vector3, Quaternion) wallAt = saved.TryGetValue(WallName, out var w) ? w : (player.position + ahead * 2.5f + Vector3.up * 1.3f, facing);
            (Vector3, Quaternion) boxAt = saved.TryGetValue(BoxName, out var b) ? b : (player.position + ahead * 2f + Vector3.Cross(Vector3.up, ahead) * 1.2f, facing);

            _wall = Root(WallName, wallAt);
            Transform chalk = BuildChalk(_wall);
            // Drawn life size: the gun's real length, measured from its model once
            StartCoroutine(Model(ThundergunIds.Weapon, _wall, model =>
            {
                Bounds bounds = WorldBounds(model);
                float measured = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                // A gun is somewhere between half a metre and two: anything else is a bad measurement
                float length = measured > 0.5f && measured < 2f ? measured : 1.1f;
                ThundergunPlugin.Log.LogInfo($"Wall buy chalk sized to the gun: {measured:F2} m measured, {length:F2} m used");
                model.transform.SetParent(null, false);
                AssetPoolObject.ReturnToPool(model, true);
                if (chalk != null)
                    SizeChalk(chalk, length);
            }, loaded: true));
            _box = Root(BoxName, boxAt);
            StartCoroutine(Model(AmmoCaseTpl, _box, model => FitCollider(_box, model)));
            ThundergunPlugin.Log.LogInfo($"Hideout stations set out ({(saved.Count > 0 ? "saved positions" : "in front of the player: place them with UnityExplorer")})");
        }

        private static Transform Root(string name, (Vector3 position, Quaternion rotation) at)
        {
            GameObject old = GameObject.Find(name);
            if (old != null)
                Destroy(old);
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(at.position, at.rotation);
            return go.transform;
        }

        /// <summary>The outline: a flat chalk drawing of the gun, its forward face (+Z) out of the wall.</summary>
        private static Transform BuildChalk(Transform wall)
        {
            Texture2D chalk = ThundergunChalk.Make();
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "chalk";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(wall, false);
            // A hair off the wall so it never flickers into it
            quad.transform.localPosition = new Vector3(0f, 0f, 0.005f);
            var material = new Material(Shader.Find("Sprites/Default")) { mainTexture = chalk };
            quad.GetComponent<Renderer>().sharedMaterial = material;
            wall.gameObject.AddComponent<BoxCollider>();
            SizeChalk(quad.transform, 1f);
            return quad.transform;
        }

        // The picture frames the gun with 4% to spare (TgIcons), so the drawing is that much wider than the gun
        private const float PictureMargin = 1.04f;

        /// <summary>
        /// Sized to the gun, and mirrored: the picture is the gun's left side, muzzle left, and the bought model hangs
        /// the other way round, so the drawing is flipped to match its outline.
        /// </summary>
        private static void SizeChalk(Transform quad, float gunLength)
        {
            Texture2D chalk = quad.GetComponent<Renderer>().sharedMaterial.mainTexture as Texture2D;
            float width = gunLength * PictureMargin, height = chalk != null ? width * chalk.height / chalk.width : width * 0.4f;
            quad.localScale = new Vector3(-width, height, 1f);
            BoxCollider box = quad.parent.GetComponent<BoxCollider>();
            if (box != null)
                box.size = new Vector3(width, height, 0.05f);
        }

        /// <summary>
        /// A bought gun hangs inside its outline until the next raid, loaded with a cartridge. Just bought, it slides
        /// slowly out of the wall as in BO; on a later visit it's simply there.
        /// </summary>
        private void ShowWallGun(bool show)
        {
            if (show && _wallGun == null && !_loadingGun)
            {
                _loadingGun = true;
                bool slide = _slideOut;
                _slideOut = false;
                StartCoroutine(Model(ThundergunIds.Weapon, _wall, model =>
                {
                    _loadingGun = false;
                    _wallGun = model;
                    LayAlong(model, _wall);
                    if (slide)
                        StartCoroutine(SlideOut(model.transform));
                }, loaded: true));
            }
            else if (!show && _wallGun != null)
            {
                // Back to the game's model pool, as it came out
                _wallGun.transform.SetParent(null, false);
                AssetPoolObject.ReturnToPool(_wallGun, true);
                _wallGun = null;
            }
        }

        private bool _loadingGun;
        private static bool _slideOut;

        private const float SlideDepth = 0.35f, SlideSeconds = 1.6f;

        /// <summary>From inside the wall out to its place, easing to a stop.</summary>
        private static IEnumerator SlideOut(Transform gun)
        {
            Vector3 end = gun.localPosition, start = end - new Vector3(0f, 0f, SlideDepth);
            for (float t = 0f; t < SlideSeconds && gun != null; t += Time.deltaTime)
            {
                float k = t / SlideSeconds;
                gun.localPosition = Vector3.Lerp(start, end, 1f - (1f - k) * (1f - k) * (1f - k));
                yield return null;
            }
            if (gun != null)
                gun.localPosition = end;
        }

        /// <summary>The model laid flat against the wall: its longest side across it, centred, just out from the chalk.</summary>
        private static void LayAlong(GameObject model, Transform wall)
        {
            model.transform.SetParent(null, false);
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Bounds bounds = WorldBounds(model);
            // Longest side along the wall's width, thinnest out of the wall
            Quaternion lay = bounds.size.z > bounds.size.x ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            model.transform.SetParent(wall, false);
            model.transform.localRotation = lay;
            model.transform.localPosition = -(lay * bounds.center) + new Vector3(0f, 0f, 0.08f);
        }

        private static void FitCollider(Transform root, GameObject model)
        {
            model.transform.SetParent(root, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            Bounds bounds = WorldBounds(model);
            var box = root.gameObject.GetComponent<BoxCollider>() ?? root.gameObject.AddComponent<BoxCollider>();
            box.center = root.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
        }

        /// <summary>
        /// A display model of an item, built the way the hideout's weapon stands build theirs: its bundles loaded, made
        /// by the game's factory, and purely visual (no physics; the colliders off, so only the station's box is hit).
        /// </summary>
        private static IEnumerator Model(string template, Transform parent, Action<GameObject> done, bool loaded = false)
        {
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            Item item = factory.CreateItem(MongoID.Generate(), template, null);
            if (item == null)
                yield break;
            // A display gun with a full cartridge in it (an empty gun shows its drums gone)
            if (loaded && item is Weapon weapon && factory.CreateItem(MongoID.Generate(), ThundergunIds.Cartridge, null) is Magazine cartridge)
            {
                Item charges = factory.CreateItem(MongoID.Generate(), ThundergunIds.Charge, null);
                if (charges != null)
                {
                    charges.StackObjectsCount = ThundergunCharges.PlainCharges;
                    cartridge.Cartridges.Add(charges, false);
                }
                weapon.GetMagazineSlot()?.AddWithoutRestrictions(cartridge);
            }
            Task bundles = EasyAssetsExtensions.WaitForAllBundlesJob(item.GetAllBundleTokens(), () => { }, CancellationToken.None, null);
            while (!bundles.IsCompleted)
                yield return null;
            if (bundles.IsFaulted || parent == null)
                yield break;
            GameObject model = Singleton<ObjectsFactory>.Instance?.CreateItem(item, false);
            if (model == null)
                yield break;
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
            foreach (Rigidbody rb in model.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            model.SetActive(true);
            done(model);
        }

        /// <summary>
        /// The visible model only: active meshes that draw. Inactive parts (the gun's air-wave template), particles and
        /// hidden charges report bounds that can sit at the world's origin and would stretch it.
        /// </summary>
        private static Bounds WorldBounds(GameObject go)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(false)
                .Where(r => r.enabled && !r.forceRenderingOff && (r is MeshRenderer || r is SkinnedMeshRenderer)).ToArray();
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.one * 0.3f);
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers.Skip(1))
                bounds.Encapsulate(r.bounds);
            return bounds;
        }

        // ---------- Positions ----------

        private void Save()
        {
            if (_wall == null || _box == null)
            {
                NotificationManager.DisplayWarningNotification("The Thundergun stations aren't out: open the hideout first.");
                return;
            }
            try
            {
                var lines = new[] { (WallName, _wall), (BoxName, _box) }.Select(s =>
                {
                    Vector3 p = s.Item2.position;
                    Vector3 r = s.Item2.eulerAngles;
                    return FormattableString.Invariant($"{s.Item1} {p.x} {p.y} {p.z} {r.x} {r.y} {r.z}");
                });
                File.WriteAllLines(PositionsFile, lines);
                NotificationManager.DisplayMessageNotification("Thundergun station positions saved.");
                ThundergunPlugin.Log.LogInfo($"Station positions saved to {PositionsFile}:\n{string.Join("\n", lines)}");
            }
            catch (Exception e)
            {
                ThundergunPlugin.Log.LogError($"Saving station positions failed: {e.Message}");
            }
        }

        // Where the user placed them in the hideout (2026-10-27): used until a stations file is saved
        private static readonly Dictionary<string, (Vector3, Quaternion)> Defaults = new Dictionary<string, (Vector3, Quaternion)>
        {
            [WallName] = (new Vector3(7.546152f, 1.049445f, 6.46787f), Quaternion.Euler(0f, 179.1013f, 0f)),
            [BoxName] = (new Vector3(7.227804f, 0.3301507f, 6.391765f), Quaternion.Euler(2.6909f, 182.4395f, 0f)),
        };

        /// <summary>One line per station: name, position x y z, rotation x y z (degrees).</summary>
        private static Dictionary<string, (Vector3, Quaternion)> Load()
        {
            var result = new Dictionary<string, (Vector3, Quaternion)>(Defaults);
            try
            {
                if (!File.Exists(PositionsFile))
                    return result;
                foreach (string line in File.ReadAllLines(PositionsFile))
                {
                    string[] f = line.Split(' ');
                    if (f.Length != 7)
                        continue;
                    float[] v = f.Skip(1).Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    result[f[0]] = (new Vector3(v[0], v[1], v[2]), Quaternion.Euler(v[3], v[4], v[5]));
                }
            }
            catch (Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Couldn't read {PositionsFile}: {e.Message}");
            }
            return result;
        }
    }

    /// <summary>
    /// The stations' BO3 sounds (sounds\*.wav beside the plugin): "purchase_accept" (the cha-ching), "wallbuy" (the gun
    /// coming off the wall) and "deny" (refused). Played in the player's head like BO's, at their F12 volumes.
    /// </summary>
    internal static class StationSounds
    {
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static AudioSource _source;

        public static void Play(string name, float volume)
        {
            try
            {
                if (!Clips.TryGetValue(name, out AudioClip clip))
                    Clips[name] = clip = LoadWav(Path.Combine(Path.GetDirectoryName(typeof(StationSounds).Assembly.Location), "sounds", name + ".wav"));
                if (clip == null || volume <= 0f)
                    return;
                if (_source == null)
                {
                    var go = new GameObject("tg_station_sounds");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _source = go.AddComponent<AudioSource>();
                    _source.spatialBlend = 0f;
                }
                // One source for all: one-shots overlap, so the cha-ching and the wall sound play together
                _source.PlayOneShot(clip, volume);
            }
            catch (Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Station sound {name} failed: {e.Message}");
            }
        }

        /// <summary>A plain 16-bit PCM WAV (Greyhound's export) into a clip.</summary>
        private static AudioClip LoadWav(string path)
        {
            if (!File.Exists(path))
            {
                ThundergunPlugin.Log.LogWarning($"Sound missing: {path}");
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            int channels = BitConverter.ToInt16(bytes, 22), rate = BitConverter.ToInt32(bytes, 24);
            // Walk the chunks to "data" (there can be others before it)
            int at = 12;
            while (at + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                int size = BitConverter.ToInt32(bytes, at + 4);
                if (id == "data")
                {
                    int count = Math.Min(size, bytes.Length - at - 8) / 2;
                    var samples = new float[count];
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2) / 32768f;
                    AudioClip clip = AudioClip.Create("tg_purchase_accept", count / channels, channels, rate, false);
                    clip.SetData(samples, 0);
                    return clip;
                }
                at += 8 + size + (size & 1);
            }
            return null;
        }
    }

    /// <summary>
    /// The chalk outline: drawn from the gun's own inventory picture (icons\thundergun.png, rendered at build time).
    /// Its silhouette's edge as a thick chalk line, the bigger shapes inside as fainter strokes, all broken up a little
    /// like chalk on a rough wall.
    /// </summary>
    internal static class ThundergunChalk
    {
        private static Texture2D _made;

        public static Texture2D Make()
        {
            if (_made != null)
                return _made;
            string path = Path.Combine(Path.GetDirectoryName(typeof(ThundergunChalk).Assembly.Location), "icons", "thundergun.png");
            if (!File.Exists(path))
            {
                ThundergunPlugin.Log.LogWarning($"No {path}: the wall buy has no chalk outline");
                return null;
            }
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            source.LoadImage(File.ReadAllBytes(path), false);
            const int w = 768;
            int h = Mathf.Max(32, w * source.height / source.width);

            var solid = new bool[w * h];
            var lum = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = source.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                    solid[y * w + x] = c.a > 0.3f;
                    lum[y * w + x] = (c.r + c.g + c.b) / 3f;
                }
            }
            UnityEngine.Object.Destroy(source);

            var random = new System.Random(935);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    // Silhouette edge: anything solid and empty within 2 px of each other
                    bool any = false, all = true;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool s = xx >= 0 && yy >= 0 && xx < w && yy < h && solid[yy * w + xx];
                            any |= s;
                            all &= s;
                        }
                    }
                    float a = any && !all ? 0.95f : 0f;
                    // Inner detail: strong shading changes inside the gun, as thinner, fainter strokes
                    if (a == 0f && solid[i] && x > 0 && y > 0 && x < w - 1 && y < h - 1)
                    {
                        float gx = lum[i + 1] - lum[i - 1], gy = lum[i + w] - lum[i - w];
                        if (gx * gx + gy * gy > 0.035f)
                            a = 0.55f;
                    }
                    if (a > 0f)
                    {
                        // Chalk on a rough wall: uneven pressure and the odd skipped grain
                        a *= 0.65f + 0.35f * Mathf.PerlinNoise(x * 0.09f, y * 0.09f);
                        if (random.NextDouble() < 0.14)
                            a *= 0.25f;
                    }
                    pixels[i] = new Color32(238, 242, 255, (byte)(a * 255));
                }
            }
            _made = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "tg_chalk", wrapMode = TextureWrapMode.Clamp };
            _made.SetPixels32(pixels);
            _made.Apply(true, true);
            return _made;
        }
    }
}
