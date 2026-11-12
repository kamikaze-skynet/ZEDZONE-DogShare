using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Guest <-> host messages ride on the game's own co-op chat (MsgChat), marked with a prefix.
    //  guest -> host : NetChatSync.SendChat("@@DS1|...")      (normal chat path, host intercepts)
    //  host -> guest : NetworkManager.SendTo(peer, MsgChat)    (only to the guest who asked)
    // Every chat line goes through NetChatSync.Append before it's shown, so marked lines are dropped there.
    //
    // Guest -> host:  L                    list my dogs
    //                 C|dog|Follow         command (Follow, Stay, Passive, Defensive, Aggressive)
    //                 P|dog                pet
    //                 N|dog|name           rename
    //                 S|dog|skillId        upgrade skill
    //                 R|dog                release
    //                 F|dog|<item>         feed this item (already taken from the guest's backpack)
    //                 A|dog|itemId         first aid with this item (already used up)
    //                 G|dog|slot|<item>    set gear slot c(ollar) / v(est) / p(ouch); empty = unequipped
    //                 U|dog|<inventory>    new saddlebag contents
    //                 V|1/0                my petting pose on / off (see Anim.cs)
    //   <item>/<inventory> = xfer|i|n|base64 chunk, the game's own item bytes
    // Host -> guest:  D|to|seq|count|dog|npcGuid|netId|name|breed|level|xp|points|spent|stay|combat|lastPet|unconscious|hunger
    //                 K|to|seq|dog|id:level:max;...
    //                 B|to|dog|slot|<item> gear slot contents (only when changed)
    //                 E|to|seq             end of list
    //                 M|to|text            message to show
    //                 I|to|dog|<item>      fetch: the dog brought this ball back (see Fetch.cs)
    //                 T|to|dog|itemId|name gift: "<dog> brought you <item>" (the gift itself is on the ground)
    //                 *-broadcasts (Anim.cs): Y animation, S sound, Z petting pose, W speech bubble
    // "dog" is the first 8 characters of the dog's npcGUID, "to" the first 8 of the guest's characterId.
    internal static class Proto
    {
        public const string Mark = "@@DS1|";
        public static bool Is(string s) => s != null && s.StartsWith(Mark, StringComparison.Ordinal);
        public static string Short(string id) => string.IsNullOrEmpty(id) ? "" : (id.Length > 8 ? id.Substring(0, 8) : id);
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('|', '/').Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }
        public static string Join(params object[] parts)
        {
            var sb = new StringBuilder(Mark);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(Convert.ToString(parts[i], CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
        public static string[] Split(string s) => s.Substring(Mark.Length).Split('|');

        // Items travel as the game's own bytes (NetItemSync / NetContainerSync serializers),
        // base64 in chunks small enough for the 200-character chat limit.
        const int Chunk = 140;
        static int _xfer;
        public static void SendBlob(Action<string> send, byte[] bytes, params object[] head)
        {
            string b64 = bytes == null || bytes.Length == 0 ? "" : Convert.ToBase64String(bytes);
            int n = (b64.Length + Chunk - 1) / Chunk;
            int x = ++_xfer;
            if (n == 0) { send(Join(Concat(head, x, 0, 0, ""))); return; }
            for (int i = 0; i < n; i++)
                send(Join(Concat(head, x, i, n, b64.Substring(i * Chunk, Math.Min(Chunk, b64.Length - i * Chunk)))));
        }
        static object[] Concat(object[] head, params object[] tail)
        {
            var r = new object[head.Length + tail.Length];
            head.CopyTo(r, 0); tail.CopyTo(r, head.Length);
            return r;
        }

        static readonly Dictionary<string, string[]> _parts = new Dictionary<string, string[]>();
        // p = split message; the last four fields are xfer|i|n|chunk. Returns the bytes once complete.
        public static bool Collect(string owner, string[] p, out byte[] bytes)
        {
            bytes = null;
            int k = p.Length - 4;
            if (k < 1) return false;
            int i = ToInt(p[k + 1]), n = ToInt(p[k + 2]);
            if (n == 0) { bytes = new byte[0]; return true; }
            string key = owner + "|" + p[0] + "|" + p[k];
            if (!_parts.TryGetValue(key, out var arr) || arr.Length != n) { arr = new string[n]; _parts[key] = arr; }
            if (i < 0 || i >= n) return false;
            arr[i] = p[k + 3];
            foreach (var c in arr) if (c == null) return false;
            _parts.Remove(key);
            try { bytes = Convert.FromBase64String(string.Concat(arr)); return true; }
            catch { return false; }
        }
        static int ToInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

        public static byte[] ItemBytes(ItemData item)
        {
            if (item == null) return new byte[0];
            var a = NetItemSync.SerializeItemData(item);
            return a == null ? new byte[0] : (byte[])a;
        }
        public static ItemData ItemFrom(byte[] b) => b == null || b.Length == 0 ? null : NetItemSync.DeserializeItemData(b);
        public static byte[] InvBytes(InventoryData inv)
        {
            if (inv == null) return new byte[0];
            var a = NetContainerSync.SerializeInventory(inv);
            return a == null ? new byte[0] : (byte[])a;
        }
        public static string Hash(byte[] b)
        {
            if (b == null || b.Length == 0) return "-";
            using (var sha = System.Security.Cryptography.SHA1.Create()) return Convert.ToBase64String(sha.ComputeHash(b));
        }
    }

    [HarmonyPatch]
    internal static class ChatPatches
    {
        // Hide marked lines everywhere (local echo, relays, speech bubbles).
        [HarmonyPatch(typeof(NetChatSync), nameof(NetChatSync.Append))]
        [HarmonyPrefix]
        static bool Append(string text)
        {
            if (!Proto.Is(text)) return true;
            DLog.Once("hidechat", "start", "Hiding DogShare's chat lines works");
            return false;
        }

        // Host: requests from guests.
        [HarmonyPatch(typeof(NetChatSync), nameof(NetChatSync.OnHostReceive))]
        [HarmonyPrefix]
        static bool OnHostReceive(MsgChat msg, NetPeer peer)
        {
            try
            {
                var text = msg?.text;
                if (!Proto.Is(text)) return true;
                long t0 = Hitch.Start();
                try { HostHandler.Handle(text, peer); } finally { Hitch.Stop("guest requests", t0); }
            }
            catch (Exception e) { DLog.Error("error", "Handling a guest request failed", e); }
            return false;   // never relay or show
        }

        // Guest: replies from the host.
        [HarmonyPatch(typeof(NetChatSync), nameof(NetChatSync.OnClientReceive))]
        [HarmonyPrefix]
        static bool OnClientReceive(MsgChat msg)
        {
            try
            {
                var text = msg?.text;
                if (!Proto.Is(text)) return true;
                long t0 = Hitch.Start();
                try { GuestMirror.OnHostMessage(text); } finally { Hitch.Stop("host replies", t0); }
            }
            catch (Exception e) { DLog.Error("error", "Handling a host reply failed", e); }
            return false;
        }
    }

    internal static class HostHandler
    {
        static int _seq;

        internal static void Handle(string text, NetPeer peer)
        {
            if (!NetworkManager.isOnline || NetworkManager.isClient) return;
            var nm = NetworkManager.instance;
            if (nm == null || peer == null) return;
            string charId = null; bool hardcore = false;
            if (!nm.TryGetPeerCharacter(peer, out charId, out hardcore) || string.IsNullOrEmpty(charId))
            {
                DLog.Throttle("nochar", 30f, "req", "Got a DogShare request from a player who has no character yet - ignored");
                return;
            }
            var p = Proto.Split(text);
            string op = p[0];
            string to = Proto.Short(charId);
            string who = PeerName(charId);
            if (_moddedGuests.Add(charId)) DLog.Info("req", $"{who} ({to}) runs DogShare - answering their requests");
            AnimSync.RememberPeer(charId, peer);
            if (op == "V") { AnimSync.HostOnGuestPose(charId, p.Length > 1 && p[1] == "1"); return; }
            if (Kennel.HandleGuest(op, p, charId, peer)) return;

            if (op == "L")
            {
                if (p.Length > 1 && p[1] == "1") ForgetGear(charId);   // guest wants everything again
                SendList(peer, charId); return;
            }
            if (p.Length < 2) return;

            // item transfers arrive in chunks; wait for the last one
            byte[] blob = null;
            if (op == "F" || op == "G" || op == "U")
            {
                if (!Proto.Collect(charId, p, out blob)) return;
                DLog.Debug("req", $"{who}: received {OpName(op)} data, {blob?.Length ?? 0} bytes");
            }

            var dog = FindDog(charId, p[1]);
            if (dog == null)
            {
                DLog.Warn("req", $"{who} asked to {OpName(op)} dog {p[1]}, but no dog of theirs has that id here");
                Reply(peer, Proto.Join("M", to, "That dog isn't yours any more.")); SendList(peer, charId); return;
            }
            string dogName = dog.npcData?.characterData?.characterName;
            DLog.Info("req", $"{who} -> {OpName(op)}{(op == "C" && p.Length > 2 ? " " + p[2] : op == "S" && p.Length > 2 ? " skill " + p[2] : op == "G" && p.Length > 2 ? " slot " + p[2] : op == "A" && p.Length > 2 ? " item " + p[2] : "")} on '{dogName}' ({p[1]})");

            string note = null;
            switch (op)
            {
                case "C": note = Command(dog, p.Length > 2 ? p[2] : ""); break;
                case "P": note = Pet(dog); break;
                case "N": note = Rename(dog, p.Length > 2 ? Proto.Clean(p[2], 20) : ""); break;
                case "S": note = Skill(dog, p.Length > 2 ? p[2] : "", charId); break;
                case "R": note = Release(dog); break;
                case "F": note = Feed(dog, blob); break;
                case "A": note = FirstAid(dog, p.Length > 2 ? p[2] : ""); break;
                case "G": note = SetGear(dog, p.Length > 2 ? p[2] : "", blob, charId); break;
                case "U": note = SetPouch(dog, blob, charId); break;
            }
            if (!string.IsNullOrEmpty(note)) { DLog.Info("req", $"  result for {who}: {note}"); Reply(peer, Proto.Join("M", to, Proto.Clean(note, 150))); }
            else DLog.Debug("req", "  done");
            SendList(peer, charId);
        }

        static readonly HashSet<string> _moddedGuests = new HashSet<string>();

        static string PeerName(string charId)
        {
            try { var g = DogShareBehaviour.GuestById(charId); if (g != null) return DogShareBehaviour.GuestName(g); } catch { }
            return "guest " + Proto.Short(charId);
        }

        internal static string OpName(string op) => op switch
        {
            "L" => "list dogs", "C" => "command", "P" => "pet", "N" => "rename", "S" => "upgrade skill", "R" => "release",
            "F" => "feed", "A" => "first aid", "G" => "set gear", "U" => "update saddlebag", _ => op
        };

        static GOAPAgent_Pet FindDog(string ownerId, string handle)
        {
            foreach (var d in DogShareBehaviour.Dogs())
            {
                var n = d.npcData;
                if (n == null || n.leaderCharacterGUID != ownerId) continue;
                if (Proto.Short(n.npcGUID) == handle) return d;
            }
            return null;
        }

        static readonly HashSet<string> Commands = new HashSet<string> { "Follow", "Stay", "Passive", "Defensive", "Aggressive" };

        static string Command(GOAPAgent_Pet dog, string cmd)
        {
            if (!Commands.Contains(cmd)) { DLog.Warn("req", $"  unknown command '{cmd}' ignored"); return null; }
            dog.ExecutePetCommand(cmd);
            var pd = dog.npcData?.petData;
            DLog.Info("req", $"  now: {(pd == null ? "?" : (pd.isStayCommand ? "Stay" : "Follow") + ", combat mode " + pd.combatMode)}");
            Changed(dog);
            return null;
        }

        static float Now()
        {
            try { return GameController.instance.gameData.inGameTime; } catch { return -1f; }
        }

        const float PetCooldownDays = 1f / 144f;   // the game's own check: 10 in-game minutes

        static bool PetReady(GOAPAgent_Pet dog)
        {
            var pd = dog.npcData?.petData;
            float now = Now();
            return pd != null && now >= 0f && now - pd.lastPetInGameTime >= PetCooldownDays;
        }

        static string Pet(GOAPAgent_Pet dog)
        {
            var pc = dog.pet; var pd = dog.npcData?.petData;
            if (pc == null || pd == null) return null;
            if (pd.isUnconscious) return "Your dog is unconscious.";
            if (!PetReady(dog)) return "Your dog was petted a moment ago. Try again in a few minutes.";
            pd.lastPetInGameTime = Now();
            float before = pd.affectionXp;
            pc.AddAffection(PetSystem.affectionPet);
            DLog.Info("req", $"  affection {before:0.#} -> {pd.affectionXp:0.#} (level {pd.affectionLevel})");
            try { pc.PlayPetAnim("Dog_SitAlert", 2.5f); } catch (Exception e) { DLog.Error("req", "  petting animation failed", e); }   // also sent to guests
            try { pc.PlayHappy(); } catch { }                                                                                         // sound, also sent to guests
            Changed(dog);
            return "Good dog!";
        }

        static string Rename(GOAPAgent_Pet dog, string name)
        {
            var cd = dog.npcData?.characterData;
            if (cd == null || string.IsNullOrEmpty(name)) return null;
            cd.characterName = name;
            try { PetOverheadUI.NotifyAdd(dog.npcData); } catch { }
            Changed(dog);
            return "Renamed to " + name + ".";
        }

        static string Skill(GOAPAgent_Pet dog, string idText, string ownerId)
        {
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return null;
            var pd = dog.npcData?.petData;
            var mgr = PetSkillAttrManager.instance;
            if (pd == null || mgr == null) return null;
            var attr = mgr.Get(id);
            if (attr == null) return null;
            CharacterData owner = null;
            foreach (var g in DogShareBehaviour.Guests())
                if (DogShareBehaviour.IdOf(g) == ownerId) { owner = g.characterData; break; }
            int reason = 0;
            if (!mgr.CanLearn(pd, attr, owner, out reason))
            {
                string why = null;
                try { if (reason != 0) why = TextManager.instance.GetText(reason); } catch { }
                return string.IsNullOrEmpty(why) ? "Can't upgrade that skill yet." : why;
            }
            if (!pd.TryUpgradeSkill(id, mgr.GetMaxLevel(id))) return "Can't upgrade that skill yet.";
            try { PetSystem.EnsurePouchSize(pd); } catch { }
            Changed(dog);
            string skill = null;
            try { skill = attr.SkillName; } catch { }
            return (string.IsNullOrEmpty(skill) ? "Skill" : skill) + " is now level " + pd.GetSkillLevel(id) + ".";
        }

        static string Release(GOAPAgent_Pet dog)
        {
            var n = dog.npcData;
            if (n == null) return null;
            string name = n.characterData?.characterName;
            try { PetPanel.NotifyPetRemoved(n); } catch { }
            return PetSystem.ReleasePet(n) ? (string.IsNullOrEmpty(name) ? "Released." : name + " was released.") : "Couldn't release that dog.";
        }

        static string Feed(GOAPAgent_Pet dog, byte[] blob)
        {
            var pc = dog.pet; var item = Proto.ItemFrom(blob);
            if (pc == null || item == null) { DLog.Error("item", $"  feed: couldn't read the food item ({blob?.Length ?? 0} bytes)"); return null; }
            var pd = dog.npcData?.petData;
            float before = pd?.affectionXp ?? 0f;
            float hBefore = dog.npcData?.hunger ?? 0f;
            pc.EatFood(item, PetSystem.affectionFeed, true);
            DLog.Info("item", $"  fed item {item.itemId}; affection {before:0.#} -> {pd?.affectionXp ?? 0f:0.#}, hunger {hBefore:0} -> {dog.npcData?.hunger ?? 0f:0}");
            Changed(dog);
            string name = dog.npcData?.characterData?.characterName;
            return (string.IsNullOrEmpty(name) ? "Your dog" : name) + " ate it.";
        }

        static string FirstAid(GOAPAgent_Pet dog, string idText)
        {
            var pc = dog.pet; var pd = dog.npcData?.petData;
            if (pc == null || pd == null || !int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return null;
            float pct = PetSystem.GetFirstAidHealPercent(id);
            bool wasOut = pd.isUnconscious;
            pc.HealByFirstAid(pct);
            if (pd.isUnconscious) { try { pc.WakeUp(pct, true); } catch (Exception e) { DLog.Error("item", "  WakeUp failed", e); } }
            DLog.Info("item", $"  first aid with item {id}: heal {pct:0.##}, {(wasOut ? (pd.isUnconscious ? "still unconscious" : "woke up") : "was awake")}");
            Changed(dog);
            return null;
        }

        // The guest's game already moved the item between their backpack and their copy of the slot;
        // the real dog's slot just becomes what they sent.
        static string SetGear(GOAPAgent_Pet dog, string slot, byte[] blob, string charId)
        {
            var pc = dog.pet; var pd = dog.npcData?.petData;
            if (pc == null || pd == null) return null;
            var item = Proto.ItemFrom(blob);
            if (blob != null && blob.Length > 0 && item == null) { DLog.Error("item", $"  set gear {slot}: couldn't read the item ({blob.Length} bytes) - slot left unchanged"); return null; }
            var oldItem = GearItem(pd, slot);
            DLog.Info("item", $"  gear slot {SlotName(slot)}: {(oldItem == null ? "empty" : "item " + oldItem.itemId)} -> {(item == null ? "empty" : "item " + item.itemId)}");
            switch (slot)
            {
                case "c": pd.collarItem = item; break;
                case "v": pd.vestItem = item; break;
                case "p": pd.pouchItem = item; break;
                default: return null;
            }
            try { PetSystem.EnsurePouchSize(pd); } catch { }
            try { pc.RefreshProperties(); } catch { }
            try { pc.RefreshGearVisual(); } catch { }
            Changed(dog);
            RememberGear(charId, dog.npcData.npcGUID, slot, Proto.ItemBytes(GearItem(pd, slot)));
            return null;
        }

        static string SetPouch(GOAPAgent_Pet dog, byte[] blob, string charId)
        {
            var pd = dog.npcData?.petData;
            var inv = pd?.pouchItem?.inventoryData;
            if (inv == null || blob == null) { DLog.Warn("item", "  saddlebag update ignored: the dog has no saddlebag here"); return null; }
            var incoming = NetContainerSync.DeserializeInventory(blob);
            if (incoming == null) { DLog.Error("item", $"  saddlebag: couldn't read the contents ({blob.Length} bytes) - left unchanged"); return null; }
            NetItemSync.ReplaceInventoryContents(inv, incoming);
            DLog.Info("item", $"  saddlebag contents replaced ({blob.Length} bytes, {CountItems(inv)} item stack(s) now)");
            RememberGear(charId, dog.npcData.npcGUID, "p", Proto.ItemBytes(pd.pouchItem));
            return null;
        }

        internal static string SlotName(string slot) => slot == "c" ? "collar" : slot == "v" ? "vest" : slot == "p" ? "saddlebag" : slot;

        internal static int CountItems(InventoryData inv)
        {
            try { var l = inv?.itemList; return l == null ? 0 : l.Count; } catch { return -1; }
        }

        internal static ItemData GearItem(PetData pd, string slot) =>
            slot == "c" ? pd.collarItem : slot == "v" ? pd.vestItem : slot == "p" ? pd.pouchItem : null;

        // What each guest's copy of each gear slot currently is, so we only resend changes.
        static readonly Dictionary<string, string> _sentGear = new Dictionary<string, string>();
        static void ForgetGear(string charId)
        {
            foreach (var k in new List<string>(_sentGear.Keys)) if (k.StartsWith(charId + "|", StringComparison.Ordinal)) _sentGear.Remove(k);
        }
        static void RememberGear(string charId, string guid, string slot, byte[] bytes) => _sentGear[charId + "|" + guid + "|" + slot] = Proto.Hash(bytes);

        static void SendGear(NetPeer peer, string charId, string to, GOAPAgent_Pet d)
        {
            var n = d.npcData; var pd = n.petData;
            foreach (var slot in new[] { "c", "v", "p" })
            {
                byte[] bytes;
                try { bytes = Proto.ItemBytes(GearItem(pd, slot)); } catch (Exception e) { DLog.Error("send", "Couldn't read gear slot " + SlotName(slot), e); continue; }
                string key = charId + "|" + n.npcGUID + "|" + slot, h = Proto.Hash(bytes);
                if (_sentGear.TryGetValue(key, out var old) && old == h) continue;
                _sentGear[key] = h;
                var it = GearItem(pd, slot);
                DLog.Info("send", $"Gear copy to {PeerName(charId)}: '{n.characterData?.characterName}' {SlotName(slot)} = {(it == null ? "empty" : "item " + it.itemId)} ({bytes.Length} bytes)");
                Proto.SendBlob(t => Reply(peer, t), bytes, "B", to, Proto.Short(n.npcGUID), slot);
            }
        }

        static void Changed(GOAPAgent_Pet dog)
        {
            try { PetPanel.NotifyPetChanged(dog.npcData); } catch { }
        }

        internal static void SendList(NetPeer peer, string charId)
        {
            string to = Proto.Short(charId);
            int seq = ++_seq;
            var mine = DogShareBehaviour.Dogs().FindAll(d => d.npcData != null && d.npcData.leaderCharacterGUID == charId);
            var mgr = PetSkillAttrManager.instance;
            foreach (var d in mine)
            {
                var n = d.npcData; var pd = n.petData;
                if (pd == null) continue;
                string h = Proto.Short(n.npcGUID);
                int netId = -1;
                try { var sync = NetMonsterSync.instance; if (sync != null && d.pet != null) sync.HostTryGetNetId(d.pet, out netId); } catch (Exception e) { DLog.Error("send", "net id lookup failed", e); }
                if (netId < 0) DLog.Throttle("nonet|" + n.npcGUID, 60f, "send", $"Dog '{n.characterData?.characterName}' has no network id yet - the guest can't see it until it's near them");
                Reply(peer, Proto.Join("D", to, seq, mine.Count, h, n.npcGUID, netId,
                    Proto.Clean(n.characterData?.characterName, 20), pd.breedId, pd.affectionLevel,
                    pd.affectionXp.ToString("0.##", CultureInfo.InvariantCulture),
                    pd.availableSkillPoints, pd.skillPointsSpent, pd.isStayCommand ? 1 : 0, pd.combatMode,
                    pd.lastPetInGameTime.ToString("0.#####", CultureInfo.InvariantCulture), pd.isUnconscious ? 1 : 0,
                    n.hunger.ToString("0.#", CultureInfo.InvariantCulture)));

                var sb = new StringBuilder();
                if (mgr != null)
                {
                    foreach (var attr in mgr.GetAll())
                    {
                        if (attr == null) continue;
                        int id = attr.petSkillId;
                        if (sb.Length > 0) sb.Append(';');
                        sb.Append(id).Append(':').Append(pd.GetSkillLevel(id)).Append(':')
                          .Append(mgr.GetMaxLevel(id));
                    }
                }
                Reply(peer, Proto.Join("K", to, seq, h, sb.ToString()));
                SendGear(peer, charId, to, d);
            }
            Reply(peer, Proto.Join("E", to, seq));
            var names = new List<string>();
            foreach (var d in mine) names.Add(d.npcData?.characterData?.characterName ?? "?");
            DLog.Change("list|" + charId, "send", $"{PeerName(charId)} owns {mine.Count} dog(s) here: [{string.Join(", ", names)}]");
        }

        static bool _sendToBroken;
        internal static void Reply(NetPeer peer, string text)
        {
            if (text.Length > 200) text = text.Substring(0, 200);
            if (!_sendToBroken)
            {
                try
                {
                    var msg = new MsgChat { senderId = 0, senderName = "", text = text };
                    NetworkManager.instance.SendTo(peer, msg, DeliveryMethod.ReliableOrdered, 0);
                    DLog.Once("sendto", "send", "Direct replies to guests work (NetworkManager.SendTo)");
                    return;
                }
                catch (Exception e)
                {
                    _sendToBroken = true;
                    DLog.Error("send", "Direct reply to a guest failed - falling back to chat broadcast (guests without DogShare would see these lines)", e);
                }
            }
            // Fallback: normal chat broadcast. Guests with DogShare hide it; guests without it would see the line.
            try { NetChatSync.instance.SendChat(text); } catch (Exception e) { DLog.Error("send", "Reply failed", e); }
        }
    }
}
