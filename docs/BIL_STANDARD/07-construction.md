# 构造指令（§14）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 14. 构造指令

### 14.1 静态普通构造

```bil
new type(TYPE_SYMBOL) RESULT [ARG_0, ARG_1, ...]
```

规则：

- TYPE_SYMBOL 必须是可普通构造的具体 canonical 类型符号；
- 参数已经完成默认值填充与具名参数重排；
- 泛型构造所需的 `.generic.<Name>` hidden arguments 已按第 7 节放入参数列表；
- 参数类型必须与一个 init 的规范 BIL 签名严格匹配；
- 验证器必须确认该精确签名唯一；
- RESULT 类型必须严格等于构造结果类型；
- abstract 类型和 enum struct 不得使用该指令。

Middleware 根据目标类型和精确参数类型选择 init；不执行 source-level overload ranking。

### 14.2 动态普通构造

```bil
new.indirect TYPEID_VAR RESULT [ARG_0, ARG_1, ...]
```

语义对应 `new typeValue(...)` 和泛型 `T()`。运行时按实际 typeid 的 init 表解析严格匹配的构造入口。

目标为 abstract 类型、enum struct 或无匹配 init 时抛出 `core.NoSuchMethodException`。

编译期分工（SYNTAX §3.7）：frontend 对**零参 `T()`** 先按「约束最大基类」静态判定——界没有可访问零参 init 直接编译错误，不会落到本指令；内建标量界（整数/浮点/bool/char/String）由 VM 特判产零值（不查 init 表）；带实参形态与动态 `new typeValue(...)` 保持运行期解析，上面的 `NoSuchMethodException` 是它们的运行期兜底。

### 14.3 enum case 构造

```bil
new.case type(ENUM_TYPE) case(ENUM_TYPE.CaseName) RESULT [ARG_0, ARG_1, ...]
```

规则：

- ENUM_TYPE 必须是 enum struct；
- case 符号必须以 ENUM_TYPE 为 owner；
- 实参为 case 入口的**组合实参**（RUNTIME §16）：case 模板的固定实参与调用点洞实参按 init 参数序组成，参数类型必须严格匹配 case 绑定 init 的签名；模板无固定实参时组合实参即洞实参（§8.5 洞签名序）；
- RESULT 必须严格为 ENUM_TYPE；
- 该指令是 enum 值的唯一标准构造形式。

enum struct **没有零值**。enum 类型的存储位置（`.vars` 条目、字段、数组
元素）在被读取前必须已经由 `new.case` / `new.wrapped.case` 构造写入：

- frontend 经 DA 与 init 编织保证该不变量；
- 验证器拒绝「声明了 enum struct 字段、但宿主类型存在未在全路径写入该
  字段的 init」的模块（§21.8）；局部变量读前未写由既有 DA 规则拒绝；
- VM / 运行时读到未构造的 enum 槽一律按宿主错误处理，不存在「默认
  case」兜底。

推论：`.array<enum struct>` 的批量零初始化与本条冲突——
`arrayOf\<T>(size)`（`RUNTIME.md` §26）在 T 为 enum struct 时由 frontend
在泛型实例化点拒绝（编译期诊断）；`arrayOfElements\<T>(elements...)`
不受限（元素逐项显式给出）。

### 14.4 `new.wrapped` 家族（有参 `..init.wrapper` 的实体构造）

当目标类型声明了**带参数**的 `..init.wrapper`（§9.7）时，创建该类型实例**必须**使用本家族指令；无参 `..init.wrapper` 或未声明 `..init.wrapper` 的类型**禁止**使用本家族，仍用普通 `new` / `new.case`（§14.1 / §14.3）。

#### 14.4.1 普通构造

```bil
new.wrapped type(TYPE_SYMBOL) RESULT [WRAPPER_ARG_0, ...] [INIT_ARG_0, ...]
```

**形态设计理由**：与 `new type(TYPE) RESULT [ARGS]` 同构，仅在 RESULT 后增加**第二个**实参列表。两个相邻 `[...]` 列表在词法上无二义（无需查表即可分段）；第一表 = `..init.wrapper` 参数（声明序），第二表 = 命中的 `init` 重载实参（已完成默认值填充与具名重排，同 §14.1）。平铺单表会在未知 `..init.wrapper` 元数时无法分段，故不采用。

规则：

- TYPE_SYMBOL / RESULT / abstract / enum-struct 限制同 §14.1（enum-struct 不得用本指令，改用 §14.4.2）；
- TYPE 必须恰好声明一个 `..init.wrapper`，且其规范参数列表**非空**；
- `WRAPPER_ARG_*` 个数与类型必须与该 `..init.wrapper` 签名**严格匹配**（复用 §3.3 调用映射后的规范序；hidden generic 规则同普通 invoke）；
- `INIT_ARG_*` 必须与 TYPE 上唯一匹配的 `init` 规范签名严格匹配（同 §14.1）；
- 与普通 `new` **互斥**：有参 `..init.wrapper` 的类型上出现 `new` → 非法；无参或未声明 `..init.wrapper` 的类型上出现 `new.wrapped` → 非法。

语义：运行时先分配实例，以 `WRAPPER_ARG_*` 调用 `..init.wrapper`（实体 init 之前，§9.7），再以 `INIT_ARG_*` 调用命中的 `init`。

#### 14.4.2 enum case 构造

```bil
new.wrapped.case type(ENUM_TYPE) case(ENUM_TYPE.CaseName) RESULT
    [WRAPPER_ARG_0, ...] [CASE_ARG_0, ...]
```

规则：在 §14.3 之上叠加与 §14.4.1 相同的 wrapper 前缀实参与互斥规则（`new.case` ↔ `new.wrapped.case`）。

### 14.5 wrapper 初始化指令（仅 `..init.wrapper` 体内）

以下三条指令**只能**出现在方法简单名为 `..init.wrapper` 的 fn 体内（§9.7）；其它 fn 中出现一律非法。语义：按 ARGS 调用 wrapper 类型的 init 重载，将结果写入 Middleware 合成的隐藏存储（命名约定 §5.3）；**无结果变量**（不产生可流动的 wrapper 值）。

```bil
new.wrapper.field field(FIELD_SYMBOL) type(WRAPPER_TYPE) [ARG_0, ...]
new.wrapper.method fn(METHOD_SYMBOL) type(WRAPPER_TYPE) [ARG_0, ...]
new.wrapper.entity type(WRAPPER_TYPE) [ARG_0, ...]
```

操作数序（对齐 `get.wrapper` / `field(...)` / `fn(...)` / `type(...)` 既有拼写）：

| 指令 | 操作数 |
|------|--------|
| `new.wrapper.field` | `field(FIELD)` → `type(WRAPPER_TYPE)` → `[ARGS]` |
| `new.wrapper.method` | `fn(METHOD)` → `type(WRAPPER_TYPE)` → `[ARGS]` |
| `new.wrapper.entity` | `type(WRAPPER_TYPE)` → `[ARGS]` |

规则：

- `WRAPPER_TYPE` 必须是 wrapper 类型引用；
- `ARGS` 必须与 `WRAPPER_TYPE` 上唯一匹配的 `init` 规范签名严格匹配（§3.3 / §14.1 同一套严格匹配；含默认值填充后的规范序）；
- `new.wrapper.field`：`FIELD` 必须是当前 `..init.wrapper` 宿主类型（或沿继承可见）的实例字段，且该字段声明带 `wrapped(WRAPPER_TYPE)`（§8.3.1）；
- `new.wrapper.method`：`METHOD` 必须是当前宿主上的方法符号（实例或静态——静态方法的 Method wrapper 通常改挂 companion，见 §8.7；本指令用于仍挂在宿主上的方法应用）；
- `new.wrapper.entity`：当前宿主类型声明必须带 `wrapped(WRAPPER_TYPE)`；
- 同一 `..init.wrapper` 体内，对同一目标（同一 FIELD / 同一 METHOD / entity×同一 W）的重复初始化非法（验证器可检静态重复）；
- 指令不读/写普通局部结果；ARGS 中的变量按 §21.4 DA 规则须已赋值。

---
