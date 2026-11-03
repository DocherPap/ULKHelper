# cactbot data attribution

`Fates/FateSeeds.cs` adapts point coordinates, Chinese short labels, weather/night conditions, and cooldown estimates from the cactbot contributors' `ui/eureka/zone_anemos.ts`, `zone_pagos.ts`, `zone_pyros.ts`, and `zone_hydatos.ts`.

Source: https://github.com/quisquous/cactbot/tree/8d12b650896aed42e56bcfe07c8580426ab2b9f8/ui/eureka

Licensed under Apache License 2.0; see `cactbot-LICENSE.txt` in this directory. The data was converted to C# records; full FATE names, levels and map textures are resolved from the user's local game files. No original cactbot map images or runtime code are shipped.

# CriticalEncounter.CrowdSource data attribution

`Fates/FateTriggers.cs` adapts Chinese trigger descriptions from `web/public/assets/dynamic-event-catalog.json`, version `c2bf132ca39c1467c60bb87b97c3281160ae3c80` of https://github.com/Dalamud-DailyRoutines/CriticalEncounter.CrowdSource . Copyright (c) 2026 AtmoOmen, MIT License; see `CriticalEncounter.CrowdSource-LICENSE.txt`. Markdown emphasis was removed and the five Happy Bunny entries were given explicit special-trigger explanations.
