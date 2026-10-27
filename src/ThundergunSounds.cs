using System.Collections.Generic;
using System.Linq;
using EFT;

namespace Thundergun
{
    /// <summary>
    /// The F12 "Shot" and "Reload and handling" sliders. The gun's sound player (in its bundle) carries each level the
    /// game reads as it plays: the shot banks' base volume and every foley event's volume. Each is scaled from its
    /// level as built, on every Thundergun model as it appears and again whenever a slider moves.
    /// </summary>
    internal static class ThundergunSounds
    {
        private static readonly HashSet<WeaponSoundPlayer> Players = new HashSet<WeaponSoundPlayer>();
        private static readonly Dictionary<SoundBank, float> BankLevels = new Dictionary<SoundBank, float>();
        private static readonly Dictionary<BaseSoundPlayer.SoundElement, float> ElementLevels = new Dictionary<BaseSoundPlayer.SoundElement, float>();

        public static void Enable()
        {
            ThundergunSettings.ShotVolume.SettingChanged += (_, __) => ApplyAll();
            ThundergunSettings.FoleyVolume.SettingChanged += (_, __) => ApplyAll();
        }

        /// <summary>A Thundergun model's sound player, at the current slider levels.</summary>
        public static void Register(WeaponSoundPlayer player)
        {
            if (player == null)
                return;
            Players.Add(player);
            Apply(player);
        }

        private static void ApplyAll()
        {
            Players.RemoveWhere(p => p == null);
            foreach (WeaponSoundPlayer player in Players)
                Apply(player);
        }

        private static void Apply(WeaponSoundPlayer player)
        {
            float shot = ThundergunSettings.ShotVolume.Value;
            foreach (SoundBank bank in new[] { player.Body, player.Tail, player.Doublet, player.BodySilenced, player.TailSilenced, player.DoubletSilenced })
            {
                if (bank == null)
                    continue;
                if (!BankLevels.TryGetValue(bank, out float built))
                    BankLevels[bank] = built = bank.BaseVolume;
                bank.BaseVolume = built * shot;
            }
            float foley = ThundergunSettings.FoleyVolume.Value;
            foreach (BaseSoundPlayer.SoundElement element in (player.AdditionalSounds ?? Enumerable.Empty<BaseSoundPlayer.SoundElement>())
                         .Concat(player.MainSounds ?? Enumerable.Empty<BaseSoundPlayer.SoundElement>()))
            {
                if (element == null)
                    continue;
                if (!ElementLevels.TryGetValue(element, out float built))
                    ElementLevels[element] = built = element.Volume;
                element.Volume = built * foley;
            }
        }
    }
}
