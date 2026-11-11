using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Communications;
using SPT.Common.Http;
using SPT.Reflection.Utils;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Thundergun
{
    /// <summary>
    /// Each player's choice: Thundergun in their normal gameplay, or only in the Kennerz Zombies mode. The server keeps the
    /// pick per profile (POST /thundergun/normalgame; it counts from the next server start). F12 "1. Normal gameplay" shows and changes
    /// it (turning it off asks first: it removes what the player got from it). Every Kennerz mod with such a choice
    /// signs up in one shared list (AppDomain data, plain .NET types only, so mods built apart still understand each
    /// other) and one of them asks about all of them in a single window at the main menu, whenever one hasn't been
    /// picked yet (a newly installed mod brings the window back, the others' picks already filled in).
    /// </summary>
    internal class ThundergunNormalGame : MonoBehaviour
    {
        private const string Route = "/thundergun/normalgame";
        private const string Name = "Thundergun";
        private const string What = "A chalk wall buy and an ammo box appear in your hideout's side room once its wall is knocked down. Buy the Thundergun and its cartridges with roubles and take it into your normal raids!";
        private const string Removes = "every Thundergun and pressure cartridge you own (stash, mail, insurance), and the roubles you spent on them.";
        private const string Gone = "<b>Thundergun:</b> turning it off in F12 or uninstalling it <b>removes every Thundergun and cartridge from your stash</b> (no refund).";
        private const int Order = 3;

        private static ConfigEntry<bool> _show;
        private static bool _quiet, _confirm, _known;
        private static volatile bool _asking;
        private static volatile int _now = -1, _next = -1, _chosen = -1;
        private static volatile string _notice;
        private static volatile bool _changed;
        private static float _nextAsk;
        private static int _fails;

        public static void Enable(ConfigFile config, GameObject host)
        {
            _show = config.Bind("1. Normal gameplay", "Show in normal gameplay", true, new ConfigDescription(
                "On: " + What + " Off: " + Name + " is only in the zombies mode (where it always works). Turning it off removes what you got " +
                "from it: " + Removes + " Counts after restarting the game and the server.",
                null, new ConfigurationManagerAttributes { Order = 1000 }));
            _show.SettingChanged += (_, __) => Changed();
            host.AddComponent<ThundergunNormalGame>();
            SignUp();
        }

        /// <summary>ConfigurationManager reads this by name (BepInEx's convention).</summary>
        private sealed class ConfigurationManagerAttributes
        {
            public int? Order;
        }

        // ---------- The shared list (every Kennerz mod with a normal-gameplay choice) ----------

        private const string ListKey = "Kennerz.NormalGame.Mods";
        // This window's code version: of all the copies signed up (each Kennerz mod carries one), the newest draws
        private const int WindowVersion = 4;

        private static void SignUp()
        {
            var list = AppDomain.CurrentDomain.GetData(ListKey) as List<Dictionary<string, object>>;
            if (list == null)
            {
                list = new List<Dictionary<string, object>>();
                AppDomain.CurrentDomain.SetData(ListKey, list);
            }
            list.RemoveAll(d => d.TryGetValue("route", out object r) && (string)r == Route);
            list.Add(new Dictionary<string, object>
            {
                ["route"] = Route,
                ["name"] = Name,
                ["what"] = What,
                ["gone"] = Gone,
                ["order"] = Order,
                ["version"] = WindowVersion,
                ["now"] = (Func<int>)(() => _now),
                ["next"] = (Func<int>)(() => _next),
                ["chosen"] = (Func<int>)(() => _chosen),
                ["set"] = (Action<bool>)(on => Ask(on)),
            });
        }

        private static List<Dictionary<string, object>> Mods() =>
            ((AppDomain.CurrentDomain.GetData(ListKey) as List<Dictionary<string, object>>) ?? new List<Dictionary<string, object>>())
            .OrderBy(d => (int)d["order"]).ToList();

        // ---------- This mod's pick ----------

        private static void Changed()
        {
            if (_quiet)
                return;
            if (_show.Value)
                Ask(true);
            else
                _confirm = true; // REMOVE IT / KEEP IT first
        }

        private static void Quietly(bool value)
        {
            _quiet = true;
            _show.Value = value;
            _quiet = false;
        }

        private static void Ask(bool? set)
        {
            if (_asking && set == null)
                return;
            _asking = true;
            string body = set == null ? "{}" : set.Value ? "{\"set\":true}" : "{\"set\":false}";
            Task.Run(() =>
            {
                try
                {
                    string reply = RequestHandler.PostJson(Route, body) ?? "";
                    Match now = Regex.Match(reply, "\"now\"\\s*:\\s*(true|false)");
                    Match next = Regex.Match(reply, "\"next\"\\s*:\\s*(true|false)");
                    Match chosen = Regex.Match(reply, "\"chosen\"\\s*:\\s*(true|false)");
                    if (!next.Success || !chosen.Success)
                        throw new Exception("no reply");
                    _now = now.Success && now.Groups[1].Value == "true" ? 1 : 0;
                    _next = next.Groups[1].Value == "true" ? 1 : 0;
                    _chosen = chosen.Groups[1].Value == "true" ? 1 : 0;
                    _changed = true;
                    if (set != null)
                        _notice = Name + (set.Value ? " will be in your normal gameplay" : " will be removed from your normal gameplay") + RestartLine;
                }
                catch
                {
                    if (set != null)
                        _notice = "Couldn't reach the server to change " + Name + ". Try again in a moment.";
                    else if (++_fails >= 3)
                        _known = true; // an older server without the choice: nothing to ask
                }
                finally
                {
                    _asking = false;
                }
            });
        }

        private static bool Fika => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core");

        private static string RestartLine => Fika
            ? " once the game and the server have restarted (co-op: the host restarts the server)."
            : " after you restart the game and the server.";

        private static bool AtMainMenu =>
            !Singleton<GameWorld>.Instantiated && ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile != null;

        private void Update()
        {
            if (!_known && !_asking && Time.unscaledTime >= _nextAsk && AtMainMenu)
            {
                _nextAsk = Time.unscaledTime + 5f;
                Ask(null);
            }
            if (_changed)
            {
                _changed = false;
                _known = true;
                if (!_confirm && _next >= 0)
                    Quietly(_next == 1);
            }
            if (_notice != null)
            {
                string n = _notice;
                _notice = null;
                try
                {
                    NotificationManager.DisplayMessageNotification(n);
                }
                catch
                {
                    Debug.Log(n);
                }
            }
            // One mod draws the shared window: the newest copy of it (ties: the first in the list's order)
            if (_drawer == null && Owner())
                _drawer = this;
            if (_drawer == this)
                Prompt.Tick();
            Blocker.Hold(_confirm || _drawer == this && Prompt.Showing);
        }

        private static ThundergunNormalGame _drawer;

        private static bool Owner()
        {
            List<Dictionary<string, object>> mods = Mods();
            int newest = mods.Max(m => m.TryGetValue("version", out object v) ? (int)v : 0);
            Dictionary<string, object> owner = mods.First(m => (m.TryGetValue("version", out object v) ? (int)v : 0) == newest);
            return (string)owner["route"] == Route;
        }

        private void OnGUI()
        {
            bool up = _confirm || _drawer == this && Prompt.Showing;
            if (!up)
                return;
            // On top of every other window (the zombies party panels too), and nothing under it takes a click or key
            GUI.depth = -1000;
            if (_drawer == this)
                Prompt.Draw();
            if (_confirm)
                DrawConfirm();
            Event e = Event.current;
            if (e != null && (e.isMouse || e.isKey || e.type == EventType.ScrollWheel))
                e.Use();
        }

        // ---------- The F12 "remove it?" window ----------

        private static void DrawConfirm()
        {
            Look.Make();
            float s = Look.S;
            Look.Dim();
            string text = "<color=#ffd24a><b>GOOD TO KNOW</b></color>\n" +
                "• " + Gone + "\n" +
                (Look.Zombies ? "• <b>The zombies mode keeps it</b>: it always works there.\n" : "") +
                (Fika ? "• <b>Co-op (Fika):</b> this only changes <b>your own</b> game and profile, not your friends'. It counts once the host restarts the server.\n"
                      : "• <b>It counts after you restart the game and the server.</b>\n") +
                "• Turning it back on later starts it all fresh.";
            float w0 = 760f * s - 64f * s;
            float noteH = Look.NoteHeight(text, w0, 260f * s);
            float h = 26f * s + 56f * s + 120f * s + noteH + 100f * s;
            var rect = new Rect((Screen.width - 760f * s) / 2f, Mathf.Max(20f * s, (Screen.height - h) / 2f), 760f * s, h);
            Look.Panel(rect);
            float x = rect.x + 32f * s, w = w0, y = rect.y + 26f * s;
            GUI.Label(new Rect(x, y, w, 40f * s), "REMOVE " + Name.ToUpperInvariant() + " FROM YOUR NORMAL GAMEPLAY?", Look.Title);
            y += 56f * s;
            GUI.Label(new Rect(x, y, w, 110f * s), "This removes what you got from it: <b>" + Removes + "</b>", Look.Text);
            y += 120f * s;
            Look.ScrollNote(new Rect(x, y, w, noteH), text, ref _confirmScroll);
            if (GUI.Button(new Rect(x, rect.yMax - 72f * s, w / 2f - 10f * s, 48f * s), "REMOVE IT", Look.Danger))
            {
                _confirm = false;
                Ask(false);
            }
            if (GUI.Button(new Rect(x + w / 2f + 10f * s, rect.yMax - 72f * s, w / 2f - 10f * s, 48f * s), "KEEP IT", Look.Button))
            {
                _confirm = false;
                Quietly(true);
            }
        }

        private static Vector2 _confirmScroll;

        // ---------- The shared first-launch window (drawn by one mod for all) ----------

        private static class Prompt
        {
            private static bool _done, _open;
            private static float _menuSince = -1f;
            private static readonly Dictionary<string, bool> Picks = new Dictionary<string, bool>();
            private static HashSet<string> _seen = new HashSet<string>();

            public static bool Showing => _open;

            // Players asked with the zombies mode installed (BepInEx\config\Kennerz.NormalGame.txt, one profile id a line;
            // shared by every Kennerz mod): asked once more when the zombies mode arrives after their first answer
            private static string AskedFile => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "Kennerz.NormalGame.txt");

            private static string ProfileId => ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.Id.ToString();

            private static bool ZombiesAsked()
            {
                try
                {
                    string id = ProfileId;
                    return id == null || System.IO.File.Exists(AskedFile) && System.IO.File.ReadAllLines(AskedFile).Contains(id);
                }
                catch
                {
                    return true;
                }
            }

            private static void MarkZombiesAsked()
            {
                try
                {
                    string id = ProfileId;
                    if (id != null && !ZombiesAsked())
                        System.IO.File.AppendAllText(AskedFile, id + Environment.NewLine);
                }
                catch
                {
                    // asked again next time, nothing worse
                }
            }

            public static void Tick()
            {
                if (!AtMainMenu)
                {
                    _menuSince = -1f;
                    return;
                }
                if (_menuSince < 0f)
                    _menuSince = Time.unscaledTime;
                List<Dictionary<string, object>> mods = Mods();
                // A mod signed up since the last answer: ask again
                if (_done && mods.Any(m => !_seen.Contains((string)m["route"]) && ((Func<int>)m["chosen"])() == 0))
                    _done = false;
                if (_done || _open)
                    return;
                bool allKnown = mods.All(m => ((Func<int>)m["chosen"])() >= 0);
                if (!allKnown && Time.unscaledTime - _menuSince < 20f)
                    return;
                var known = mods.Where(m => ((Func<int>)m["chosen"])() >= 0).ToList();
                // Everything picked: done, unless the zombies mode has arrived since (asked once more, picks filled in)
                if (known.Count == 0 || known.All(m => ((Func<int>)m["chosen"])() == 1) && (!Look.Zombies || ZombiesAsked()))
                {
                    if (allKnown)
                    {
                        _done = true;
                        _seen = new HashSet<string>(mods.Select(m => (string)m["route"]));
                    }
                    return;
                }
                Picks.Clear();
                // Picked before: as picked. New: on without the zombies mode (the mod was got for normal gameplay),
                // off with it (zombies is the likely reason it's here)
                foreach (var m in known)
                    Picks[(string)m["route"]] = ((Func<int>)m["chosen"])() == 1 ? ((Func<int>)m["next"])() == 1 : !Look.Zombies;
                _open = true;
            }

            private static Vector2 _listScroll, _noteScroll;

            private static string NoteText(List<Dictionary<string, object>> mods) =>
                "<color=#ffd24a><b>GOOD TO KNOW</b></color>\n" +
                "• <b>Change it any time:</b> F12, each mod's <b>1. Normal gameplay</b>.\n" +
                string.Concat(mods.Where(m => m.ContainsKey("gone")).Select(m => "• " + (string)m["gone"] + "\n")) +
                "• Turning one on again later starts it fresh.\n" +
                (Fika ? "• <b>Co-op (Fika):</b> everyone picks for themselves. Yours only changes <b>your own</b> game. Changes count once the host restarts the server."
                      : "• <b>Changes count after you restart the game and the server.</b>");

            public static void Draw()
            {
                if (!_open || !AtMainMenu)
                    return;
                Look.Make();
                float s = Look.S;
                var mods = Mods().Where(m => Picks.ContainsKey((string)m["route"])).ToList();
                Look.Dim();
                float width = 960f * s, w = width - 68f * s;
                string note = NoteText(mods);
                float rowH = 112f * s;
                float noteH = Look.NoteHeight(note, w, 220f * s);
                float fixedH = 26f * s + 50f * s + 100f * s + 14f * s + noteH + 96f * s;
                float listH = mods.Count * rowH;
                float listBox = Mathf.Max(rowH, Mathf.Min(listH, Screen.height - 40f * s - fixedH));
                float h = fixedH + listBox;
                var rect = new Rect((Screen.width - width) / 2f, Mathf.Max(20f * s, (Screen.height - h) / 2f), width, h);
                Look.Panel(rect);
                float x = rect.x + 34f * s, y = rect.y + 26f * s;
                GUI.Label(new Rect(x, y, w, 40f * s), "YOUR NORMAL GAMEPLAY", Look.Title);
                y += 50f * s;
                GUI.Label(new Rect(x, y, w, 90f * s), (Look.Zombies
                    ? "These mods are part of the <b>Dr K's SPZombies</b> mode and <b>always work there</b>. "
                    + "Do you also want them in your <b>normal gameplay</b>? Not sure? Leave them <b>off</b>: your normal game stays exactly as it was."
                    : "Do you want these in your <b>normal gameplay</b>? Pick <b>off</b> to keep your normal game exactly as it was."), Look.Text);
                y += 100f * s;
                bool scrolls = listH > listBox + 1f;
                float rw = scrolls ? w - 22f * s : w;
                Look.PushBars();
                _listScroll = GUI.BeginScrollView(new Rect(x, y, w, listBox), _listScroll, new Rect(0f, 0f, rw, listH), false, false);
                float ry = 0f;
                foreach (var m in mods)
                {
                    string route = (string)m["route"];
                    bool isNew = ((Func<int>)m["chosen"])() == 0;
                    GUI.Label(new Rect(0f, ry, rw * 0.6f, 34f * s), "<b>" + (string)m["name"] + "</b>" + (isNew ? "  <color=#ffd24a><size=" + Mathf.RoundToInt(15 * s) + ">NEW</size></color>" : ""), Look.Heading);
                    bool on = Picks[route];
                    if (GUI.Button(new Rect(rw - 330f * s, ry, 150f * s, 38f * s), "OFF", on ? Look.Unpicked : Look.Picked))
                        Picks[route] = false;
                    if (GUI.Button(new Rect(rw - 170f * s, ry, 170f * s, 38f * s), "ON", on ? Look.Picked : Look.Unpicked))
                        Picks[route] = true;
                    GUI.Label(new Rect(0f, ry + 42f * s, rw, rowH - 46f * s), (string)m["what"], Look.Small);
                    ry += rowH;
                }
                GUI.EndScrollView();
                Look.PopBars();
                y += listBox + 14f * s;
                Look.ScrollNote(new Rect(x, y, w, noteH), note, ref _noteScroll);
                if (GUI.Button(new Rect(rect.x + rect.width / 2f - 150f * s, rect.yMax - 70f * s, 300f * s, 50f * s), "CONFIRM", Look.Button))
                    Confirm(mods);
            }

            private static void Confirm(List<Dictionary<string, object>> mods)
            {
                bool restart = false;
                foreach (var m in mods)
                {
                    bool on = Picks[(string)m["route"]];
                    restart |= ((Func<int>)m["now"])() != (on ? 1 : 0);
                    ((Action<bool>)m["set"])(on);
                }
                _open = false;
                _done = true;
                _seen = new HashSet<string>(Mods().Select(m => (string)m["route"]));
                if (Look.Zombies)
                    MarkZombiesAsked();
                _notice = restart ? "Saved. It counts" + RestartLine : "Saved. Change it any time in F12, under each mod's \"1. Normal gameplay\".";
            }
        }

        // ---------- Blocking the menus underneath while a window is up ----------

        private static class Blocker
        {
            private static EventSystem _off;

            /// <summary>While true, the game's own menu buttons can't be clicked (only our window).</summary>
            public static void Hold(bool block)
            {
                if (block)
                {
                    EventSystem current = EventSystem.current;
                    if (current != null && current.enabled && _off == null)
                    {
                        _off = current;
                        current.enabled = false;
                    }
                }
                else if (_off != null)
                {
                    _off.enabled = true;
                    _off = null;
                }
            }
        }

        // ---------- Looks ----------

        private static class Look
        {
            public static float S;
            public static GUIStyle Title, Heading, Text, Small, Button, Danger, Picked, Unpicked;
            private static Texture2D _px;

            public static bool Zombies => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.kennerz.zmcore");

            public static void Make()
            {
                S = Screen.height / 1080f;
                if (Text != null && Text.fontSize == Mathf.RoundToInt(20 * S))
                    return;
                _px = Texture2D.whiteTexture;
                Color white = Color.white;
                Title = Label(28, FontStyle.Bold, new Color(0.9f, 0.12f, 0.12f));
                Heading = Label(24, FontStyle.Normal, white);
                Text = Label(20, FontStyle.Normal, white);
                Small = Label(17, FontStyle.Normal, new Color(0.88f, 0.88f, 0.88f));
                Button = Btn(new Color(0.16f, 0.16f, 0.16f), white, 21);
                Danger = Btn(new Color(0.55f, 0.06f, 0.06f), white, 21);
                Picked = Btn(new Color(0.08f, 0.42f, 0.12f), white, 19);
                Unpicked = Btn(new Color(0.12f, 0.12f, 0.12f), new Color(0.55f, 0.55f, 0.55f), 19);
            }

            private static GUIStyle Label(int size, FontStyle style, Color colour) => new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(size * S), fontStyle = style, wordWrap = true, richText = true, normal = { textColor = colour },
            };

            private static GUIStyle Btn(Color back, Color text, int size)
            {
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, back);
                tex.Apply();
                var hover = new Texture2D(1, 1);
                hover.SetPixel(0, 0, back * 1.35f);
                hover.Apply();
                return new GUIStyle(GUI.skin.button)
                {
                    fontSize = Mathf.RoundToInt(size * S), fontStyle = FontStyle.Bold, richText = true,
                    normal = { background = tex, textColor = text }, hover = { background = hover, textColor = Color.white },
                    active = { background = hover, textColor = Color.white },
                };
            }

            /// <summary>Darkens everything behind the window.</summary>
            public static void Dim() => Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.82f));

            /// <summary>The window: solid, with a red top edge.</summary>
            public static void Panel(Rect r)
            {
                Fill(r, new Color(0.055f, 0.055f, 0.06f, 1f));
                Fill(new Rect(r.x, r.y, r.width, 4f * S), new Color(0.8f, 0.08f, 0.08f, 1f));
            }

            /// <summary>The note's height for its text, at most <paramref name="max"/> (then it scrolls).</summary>
            public static float NoteHeight(string text, float width, float max) =>
                Mathf.Min(Small.CalcHeight(new GUIContent(text), width - 32f * S) + 24f * S, max);

            /// <summary>The "good to know" box with its text, a red scrollbar on the right when it doesn't all fit.</summary>
            public static void ScrollNote(Rect r, string text, ref Vector2 scroll)
            {
                Note(r);
                float inner = r.width - 32f * S;
                float full = Small.CalcHeight(new GUIContent(text), inner);
                Rect view = new Rect(r.x + 16f * S, r.y + 12f * S, r.width - 24f * S, r.height - 24f * S);
                if (full <= view.height + 1f)
                {
                    GUI.Label(new Rect(view.x, view.y, inner, full), text, Small);
                    return;
                }
                PushBars();
                float textW = view.width - 22f * S;
                float textH = Small.CalcHeight(new GUIContent(text), textW);
                scroll = GUI.BeginScrollView(view, scroll, new Rect(0f, 0f, textW, textH), false, false);
                GUI.Label(new Rect(0f, 0f, textW, textH), text, Small);
                GUI.EndScrollView();
                PopBars();
            }

            private static Texture2D _barBack, _barThumb;
            private static Texture2D _oldBack, _oldThumb;
            private static float _oldWidth, _oldThumbWidth;

            /// <summary>Red scrollbars for our windows (the skin's own, put back straight after).</summary>
            public static void PushBars()
            {
                _barBack = _barBack ?? Solid(new Color(0.16f, 0.03f, 0.03f));
                _barThumb = _barThumb ?? Solid(new Color(0.85f, 0.1f, 0.1f));
                _oldBack = GUI.skin.verticalScrollbar.normal.background;
                _oldThumb = GUI.skin.verticalScrollbarThumb.normal.background;
                _oldWidth = GUI.skin.verticalScrollbar.fixedWidth;
                _oldThumbWidth = GUI.skin.verticalScrollbarThumb.fixedWidth;
                GUI.skin.verticalScrollbar.normal.background = _barBack;
                GUI.skin.verticalScrollbarThumb.normal.background = _barThumb;
                GUI.skin.verticalScrollbar.fixedWidth = 8f * S;
                GUI.skin.verticalScrollbarThumb.fixedWidth = 8f * S;
            }

            public static void PopBars()
            {
                GUI.skin.verticalScrollbar.normal.background = _oldBack;
                GUI.skin.verticalScrollbarThumb.normal.background = _oldThumb;
                GUI.skin.verticalScrollbar.fixedWidth = _oldWidth;
                GUI.skin.verticalScrollbarThumb.fixedWidth = _oldThumbWidth;
            }

            private static Texture2D Solid(Color c)
            {
                var t = new Texture2D(1, 1);
                t.SetPixel(0, 0, c);
                t.Apply();
                return t;
            }

            /// <summary>The "good to know" box: solid, a yellow edge on its left.</summary>
            public static void Note(Rect r)
            {
                Fill(r, new Color(0.13f, 0.11f, 0.05f, 1f));
                Fill(new Rect(r.x, r.y, 5f * S, r.height), new Color(1f, 0.8f, 0.25f, 1f));
            }

            private static void Fill(Rect r, Color c)
            {
                Color before = GUI.color;
                GUI.color = c;
                GUI.DrawTexture(r, _px ?? Texture2D.whiteTexture);
                GUI.color = before;
            }
        }
    }
}
