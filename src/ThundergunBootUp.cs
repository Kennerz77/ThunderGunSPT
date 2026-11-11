using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The original's first raise (switches flipped, barrel run out, with its sound) only when the gun is newly in your hands:
    /// bought off the wall, or (Kennerz Zombies) out of the mystery box or Pack-a-Punch. Every other draw is the original's plain
    /// pullout (the user, 2026-11-08, as the Ray Gun). The controller picks the draw by its own "TgBootUp" bool (OUT TO
    /// IDLE ARM = first raise, OUT TO IDLE = pullout; TgImporter.AddBootUpChoice); it's set here as EFT starts the draw
    /// (SetActiveParam). Other mods ask for it with <see cref="Next"/> just before drawing the gun.
    /// </summary>
    public static class ThundergunBootUp
    {
        public const string Param = "TgBootUp";
        private static readonly int ParamHash = Animator.StringToHash(Param);
        private static float _until = -1f;

        /// <summary>The next Thundergun draw (within 10 s) is the first raise.</summary>
        public static void Next() => _until = Time.time + 10f;

        internal static void Enable(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(ObjectInHandsAnimator), nameof(ObjectInHandsAnimator.SetActiveParam));
            if (m != null)
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ThundergunBootUp), nameof(ActivePrefix)));
        }

        private static void ActivePrefix(ObjectInHandsAnimator __instance, bool active)
        {
            if (!active)
                return;
            try
            {
                var a = __instance?.Animator;
                if (a == null || !a.HasParameter(ParamHash))
                    return;
                bool boot = Time.time < _until;
                a.SetBool(ParamHash, boot);
                if (boot)
                    _until = -1f;
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Thundergun first raise choice failed: {e.Message}");
            }
        }
    }
}
