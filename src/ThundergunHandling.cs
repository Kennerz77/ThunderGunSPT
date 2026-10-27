using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The Thundergun is a heavy, awkward thing to carry: while it's in the hands the player moves slower, by the F12
    /// "Movement penalty". It goes through the game's own speed limits, under a cause of its own (the game keeps one
    /// per cause, so armour and weight limits are left alone), and the stats panel lists it like armour's.
    /// </summary>
    internal static class ThundergunHandling
    {
        // A speed-limit cause and a mouse modifier the game doesn't use: both are kept in dictionaries per cause
        private const Player.ESpeedLimit Cause = (Player.ESpeedLimit)935;
        private const Player.EMouseSensitivityModifier TurnCause = (Player.EMouseSensitivityModifier)935;
        private const string RowName = "MOVEMENT SPEED";
        private const string TurnRowName = "TURN SPEED";

        private static Player _limited;
        private static float _applied = -1f;
        private static Player _turnLimited;
        private static float _turnApplied = -1f;

        public static void Enable(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Show)),
                new HarmonyMethod(typeof(ThundergunHandling).GetMethod(nameof(SpecPanelPrefix), BindingFlags.Static | BindingFlags.NonPublic)));

        /// <summary>Twice a second (from the tracker): the limit on while the Thundergun is in the hands, off otherwise.</summary>
        public static void Update(Player player)
        {
            bool holding = player != null && player.HandsController?.Item is Weapon held && ThundergunIds.Is(held);
            UpdateTurn(player, holding);
            float penalty = Mathf.Clamp(ThundergunSettings.MovementPenalty.Value, 0f, 90f);
            if (_limited != null && (_limited != player || !holding || penalty != _applied))
            {
                if (_limited.MovementContext != null)
                    _limited.RemoveStateSpeedLimit(Cause);
                _limited = null;
                _applied = -1f;
            }
            if (!holding || penalty <= 0f || _limited != null || player.MovementContext == null)
                return;
            // Like armour's: a cap on speed, as a share of the player's top speed
            player.AddStateSpeedLimit((100f - penalty) / 100f * player.MovementContext.MaxSpeed, Cause);
            _limited = player;
            _applied = penalty;
        }

        /// <summary>Slower turning, as heavy armour's mouse penalty: a negative share off the look sensitivity.</summary>
        private static void UpdateTurn(Player player, bool holding)
        {
            float penalty = Mathf.Clamp(ThundergunSettings.TurnPenalty.Value, 0f, 90f);
            if (_turnLimited != null && (_turnLimited != player || !holding || penalty != _turnApplied))
            {
                _turnLimited.RemoveMouseSensitivityModifier(TurnCause);
                _turnLimited = null;
                _turnApplied = -1f;
            }
            if (!holding || penalty <= 0f || _turnLimited != null)
                return;
            player.AddMouseSensitivityModifier(TurnCause, -penalty / 100f);
            _turnLimited = player;
            _turnApplied = penalty;
        }

        // The penalties shown in the gun's stats, kept in step with the settings
        private static void SpecPanelPrefix(ItemContext itemContext)
        {
            try
            {
                if (!(itemContext?.Item is Weapon weapon) || !ThundergunIds.Is(weapon) || weapon.Attributes == null)
                    return;
                AddRow(weapon, RowName, () => ThundergunSettings.MovementPenalty.Value);
                AddRow(weapon, TurnRowName, () => ThundergunSettings.TurnPenalty.Value);
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Thundergun stats row skipped: {e.Message}");
            }
        }

        private static void AddRow(Weapon weapon, string name, System.Func<float> percent)
        {
            if (weapon.Attributes.Any(a => a.Name == name))
                return;
            weapon.Attributes.Add(new ItemAttribute(EItemAttributeId.Undefined)
            {
                Name = name,
                DisplayNameFunc = () => name,
                Base = () => -percent(),
                StringValue = () => $"<color=#c94a4a>-{Mathf.RoundToInt(percent())}%</color> while held",
                DisplayType = () => EItemAttributeDisplayType.Compact,
            });
        }
    }
}
