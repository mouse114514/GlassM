# GlassM

Casualties Unknown 流式生成性能模组。

核心思路:加载画面内一次性预填全图地形**数据**(纯数组,开销极低),之后以玩家为中心按需构建 tilemap 渲染——取代原版一次性构建全部 256 个区块的 tilemap/碰撞体/刚体。

## 核心功能

- **流式渲染**:地形数据开局即完整,区块 tilemap 按需生成/卸载,玩家(联机时含所有玩家)周围动态更新
- **物理区动态开关**:远离玩家的区块关闭 collider/刚体模拟,刚体负担从 256 块大幅下降
- **矿石走原版管线**:反射调用原版 `GenerateOres`,铜/铁/钛分布与原版一致,且兼容改矿类 mod 的钩子
- **出生点精确**:出生空腔同步渲染,`PlaceBody` 碰撞扫描正常落位(深度 0~15m)
- **无限岩保护**:流式生成的结构不会破坏地图边界的无限岩(id 14)
- **实体对齐原版**:沙藤/钟乳石/炮塔等生成参数与坐标逐项对照原版反编译源码
- **PerfDiag 诊断**:每 5 秒输出各系统耗时、FPS、场景负载
- **联机兼容**(KrokoshaCasualtiesMP):以所有玩家为中心生成,客户端玩家不会因主机卸载区块而掉落

## 兼容性(v0.1.1)

| Mod | 状态 |
|---|---|
| Custom Structures | ✅ 结构直接写入预填数据,不被覆盖 |
| TitaniumRestored | ✅ 挂在 GenerateOres 后,数据已填满 |
| CUCoreLib(自定义矿物/结构/tile) | ✅ |
| TrapLib / AdditionalTraps | ✅ |
| NoOilMod | ✅ |
| QoL Unknown 实体覆盖 | ❌ 暂不兼容(实体仍走 GlassM 流式管线) |
| CUCoreLib 建筑(PlaceCrystals) | ❌ 暂不兼容 |

## 安装

1. 杀游戏进程,`GlassM.dll` → `BepInEx\plugins\`
2. 启动游戏,日志出现 `GlassM loading` 即生效

## 构建

```
dotnet build -c Release --nologo -v q
```

产物 `bin\Release\GlassM.dll`。

## 协议

MIT License,见 [LICENSE](LICENSE)。
