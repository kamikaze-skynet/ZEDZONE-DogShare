using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // The game doesn't sync petting / feeding animations at all: PettingRoutine / FeedingRoutine play the dog's
    // PlayPetAnim ("Dog_SitAlert", "Dog_Eat") and the player's SetPetDogVisual pose only on the PC doing it.
    // DogShare syncs both for players who have it:
    //   host -> guests  Y|*|netId|anim|seconds   a dog played an animation (any dog the host simulates)
    //   host -> guests  Z|*|charId8|1/0           a player started / stopped the petting pose
    //   guest -> host   V|1/0                     I started / stopped the petting pose
    //   host -> guests  S|*|netId|sound|arg       a dog made a sound (bark, whine, soft, happy)
    internal static class AnimSync
    {
        internal static bool Applying;   // set while we apply a remote animation, so we don't echo it back

        // ---------------------------------------------------------------- host
        static readonly Dictionary<string, NetPeer> _peers = new Dictionary<string, NetPeer>();
        internal static void RememberPeer(string charId, NetPeer peer) { if (!string.IsNullOrEmpty(charId) && peer != null) _peers[charId] = peer; }

        internal static void BroadcastToGuests(string text, string exceptCharId = null)
        {
            foreach (var kv in new List<KeyValuePair<string, NetPeer>>(_peers))
            {
                if (kv.Key == exceptCharId) continue;
                try
                {
                    var msg = new MsgChat { senderId = 0, senderName = "", text = text };
                    NetworkManager.instance.SendTo(kv.Value, msg, DeliveryMethod.ReliableOrdered, 0);
                }
                catch (Exception e)
                {
                    _peers.Remove(kv.Key);
                    DLog.Info("send", $"Stopped sending animations to {DLog.S(kv.Key)} (left?): {e.Message}");
                }
            }
        }

        internal static bool IsHost()
        {
            try { return NetworkManager.isOnline && !NetworkManager.isClient; } catch { return false; }
        }

        // Guest told us they started / stopped petting: show it on our copy of them and tell the others.
        internal static void HostOnGuestPose(string charId, bool on)
        {
            var g = DogShareBehaviour.GuestById(charId);
            if (g != null) ApplyPose(g, on);
            BroadcastToGuests(Proto.Join("Z", "*", Proto.Short(charId), on ? 1 : 0), charId);
            DLog.Debug("send", $"{(g == null ? DLog.S(charId) : DogShareBehaviour.GuestName(g))} {(on ? "started" : "stopped")} petting - shown here and sent to other guests");
        }

        // ---------------------------------------------------------------- everyone
        internal static void ApplyPose(HumanCharacterController h, bool on)
        {
            if (h == null) return;
            Applying = true;
            try { h.SetPetDogVisual(on); }
            catch (Exception e) { DLog.Throttle("poseerr", 30f, "error", "Showing the petting pose failed: " + e.Message); }
            finally { Applying = false; }
        }

        internal static void ApplyDogAnim(PetCharacterController pc, string anim, float seconds)
        {
            if (pc == null) return;
            Applying = true;
            var saved = PromptFlags.Clear(pc, "animation");   // PlayPetAnim may skip stand-ins too
            try { pc.PlayPetAnim(anim, seconds); }
            catch (Exception e) { DLog.Throttle("animerr", 30f, "error", "Playing the dog animation failed: " + e.Message); }
            finally { PromptFlags.Restore(pc, saved); Applying = false; }
        }

        // ---------------------------------------------------------------- guest receiving
        internal static void GuestOnMessage(string[] p)
        {
            if (p[0] == "Y" && p.Length >= 5)
            {
                int netId = Int(p[2]);
                var c = NetMonsterSync.instance?.ClientGetPuppet(netId)?.TryCast<PetCharacterController>();
                if (c == null) { DLog.Debug("guest", $"Dog animation {p[3]} for net id {netId}, but that dog isn't on this PC"); return; }
                ApplyDogAnim(c, p[3], Flt(p[4]));
                DLog.Debug("guest", $"Played dog animation {p[3]} on net id {netId}");
            }
            else if (p[0] == "S" && p.Length >= 5)
            {
                int netId = Int(p[2]);
                var c = NetMonsterSync.instance?.ClientGetPuppet(netId)?.TryCast<PetCharacterController>();
                if (c == null) return;
                ApplySound(c, p[3], p[4]);
                DLog.Debug("guest", $"Played dog sound {p[3]} on net id {netId}");
            }
            else if (p[0] == "Z" && p.Length >= 4)
            {
                string who = p[2];
                bool on = p[3] == "1";
                if (who == Proto.Short(DogShareBehaviour.IdOf(DogShareBehaviour.HostPlayer()))) return;   // that's me
                foreach (var h in UnityEngine.Object.FindObjectsOfType<HumanCharacterController>())
                {
                    if (h == null || Proto.Short(DogShareBehaviour.IdOf(h)) != who) continue;
                    ApplyPose(h, on);
                    DLog.Debug("guest", $"{DogShareBehaviour.GuestName(h)} {(on ? "started" : "stopped")} petting");
                    return;
                }
            }
        }

        internal static void ApplySound(PetCharacterController pc, string kind, string arg)
        {
            Applying = true;
            var saved = PromptFlags.Clear(pc, "sound");
            try
            {
                float f = Flt(arg);
                switch (kind)
                {
                    case "bark": pc.PlayBark(); break;
                    case "whine": pc.PlayWhine(); break;
                    case "soft": pc.PlaySoft(f); break;
                    case "happy": pc.PlayHappy(); break;
                    case "hit": pc.PlayOnHitAudioClip(arg == "1"); break;
                }
            }
            catch (Exception e) { DLog.Throttle("snderr", 30f, "error", "Playing a dog sound failed: " + e.Message); }
            finally { PromptFlags.Restore(pc, saved); Applying = false; }
        }

        // Host: tell the guests a dog made a sound.
        internal static void SendSound(PetCharacterController pc, string kind, string arg)
        {
            try
            {
                if (Applying || !IsHost() || pc == null) return;
                int netId = -1;
                if (!NetMonsterSync.instance.HostTryGetNetId(pc, out netId) || netId < 0) return;
                BroadcastToGuests(Proto.Join("S", "*", netId, kind, arg));
                DLog.Debug("send", $"Dog sound {kind} (net id {netId}) sent to guests");
            }
            catch (Exception e) { DLog.Throttle("sndsend", 30f, "error", "Sending a dog sound failed: " + e.Message); }
        }

        static int Int(string s) => int.TryParse(s, out var v) ? v : -1;
        static float Flt(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1f;

        // ---------------------------------------------------------------- guest's own petting pose
        static float _poseOffAt = -1f;
        internal static void GuestPetPose(float seconds)
        {
            var me = DogShareBehaviour.HostPlayer();
            if (me == null) return;
            me.SetPetDogVisual(true);          // our postfix tells the host
            _poseOffAt = Time.unscaledTime + seconds;
        }
        internal static void Tick()
        {
            if (_poseOffAt > 0f && Time.unscaledTime >= _poseOffAt)
            {
                _poseOffAt = -1f;
                try { DogShareBehaviour.HostPlayer()?.SetPetDogVisual(false); } catch { }
            }
        }
    }

    [HarmonyPatch]
    internal static class AnimPatches
    {
        // Host: any dog animation the host plays (petting, eating, ...) goes to the guests' copies.
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PlayPetAnim))]
        [HarmonyPostfix]
        static void PlayPetAnim(PetCharacterController __instance, string stateName, float suspendSeconds, bool __result)
        {
            try
            {
                if (AnimSync.Applying || !AnimSync.IsHost() || __instance == null || string.IsNullOrEmpty(stateName)) return;
                int netId = -1;
                if (!NetMonsterSync.instance.HostTryGetNetId(__instance, out netId) || netId < 0) return;
                AnimSync.BroadcastToGuests(Proto.Join("Y", "*", netId, Proto.Clean(stateName, 40),
                    suspendSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
                DLog.Debug("send", $"Dog animation {stateName} ({suspendSeconds:0.#}s, net id {netId}) sent to guests");
            }
            catch (Exception e) { DLog.Throttle("animsend", 30f, "error", "Sending a dog animation failed: " + e.Message); }
        }

        // Host: dog sounds go to the guests' copies too.
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PlayBark))]
        [HarmonyPostfix] static void Bark(PetCharacterController __instance) => AnimSync.SendSound(__instance, "bark", "0");

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PlayWhine))]
        [HarmonyPostfix] static void Whine(PetCharacterController __instance) => AnimSync.SendSound(__instance, "whine", "0");

        // TryPlayWhine only whines when its cooldown allows; nextWhineTime moves when it actually did.
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.TryPlayWhine))]
        [HarmonyPrefix] static void TryWhinePre(PetCharacterController __instance, out float __state)
        {
            __state = 0f;
            try { __state = __instance.nextWhineTime; } catch { }
        }
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.TryPlayWhine))]
        [HarmonyPostfix] static void TryWhinePost(PetCharacterController __instance, float __state)
        {
            try { if (__instance.nextWhineTime != __state) AnimSync.SendSound(__instance, "whine", "0"); } catch { }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PlaySoft))]
        [HarmonyPostfix] static void Soft(PetCharacterController __instance, float volumeScale)
            => AnimSync.SendSound(__instance, "soft", volumeScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PlayHappy))]
        [HarmonyPostfix] static void Happy(PetCharacterController __instance) => AnimSync.SendSound(__instance, "happy", "0");

        // The local player's petting pose: host tells the guests, a guest tells the host.
        [HarmonyPatch(typeof(HumanCharacterController), nameof(HumanCharacterController.SetPetDogVisual))]
        [HarmonyPostfix]
        static void SetPetDogVisual(HumanCharacterController __instance, bool active)
        {
            try
            {
                if (AnimSync.Applying || __instance == null || !NetworkManager.isOnline) return;
                var me = DogShareBehaviour.HostPlayer();
                if (!DogShareBehaviour.Same(__instance, me)) return;
                if (NetworkManager.isClient)
                {
                    if (GuestMirror.HostHasMod) GuestMirror.Send("V", active ? 1 : 0);
                }
                else
                {
                    AnimSync.BroadcastToGuests(Proto.Join("Z", "*", Proto.Short(DogShareBehaviour.IdOf(me)), active ? 1 : 0));
                }
                DLog.Debug("act", $"My petting pose {(active ? "on" : "off")} - sent");
            }
            catch (Exception e) { DLog.Throttle("posesend", 30f, "error", "Sending the petting pose failed: " + e.Message); }
        }
    }
}
