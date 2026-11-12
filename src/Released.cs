using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using ZedZone.Net;

namespace DogShare
{
    // Released dogs coming back. PetSystem.ReleasePet tells the pet panel the dog is gone, but the dog's id stays in its
    // owner's team list (CharacterData.teamNPCGuids). The next time the team list is rebuilt (hiring a teammate, or
    // DogShare taking a dog back) the released dog is put back on the team and follows its old owner again.
    // DogShare now remembers released dogs on the host (saved to BepInEx\config\DogShare.released.txt), takes them off
    // every team list, never adds them to one again, and leaves them out of give / take back. Adopting the dog again
    // (PetSystem.AdoptPet) forgets it.
    internal static class Released
    {
        static readonly HashSet<string> _ids = new HashSet<string>();
        static bool _loaded;
        static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "DogShare.released.txt");

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(FilePath))
                    foreach (var l in File.ReadAllLines(FilePath)) { var s = l.Trim(); if (s.Length > 0) _ids.Add(s); }
                if (_ids.Count > 0) DLog.Info("host", $"Remembering {_ids.Count} released dog(s) - they won't rejoin a team");
            }
            catch (Exception e) { DLog.Error("host", "Couldn't read the released dogs list", e); }
        }

        static void Save()
        {
            try { File.WriteAllLines(FilePath, _ids); }
            catch (Exception e) { DLog.Error("host", "Couldn't save the released dogs list", e); }
        }

        internal static bool Is(string npcGuid) { Load(); return !string.IsNullOrEmpty(npcGuid) && _ids.Contains(npcGuid); }
        internal static bool Is(GOAPNPCData d) => d != null && Is(d.npcGUID);
        internal static int Count { get { Load(); return _ids.Count; } }

        internal static void Add(GOAPNPCData d, string why)
        {
            if (d == null || string.IsNullOrEmpty(d.npcGUID)) return;
            Load();
            if (_ids.Add(d.npcGUID)) Save();
            DLog.Info("host", $"Dog {DLog.S(d.npcGUID)} '{d.characterData?.characterName}' was released ({why}) - taking it off every team list for good");
            DropEverywhere(d.npcGUID, d);
        }

        internal static void Forget(GOAPNPCData d, string why)
        {
            if (d == null || string.IsNullOrEmpty(d.npcGUID)) return;
            Load();
            if (_ids.Remove(d.npcGUID)) { Save(); DLog.Info("host", $"Dog {DLog.S(d.npcGUID)} '{d.characterData?.characterName}' {why} - it can join a team again"); }
        }

        // Take a released dog's id out of every player's team list (host and stand-ins), and off the Teammates card.
        internal static void DropEverywhere(string guid, GOAPNPCData d)
        {
            var people = new List<HumanCharacterController>();
            try { var h = DogShareBehaviour.HostPlayer(); if (h != null) people.Add(h); } catch { }
            try { people.AddRange(DogShareBehaviour.Guests()); } catch { }
            foreach (var p in people)
            {
                try
                {
                    var cd = p?.characterData;
                    if (cd?.teamNPCGuids == null || !cd.teamNPCGuids.Contains(guid)) continue;
                    bool removed = false;
                    if (d != null) { try { removed = cd.RemoveTeamNPC(d); } catch { } }
                    if (cd.teamNPCGuids.Contains(guid)) { cd.teamNPCGuids.Remove(guid); removed = true; }
                    try { cd.SyncTeamUI(); } catch { }
                    if (removed) DLog.Info("host", $"Took released dog {DLog.S(guid)} out of {DogShareBehaviour.GuestName(p)}'s team list");
                }
                catch (Exception e) { DLog.ErrorThrottled("relteam|" + guid, 30f, "host", "Taking a released dog off a team list failed", e); }
            }
            if (d != null) { try { TeamNPCPanel.instance?.RemoveNPC(d); } catch { } }
        }

        // Called from the host's 2-second upkeep: catch released dogs that got back on a team list another way (e.g. a save).
        internal static void Sweep()
        {
            Load();
            if (_ids.Count == 0) return;
            foreach (var id in new List<string>(_ids))
            {
                bool listed = false;
                try
                {
                    var h = DogShareBehaviour.HostPlayer();
                    if (h?.characterData?.teamNPCGuids != null && h.characterData.teamNPCGuids.Contains(id)) listed = true;
                    if (!listed) foreach (var g in DogShareBehaviour.Guests()) if (g?.characterData?.teamNPCGuids != null && g.characterData.teamNPCGuids.Contains(id)) { listed = true; break; }
                }
                catch { }
                if (!listed) continue;
                GOAPNPCData data = null;
                foreach (var p in UnityEngine.Object.FindObjectsOfType<GOAPAgent_Pet>()) if (p?.npcData != null && p.npcData.npcGUID == id) { data = p.npcData; break; }
                DropEverywhere(id, data);
            }
        }
    }

    [HarmonyPatch]
    internal static class ReleasedPatches
    {
        static bool IsHostOrOffline() { try { return !NetworkManager.isClient; } catch { return true; } }

        [HarmonyPatch(typeof(PetSystem), nameof(PetSystem.ReleasePet))]
        [HarmonyPostfix]
        static void AfterRelease(GOAPNPCData npcData, bool __result)
        {
            try { if (__result && IsHostOrOffline()) Released.Add(npcData, "PetSystem.ReleasePet"); }
            catch (Exception e) { DLog.Error("host", "Remembering a released dog failed", e); }
        }

        [HarmonyPatch(typeof(PetSystem), nameof(PetSystem.AdoptPet))]
        [HarmonyPostfix]
        static void AfterAdopt(GOAPNPCData npcData, bool __result)
        {
            try { if (__result) Released.Forget(npcData, "was adopted again"); } catch { }
        }

        // The game re-adds team members from its lists when the team changes (hiring someone, a dog taken back).
        [HarmonyPatch(typeof(CharacterData), nameof(CharacterData.AddTeamNPC))]
        [HarmonyPrefix]
        static bool BeforeAddTeam(GOAPNPCData npcData, ref bool __result)
        {
            try
            {
                if (!IsHostOrOffline() || !Released.Is(npcData)) return true;
                DLog.Throttle("reladd|" + npcData.npcGUID, 30f, "host", $"Kept released dog {DLog.S(npcData.npcGUID)} '{npcData.characterData?.characterName}' from rejoining a team");
                __result = false;
                return false;
            }
            catch { return true; }
        }
    }
}
