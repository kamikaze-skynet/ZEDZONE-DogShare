using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Guest side. On a guest's PC the host's dog is a stand-in (a real PetCharacterController with a
    // GOAPAgent_Pet) but without pet data, so the game shows no prompts for it. For each dog the host
    // says is ours, we give that stand-in a mirror GOAPNPCData/PetData naming us as owner. The game then
    // shows its own "Commands / Pet" prompt, the Teammates card and the orders menu. Every action on a
    // mirror is caught here and sent to the host, which runs it on the real dog.
    public class GuestMirror : MonoBehaviour
    {
        public GuestMirror(IntPtr ptr) : base(ptr) { }

        internal class Mirror
        {
            public string Handle, Guid; public int NetId;
            public GOAPNPCData Data; public PetData Pet;
            public IntPtr AttachedTo;
            public bool OnPanel;
        }

        // npcData pointer -> mirror, for the patches
        internal static readonly Dictionary<IntPtr, Mirror> ByPtr = new Dictionary<IntPtr, Mirror>();
        static readonly Dictionary<string, Mirror> ByGuid = new Dictionary<string, Mirror>();

        class Incoming { public string Handle, Guid, Name; public int NetId, Breed, Level, Points, Spent, Combat; public float Xp, LastPet; public bool Stay, Unc; public List<int[]> Skills = new List<int[]>(); }
        static readonly Dictionary<string, Incoming> _incoming = new Dictionary<string, Incoming>();
        static int _incomingSeq = -1;

        static bool _hostHasMod;
        static int _probes;
        static float _nextPoll, _nextProbe;
        static bool _wasGuest;

        internal static bool IsGuest()
        {
            try { return NetworkManager.isOnline && NetworkManager.isClient; } catch { return false; }
        }

        internal static Mirror Of(GOAPNPCData d)
        {
            if (d == null) return null;
            return ByPtr.TryGetValue(d.Pointer, out var m) ? m : null;
        }

        internal static void Send(params object[] parts)
        {
            try { NetChatSync.instance.SendChat(Proto.Join(parts)); }
            catch (Exception e) { Plugin.L.LogError("send: " + e.Message); }
        }

        internal static void Note(string s)
        {
            try { SystemNotificationPanel.ShowNotificationSafe(s, true); } catch { }
        }

        static string MyId()
        {
            try { return DogShareBehaviour.IdOf(DogShareBehaviour.HostPlayer()); } catch { return null; }
        }

        void Update()
        {
            try
            {
                bool guest = IsGuest();
                if (!guest)
                {
                    if (_wasGuest) { DetachAll(); _hostHasMod = false; _probes = 0; }
                    _wasGuest = false;
                    return;
                }
                if (!_wasGuest) { _wasGuest = true; _nextProbe = Time.unscaledTime + 8f; }
                if (string.IsNullOrEmpty(MyId())) return;

                float now = Time.unscaledTime;
                if (_hostHasMod)
                {
                    if (now >= _nextPoll) { _nextPoll = now + 3f; Send("L"); }
                }
                else if (_probes < 3 && now >= _nextProbe)
                {
                    // Ask once in a while whether the host runs DogShare. A host without it would show this line in chat, so only 3 tries.
                    _probes++; _nextProbe = now + 30f; Send("L");
                }
            }
            catch (Exception e) { Plugin.L.LogError("guest update: " + e.Message); }
        }

        // ---------------------------------------------------------------- host replies
        internal static void OnHostMessage(string text)
        {
            var p = Proto.Split(text);
            if (p.Length < 2) return;
            if (p[1] != Proto.Short(MyId())) return;
            if (!_hostHasMod) { _hostHasMod = true; Plugin.L.LogInfo("Host runs DogShare - shared dogs enabled."); }
            switch (p[0])
            {
                case "M":
                    if (p.Length > 2) Note(p[2]);
                    break;
                case "D":
                {
                    if (p.Length < 19) return;
                    int seq = I(p[2]);
                    if (seq != _incomingSeq) { _incoming.Clear(); _incomingSeq = seq; }
                    var d = new Incoming
                    {
                        Handle = p[4], Guid = p[5], NetId = I(p[6]), Name = p[7], Breed = I(p[8]), Level = I(p[9]),
                        Xp = F(p[10]), Points = I(p[11]), Spent = I(p[12]), Stay = p[13] == "1", Combat = I(p[14]),
                        LastPet = F(p[15]), Unc = p[16] == "1"
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
                        if (f.Length >= 2) d.Skills.Add(new[] { I(f[0]), I(f[1]) });
                    }
                    break;
                }
                case "E":
                    if (_incoming.Count > 0 && I(p[2]) != _incomingSeq) return;
                    Apply(new List<Incoming>(_incoming.Values));
                    _incoming.Clear(); _incomingSeq = -1;
                    break;
            }
        }

        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        // ---------------------------------------------------------------- mirrors
        static void Apply(List<Incoming> dogs)
        {
            string me = MyId();
            var keep = new HashSet<string>();
            foreach (var d in dogs)
            {
                if (string.IsNullOrEmpty(d.Guid)) continue;
                keep.Add(d.Guid);
                try { Upsert(d, me); }
                catch (Exception e) { Plugin.L.LogError("mirror " + d.Name + ": " + e); }
            }
            foreach (var g in new List<string>(ByGuid.Keys))
                if (!keep.Contains(g)) Remove(ByGuid[g]);
        }

        static void Upsert(Incoming d, string me)
        {
            if (!ByGuid.TryGetValue(d.Guid, out var m))
            {
                m = new Mirror { Handle = d.Handle, Guid = d.Guid };
                m.Data = new GOAPNPCData();
                m.Pet = new PetData();
                m.Data.petData = m.Pet;
                m.Data.npcGUID = d.Guid;
                ByGuid[d.Guid] = m;
                ByPtr[m.Data.Pointer] = m;
                Plugin.L.LogInfo($"Shared dog '{d.Name}' is yours here.");
            }
            m.Handle = d.Handle; m.NetId = d.NetId;
            m.Data.leaderCharacterGUID = me;

            var pd = m.Pet;
            pd.breedId = d.Breed;
            pd.affectionLevel = d.Level;
            pd.affectionXp = d.Xp;
            pd.skillPointsSpent = d.Spent;
            pd.basicCommand = d.Stay ? "Stay" : "Follow";
            pd.combatMode = d.Combat;
            pd.lastPetInGameTime = d.LastPet;
            foreach (var s in d.Skills) { try { pd.SetSkillLevel(s[0], s[1]); } catch { } }

            // The stand-in dog on this PC.
            PetCharacterController pc = null;
            try
            {
                var sync = NetMonsterSync.instance;
                var c = sync != null && d.NetId >= 0 ? sync.ClientGetPuppet(d.NetId) : null;
                pc = c?.TryCast<PetCharacterController>();
            }
            catch { }

            if (pc != null)
            {
                var cd = pc.characterData;
                if (cd != null)
                {
                    if (string.IsNullOrEmpty(cd.characterId)) cd.characterId = d.Guid;
                    if (!string.IsNullOrEmpty(d.Name)) cd.characterName = d.Name;
                    m.Data.characterData = cd;
                }
                var agent = pc.petAgent;
                if (agent != null && (agent.npcData == null || agent.npcData.Pointer != m.Data.Pointer))
                {
                    agent.npcData = m.Data;
                    agent.enabled = false;            // the host runs this dog's AI, not us
                    m.AttachedTo = pc.Pointer;
                    try { pc.RefreshInteracts(); } catch { }
                }
            }
            else if (m.Data.characterData == null)
            {
                var cd = new CharacterData();
                cd.characterId = d.Guid; cd.characterName = d.Name;
                m.Data.characterData = cd;
            }

            if (!m.OnPanel)
            {
                try { TeamNPCPanel.instance?.AddNPC(m.Data); m.OnPanel = true; } catch (Exception e) { Plugin.L.LogWarning("team card: " + e.Message); m.OnPanel = true; }
            }
            try { PetPanel.NotifyPetChanged(m.Data); } catch { }
        }

        static void Remove(Mirror m)
        {
            try
            {
                foreach (var pc in UnityEngine.Object.FindObjectsOfType<PetCharacterController>())
                {
                    var a = pc?.petAgent;
                    if (a != null && a.npcData != null && a.npcData.Pointer == m.Data.Pointer) { a.npcData = null; try { pc.RefreshInteracts(); } catch { } }
                }
            }
            catch { }
            try { if (m.OnPanel) TeamNPCPanel.instance?.RemoveNPC(m.Data); } catch { }
            try { PetPanel.NotifyPetRemoved(m.Data); } catch { }
            ByGuid.Remove(m.Guid);
            ByPtr.Remove(m.Data.Pointer);
            Plugin.L.LogInfo("A shared dog is no longer yours here.");
        }

        static void DetachAll()
        {
            foreach (var m in new List<Mirror>(ByGuid.Values)) Remove(m);
        }

        internal static List<GOAPNPCData> All()
        {
            var l = new List<GOAPNPCData>();
            foreach (var m in ByGuid.Values) l.Add(m.Data);
            return l;
        }
    }

    // Catch every action the game's own UI takes on a mirror dog and send it to the host.
    [HarmonyPatch]
    internal static class GuestPatches
    {
        static readonly HashSet<string> PetCommands = new HashSet<string> { "Follow", "Stay", "Passive", "Defensive", "Aggressive" };

        static bool Forward(GuestMirror.Mirror m, string cmd)
        {
            if (cmd == null) return false;
            int dot = cmd.LastIndexOf('.');
            if (dot >= 0) cmd = cmd.Substring(dot + 1);
            if (PetCommands.Contains(cmd))
            {
                GuestMirror.Send("C", m.Handle, cmd);
                // show it right away; the host's next update confirms it
                if (cmd == "Follow" || cmd == "Stay") { m.Pet.basicCommand = cmd; }
                else
                {
                    try { m.Pet.combatMode = cmd == "Passive" ? PetData.CombatModePassive : cmd == "Defensive" ? PetData.CombatModeDefensive : PetData.CombatModeAggressive; } catch { }
                }
                try { PetPanel.NotifyPetChanged(m.Data); } catch { }
            }
            else if (cmd == "OpenPouch") GuestMirror.Note("The saddlebag of a shared dog can only be opened by the host.");
            return true;
        }

        [HarmonyPatch(typeof(GOAPAgent_Pet), nameof(GOAPAgent_Pet.ExecutePetCommand))]
        [HarmonyPrefix]
        static bool ExecutePetCommand(GOAPAgent_Pet __instance, string command)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return true;
                Forward(m, command);
                return false;
            }
            catch { return true; }
        }

        [HarmonyPatch(typeof(GOAPNPCData), nameof(GOAPNPCData.ExecuteNPCCommand))]
        [HarmonyPrefix]
        static bool ExecuteNPCCommand(GOAPNPCData __instance, string commandStr)
        {
            try
            {
                var m = GuestMirror.Of(__instance);
                if (m == null) return true;
                Forward(m, commandStr);
                return false;
            }
            catch { return true; }
        }

        // Petting finishes with AddAffection; the host applies the real affection (and its cooldown).
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.AddAffection))]
        [HarmonyPrefix]
        static bool AddAffection(PetCharacterController __instance)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return true;
                GuestMirror.Send("P", m.Handle);
                return false;
            }
            catch { return true; }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFeedSelect))]
        [HarmonyPrefix]
        static bool OpenFeedSelect(PetCharacterController __instance)
        {
            if (GuestMirror.Of(__instance?.npcData) == null) return true;
            GuestMirror.Note("Shared dogs are kept fed by the host's world.");
            return false;
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFirstAidSelect))]
        [HarmonyPrefix]
        static bool OpenFirstAidSelect(PetCharacterController __instance)
        {
            if (GuestMirror.Of(__instance?.npcData) == null) return true;
            GuestMirror.Note("Your dog will wake up on its own.");
            return false;
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnUpgradeSkillClick))]
        [HarmonyPrefix]
        static bool OnUpgradeSkillClick(PetPanel __instance, int skillId)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.boundPet);
                if (m == null) return true;
                GuestMirror.Send("S", m.Handle, skillId);
                return false;
            }
            catch { return true; }
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnRenameClick))]
        [HarmonyPostfix]
        static void OnRenameClick(PetPanel __instance)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.boundPet);
                if (m == null) return;
                var name = m.Data.characterData?.characterName;
                if (!string.IsNullOrEmpty(name)) GuestMirror.Send("N", m.Handle, Proto.Clean(name, 20));
            }
            catch { }
        }

        [HarmonyPatch(typeof(PetSystem), nameof(PetSystem.ReleasePet))]
        [HarmonyPrefix]
        static bool ReleasePet(GOAPNPCData npcData, ref bool __result)
        {
            try
            {
                var m = GuestMirror.Of(npcData);
                if (m == null) return true;
                GuestMirror.Send("R", m.Handle);
                __result = true;
                return false;
            }
            catch { return true; }
        }

        // Let the Teammates panel and pet panel list our shared dogs like our own.
        [HarmonyPatch(typeof(PetSystem), nameof(PetSystem.GetPlayerPetDatas))]
        [HarmonyPostfix]
        static void GetPlayerPetDatas(ref Il2CppSystem.Collections.Generic.List<GOAPNPCData> __result) => AddMirrors(ref __result);

        [HarmonyPatch(typeof(PetSystem), nameof(PetSystem.GetActivePlayerPetDatas))]
        [HarmonyPostfix]
        static void GetActivePlayerPetDatas(ref Il2CppSystem.Collections.Generic.List<GOAPNPCData> __result) => AddMirrors(ref __result);

        static void AddMirrors(ref Il2CppSystem.Collections.Generic.List<GOAPNPCData> list)
        {
            try
            {
                if (!GuestMirror.IsGuest() || GuestMirror.ByPtr.Count == 0) return;
                if (list == null) list = new Il2CppSystem.Collections.Generic.List<GOAPNPCData>();
                foreach (var d in GuestMirror.All())
                {
                    bool has = false;
                    for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].Pointer == d.Pointer) { has = true; break; }
                    if (!has) list.Add(d);
                }
            }
            catch { }
        }
    }
}
