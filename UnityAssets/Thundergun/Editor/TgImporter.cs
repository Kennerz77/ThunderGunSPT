using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT;
using Thundergun.CodFormats;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thundergun.Editor
{
    /// <summary>
    /// Builds the Thundergun's prefabs from the BO1 port (no Blender/FBX):
    /// weapon_thundergun (EFT's weapon rig with the gun model, BO1 clips and the rebuilt Saiga controller),
    /// weapon_thundergun_container (WeaponPrefab, sound player and the other EFT components the game reads), and the
    /// Zeus canister magazines (the canister mesh, split off the gun: EFT shows the magazine item in mod_magazine).
    /// </summary>
    public static class TgImporter
    {
        public static string WeaponPrefabPath => $"{TgPaths.Root}/{TgPaths.WeaponName}.prefab";
        public static string ContainerPrefabPath => $"{TgPaths.Root}/{TgPaths.ContainerName}.prefab";
        public static string CanisterPrefabPath => $"{TgPaths.Root}/{TgPaths.CanisterName}.prefab";
        public static string ControllerPath => $"{TgPaths.Generated}/{TgPaths.WeaponName}.controller";

        // Gun model faces on these bones are the canisters (the one in the gun, and the fresh one during a reload)
        private static readonly string[] CanisterBones = { "tag_ammo_drum_01", "tag_ammo_drum_02" };
        // Faces with this material aren't drawn (BO3 hides it)
        private const string HiddenMaterial = "mtl_weapon_scope_acog_lens";

        private const string Anims = "vm_thunder_gun_";

        /// <summary>
        /// Which BO3 HD animation (and frame range, 30 fps) plays in each of EFT's Hands states. The reload is BO3's
        /// empty reload, split at its own notes: cell eject 1, canisters out 6, in 33, cell lock 62, end 90. The first
        /// raise (switches flipped, barrel extended) is the arming draw and the inspect. States without a BO3
        /// counterpart use the idle.
        /// </summary>
        private static readonly Dictionary<string, (string anim, int first, int last, bool loop)> HandsMap =
            new Dictionary<string, (string, int, int, bool)>
            {
                // EFT lets the next shot or reload start only as the idle loop comes round: a short loop (like the
                // Saiga's 0.3 s) keeps the gun responsive. BO3's full breathing idle plays as the long-idle fidget.
                ["IDLE"] = (Anims + "idle", 0, 9, true),
                ["TOO IDLE"] = (Anims + "idle", 0, 999, true),
                ["SPAWN"] = (Anims + "pullout", 0, 0, false),
                // Every draw is BO3's first raise: switches flipped, barrel run out
                ["OUT TO IDLE"] = (Anims + "first_raise", 0, 999, false),
                ["OUT TO IDLE ARM"] = (Anims + "first_raise", 0, 999, false),
                ["IDLE TO OUT"] = (Anims + "putaway", 0, 999, false),
                ["FAST IDLE TO OUT"] = (Anims + "putaway_quick", 0, 999, false),
                ["FIRE"] = (Anims + "fire", 0, 999, false),
                // An empty trigger is only the click: no shot animation, no kick (a short slice so the state ends quickly)
                ["DRY FIRE"] = (Anims + "idle", 0, 4, false),
                ["DRY FIRE DISARMED"] = (Anims + "idle", 0, 4, false),
                ["DRY FIRE JAMS"] = (Anims + "idle", 0, 4, false),
                // Reload: start, out (spent canisters off), in (fresh ones on), end (locked, barrel back out)
                ["RELOAD START"] = (Anims + "reload_empty", 0, 6, false),
                ["RELOAD OUT"] = (Anims + "reload_empty", 6, 33, false),
                ["RELOAD IN"] = (Anims + "reload_empty", 33, 62, false),
                ["RELOAD END"] = (Anims + "reload_empty", 62, 999, false),
                ["RELOAD CHARGE END"] = (Anims + "reload_empty", 62, 999, false),
                ["RELOAD START EMPTY"] = (Anims + "reload_empty", 28, 33, false),
                ["RELOAD OUT ALL"] = (Anims + "reload_empty", 0, 33, false),
                // Arms down for the inventory, and back up
                ["OPEN INVENTORY"] = (Anims + "putaway", 0, 999, false),
                ["IN INVENTORY"] = (Anims + "putaway", 999, 999, false),
                ["CLOSE INVENTORY"] = (Anims + "pullout", 0, 999, false),
                ["RELOAD INVENTORY END"] = (Anims + "putaway", 0, 999, false),
                ["INV TO RELOAD"] = (Anims + "pullout", 0, 10, false),
                ["HANDS OUT"] = (Anims + "putaway", 0, 999, false),
                ["HANDS IN"] = (Anims + "pullout", 0, 999, false),
                ["PATROL OUT"] = (Anims + "sprint_in", 0, 999, false),
                ["PATROL"] = (Anims + "sprint_loop", 0, 999, true),
                ["PATROL IN"] = (Anims + "sprint_out", 0, 999, false),
                // Inspecting the gun: BO3's first raise flips the switches and runs the barrel out
                ["LOOK"] = (Anims + "first_raise", 0, 999, false),
                ["CHECK"] = (Anims + "idle", 0, 999, true),
            };

        /// <summary>
        /// Playback speed of some Hands states. EFT waits for the shot state to finish before the next trigger pull
        /// (or the empty click) registers: BO3's 0.9 s shot played at 2.5x keeps the kick and lets the gun cycle.
        /// </summary>
        private static readonly Dictionary<string, float> StateSpeeds = new Dictionary<string, float>
        {
            ["FIRE"] = 2.5f,
        };

        /// <summary>
        /// Frames over which a state's clip eases in from / out to the exact idle pose, where it starts or ends away
        /// from it (the reload, the draws), so states hand over without a hitch.
        /// </summary>
        private static readonly Dictionary<string, (int easeIn, int easeOut)> Ease = new Dictionary<string, (int, int)>
        {
            // The short idle loop ends back on its first frame, so it loops without a jump
            ["IDLE"] = (0, 9),
            ["RELOAD START"] = (6, 0),
            ["RELOAD OUT ALL"] = (6, 0),
            ["RELOAD END"] = (0, 12),
            ["RELOAD CHARGE END"] = (0, 12),
            ["OUT TO IDLE"] = (0, 10),
            ["OUT TO IDLE ARM"] = (0, 12),
            ["CLOSE INVENTORY"] = (0, 10),
            ["HANDS IN"] = (0, 10),
        };

        /// <summary>
        /// BO3's sound notes ("sndnt#alias") and the sound each plays: (our event, clip). States playing a range of an
        /// animation with notes get exactly those sounds instead of the Saiga's.
        /// </summary>
        private static readonly Dictionary<string, (string sound, string clip)> SoundAliases = new Dictionary<string, (string, string)>
        {
            ["fly_thundergun_first_raise"] = ("TgRaise", "fly_thunder_first_raise"),
            ["fly_thundergun_cell_eject"] = ("TgCellEject", "fly_thundergun_eject"),
            ["fly_thundergun_cell_replace"] = ("TgCellReplace", "fly_thundergun_cell_replace"),
            ["fly_thundergun_cell_lock"] = ("TgCellLock", "fly_thundergun_cell_slide_lock"),
        };

        /// <summary>Sounds BO3's notes don't have: BO1's tech beep closes the reload, as the canister locks home.</summary>
        private static readonly Dictionary<string, (int frame, string sound, string clip)[]> ExtraNotes = new Dictionary<string, (int, string, string)[]>
        {
            [Anims + "reload_empty"] = new[] { (80, "TgBeep", "thundergun_reload_beep") },
        };

        /// <summary>Sound notes per animation, read from the animations as they load (frame, our sound event).</summary>
        private static readonly Dictionary<string, List<(int frame, string sound)>> Notetracks = new Dictionary<string, List<(int, string)>>();

        /// <summary>Our sound events and their clips (event name "Snd" + sound).</summary>
        private static Dictionary<string, string> NotetrackClips => SoundAliases.Values
            .Select(a => new KeyValuePair<string, string>(a.sound, a.clip))
            .Concat(ExtraNotes.Values.SelectMany(n => n).Select(n => new KeyValuePair<string, string>(n.sound, n.clip)))
            .Concat(new[]
            {
                // Not an animation note: BO3's remastered shot, played by the client mod as the Pack-a-Punched sound
                new KeyValuePair<string, string>("TgPapShot", "wpn_thundergun_fire_plr"),
            }).ToDictionary(kv => kv.Key, kv => kv.Value);

        // A state can leave before its clip ends (EFT's transitions use exit times), so late notes are pulled in
        private const float LatestNote = 0.88f;

        private static Dictionary<string, List<(float, string)>> StateSounds(Dictionary<string, (string anim, int first, int last, bool loop)> map,
            Dictionary<string, int> lastFrame)
        {
            var result = new Dictionary<string, List<(float, string)>>();
            foreach (var (state, (anim, first, last, _)) in map)
            {
                if (!Notetracks.TryGetValue(anim, out var notes))
                    continue;
                int span = Math.Max(1, last - first);
                // An empty list still replaces the Saiga's sounds: only BO3's own play during the reload. A note on
                // the frame where one piece hands over to the next belongs to the next (it played in both)
                int end = lastFrame[anim];
                result[state] = notes.Where(n => n.frame >= first && (n.frame < last || last == end))
                    .Select(n => (Mathf.Min(LatestNote, (n.frame - first) / (float)span), n.sound)).ToList();
            }
            return result;
        }

        /// <summary>
        /// State pairs that play one BO1 animation in consecutive pieces (the reload's start, out, in and end): the
        /// next piece starts where the last one stopped, so the hand-over must be instant and at the very end. The
        /// Saiga's exit times and crossfades, tuned to its own clips, made the reload stall and jump.
        /// </summary>
        private static HashSet<(string from, string to)> Contiguous(Dictionary<string, (string anim, int first, int last, bool loop)> map)
        {
            var pairs = new HashSet<(string, string)>();
            foreach (var (from, a) in map)
                foreach (var (to, b) in map)
                    if (from != to && a.anim == b.anim && a.last == b.first && a.last > a.first)
                        pairs.Add((from, to));
            return pairs;
        }

        [MenuItem("Thundergun/1. Import Thundergun")]
        public static void ImportAll()
        {
            // Clips are named after their animation and range: drop earlier imports' so none linger in the project
            AssetDatabase.DeleteAsset(TgPaths.Clips);
            TgPaths.EnsureDirs();
            var warnings = new List<string>();

            // BO3's HD Thundergun; BO1's arms rig (same bone names) gives the hierarchy above the arms
            CodModel gun = TgBo3.LoadModel();
            CodModel bind = CodModel.Load(Path.Combine(TgPaths.ModelDir, "am_t5_thundergun.xmodel_bin"));
            Debug.Log($"[Thundergun] Gun model: {gun.Bones.Count} bones, {gun.Vertices.Count} verts, {gun.Faces.Count} faces, {gun.Materials.Count} materials");

            Dictionary<string, AudioClip> sounds = ImportSounds();

            var rig = new Dictionary<string, Transform>();
            GameObject root = TgRig.Build(TgPaths.WeaponName, rig);
            try
            {
                Transform weapon = rig["weapon"];
                // The gun model's root (j_gun) faces the way BO1's identity pose does in prefab space: forward +Z, up +Y
                var jgun = new GameObject("j_gun").transform;
                jgun.SetParent(weapon, false);
                jgun.gameObject.layer = weapon.gameObject.layer;
                Quaternion jgunLocal = Quaternion.Inverse(weapon.rotation) * TgSpace.Rot(gun.Bones[0].Rest);
                jgun.localRotation = jgunLocal;
                Dictionary<string, Transform> gunBones = BuildGunSkeleton(gun, jgun);

                var materials = new Dictionary<int, Material>();
                for (int m = 0; m < gun.Materials.Count; m++)
                    materials[m] = BuildMaterial(gun.Materials[m], false);
                BuildGunMeshes(gun, gunBones, materials);
                AddOriginsMaterials(gun, jgun, materials);

                var anims = new Dictionary<string, CodAnim>();
                Notetracks.Clear();
                CodAnim Anim(string n)
                {
                    if (anims.TryGetValue(n, out CodAnim a))
                        return a;
                    a = anims[n] = TgBo3.LoadAnim(n, gun, bind, out var notes);
                    var noteSounds = new List<(int, string)>();
                    foreach (var (frame, note) in notes.Where(x => x.note.StartsWith("sndnt#")))
                    {
                        string alias = note.Substring("sndnt#".Length);
                        if (SoundAliases.TryGetValue(alias, out var s))
                            noteSounds.Add((frame, s.sound));
                        else
                            warnings.Add($"{n}: no sound for note {alias} at frame {frame}");
                    }
                    if (ExtraNotes.TryGetValue(n, out var extra))
                        noteSounds.AddRange(extra.Select(x => (x.frame, x.sound)));
                    if (noteSounds.Count > 0)
                        Notetracks[n] = noteSounds;
                    return a;
                }

                var retarget = new TgRetarget(root.transform, rig, gun, gunBones, jgunLocal, bind, Anim(Anims + "idle"));
                Debug.Log($"[Thundergun] View space offset: {retarget.Offset}, shoulders moved {retarget.ShoulderShifts}");
                retarget.PlaceMarkers(Anim(Anims + "idle"), null);

                // One clip per distinct (animation, range)
                var clips = new Dictionary<string, AnimationClip>();
                var handsClips = new Dictionary<string, AnimationClip>();
                foreach (var (state, (anim, first, last, loop)) in HandsMap)
                {
                    CodAnim a = Anim(anim);
                    int f0 = Math.Min(first, a.Frames.Count - 1), f1 = Math.Min(last, a.Frames.Count - 1);
                    Ease.TryGetValue(state, out var ease);
                    string key = $"{anim.Replace(Anims, "tg_")}_{f0}_{f1}" + (ease.easeIn + ease.easeOut > 0 ? $"_e{ease.easeIn}_{ease.easeOut}" : "");
                    if (!clips.TryGetValue(key, out AnimationClip clip))
                    {
                        var clipWarnings = new List<string>();
                        // The shot: the Thundergun has no real recoil, just a push back into the shoulder and a rumble
                        clip = retarget.BakeClip(key, a, f0, f1, loop, clipWarnings, kick: anim == Anims + "fire",
                            easeIn: ease.easeIn, easeOut: ease.easeOut);
                        string clipPath = $"{TgPaths.Clips}/{key}.anim";
                        AssetDatabase.DeleteAsset(clipPath);
                        AssetDatabase.CreateAsset(clip, clipPath);
                        clips[key] = clip;
                        if (clipWarnings.Count > 0)
                            warnings.Add($"{key}: {clipWarnings.Count} frame(s) out of reach, first: {clipWarnings[0]}");
                    }
                    handsClips[state] = clip;
                }
                AnimationClip idle = handsClips["IDLE"];
                Debug.Log($"[Thundergun] Baked {clips.Count} clips for {handsClips.Count} Hands states");

                // HandsMap's "999" (to the end) is resolved per animation for the notetrack spans
                var resolved = HandsMap.ToDictionary(kv => kv.Key, kv => (kv.Value.anim, Math.Min(kv.Value.first, Anim(kv.Value.anim).Frames.Count - 1),
                    Math.Min(kv.Value.last, Anim(kv.Value.anim).Frames.Count - 1), kv.Value.loop));
                AnimatorController controller = TgController.Build(ControllerPath, root.transform, handsClips, idle,
                    StateSounds(resolved, anims.ToDictionary(kv => kv.Key, kv => kv.Value.Frames.Count - 1)), Contiguous(resolved), StateSpeeds);
                AddGunCollider(weapon, jgun);
                // The air wave the client plays along each shot (inactive here: a template, copied into the world)
                if (rig.TryGetValue("fireport", out Transform fireport))
                    TgBlast.Build(fireport);
                else
                    warnings.Add("no fireport in the rig: no blast wave");

                // The canisters are part of the gun model (BO1 animates both through every reload), so the Zeus
                // canister item EFT hangs in the magazine slots, and the charge it shows in the chamber, stay out of
                // sight. The slots still ride the canisters' bones.
                foreach (string slot in new[] { "mod_magazine", "mod_magazine_new", "patron_in_weapon" })
                {
                    if (rig.TryGetValue(slot, out Transform t))
                        t.localScale = Vector3.one * 0.001f;
                }

                // Rest pose for the prefab (inventory icons, and before the Animator runs): the idle
                retarget.ApplyFrame(Anim(Anims + "idle"), 0);

                var animator = root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var links = root.AddComponent<TransformLinks>();
                CopyFields(links, ComponentFields("TransformLinks", "_10_12g.generated"), r => ResolveInRig(r, root.transform));
                links.Self = root.transform;

                AssetDatabase.DeleteAsset(WeaponPrefabPath);
                GameObject weaponPrefab = PrefabUtility.SaveAsPrefabAsset(root, WeaponPrefabPath);
                BuildContainer(weaponPrefab, controller, sounds);
                BuildCanister(gun, gunBones, materials, CanisterPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            foreach (string w in warnings)
                Debug.LogWarning("[Thundergun] " + w);
            AssetDatabase.SaveAssets();
            Debug.Log("[Thundergun] Import done");
        }

        // ---------- Gun model ----------

        private static Dictionary<string, Transform> BuildGunSkeleton(CodModel gun, Transform jgun)
        {
            var bones = new Dictionary<string, Transform> { [gun.Bones[0].Name] = jgun };
            Quaternion[] world = gun.Bones.Select(b => TgSpace.Rot(b.Rest)).ToArray();
            Vector3[] pos = gun.Bones.Select(b => TgSpace.Pos(b.Rest.Pos)).ToArray();
            for (int i = 1; i < gun.Bones.Count; i++)
            {
                CodBone b = gun.Bones[i];
                var t = new GameObject(b.Name).transform;
                t.SetParent(bones[gun.Bones[b.Parent].Name], false);
                t.gameObject.layer = jgun.gameObject.layer;
                Quaternion parentRot = world[b.Parent];
                t.localPosition = Quaternion.Inverse(parentRot) * (pos[i] - pos[b.Parent]);
                t.localRotation = Quaternion.Inverse(parentRot) * world[i];
                bones[b.Name] = t;
            }
            return bones;
        }

        /// <summary>The model is rigid (one bone per vertex): a plain mesh per bone, no skinning.</summary>
        private static void BuildGunMeshes(CodModel gun, Dictionary<string, Transform> bones, Dictionary<int, Material> materials)
        {
            int hidden = gun.Materials.FindIndex(m => m.Name == HiddenMaterial);
            foreach (var group in Wires(gun, materials, LightSections(gun, materials)).Where(f => f.Material != hidden).GroupBy(f => FaceBone(gun, f)))
            {
                string boneName = gun.Bones[group.Key].Name;
                Mesh mesh = BuildMesh(gun, group.ToList(), group.Key, $"{TgPaths.WeaponName}_{boneName}", out List<int> mats);
                if (mesh == null)
                    continue;
                var go = new GameObject($"{TgPaths.WeaponName}_{boneName}_LOD0");
                go.layer = bones[boneName].gameObject.layer;
                go.transform.SetParent(bones[boneName], false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => materials[m]).ToArray();
            }
        }

        /// <summary>
        /// A box around the gun, switched off like the vanilla guns' (the game turns it on when the gun is dropped).
        /// Left on, it pushes against the player's own body while held: the slide across the floor.
        /// </summary>
        private static void AddGunCollider(Transform weapon, Transform jgun)
        {
            var bounds = new Bounds();
            bool first = true;
            foreach (MeshFilter mf in jgun.GetComponentsInChildren<MeshFilter>())
            {
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    Vector3 p = weapon.InverseTransformPoint(mf.transform.TransformPoint(v));
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
            }
            var go = new GameObject($"{TgPaths.WeaponName}_collider");
            go.layer = weapon.gameObject.layer;
            go.transform.SetParent(weapon, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
            box.enabled = false;
        }

        /// <summary>First synthetic material index: the canister grille's four sections, tg_light_1..4.</summary>
        public const int LightMaterialBase = 1000;
        /// <summary>Synthetic material index for the wire bundle (split off front_body_b).</summary>
        public const int WiresMaterial = 2000;

        /// <summary>
        /// Render queue that keeps Pack-a-Punch's camo off a material: PaP doesn't paint anything from the alpha-test
        /// queue up. Still drawn as solid. Used for the parts that keep their own look on the Zeus Cannon.
        /// </summary>
        private const int KeepLookQueue = 2450;

        /// <summary>
        /// The wire bundle shares front_body_b with body panels; its faces (the colourful strip at the right of the
        /// texture, u above 0.74) get their own copy of the material so the camo can skip just the wires.
        /// </summary>
        private static IEnumerable<CodFace> Wires(CodModel gun, Dictionary<int, Material> materials, IEnumerable<CodFace> faces)
        {
            int body = gun.Materials.FindIndex(m => m.Name == "mtl_wpn_t7_zmb_hd_thundergun_front_body_b");
            if (body < 0)
            {
                foreach (CodFace f in faces)
                    yield return f;
                yield break;
            }
            string path = $"{TgPaths.Materials}/tg_wires.mat";
            AssetDatabase.DeleteAsset(path);
            var wires = new Material(BuildMaterial(gun.Materials[body], false)) { name = "tg_wires" };
            KeepLook(wires);
            AssetDatabase.CreateAsset(wires, path);
            materials[WiresMaterial] = wires;
            foreach (CodFace f in faces)
            {
                if (f.Material == body && f.Corners.All(c => c.U > 0.74f))
                    yield return new CodFace { Object = f.Object, Material = WiresMaterial, Corners = f.Corners };
                else
                    yield return f;
            }
        }

        /// <summary>
        /// The canister disc's grille is the ammo counter: four sections that glow orange, one per charge. In the
        /// model it's one material, so its faces are split by section (nearest of the disc's four glow_N patches)
        /// into tg_light_1..4, each glowing with the grille texture; the client mod turns them on and off.
        /// </summary>
        private static IEnumerable<CodFace> LightSections(CodModel gun, Dictionary<int, Material> materials)
        {
            int grille = gun.Materials.FindIndex(m => m.Name == "mtl_t5_weapon_thundergun_mesh");
            if (grille < 0)
            {
                // The HD model has real bulbs (glow_1..4 on tag_bulb1..4): nothing to split
                foreach (CodFace f in gun.Faces)
                    yield return f;
                yield break;
            }
            var centres = new Dictionary<int, Vector3>();
            for (int n = 1; n <= 4; n++)
            {
                int glow = gun.Materials.FindIndex(m => m.Name == $"mtl_wpn_t7_zmb_hd_thundergun_glow_{n}");
                List<CodFace> patch = gun.Faces.Where(f => f.Material == glow).ToList();
                if (glow >= 0 && patch.Count > 0)
                    centres[n] = patch.Select(Centre).Aggregate(Vector3.zero, (a, b) => a + b) / patch.Count;
            }
            if (grille < 0 || centres.Count < 4)
            {
                Debug.LogWarning("[Thundergun] Canister grille or its four glow patches not found; no ammo lights");
                foreach (CodFace f in gun.Faces)
                    yield return f;
                yield break;
            }
            for (int n = 1; n <= 4; n++)
                materials[LightMaterialBase + n] = BuildLightMaterial(gun.Materials[grille], n);

            Vector3 Centre(CodFace f) => f.Corners.Select(c => TgSpace.Pos(gun.Vertices[c.Vertex].Pos)).Aggregate(Vector3.zero, (a, b) => a + b) / 3f;
            var counts = new int[5];
            foreach (CodFace f in gun.Faces)
            {
                if (f.Material != grille)
                {
                    yield return f;
                    continue;
                }
                Vector3 c = Centre(f);
                int section = centres.OrderBy(kv => (kv.Value - c).sqrMagnitude).First().Key;
                counts[section]++;
                yield return new CodFace { Object = f.Object, Material = LightMaterialBase + section, Corners = f.Corners };
            }
            Debug.Log($"[Thundergun] Ammo light sections: {string.Join(", ", Enumerable.Range(1, 4).Select(n => $"{n}: {counts[n]} faces"))}");
        }

        /// <summary>A grille section: the grille's own texture, glowing orange. Shipped lit for sections 3 and 4 (a full plain canister).</summary>
        private static Material BuildLightMaterial(CodMaterial grille, int section)
        {
            Material source = BuildMaterial(grille, false);
            string path = $"{TgPaths.Materials}/tg_light_{section}.mat";
            AssetDatabase.DeleteAsset(path);
            var mat = new Material(source) { name = $"tg_light_{section}" };
            bool lit = section == 3 || section == 4;
            SetEmission(mat, source.GetTexture("_MainTex") as Texture2D, lit ? new Color(1f, 0.42f, 0.1f) * 1.8f : Color.black);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static int FaceBone(CodModel gun, CodFace f)
        {
            var votes = f.Corners.Select(c => gun.Vertices[c.Vertex].Weights.OrderByDescending(w => w.weight).First().bone).ToList();
            return votes.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        }

        /// <summary>Faces into a mesh in the bone's local space, one submesh per material.</summary>
        private static Mesh BuildMesh(CodModel gun, List<CodFace> faces, int bone, string name, out List<int> materialOrder)
        {
            Quaternion inv = Quaternion.Inverse(TgSpace.Rot(gun.Bones[bone].Rest));
            Vector3 origin = TgSpace.Pos(gun.Bones[bone].Rest.Pos);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var unique = new Dictionary<(int, Vector3, Vector2), int>();
            var submeshes = new Dictionary<int, List<int>>();

            foreach (CodFace f in faces)
            {
                if (!submeshes.TryGetValue(f.Material, out List<int> tris))
                    submeshes[f.Material] = tris = new List<int>();
                var corner = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    CodCorner k = f.Corners[c];
                    Vector3 n = (inv * TgSpace.Dir(k.Normal)).normalized;
                    var uv = new Vector2(k.U, 1f - k.V);
                    var key = (k.Vertex, n, uv);
                    if (!unique.TryGetValue(key, out int index))
                    {
                        index = vertices.Count;
                        unique[key] = index;
                        vertices.Add(inv * (TgSpace.Pos(gun.Vertices[k.Vertex].Pos) - origin));
                        normals.Add(n);
                        uvs.Add(uv);
                    }
                    corner[c] = index;
                }
                tris.AddRange(corner);
            }
            if (vertices.Count == 0)
            {
                materialOrder = null;
                return null;
            }

            materialOrder = submeshes.Keys.OrderBy(k => k).ToList();
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = materialOrder.Count;
            for (int i = 0; i < materialOrder.Count; i++)
            {
                int[] tris = submeshes[materialOrder[i]].ToArray();
                // The axis mirror flips winding: keep whichever winding agrees with the vertex normals
                int agree = 0;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = vertices[tris[t]], b = vertices[tris[t + 1]], c = vertices[tris[t + 2]];
                    if (Vector3.Dot(Vector3.Cross(b - a, c - a), normals[tris[t]]) > 0f)
                        agree++;
                }
                if (agree * 2 < tris.Length / 3)
                {
                    for (int t = 0; t < tris.Length; t += 3)
                        (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
                }
                // The ammo bulbs are tiny cards set into the drum: draw them from both sides so none is culled away
                int matIndex = materialOrder[i];
                if (matIndex < gun.Materials.Count && gun.Materials[matIndex].Name.Contains("_glow_"))
                    tris = tris.Concat(Enumerable.Range(0, tris.Length / 3).SelectMany(t => new[] { tris[t * 3], tris[t * 3 + 2], tris[t * 3 + 1] })).ToArray();
                mesh.SetTriangles(tris, i);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            string path = $"{TgPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // ---------- Canisters ----------

        /// <summary>
        /// The Pressure Cartridge item model: the drum, with the four bulb cards set in it (tag_bulb1..4 hang off the
        /// drum). They're the gun's own tg_light materials; the client mod lights two on a full cartridge.
        /// </summary>
        private static void BuildCanister(CodModel gun, Dictionary<string, Transform> bones, Dictionary<int, Material> materials, string path)
        {
            int boneIndex = gun.BoneIndex(CanisterBones[0]);
            var drumBones = new HashSet<int> { boneIndex };
            for (int n = 1; n <= 4; n++)
                drumBones.Add(gun.BoneIndex($"tag_bulb{n}"));
            List<CodFace> faces = gun.Faces.Where(f => drumBones.Contains(FaceBone(gun, f))).ToList();
            string name = Path.GetFileNameWithoutExtension(path);
            Mesh mesh = BuildMesh(gun, faces, boneIndex, $"{name}_mesh", out List<int> mats);
            if (mesh == null)
            {
                Debug.LogError($"[Thundergun] No canister geometry on {CanisterBones[0]}");
                return;
            }

            var root = new GameObject(name);
            try
            {
                var lod = new GameObject($"{name}_LOD0");
                lod.transform.SetParent(root.transform, false);
                lod.AddComponent<MeshFilter>().sharedMesh = mesh;
                lod.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => materials[m]).ToArray();
                // Off, like vanilla magazines: the canister rides in the gun, and an active collider there pushes
                // against the player's own body (the slide across the floor). The game enables it when dropped.
                var box = lod.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
                box.enabled = false;

                // EFT places the top cartridges here; charges live inside the canister, so keep them out of sight
                foreach (string patron in new[] { "patron_001", "patron_002" })
                {
                    var p = new GameObject(patron).transform;
                    p.SetParent(root.transform, false);
                    p.localPosition = mesh.bounds.center;
                    p.localScale = Vector3.one * 0.01f;
                }

                var pivot = root.AddComponent<PreviewPivot>();
                CopyFields(pivot, ComponentFields("PreviewPivot", "mag_saiga12", "mag_components.json"), _ => null);
                pivot.pivotPosition = mesh.bounds.center;

                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Thundergun] Canister prefab {path}: {faces.Count} faces, bounds {mesh.bounds.size}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- Container (WeaponPrefab and friends) ----------

        private static void BuildContainer(GameObject weaponPrefab, AnimatorController controller, Dictionary<string, AudioClip> sounds)
        {
            var root = new GameObject(TgPaths.ContainerName);
            try
            {
                SoundBank body = BuildBank("thundergun_fire", 600f, new[] { sounds["thundergun_fire_01"], sounds["thundergun_fire_02"] });
                SoundBank tail = BuildBank("thundergun_tail", 600f, new[] { sounds["silence"] });
                SoundBank bodySilenced = BuildBank("thundergun_fire_silenced", 130f, new[] { sounds["thundergun_fire_01"], sounds["thundergun_fire_02"] });
                SoundBank tailSilenced = BuildBank("thundergun_tail_silenced", 130f, new[] { sounds["silence"] });

                Object Resolve(string r)
                {
                    if (r == null) return null;
                    if (r.StartsWith("GameObject:") && r.Contains(".generated")) return weaponPrefab;
                    if (r.StartsWith("SO:SoundBank:"))
                    {
                        string bank = r.Substring("SO:SoundBank:".Length);
                        return bank.EndsWith("tail_silenced") ? tailSilenced : bank.EndsWith("tail") ? tail : bank.EndsWith("silenced") ? bodySilenced : body;
                    }
                    if (r.StartsWith("MB:TransformLinks@")) return weaponPrefab.GetComponent<TransformLinks>();
                    if (r.StartsWith("Transform:")) return ResolveInRig(r, weaponPrefab.transform);
                    return null;
                }

                var wp = root.AddComponent<WeaponPrefab>();
                CopyFields(wp, ComponentFields("WeaponPrefab", "_container"), Resolve);
                wp._weaponObject = weaponPrefab;
                wp._weaponObjectSimple = weaponPrefab;
                wp._originalAnimatorController = controller;
                wp._animatorSimple = null;
                wp._animatorSpirit = null;
                wp._fastAnimatorControllerBinaryData = null;
                wp._avatar = null;
                wp.Colliders = new List<Collider>();
                wp.Renderers = new List<Renderer>();
                wp._originallyEnabledComponents = new List<Component>();
                wp.RegisteredComponentsToClean = new List<Component>();
                wp.PoolHistory = new List<string>();
                wp.Hierarchy = null;

                CopyFields(root.AddComponent<MuzzleManager>(), ComponentFields("MuzzleManager", "_container"), Resolve);
                CopyFields(root.AddComponent<ShellExtractionData>(), ComponentFields("ShellExtractionData", "_container"), Resolve);
                CopyFields(root.AddComponent<PreviewPivot>(), ComponentFields("PreviewPivot", "_container"), Resolve);

                var player = root.AddComponent<WeaponSoundPlayer>();
                CopyFields(player, ComponentFields("WeaponSoundPlayer", "_container"), Resolve);
                player.Body = body;
                player.Tail = tail;
                player.BodySilenced = bodySilenced;
                player.TailSilenced = tailSilenced;
                foreach (WeaponSoundPlayer_SoundElement e in player.AdditionalSounds)
                {
                    string clip = FoleyFor(e.EventName, out float volume);
                    e.SoundClips = new List<AudioClip> { sounds.TryGetValue(clip, out AudioClip c) ? c : sounds["silence"] };
                    e.Volume = volume;
                }
                foreach (var (sound, clip) in NotetrackClips)
                {
                    player.AdditionalSounds.Add(new WeaponSoundPlayer_SoundElement
                    {
                        EventName = "Snd" + sound, RollOff = 30, Volume = 1f,
                        SoundClips = new List<AudioClip> { sounds.TryGetValue(clip, out AudioClip c) ? c : sounds["silence"] },
                    });
                }
                player.MainSounds = player.MainSounds ?? new List<WeaponSoundPlayer_SoundElement>();
                foreach (WeaponSoundPlayer_SoundElement e in player.MainSounds)
                    e.SoundClips = new List<AudioClip> { sounds["thundergun_fire_01"] };
                // Semi-auto: the whole shot plays. As an "automatic" weapon EFT loops the body sound and cuts it one
                // beat later for the tail, which clipped the Thundergun's long blast.
                player.Non_auto = 1;

                AssetDatabase.DeleteAsset(ContainerPrefabPath);
                PrefabUtility.SaveAsPrefabAsset(root, ContainerPrefabPath);
                Debug.Log($"[Thundergun] Container prefab {ContainerPrefabPath}: {player.AdditionalSounds.Count} foley events");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>BO1 / BO3 Thundergun foley for each of EFT's weapon sound events.</summary>
        private static string FoleyFor(string eventName, out float volume)
        {
            volume = 1f;
            switch (eventName)
            {
                case "SndDraw": return "fly_thunder_first_raise";
                case "SndHolster": return "fly_thundergun_cell_slide_lock";
                case "SndMagOut": case "SndMagPullOut": return "thundergun_mag_out";
                case "SndMagOutAll": case "SndMagSbrosnik": return "thundergun_ammo_eject";
                case "SndMagIn": return "thundergun_reload";
                case "SndMagHit": return "thundergun_reload_beep";
                case "SndBoltOut": case "SndBoltOutSlow": case "SndBoltOutTry": return "fly_thundergun_cell_slide_lock";
                case "SndBoltIn": return "fly_thundergun_cell_on";
                // Empty trigger: EFT's own click (the Saiga's), exported from the game's audio bundle
                case "SndDryFire": return "eft_saiga_trigger_empty";
                case "SndMisfire": case "SndJam": return "eft_saiga_trigger_empty";
                case "SndSelector": case "SndButton": case "SndModSwitch": return "thundergun_reload_beep";
                case "SndAmmoIn": case "SndShellInGun": case "SndShellGrab": case "SndAmmoCatch": case "SndShellRemove": return "fly_thundergun_cell_replace";
                case "SndGunRattle1": volume = 0.35f; return "thundergun_gen_00";
                case "SndGunRattle2": volume = 0.35f; return "thundergun_gen_01";
                default: return "silence";
            }
        }

        private static SoundBank BuildBank(string name, float rolloff, AudioClip[] clips)
        {
            string path = $"{TgPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            var bank = ScriptableObject.CreateInstance<SoundBank>();
            bank.name = name;
            bank.BaseVolume = 1f;
            bank.BasePitch = 1f;
            bank.Rolloff = rolloff;
            bank.HasEnvironment = 1;
            bank._clipLiength = clips.Max(c => c.length);
            // Outdoor and indoor; close, distant and far (BlendOptions left empty: EFT then always uses the close set)
            bank.Environments = Enumerable.Range(0, 2).Select(_ => new SoundBank_EnvironmentVariety
            {
                Clips = Enumerable.Range(0, 3).Select(__ => new SoundBank_DistanceVarity { Clips = clips.ToList() }).ToList(),
            }).ToList();
            AssetDatabase.CreateAsset(bank, path);
            return bank;
        }

        // ---------- Sounds ----------

        private static Dictionary<string, AudioClip> ImportSounds()
        {
            var sources = new List<string>();
            sources.AddRange(Directory.GetFiles(TgPaths.SoundDir, "*.wav").Where(f => !f.EndsWith("_bk.wav") && !f.EndsWith("_old.wav")));
            foreach (string rel in new[]
            {
                @"fly\weapon\reload\zm\thunder\fly_thunder_first_raise.wav",
                @"wpn\energy\thundergun\reload\plr\fly_thundergun_cell_on.wav",
                @"wpn\energy\thundergun\reload\plr\fly_thundergun_cell_replace.wav",
                @"wpn\energy\thundergun\reload\plr\fly_thundergun_cell_slide_lock.wav",
                @"wpn\energy\thundergun\reload\plr\fly_thundergun_eject.wav",
                @"wpn\energy\thundergun\plr\shot\wpn_thundergun_fire_plr.wav",
                @"wpn\energy\thundergun\plr\shot\wpn_thundergun_fire_rear_plr.wav",
                @"wpn\energy\thundergun\lfe\wpn_thundergun_fire_lfe.wav",
            })
            {
                string full = Path.Combine(TgPaths.Bo3SoundDir, rel);
                if (File.Exists(full))
                    sources.Add(full);
                else
                    Debug.LogWarning($"[Thundergun] BO3 sound missing: {full}");
            }

            string silence = $"{TgPaths.Audio}/silence.wav";
            if (!File.Exists(silence))
                File.WriteAllBytes(silence, SilentWav(0.05f));

            var clips = new Dictionary<string, AudioClip>();
            foreach (string source in sources)
            {
                string asset = $"{TgPaths.Audio}/{Path.GetFileName(source)}";
                if (!File.Exists(asset))
                    File.Copy(source, asset);
            }

            // Replacements: a file in sounds_custom named like a sound (any format Unity imports) takes its place,
            // e.g. sounds_custom/thundergun_reload.mp3 replaces thundergun_reload
            var custom = new Dictionary<string, string>();
            if (Directory.Exists(TgPaths.CustomSoundDir))
            {
                foreach (string file in Directory.GetFiles(TgPaths.CustomSoundDir).Where(f => new[] { ".wav", ".mp3", ".ogg", ".aif", ".aiff" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    string asset = $"{TgPaths.Audio}/custom_{Path.GetFileName(file)}";
                    File.Copy(file, asset, true);
                    custom[Path.GetFileNameWithoutExtension(file)] = asset;
                }
            }
            AssetDatabase.Refresh();
            foreach (string asset in Directory.GetFiles(TgPaths.Audio).Where(f => !f.EndsWith(".meta") && !Path.GetFileName(f).StartsWith("custom_")))
            {
                string path = asset.Replace('\\', '/');
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    clips[Path.GetFileNameWithoutExtension(path)] = clip;
            }
            foreach (var (name, asset) in custom)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(asset);
                if (clip == null)
                    continue;
                clips[name] = clip;
                Debug.Log($"[Thundergun] Custom sound: {name} ({clip.length:F2}s)");
            }
            Debug.Log($"[Thundergun] {clips.Count} sounds");
            return clips;
        }

        private static byte[] SilentWav(float seconds)
        {
            const int rate = 22050;
            int samples = (int)(rate * seconds);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + samples * 2);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(samples * 2);
                w.Write(new byte[samples * 2]);
                return ms.ToArray();
            }
        }

        // ---------- Materials ----------

        /// <summary>
        /// Standard (Specular setup), like PaP. The port's _images folder per material holds the colour (_c), normal
        /// (_n), specular (_s) and gloss (_g) maps; the four glow materials light up the back body with BO3's emissive
        /// map (its _up variant for the Pack-a-Punched look).
        /// </summary>
        /// <summary>
        /// Blue copies of the control box (its gauge and lamp glow yellow): the emission map with red and blue swapped.
        /// They ride on a switched-off renderer (tg_origins_materials); the client swaps them in on a gun upgraded in the
        /// Origins machine, and back out.
        /// </summary>
        private static void AddOriginsMaterials(CodModel gun, Transform jgun, Dictionary<int, Material> materials)
        {
            var blues = new List<Material>();
            foreach (int index in Enumerable.Range(0, gun.Materials.Count).Where(i => gun.Materials[i].Name.EndsWith("_control_box")))
            {
                Material source = materials[index];
                if (!(source.GetTexture("_EmissionMap") is Texture2D emission))
                    continue;
                string path = AssetDatabase.GetAssetPath(emission);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                tex.LoadImage(File.ReadAllBytes(path));
                Color32[] pixels = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                    (pixels[i].r, pixels[i].b) = (pixels[i].b, pixels[i].r);
                tex.SetPixels32(pixels);
                string bluePath = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_blue.png");
                File.WriteAllBytes(bluePath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(bluePath);
                var importer = (TextureImporter)AssetImporter.GetAtPath(bluePath);
                importer.sRGBTexture = true;
                importer.SaveAndReimport();

                string matPath = $"{TgPaths.Materials}/{source.name}_blue.mat";
                AssetDatabase.DeleteAsset(matPath);
                var blue = new Material(source) { name = source.name + "_blue" };
                blue.SetTexture("_EmissionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(bluePath));
                AssetDatabase.CreateAsset(blue, matPath);
                blues.Add(blue);
            }
            var go = new GameObject("tg_origins_materials");
            go.layer = jgun.gameObject.layer;
            go.transform.SetParent(jgun, false);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = blues.ToArray();
            r.enabled = false;
            Debug.Log($"[Thundergun] Origins materials: {string.Join(", ", blues.Select(m => m.name))}");
        }

        public static Material BuildMaterial(CodMaterial m, bool upgraded)
        {
            if (m.Name.StartsWith("mtl_wpn_t7_zmb_hd_thundergun"))
                return BuildMaterialHd(m, upgraded);
            string path = $"{TgPaths.Materials}/{m.Name}{(upgraded ? "_up" : "")}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard (Specular setup)")) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }

            string images = Path.Combine(TgPaths.ModelDir, "_images");
            string folder = Path.Combine(images, m.Name switch
            {
                "t5_thundergun_redlight" => "mtl_t5_weapon_thundergun",
                "t5_thundergun_lightcounter" => "mtl_t5_weapon_thundergun_box_plastic",
                _ => m.Name,
            });

            if (m.Name.Contains("_glow_"))
            {
                // The four sections of the canister's disc are its ammo lights (the client mod lights one per charge).
                // As shipped: the plain gun's two, full. Sections 3 and 4 are the top pair.
                mat.color = Color.black;
                string emissive = Path.Combine(images, "i_wpn_t7_zmb_hd_thundergun_back_body_d_e.tiff");
                bool lit = upgraded || m.Name.EndsWith("_3") || m.Name.EndsWith("_4");
                SetEmission(mat, ImportTexture(emissive, TextureImporterType.Default, true), lit ? new Color(1f, 0.45f, 0.1f) * 2.2f : Color.black);
            }
            else
            {
                string albedo = Pick(folder, "_c.png");
                if (albedo != null)
                    mat.SetTexture("_MainTex", ImportTexture(albedo, TextureImporterType.Default, true));
                mat.color = Color.white;
                string normal = Largest(folder, "_n.png");
                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", ImportTexture(normal, TextureImporterType.NormalMap, false));
                    mat.EnableKeyword("_NORMALMAP");
                }
                string spec = Pick(folder, "_s.png") ?? FindContaining(folder, "_s-r") ?? FindContaining(folder, "spec");
                if (spec != null)
                {
                    mat.SetTexture("_SpecGlossMap", BuildSpecGloss(spec, Pick(folder, "_g.png")));
                    mat.SetFloat("_GlossMapScale", 1f);
                    mat.EnableKeyword("_SPECGLOSSMAP");
                }
                else
                {
                    mat.SetColor("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
                    mat.SetFloat("_Glossiness", 0.4f);
                }

                // Materials are reused between imports: start without glow, then add the lights' own
                mat.SetTexture("_EmissionMap", null);
                mat.SetColor("_EmissionColor", Color.black);
                mat.DisableKeyword("_EMISSION");
                if (m.Name == "t5_thundergun_redlight")
                    SetEmission(mat, null, new Color(1f, 0.08f, 0.04f) * 2f);
                else if (m.Name == "t5_thundergun_lightcounter")
                    SetEmission(mat, null, new Color(0.3f, 0.75f, 1f) * 1.5f);
                else if (m.Name == "t5_thundergun_lightbox")
                    SetEmission(mat, null, new Color(0.45f, 0.85f, 1f) * 1.2f);

                if (m.Name.Contains("glass"))
                    MakeTransparent(mat, 0.35f);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// The HD model's materials: Standard (Specular setup) from Greyhound's per-material maps (_c colour, _n normal,
        /// _s specular with _g gloss in its alpha, _o occlusion). glow_1..4 are the canister drum's four bulbs, the
        /// ammo lights, named tg_light_1..4 for the client mod; shipped with the plain gun's two (2 and 3) lit.
        /// </summary>
        private static Material BuildMaterialHd(CodMaterial m, bool upgraded)
        {
            string light = m.Name.StartsWith("mtl_wpn_t7_zmb_hd_thundergun_glow_") ? m.Name.Substring(m.Name.Length - 1) : null;
            string name = light != null ? $"tg_light_{light}" : m.Name;
            string path = $"{TgPaths.Materials}/{name}{(upgraded ? "_up" : "")}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard (Specular setup)")) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetTexture("_EmissionMap", null);
            mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");

            string folder = Path.Combine(TgBo3.ModelDir, "_images", m.Name);
            if (light != null)
            {
                // A grille section's glow card, behind the cut-out grille: BO3's own section glow (back_body_d_e),
                // or flat purple for the Zeus Cannon
                // Shipped dark: the client lights them from the charges, and a model it isn't driving stays dark
                bool lit = false;
                mat.color = new Color(0.08f, 0.05f, 0.04f);
                mat.SetColor("_SpecColor", new Color(0.3f, 0.3f, 0.3f));
                mat.SetFloat("_Glossiness", 0.8f);
                string glow = FindBySuffix(folder, "_e.png");
                Texture2D map = glow != null ? ImportEmissive(glow) : null;
                Color on = map != null ? Color.white * 3f : new Color(1f, 0.45f, 0.1f) * 5f; // orange on the Zeus Cannon too
                SetEmission(mat, map, lit ? on : Color.black);
            }
            else if (m.Name.EndsWith("_glass"))
            {
                mat.color = new Color(0.1f, 0.1f, 0.12f);
                mat.SetFloat("_Glossiness", 0.9f);
                MakeTransparent(mat, 0.3f);
            }
            else if (m.Name.EndsWith("_glo"))
            {
                // A small glowing part (the lamp at the drum's hub): orange, blue on an Origins gun (the client sets that)
                mat.color = new Color(1f, 0.5f, 0.12f);
                SetEmission(mat, null, new Color(1f, 0.45f, 0.1f) * 1.4f);
            }
            else
            {
                string albedo = FindBySuffix(folder, "_c.png");
                mat.SetTexture("_MainTex", albedo != null ? ImportTexture(albedo, TextureImporterType.Default, true) : null);
                mat.color = Color.white;
                string normal = FindBySuffix(folder, "_n.png");
                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", ImportTexture(normal, TextureImporterType.NormalMap, false));
                    mat.EnableKeyword("_NORMALMAP");
                }
                string spec = FindBySuffix(folder, "_s.png");
                if (spec != null)
                {
                    mat.SetTexture("_SpecGlossMap", BuildSpecGloss(spec, FindBySuffix(folder, "_g.png")));
                    mat.SetFloat("_GlossMapScale", 1f);
                    mat.EnableKeyword("_SPECGLOSSMAP");
                }
                string occlusion = FindBySuffix(folder, "_o.png");
                if (occlusion != null)
                    mat.SetTexture("_OcclusionMap", ImportTexture(occlusion, TextureImporterType.Default, false));

                // Glowing details (the gauge's backlight, the lens, the knob) come from BO3's emissive maps
                string emissive = FindBySuffix(folder, "_e.png");
                if (emissive != null)
                    SetEmission(mat, ImportEmissive(emissive), Color.white * 1.6f);

                // The drum's ring grille is a mesh with holes (the colour map's alpha): cut out, so the glowing
                // sections behind it show through as in BO3
                if (m.Name.EndsWith("_back_body_b") || m.Name.EndsWith("_back_body_c"))
                    MakeCutout(mat, 0.5f);
                else if (m.Name.EndsWith("_control_box"))
                    KeepLook(mat);
                else
                    mat.renderQueue = -1; // the shader's own
            }
            // The ammo lights and the gauge's glow keep their own look on the Zeus Cannon too
            if (light != null || m.Name.EndsWith("_glo"))
                KeepLook(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Out of Pack-a-Punch's reach (see <see cref="KeepLookQueue"/>). Unity puts an opaque Standard material back
        /// in the shader's own queue whenever it validates it, so a custom queue alone never reached the bundle; a
        /// cutout that clips nothing keeps the alpha-test queue and draws exactly like the opaque one.
        /// </summary>
        private static void KeepLook(Material mat) => MakeCutout(mat, 0f);

        private static void MakeCutout(Material mat, float cutoff)
        {
            mat.SetFloat("_Mode", 1f);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static string FindBySuffix(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("$")) : null;

        private static void SetEmission(Material mat, Texture2D map, Color color)
        {
            mat.SetTexture("_EmissionMap", map);
            mat.SetColor("_EmissionColor", color);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.EnableKeyword("_EMISSION");
        }

        private static void MakeTransparent(Material mat, float alpha)
        {
            mat.SetFloat("_Mode", 2f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
            Color c = mat.color;
            c.a = alpha;
            mat.color = c;
        }

        /// <summary>Prefers the port's full-resolution "_-g" maps over the "~-g" previews.</summary>
        private static string Pick(string dir, string suffix)
        {
            if (!Directory.Exists(dir))
                return null;
            string[] files = Directory.GetFiles(dir, "*" + suffix).Where(f => !Path.GetFileName(f).StartsWith("$")).ToArray();
            return files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("_-")) ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        private static string Largest(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault() : null;

        private static string FindContaining(string dir, string part) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").FirstOrDefault(f => Path.GetFileName(f).Contains(part)) : null;

        private static Texture2D ImportTexture(string source, TextureImporterType type, bool srgb)
        {
            if (!File.Exists(source))
                return null;
            string assetPath = $"{TgPaths.Textures}/{Path.GetFileName(source).Replace("&", "_").Replace("$", "_").Replace("~", "_")}";
            if (!File.Exists(assetPath))
            {
                File.Copy(source, assetPath);
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer.textureType != type || importer.sRGBTexture != srgb || importer.maxTextureSize != 4096)
            {
                importer.textureType = type;
                importer.sRGBTexture = srgb;
                importer.maxTextureSize = 4096; // the port's full-resolution maps: 2K read pixelated in first person
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private const int EmissiveSize = 1024;

        /// <summary>
        /// BO3's glow maps are small (the grille sections' is 128x128) and compressed they read as a grid of pixels up
        /// close. Imported as a smooth copy instead: upscaled bilinearly to <see cref="EmissiveSize"/>, blurred by
        /// about one source pixel so no texel edges remain, and stored uncompressed with trilinear filtering.
        /// </summary>
        private static Texture2D ImportEmissive(string source)
        {
            if (source == null || !File.Exists(source))
                return null;
            string assetPath = $"{TgPaths.Textures}/{Path.GetFileNameWithoutExtension(source).Replace("&", "_").Replace("$", "_").Replace("~", "_")}_smooth.png";
            if (!File.Exists(assetPath))
            {
                var src = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                src.LoadImage(File.ReadAllBytes(source));
                int size = Mathf.Max(EmissiveSize, Mathf.Max(src.width, src.height));
                int radius = Mathf.Max(1, size / Mathf.Max(src.width, src.height) / 2);
                var up = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        up[y * size + x] = src.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size);
                Color[] smooth = BoxBlur(BoxBlur(up, size, radius, true), size, radius, false);
                var dst = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                dst.SetPixels(smooth);
                File.WriteAllBytes(assetPath, dst.EncodeToPNG());
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer.textureCompression != TextureImporterCompression.Uncompressed || importer.filterMode != FilterMode.Trilinear)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 4;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>One pass of a box blur, across rows or down columns (edges clamp).</summary>
        private static Color[] BoxBlur(Color[] pixels, int size, int radius, bool horizontal)
        {
            var result = new Color[pixels.Length];
            float weight = 1f / (radius * 2 + 1);
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                {
                    Color sum = Color.clear;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int c = Mathf.Clamp(b + k, 0, size - 1);
                        sum += horizontal ? pixels[a * size + c] : pixels[c * size + a];
                    }
                    result[horizontal ? a * size + b : b * size + a] = sum * weight;
                }
            }
            return result;
        }

        /// <summary>Standard (Specular setup) reads smoothness from the specular map's alpha, so pack _g into _s.a.</summary>
        private static Texture2D BuildSpecGloss(string specSource, string glossSource)
        {
            string assetPath = $"{TgPaths.Textures}/{Path.GetFileNameWithoutExtension(specSource).Replace("&", "_").Replace("$", "_").Replace("~", "_")}_sg.png";
            if (!File.Exists(assetPath))
            {
                var spec = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                spec.LoadImage(File.ReadAllBytes(specSource));
                Texture2D gloss = null;
                if (glossSource != null)
                {
                    gloss = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    gloss.LoadImage(File.ReadAllBytes(glossSource));
                }
                int w = spec.width, h = spec.height;
                Color[] pixels = spec.GetPixels();
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        pixels[i].a = gloss != null ? gloss.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r : 0.4f;
                    }
                }
                spec.SetPixels(pixels);
                File.WriteAllBytes(assetPath, spec.EncodeToPNG());
                Object.DestroyImmediate(spec);
                if (gloss != null)
                    Object.DestroyImmediate(gloss);
                AssetDatabase.ImportAsset(assetPath);
                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                importer.maxTextureSize = 4096;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // ---------- Copying the Saiga's component values ----------

        /// <summary>The dumped fields of the Saiga component of this class whose owner path contains <paramref name="ownerPart"/>.</summary>
        private static object ComponentFields(string cls, string ownerPart, string file = "saiga_components.json")
        {
            object all = MiniJson.Parse(File.ReadAllText(TgPaths.Reference(file)));
            object entry = all.Arr().FirstOrDefault(c => c.Str("class") == cls && c.Str("owner").Contains(ownerPart));
            if (entry == null)
                throw new InvalidOperationException($"No {cls} on {ownerPart} in {file}");
            return entry.Get("fields");
        }

        /// <summary>Saiga rig paths are "Transform:weapon_..._10_12g.generated/a/b"; ours are the same below the root.</summary>
        private static Object ResolveInRig(string reference, Transform root)
        {
            if (reference == null || !reference.StartsWith("Transform:"))
                return null;
            string path = reference.Substring("Transform:".Length);
            int slash = path.IndexOf('/');
            if (slash < 0)
                return path.EndsWith(".generated") ? root : null;
            return root.Find(path.Substring(slash + 1));
        }

        /// <summary>
        /// Fills a stub's serialized fields from a dumped component: values by name, nested classes and lists
        /// recursively, object references through <paramref name="resolve"/> (unknown ones become null).
        /// </summary>
        public static void CopyFields(object target, object json, Func<string, Object> resolve)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = json.Get(field.Name);
                if (value == null && !(json is Dictionary<string, object> d && d.ContainsKey(field.Name)))
                    continue;
                field.SetValue(target, Convert(value, field.FieldType, resolve));
            }
        }

        private static object Convert(object value, Type type, Func<string, Object> resolve)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                Object o = value is Dictionary<string, object> r ? resolve(r.Str("ref")) : null;
                return o != null && type.IsInstanceOfType(o) ? o : null;
            }
            if (value == null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return value as string ?? "";
            if (type == typeof(float)) return (float)(double)value;
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return (int)(long)(double)value;
            if (type == typeof(uint)) return (uint)(long)(double)value;
            if (type == typeof(long)) return (long)(double)value;
            if (type == typeof(short)) return (short)(double)value;
            if (type == typeof(ushort)) return (ushort)(double)value;
            if (type == typeof(byte)) return value is bool b ? (byte)(b ? 1 : 0) : (byte)(double)value;
            if (type == typeof(sbyte)) return (sbyte)(double)value;
            if (type == typeof(bool)) return value is bool bb ? bb : (double)value != 0;
            if (type == typeof(Vector2)) return new Vector2(value.Num("x"), value.Num("y"));
            if (type == typeof(Vector3)) return new Vector3(value.Num("x"), value.Num("y"), value.Num("z"));
            if (type == typeof(Vector4)) return new Vector4(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Quaternion)) return new Quaternion(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Color)) return new Color(value.Num("r"), value.Num("g"), value.Num("b"), value.Num("a"));
            if (type == typeof(LayerMask)) return (LayerMask)value.Int("m_Bits");
            if (type == typeof(AnimationCurve))
            {
                var keys = (value.Get("m_Curve") as List<object> ?? new List<object>())
                    .Select(k => new Keyframe(k.Num("time"), k.Num("value"), k.Num("inSlope"), k.Num("outSlope"), k.Num("inWeight"), k.Num("outWeight"))).ToArray();
                return new AnimationCurve(keys);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                if (value is List<object> items)
                    foreach (object item in items)
                        list.Add(Convert(item, element, resolve));
                return list;
            }
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                object instance = Activator.CreateInstance(type);
                if (value is Dictionary<string, object>)
                    CopyFields(instance, value, resolve);
                return instance;
            }
            return null;
        }
    }
}
