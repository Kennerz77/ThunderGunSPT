using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Thundergun.Editor
{
    /// <summary>
    /// Inventory pictures, rendered here instead of by the game's icon studio (which washes these models out white):
    /// the Thundergun, the Zeus Cannon in Pack-a-Punch's own camo, and a full and a spent Pressure Cartridge. Written
    /// as transparent PNGs to icons/ in the project root; the client plugin ships them and swaps them into the cells.
    /// </summary>
    public static class TgIcons
    {
        public static string IconsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../icons"));

        // PaP's camo albedo (read only, never changed) and the tiling PaP paints it with
        private const string CamoAlbedo = @"C:\Git\PaPHideoutModule\UnityProject\Assets\PaP\Camo\pap_camo_albedo.png";
        private const float CamoTiling = 1f;
        private const float CamoEmission = 0.06f;

        // Bulbs: the plain gun's two (2 and 3), as the client lights them; the Zeus Cannon's four with the purple cast
        private static readonly Color Plain = Color.white * 1.2f;
        private static readonly Color Zeus = new Color(1.1f, 0.7f, 1.6f) * 1.1f;

        private const int Width = 1024;

        public static void RenderAll()
        {
            Directory.CreateDirectory(IconsDir);
            var rig = new List<Object>();
            try
            {
                rig.AddRange(Lights());
                RenderGun("thundergun", camo: null, lit: new[] { 2, 3 }, Plain);
                Texture2D camo = LoadCamo();
                if (camo != null)
                {
                    rig.Add(camo);
                    RenderGun("zeus_cannon", camo, lit: new[] { 1, 2, 3, 4 }, Zeus);
                }
                else
                {
                    Debug.LogWarning($"[Thundergun] PaP camo not found at {CamoAlbedo}; no Zeus Cannon icon");
                }
                RenderCartridge("cartridge_full", lit: new[] { 2, 3 }, spent: new int[0]);
                RenderCartridge("cartridge_empty", lit: new int[0], spent: new[] { 1, 2, 3, 4 });
            }
            finally
            {
                foreach (Object o in rig)
                    Object.DestroyImmediate(o);
            }
        }

        private static IEnumerable<Object> Lights()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
            foreach (var (rotation, intensity) in new[] { (Quaternion.Euler(30f, 60f, 0f), 1.0f), (Quaternion.Euler(-20f, -120f, 0f), 0.45f) })
            {
                var go = new GameObject("icon_light");
                Light light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = intensity;
                go.transform.rotation = rotation;
                yield return go;
            }
        }

        private static Texture2D LoadCamo()
        {
            if (!File.Exists(CamoAlbedo))
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            tex.LoadImage(File.ReadAllBytes(CamoAlbedo));
            return tex;
        }

        /// <summary>
        /// The gun from its left side (the control box), muzzle to the left as EFT pictures its guns. Laid out in CoD
        /// space through j_gun: the barrel (CoD +X) along +Z, up (CoD +Z) along +Y, its left (CoD +Y) along -X.
        /// </summary>
        private static void RenderGun(string name, Texture2D camo, int[] lit, Color on)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(TgImporter.WeaponPrefabPath));
            try
            {
                Transform jgun = go.GetComponentsInChildren<Transform>(true).First(t => t.name == "j_gun");
                jgun.SetParent(null, true);
                jgun.position = Vector3.zero;
                jgun.rotation = TgSpace.Rot(TgBo3.LoadModel().Bones[0].Rest);
                Object.DestroyImmediate(go);
                go = jgun.gameObject;
                Dress(go, camo, lit, on);
                Shoot(go, name, forward: Vector3.right, up: Vector3.up);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>The cartridge face on, from the side its bulbs sit in, the lit pair to the right.</summary>
        private static void RenderCartridge(string name, int[] lit, int[] spent)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(TgImporter.CanisterPrefabPath));
            try
            {
                Dress(go, null, lit, Plain, spent);
                MeshFilter mf = go.GetComponentInChildren<MeshFilter>();
                Bounds b = mf.sharedMesh.bounds;
                // The thin axis is the drum's axle; the bulb cards lie on one face of it
                Vector3 size = b.size;
                Vector3 axle = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
                Vector3 bulbs = BulbCentre(mf) - b.center;
                Vector3 toCamera = Vector3.Dot(bulbs, axle) >= 0f ? axle : -axle;
                Vector3 side = Vector3.ProjectOnPlane(bulbs, axle);
                Vector3 forward = mf.transform.TransformDirection(-toCamera);
                // Screen right = up x forward: pick up so the lit bulbs land on the right
                Vector3 right = mf.transform.TransformDirection(side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.Cross(axle, Vector3.up));
                Shoot(go, name, forward, Vector3.Cross(forward, right));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static Vector3 BulbCentre(MeshFilter mf)
        {
            Mesh mesh = mf.sharedMesh;
            Material[] mats = mf.GetComponent<MeshRenderer>().sharedMaterials;
            Vector3[] vertices = mesh.vertices;
            var sum = Vector3.zero;
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
            {
                if (mats[s] == null || !mats[s].name.StartsWith("tg_light_"))
                    continue;
                foreach (int i in mesh.GetTriangles(s))
                {
                    sum += vertices[i];
                    count++;
                }
            }
            return count > 0 ? sum / count : mesh.bounds.center;
        }

        /// <summary>Copies of the model's materials: the bulbs lit or dark, the rest in camo where PaP would paint it.</summary>
        private static void Dress(GameObject go, Texture2D camo, int[] lit, Color on, int[] spent = null)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterials = r.sharedMaterials.Select(m =>
                {
                    if (m == null)
                        return null;
                    var copy = new Material(m);
                    for (int n = 1; n <= 4; n++)
                    {
                        if (m.name.StartsWith($"tg_light_{n}"))
                        {
                            copy.SetColor("_EmissionColor", lit.Contains(n) ? on : Color.black);
                            // Spent: the card clipped away, as the client does (see Bulb.Show)
                            copy.SetFloat("_Cutoff", spent != null && spent.Contains(n) ? 1.01f : 0f);
                        }
                    }
                    // As PaPCamo.ShouldPaint: nothing from the alpha-test queue up, nothing without a colour map
                    if (camo != null && m.renderQueue < 2450 && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null)
                    {
                        copy.SetTexture("_MainTex", camo);
                        copy.SetTextureScale("_MainTex", Vector2.one * CamoTiling);
                        copy.color = Color.white;
                        copy.SetTexture("_EmissionMap", camo);
                        copy.SetTextureScale("_EmissionMap", Vector2.one * CamoTiling);
                        copy.SetColor("_EmissionColor", new Color(CamoEmission, CamoEmission, CamoEmission));
                        copy.EnableKeyword("_EMISSION");
                    }
                    return copy;
                }).ToArray();
            }
        }

        /// <summary>An orthographic shot framing the model tightly, transparent around it.</summary>
        private static void Shoot(GameObject go, string name, Vector3 forward, Vector3 up)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
            Quaternion view = Quaternion.LookRotation(forward, up);
            Quaternion inv = Quaternion.Inverse(view);
            // Bounds in the camera's frame: corners of every renderer's world bounds
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (Renderer r in renderers)
            {
                Bounds wb = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = wb.center + Vector3.Scale(wb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 p = inv * corner;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            Vector3 centre = view * ((min + max) / 2f);
            float w = max.x - min.x, h = max.y - min.y;
            int width = Width, height = Mathf.Max(64, Mathf.RoundToInt(Width * h / w));

            var camGo = new GameObject("icon_cam");
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = h / 2f * 1.04f;
                cam.aspect = (float)width / height;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.transform.rotation = view;
                cam.transform.position = centre - forward.normalized * ((max.z - min.z) + 1f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = (max.z - min.z) * 2f + 4f;
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(IconsDir, $"{name}.png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log($"[Thundergun] Icon {name}.png: {width}x{height}");
            }
            finally
            {
                RenderTexture.active = null;
                Object.DestroyImmediate(camGo);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
