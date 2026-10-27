using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The Pressure Cartridge as the player sees it. Its charges are all-or-nothing (every one must be fired before
    /// the gun lets it go), so the inventory shows FULL or EMPTY instead of a count, and its model's two bulbs glow
    /// while it's full and go dark once it's spent: in the inventory icon, the inspect view and on the floor.
    /// </summary>
    internal static class ThundergunCartridge
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GridItemView), nameof(GridItemView.UpdateItemValue)),
                new HarmonyMethod(Method(nameof(ValuePrefix))));
            harmony.Patch(AccessTools.Method(typeof(IconsHash), nameof(IconsHash.GetItemHash)),
                postfix: new HarmonyMethod(Method(nameof(IconHashPostfix))));

            // Every way the game builds an item model (icons, the inspect window, loot, the hands)
            harmony.Patch(AccessTools.FirstMethod(typeof(ObjectsFactory), m =>
                    m.Name == nameof(ObjectsFactory.CreateItemAsync) && m.ReturnType == typeof(Task<GameObject>)
                    && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Item)),
                postfix: new HarmonyMethod(Method(nameof(CreateAsyncPostfix))));
            foreach (MethodInfo create in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m =>
                         (m.Name == nameof(ObjectsFactory.CreateItem) || m.Name == nameof(ObjectsFactory.CreateCleanLootPrefab)
                          || m.Name == nameof(ObjectsFactory.CreateLootPrefab) || m.Name == nameof(ObjectsFactory.CreateItemUsablePrefab))
                         && m.ReturnType == typeof(GameObject)
                         && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Item)))
                harmony.Patch(create, postfix: new HarmonyMethod(Method(nameof(CreatePostfix))));
        }

        private static MethodInfo Method(string name) => typeof(ThundergunCartridge).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static bool Full(Item item) => item is Magazine m && m.Count > 0;

        // ---------- Inventory cell ----------

        private const string FullText = "<color=#dadabc>FULL</color>";
        private const string EmptyText = "<color=red>EMPTY</color>";

        private static void ValuePrefix(GridItemView __instance, ref string newValue)
        {
            Item item = __instance.Item;
            if (ThundergunIds.IsCartridge(item))
                newValue = Full(item) ? FullText : EmptyText;
            // The gun: charged while anything is left in it
            else if (ThundergunIds.Is(item) && item is Weapon weapon && weapon.GetCurrentMagazine() != null)
                newValue = ThundergunCharges.Count(weapon) > 0 ? "<color=#dadabc>CHARGED</color>" : EmptyText;
        }

        // Our own pictures (ThundergunIcons) under their own hash; a full and a spent cartridge are different ones
        private static void IconHashPostfix(Item item, ref int __result)
        {
            if (ThundergunIds.IsCartridge(item))
                __result = ThundergunIcons.Salted(__result * 31 + (Full(item) ? 0x46554C4C : 0x454D5054)); // "FULL" / "EMPT"
            else if (ThundergunIds.Is(item))
                __result = ThundergunIcons.Salted(__result);
        }

        // ---------- Model ----------

        private static void CreatePostfix(GameObject __result, Item item)
        {
            if (__result != null)
                Dress(__result, item);
        }

        private static void CreateAsyncPostfix(ref Task<GameObject> __result, Item item)
        {
            if (ThundergunIds.IsCartridge(item) || ThundergunIds.Is(item))
                __result = Dressed(__result, item);
        }

        private static async Task<GameObject> Dressed(Task<GameObject> task, Item item)
        {
            GameObject go = await task;
            if (go != null)
                Dress(go, item);
            return go;
        }

        /// <summary>
        /// A cartridge model gets its lights set; a Thundergun model its view (the ammo lights). A charge's model (the
        /// buckshot shell it's cloned from: in the chamber, at the shell port as the gun "ejects" it) is hidden.
        /// </summary>
        private static void Dress(GameObject go, Item item)
        {
            try
            {
                if (ThundergunIds.IsCartridge(item))
                    (go.GetComponent<CartridgeLights>() ?? go.AddComponent<CartridgeLights>()).Show(Full(item));
                else if (ThundergunIds.Is(item))
                {
                    ThundergunTracker.Attach(go);
                    // A pooled model already had its view: rewrite its lights for this gun
                    go.GetComponentInChildren<ThundergunView>(true)?.Refresh();
                }
                HiddenCharge.Set(go, ThundergunLock.IsCharge(item));
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Pressure Cartridge lights skipped: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Marks a charge's model as hidden. Models are pooled by prefab, and the charge shares the vanilla buckshot
    /// shell's, so a model built for a real shell afterwards gets its renderers back.
    /// </summary>
    internal class HiddenCharge : MonoBehaviour
    {
        public static void Set(GameObject go, bool hide)
        {
            HiddenCharge marker = go.GetComponent<HiddenCharge>();
            if (hide == (marker != null))
                return;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                r.forceRenderingOff = hide;
            if (hide)
                go.AddComponent<HiddenCharge>();
            else
                Destroy(marker);
        }
    }

    /// <summary>A cartridge model's own copies of its four bulb materials, two lit when it's full.</summary>
    internal class CartridgeLights : MonoBehaviour
    {
        // The plain gun's two charges sit on the middle bulbs, as on the gun
        private static readonly int[] Lit = { 1, 2 };
        private static readonly Color On = Color.white * 1.2f;

        private Material[] _bulbs;

        public void Show(bool full)
        {
            if (_bulbs == null)
            {
                _bulbs = new Material[4];
                foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        for (int n = 1; n <= 4 && mats[i] != null; n++)
                        {
                            if (mats[i].name.StartsWith($"tg_light_{n}"))
                            {
                                mats[i] = _bulbs[n - 1] = new Material(mats[i]);
                                changed = true;
                            }
                        }
                    }
                    if (changed)
                        r.sharedMaterials = mats;
                }
            }
            // Full: the charge bulbs lit, the outer two idle (a Zeus Cannon's extra pair). Spent: dark all round, as
            // it left the gun
            for (int n = 0; n < 4; n++)
            {
                if (_bulbs[n] != null)
                    Bulb.Show(_bulbs[n], spent: !full, full && Lit.Contains(n) ? On : Color.black);
            }
        }
    }
}
