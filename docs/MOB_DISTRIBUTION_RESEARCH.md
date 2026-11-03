# 优雷卡小怪分布资料调研

查询日期：2026-11-03。用户仅授权查找信息，本次未实现功能、导入可执行数据或改变自动触发行为。

## 主要发现

[FFXIV Interactive Eureka Maps](https://www.ffxiv-interactive-eureka-maps.com/map/anemos) 及作者仓库 [Kassanu/eureka-interactive-maps](https://github.com/Kassanu/eureka-interactive-maps) 提供四岛结构化JSON。位置和属性在 `data/zones/<岛>/zone.json`，名称与游戏ID在相邻 `entities.json`。本次读取master，schemaVersion为10。

| 岛 | 敌人名称实体数 | 怪物地图标注条目 | 有spawnsFate的条目 | 对应不同NM |
| --- | ---: | ---: | ---: | ---: |
| [常风](https://github.com/Kassanu/eureka-interactive-maps/blob/master/data/zones/anemos/zone.json) | 93 | 133 | 20 | 20 |
| [恒冰](https://github.com/Kassanu/eureka-interactive-maps/blob/master/data/zones/pagos/zone.json) | 71 | 91 | 17 | 17 |
| [涌火](https://github.com/Kassanu/eureka-interactive-maps/blob/master/data/zones/pyros/zone.json) | 77 | 111 | 19 | 17 |
| [丰水](https://github.com/Kassanu/eureka-interactive-maps/blob/master/data/zones/hydatos/zone.json) | 53 | 80 | 12 | 11 |
| 合计 | 294 | 415 | 68 | 65 |

以上是JSON记录数，不是服务器怪物数量或完整出生点统计。按spawnsFate引用对应到entities.fates.gameId后，65个不同FATE ID与本项目FateTriggers中含击杀描述的65条普通NM规则集合完全一致，无缺失/额外ID。这证明关联覆盖，不证明每个位置/等级/条件均正确。UFO及兵武塔支援等特殊触发不属于这65条普通击杀规则。

## 可取得的字段及坐标含义

- zone.json中enemies.items含entity、positions、level、element、aggro、family、spawnsFate，部分另有adaptation/mutation条件。
- entities.json提供敌人/头目/FATE实体UUID、gameId、英日德法名称。没有简体中文；敌人的gameId所对应本地表和国服名称应在正式采用前核对，不能仅凭字段名猜测ID类型。
- [坐标定义](https://github.com/Kassanu/eureka-interactive-maps/blob/master/model/primitives.ts) 明确为游戏地图格坐标，通常约1–42；x/y不是世界X/Y，也没有高度。本次四岛每条怪物记录都是一个positions点，结构允许多个点，不提供怪群边界/完整巡逻路线/可达路径。
- [数据贡献说明](https://github.com/Kassanu/eureka-interactive-maps/blob/master/CONTRIBUTING.md) 表明位置可由网页编辑器人工调整和提交，属于社区维护资料，需要进岛核验。

资料样例（社区地图标注，非实测世界坐标）：

| 岛 | 怪物 | 等级 | 地图坐标 |
| --- | --- | ---: | --- |
| 常风 | Flowering Sabotender / 仙人花 | 6 | 14.4, 20.4 |
| 常风 | Sea Bishop / 海祭司 | 7 | 28.1, 27.6 |
| 恒冰 | Yukinko / 雪童子 | 25 | 21.1, 28.5 |
| 恒冰 | Ameretat | 40 | 23.5, 13.7 |
| 涌火 | Pyros Bhoot / 涌火浮灵 | 40 | 26.9, 25.9 |
| 丰水 | Crystal Claw | 65 | 31.0, 20.1 |

## 质量边界与后续使用判断

1. 同名不同等级要分开。涌火Pyros Bhoot同时有38级(12.7,28.7)与40级(26.9,25.9)关联同一FATE；当前项目已核实的触发描述要求40级，不能直接把所有关联条目当作有效触发怪。
2. 同一触发对象可能有多个标注。涌火Val Bloodglider为55级，标了(34.1,7.7)与(38.4,15.9)；丰水Laboratory Lion的两个点仅差0.1地图单位，应进一步判断是否冗余。
3. 小怪点不等于NM点。例如仙人花标注(14.4,20.4)，对应NM标注(14.1,21.8)。导航不能拿NM中心替代实际刷怪地点。
4. 数据没有精确世界高度、可行走路径、每只怪的当前存活/刷新状态，也不保证完整覆盖所有同种怪。可作为候选刷怪地点资料；真正接近后仍需已加载对象的真实名称ID/等级/位置、天气夜间条件和网格验证。这只是调研结论，未接入移动或攻击。
5. 仓库声明[GPL-3.0](https://github.com/Kassanu/eureka-interactive-maps/blob/master/LICENSE)，没有在所查文件中发现独立的数据宽松授权；若后续要直接分发其数据，需先核对适用授权，不默认按MIT处理。

## 其他来源

- [Eureka Tracker](https://ffxiv-eureka.com/local-tracker) 可交叉核对NM与触发怪关系，作者[更新日志](https://ffxiv-eureka.com/changelog)说明可打开大地图；不把NM位置自动视为小怪分布点。
- [XIVAPI Mappy Data文档](https://cafemaker.wakingsands.com/docs/Mappy-Data) 描述社区采集的地图/内存坐标数据及JSON下载，可作候选交叉来源；本次Map Data下载接口返回530，未验证其四岛覆盖，不依赖它作主数据。
- 旧[禁地优雷卡综合管理平台](https://e.ffxivsc.cn/) 首页明确公告2022年关闭，不作为新数据服务来源。

## 本次读取文件的Git blob SHA

记录blob SHA供以后对照，不代表已确认游戏版本适配：

| 岛 | zone.json | entities.json |
| --- | --- | --- |
| anemos | 2f8afd41f7ac955eac6237213d7b27e014a8c13c | 3c0f71edaf823de6eaa0e1aaeb87491b1aee65f8 |
| pagos | ddc91df0d4af28390b26d9d234059544a4c51fd3 | 034db3aaaaaac797d244c4ff05b2bc2f4b87e2b9 |
| pyros | 4b09554692bb8df3a01aedb1fe0547d1836b6f8b | bdb6ceb841a99bcccd9f83fd28ee64203ec70c93 |
| hydatos | 7d089099f0c5a9244c9137d89d6217de876a35ad | 71dbe49e93b170dbc04d84867140d04dad130fe7 |
