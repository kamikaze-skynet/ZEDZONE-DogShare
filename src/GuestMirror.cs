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
            public PetCharacterController Pc;
            public bool OnPanel;
            public string LastSig;   // what the cards last showed; refresh them only when this changes
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
        internal static int Count => ByGuid.Count;
        internal static IEnumerable<Mirror> Mirrors() => ByGuid.Values;

        class Incoming { public string Handle, Guid, Name; public int NetId, Breed, Level, Points, Spent, Combat; public float Xp, LastPet, Hunger = -1f; public bool Stay, Unc; public List<int[]> Skills = new List<int[]>(); }
        static readonly Dictionary<string, Incoming> _incoming = new Dictionary<string, Incoming>();
        static int _incomingSeq = -1;

        static bool _hostHasMod;
        internal static bool HostHasMod => _hostHasMod;
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
            catch (Exception e) { DLog.Error("act", "Couldn't send to the host", e); }
            if (parts.Length > 0 && Convert.ToString(parts[0]) != "L") DLog.Debug("act", "-> host: " + HostHandler.OpName(parts.Length > 0 ? Convert.ToString(parts[0]) : "?") + (parts.Length > 1 ? " " + string.Join(" ", Array.ConvertAll(parts, x => Convert.ToString(x))) : ""));
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
            long t0 = Hitch.Start();
            try { GuestUpdate(); } finally { Hitch.Stop("guest update", t0); }
        }

        void GuestUpdate()
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
                if (!_wasGuest) { _wasGuest = true; _nextProbe = Time.unscaledTime + 8f; DLog.Info("guest", "Joined a co-op world as a guest - will ask the host about shared dogs in a few seconds"); }
                if (string.IsNullOrEmpty(MyId())) { DLog.Throttle("noid", 20f, "guest", "Waiting for my character to load"); return; }
                if (Time.unscaledTime >= _nextStatus) { _nextStatus = Time.unscaledTime + 30f; Status(); }

                float now = Time.unscaledTime;
                if (_hostHasMod)
                {
                    if (now >= _nextPoll) { _nextPoll = now + 3f; Send("L", _askFull ? "1" : "0"); _askFull = false; }
                }
                else if (_probes < 3 && now >= _nextProbe)
                {
                    // Ask once in a while whether the host runs DogShare. A host without it would show this line in chat, so only 3 tries.
                    _probes++; _nextProbe = now + 30f; Send("L", "1"); _askFull = false;
                    DLog.Info("guest", $"Asking the host whether it runs DogShare (try {_probes}/3)" + (_probes == 3 ? " - last try; if no answer, the host doesn't have DogShare 1.2+" : ""));
                }
            }
            catch (Exception e) { DLog.Error("error", "Guest update failed", e); }
            try { AnimSync.Tick(); } catch { }
            try { OneDog.Tick(); } catch (Exception e) { DLog.ErrorThrottled("onedog", 30f, "error", "Choosing which shared dog a key press was for failed", e); }
            try { TickInteract(); } catch (Exception e) { DLog.Throttle("interr", 30f, "error", "Range check for shared dogs failed: " + e.Message); }
            try { TickPouch(); } catch (Exception e) { DLog.Error("item", "Saddlebag sync failed - stopped syncing it", e); _pouchOpen = null; }
        }

        // Stand-ins don't run the game's own "is the player close enough to interact" check (that runs from
        // the dog's AI update, which the host does for this dog). Run it here for our shared dogs so the
        // game's Commands / Pet / Feed prompt appears and disappears as you walk up and away.
        static void TickInteract()
        {
            if (ByGuid.Count == 0) return;
            foreach (var m in ByGuid.Values)
            {
                var pc = m.Pc;
                if (pc == null) continue;
                if (pc.npcData == null || pc.npcData.Pointer != m.Data.Pointer) continue;
                pc.UpdateCharacterInteractCheck();
            }
        }

        static float _nextStatus;
        static void Status()
        {
            int attached = 0;
            var parts = new List<string>();
            foreach (var m in ByGuid.Values)
            {
                bool on = IsAttached(m);
                if (on) attached++;
                parts.Add($"{m.Data.characterData?.characterName ?? m.Handle}: {(on ? "on its stand-in" : "stand-in not here")}{(m.OnPanel ? ", on Teammates" : "")}");
            }
            DLog.Change("gueststatus", "status", $"Guest: host runs DogShare={_hostHasMod}, shared dogs {ByGuid.Count} ({attached} attached) [{string.Join("; ", parts)}], saddlebag open={(_pouchOpen != null)}");
        }

        static bool IsAttached(Mirror m)
        {
            try
            {
                var c = NetMonsterSync.instance?.ClientGetPuppet(m.NetId)?.TryCast<PetCharacterController>();
                return c != null && c.npcData != null && c.npcData.Pointer == m.Data.Pointer;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- saddlebag
        internal static void OpenPouch(Mirror m)
        {
            var inv = m.Pet.pouchItem?.inventoryData;
            if (inv == null) { DLog.Info("item", "Open saddlebag: the dog has no saddlebag on"); Note("Your dog isn't wearing a saddlebag."); return; }
            var panel = LootInventoryPanel.instance;
            if (panel == null) { DLog.Error("item", "Open saddlebag: the game's loot window isn't available"); return; }
            try { PlayerPanel.instance?.OpenPanel(); }   // the game opens your backpack next to the saddlebag
            catch (Exception e) { DLog.Error("item", "Opening the backpack panel failed", e); }
            panel.OpenPanel(inv, m.Data.characterData?.characterName ?? "Saddlebag");
            _pouchOpen = m;
            _pouchHash = Proto.Hash(Proto.InvBytes(inv));
            _nextPouchCheck = Time.unscaledTime + 0.5f;
            DLog.Info("item", $"Opened saddlebag of '{m.Data.characterData?.characterName}' ({HostHandler.CountItems(inv)} item stack(s))");
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
                    DLog.Info("item", $"Saddlebag changed - sent to host ({bytes.Length} bytes, {HostHandler.CountItems(inv)} item stack(s))");
                }
            }
            if (!open) { _pouchOpen = null; _pouchClosedAt = Time.unscaledTime; DLog.Info("item", "Saddlebag closed"); }
        }

        static void SendRaw(string text)
        {
            try { NetChatSync.instance.SendChat(text); } catch (Exception e) { DLog.Error("act", "Couldn't send to the host", e); }
        }

        internal static void SendItem(byte[] bytes, params object[] head) => Proto.SendBlob(t => SendRaw(t), bytes, head);

        // ---------------------------------------------------------------- host replies
        internal static void OnHostMessage(string text)
        {
            var p = Proto.Split(text);
            if (p.Length < 2) return;
            if (p[1] == "*") { if (!_hostHasMod) { _hostHasMod = true; DLog.Info("guest", "Host runs DogShare - shared dogs enabled"); } AnimSync.GuestOnMessage(p); return; }
            if (p[1] != Proto.Short(MyId())) return;
            if (p[0] == "KE" || p[0] == "KX") { if (!_hostHasMod) _hostHasMod = true; Kennel.GuestOnMessage(p); return; }
            if (!_hostHasMod) { _hostHasMod = true; DLog.Info("guest", "Host runs DogShare - shared dogs enabled"); }
            if (p[0] != "D" && p[0] != "K" && p[0] != "E") DLog.Debug("guest", "<- host: " + HostHandler.OpName(p[0]) + " " + (p[0] == "B" ? p[2] + " " + p[3] + " part " + (p.Length > 6 ? p[5] + "/" + p[6] : "?") : text.Length > 120 ? text.Substring(Proto.Mark.Length, 110) + "..." : text.Substring(Proto.Mark.Length)));
            switch (p[0])
            {
                case "M":
                    if (p.Length > 2) { DLog.Info("guest", "Host says: " + p[2]); Note(p[2]); }
                    break;
                case "D":
                {
                    if (p.Length < 17) { DLog.Error("guest", $"Couldn't read a dog entry from the host ({p.Length} fields, expected 17) - is the host on the same DogShare version?"); return; }
                    int seq = I(p[2]);
                    if (seq != _incomingSeq) { _incoming.Clear(); _incomingSeq = seq; }
                    var d = new Incoming
                    {
                        Handle = p[4], Guid = p[5], NetId = I(p[6]), Name = p[7], Breed = I(p[8]), Level = I(p[9]),
                        Xp = F(p[10]), Points = I(p[11]), Spent = I(p[12]), Stay = p[13] == "1", Combat = I(p[14]),
                        LastPet = F(p[15]), Unc = p[16] == "1", Hunger = p.Length > 17 ? F(p[17]) : -1f
                    };
                    _incoming[d.Handle] = d;
                    break;
                }
                case "K":
                {
                    if (p.Length < 5 || I(p[2]) != _incomingSeq) return;
                    if (!_incoming.TryGetValue(p[3], out var d)) { DLog.Warn("guest", $"Got skills for dog {p[3]} without its entry - skipped"); return; }
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
                case "I":
                    Fetch.GuestOnBall(p);
                    break;
                case "T":
                    Fetch.GuestOnGift(p);
                    break;
                case "E":
                    if (_incoming.Count > 0 && I(p[2]) != _incomingSeq) return;
                    {
                        var names = new List<string>();
                        foreach (var x in _incoming.Values) names.Add(x.Name);
                        DLog.Change("hostlist", "guest", $"Host says I own {_incoming.Count} dog(s) here: [{string.Join(", ", names)}]");
                    }
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
                catch (Exception e) { DLog.ErrorThrottled("setup|" + d.Guid, 30f, "guest", "Setting up shared dog '" + d.Name + "' failed (retrying every 3 s, logged every 30 s)", e); }
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
                var placeholder = new CharacterData();             // replaced by the stand-in's own once it's found;
                placeholder.characterId = d.Guid;                  // GOAPNPCData reads hunger etc. from it
                placeholder.characterName = d.Name;
                m.Data.characterData = placeholder;
                ByGuid[d.Guid] = m;
                ByPtr[m.Data.Pointer] = m;
                DLog.Info("guest", $"Host says dog '{d.Name}' ({d.Handle}, net id {d.NetId}) is mine - created its pet data here");
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
                if (c != null && pc == null) DLog.Change("cast|" + d.Guid, "guest", $"'{d.Name}': the object with net id {d.NetId} isn't a dog on this PC");
            }
            catch (Exception e) { DLog.Error("guest", "Looking up the stand-in dog failed", e); }
            DLog.Change("found|" + d.Guid, "guest", pc != null ? $"'{d.Name}': found its stand-in on this PC (net id {d.NetId})" : $"'{d.Name}': its stand-in isn't on this PC right now (net id {d.NetId}; it shows up when it's near you)");

            if (pc != null)
            {
                var cd = pc.characterData;
                if (cd != null)
                {
                    if (string.IsNullOrEmpty(cd.characterId)) cd.characterId = d.Guid;
                    if (!string.IsNullOrEmpty(d.Name)) cd.characterName = d.Name;
                    m.Data.characterData = cd;
                }
                m.Pc = pc;
                var agent = pc.petAgent;
                if (agent != null && (agent.npcData == null || agent.npcData.Pointer != m.Data.Pointer))
                {
                    agent.npcData = m.Data;
                    agent.enabled = false;            // the host runs this dog's AI, not us
                    m.AttachedTo = pc.Pointer;
                    m.Pc = pc;
                    try { pc.RefreshInteracts(); } catch (Exception e) { DLog.Error("guest", "RefreshInteracts failed", e); }
                    bool readsBack = pc.npcData != null && pc.npcData.Pointer == m.Data.Pointer;
                    bool owned = false; try { owned = pc.IsOwnedByLocalPlayer(); } catch { }
                    DLog.Info("guest", $"'{d.Name}': stand-in flags: {PromptFlags.Describe(pc)}");
                    DLog.Info("guest", $"'{d.Name}': attached pet data to its stand-in - game sees it: {(readsBack ? "yes" : "NO")}, owned by me: {(owned ? "yes" : "NO")}" +
                        (readsBack && owned ? " -> the Commands / Pet prompt should appear when you walk up to it" : " -> the game's prompts won't show for it"));
                }
                else if (agent == null) DLog.Change("noagent|" + d.Guid, "guest", $"'{d.Name}': its stand-in has no pet AI component - can't attach");
            }
            else if (m.Data.characterData == null)
            {
                var cd = new CharacterData();
                cd.characterId = d.Guid; cd.characterName = d.Name;
                m.Data.characterData = cd;
            }

            // hunger lives in the character data, so set it after that's in place
            if (d.Hunger >= 0f && m.Data.characterData != null)
            {
                try
                {
                    if (Math.Abs(m.Data.hunger - d.Hunger) >= 1f) DLog.Debug("guest", $"'{d.Name}' hunger {m.Data.hunger:0} -> {d.Hunger:0} (from host)");
                    m.Data.hunger = d.Hunger;
                }
                catch (Exception e) { DLog.Throttle("hungerr|" + d.Guid, 60f, "error", $"'{d.Name}': couldn't show hunger: {e.Message}"); }
            }

            if (!m.OnPanel)
            {
                try
                {
                    var panel = TeamNPCPanel.instance;
                    if (panel == null) DLog.Warn("guest", "Teammates panel isn't available yet");
                    else { panel.AddNPC(m.Data); DLog.Info("guest", $"'{d.Name}': added to the Teammates panel"); }
                    m.OnPanel = true;
                }
                catch (Exception e) { DLog.Error("guest", $"'{d.Name}': adding to the Teammates panel failed", e); m.OnPanel = true; }
            }
            // The host resends the list every 3 s. Refreshing the pet and Teammates cards each time rebuilt their UI
            // for nothing (a hitch with a full Teammates card), so only refresh when something they show changed.
            var sig = Sig(d, pc);
            if (sig != m.LastSig)
            {
                m.LastSig = sig;
                try { PetPanel.NotifyPetChanged(m.Data); } catch { }
            }
        }

        static string Sig(Incoming d, PetCharacterController pc)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(d.Name).Append('|').Append(d.NetId).Append('|').Append(d.Breed).Append('|').Append(d.Level).Append('|')
              .Append(d.Xp.ToString("0.##", CultureInfo.InvariantCulture)).Append('|').Append(d.Points).Append('|').Append(d.Spent).Append('|')
              .Append(d.Stay).Append('|').Append(d.Combat).Append('|').Append(d.LastPet.ToString("0.#####", CultureInfo.InvariantCulture)).Append('|')
              .Append(d.Unc).Append('|').Append((int)d.Hunger).Append('|').Append(pc == null ? IntPtr.Zero : pc.Pointer);
            foreach (var k in d.Skills) sb.Append('|').Append(k[0]).Append(':').Append(k[1]);
            return sb.ToString();
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
                try { item = Proto.ItemFrom(bytes); } catch (Exception e) { DLog.Error("item", $"Couldn't read the {HostHandler.SlotName(slot)} copy from the host", e); continue; }
                if (bytes.Length > 0 && item == null) { DLog.Error("item", $"The {HostHandler.SlotName(slot)} copy from the host didn't decode ({bytes.Length} bytes)"); continue; }
                DLog.Info("item", $"'{m.Data.characterData?.characterName ?? m.Handle}' {HostHandler.SlotName(slot)}: {(item == null ? "empty" : "item " + item.itemId)} (copy from host{(slot == "p" && item?.inventoryData != null ? ", " + HostHandler.CountItems(item.inventoryData) + " stack(s) inside" : "")})");
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
            DLog.Info("item", $"Changed '{m.Data.characterData?.characterName}' {HostHandler.SlotName(slot)} here -> {(item == null ? "empty (item back in my backpack)" : "item " + item.itemId)}; sent to host ({bytes.Length} bytes)");
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
            DLog.Info("guest", $"Dog '{m.Data.characterData?.characterName ?? m.Handle}' is no longer mine here - removed its pet data");
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
            DLog.Info("act", $"'{m.Data.characterData?.characterName}': game menu chose '{cmd}'");
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
            else DLog.Warn("act", $"'{cmd}' isn't supported for shared dogs - ignored");
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
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OnEnterPlayerInteractRange))]
        [HarmonyPostfix]
        static void EnterRange(PetCharacterController __instance)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return;
                GameObject io = null; try { io = __instance.interactObject; } catch { }
                DLog.Throttle("range|" + m.Guid, 10f, "guest", $"Walked up to '{m.Data.characterData?.characterName}' - prompt built (interact object {(io == null ? "MISSING" : io.activeInHierarchy ? "active" : "inactive")}; flags now {PromptFlags.Describe(__instance)})");
            }
            catch { }
        }

        // The game's petting routine runs over several frames and stops on a stand-in (it re-checks
        // isNetPuppet). For shared dogs, pressing Pet asks the host directly; the host applies the
        // affection with the game's cooldown and confirms.
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PetInteract))]
        [HarmonyPrefix]
        static bool PetInteract(PetCharacterController __instance)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return true;
                if (m.Pet.isUnconscious) { GuestMirror.Note("Your dog is unconscious."); DLog.Info("act", "Pet: dog is unconscious - not sent"); return false; }
                DLog.Info("act", $"Pet '{m.Data.characterData?.characterName}' - asking host for the affection");
                GuestMirror.Send("P", m.Handle);
                try { AnimSync.GuestPetPose(2.5f); } catch (Exception e) { DLog.Error("act", "Petting pose failed", e); }
                return false;
            }
            catch (Exception e) { DLog.Error("act", "Pet failed", e); return true; }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.AddAffection))]
        [HarmonyPrefix]
        static bool AddAffection(PetCharacterController __instance)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.npcData);
                if (m == null) return true;
                DLog.Info("act", $"Petted '{m.Data.characterData?.characterName}' - asking host for the affection");
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
                if (item == null || me == null) { DLog.Warn("item", "Feed: no item or no local character"); return false; }
                float max = 0f; try { max = PetSystem.petMaxHunger; } catch { }
                if (max > 0f && m.Data.hunger >= max - 0.5f)
                {
                    GuestMirror.Note($"{m.Data.characterData?.characterName ?? "Your dog"} isn't hungry.");
                    DLog.Info("item", $"Feed: '{m.Data.characterData?.characterName}' is full ({m.Data.hunger:0}/{max:0}) - kept the food");
                    return false;
                }
                var bytes = Proto.ItemBytes(item);
                if (!BasicCharacterController.ConsumeOneItemFromCharacter(me, item, true)) { DLog.Warn("item", $"Feed: couldn't take item {item.itemId} from my backpack - nothing sent"); return false; }
                GuestMirror.SendItem(bytes, "F", m.Handle);
                DLog.Info("item", $"Fed '{m.Data.characterData?.characterName}' item {item.itemId}: took 1 from my backpack, sent to host ({bytes.Length} bytes)");
                try { GameController.instance.RefreshInventoryPanels(); } catch { }
                try { __instance.PlayHappy(); } catch { }
                return false;
            }
            catch (Exception e) { DLog.Error("item", "Feed failed", e); return false; }
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
                if (item == null || me == null) { DLog.Warn("item", "First aid: no item or no local character"); return false; }
                int id = item.itemId;
                if (!BasicCharacterController.ConsumeOneItemFromCharacter(me, item, true)) { DLog.Warn("item", $"First aid: couldn't take item {id} from my backpack - nothing sent"); return false; }
                GuestMirror.Send("A", m.Handle, id);
                DLog.Info("item", $"First aid on '{m.Data.characterData?.characterName}' with item {id}: took 1 from my backpack, asked host to heal");
                try { GameController.instance.RefreshInventoryPanels(); } catch { }
                return false;
            }
            catch (Exception e) { DLog.Error("item", "First aid failed", e); return false; }
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
            try { var m = GuestMirror.Of(__instance?.boundPet); if (m != null) SendGearChanges(m, __state); } catch (Exception e) { DLog.Error("item", "Equip failed", e); }
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
            try { var m = GuestMirror.Of(__instance?.boundPet); if (m != null) SendGearChanges(m, __state); } catch (Exception e) { DLog.Error("item", "Unequip failed", e); }
        }

        [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.OnUpgradeSkillClick))]
        [HarmonyPrefix]
        static bool OnUpgradeSkillClick(PetPanel __instance, int skillId)
        {
            try
            {
                var m = GuestMirror.Of(__instance?.boundPet);
                if (m == null) return true;
                DLog.Info("act", $"'{m.Data.characterData?.characterName}': upgrade skill {skillId}");
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
                if (!string.IsNullOrEmpty(name)) { DLog.Info("act", $"Renamed shared dog to '{name}'"); GuestMirror.Send("N", m.Handle, Proto.Clean(name, 20)); }
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
                DLog.Info("act", $"Release '{m.Data.characterData?.characterName}'");
                GuestMirror.Send("R", m.Handle);
                __result = true;
                return false;
            }
            catch { return true; }
        }

        // Let the Teammates panel and pet panel list our shared dogs like our own.
        // On a guest the game still thinks it's a one-pet session (NetSim.singlePetSession is only lifted on the host),
        // so PetSystem.IsPetFrozen calls every pet after the first one in the list "frozen". PetPanel.Open then swaps a
        // frozen pet for the first one: with two shared dogs, opening the second dog's menu showed the first dog's,
        // and its saddlebag / orders went to the first dog. The host runs these dogs and already allows several,
        // so a shared dog is never frozen here.
        [HarmonyPatch(typeof(PetSystem), nameof(PetSystem.IsPetFrozen))]
        [HarmonyPrefix]
        static bool IsPetFrozen(GOAPNPCData __0, ref bool __result)
        {
            try
            {
                if (GuestMirror.Of(__0) == null) return true;
                __result = false;
                DLog.Once("notfrozen", "guest", "Shared dogs are never 'frozen' here (the game's one-pet rule only applies to the host's dogs)");
                return false;
            }
            catch { return true; }
        }

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
                DLog.Change("petlists", "guest", $"The game's pet lists now include my {GuestMirror.ByPtr.Count} shared dog(s)");
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
