using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2CppInterop.Runtime;
using LiteNetLib;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // Kennel: put a dog away and bring the same dog back later (name, breed, affection, skills, gear, saddlebag).
    //
    //   F9  : store your nearest dog. Host / single player: done here. Guest: asks the host ("KS|dog").
    //   F10 : open / close the kennel list and click a dog to bring it back next to you.
    //         Guest: asks the host for its list ("KL"), then "KR|npcGuid" to bring one back.
    //
    // The host keeps every player's stored dogs, one file per world: BepInEx\config\DogShare.kennel\<world id>.txt.
    // Each entry is the dog's GOAPNPCData in the game's own save format (ZedZoneSerializer), so stored dogs don't
    // get hungry or older. Each player only sees and brings back their own dogs; a guest's dogs wait for them.
    // Safety: the file is written and read back before a dog is removed, and an entry is only deleted once the
    // dog is back in the world. Every step is logged under [kennel].
    //
    // Host -> guest:  KE|to|seq|npcGuid|name|breed|level|stored   one stored dog
    //                 KX|to|seq|count                             end of the kennel list
    internal static class Kennel
    {
        internal class Entry { public string Guid, Owner, OwnerName, Name, Stored; public int Breed, Level; public byte[] Bytes; }

        static readonly List<Entry> _entries = new List<Entry>();
        static string _world;   // world id the entries belong to
        static string Dir => Path.Combine(BepInEx.Paths.ConfigPath, "DogShare.kennel");
        static string FileFor(string world) => Path.Combine(Dir, Safe(world) + ".txt");
        static string LegacyFile => Path.Combine(BepInEx.Paths.ConfigPath, "DogShare.kennel.txt");   // 1.5.0 test builds

        static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        static string WorldId()
        {
            try
            {
                var gd = GameController.instance?.gameData;
                if (gd == null) return null;
                if (!string.IsNullOrEmpty(gd.id)) return gd.id;
                return "created-" + gd.createdDateTime.ToString(CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- file
        static bool Ensure()
        {
            var w = WorldId();
            if (string.IsNullOrEmpty(w)) return false;
            if (w == _world) return true;
            _world = w;
            _entries.Clear();
            try
            {
                Directory.CreateDirectory(Dir);
                Read(FileFor(w));
                if (File.Exists(LegacyFile))
                {
                    int before = _entries.Count;
                    Read(LegacyFile);
                    Save();
                    File.Move(LegacyFile, LegacyFile + ".moved-to-" + Safe(w));
                    DLog.Info("kennel", $"Moved {_entries.Count - before} dog(s) from the 1.5.0 test kennel into this world's kennel");
                }
                DLog.Info("kennel", $"World {DLog.S(w)}: kennel has {_entries.Count} dog(s) [{string.Join(", ", _entries.ConvertAll(x => x.Name + " (" + x.OwnerName + ")"))}]");
            }
            catch (Exception e) { DLog.Error("kennel", "Couldn't read the kennel file", e); }
            return true;
        }

        static void Read(string path)
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split('\t');
                try
                {
                    Entry e;
                    if (p.Length >= 9) e = new Entry { Guid = p[0], Owner = p[1], OwnerName = p[2], Name = p[3], Breed = I(p[4]), Level = I(p[5]), Stored = p[6], Bytes = Convert.FromBase64String(p[8]) };
                    else if (p.Length >= 5) e = new Entry { Guid = p[0], Owner = p[1], OwnerName = "?", Name = p[2], Stored = p[3], Bytes = Convert.FromBase64String(p[4]) };   // 1.5.0 test format
                    else continue;
                    if (!_entries.Exists(x => x.Guid == e.Guid)) _entries.Add(e);
                }
                catch (Exception ex) { DLog.Error("kennel", $"Couldn't read kennel entry {DLog.S(p.Length > 0 ? p[0] : "?")} - left in the file", ex); }
            }
        }

        static void Save()
        {
            Directory.CreateDirectory(Dir);
            var path = FileFor(_world);
            var lines = new List<string>();
            foreach (var x in _entries)
                lines.Add(string.Join("\t", x.Guid, x.Owner, Clean(x.OwnerName), Clean(x.Name), x.Breed, x.Level, x.Stored, "v1", Convert.ToBase64String(x.Bytes)));
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines);
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        // ---------------------------------------------------------------- the game's save format
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

        static bool IsHostOrOffline() { try { return !NetworkManager.isOnline || !NetworkManager.isClient; } catch { return true; } }
        static bool IsOnlineHost() { try { return NetworkManager.isOnline && !NetworkManager.isClient; } catch { return false; } }

        // ---------------------------------------------------------------- host: store / restore (any owner)
        // Returns the message for the owner.
        internal static string Store(GOAPAgent_Pet dog, HumanCharacterController owner, bool ownerIsGuest)
        {
            if (!Ensure()) return "The kennel isn't ready yet - try again in a moment.";
            var data = dog?.npcData; var pd = data?.petData;
            string name = data?.characterData?.characterName ?? "your dog";
            if (pd == null) return "That dog has no pet data.";
            if (pd.ridingWithOwner) return $"{name} is in a car - get out first.";
            if (pd.isUnconscious) return $"{name} is unconscious.";
            var mgr = GOAPNPCManager.instance;
            if (mgr == null) return "The kennel isn't ready yet - try again in a moment.";
            _entries.RemoveAll(x => x.Guid == data.npcGUID);

            DLog.Info("kennel", $"Storing '{name}' ({DLog.S(data.npcGUID)}) for {DogShareBehaviour.GuestName(owner)}: {Describe(data)}");
            DropHeld(pd, owner, name);

            // 1. save and check it reads back
            var bytes = ToBytes(data);
            var check = FromBytes(bytes);
            if (check == null || check.npcGUID != data.npcGUID || check.petData == null)
            {
                DLog.Error("kennel", $"  '{name}' didn't survive a save/load check ({bytes.Length} bytes) - not stored");
                return $"Couldn't store {name} safely - it stays here.";
            }
            DLog.Info("kennel", $"  saved {bytes.Length} bytes, read back OK");

            // 2. write the file first
            _entries.Add(new Entry
            {
                Guid = data.npcGUID, Owner = DogShareBehaviour.IdOf(owner), OwnerName = DogShareBehaviour.GuestName(owner), Name = name,
                Breed = pd.breedId, Level = pd.affectionLevel, Stored = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Bytes = bytes
            });
            try { Save(); }
            catch (Exception ex)
            {
                _entries.RemoveAll(x => x.Guid == data.npcGUID);
                DLog.Error("kennel", $"  couldn't write the kennel file - '{name}' stays in the world", ex);
                return $"Couldn't save the kennel file - {name} stays here.";
            }

            // 3. take it out of the world and off every team
            var host = DogShareBehaviour.HostPlayer();
            try { owner.characterData?.RemoveTeamNPC(data); } catch { }
            if (host != null && host.Pointer != owner.Pointer) DogShareBehaviour.DropFromHostTeam(host, data, "stored in the kennel");
            try { TeamNPCPanel.instance?.RemoveNPC(data); } catch { }
            try { PetPanel.NotifyPetRemoved(data); } catch { }
            try { owner.characterData?.SyncTeamUI(); } catch { }
            try { mgr.DespawnNPCInstance(data.npcGUID); } catch (Exception e) { DLog.Warn("kennel", "  DespawnNPCInstance: " + e.Message); }
            try { mgr.RemoveNPC(data.npcGUID); } catch (Exception e) { DLog.Error("kennel", "  RemoveNPC failed", e); }
            bool gone = mgr.GetNPCData(data.npcGUID) == null;
            DLog.Info("kennel", $"  out of the game's NPC list: {(gone ? "yes" : "NO")}; kennel now has {_entries.Count} dog(s)");
            return $"{name} is in the kennel. Press {Plugin.KennelRestoreKey.Value} to bring a dog back.";
        }

        // A dog carrying something (a ball, a gift) drops it at its owner's feet first, so the item isn't lost.
        static void DropHeld(PetData pd, HumanCharacterController owner, string name)
        {
            ItemData held = null;
            try { held = pd.holdingItem; } catch { }
            if (held == null) return;
            try
            {
                var go = GameController.instance.DropItemByPlayer(held, DogShareBehaviour.Pos(owner), 0f, 0f, 0, -128);
                DLog.Info("kennel", $"  '{name}' was carrying item {held.itemId} - dropped it at {DogShareBehaviour.GuestName(owner)}'s feet ({(go != null ? "ok" : "no object back")})");
            }
            catch (Exception e) { DLog.Error("kennel", $"  dropping what '{name}' was carrying failed", e); }
            try { pd.holdingItem = null; } catch { }
        }

        internal static string Restore(string guid, HumanCharacterController owner, bool ownerIsGuest)
        {
            if (!Ensure()) return "The kennel isn't ready yet - try again in a moment.";
            string ownerId = DogShareBehaviour.IdOf(owner);
            var e = _entries.Find(x => x.Guid == guid);
            if (e == null) return "That dog isn't in the kennel any more.";
            if (e.Owner != ownerId) return $"{e.Name} isn't your dog.";
            var mgr = GOAPNPCManager.instance;
            if (mgr == null) return "The kennel isn't ready yet - try again in a moment.";

            int cap = ownerIsGuest ? Plugin.MaxPerGuest.Value : 3;
            bool full;
            if (ownerIsGuest) full = DogShareBehaviour.Dogs().FindAll(d => DogShareBehaviour.OwnedBy(d, owner)).Count >= cap;
            else { full = false; try { full = !PetSystem.CanRescueMorePets(); } catch { } }
            if (full) return "You already have the most dogs you can keep. Store one first.";

            if (mgr.GetNPCData(e.Guid) != null)
            {
                DLog.Warn("kennel", $"'{e.Name}' ({DLog.S(e.Guid)}) is already in the world - dropping the kennel copy");
                _entries.Remove(e); Save();
                return $"{e.Name} is already out.";
            }

            var data = FromBytes(e.Bytes);
            if (data == null) { DLog.Error("kennel", $"'{e.Name}' couldn't be read back ({e.Bytes.Length} bytes) - kept in the kennel"); return $"Couldn't load {e.Name} - it stays in the kennel."; }
            DLog.Info("kennel", $"Bringing back '{e.Name}' ({DLog.S(e.Guid)}) for {DogShareBehaviour.GuestName(owner)}: {Describe(data)}");

            Vector2 at = DogShareBehaviour.Pos(owner) + new Vector2(1f, 0f);
            data.leaderCharacterGUID = ownerId;
            try { data.npcPosition = at; } catch { }
            try { if (data.petData != null) { data.petData.ridingWithOwner = false; data.petData.basicCommand = "Follow"; } } catch { }

            mgr.AddNPC(data);
            try { mgr.InitNPCNow(e.Guid); } catch (Exception ex) { DLog.Warn("kennel", "  InitNPCNow: " + ex.Message); }
            GOAPAgent_Pet agent = null;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<GOAPAgent_Pet>()) if (p?.npcData != null && p.npcData.npcGUID == e.Guid) { agent = p; break; }
            if (agent != null)
            {
                try { agent.SetLeader(owner); } catch (Exception ex) { DLog.Warn("kennel", "  SetLeader: " + ex.Message); }
                try { agent.transform.position = new Vector3(at.x, at.y, agent.transform.position.z); } catch { }
            }
            DLog.Info("kennel", $"  in the NPC list: {(mgr.GetNPCData(e.Guid) != null ? "yes" : "NO")}, dog in the world: {(agent != null ? "yes" : "no (appears when someone is near)")}");
            if (!ownerIsGuest)
            {
                try { owner.characterData.AddTeamNPC(data); owner.characterData.SyncTeamUI(); } catch (Exception ex) { DLog.Warn("kennel", "  AddTeamNPC: " + ex.Message); }
                try { PetPanel.NotifyPetChanged(data); } catch { }
            }

            if (mgr.GetNPCData(e.Guid) == null)
            {
                DLog.Error("kennel", $"  '{e.Name}' didn't make it into the world - kept in the kennel");
                return $"Couldn't bring {e.Name} back - it stays in the kennel.";
            }
            _entries.Remove(e); Save();
            return $"{e.Name} is back from the kennel.";
        }

        internal static List<Entry> ListFor(string ownerId)
        {
            Ensure();
            return _entries.FindAll(x => x.Owner == ownerId);
        }

        // ---------------------------------------------------------------- host: guest requests (from HostHandler)
        static int _seq;
        internal static bool HandleGuest(string op, string[] p, string charId, NetPeer peer)
        {
            if (op != "KS" && op != "KL" && op != "KR") return false;
            if (!IsOnlineHost()) return true;
            string to = Proto.Short(charId);
            var guest = DogShareBehaviour.GuestById(charId);
            if (guest == null) { HostHandler.Reply(peer, Proto.Join("M", to, "Kennel: the host can't find your character yet.")); return true; }
            string msg = null;
            try
            {
                if (op == "KS" && p.Length > 1)
                {
                    GOAPAgent_Pet dog = null;
                    foreach (var d in DogShareBehaviour.Dogs())
                        if (d.npcData != null && d.npcData.leaderCharacterGUID == charId && Proto.Short(d.npcData.npcGUID) == p[1]) { dog = d; break; }
                    msg = dog == null ? "That dog isn't yours any more." : Store(dog, guest, true);
                }
                else if (op == "KR" && p.Length > 1) msg = Restore(p[1], guest, true);
                if (op != "KL") HostHandler.SendList(peer, charId);
            }
            catch (Exception e) { DLog.Error("kennel", $"Kennel request {op} from {DogShareBehaviour.GuestName(guest)} failed", e); msg = "The kennel had a problem - the host's DogShare.log has details."; }
            if (!string.IsNullOrEmpty(msg)) HostHandler.Reply(peer, Proto.Join("M", to, Proto.Clean(msg, 150)));
            SendKennelList(peer, charId);
            return true;
        }

        static void SendKennelList(NetPeer peer, string charId)
        {
            string to = Proto.Short(charId);
            int seq = ++_seq;
            var mine = ListFor(charId);
            foreach (var e in mine)
                HostHandler.Reply(peer, Proto.Join("KE", to, seq, e.Guid, Proto.Clean(e.Name, 20), e.Breed, e.Level, Proto.Clean(e.Stored, 16)));
            HostHandler.Reply(peer, Proto.Join("KX", to, seq, mine.Count));
        }

        // ---------------------------------------------------------------- guest: replies from the host
        static readonly List<Entry> _remote = new List<Entry>();
        static readonly List<Entry> _incoming = new List<Entry>();
        static int _incomingSeq = -1;
        static bool _remoteLoaded;
        static float _waitSince = -1f;   // when the last kennel request went to the host (cleared by its KX)
        static bool _noAnswer;           // the host never answered: it's on a DogShare without the kennel

        static void Ask(params object[] parts)
        {
            _waitSince = Time.unscaledTime; _noAnswer = false;
            GuestMirror.Send(parts);
        }

        static void CheckWait()
        {
            if (_waitSince < 0f || Time.unscaledTime - _waitSince < 6f) return;
            _waitSince = -1f; _noAnswer = true; _remoteLoaded = true; _remote.Clear();
            _status = "";
            DLog.Warn("kennel", "The host didn't answer a kennel request - the host needs DogShare 1.5.0 or newer");
            if (!_open) GuestMirror.Note("The host didn't answer. The kennel needs DogShare 1.5.0 or newer on the host.");
        }

        internal static bool GuestOnMessage(string[] p)
        {
            if (p[0] == "KE" && p.Length >= 8)
            {
                int seq = I(p[2]);
                if (seq != _incomingSeq) { _incoming.Clear(); _incomingSeq = seq; }
                _incoming.Add(new Entry { Guid = p[3], Name = p[4], Breed = I(p[5]), Level = I(p[6]), Stored = p[7] });
                return true;
            }
            if (p[0] == "KX" && p.Length >= 4)
            {
                int seq = I(p[2]);
                if (seq != _incomingSeq) _incoming.Clear();
                _remote.Clear(); _remote.AddRange(_incoming); _incoming.Clear(); _incomingSeq = -1;
                _remoteLoaded = true; _waitSince = -1f; _noAnswer = false;
                DLog.Info("kennel", $"Host says my kennel has {_remote.Count} dog(s): [{string.Join(", ", _remote.ConvertAll(x => x.Name))}]");
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- input (every PC)
        static bool _open;
        static string _status = "";
        static int _sel;   // selected row in the list (arrow keys), brought back with Space / Enter

        internal static void Tick()
        {
            bool guest = GuestMirror.IsGuest();
            if (guest && !GuestMirror.HostHasMod)
            {
                if (DogShareBehaviour.Pressed(Plugin.KennelStoreKey.Value) || DogShareBehaviour.Pressed(Plugin.KennelRestoreKey.Value))
                    GuestMirror.Note("The kennel needs DogShare on the host too.");
                return;
            }
            if (guest) CheckWait();
            if (DogShareBehaviour.Pressed(Plugin.KennelStoreKey.Value)) Run("store", () => { if (guest) GuestStore(); else HostStore(); });
            else if (DogShareBehaviour.Pressed(Plugin.KennelRestoreKey.Value))
            {
                _open = !_open;
                _status = "";
                if (_open && guest) { _remoteLoaded = false; Ask("KL"); }
                if (_open && !guest) Ensure();
            }
            if (!_open) return;
            if (DogShareBehaviour.Pressed(KeyCode.Escape)) { _open = false; return; }
            var list = CurrentList(guest);
            if (list.Count == 0) { _sel = 0; return; }
            if (DogShareBehaviour.Pressed(KeyCode.UpArrow)) _sel = (_sel - 1 + list.Count) % list.Count;
            else if (DogShareBehaviour.Pressed(KeyCode.DownArrow)) _sel = (_sel + 1) % list.Count;
            _sel = Math.Max(0, Math.Min(_sel, list.Count - 1));
            if (DogShareBehaviour.Pressed(KeyCode.Space) || DogShareBehaviour.Pressed(KeyCode.Return)) BringBack(list[_sel], guest);
        }

        static List<Entry> CurrentList(bool guest)
        {
            if (guest) return _remoteLoaded ? _remote : new List<Entry>();
            var me = DogShareBehaviour.HostPlayer();
            return me == null ? new List<Entry>() : ListFor(DogShareBehaviour.IdOf(me));
        }

        static void BringBack(Entry e, bool guest)
        {
            var guid = e.Guid;
            if (guest)
            {
                DLog.Info("kennel", $"Asking the host to bring back '{e.Name}'");
                _remoteLoaded = false; Ask("KR", guid);
                _status = $"Asking the host for {e.Name}...";
            }
            else Run("bring back", () => { var me = DogShareBehaviour.HostPlayer(); if (me != null) { _status = Restore(guid, me, false); DogShareBehaviour.Say(_status); } });
        }

        static void Run(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { DLog.Error("kennel", $"Kennel {what} failed", e); DogShareBehaviour.Say($"DogShare kennel: {what} failed - see DogShare.log"); }
        }

        static void HostStore()
        {
            var me = DogShareBehaviour.HostPlayer();
            if (me == null) { DogShareBehaviour.Say("DogShare kennel: couldn't find your character."); return; }
            var mine = DogShareBehaviour.Dogs().FindAll(d => DogShareBehaviour.OwnedBy(d, me));
            var dog = Nearest(mine, DogShareBehaviour.Pos(me));
            if (dog == null) { DogShareBehaviour.Say("DogShare kennel: none of your dogs are close by."); return; }
            DogShareBehaviour.Say(Store(dog, me, false));
        }

        static void GuestStore()
        {
            var me = DogShareBehaviour.HostPlayer();   // on a guest this is the guest's own character
            if (me == null) return;
            GuestMirror.Mirror best = null; float bestD = Reach2();
            foreach (var m in GuestMirror.Mirrors())
            {
                if (m.Pc == null) continue;
                float d = (DogShareBehaviour.Pos(m.Pc) - DogShareBehaviour.Pos(me)).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = m; }
            }
            if (best == null) { GuestMirror.Note("None of your dogs are close by."); return; }
            if (GuestMirror.PouchOpenFor(best)) { GuestMirror.Note("Close the saddlebag first, then store the dog."); return; }
            DLog.Info("kennel", $"Asking the host to store '{best.Data.characterData?.characterName}' ({best.Handle})");
            Ask("KS", best.Handle);
        }

        static readonly Dictionary<int, string> _breeds = new Dictionary<int, string>();
        static string BreedName(int id)
        {
            if (_breeds.TryGetValue(id, out var n)) return n;
            try { n = PetSystem.GetBreedName(id); } catch { }
            if (string.IsNullOrEmpty(n)) n = "breed " + id;
            _breeds[id] = n;
            return n;
        }

        static float Reach2() { float r = Plugin.Range.Value * 2f; return r * r; }

        static GOAPAgent_Pet Nearest(List<GOAPAgent_Pet> dogs, Vector2 from)
        {
            GOAPAgent_Pet best = null; float bestD = Reach2();
            foreach (var d in dogs) { float dd = (DogShareBehaviour.Pos(d) - from).sqrMagnitude; if (dd <= bestD) { bestD = dd; best = d; } }
            return best;
        }

        // ---------------------------------------------------------------- list window (IMGUI)
        internal static void OnGUI()
        {
            if (!_open) return;
            try
            {
                bool guest = GuestMirror.IsGuest();
                var list = CurrentList(guest);
                const float w = 380f, row = 26f;
                const int maxRows = 8;
                int rows = Math.Max(1, Math.Min(list.Count, maxRows));
                float h = 64f + rows * row + (_status.Length > 0 ? 24f : 0f) + (guest && _noAnswer ? row : 0f);
                var r = new Rect((Screen.width - w) / 2f, Screen.height * 0.2f, w, h);
                GUI.Box(r, list.Count > maxRows ? $"Kennel ({_sel + 1} / {list.Count})" : "Kennel");
                float y = r.y + 26f;
                if (guest && _noAnswer) GUI.Label(new Rect(r.x + 12f, y, w - 24f, row * 2f), "The host didn't answer. The kennel needs DogShare 1.5.0 or newer on the host.");
                else if (guest && !_remoteLoaded) GUI.Label(new Rect(r.x + 12f, y, w - 24f, row), "Asking the host...");
                else if (list.Count == 0 && !_noAnswer) GUI.Label(new Rect(r.x + 12f, y, w - 24f, row), $"No dogs in the kennel. Press {Plugin.KennelStoreKey.Value} next to a dog to store it.");
                int first = Math.Max(0, Math.Min(_sel - maxRows + 1, list.Count - maxRows));
                for (int i = first; i < Math.Min(list.Count, first + maxRows); i++)
                {
                    var e = list[i];
                    var line = new Rect(r.x + 8f, y, w - 16f, row - 2f);
                    if (i == _sel) GUI.Box(line, "");
                    GUI.Label(new Rect(line.x + 6f, line.y + 3f, line.width - 12f, row), $"{(i == _sel ? "> " : "   ")}{e.Name}  ({BreedName(e.Breed)}, level {e.Level}, stored {e.Stored})");
                    y += row;
                }
                if (_status.Length > 0) GUI.Label(new Rect(r.x + 12f, r.yMax - 46f, w - 24f, 22f), _status);
                GUI.Label(new Rect(r.x + 12f, r.yMax - 24f, w - 24f, 22f), $"Up / Down: choose    Space: bring back    {Plugin.KennelRestoreKey.Value} / Esc: close");
            }
            catch (Exception e) { DLog.ErrorThrottled("kennelgui", 30f, "kennel", "Drawing the kennel list failed", e); _open = false; }
        }
    }
}
