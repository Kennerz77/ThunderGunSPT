using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thundergun.Editor
{
    /// <summary>
    /// The visible air blast. Neither BO1's nor BO3's export carries the Thundergun's effect (only its name), so it's
    /// built here: a cone of rippling, bending air, a pale rush of air through it and a shock ring racing ahead. It
    /// sits inactive under the gun's fireport as <see cref="TemplateName"/>; the client copies it into the world along
    /// each shot and plays it (it doesn't follow the gun). Every system fires once and is gone within half a second.
    /// </summary>
    public static class TgBlast
    {
        public const string TemplateName = "tg_blast_wave";

        private static string Dir => $"{TgPaths.Generated}/Blast";

        public static void Build(Transform fireport)
        {
            if (!AssetDatabase.IsValidFolder(Dir))
                AssetDatabase.CreateFolder(TgPaths.Generated, "Blast");

            Texture2D ripple = SaveTexture("tg_blast_ripple", 128, RipplePixel, linear: true);
            Texture2D ring = SaveTexture("tg_blast_ring", 256, RingPixel, linear: true);
            Texture2D puff = SaveTexture("tg_blast_puff", 64, PuffPixel, linear: false);

            var haze = AssetDatabase.LoadAssetAtPath<Shader>($"{TgPaths.Root}/Shaders/TgHaze.shader");
            if (haze == null)
            {
                // Still a wave, just without the bending
                Debug.LogWarning("[Thundergun] Haze shader missing; the blast uses plain particles");
                haze = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            }
            Material hazeMat = SaveMaterial("tg_blast_haze", haze, ripple, m => m.SetFloat("_Strength", 0.07f));
            Material ringMat = SaveMaterial("tg_blast_ring", haze, ring, m => m.SetFloat("_Strength", 0.11f));
            Material airMat = SaveMaterial("tg_blast_air", Shader.Find("Legacy Shaders/Particles/Alpha Blended"), puff,
                m => m.SetColor("_TintColor", new Color(0.85f, 0.88f, 0.92f, 0.5f)));

            var root = new GameObject(TemplateName);
            root.transform.SetParent(fireport, false);
            root.SetActive(false);

            // Bending air: big soft ripples flung forward and swelling, so the room behind wobbles along the cone
            Systems(root.transform, "haze", hazeMat, burst: 16, lifetime: (0.28f, 0.42f), speed: (16f, 26f), size: (0.5f, 0.8f),
                grow: 4.5f, cone: 22f, alpha: 1f, local: false);
            // A faint pale rush of air, just enough to read in a dark room
            Systems(root.transform, "air", airMat, burst: 22, lifetime: (0.22f, 0.38f), speed: (14f, 24f), size: (0.3f, 0.6f),
                grow: 4f, cone: 18f, alpha: 0.35f, local: false);
            // The shock front: one ring facing down the barrel, racing out and opening wide
            Systems(root.transform, "ring", ringMat, burst: 1, lifetime: (0.32f, 0.32f), speed: (28f, 28f), size: (0.6f, 0.6f),
                grow: 10f, cone: 0f, alpha: 1f, local: true);

            Debug.Log($"[Thundergun] Blast wave template under {fireport.name}: haze, air and shock ring");
        }

        private static void Systems(Transform parent, string name, Material material, int burst, (float, float) lifetime, (float, float) speed,
            (float, float) size, float grow, float cone, float alpha, bool local)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime.Item1, lifetime.Item2);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.Item1, speed.Item2);
            main.startSize = new ParticleSystem.MinMaxCurve(size.Item1, size.Item2);
            main.startRotation = local ? 0f : new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(1f, 1f, 1f, alpha);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(burst, 1) * 2;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = cone > 0f;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = cone;
            shape.radius = 0.06f;

            // Swell as it travels; fade in fast, out over the rest
            ParticleSystem.SizeOverLifetimeModule sizeOver = ps.sizeOverLifetime;
            sizeOver.enabled = true;
            sizeOver.size = new ParticleSystem.MinMaxCurve(grow, new AnimationCurve(new Keyframe(0f, 1f / grow), new Keyframe(1f, 1f)));

            ParticleSystem.ColorOverLifetimeModule colour = ps.colorOverLifetime;
            colour.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
            colour.color = gradient;

            // Slow down as the air spends itself
            ParticleSystem.LimitVelocityOverLifetimeModule drag = ps.limitVelocityOverLifetime;
            drag.enabled = !local;
            drag.drag = 3f;
            drag.multiplyDragByParticleSize = false;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            // The ring lies across the barrel's line; the rest face the camera
            renderer.alignment = local ? ParticleSystemRenderSpace.Local : ParticleSystemRenderSpace.View;
            renderer.sortingFudge = local ? -10f : 0f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.maxParticleSize = 4f;
        }

        // ---------- Textures ----------

        private delegate Color Pixel(float x, float y);

        /// <summary>Soft radial ripples: RG push outward/inward in waves, alpha fades to nothing at the centre and edge.</summary>
        private static Color RipplePixel(float x, float y)
        {
            Vector2 d = new Vector2(x - 0.5f, y - 0.5f) * 2f;
            float r = d.magnitude;
            if (r >= 1f)
                return new Color(0.5f, 0.5f, 0f, 0f);
            Vector2 dir = r > 0.001f ? d / r : Vector2.zero;
            float wave = Mathf.Sin(r * Mathf.PI * 3f);
            Vector2 offset = dir * wave * 0.5f + new Vector2(0.5f, 0.5f);
            float mask = Mathf.SmoothStep(0f, 1f, r * 3f) * Mathf.SmoothStep(1f, 0f, r);
            return new Color(offset.x, offset.y, 0f, mask);
        }

        /// <summary>One sharp ring of pushed air: strongest at its rim, pushing outward.</summary>
        private static Color RingPixel(float x, float y)
        {
            Vector2 d = new Vector2(x - 0.5f, y - 0.5f) * 2f;
            float r = d.magnitude;
            Vector2 dir = r > 0.001f ? d / r : Vector2.zero;
            float band = Mathf.Exp(-Mathf.Pow((r - 0.8f) / 0.08f, 2f));
            Vector2 offset = dir * band * 0.5f + new Vector2(0.5f, 0.5f);
            return new Color(offset.x, offset.y, 0f, band);
        }

        /// <summary>A soft, slightly lumpy puff.</summary>
        private static Color PuffPixel(float x, float y)
        {
            float r = new Vector2(x - 0.5f, y - 0.5f).magnitude * 2f;
            float noise = Mathf.PerlinNoise(x * 6f, y * 6f) * 0.35f + 0.65f;
            float a = Mathf.Clamp01(1f - r) * noise;
            return new Color(1f, 1f, 1f, a * a);
        }

        private static Texture2D SaveTexture(string name, int size, Pixel pixel, bool linear)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, linear);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = pixel((x + 0.5f) / size, (y + 0.5f) / size);
            tex.SetPixels(pixels);
            tex.Apply();
            string path = $"{Dir}/{name}.png";
            File.WriteAllBytes(Path.Combine(Application.dataPath, "..", path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = !linear;
            importer.alphaIsTransparency = !linear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material SaveMaterial(string name, Shader shader, Texture2D tex, System.Action<Material> setup)
        {
            string path = $"{Dir}/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var mat = new Material(shader) { name = name, mainTexture = tex };
            setup(mat);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
