# Rigi 标准库 MVP 设计

本文确定标准库 MVP 的命名空间、职责边界、公共契约和施工依赖，作为后续实现的设计入口。
它不是已实现 API 清单或进度报告。

---

本文件是**目录索引**。正文已按主题拆分到 `STDLIB/` 子目录，
章节号（§）与原文件完全一致、永不重排——代码与文档中的 `§N.M` 引用经本索引定位分文件。

| § | 章节 | 文件 |
|---|---|---|
| §1–§2 | 文档定位 / 已确定的 MVP 范围（目标、命名空间、不做的内容） | [STDLIB/01-overview-scope.md](STDLIB/01-overview-scope.md) |
| §3 | 跨模块原则 | [STDLIB/02-cross-module-principles.md](STDLIB/02-cross-module-principles.md) |
| §4–§4.2 | 各 NS 的最小能力（引言）/ `core` / `core.collections` | [STDLIB/03-core-collections.md](STDLIB/03-core-collections.md) |
| §4.3 | `core.text` | [STDLIB/04-text.md](STDLIB/04-text.md) |
| §4.4 | `core.io` | [STDLIB/05-io.md](STDLIB/05-io.md) |
| §4.5 | `core.fs` | [STDLIB/06-fs.md](STDLIB/06-fs.md) |
| §4.6 | `core.serialization` | [STDLIB/07-serialization.md](STDLIB/07-serialization.md) |
| §4.7 | `core.serialization.json` | [STDLIB/08-json.md](STDLIB/08-json.md) |
| §4.8 + §4.12 | `core.coroutine` 与 `core.messaging` / `core.native` | [STDLIB/09-coroutine-messaging-native.md](STDLIB/09-coroutine-messaging-native.md) |
| §4.9 | `core.time` | [STDLIB/10-time.md](STDLIB/10-time.md) |
| §4.10 + §4.11 | `core.system`（后续规划）/ `core.math` | [STDLIB/11-system-math.md](STDLIB/11-system-math.md) |
| §5 | 依赖与代码组织 | [STDLIB/12-dependencies-organization.md](STDLIB/12-dependencies-organization.md) |
| §6 | 建议施工顺序 | [STDLIB/13-build-order.md](STDLIB/13-build-order.md) |
| §7–§8 | 验证要求 / MVP 应用验收 | [STDLIB/14-verification-acceptance.md](STDLIB/14-verification-acceptance.md) |
| §9–§10 | 兼容性与维护 / 施工前契约清单（D1–D8） | [STDLIB/15-maintenance-contracts.md](STDLIB/15-maintenance-contracts.md) |

## 维护约定

- 新增标准库设计内容进对应主题文件，沿用既有 `## N.` / `### N.M` 编号；新增整章时在末尾续号并回本索引登记。
- 不记录里程碑/进度信息（历史见 `legacy/`）；只描述标准库的当前设计与契约。
