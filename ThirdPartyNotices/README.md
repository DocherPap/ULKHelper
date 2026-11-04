# cactbot data attribution

`Fates/FateSeeds.cs` adapts point coordinates, Chinese short labels, weather/night conditions, and cooldown estimates from the cactbot contributors' `ui/eureka/zone_anemos.ts`, `zone_pagos.ts`, `zone_pyros.ts`, and `zone_hydatos.ts`.

Source: https://github.com/quisquous/cactbot/tree/8d12b650896aed42e56bcfe07c8580426ab2b9f8/ui/eureka

Licensed under Apache License 2.0; see `cactbot-LICENSE.txt` in this directory. The data was converted to C# records; full FATE names, levels and map textures are resolved from the user's local game files. No original cactbot map images or runtime code are shipped.

# CriticalEncounter.CrowdSource data attribution

`Fates/FateTriggers.cs` adapts Chinese trigger descriptions from `web/public/assets/dynamic-event-catalog.json`, version `c2bf132ca39c1467c60bb87b97c3281160ae3c80` of https://github.com/Dalamud-DailyRoutines/CriticalEncounter.CrowdSource . Copyright (c) 2026 AtmoOmen, MIT License; see `CriticalEncounter.CrowdSource-LICENSE.txt`. Markdown emphasis was removed and the five Happy Bunny entries were given explicit special-trigger explanations.

# Island crystal location reference

The fourteen factual X/Z crystal positions and PlaceName row IDs in `Automation/IslandCrystalRoutes.cs` were cross-referenced against Lifestream's `CustomAethernet.cs` at commit `b36320170faafc8315f7585edb45cfbcd34e7046`: https://github.com/NightmareXIV/Lifestream/blob/b36320170faafc8315f7585edb45cfbcd34e7046/Lifestream/Systems/Custom/CustomAethernet.cs . Chinese names are resolved from the user's game data. No Lifestream implementation or synthetic IDs are included; route selection and game interaction are independently implemented.

# Logos reference data

`Logos/catalog.json` is an unmodified copy of the factual recipe/appraisal catalog from https://github.com/Pop115/XivEureka/blob/main/XivEureka/Data/logos.json (retrieved 2026-11-03). Source author Pop115; distributed under AGPL-3.0, retained in `XivEureka-data-LICENSE.txt`. Original source identifies recipes as captured from https://ffxiv-eureka.com/logograms on 2026-10-11 and appraisal pools cross-checked with Item data on 2026-10-17. No XivEureka runtime implementation is copied. Chinese names are read from the user's local game files. Preserve this catalog, source attribution and license when distributing this data; the source catalog remains available at the linked URL and in this repository.
