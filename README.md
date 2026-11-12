# DogShare (ZED ZONE)

Co-op dogs for guests in ZED ZONE.

In co-op the game only allows **one dog for the whole session**, and only the host can rescue it. Dogs are run on the host's PC, so guests never even get the "rescue" prompt at a dog cage. DogShare lets the host hand dogs to the other players.

- The host can keep **3 dogs** in co-op again, like single-player.
- **F7** (host): gives the dog closest to you to the player closest to you. Each guest can have up to 3.
- **F8** (host): takes back the guest's dog closest to you.
- **Kennel (everyone):** **F9** puts your dog closest to you in the kennel. It leaves the world and your team, and keeps its name, breed, affection, skills, gear and saddlebag, and it doesn't get hungry. **F10** opens the kennel list; pick a dog with the **Up / Down** arrows and press **Space** to bring it back next to you. The host keeps each player's kennel per world (`BepInEx/config/DogShare.kennel/`), so a guest's stored dogs wait for them.
- A guest's dog follows them and fights for them. It's saved in the host's world, and links back to its owner when they rejoin.
- **Guests use their dog like their own:** the game's **Commands / Pet / Feed** prompt at the dog, the Teammates card and its orders menu (Follow, Stay, Passive, Defensive, Aggressive), rename, skills, first aid, gear (collar, vest, saddlebag) and the saddlebag's storage. Food, medicine and gear come from and go back to the guest's own backpack; the host does each action on the real dog with the game's own code.
- **Animations and sounds are synced** for players with DogShare: dogs sitting to be petted or eating, their barks, whines and happy sounds, and the petting pose reach everyone (the game itself only plays them on the host / the PC doing it).
- **Shared dogs get hungry like any dog**, and whoever owns one feeds it (the host his own, a guest theirs, with the game's food picker). `AutoFeedGuestDogs` turns on an automatic top-up instead.
- **Fetch:** a guest throws a tennis ball, their dog runs after it and brings it back to their backpack (or their feet if it's full).
- **Gifts:** when a guest's dog brings them something, the guest gets the game's "Bear brought you a dead crow" notice (the game only showed it to the host).
- **Speech bubbles** over dogs show for everyone.

**Everyone should have DogShare:** the host to give dogs, guests for the prompts and Teammates card. A guest without it still gets a dog that follows and fights for them.

## Requirements

- ZED ZONE (Windows, Steam)
- [ZED ZONE Code Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258) (BepInEx 6 IL2CPP + ZedModsLoader). Run its `install.cmd` once.

## Install

- **Steam Workshop:** subscribe to [DogShare](https://steamcommunity.com/sharedfiles/filedetails/?id=3837154256). The loader picks it up automatically.
- **Manually:** put `DogShare.dll` in `%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE\Mods\DogShare\`.

Start the game through Steam. `BepInEx\LogOutput.log` should show `DogShare <version> loaded`.

## How to use it

1. Host: rescue a dog from a cage at a Scavenger Camp and set it to follow you (a dog on "stay" won't follow its new owner).
2. Stand next to your friend and press **F7**. The dog switches to them.
3. Rescue another dog. You can keep 3 yourself and give each guest up to 3.
4. To take a dog back, stand next to it and press **F8**.

## Config

`BepInEx\config\girthy_mods.zedzone.dogshare.cfg` (created after the first launch):

| Setting | Default | What it does |
|---|---|---|
| `Keys.GiveDogKey` | `F7` | Give your nearest dog to the nearest guest. |
| `Keys.TakeBackDogKey` | `F8` | Take back the nearest guest-owned dog. |
| `Keys.KennelStoreKey` | `F9` | Put your nearest dog in the kennel. |
| `Keys.KennelBringBackKey` | `F10` | Open / close the kennel list. |
| `Rules.MaxDogsPerGuest` | `3` | How many dogs each guest can be given. |
| `Rules.Range` | `8` | How close the guest / dog has to be. |
| `Rules.AutoFeedGuestDogs` | `false` | Keep guest-owned dogs' hunger full automatically. Off: guest dogs get hungry like your own and their owner feeds them. |
| `Debug.VerboseLog` | `true` | Also log every request and reply (DEBUG lines). |

## Logs (for testing and bug reports)

DogShare writes its own log to **`BepInEx\DogShare.log`** in the game folder (rewritten every launch), and the same lines go to `BepInEx\LogOutput.log`. Each line has a time and a category:

| Category | What it covers |
|---|---|
| `start` | Loading, which game methods were hooked, and anything that failed to hook |
| `role` | Whether you're offline, the host or a guest |
| `host` | Giving / taking back dogs, re-linking a guest's dog, hunger top-ups (if AutoFeedGuestDogs is on) |
| `req` | Each request the host got from a guest, and its result |
| `send` | What the host sent back (dog lists, gear copies) |
| `guest` | On a guest's PC: shared dogs found, pet data attached, Teammates card |
| `act` | On a guest's PC: each action sent to the host (orders, petting, skills, rename) |
| `item` | Feeding, first aid, gear and saddlebag items moving between PCs |
| `car` | A guest's dog getting in and out of a car with its owner |
| `status` | A summary every 30 s (only when something changed) |

Failures are logged as `ERROR` with the reason. `Debug.VerboseLog` (default on) adds the raw requests and replies; turn it off once everything works. To report a problem, send `DogShare.log` from both the host and the guest.

## How it works

- `NetSim.singlePetSession` is true whenever the game is online. `PetSystem.CanRescueMorePets` then allows 1 pet instead of 3, and extra pets are frozen. DogShare makes it false on the host only.
- On a guest's PC the host's dog is a stand-in `PetCharacterController` without pet data. DogShare gives it a mirror `GOAPNPCData`/`PetData` naming the guest as owner (matched by `NetMonsterSync` net id), so the game's own prompts, Teammates card and orders menu appear. Every action on a mirror (`ExecutePetCommand`, `ExecuteNPCCommand`, petting's `AddAffection`, feeding, first aid, skill upgrades, rename, release, gear equip/unequip, saddlebag changes) is caught and sent to the host. Items travel as the game's own bytes (`NetItemSync.SerializeItemData`, `NetContainerSync.SerializeInventory`), base64 in chunks; the host keeps each guest's copy of the dog's gear in sync and only resends what changed.
- Guests talk to the host with hidden lines on the game's co-op chat (`MsgChat` marked `@@DS1|`). The host answers only that guest with `NetworkManager.SendTo`, and every marked line is dropped in `NetChatSync.Append`, so nobody sees them. The host runs the game's own `ExecutePetCommand`, `TryUpgradeSkill`, `ReleasePet` etc. and only accepts requests for dogs owned by the sender's character.
- Fetch: a guest's tennis ball lands through a drop request to the host, and the host only shows the throw as a visual, so the game never calls `PetSystem.NotifyToyThrown` for it. DogShare matches the guest's throw (`NetThrowSync.OnHostReceiveThrow`) to the ball their drop request spawns and calls `NotifyToyThrown` with the guest as thrower. When the dog delivers it (`GOAPAction_Pet_FetchToy.Deliver`), the hand-over to the guest's stand-in is turned into a message that puts the ball in the guest's real backpack.
- Cars: a riding dog is despawned and kept as data next to its owner (`PetData.ridingWithOwner`, `GOAPNPCManager.UpdateOfflinePet`). On the host a guest's stand-in isn't moved while it's in a vehicle (`NetPlayerSync.UpdatePuppets`), so a guest's dog would stay where the drive started. DogShare keeps a guest's riding dog on the vehicle's position and lets it out next to the vehicle.
- A dog's owner is its GOAP "leader". DogShare calls the game's own `GOAPAgent.SetLeader` with the guest's player character on the host, the same call hired teammates use, so the dog's owner id becomes the guest's character id and is saved with the world.

## Known limits

- A guest's saddlebag changes are saved to the host's world about every half second while it's open. If a guest disconnects within that moment, their last move may not be saved.
- The host can't open the game's menu on a guest's dog (the game checks the owner).
- The Teammates card's counter (for example 0/3) counts hired teammates only, not dogs. That's the game's own rule, in single-player too. DogShare adds a separate dog count next to it ("· Dogs 2/3").
- If a guest leaves, their dogs wait in the host's world until they come back.
- Game updates may break it. Check `BepInEx\LogOutput.log` for `DogShare` lines and open an issue.

## Build

Needs the .NET 6+ SDK, and the game with the Code Mod Loader run at least once (so `BepInEx\interop` exists).

```
dotnet build src\DogShare.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\ZED ZONE"
```

Add `-p:CopyToMods=true` to copy the DLL into your Mods folder.

## Publish to the Steam Workshop

```
.\publish-workshop.ps1 -SteamUser <your steam login>
```

It builds the mod, copies the DLL into `workshop\content\DogShare\`, downloads SteamCMD into `tools\` if needed, and uploads with `workshop_build_item`. SteamCMD asks for your password and Steam Guard code itself; nothing is saved by the script. The first upload creates the item and writes its id to `workshop\publishedfileid.txt`, and later runs update that item. The item starts **Private**; pass `-Visibility Public` when you're ready.

## License

MIT
