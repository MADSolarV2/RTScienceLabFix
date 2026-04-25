# RTScienceLabFix

Fixes a bug in RemoteTech where science transmitted from the Mobile 
Processing Lab does not work. You get the transmission text, and the complete message, but no actual science is transfrered or deducted from the lab.

I believe this is a bug that has existed for a while with remotetech, at least as far back as I can remember.

## Dependencies
- KSP 1.12.x
- RemoteTech v1.9.12
- Harmony 2 (included with many mods; install separately if needed)

## Known Issues
- A harmless NullReferenceException from stock ModuleScienceLab.OnTransmissionComplete 
  still appears in KSP.log after transmission. This has no gameplay effect — 
  science is correctly awarded and the lab buffer correctly deducts before it fires.
  -Not tested with transmitting from background vessels. There's a chance if you're not directly controlling the vessel with the MPL at the time of transmission, science could be lost or duped or nothing happens at all.

## Installation
Drop the RTScienceLabFix folder into GameData.

## License
MIT
