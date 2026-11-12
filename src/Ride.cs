using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ZedZone.Net;

namespace DogShare
{
    // A guest's dog riding in a car with its owner.
    //
    // How the game does it (host):
    //  * When the dog's owner is driving or riding (HumanCharacterController.isDriving), the dog walks to the car and
    //    GOAPAgent_Pet.RideWithOwner sets PetData.ridingWithOwner, moves the dog's npcPosition to the owner and
    //    despawns the dog. It now lives only as data.
    //  * GOAPNPCManager.UpdateOfflinePet keeps that data at the owner's transform position while the owner is
    //    driving, and when the owner gets out it clears ridingWithOwner and puts the dog next to the owner again.
    //
    // Why a guest's dog gets left behind:
    //  * On the host a guest is a stand-in (net puppet). NetPlayerSync.UpdatePuppets stops moving a stand-in while
    //    it is in a vehicle (the car itself is moved by NetVehicleSync), so the stand-in's transform stays where the
    //    guest got in. The dog's data is kept "with its owner" at that spot for the whole drive, and when the guest
    //    gets out the dog is put back next to that spot - wherever the car started. The host's own dogs are fine
    //    because the host's character does move with the car.
    //
    // Fix (host only, guest-owned dogs only):
    //  * While a guest's dog is riding and its owner is in a vehicle, keep the dog's data on the vehicle's position.
    //  * When the owner gets out (the game clears ridingWithOwner), put the dog next to where the car is, not where
    //    the drive started. If the game hasn't let the dog out a few seconds after its owner left the car, let it out.
    internal static class Ride
    {
        class Rider
        {
            public GOAPNPCData Npc;
            public string Name;
            public HumanCharacterController Owner;
            public Vector2 LastCar;
            public bool HasCar;
            public float OwnerOutSince = -1f;
        }

        static readonly Dictionary<string, Rider> _riders = new Dictionary<string, Rider>();
        const float LetOutAfter = 3f;      // seconds after the owner left the car before DogShare lets the dog out itself
        const float ExitOffset = 1.5f;     // how far from the car the dog is put when it gets out

        // ---------------------------------------------------------------- guest lookup (cached; UpdateOfflinePet runs often)
        static Dictionary<string, HumanCharacterController> _guests = new Dictionary<string, HumanCharacterController>();
        static float _guestsAt = -10f;

        static HumanCharacterController Guest(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (Time.unscaledTime - _guestsAt > 0.5f)
            {
                _guestsAt = Time.unscaledTime;
                var map = new Dictionary<string, HumanCharacterController>();
                foreach (var g in DogShareBehaviour.Guests())
                {
                    var gid = DogShareBehaviour.IdOf(g);
                    if (!string.IsNullOrEmpty(gid)) map[gid] = g;
                }
                _guests = map;
            }
            return _guests.TryGetValue(id, out var h) && h != null ? h : null;
        }

        static bool IsHost()
        {
            try { return NetworkManager.isOnline && !NetworkManager.isClient; } catch { return false; }
        }

        static BasicVehicle CarOf(HumanCharacterController h)
        {
            try { var v = h?.drivingVehicle; return v != null ? v : null; } catch { return null; }
        }

        static string NameOf(GOAPNPCData npc)
        {
            try { var n = npc?.characterData?.characterName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return DLog.S(npc?.npcGUID);
        }

        static bool Riding(GOAPNPCData npc)
        {
            try { return npc?.petData != null && npc.petData.ridingWithOwner; } catch { return false; }
        }

        static Vector2 NextTo(Vector2 car)
        {
            var off = UnityEngine.Random.insideUnitCircle;
            if (off.sqrMagnitude < 0.01f) off = Vector2.right;
            return car + off.normalized * ExitOffset;
        }

        // ---------------------------------------------------------------- hooks
        // RideWithOwner just ran on the host: the dog is now riding.
        internal static void OnBoarded(GOAPAgent_Pet dog)
        {
            if (!IsHost() || dog == null) return;
            var npc = dog.npcData;
            if (npc == null || !Riding(npc)) return;
            var owner = Guest(npc.leaderCharacterGUID);
            if (owner == null) return;                       // the host's own dog: the game handles it
            var r = new Rider { Npc = npc, Name = NameOf(npc), Owner = owner };
            _riders[npc.npcGUID] = r;
            var car = CarOf(owner);
            if (car != null) Follow(r, car);
            DLog.Info("car", $"'{r.Name}' got in the car with {DogShareBehaviour.GuestName(owner)} - keeping it with the car");
        }

        // After the game's offline update for a pet (runs while the dog is despawned, e.g. riding in a car).
        internal static void AfterOfflineUpdate(GOAPNPCData npc)
        {
            if (!IsHost() || npc == null || string.IsNullOrEmpty(npc.npcGUID)) return;
            _riders.TryGetValue(npc.npcGUID, out var r);
            bool riding = Riding(npc);
            if (r == null)
            {
                if (!riding) return;
                // riding but we missed the boarding (e.g. DogShare loaded mid-drive)
                var owner = Guest(npc.leaderCharacterGUID);
                if (owner == null) return;
                r = new Rider { Npc = npc, Name = NameOf(npc), Owner = owner };
                _riders[npc.npcGUID] = r;
                DLog.Info("car", $"'{r.Name}' is riding with {DogShareBehaviour.GuestName(owner)} - keeping it with the car");
            }
            if (riding)
            {
                var car = CarOf(r.Owner);
                if (car != null) Follow(r, car);             // overrides the stale stand-in position the game just used
            }
            else
            {
                _riders.Remove(npc.npcGUID);
                GotOut(r, "the game let it out");
            }
        }

        // Every frame on the host: keep riding dogs on their owner's car even if the game doesn't update them
        // (far from the host), and let a dog out if its owner left the car and the game didn't.
        internal static void HostTick()
        {
            if (_riders.Count == 0) return;
            if (!IsHost()) { _riders.Clear(); return; }
            List<string> done = null;
            foreach (var kv in _riders)
            {
                var r = kv.Value;
                try
                {
                    if (r.Npc == null || r.Owner == null || r.Owner.WasCollected) { (done ??= new List<string>()).Add(kv.Key); continue; }
                    if (!Riding(r.Npc))
                    {
                        GotOut(r, "the game let it out");
                        (done ??= new List<string>()).Add(kv.Key);
                        continue;
                    }
                    var car = CarOf(r.Owner);
                    if (car != null) { r.OwnerOutSince = -1f; Follow(r, car); continue; }

                    if (r.OwnerOutSince < 0f) r.OwnerOutSince = Time.unscaledTime;
                    else if (Time.unscaledTime - r.OwnerOutSince >= LetOutAfter)
                    {
                        try { r.Npc.petData.ridingWithOwner = false; } catch { }
                        GotOut(r, $"its owner left the car {LetOutAfter:0}s ago and the game didn't let it out");
                        (done ??= new List<string>()).Add(kv.Key);
                    }
                }
                catch (Exception e)
                {
                    DLog.ErrorThrottled("ride|" + kv.Key, 30f, "car", $"Keeping '{r.Name}' with the car failed", e);
                    (done ??= new List<string>()).Add(kv.Key);
                }
            }
            if (done != null) foreach (var k in done) _riders.Remove(k);
        }

        static void Follow(Rider r, BasicVehicle car)
        {
            Vector2 p = car.transform.position;
            r.LastCar = p;
            r.HasCar = true;
            r.Npc.npcPosition = p;
        }

        // Callers take the dog off _riders themselves (HostTick can't while it walks the list).
        static void GotOut(Rider r, string why)
        {
            if (!r.HasCar || r.Npc == null) return;
            // the game placed the dog next to the stand-in, which may still be where the drive started
            var p = NextTo(r.LastCar);
            try { r.Npc.npcPosition = p; } catch { }
            DLog.Info("car", $"'{r.Name}' got out of the car with {DogShareBehaviour.GuestName(r.Owner)} at ({p.x:0},{p.y:0}) ({why})");
        }
    }

    internal static class RidePatches
    {
        [HarmonyPatch(typeof(GOAPAgent_Pet), nameof(GOAPAgent_Pet.RideWithOwner))]
        [HarmonyPostfix]
        static void Boarded(GOAPAgent_Pet __instance)
        {
            try { Ride.OnBoarded(__instance); } catch (Exception e) { DLog.ErrorThrottled("rideboard", 30f, "car", "A guest's dog getting in a car failed", e); }
        }

        [HarmonyPatch(typeof(GOAPNPCManager), nameof(GOAPNPCManager.UpdateOfflinePet))]
        [HarmonyPostfix]
        static void OfflinePet(GOAPNPCData __0)
        {
            long t0 = Hitch.Start();
            try { Ride.AfterOfflineUpdate(__0); } catch (Exception e) { DLog.ErrorThrottled("rideoffline", 30f, "car", "Keeping a guest's dog with the car failed", e); }
            finally { Hitch.Stop("car check", t0); }
        }
    }
}
