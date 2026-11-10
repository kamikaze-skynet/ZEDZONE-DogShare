# DogShare (ZED ZONE)

Co-op dogs for guests in ZED ZONE.

In co-op the game only allows **one dog for the whole session**, and only the host can rescue it. Dogs are run on the host's PC, so guests never even get the "rescue" prompt at a dog cage. DogShare lets the host hand dogs to the other players.

- The host can keep **3 dogs** in co-op again, like single-player.
- **F7** (host): gives the dog closest to you to the player closest to you. Each guest can have up to 3.
- **F8** (host): takes back the guest's dog closest to you.
- A guest's dog follows them and fights for them. It's saved in the host's world, and links back to its owner when they rejoin.
- **Guests use their dog like their own:** walk up to it for the game's **Commands / Pet** prompt, see it on the Teammates card, and use its orders menu (Follow, Stay, Passive, Defensive, Aggressive), rename it and spend its skill points. Each action is sent to the host and done there with the game's own code.
- Guest dogs don't need feeding or healing from the guest. DogShare keeps them fed, and an unconscious dog wakes up by itself as usual.

**Everyone should have DogShare:** the host to give dogs, guests for the prompts and Teammates card. A guest without it still gets a dog that follows and fights for them.

## Requirements

- ZED ZONE (Windows, Steam)
- [ZED ZONE Code Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258) (BepInEx 6 IL2CPP + ZedModsLoader). Run its `install.cmd` once.

## Install

- **Steam Workshop:** subscribe to [DogShare](https://steamcommunity.com/sharedfiles/filedetails/?id=3837154256). The loader picks it up automatically.
- **Manually:** put `DogShare.dll` in `%USERPROFILE%\AppData\LocalLow\LevenLiu\ZEDZONE\Mods\DogShare\`.

Start the game through Steam. `BepInEx\LogOutput.log` should show `DogShare 1.0.0 loaded`.

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
| `Rules.MaxDogsPerGuest` | `3` | How many dogs each guest can be given. |
| `Rules.Range` | `8` | How close the guest / dog has to be. |
| `Rules.KeepGuestDogsFed` | `true` | Keep guest-owned dogs' hunger full. |

## How it works

- `NetSim.singlePetSession` is true whenever the game is online. `PetSystem.CanRescueMorePets` then allows 1 pet instead of 3, and extra pets are frozen. DogShare makes it false on the host only.
- On a guest's PC the host's dog is a stand-in `PetCharacterController` without pet data. DogShare gives it a mirror `GOAPNPCData`/`PetData` naming the guest as owner (matched by `NetMonsterSync` net id), so the game's own prompts, Teammates card and orders menu appear. Every action on a mirror (`ExecutePetCommand`, `ExecuteNPCCommand`, petting's `AddAffection`, skill upgrades, rename, release) is caught and sent to the host.
- Guests talk to the host with hidden lines on the game's co-op chat (`MsgChat` marked `@@DS1|`). The host answers only that guest with `NetworkManager.SendTo`, and every marked line is dropped in `NetChatSync.Append`, so nobody sees them. The host runs the game's own `ExecutePetCommand`, `TryUpgradeSkill`, `ReleasePet` etc. and only accepts requests for dogs owned by the sender's character.
- A dog's owner is its GOAP "leader". DogShare calls the game's own `GOAPAgent.SetLeader` with the guest's player character on the host, the same call hired teammates use, so the dog's owner id becomes the guest's character id and is saved with the world.

## Known limits

- Feed and first aid are switched off for shared dogs (the host's world keeps them fed; they wake up on their own).
- Gear (collar, vest, saddlebag) and the saddlebag pouch aren't available to guests yet, since they'd mean moving items between the guest's inventory and the host's world.
- The host can't open the game's menu on a guest's dog (the game checks the owner).
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
