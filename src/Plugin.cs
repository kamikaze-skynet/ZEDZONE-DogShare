using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Host: lifts the one-pet co-op cap, gives / takes back dogs, runs guest dog commands.
    // Guest: F6 menu for the dogs the host gave you (talks to the host over hidden chat lines).
    //  * Lifts the "only one pet in a co-op session" rule on the host (back to the single-player cap).
    //  * F7 near a guest: hands the dog closest to you to that guest (max 3 per guest).
    //  * F8 near a guest's dog: takes it back.
    //  * Keeps guest dogs re-linked to their owner when the guest (re)joins, and keeps them fed.
    [BepInPlugin(Guid, "DogShare (co-op dogs for guests)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "girthy_mods.zedzone.dogshare";
        public const string Version = "1.1.0";

        internal static ManualLogSource L;
        internal static ConfigEntry<KeyCode> GiveKey, TakeKey, MenuKey;
        internal static ConfigEntry<int> MaxPerGuest;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<bool> KeepFed;

        public override void Load()
        {
            L = Log;
            GiveKey = Config.Bind("Keys", "GiveDogKey", KeyCode.F7, "Host: give the dog nearest to you to the guest nearest to you.");
            TakeKey = Config.Bind("Keys", "TakeBackDogKey", KeyCode.F8, "Host: take back the guest-owned dog nearest to you.");
            MaxPerGuest = Config.Bind("Rules", "MaxDogsPerGuest", 3, "How many dogs each guest can be given.");
            Range = Config.Bind("Rules", "Range", 8f, "How close (in game units) the guest / dog must be to the host.");
            KeepFed = Config.Bind("Rules", "KeepGuestDogsFed", true, "Guests can't feed dogs through the menu, so keep guest-owned dogs' hunger full.");

            new Harmony(Guid).PatchAll(typeof(Patches));
            MenuKey = Config.Bind("Keys", "DogMenuKey", KeyCode.F6, "Guest: open the menu for the dogs the host gave you.");
            ClassInjector.RegisterTypeInIl2Cpp<DogShareBehaviour>();
            ClassInjector.RegisterTypeInIl2Cpp<GuestDogMenu>();
            AddComponent<DogShareBehaviour>();
            AddComponent<GuestDogMenu>();
            Log.LogInfo($"DogShare {Version} loaded (host-only). Give={GiveKey.Value} TakeBack={TakeKey.Value}");
        }
    }

    // NetSim.singlePetSession is "true whenever online". It drives CanRescueMorePets (1 vs 3),
    // the pet-limit text, and the freezing of extra pets. Only ever lift it on the host.
    [HarmonyPatch(typeof(NetSim), nameof(NetSim.singlePetSession), MethodType.Getter)]
    internal static class Patches
    {
        [HarmonyPostfix]
        static void Postfix(ref bool __result)
        {
            try { if (__result && !NetworkManager.isClient) __result = false; }
            catch { }
        }
    }

    public class DogShareBehaviour : MonoBehaviour
    {
        public DogShareBehaviour(IntPtr ptr) : base(ptr) { }

        float _nextTick;
        bool _loggedRelinkMiss;

        void Update()
        {
            try
            {
                if (!NetworkManager.isOnline || NetworkManager.isClient) return;   // host only
                if (Pressed(Plugin.GiveKey.Value)) Give();
                else if (Pressed(Plugin.TakeKey.Value)) TakeBack();
                if (Time.unscaledTime >= _nextTick) { _nextTick = Time.unscaledTime + 2f; Maintain(); }
            }
            catch (Exception e) { Plugin.L.LogError("Update: " + e); _nextTick = Time.unscaledTime + 10f; }
        }

        // ---------------------------------------------------------------- input
        static bool _legacyInputBroken;
        internal static bool Pressed(KeyCode key)
        {
            if (!_legacyInputBroken)
            {
                try { return Input.GetKeyDown(key); }
                catch { _legacyInputBroken = true; Plugin.L.LogWarning("Legacy input unavailable, using Input System."); }
            }
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                var k = key switch
                {
                    KeyCode.F1 => UnityEngine.InputSystem.Key.F1, KeyCode.F2 => UnityEngine.InputSystem.Key.F2,
                    KeyCode.F3 => UnityEngine.InputSystem.Key.F3, KeyCode.F4 => UnityEngine.InputSystem.Key.F4,
                    KeyCode.F5 => UnityEngine.InputSystem.Key.F5, KeyCode.F6 => UnityEngine.InputSystem.Key.F6,
                    KeyCode.F7 => UnityEngine.InputSystem.Key.F7, KeyCode.F8 => UnityEngine.InputSystem.Key.F8,
                    KeyCode.F9 => UnityEngine.InputSystem.Key.F9, KeyCode.F10 => UnityEngine.InputSystem.Key.F10,
                    KeyCode.F11 => UnityEngine.InputSystem.Key.F11, KeyCode.F12 => UnityEngine.InputSystem.Key.F12,
                    _ => UnityEngine.InputSystem.Key.None
                };
                return k != UnityEngine.InputSystem.Key.None && kb[k].wasPressedThisFrame;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- world queries
        internal static HumanCharacterController HostPlayer()
        {
            foreach (var h in UnityEngine.Object.FindObjectsOfType<HumanCharacterController>())
                if (h != null && h.isPlayer && !h.isNetPuppet) return h;
            return null;
        }

        internal static List<HumanCharacterController> Guests()
        {
            var list = new List<HumanCharacterController>();
            foreach (var h in UnityEngine.Object.FindObjectsOfType<HumanCharacterController>())
                if (h != null && h.isNetPuppet && h.characterData != null && !string.IsNullOrEmpty(h.characterData.characterId))
                    list.Add(h);
            return list;
        }

        internal static List<GOAPAgent_Pet> Dogs()
        {
            var list = new List<GOAPAgent_Pet>();
            foreach (var p in UnityEngine.Object.FindObjectsOfType<GOAPAgent_Pet>())
            {
                if (p == null) continue;
                var d = p.npcData;
                if (d == null || PetSystem.IsStrayPet(d)) continue;
                list.Add(p);
            }
            return list;
        }

        internal static Vector2 Pos(Component c) => c.transform.position;
        internal static bool Same(UnityEngine.Object a, UnityEngine.Object b) => a != null && b != null && a.Pointer == b.Pointer;
        internal static bool OwnedBy(GOAPAgent_Pet d, HumanCharacterController who)
            => Same(d.leaderCharacter, who) || (!string.IsNullOrEmpty(IdOf(who)) && GuidOf(d) == IdOf(who));
        internal static string GuidOf(GOAPAgent_Pet p) => p.npcData?.leaderCharacterGUID;
        internal static string IdOf(HumanCharacterController h) => h?.characterData?.characterId;

        static T Nearest<T>(IEnumerable<T> items, Vector2 from, float range) where T : Component
        {
            T best = null; float bestD = range * range;
            foreach (var i in items)
            {
                float d = (Pos(i) - from).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        internal static string GuestName(HumanCharacterController g)
        {
            try { var n = g.characterData.characterName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "that player";
        }

        internal static void Say(string msg)
        {
            Plugin.L.LogInfo(msg);
            try { SystemNotificationPanel.ShowNotificationSafe(msg, true); } catch { }
        }

        // One-line state dump to LogOutput.log, to help when something doesn't work.
        static void Diag()
        {
            try
            {
                int puppets = 0, withData = 0, players = 0;
                foreach (var h in UnityEngine.Object.FindObjectsOfType<HumanCharacterController>())
                {
                    if (h == null) continue;
                    if (h.isNetPuppet) { puppets++; if (h.characterData != null && !string.IsNullOrEmpty(h.characterData.characterId)) withData++; }
                    if (h.isPlayer && !h.isNetPuppet) players++;
                }
                var host = HostPlayer();
                var sb = new System.Text.StringBuilder($"[diag] localPlayers={players} puppets={puppets} puppetsWithId={withData} hostId={IdOf(host)} dogs:");
                foreach (var d in Dogs())
                    sb.Append($" [owner={GuidOf(d)} leader={(d.leaderCharacter == null ? "none" : (d.leaderCharacter.isNetPuppet ? "guest" : "local"))} dist={(host == null ? -1 : (Pos(d) - Pos(host)).magnitude):0.0}]");
                Plugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.L.LogWarning("diag failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- actions
        void Give()
        {
            var host = HostPlayer();
            if (host == null) { Say("DogShare: couldn't find your character."); return; }
            var here = Pos(host);
            string hostId = IdOf(host);

            var guest = Nearest(Guests(), here, Plugin.Range.Value);
            if (guest == null) { Diag(); Say("DogShare: stand next to the player you want to give a dog to."); return; }

            var dogs = Dogs();
            var mine = dogs.FindAll(d => OwnedBy(d, host));
            var dog = Nearest(mine, here, Plugin.Range.Value * 2f);
            if (dog == null) { Diag(); Say("DogShare: none of your dogs are close by."); return; }

            string gid = IdOf(guest);
            int has = dogs.FindAll(d => OwnedBy(d, guest)).Count;
            if (has >= Plugin.MaxPerGuest.Value) { Say($"DogShare: {GuestName(guest)} already has {has} dogs."); return; }

            dog.SetLeader(guest);
            if (GuidOf(dog) != gid)
                Plugin.L.LogWarning($"SetLeader did not store the guest id (got '{GuidOf(dog)}', expected '{gid}').");
            try { PetPanel.NotifyPetRemoved(dog.npcData); } catch { }
            Say($"Gave your dog to {GuestName(guest)} ({has + 1}/{Plugin.MaxPerGuest.Value}).");
        }

        void TakeBack()
        {
            var host = HostPlayer();
            if (host == null) { Say("DogShare: couldn't find your character."); return; }
            var here = Pos(host);
            string hostId = IdOf(host);

            var guestIds = new HashSet<string>();
            foreach (var g in Guests()) guestIds.Add(IdOf(g));
            var theirs = Dogs().FindAll(d =>
            {
                var gid = GuidOf(d);
                if (string.IsNullOrEmpty(gid) || gid == hostId) return false;
                var lead = d.leaderCharacter;
                return guestIds.Contains(gid) || (lead != null && lead.isNetPuppet) || lead == null;
            });
            var dog = Nearest(theirs, here, Plugin.Range.Value);
            if (dog == null) { Say("DogShare: no guest's dog is close to you."); return; }
            if (!PetSystem.CanRescueMorePets()) { Say("DogShare: you already have the most dogs you can keep."); return; }

            dog.SetLeader(host);
            try { PetPanel.NotifyPetChanged(dog.npcData); } catch { }
            Say("DogShare: you took the dog back.");
        }

        // Re-link guest dogs to their owner (after a reload / rejoin) and keep them fed.
        void Maintain()
        {
            var guests = Guests();
            var byId = new Dictionary<string, HumanCharacterController>();
            foreach (var g in guests) byId[IdOf(g)] = g;
            var host = HostPlayer();
            string hostId = IdOf(host);

            float max = 0f;
            try { max = PetSystem.petMaxHunger; } catch { }

            foreach (var dog in Dogs())
            {
                var gid = GuidOf(dog);
                if (string.IsNullOrEmpty(gid) || gid == hostId || Same(dog.leaderCharacter, host)) continue;

                if (byId.TryGetValue(gid, out var owner))
                {
                    var lead = dog.leaderCharacter;
                    if (lead == null || lead.Pointer != owner.Pointer)
                    {
                        dog.SetLeader(owner);
                        Plugin.L.LogInfo($"Re-linked a dog to {GuestName(owner)}.");
                    }
                }
                else if (!_loggedRelinkMiss && guests.Count > 0)
                {
                    _loggedRelinkMiss = true;
                    Plugin.L.LogInfo($"A guest-owned dog's owner ({gid}) isn't in this session.");
                }

                if (Plugin.KeepFed.Value && max > 0f)
                {
                    var d = dog.npcData;
                    if (d != null && d.hunger < max) d.hunger = max;
                }
            }
        }
    }
}
