using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Kennel, phase 1 prototype (host / single player, own dogs only).
    //   F9  : put your nearest dog in the kennel. It leaves the world and your team; its whole record
    //         (GOAPNPCData: name, breed, affection, skills, gear, saddlebag) is saved with the game's own save
    //         format (ZedZoneSerializer) to BepInEx\config\DogShare.kennel.txt.
    //   F10 : bring back the dog stored longest ago, next to you.
    // Safety: the file is written and read back before the dog is removed, and an entry is only deleted
    // once the restored dog is in the world again. Every step is logged under [kennel].
    internal static class Kennel
    {
        internal class Entry { public string Guid, Owner, Name, Stored; public byte[] Bytes; }

        static readonly List<Entry> _entries = new List<Entry>();
        static bool _loaded;
        static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "DogShare.kennel.txt");

        // ---------------------------------------------------------------- file
        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('\t');
                    if (p.Length < 5) continue;
                    try { _entries.Add(new Entry { Guid = p[0], Owner = p[1], Name = p[2], Stored = p[3], Bytes = Convert.FromBase64String(p[4]) }); }
                    catch (Exception e) { DLog.Error("kennel", $"Couldn't read kennel entry for {DLog.S(p[0])} - kept in the file", e); }
                }
                DLog.Info("kennel", $"Kennel has {_entries.Count} dog(s): [{string.Join(", ", _entries.ConvertAll(x => x.Name))}]");
            }
            catch (Exception e) { DLog.Error("kennel", "Couldn't read the kennel file", e); }
        }

        static void Save()
        {
            var lines = new List<string>();
            foreach (var x in _entries)
                lines.Add(string.Join("\t", x.Guid, x.Owner, Clean(x.Name), x.Stored, Convert.ToBase64String(x.Bytes)));
            var tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, lines);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
            else File.Move(tmp, FilePath);
        }

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        // ---------------------------------------------------------------- serialization (the game's save format)
        static ZedZoneSerializer _ser;
        static ZedZoneSerializer Ser => _ser ??= new ZedZoneSerializer();

        static byte[] ToBytes(GOAPNPCData d)
        {
            var ms = new Il2CppSystem.IO.MemoryStream();
            Ser.Serialize(ms, d);
            return (byte[])ms.ToArray();
        }

        static GOAPNPCData FromBytes(byte[] b)
        {
            var ms = new Il2CppSystem.IO.MemoryStream((Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)b);
            var o = Ser.Deserialize(ms, null, Il2CppType.Of<GOAPNPCData>());
            return o?.TryCast<GOAPNPCData>();
        }

        static string Describe(GOAPNPCData d)
        {
            var pd = d?.petData;
            if (pd == null) return "no pet data";
            int skills = 0; try { skills = pd.skillLevels?.Count ?? 0; } catch { }
            int bag = 0; try { bag = pd.pouchItem?.inventoryData != null ? HostHandler.CountItems(pd.pouchItem.inventoryData) : 0; } catch { }
            return $"breed {pd.breedId}, affection level {pd.affectionLevel} ({pd.affectionXp:0.#} xp), {skills} skill entries, " +
                   $"collar {(pd.collarItem == null ? "-" : pd.collarItem.itemId.ToString())}, vest {(pd.vestItem == null ? "-" : pd.vestItem.itemId.ToString())}, " +
                   $"saddlebag {(pd.pouchItem == null ? "-" : pd.pouchItem.itemId + " with " + bag + " stack(s)")}, hunger {d.hunger:0}";
        }

        // ---------------------------------------------------------------- input (called from DogShareBehaviour.Update)
        internal static void Tick()
        {
            if (DogShareBehaviour.Pressed(Plugin.KennelStoreKey.Value)) Run("store", Store);
            else if (DogShareBehaviour.Pressed(Plugin.KennelRestoreKey.Value)) Run("bring back", Restore);
        }

        static void Run(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { DLog.Error("kennel", $"Kennel {what} failed", e); DogShareBehaviour.Say($"DogShare kennel: {what} failed - see DogShare.log"); }
        }

        static bool IsHostOrOffline() { try { return !NetworkManager.isOnline || !NetworkManager.isClient; } catch { return true; } }

        // ---------------------------------------------------------------- store
        static void Store()
        {
            Load();
            if (!IsHostOrOffline()) return;
            var me = DogShareBehaviour.HostPlayer();
            if (me == null) { DogShareBehaviour.Say("DogShare kennel: couldn't find your character."); return; }
            string myId = DogShareBehaviour.IdOf(me);
            var mine = DogShareBehaviour.Dogs().FindAll(d => DogShareBehaviour.OwnedBy(d, me));
            GOAPAgent_Pet dog = null; float best = Plugin.Range.Value * Plugin.Range.Value * 4f;
            foreach (var d in mine) { float dd = (DogShareBehaviour.Pos(d) - DogShareBehaviour.Pos(me)).sqrMagnitude; if (dd <= best) { best = dd; dog = d; } }
            if (dog == null) { DogShareBehaviour.Say("DogShare kennel: none of your dogs are close by."); return; }

            var data = dog.npcData; var pd = data?.petData;
            string name = data?.characterData?.characterName ?? "your dog";
            if (pd == null) { DogShareBehaviour.Say("DogShare kennel: that dog has no pet data."); return; }
            if (pd.ridingWithOwner) { DogShareBehaviour.Say($"DogShare kennel: {name} is in a car."); return; }
            if (pd.isUnconscious) { DogShareBehaviour.Say($"DogShare kennel: {name} is unconscious."); return; }
            if (_entries.Exists(x => x.Guid == data.npcGUID)) { DLog.Warn("kennel", $"'{name}' is already in the kennel file - replacing that entry"); _entries.RemoveAll(x => x.Guid == data.npcGUID); }

            DLog.Info("kennel", $"Storing '{name}' ({DLog.S(data.npcGUID)}): {Describe(data)}");

            // 1. serialize and check it reads back
            var bytes = ToBytes(data);
            var check = FromBytes(bytes);
            if (check == null || check.npcGUID != data.npcGUID || check.petData == null)
            { DLog.Error("kennel", $"'{name}' didn't survive a save/load check ({bytes.Length} bytes) - not storing it"); DogShareBehaviour.Say($"DogShare kennel: couldn't save {name} safely - left it here."); return; }
            DLog.Info("kennel", $"  saved {bytes.Length} bytes, read back OK: {Describe(check)}");

            // 2. write the file first
            _entries.Add(new Entry { Guid = data.npcGUID, Owner = myId, Name = name, Stored = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Bytes = bytes });
            Save();

            // 3. take it out of the world
            try { me.characterData.RemoveTeamNPC(data); } catch (Exception e) { DLog.Warn("kennel", "  RemoveTeamNPC: " + e.Message); }
            try { TeamNPCPanel.instance?.RemoveNPC(data); } catch { }
            try { PetPanel.NotifyPetRemoved(data); } catch { }
            try { me.characterData.SyncTeamUI(); } catch { }
            var mgr = GOAPNPCManager.instance;
            if (mgr == null) { DLog.Error("kennel", "  GOAPNPCManager isn't there - the dog stays in the world (kennel copy kept)"); return; }
            try { mgr.DespawnNPCInstance(data.npcGUID); DLog.Info("kennel", "  despawned"); } catch (Exception e) { DLog.Warn("kennel", "  DespawnNPCInstance: " + e.Message); }
            try { mgr.RemoveNPC(data.npcGUID); DLog.Info("kennel", "  removed from the NPC list"); } catch (Exception e) { DLog.Error("kennel", "  RemoveNPC failed", e); }
            bool gone = mgr.GetNPCData(data.npcGUID) == null;
            DLog.Info("kennel", $"  still in the game's NPC list: {(gone ? "no - OK" : "YES")}; dog object still alive: {(dog != null && dog.gameObject != null && dog.gameObject.activeInHierarchy ? "yes" : "no")}");
            DogShareBehaviour.Say($"{name} is in the kennel. Press {Plugin.KennelRestoreKey.Value} to bring a dog back.");
        }

        // ---------------------------------------------------------------- restore
        static void Restore()
        {
            Load();
            if (!IsHostOrOffline()) return;
            var me = DogShareBehaviour.HostPlayer();
            if (me == null) { DogShareBehaviour.Say("DogShare kennel: couldn't find your character."); return; }
            string myId = DogShareBehaviour.IdOf(me);
            var e = _entries.Find(x => x.Owner == myId) ?? _entries.Find(x => true);
            if (e == null) { DogShareBehaviour.Say("DogShare kennel: the kennel is empty."); return; }
            if (!PetSystem.CanRescueMorePets()) { DogShareBehaviour.Say("DogShare kennel: you already have the most dogs you can keep."); return; }
            var mgr = GOAPNPCManager.instance;
            if (mgr == null) { DLog.Error("kennel", "GOAPNPCManager isn't there"); return; }
            if (mgr.GetNPCData(e.Guid) != null)
            {
                DLog.Warn("kennel", $"'{e.Name}' ({DLog.S(e.Guid)}) is already in the world - dropping the kennel copy");
                _entries.Remove(e); Save();
                DogShareBehaviour.Say($"DogShare kennel: {e.Name} is already out.");
                return;
            }

            var data = FromBytes(e.Bytes);
            if (data == null) { DLog.Error("kennel", $"'{e.Name}' couldn't be read back ({e.Bytes.Length} bytes) - kept in the kennel"); DogShareBehaviour.Say($"DogShare kennel: couldn't load {e.Name}."); return; }
            DLog.Info("kennel", $"Bringing back '{e.Name}' ({DLog.S(e.Guid)}): {Describe(data)}");

            Vector2 at = DogShareBehaviour.Pos(me) + new Vector2(1f, 0f);
            data.leaderCharacterGUID = myId;
            try { data.npcPosition = at; } catch { }
            try { if (data.petData != null) { data.petData.ridingWithOwner = false; data.petData.basicCommand = "Follow"; } } catch { }

            mgr.AddNPC(data);
            DLog.Info("kennel", $"  AddNPC done; in the NPC list: {(mgr.GetNPCData(e.Guid) != null ? "yes" : "NO")}");
            try { mgr.InitNPCNow(e.Guid); DLog.Info("kennel", "  InitNPCNow done"); } catch (Exception ex) { DLog.Warn("kennel", "  InitNPCNow: " + ex.Message); }

            GOAPAgent_Pet agent = null;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<GOAPAgent_Pet>()) if (p?.npcData != null && p.npcData.npcGUID == e.Guid) { agent = p; break; }
            if (agent != null)
            {
                try { agent.SetLeader(me); } catch (Exception ex) { DLog.Warn("kennel", "  SetLeader: " + ex.Message); }
                try { agent.transform.position = new Vector3(at.x, at.y, agent.transform.position.z); } catch { }
            }
            DLog.Info("kennel", $"  dog in the world: {(agent != null ? "yes" : "NO (it may appear when you're near its spot)")}");
            try { me.characterData.AddTeamNPC(data); me.characterData.SyncTeamUI(); } catch (Exception ex) { DLog.Warn("kennel", "  AddTeamNPC: " + ex.Message); }
            try { PetPanel.NotifyPetChanged(data); } catch { }

            if (mgr.GetNPCData(e.Guid) != null)
            {
                _entries.Remove(e); Save();
                DogShareBehaviour.Say($"{e.Name} is back from the kennel.");
            }
            else
            {
                DLog.Error("kennel", $"  '{e.Name}' didn't make it into the world - kept in the kennel");
                DogShareBehaviour.Say($"DogShare kennel: couldn't bring {e.Name} back - still in the kennel.");
            }
        }
    }
}
