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
            public readonly Dictionary<string, string> GearHash = new Dictionary<string, string>();
        }

        // Latest gear bytes from the host, by dog handle and slot (applied when the mirror exists).
        static readonly Dictionary<string, byte[]> _gear = new Dictionary<string, byte[]>();
        // Saddlebag currently open on this PC
        static Mirror _pouchOpen;
        static string _pouchHash;
        static float _nextPouchCheck, _pouchClosedAt = -100f;

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
        static bool _askFull = true;

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
                    if (_wasGuest) { DetachAll(); _hostHasMod = false; _probes = 0; _askFull = true; _gear.Clear(); }
                    _wasGuest = false;
                    return;
                }
                if (!_wasGuest) { _wasGuest = true; _nextProbe = Time.unscaledTime + 8f; }
                if (string.IsNullOrEmpty(MyId())) return;

                float now = Time.unscaledTime;
                if (_hostHasMod)
                {
                    if (now >= _nextPoll) { _nextPoll = now + 3f; Send("L", _askFull ? "1" : "0"); _askFull = false; }
                }
                else if (_probes < 3 && now >= _nextProbe)
                {
                    // Ask once in a while whether the host runs DogShare. A host without it would show this line in chat, so only 3 tries.
                    _probes++; _nextProbe = now + 30f; Send("L", "1"); _askFull = false;
                }
            }
            catch (Exception e) { Plugin.L.LogError("guest update: " + e.Message); }
            try { TickPouch(); } catch (Exception e) { Plugin.L.LogError("saddlebag: " + e.Message); _pouchOpen = null; }
        }

        // ---------------------------------------------------------------- saddlebag
        internal static void OpenPouch(Mirror m)
        {
            var inv = m.Pet.pouchItem?.inventoryData;
            if (inv == null) { Note("Your dog isn't wearing a saddlebag."); return; }
            var panel = LootInventoryPanel.instance;
            if (panel == null) return;
            panel.OpenPanel(inv, m.Data.characterData?.characterName ?? "Saddlebag");
            _pouchOpen = m;
            _pouchHash = Proto.Hash(Proto.InvBytes(inv));
            _nextPouchCheck = Time.unscaledTime + 0.5f;
        }

        static bool PouchStillOpen(InventoryData inv)
        {
            var panel = LootInventoryPanel.instance;
            if (panel == null || !panel.gameObject.activeInHierarchy) return false;
            var datas = new Il2CppSystem.Collections.Generic.List<InventoryData>();
            var names = new Il2CppSystem.Collections.Generic.List<string>();
            panel.GetOpenedInventoryStack(datas, names);
            for (int i = 0; i < datas.Count; i++) if (datas[i] != null && datas[i].Pointer == inv.Pointer) return true;
            return false;
        }

        static void TickPouch()
        {
            var m = _pouchOpen;
            if (m == null || Time.unscaledTime < _nextPouchCheck) return;
            _nextPouchCheck = Time.unscaledTime + 0.5f;
            var inv = m.Pet.pouchItem?.inventoryData;
            bool open = inv != null && PouchStillOpen(inv);
            if (inv != null)
            {
                var bytes = Proto.InvBytes(inv);
                var h = Proto.Hash(bytes);
                if (h != _pouchHash)
                {
                    _pouchHash = h;
                    Proto.SendBlob(t => SendRaw(t), bytes, "U", m.Handle);
                }
            }
            if (!open) { _pouchOpen = null; _pouchClosedAt = Time.unscaledTime; }
        }

        static void SendRaw(string text)
        {
            try { NetChatSync.instance.SendChat(text); } catch (Exception e) { Plugin.L.LogError("send: " + e.Message); }
        }

        internal static void SendItem(byte[] bytes, params object[] head) => Proto.SendBlob(t => SendRaw(t), bytes, head);

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
                case "B":
                {
                    if (p.Length < 8) return;
                    if (!Proto.Collect("host", p, out var bytes)) return;
                    _gear[p[2] + "|" + p[3]] = bytes;
                    foreach (var m in ByGuid.Values) if (m.Handle == p[2]) ApplyGear(m);
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
            try { pd.isUnconscious = d.Unc; } catch { }
            ApplyGear(m);

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

        static void ApplyGear(Mirror m)
        {
            foreach (var slot in new[] { "c", "v", "p" })
            {
                if (!_gear.TryGetValue(m.Handle + "|" + slot, out var bytes)) continue;
                var h = Proto.Hash(bytes);
                if (m.GearHash.TryGetValue(slot, out var old) && old == h) continue;
                if (slot == "p" && (_pouchOpen == m || Time.unscaledTime - _pouchClosedAt < 2f)) continue;   // don't swap a bag that's open here
                ItemData item = null;
                try { item = Proto.ItemFrom(bytes); } catch (Exception e) { Plugin.L.LogWarning("gear copy: " + e.Message); continue; }
                if (slot == "c") m.Pet.collarItem = item;
                else if (slot == "v") m.Pet.vestItem = item;
                else m.Pet.pouchItem = item;
                m.GearHash[slot] = h;
                try { PetPanel.NotifyPetChanged(m.Data); } catch { }
            }
        }

        internal static void GearChangedHere(Mirror m, string slot)
        {
            var item = HostHandler.GearItem(m.Pet, slot);
            var bytes = Proto.ItemBytes(item);
            m.GearHash[slot] = Proto.Hash(bytes);
            _gear[m.Handle + "|" + slot] = bytes;
            SendItem(bytes, "G", m.Handle, slot);
            if (slot == "p" && _pouchOpen == m && item == null) _pouchOpen = null;
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
            else if (cmd == "OpenPouch") GuestMirror.OpenPouch(m);
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

        static CharacterData LocalCharacter()
        {
            try { return DogShareBehaviour.HostPlayer()?.characterData; } catch { return null; }
        }

        // Feeding: the game's own food picker opens as usual. When you pick, one item comes out of
        // your backpack and the host feeds it to the real dog.
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OnSelectFoodTool))]
        [HarmonyPrefix]
        static bool OnSelectFoodTool(PetCharacterController __instance, ToolData toolData)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return true;
                var item = toolData?.itemData;
                var me = LocalCharacter();
                if (item == null || me == null) return false;
                var bytes = Proto.ItemBytes(item);
                if (!BasicCharacterController.ConsumeOneItemFromCharacter(me, item, true)) return false;
                GuestMirror.SendItem(bytes, "F", m.Handle);
                try { GameController.instance.RefreshInventoryPanels(); } catch { }
                try { __instance.PlayHappy(); } catch { }
                return false;
            }
            catch (Exception e) { Plugin.L.LogError("feed: " + e.Message); return false; }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OnSelectFirstAidTool))]
        [HarmonyPrefix]
        static bool OnSelectFirstAidTool(PetCharacterController __instance, ToolData toolData)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return true;
                var item = toolData?.itemData;
                var me = LocalCharacter();
                if (item == null || me == null) return false;
                int id = item.itemId;
                if (!BasicCharacterController.ConsumeOneItemFromCharacter(me, item, true)) return false;
                GuestMirror.Send("A", m.Handle, id);
                try { GameController.instance.RefreshInventoryPanels(); } catch { }
                return false;
            }
            catch (Exception e) { Plugin.L.LogError("first aid: " + e.Message); return false; }
        }

        // Gear: the game's own pet panel moves the item between your backpack and your copy of the
        // slot; afterwards we send the slot's new contents to the host.
        static string[] GearPtrs(GuestMirror.Mirror m) => new[]
        {
            P(m.Pet.collarItem), P(m.Pet.vestItem), P(m.Pet.pouchItem)
        };
        static string P(ItemData i) => i == null ? "0" : i.Pointer.ToString();
        static void SendGearChanges(GuestMirror.Mirror m, string[] before)
        {
            var after = GearPtrs(m);
            string[] slots = { "c", "v", "p" };
            for (int i = 0; i < 3; i++) if (before[i] != after[i]) GuestMirror.GearChangedHere(m, slots[i]);
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnSelectEquipTool))]
        [HarmonyPrefix]
        static void EquipPre(PetPanel __instance, out string[] __state)
        {
            __state = null;
            try { var m = GuestMirror.Of(__instance?.boundPet); if (m != null) __state = GearPtrs(m); } catch { }
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnSelectEquipTool))]
        [HarmonyPostfix]
        static void EquipPost(PetPanel __instance, string[] __state)
        {
            if (__state == null) return;
            try { var m = GuestMirror.Of(__instance?.boundPet); if (m != null) SendGearChanges(m, __state); } catch (Exception e) { Plugin.L.LogError("equip: " + e.Message); }
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnEquipSlotClick))]
        [HarmonyPrefix]
        static void SlotPre(PetPanel __instance, out string[] __state)
        {
            __state = null;
            try { var m = GuestMirror.Of(__instance?.boundPet); if (m != null) __state = GearPtrs(m); } catch { }
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnEquipSlotClick))]
        [HarmonyPostfix]
        static void SlotPost(PetPanel __instance, string[] __state)
        {
            if (__state == null) return;
            try { var m = GuestMirror.Of(__instance?.boundPet); if (m != null) SendGearChanges(m, __state); } catch (Exception e) { Plugin.L.LogError("unequip: " + e.Message); }
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
