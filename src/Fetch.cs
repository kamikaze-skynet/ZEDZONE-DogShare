using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Fetch (tennis ball) for dogs a guest owns.
    //
    // Why vanilla fails in co-op:
    //  * A guest's ball flies on the guest's screen; when it lands the guest asks the host to drop it
    //    (NetItemSync.ClientInterceptDrop -> host OnHostReceiveDropRequest -> DropItem). The host only
    //    shows the throw as a visual (NetThrowSync.SpawnRemoteThrow sets netVisualOnly), so nobody ever
    //    calls PetSystem.NotifyToyThrown on the host - the guest's dog never hears about the ball.
    //  * If a dog does fetch for a guest, GOAPAction_Pet_FetchToy.Deliver hands the ball to its leader with
    //    GameController.TryAddItemToCharacter. On the host the leader is the guest's stand-in, whose
    //    inventory isn't the guest's real backpack, so the ball would vanish.
    //
    // Fix (host):
    //  1. Remember pet-toy throws reported by each guest (OnHostReceiveThrow).
    //  2. When that guest's drop request spawns the ball (HostRegisterGroundItem during OnHostReceiveDropRequest),
    //     call PetSystem.NotifyToyThrown with the guest's stand-in as thrower, next frame - exactly what
    //     PetToyThrowable.OnTweenOver does for the host's own throws. Only dogs whose leader is that guest react.
    //  3. While Deliver runs for a guest's dog, turn the hand-over into an "I" message carrying the ball to that guest.
    // Fix (guest): "I" -> put the ball in my backpack (or drop it at my feet if the backpack is full).
    internal static class Fetch
    {
        struct Throw { public float Time; public int ItemId; public Vector2 End; }
        static readonly Dictionary<int, Throw> _throws = new Dictionary<int, Throw>();
        static NetPeer _dropPeer;
        struct Pending { public BasicItem Item; public int PlayerId; public float Time; }
        static readonly List<Pending> _pending = new List<Pending>();
        internal static GOAPAgent_Pet Delivering;

        const float ThrowWindow = 8f;   // seconds between "throw" and "drop request" for the same guest

        internal static bool IsToy(int itemId)
        {
            try { return PetSystem.IsPetToy(itemId); } catch { return false; }
        }

        // ---------------------------------------------------------------- host
        internal static void HostOnThrow(MsgThrow msg, NetPeer peer)
        {
            if (msg == null || peer == null || !IsToy(msg.sourceItemId)) return;
            int pid = -1;
            try { pid = NetworkManager.instance.GetPeerPlayerId(peer); } catch (Exception e) { DLog.ErrorThrottled("fetchpid", 60f, "act", "Fetch: couldn't tell which guest threw", e); }
            if (pid < 0) return;
            _throws[pid] = new Throw { Time = Time.unscaledTime, ItemId = msg.sourceItemId, End = new Vector2(msg.endX, msg.endY) };
            DLog.Info("act", $"Fetch: {GuestName(pid)} threw a pet toy (item {msg.sourceItemId}) - waiting for it to land");
        }

        internal static void HostBeginDrop(NetPeer peer) => _dropPeer = peer;
        internal static void HostEndDrop() => _dropPeer = null;

        internal static void HostOnGroundItem(BasicItem item)
        {
            if (_dropPeer == null || item == null) return;
            ItemData data = null;
            try { data = item.itemData; } catch { }
            if (data == null || !IsToy(data.itemId)) return;
            int pid = -1;
            try { pid = NetworkManager.instance.GetPeerPlayerId(_dropPeer); } catch { }
            if (pid < 0 || !_throws.TryGetValue(pid, out var t)) return;
            if (Time.unscaledTime - t.Time > ThrowWindow)
            {
                DLog.Debug("act", $"Fetch: {GuestName(pid)} dropped a pet toy long after their last throw - treated as a normal drop");
                return;
            }
            _throws.Remove(pid);
            _pending.Add(new Pending { Item = item, PlayerId = pid, Time = Time.unscaledTime });
        }

        // Called every frame from DogShareBehaviour (host only).
        internal static void HostTick()
        {
            if (_pending.Count == 0) return;
            var list = new List<Pending>(_pending);
            _pending.Clear();
            foreach (var p in list)
            {
                try { NotifyDogs(p); }
                catch (Exception e) { DLog.Error("act", "Fetch: telling the dogs about the ball failed", e); }
            }
        }

        static void NotifyDogs(Pending p)
        {
            if (p.Item == null) { DLog.Warn("act", "Fetch: the ball was gone before the dogs could see it"); return; }
            var data = p.Item.itemData;
            if (data == null) return;
            HumanCharacterController guest = null;
            var sync = NetPlayerSync.instance;
            if (sync == null || !sync.TryGetRemoteController(p.PlayerId, out guest) || guest == null)
            {
                DLog.Warn("act", $"Fetch: couldn't find {GuestName(p.PlayerId)}'s character - no fetch");
                return;
            }
            string gid = DogShareBehaviour.IdOf(guest);
            var theirs = DogShareBehaviour.Dogs().FindAll(d => d.npcData != null && d.npcData.leaderCharacterGUID == gid);
            if (theirs.Count == 0) { DLog.Debug("act", $"Fetch: {GuestName(p.PlayerId)} threw a ball but owns no dog here"); return; }

            PetSystem.NotifyToyThrown(guest, data, p.Item);

            GOAPAgent_Pet taker = null;
            foreach (var d in theirs)
            {
                try { if (d.pendingToyItem != null && d.pendingToyItem.Pointer == data.Pointer) { taker = d; break; } } catch { }
            }
            if (taker != null)
                DLog.Info("act", $"Fetch: '{taker.npcData?.characterData?.characterName}' is going for {GuestName(p.PlayerId)}'s ball");
            else
            {
                var why = new List<string>();
                foreach (var d in theirs)
                {
                    var pd = d.npcData?.petData;
                    string n = d.npcData?.characterData?.characterName ?? "?";
                    if (pd == null) { why.Add(n + ": no pet data"); continue; }
                    if (pd.isStayCommand) why.Add(n + ": told to stay");
                    else if (pd.isUnconscious) why.Add(n + ": knocked out");
                    else if (pd.holdingItem != null) why.Add(n + ": already holding something");
                    else if (d.leaderCharacter == null) why.Add(n + ": has no leader here");
                    else why.Add(n + ": too far / busy / ball claimed");
                }
                DLog.Info("act", $"Fetch: none of {GuestName(p.PlayerId)}'s dogs went for the ball ({string.Join("; ", why)})");
            }
        }

        // Host: is this character a guest's stand-in? (the remote player's character on the host)
        internal static bool TryGuest(BasicCharacterController who, out int pid, out HumanCharacterController remote)
        {
            pid = -1; remote = null;
            if (who == null || !NetworkManager.isOnline || NetworkManager.isClient) return false;
            var sync = NetPlayerSync.instance;
            if (sync == null || !sync.TryGetPlayerIdOf(who, out pid)) return false;
            return sync.TryGetRemoteController(pid, out remote) && remote != null && remote.Pointer == who.Pointer;
        }

        internal static NetPeer PeerOf(int pid, HumanCharacterController remote)
        {
            NetPeer peer = null;
            try { peer = NetworkManager.instance.GetPeerByPlayerId(pid); } catch { }
            return peer ?? AnimSync.PeerOf(DogShareBehaviour.IdOf(remote));
        }

        // Inside Deliver: the dog hands the ball to its leader. For a guest leader, send it to them instead.
        internal static bool HostRedirectHandOver(BasicCharacterController who, ItemData item)
        {
            if (Delivering == null || item == null || who == null) return false;
            if (!TryGuest(who, out int pid, out var remote)) return false;

            var peer = PeerOf(pid, remote);
            string gid = DogShareBehaviour.IdOf(remote);
            string dog = Delivering.npcData?.characterData?.characterName ?? "?";
            if (peer == null)
            {
                DLog.Warn("act", $"Fetch: '{dog}' brought the ball back but {GuestName(pid)} isn't reachable - dropping it at their feet");
                return DropAt(remote, item);
            }
            var bytes = Proto.ItemBytes(item);
            if (bytes.Length == 0) { DLog.Warn("act", "Fetch: couldn't pack the ball - dropping it at the guest's feet"); return DropAt(remote, item); }
            string to = Proto.Short(gid);
            Proto.SendBlob(t => HostHandler.Reply(peer, t), bytes, "I", to, Proto.Clean(dog, 20));
            DLog.Info("act", $"Fetch: '{dog}' brought the ball back to {GuestName(pid)} - sent to their backpack ({bytes.Length} bytes)");
            return true;
        }

        // ---------------------------------------------------------------- gifts ("Bear brought you a dead crow")
        // GOAPAction_Pet_DeliverGift.DropGift drops the gift at the leader's feet (a normal ground item everyone
        // sees) and shows "<dog> brought you <item>" with SystemNotificationPanel - on the host's screen only.
        // For a guest's dog: keep that notice off the host's screen and send it to the guest instead.
        class Gift { public int Pid; public HumanCharacterController Remote; public string Dog; public int ItemId = -1; public string ItemName; }
        internal static object Gifting;   // non-null while DropGift runs for a guest's dog

        internal static void HostGiftBegin(GOAPAgent_Pet agent)
        {
            Gifting = null;
            if (agent == null) return;
            BasicCharacterController leader = null;
            try { leader = agent.leaderCharacter; } catch { }
            if (!TryGuest(leader, out int pid, out var remote)) return;
            var g = new Gift { Pid = pid, Remote = remote, Dog = agent.npcData?.characterData?.characterName ?? "" };
            try
            {
                var item = agent.pet?.holdingItem;
                if (item != null) { g.ItemId = item.itemId; g.ItemName = item.GetItemName(); }
            }
            catch (Exception e) { DLog.ErrorThrottled("giftitem", 60f, "act", "Gift: couldn't read what the dog is carrying", e); }
            Gifting = g;
        }

        internal static void HostGiftEnd()
        {
            var g = Gifting as Gift;
            Gifting = null;
            if (g == null) return;
            var peer = PeerOf(g.Pid, g.Remote);
            string who = GuestName(g.Pid);
            if (peer == null) { DLog.Warn("act", $"Gift: '{g.Dog}' brought {who} {g.ItemName} but they aren't reachable to tell them"); return; }
            HostHandler.Reply(peer, Proto.Join("T", Proto.Short(DogShareBehaviour.IdOf(g.Remote)), Proto.Clean(g.Dog, 20), g.ItemId, Proto.Clean(g.ItemName, 40)));
            DLog.Info("act", $"Gift: '{g.Dog}' brought {who} {g.ItemName} (item {g.ItemId}) - dropped at their feet, told them");
        }

        static bool DropAt(BasicCharacterController who, ItemData item)
        {
            try
            {
                var pos = (Vector2)who.transform.position;
                var go = GameController.instance.DropItemByPlayer(item, pos, 0f, 0f, 0, -128);
                return go != null;
            }
            catch (Exception e) { DLog.Error("act", "Fetch: dropping the ball failed", e); return false; }
        }

        static string GuestName(int pid)
        {
            try
            {
                HumanCharacterController c = null;
                var sync = NetPlayerSync.instance;
                if (sync != null && sync.TryGetRemoteController(pid, out c) && c != null) return DogShareBehaviour.GuestName(c);
            }
            catch { }
            return "player " + pid;
        }

        // ---------------------------------------------------------------- guest
        internal static void GuestOnBall(string[] p)
        {
            // I|to|dogName|xfer|i|n|chunk
            if (p.Length < 7) return;
            if (!Proto.Collect("host", p, out var bytes)) return;
            string dog = p[2];
            var item = Proto.ItemFrom(bytes);
            if (item == null) { DLog.Warn("item", $"Fetch: '{dog}' brought something back but it couldn't be read"); return; }
            var me = DogShareBehaviour.HostPlayer();   // on a guest this is my own character
            if (me == null) { DLog.Warn("item", "Fetch: got the ball back but my character isn't loaded"); return; }
            bool ok = false;
            try { ok = GameController.instance.TryAddItemToCharacter(me, item, true, true); }
            catch (Exception e) { DLog.Error("item", "Fetch: putting the ball in my backpack failed", e); }
            if (ok) { DLog.Info("item", $"Fetch: '{dog}' brought the ball back - it's in my backpack"); return; }
            try
            {
                GameController.instance.DropItemByPlayer(item, (Vector2)me.transform.position, 0f, 0f, 0, -128);
                DLog.Info("item", $"Fetch: '{dog}' brought the ball back - backpack full, dropped at my feet");
            }
            catch (Exception e) { DLog.Error("item", "Fetch: dropping the returned ball failed", e); }
        }

        // T|to|dogName|itemId|itemName(host's language)
        internal static void GuestOnGift(string[] p)
        {
            if (p.Length < 5) return;
            string dog = p[2], name = p[4];
            int id = int.TryParse(p[3], out var v) ? v : -1;
            try { if (id >= 0) { var a = ItemManager.instance?.GetItemAttrById(id); if (a != null && !string.IsNullOrEmpty(a.ItemName)) name = a.ItemName; } } catch { }
            string text = null;
            try
            {
                var fmt = TextManager.instance?.GetText(0x2b02);   // the game's "{0} brought you {1}" line
                if (!string.IsNullOrEmpty(fmt) && fmt.Contains("{0}")) text = string.Format(fmt, dog, name);
            }
            catch (Exception e) { DLog.ErrorThrottled("gifttext", 60f, "guest", "Gift: couldn't read the game's gift text - using English", e); }
            if (string.IsNullOrEmpty(text)) text = $"{dog} brought you {name}";
            try { SystemNotificationPanel.ShowNotificationSafe(text, true); }
            catch (Exception e) { DLog.Error("guest", "Gift: showing the notice failed", e); }
            DLog.Info("guest", $"Gift: {text}");
        }

        internal static void GuestOnThrow(HumanCharacterController who, ItemData item)
        {
            if (!NetworkManager.isOnline || !NetworkManager.isClient || item == null || who == null) return;
            if (!IsToy(item.itemId)) return;
            bool mine = false; try { mine = who.isPlayer && !who.isNetPuppet; } catch { }
            if (!mine) return;
            DLog.Info("act", $"Fetch: I threw a pet toy (item {item.itemId}); {GuestMirror.Count} shared dog(s) here - the host tells them when it lands");
        }
    }

    [HarmonyPatch]
    internal static class FetchPatches
    {
        [HarmonyPatch(typeof(NetThrowSync), nameof(NetThrowSync.OnHostReceiveThrow))]
        [HarmonyPrefix]
        static void HostThrow(MsgThrow __0, NetPeer __1)
        {
            try { Fetch.HostOnThrow(__0, __1); } catch (Exception e) { DLog.ErrorThrottled("fetchthrow", 60f, "act", "Fetch: reading a guest's throw failed", e); }
        }

        [HarmonyPatch(typeof(NetItemSync), nameof(NetItemSync.OnHostReceiveDropRequest))]
        [HarmonyPrefix]
        static void DropBegin(NetPeer __1) => Fetch.HostBeginDrop(__1);

        [HarmonyPatch(typeof(NetItemSync), nameof(NetItemSync.OnHostReceiveDropRequest))]
        [HarmonyFinalizer]
        static Exception DropEnd(Exception __exception) { Fetch.HostEndDrop(); return __exception; }

        [HarmonyPatch(typeof(NetItemSync), nameof(NetItemSync.HostRegisterGroundItem))]
        [HarmonyPostfix]
        static void GroundItem(BasicItem __0)
        {
            try { Fetch.HostOnGroundItem(__0); } catch (Exception e) { DLog.ErrorThrottled("fetchground", 60f, "act", "Fetch: checking a dropped ball failed", e); }
        }

        [HarmonyPatch(typeof(GOAPAction_Pet_FetchToy), nameof(GOAPAction_Pet_FetchToy.Deliver))]
        [HarmonyPrefix]
        static void DeliverBegin(GOAPAgent_Pet __0) => Fetch.Delivering = __0;

        [HarmonyPatch(typeof(GOAPAction_Pet_FetchToy), nameof(GOAPAction_Pet_FetchToy.Deliver))]
        [HarmonyFinalizer]
        static Exception DeliverEnd(Exception __exception) { Fetch.Delivering = null; return __exception; }

        [HarmonyPatch(typeof(GameController), nameof(GameController.TryAddItemToCharacter))]
        [HarmonyPrefix]
        static bool HandOver(BasicCharacterController __0, ItemData __1, ref bool __result)
        {
            if (Fetch.Delivering == null) return true;
            try
            {
                if (Fetch.HostRedirectHandOver(__0, __1)) { __result = true; return false; }
            }
            catch (Exception e) { DLog.Error("act", "Fetch: handing the ball to the guest failed", e); }
            return true;
        }

        [HarmonyPatch(typeof(GOAPAction_Pet_DeliverGift), nameof(GOAPAction_Pet_DeliverGift.DropGift))]
        [HarmonyPrefix]
        static void GiftBegin(GOAPAgent_Pet __0)
        {
            try { Fetch.HostGiftBegin(__0); } catch (Exception e) { DLog.ErrorThrottled("giftbegin", 60f, "act", "Gift: checking the dog's owner failed", e); }
        }

        [HarmonyPatch(typeof(GOAPAction_Pet_DeliverGift), nameof(GOAPAction_Pet_DeliverGift.DropGift))]
        [HarmonyFinalizer]
        static Exception GiftEnd(Exception __exception)
        {
            try { Fetch.HostGiftEnd(); } catch (Exception e) { DLog.Error("act", "Gift: telling the guest failed", e); }
            return __exception;
        }

        // The host shouldn't get "<dog> brought you ..." for a guest's dog.
        [HarmonyPatch(typeof(SystemNotificationPanel), nameof(SystemNotificationPanel.ShowNotification))]
        [HarmonyPrefix]
        static bool HostNotice() => Fetch.Gifting == null;

        [HarmonyPatch(typeof(HumanCharacterController), nameof(HumanCharacterController.ThrowItem))]
        [HarmonyPrefix]
        static void GuestThrow(HumanCharacterController __instance, ItemData __0)
        {
            try { Fetch.GuestOnThrow(__instance, __0); } catch { }
        }
    }
}
