using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Thundergun.Editor
{
    /// <summary>
    /// Renders the weapon prefab with its clips sampled to logs/preview_*.png, so the retarget can be checked without
    /// the game. EFT's arm mesh isn't ours to ship, so the arms are drawn as sticks along the rig's bones.
    /// </summary>
    public static class TgPreview
    {
        private static readonly (string clip, float t)[] Shots =
        {
            ("tg_idle_0_100", 0f),
            ("tg_fire_0_27", 0.1f),
            ("tg_reload_empty_6_33", 0.5f),
            ("tg_reload_empty_33_62", 0.3f),
            ("tg_first_raise_0_55", 0.95f),
            ("tg_putaway_0_25", 0.3f),
        };

        public static void RenderBatch()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TgImporter.WeaponPrefabPath);
            GameObject go = Object.Instantiate(prefab);
            AddSticks(go.transform);

            var lightGo = new GameObject("light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 0.9f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.38f);

            var camGo = new GameObject("cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
            cam.nearClipPlane = 0.01f;
            var rt = new RenderTexture(960, 640, 24);
            cam.targetTexture = rt;
            Directory.CreateDirectory(TgPaths.LogsDir);

            Transform eye = go.GetComponentsInChildren<Transform>(true).First(t => t.name == "Camera_animated");
            foreach (var (pattern, time) in Shots)
            {
                string file = Directory.GetFiles(TgPaths.Clips, pattern.Replace("*", "") + (pattern.EndsWith("*") ? "*" : "") + ".anim")
                    .Concat(Directory.GetFiles(TgPaths.Clips, pattern + ".anim")).FirstOrDefault();
                if (file == null)
                {
                    Debug.LogWarning($"[Thundergun] Preview: no clip {pattern}");
                    continue;
                }
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(file.Replace('\\', '/'));
                clip.SampleAnimation(go, Mathf.Min(time, clip.length));
                string tag = Path.GetFileNameWithoutExtension(file);

                // First person: from EFT's camera bone (it looks down its local -Y with +Z up)
                cam.fieldOfView = 55f;
                cam.transform.position = eye.position;
                cam.transform.rotation = Quaternion.LookRotation(eye.rotation * Vector3.down, eye.rotation * Vector3.forward);
                Shoot(cam, rt, $"fp_{tag}");

                // From the right side, to judge arm reach and the gun's placement
                cam.fieldOfView = 40f;
                cam.transform.position = eye.position + new Vector3(1.6f, 0.1f, 0.35f);
                cam.transform.LookAt(eye.position + new Vector3(0f, -0.15f, 0.35f));
                Shoot(cam, rt, $"side_{tag}");
            }
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lightGo);
        }

        /// <summary>A thin box from each arm bone to each child, parented to the bone so it follows the pose.</summary>
        private static void AddSticks(Transform root)
        {
            var mat = new Material(Shader.Find("Standard")) { color = new Color(0.85f, 0.6f, 0.45f) };
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Base Human")).ToList())
            {
                foreach (Transform child in t.Cast<Transform>().Where(c => c.name.StartsWith("Base Human")).ToList())
                {
                    float length = child.localPosition.magnitude;
                    if (length < 1e-4f)
                        continue;
                    var stick = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.DestroyImmediate(stick.GetComponent<Collider>());
                    stick.GetComponent<Renderer>().sharedMaterial = mat;
                    stick.transform.SetParent(t, false);
                    stick.transform.localPosition = child.localPosition * 0.5f;
                    stick.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, child.localPosition.normalized);
                    bool finger = t.name.Contains("Digit") || child.name.Contains("Digit");
                    float w = finger ? 0.012f : 0.035f;
                    stick.transform.localScale = new Vector3(w, w, length);
                }
            }
        }

        private static void Shoot(Camera cam, RenderTexture rt, string name)
        {
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(TgPaths.LogsDir, $"preview_{name}.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
