using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Guest-side menu (F6) for the dogs the host has given you. Everything runs on the host;
    // this window only shows what the host reports and sends the buttons back as requests.
    public class GuestDogMenu : MonoBehaviour
    {
        public GuestDogMenu(IntPtr ptr) : base(ptr) { }

        internal class SkillInfo { public int Id, Level, Max, ReqAffection; }
        internal class DogInfo
        {
            public string Handle, Name; public int Breed, Level, Affection, Points, Combat;
            public bool Stay, PetReady, Unconscious;
            public List<SkillInfo> Skills = new List<SkillInfo>();
        }

        static List<DogInfo> _dogs = new List<DogInfo>();
        static readonly Dictionary<string, DogInfo> _incoming = new Dictionary<string, DogInfo>();
        static int _incomingSeq = -1;
        static bool _gotList;
        static string _status = "";
        static float _statusUntil;

        static bool _open;
        static int _selected;
        static string _renameText = "";
        static string _renameFor;
        static bool _confirmRelease;
        static float _nextAutoRefresh;
        static Rect _rect = new Rect(40, 120, 380, 10);

        static readonly Il2CppReferenceArray<GUILayoutOption> NoOpt = new Il2CppReferenceArray<GUILayoutOption>(0);

        static string MyId()
        {
            try { return DogShareBehaviour.IdOf(DogShareBehaviour.HostPlayer()); } catch { return null; }
        }

        static bool IsGuest()
        {
            try { return NetworkManager.isOnline && NetworkManager.isClient; } catch { return false; }
        }

        static void Send(params object[] parts)
        {
            try { NetChatSync.instance.SendChat(Proto.Join(parts)); }
            catch (Exception e) { Plugin.L.LogError("send: " + e.Message); }
        }

        static void Status(string s) { _status = s; _statusUntil = Time.unscaledTime + 6f; }

        void Update()
        {
            try
            {
                if (!IsGuest()) { if (_open) _open = false; return; }
                if (DogShareBehaviour.Pressed(Plugin.MenuKey.Value))
                {
                    _open = !_open;
                    _confirmRelease = false;
                    if (_open) { _gotList = false; Send("L"); _nextAutoRefresh = Time.unscaledTime + 5f; DiagOnce(); }
                }
                if (_open && Time.unscaledTime >= _nextAutoRefresh) { _nextAutoRefresh = Time.unscaledTime + 5f; Send("L"); }
            }
            catch (Exception e) { Plugin.L.LogError("menu update: " + e.Message); }
        }

        // One-time log of what the dogs look like on a guest's PC (for making the game's own
        // dog prompts work for guests in a later version).
        static bool _diagDone;
        static void DiagOnce()
        {
            if (_diagDone) return;
            _diagDone = true;
            try
            {
                var me = DogShareBehaviour.HostPlayer();
                Plugin.L.LogInfo($"[guest-diag] myId={MyId()} pos={(me == null ? "?" : DogShareBehaviour.Pos(me).ToString())}");
                int n = 0;
                foreach (var pc in UnityEngine.Object.FindObjectsOfType<PetCharacterController>())
                {
                    if (pc == null || n++ > 8) continue;
                    string comps = "";
                    try { foreach (var c in pc.gameObject.GetComponents<Component>()) comps += c.GetIl2CppType().Name + ","; } catch { }
                    GOAPNPCData nd = null; try { nd = pc.npcData; } catch { }
                    PetData pd = null; try { pd = pc.petData; } catch { }
                    GOAPAgent_Pet ag = null; try { ag = pc.petAgent; } catch { }
                    string cid = null; try { cid = pc.characterData?.characterId; } catch { }
                    Plugin.L.LogInfo($"[guest-diag] pet '{pc.gameObject.name}' puppet={pc.isNetPuppet} npcData={(nd == null ? "null" : nd.npcGUID + " leader=" + nd.leaderCharacterGUID)} " +
                                     $"petData={(pd == null ? "null" : "breed " + pd.breedId)} agent={(ag != null)} charId={cid} " +
                                     $"dist={(me == null ? -1 : (DogShareBehaviour.Pos(pc) - DogShareBehaviour.Pos(me)).magnitude):0.0} comps={comps}");
                }
                if (n == 0) Plugin.L.LogInfo("[guest-diag] no PetCharacterController objects on this PC");
                try
                {
                    var mine = PetSystem.GetPlayerPetDatas();
                    Plugin.L.LogInfo($"[guest-diag] PetSystem.GetPlayerPetDatas() = {(mine == null ? "null" : mine.Count.ToString())}");
                }
                catch (Exception e) { Plugin.L.LogInfo("[guest-diag] GetPlayerPetDatas threw: " + e.Message); }
            }
            catch (Exception e) { Plugin.L.LogWarning("[guest-diag] failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- host replies
        internal static void OnHostMessage(string text)
        {
            var p = Proto.Split(text);
            if (p.Length < 2) return;
            var me = Proto.Short(MyId());
            if (string.IsNullOrEmpty(me) || p[1] != me) return;   // for another guest
            switch (p[0])
            {
                case "M":
                    if (p.Length > 2) Status(p[2]);
                    break;
                case "D":
                {
                    if (p.Length < 15) return;
                    int seq = I(p[2]);
                    if (seq != _incomingSeq) { _incoming.Clear(); _incomingSeq = seq; }
                    var d = new DogInfo
                    {
                        Handle = p[4], Name = p[5], Breed = I(p[6]), Level = I(p[7]), Affection = I(p[8]),
                        Points = I(p[9]), Stay = p[10] == "1", Combat = I(p[11]), PetReady = p[12] == "1", Unconscious = p[13] == "1"
                    };
                    _incoming[d.Handle] = d;
                    break;
                }
                case "K":
                {
                    if (p.Length < 5 || I(p[2]) != _incomingSeq) return;
                    if (!_incoming.TryGetValue(p[3], out var d)) return;
                    d.Skills.Clear();
                    foreach (var part in p[4].Split(';'))
                    {
                        var f = part.Split(':');
                        if (f.Length < 4) continue;
                        d.Skills.Add(new SkillInfo { Id = I(f[0]), Level = I(f[1]), Max = I(f[2]), ReqAffection = I(f[3]) });
                    }
                    break;
                }
                case "E":
                {
                    if (I(p[2]) != _incomingSeq && _incoming.Count > 0) return;
                    var list = new List<DogInfo>(_incoming.Values);
                    list.Sort((a, b) => string.CompareOrdinal(a.Handle, b.Handle));
                    _dogs = list;
                    _incoming.Clear();
                    _incomingSeq = -1;
                    _gotList = true;
                    if (_selected >= _dogs.Count) _selected = Math.Max(0, _dogs.Count - 1);
                    break;
                }
            }
        }

        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        // ---------------------------------------------------------------- drawing
        void OnGUI()
        {
            if (!_open) return;
            try { Draw(); }
            catch (Exception e) { Plugin.L.LogError("menu draw: " + e.Message); _open = false; }
        }

        static string Breed(int id)
        {
            try { var n = PetSystem.GetBreedName(id); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "Dog";
        }

        static string SkillName(int id)
        {
            try { var a = PetSkillAttrManager.instance?.Get(id); var n = a?.SkillName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "Skill " + id;
        }

        static bool Button(string label, bool enabled = true)
        {
            bool old = GUI.enabled; GUI.enabled = enabled;
            bool r = GUILayout.Button(label, NoOpt);
            GUI.enabled = old;
            return r;
        }

        static void Draw()
        {
            GUILayout.BeginArea(new Rect(_rect.x, _rect.y, _rect.width, Screen.height - _rect.y - 20), GUI.skin.box);
            GUILayout.BeginHorizontal(NoOpt);
            GUILayout.Label("<b>DogShare - your dogs</b>", NoOpt);
            GUILayout.FlexibleSpace();
            if (Button("Refresh")) { Send("L"); Status("Asking the host..."); }
            if (Button("Close")) _open = false;
            GUILayout.EndHorizontal();

            if (!_gotList)
                GUILayout.Label("Asking the host... (the host needs DogShare too)", NoOpt);
            else if (_dogs.Count == 0)
                GUILayout.Label("You don't have any dogs yet. The host can give you one with F7 when you're standing next to them.", NoOpt);
            else
            {
                if (_dogs.Count > 1)
                {
                    GUILayout.BeginHorizontal(NoOpt);
                    for (int i = 0; i < _dogs.Count; i++)
                    {
                        var nm = string.IsNullOrEmpty(_dogs[i].Name) ? Breed(_dogs[i].Breed) : _dogs[i].Name;
                        if (Button(i == _selected ? "[" + nm + "]" : nm)) { _selected = i; _confirmRelease = false; }
                    }
                    GUILayout.EndHorizontal();
                }
                DrawDog(_dogs[Math.Min(_selected, _dogs.Count - 1)]);
            }

            if (!string.IsNullOrEmpty(_status) && Time.unscaledTime < _statusUntil)
                GUILayout.Label(_status, NoOpt);
            GUILayout.EndArea();
        }

        static void DrawDog(DogInfo d)
        {
            string name = string.IsNullOrEmpty(d.Name) ? Breed(d.Breed) : d.Name;
            GUILayout.Label($"<b>{name}</b>  ({Breed(d.Breed)})", NoOpt);
            GUILayout.Label($"Affection level {d.Level}  ({d.Affection}% to next)   Skill points: {d.Points}" +
                            (d.Unconscious ? "   - unconscious" : ""), NoOpt);

            GUILayout.Space(4);
            GUILayout.Label("Orders", NoOpt);
            GUILayout.BeginHorizontal(NoOpt);
            if (Button(d.Stay ? "Follow" : "> Follow")) Send("C", d.Handle, "Follow");
            if (Button(d.Stay ? "> Stay" : "Stay")) Send("C", d.Handle, "Stay");
            GUILayout.EndHorizontal();

            GUILayout.Label("Combat", NoOpt);
            GUILayout.BeginHorizontal(NoOpt);
            int passive = 0, defensive = 1, aggressive = 2;
            try { passive = PetData.CombatModePassive; defensive = PetData.CombatModeDefensive; aggressive = PetData.CombatModeAggressive; } catch { }
            if (Button((d.Combat == passive ? "> " : "") + "Passive")) Send("C", d.Handle, "Passive");
            if (Button((d.Combat == defensive ? "> " : "") + "Defensive")) Send("C", d.Handle, "Defensive");
            if (Button((d.Combat == aggressive ? "> " : "") + "Aggressive")) Send("C", d.Handle, "Aggressive");
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (Button(d.PetReady ? "Pet" : "Pet (petted recently)", d.PetReady && !d.Unconscious)) Send("P", d.Handle);

            GUILayout.Space(4);
            GUILayout.Label("Name", NoOpt);
            if (_renameFor != d.Handle) { _renameFor = d.Handle; _renameText = d.Name ?? ""; }
            GUILayout.BeginHorizontal(NoOpt);
            _renameText = GUILayout.TextField(_renameText ?? "", 20, NoOpt);
            if (Button("Rename", !string.IsNullOrWhiteSpace(_renameText) && _renameText != d.Name))
                Send("N", d.Handle, Proto.Clean(_renameText, 20));
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("Skills", NoOpt);
            foreach (var s in d.Skills)
            {
                GUILayout.BeginHorizontal(NoOpt);
                string req = s.Level == 0 && s.ReqAffection > d.Level ? $"  (needs affection {s.ReqAffection})" : "";
                GUILayout.Label($"{SkillName(s.Id)}  {s.Level}/{s.Max}{req}", NoOpt);
                GUILayout.FlexibleSpace();
                if (Button("+", d.Points > 0 && s.Level < s.Max && req == "")) Send("S", d.Handle, s.Id);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            if (!_confirmRelease)
            {
                if (Button("Release this dog...")) _confirmRelease = true;
            }
            else
            {
                GUILayout.Label($"Release {name} for good?", NoOpt);
                GUILayout.BeginHorizontal(NoOpt);
                if (Button("Yes, release")) { Send("R", d.Handle); _confirmRelease = false; }
                if (Button("Cancel")) _confirmRelease = false;
                GUILayout.EndHorizontal();
            }
        }
    }
}
