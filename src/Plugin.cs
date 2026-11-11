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
    // Guest: the dogs the host gave you get the game's own prompts, Teammates card and orders menu;
    //        every action is sent to the host over hidden chat lines and run there.
    //  * Lifts the "only one pet in a co-op session" rule on the host (back to the single-player cap).
    //  * F7 near a guest: hands the dog closest to you to that guest (max 3 per guest).
    //  * F8 near a guest's dog: takes it back.
    //  * Keeps guest dogs re-linked to their owner when the guest (re)joins, and keeps them fed.
    [BepInPlugin(Guid, "DogShare (co-op dogs for guests)", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "girthy_mods.zedzone.dogshare";
        public const string Version = "1.4.6";

        internal static ManualLogSource L;
        internal static ConfigEntry<KeyCode> GiveKey, TakeKey;
        internal static ConfigEntry<int> MaxPerGuest;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<bool> KeepFed;
        internal static ConfigEntry<bool> VerboseLog;

        static int CountPatched(Harmony h) { int n = 0; foreach (var _ in h.GetPatchedMethods()) n++; return n; }

        public override void Load()
        {
            L = Log;
            GiveKey = Config.Bind("Keys", "GiveDogKey", KeyCode.F7, "Host: give the dog nearest to you to the guest nearest to you.");
            TakeKey = Config.Bind("Keys", "TakeBackDogKey", KeyCode.F8, "Host: take back the guest-owned dog nearest to you.");
            MaxPerGuest = Config.Bind("Rules", "MaxDogsPerGuest", 3, "How many dogs each guest can be given.");
            Range = Config.Bind("Rules", "Range", 8f, "How close (in game units) the guest / dog must be to the host.");
            KeepFed = Config.Bind("Rules", "AutoFeedGuestDogs", false, "Keep guest-owned dogs' hunger full automatically. Off (default): guest dogs get hungry like your own, and their owner feeds them.");

            VerboseLog = Config.Bind("Debug", "VerboseLog", true, "Log every request and reply (DEBUG lines). Turn off once everything works.");
            DLog.Verbose = VerboseLog.Value;
            DLog.Open(Version);
            string gameVer = "?";
            try { gameVer = Application.version; } catch { }
            DLog.Info("start", $"DogShare {Version} loading (game {gameVer}). Host keys: give={GiveKey.Value}, take back={TakeKey.Value}. MaxDogsPerGuest={MaxPerGuest.Value}, Range={Range.Value}, AutoFeedGuestDogs={KeepFed.Value}, VerboseLog={VerboseLog.Value}");

            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;
            foreach (var t in new[] { typeof(Patches), typeof(GuidPatch), typeof(ChatPatches), typeof(GuestPatches), typeof(PromptGatePatches), typeof(HostCardPatch), typeof(AnimPatches), typeof(FetchPatches), typeof(RidePatches) })
            {
                try
                {
                    var before = CountPatched(harmony);
                    harmony.PatchAll(t);
                    int n = CountPatched(harmony) - before;
                    DLog.Info("start", $"Patched {t.Name}: {n} method(s)");
                    ok++;
                }
                catch (Exception e) { failed++; DLog.Error("start", $"Patching {t.Name} FAILED - that part of DogShare won't work", e); }
            }
            foreach (var m in harmony.GetPatchedMethods())
                DLog.Debug("start", $"  hooked {m.DeclaringType?.Name}.{m.Name}");
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<DogShareBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<GuestMirror>();
                AddComponent<DogShareBehaviour>();
                AddComponent<GuestMirror>();
            }
            catch (Exception e) { DLog.Error("start", "Couldn't start DogShare's update loop", e); }
            DLog.Info("start", $"DogShare {Version} loaded ({ok} patch groups ok, {failed} failed). Log file: BepInEx\\DogShare.log");
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
            try
            {
                if (__result && !NetworkManager.isClient)
                {
                    __result = false;
                    DLog.Once("singlepet", "host", "Co-op one-dog limit lifted on this host (back to the single-player cap).");
                }
            }
            catch { }
        }
    }

    // The dog AI re-resolves its leader every update with GameController.GetCharacterByGUID, which
    // only knows local characters. On the host, also answer with remote players so a guest stays the leader.
    [HarmonyPatch(typeof(GameController), nameof(GameController.GetCharacterByGUID))]
    internal static class GuidPatch
    {
        [HarmonyPostfix]
        static void Postfix(string __0, ref BasicCharacterController __result)
        {
            if (__result != null || string.IsNullOrEmpty(__0)) return;
            try
            {
                if (!NetworkManager.isOnline || NetworkManager.isClient) return;
                var g = DogShareBehaviour.GuestById(__0);
                if (g != null)
                {
                    __result = g;
                    DLog.Throttle("guid|" + __0, 60f, "host", $"Dog leader lookup: answered {DLog.S(__0)} with remote player {DogShareBehaviour.GuestName(g)}");
                }
            }
            catch { }
        }
    }

    public class DogShareBehaviour : MonoBehaviour
    {
        public DogShareBehaviour(IntPtr ptr) : base(ptr) { }

        float _nextTick;
        bool _loggedRelinkMiss;
        int _relinkLogs;

        float _nextStatus;

        void Update()
        {
            try
            {
                string role = !NetworkManager.isOnline ? "offline / single-player" : NetworkManager.isClient ? "guest" : "host";
                DLog.Change("role", "role", "Now " + role);
                if (!NetworkManager.isOnline || NetworkManager.isClient) return;   // host only
                Fetch.HostTick();
                Ride.HostTick();
                if (Time.unscaledTime >= _nextStatus) { _nextStatus = Time.unscaledTime + 30f; Status(); }
                if (Pressed(Plugin.GiveKey.Value)) Give();
                else if (Pressed(Plugin.TakeKey.Value)) TakeBack();
                if (Time.unscaledTime >= _nextTick) { _nextTick = Time.unscaledTime + 2f; Maintain(); }
            }
            catch (Exception e) { DLog.Error("error", "Host update failed", e); _nextTick = Time.unscaledTime + 10f; }
        }

        // ---------------------------------------------------------------- input
        static bool _legacyInputBroken;
        internal static bool Pressed(KeyCode key)
        {
            if (!_legacyInputBroken)
            {
                try { return Input.GetKeyDown(key); }
                catch { _legacyInputBroken = true; DLog.Info("start", "Legacy input unavailable, using the Input System for keys."); }
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
            HashSet<IntPtr> remote = null;
            foreach (var h in UnityEngine.Object.FindObjectsOfType<HumanCharacterController>())
            {
                if (h == null || !h.isPlayer || h.isNetPuppet) continue;
                if (remote == null) { remote = new HashSet<IntPtr>(); foreach (var g in Guests()) remote.Add(g.Pointer); }
                if (!remote.Contains(h.Pointer)) return h;
            }
            return null;
        }

        // Remote players' characters, from the game's own remote-player table (NetPlayerSync),
        // plus any stand-in characters flagged as net puppets.
        internal static List<HumanCharacterController> Guests()
        {
            var list = new List<HumanCharacterController>();
            var seen = new HashSet<IntPtr>();
            try
            {
                var sync = NetPlayerSync.instance;
                if (sync != null)
                {
                    var ids = new Il2CppSystem.Collections.Generic.List<int>();
                    sync.HostCollectRemotePlayerIds(ids);
                    for (int i = 0; i < ids.Count; i++)
                    {
                        HumanCharacterController c = null;
                        if (!sync.TryGetRemoteController(ids[i], out c) || c == null) continue;
                        if (c.characterData == null || string.IsNullOrEmpty(c.characterData.characterId)) continue;
                        if (seen.Add(c.Pointer)) list.Add(c);
                    }
                }
            }
            catch (Exception e) { if (!_warnedSync) { _warnedSync = true; DLog.Error("error", "Remote player lookup (NetPlayerSync) failed - guests may not be found", e); } }
            foreach (var h in UnityEngine.Object.FindObjectsOfType<HumanCharacterController>())
                if (h != null && h.isNetPuppet && h.characterData != null && !string.IsNullOrEmpty(h.characterData.characterId) && seen.Add(h.Pointer))
                    list.Add(h);
            return list;
        }
        static bool _warnedSync;

        internal static HumanCharacterController GuestById(string characterId)
        {
            if (string.IsNullOrEmpty(characterId)) return null;
            foreach (var g in Guests()) if (IdOf(g) == characterId) return g;
            return null;
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
            DLog.Info("host", msg);
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
                var guests = Guests();
                var sb = new System.Text.StringBuilder($"[diag] localPlayers={players} puppets={puppets} puppetsWithId={withData} remotePlayers={guests.Count} hostId={IdOf(host)} dogs:");
                foreach (var d in Dogs())
                    sb.Append($" [owner={GuidOf(d)} leader={(d.leaderCharacter == null ? "none" : (Same(d.leaderCharacter, host) ? "host" : "guest"))} dist={(host == null ? -1 : (Pos(d) - Pos(host)).magnitude):0.0}]");
                DLog.Info("host", sb.ToString());
            }
            catch (Exception e) { DLog.Error("error", "diag failed", e); }
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

            DLog.Info("host", $"Giving dog {DLog.S(dog.npcData?.npcGUID)} '{dog.npcData?.characterData?.characterName}' to {GuestName(guest)} ({DLog.S(gid)}), distance {(Pos(guest) - here).magnitude:0.0}");
            dog.SetLeader(guest);
            if (GuidOf(dog) != gid)
                DLog.Error("host", $"SetLeader did not store the guest id (got '{GuidOf(dog)}', expected '{gid}') - the dog may not stay with them");
            else if (!Same(dog.leaderCharacter, guest))
                DLog.Warn("host", "The dog's leader isn't the guest's character right after SetLeader");
            else
                DLog.Info("host", "Dog's owner and leader are now the guest - OK");
            try { PetPanel.NotifyPetRemoved(dog.npcData); } catch { }
            DropFromHostTeam(host, dog.npcData, "given away");
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
                return !Same(d.leaderCharacter, host);
            });
            var dog = Nearest(theirs, here, Plugin.Range.Value);
            if (dog == null) { Say("DogShare: no guest's dog is close to you."); return; }
            if (!PetSystem.CanRescueMorePets()) { Say("DogShare: you already have the most dogs you can keep."); return; }

            DLog.Info("host", $"Taking back dog {DLog.S(dog.npcData?.npcGUID)} '{dog.npcData?.characterData?.characterName}' from {DLog.S(GuidOf(dog))}");
            dog.SetLeader(host);
            try { PetPanel.NotifyPetChanged(dog.npcData); } catch { }
            try { host.characterData.AddTeamNPC(dog.npcData); host.characterData.SyncTeamUI(); DLog.Info("host", "Dog is back on my Teammates card"); }
            catch (Exception e) { DLog.Error("host", "Couldn't put the dog back on my Teammates card", e); }
            Say("DogShare: you took the dog back.");
        }

        // A dog joins its leader's team list and Teammates card. When it's given away, take it off the host's,
        // or the host keeps commanding it from the card.
        internal static void DropFromHostTeam(HumanCharacterController host, GOAPNPCData npc, string why)
        {
            if (host == null || npc == null) return;
            try
            {
                var cd = host.characterData;
                bool listed = false;
                try { listed = cd.teamNPCGuids != null && cd.teamNPCGuids.Contains(npc.npcGUID); } catch { }
                if (listed) cd.RemoveTeamNPC(npc);
                try { TeamNPCPanel.instance?.RemoveNPC(npc); } catch { }
                try { cd.SyncTeamUI(); } catch { }
                DLog.Info("host", $"Removed dog {DLog.S(npc.npcGUID)} from my team / Teammates card ({why}; was in team list: {listed})");
            }
            catch (Exception e) { DLog.Error("host", "Couldn't take the dog off my Teammates card", e); }
        }

        void Status()
        {
            try
            {
                var host = HostPlayer();
                var guests = Guests();
                int mine = 0, guestDogs = 0, linked = 0;
                var hungers = new List<string>();
                foreach (var d in Dogs())
                {
                    if (OwnedBy(d, host)) { mine++; continue; }
                    guestDogs++;
                    var owner = GuestById(GuidOf(d));
                    if (owner != null && Same(d.leaderCharacter, owner)) linked++;
                    hungers.Add($"{d.npcData?.characterData?.characterName}: hunger {d.npcData?.hunger ?? 0f:0}/{PetSystem.petMaxHunger:0}");
                }
                var names = new List<string>();
                foreach (var g in guests) names.Add(GuestName(g));
                DLog.Change("hoststatus", "status", $"Host: {guests.Count} guest(s) [{string.Join(", ", names)}], my dogs {mine}, guest dogs {guestDogs} ({linked} following their owner here) [{string.Join("; ", hungers)}]");
            }
            catch (Exception e) { DLog.Error("error", "status", e); }
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

                // given away earlier (or before this fix) but still on the host's team list
                try
                {
                    var tl = host?.characterData?.teamNPCGuids;
                    if (tl != null && dog.npcData != null && tl.Contains(dog.npcData.npcGUID)) DropFromHostTeam(host, dog.npcData, "belongs to a guest");
                }
                catch { }

                if (byId.TryGetValue(gid, out var owner) && owner != null)
                {
                    var lead = dog.leaderCharacter;
                    if (lead == null || lead.Pointer != owner.Pointer)
                    {
                        dog.SetLeader(owner);
                        if (++_relinkLogs <= 20) DLog.Info("host", $"Re-linked dog {DLog.S(dog.npcData?.npcGUID)} to its owner {GuestName(owner)} (leader was {(lead == null ? "none" : "someone else")})");
                    }
                }
                else if (!_loggedRelinkMiss && guests.Count > 0)
                {
                    _loggedRelinkMiss = true;
                    DLog.Info("host", $"A guest-owned dog's owner ({DLog.S(gid)}) isn't in this session - it waits until they join.");
                }

                if (Plugin.KeepFed.Value && max > 0f)
                {
                    var d = dog.npcData;
                    if (d != null && d.hunger < max)
                    {
                        DLog.Throttle("fed|" + d.npcGUID, 300f, "host", $"Topped up hunger of guest dog {DLog.S(d.npcGUID)} ({d.hunger:0}/{max:0})");
                        d.hunger = max;
                    }
                }
            }
        }
    }
}
