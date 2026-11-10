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
    // Host -> guest:  D|to|seq|count|dog|npcGuid|netId|name|breed|level|xp|points|spent|stay|combat|lastPet|unconscious
    //                 K|to|seq|dog|id:level:max;...
    //                 E|to|seq             end of list
    //                 M|to|text            message to show
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
    }

    [HarmonyPatch]
    internal static class ChatPatches
    {
        // Hide marked lines everywhere (local echo, relays, speech bubbles).
        [HarmonyPatch(typeof(NetChatSync), nameof(NetChatSync.Append))]
        [HarmonyPrefix]
        static bool Append(string text) => !Proto.Is(text);

        // Host: requests from guests.
        [HarmonyPatch(typeof(NetChatSync), nameof(NetChatSync.OnHostReceive))]
        [HarmonyPrefix]
        static bool OnHostReceive(MsgChat msg, NetPeer peer)
        {
            try
            {
                var text = msg?.text;
                if (!Proto.Is(text)) return true;
                HostHandler.Handle(text, peer);
            }
            catch (Exception e) { Plugin.L.LogError("host request: " + e); }
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
                GuestMirror.OnHostMessage(text);
            }
            catch (Exception e) { Plugin.L.LogError("host reply: " + e); }
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
                Plugin.L.LogWarning("DogShare request from a peer with no character yet.");
                return;
            }
            var p = Proto.Split(text);
            string op = p[0];
            string to = Proto.Short(charId);

            if (op == "L") { SendList(peer, charId); return; }
            if (p.Length < 2) return;

            var dog = FindDog(charId, p[1]);
            if (dog == null) { Reply(peer, Proto.Join("M", to, "That dog isn't yours any more.")); SendList(peer, charId); return; }

            string note = null;
            switch (op)
            {
                case "C": note = Command(dog, p.Length > 2 ? p[2] : ""); break;
                case "P": note = Pet(dog); break;
                case "N": note = Rename(dog, p.Length > 2 ? Proto.Clean(p[2], 20) : ""); break;
                case "S": note = Skill(dog, p.Length > 2 ? p[2] : "", charId); break;
                case "R": note = Release(dog); break;
            }
            if (!string.IsNullOrEmpty(note)) Reply(peer, Proto.Join("M", to, Proto.Clean(note, 150)));
            SendList(peer, charId);
        }

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
            if (!Commands.Contains(cmd)) return null;
            dog.ExecutePetCommand(cmd);
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
            pc.AddAffection(PetSystem.affectionPet);
            try { pc.PlayHappy(); } catch { }
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
                try { var sync = NetMonsterSync.instance; if (sync != null && d.pet != null) sync.HostTryGetNetId(d.pet, out netId); } catch { }
                Reply(peer, Proto.Join("D", to, seq, mine.Count, h, n.npcGUID, netId,
                    Proto.Clean(n.characterData?.characterName, 20), pd.breedId, pd.affectionLevel,
                    pd.affectionXp.ToString("0.##", CultureInfo.InvariantCulture),
                    pd.availableSkillPoints, pd.skillPointsSpent, pd.isStayCommand ? 1 : 0, pd.combatMode,
                    pd.lastPetInGameTime.ToString("0.#####", CultureInfo.InvariantCulture), pd.isUnconscious ? 1 : 0));

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
            }
            Reply(peer, Proto.Join("E", to, seq));
        }

        static bool _sendToBroken;
        static void Reply(NetPeer peer, string text)
        {
            if (text.Length > 200) text = text.Substring(0, 200);
            if (!_sendToBroken)
            {
                try
                {
                    var msg = new MsgChat { senderId = 0, senderName = "", text = text };
                    NetworkManager.instance.SendTo(peer, msg, DeliveryMethod.ReliableOrdered, 0);
                    return;
                }
                catch (Exception e)
                {
                    _sendToBroken = true;
                    Plugin.L.LogWarning("Direct reply failed, using chat broadcast instead: " + e.Message);
                }
            }
            // Fallback: normal chat broadcast. Guests with DogShare hide it; guests without it would see the line.
            try { NetChatSync.instance.SendChat(text); } catch (Exception e) { Plugin.L.LogError("reply: " + e.Message); }
        }
    }
}
