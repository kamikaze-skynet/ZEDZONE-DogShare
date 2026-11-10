# DogShare (ZED ZONE)

Co-op dogs for guests in ZED ZONE.

In co-op the game only allows **one dog for the whole session**, and only the host can rescue it. Dogs are run on the host's PC, so guests never even get the "rescue" prompt at a dog cage. DogShare lets the host hand dogs to the other players.

- The host can keep **3 dogs** in co-op again, like single-player.
- **F7** (host): gives the dog closest to you to the player closest to you. Each guest can have up to 3.
- **F8** (host): takes back the guest's dog closest to you.
- A guest's dog follows them and fights for them. It's saved in the host's world, and links back to its owner when they rejoin.
- **F6** (guest): opens a menu for the dogs the host gave you: Follow / Stay, combat mode (Passive, Defensive, Aggressive), Pet, Rename, Skills and Release. The host runs each action, so it works the same as on the host's own dogs.
- Guest dogs don't need feeding or healing from the guest. DogShare keeps them fed, and an unconscious dog wakes up by itself as usual.

**The host needs DogShare for giving dogs. Guests need it too if they want the F6 menu.** A guest without it still gets a dog that follows and fights for them.

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
| `Keys.DogMenuKey` | `F6` | Guest: open the menu for your dogs. |
| `Rules.MaxDogsPerGuest` | `3` | How many dogs each guest can be given. |
| `Rules.Range` | `8` | How close the guest / dog has to be. |
| `Rules.KeepGuestDogsFed` | `true` | Keep guest-owned dogs' hunger full. |

## How it works

- `NetSim.singlePetSession` is true whenever the game is online. `PetSystem.CanRescueMorePets` then allows 1 pet instead of 3, and extra pets are frozen. DogShare makes it false on the host only.
- The guest menu talks to the host with hidden lines on the game's co-op chat (`MsgChat` marked `@@DS1|`). The host answers only that guest with `NetworkManager.SendTo`, and every marked line is dropped in `NetChatSync.Append`, so nobody sees them. The host runs the game's own `ExecutePetCommand`, `TryUpgradeSkill`, `ReleasePet` etc. and only accepts requests for dogs owned by the sender's character.
- A dog's owner is its GOAP "leader". DogShare calls the game's own `GOAPAgent.SetLeader` with the guest's player character on the host, the same call hired teammates use, so the dog's owner id becomes the guest's character id and is saved with the world.

## Known limits

- Guests use the F6 menu, not the game's own dog prompts: on a guest's PC the dog is only a copy of the host's, without its pet data.
- Gear (collar, vest, saddlebag) and the saddlebag pouch aren't in the guest menu yet, since they'd mean moving items between the guest's inventory and the host's world.
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
