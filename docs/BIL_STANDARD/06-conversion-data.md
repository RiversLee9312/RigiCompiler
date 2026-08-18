# 转换、wrapper 与运行时类型指令 / 值、变量、字段与索引指令（§12–§13）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 12. 转换、wrapper 与运行时类型指令

### 12.1 强制转换

```bil
cast SOURCE RESULT type(TARGET_TYPE)
cast.indirect SOURCE RESULT TYPEID_VAR
```

`cast` 表示 Rigi 的强制转换语义，包括：

1. 源类型的 `castTo`；
2. 目标类型的 `castFrom`；
3. 内建引用视图转换和数值转换。

内建引用视图转换涵盖：

- 值类型到 `Object`/`Any` 分支的装箱视图（`RUNTIME.md` §4）；
- 派生类到基类/接口的视图改写（无数据移动）；
- `T` 到 `.nullable<T>` 的装箱视图，以及 `.nullable<T>` 到 `T` 的展开——后者在源为 `null` 时抛 `core.CastException`（Rigi 层 `nullableVar as T` 即此语义）。

优先级与失败行为必须与 `SYNTAX.md` 一致。失败抛出 `core.CastException`。

`cast.indirect` 的 `TYPEID_VAR` 必须为适合 RESULT 静态边界的 `.typeid<TBound>`。

### 12.2 安全转换

```bil
cast.safe SOURCE RESULT type(TARGET_TYPE)
cast.safe.indirect SOURCE RESULT TYPEID_VAR
```

结果类型必须为对应目标类型的 nullable 形式。转换失败时产生 `null`，不得抛出 `core.CastException`。

frontend 也可以使用 `type.is` + `if` + `cast` 生成等价形式。

### 12.3 类型检查

```bil
type.is VALUE type(TARGET_TYPE) RESULT_BOOL
type.is.indirect VALUE TYPEID_VAR RESULT_BOOL

type.supers VALUE type(TARGET_TYPE) RESULT_BOOL
type.supers.indirect VALUE TYPEID_VAR RESULT_BOOL

type.with VALUE type(WRAPPER_TYPE) RESULT_BOOL
type.with.indirect VALUE TYPEID_VAR RESULT_BOOL
```

结果必须为 `.bool`。语义分别对应 Rigi 的 `is`、`supers` 和 `with`，实际 TypeSheet 查询规则由 `RUNTIME.md` 定义。

enum case 判别检查（对应 Rigi 的 `value is .Case`，语义由 `RUNTIME.md` §16.3 定义）：

```bil
type.is.case VALUE case(CASE_SYMBOL) RESULT_BOOL
```

比较 VALUE 的隐藏判别字段与该 case 编译期判别常量的整数相等性——不是子类型检查，不访问 `TypeSheet.baseTypeId`，不比较任何用户字段，不改变 VALUE 的静态类型；`typeOf` 语义不变（始终返回该 enum struct 的类型）。

规则：

- CASE_SYMBOL 必须是 `ENUM_TYPE_SYMBOL.CaseName` 形态，且以 `ENUM_TYPE_SYMBOL` 为 owner（即 §8.5 的 case 声明，不能是裸 `.CaseName`）；
- VALUE 的严格静态类型必须等于 case 的 enum 类型；
- RESULT_BOOL 必须为 `.bool`；
- 判别字段宽度（u16/u32，`RUNTIME.md` §16.1）是布局内部细节：该指令不暴露判别字段符号，Middleware 按 case 的编译期判别常量与 enum 宽度执行整数比较。

### 12.4 取得 wrapper 值

```bil
get.wrapper VALUE type(WRAPPER_TYPE) RESULT
get.wrapper.indirect VALUE WRAPPER_TYPEID_VAR RESULT
get.wrapper.field OBJECT field(HOST_FIELD) type(WRAPPER_TYPE) RESULT
```

语义对应路径表达式 `value:WrapperType`：取得绑定到 VALUE 的指定 wrapper 实例。

规则：

- WRAPPER_TYPE 必须是 wrapper 类型；
- VALUE 的精确类型必须具有该 wrapper；
- RESULT 类型必须严格等于 WRAPPER_TYPE；
- 不存在对应 wrapper 时的行为必须与语言/运行时 wrapper 规则一致；
- 该指令不等价于普通字段读取。

`get.wrapper.field`（字段-Value 应用形态）：从属主对象 OBJECT 上挂在
HOST_FIELD 的 Value wrapper 应用取得 wrapper 值拷贝（随后可对 RESULT 发
普通 `invoke` / `get.array` / `get.field`）。

规则：

- HOST_FIELD 必须是实例字段，且带 `wrapped(WRAPPER_TYPE)` 应用标记（§8.3.1）；
- OBJECT 的严格静态类型必须可赋值到 HOST_FIELD 的 owner；
- WRAPPER_TYPE 必须是 wrapper 类型；RESULT 类型必须严格等于 WRAPPER_TYPE；
- 该指令只产值拷贝，不写入字段应用存储。

> **注记（读侧统一为值拷贝）**：
> `obj:Wrapper` 是只读 place（`SYNTAX.md` §14.5），不能整体取值；因此
> `get.wrapper` / `get.wrapper.field` 在源码可达路径上无直接对应物，保留为
> lowering/VM 内部能力——**全部成员读取**统一为值拷贝后普通指令：
> Entity 应用 = `get.wrapper` + 普通 `get.field`/`invoke`/`get.array`；
> 字段-Value 应用 = `get.wrapper.field` + 普通指令；嵌套 wrapper 链逐层
> 物化后继续普通 `get.field`。成员**写入**与 proxy 体内 `this` 的原地写
> 使用 §13.3 的 `set.wrapper.field`。字段-Value 应用写寻址编码为已有链
> 元素相邻对 `field(HOST_FIELD), wrapper(W)`（HOST_FIELD 带 `wrapped(W)`，
> §8.3.1；同 owner 多字段同 W 由此区分）；Entity 类型应用仍为单独
> `wrapper(W)`。深层写穿 `place.a.b... = rhs` 在 frontend 展开为多个
> 现有 get/set 系列（**不新增**专用深写 opcode；最外层必要写回复用
> `set.wrapper.field`，普通值类型中间层反向写回仍发 `set.field`，不把
> 整条深路径压进单条超长链）。`obj:W = ...` 整体赋值是源码层编译错误，
> BIL 不需要写入指令。存储由 Middleware 合成（命名约定 §5.3）；应用标记
> 见 §8.3.1。

### 12.5 取得宿主实例（proxy 模板）

```bil
get.self RESULT
```

仅 proxy 模板 fn（带 `wrapper-proxy`，§8.4）体内合法。语义对应源码层 `self`：取被修饰宿主实例。

规则：

- 必须出现在 `wrapper-proxy(specific|wildcard)` 标记的方法体内；出现在其他 fn 内非法；
- RESULT 类型必须严格等于该模板 fn 所属 wrapper 的 `TTarget` 泛型参数（Entity wrapper 恰一泛型参数时的代入结果）；
- 零泛型参数的 wrapper 模板内出现本指令即非法；
- 该指令不读取字段，不产生 wrapper 值拷贝。

### 12.6 取得 typeid

```bil
getid.type type(TYPE_SYMBOL) TARGET_TYPEID
getid.var VALUE TARGET_TYPEID
getid.field field(FIELD_SYMBOL) TARGET_FIELDID
```

规则：

- `getid.type` 返回指定类型的运行时 typeid；
- `getid.var` 返回值的实际运行时类型，Object 使用对象实际类型而非仅静态视图；
- `getid.field` 返回携带字段签名的 `.fieldid<...>`。

---

## 13. 值、变量、字段与索引指令

### 13.1 资源加载

```bil
load res(RESOURCE_ID) TARGET
```

资源的声明类型必须与 TARGET 类型严格相同。

### 13.2 局部变量读取与写入

```bil
get.var SOURCE_VAR TARGET_VAR
set.var SOURCE_VAR TARGET_VAR
```

规则：

- `get.var` 表示对逻辑局部变量/全局变量的 getter 读取；
- `set.var` 表示对逻辑可变变量的 setter 写入；
- 源值与目标逻辑变量类型必须严格相同；
- 对普通无 getter/setter 的临时变量，这些操作可以 lower 为普通复制；
- 对带 getter/setter 或 Value Wrapper 的变量，Middleware 按精确变量类型与符号元数据执行对应语义；
- 向 `const` 或不可写变量执行 `set.var` 非法。

普通 `$temp` 作为其他指令的输入表示读取一个已经物化的临时/参数值。frontend 对源码中带 getter 的变量读取应显式生成 `get.var`，不得通过裸 `$var` 绕过 getter。

### 13.3 实例字段

```bil
get.field OBJECT TARGET field(FIELD_SYMBOL)
set.field SOURCE OBJECT field(FIELD_SYMBOL)
```

验证规则：

- FIELD_SYMBOL 必须表示实例字段；
- OBJECT 的严格静态类型必须能访问该字段（含沿继承链访问基类声明的字段）；
- `get.field` 的 TARGET 类型必须严格等于字段声明类型；字段声明类型是宿主编泛型参数（或含宿主编泛型参数）时，按「OBJECT 严格静态类型到字段宿主的构造实参」替换后判定严格相等；
- `set.field` 的 SOURCE 类型必须严格等于字段声明类型（泛型情形同上前款）；
- `set.field` 要求字段可写；
- 访问权限必须合法。

`get.field` / `set.field` 不预先降为 `invoke`。Middleware / VM 按以下顺序执行字段读写语义：

- **写**（`set.field F`）：字段带 wrapper 时先走 wrapper set 链（outer→inner）；链末落点是调用 setter（无 setter 时才直接写存储）。setter 体内 `get.field` / `set.field` 引用 `..value` 时直接读写当前 setter 对应字段的 backing 存储——不查访问器、不绕 wrapper 链。
- **读**（`get.field F`）：先调 getter，getter 返回结果再在使用点过 wrapper get 链（inner→outer 逐环 proxy.get）写目标槽。getter 体内对自身字段的 `get.field`（当前 fn 是该字段的 getter）直读 backing，不绕 wrapper 链。
- **构造期豁免**：init 体内对带 wrapper 字段的写不绕 wrapper 链（wrapper 尚未安装），但带 setter 时仍调 setter（初始化器经 setter 应用，`SYNTAX.md` §9.4.1）；setter 体内 `..value` 直写 backing。
- **`..value` 合法性**：引用 `..value` 仅在 setter 上下文合法——当前 fn 带 `setter(F)` 且 owner/类型/static 匹配，或当前 fn 是 cell 隐藏子类的 `getValue` / `setValue`。之外出现是验证错误。
- 其余实现选择仍按精确 owner 类型、字段符号和访问类别：直接 load/store、interface 字段访问器、extension field、runtime dynamic fallback、GC/ARC 屏障与共享域操作。

局部变量统一 cell 存储同理：cell 的 setValue 体（含默认透传体）用 `..value` 直写；cell 的 getValue 体不变；wrapped cell 的使用点读写（invoke getValue/setValue）由 VM 识别并把 wrapper 链外置（写：链末调 setValue；读：getValue 返回后过 get 链）。cell 的 init 体仍引用真实 `value` 字段直写。

wrapper 隐藏存储写后门（对应 `obj:Wrapper.field` 写入、proxy 体内
`this.field` 原地写；**读取**一律走 §12.4 值拷贝 + 普通 `get.field`，
不经本指令）：

```bil
set.wrapper.field SOURCE OBJECT CHAIN_ELEM... field(INNER_FIELD)
```

- `set.wrapper.field`：wrapper 隐藏存储**写入后门**（**不是**普通 `set.field`）。CHAIN 必须表达 wrapper 存储寻址——至少含一个 `wrapper(W)`（Entity = `wrapper(W)`；字段-Value = 相邻 `field(HOST_FIELD)+wrapper(W)`）。普通值类型中间层的反向写回由 frontend lowering 生成普通 `set.field`，不走本指令。

链元素 `CHAIN_ELEM` 取两态之一（**不**新增第三态 opcode/操作数类）：

- `field(FIELD)`：沿普通实例字段下钻；或作为字段-Value 应用对的 `HOST_FIELD`（见下）；
- `wrapper(WRAPPER_TYPE_REF)`：沿宿主的 wrapper 应用下钻到该 wrapper 的隐藏存储（应用标记见 §8.3.1；存储由 Middleware 合成，命名约定 §5.3），再继续后续链元素或末段字段访问。

**字段-Value 应用**编码为相邻对 `field(HOST_FIELD), wrapper(W)`：HOST_FIELD
必须是实例字段且带 `wrapped(W)`（§8.3.1）；当前位置转为 W 的隐藏存储。
同 owner 上多个字段应用同一 W 时，靠不同的 `field(HOST_FIELD)` 区分。
**Entity 类型应用**仍为单独的 `wrapper(W)`（当前位置类型声明带 `wrapped(W)`）。

首段可以是 `field(...)` 或 `wrapper(WRAPPER_TYPE)`。例如：

```bil
set.wrapper.field $v $obj wrapper(core.logging::Logged) field(core.logging::Logged#level@.i32)
set.wrapper.field $v $hero field(com.example::Hero#mp@.i32) wrapper(core.clamp::Clamped) field(core.clamp::Clamped#min@.i32)
```

读侧对应形态（值拷贝，§12.4）：

```bil
get.wrapper $obj type(core.logging::Logged) $w
get.field $w $x field(core.logging::Logged#level@.i32)
get.wrapper.field $hero field(com.example::Hero#hp@.i32) type(core.clamp::Clamped) $w
get.field $w $m field(core.clamp::Clamped#min@.i32)
```

规则：

- 链至少含一个链元素，末段必须是 `field(INNER_FIELD)`（被写的目标字段）；
- 相邻 `field(F), wrapper(W)` 且 F 声明带 `wrapped(W)` 时视为**字段应用对**：OBJECT/当前位置必须可赋值到 F 的 owner，F 必须是实例字段，位置转为 W；F 带其它 `wrapped(*)` 但不匹配 W 则非法；
- 单独的 `wrapper(WRAPPER_TYPE)` 为**类型应用**：要求当前位置静态类型的类型声明具有该 wrapper 应用标记（§8.3.1；外部/无法解析的类型引用可降级）；
- 普通 `field(FIELD)`（非字段应用对之首）要求 FIELD 是当前位置静态类型可访问的实例字段，位置转为该字段类型；
- `set.wrapper.field` 的 SOURCE 类型必须严格等于末段 INNER_FIELD 的字段类型，且 INNER_FIELD 可写；链必须含至少一个 `wrapper(...)`（wrapper 后门形态，禁止用纯 field 链冒充普通字段写入）；
- 语义为对嵌套位置做**原地写入**（经 wrapper 元素时不产生 wrapper 值拷贝）；
  只读 place 整体不可赋值，故不存在「对整个 place 写回」的指令形态；
- proxy 体内 `this` 的成员写与 `obj:Wrapper` 同构：OBJECT 取宿主（`.this` 或具体对象），链以 `wrapper(该模板所属 wrapper 类型)` 起。

### 13.4 静态字段

```bil
get.field.static TARGET type(OWNER_TYPE) field(FIELD_SYMBOL)
set.field.static SOURCE type(OWNER_TYPE) field(FIELD_SYMBOL)
```

FIELD_SYMBOL 必须表示 OWNER_TYPE 的 static 字段，值类型必须严格匹配。

§13.3 的 `..value` 约定与读写链顺序同样适用于本指令：setter 体内 `get.field.static` / `set.field.static` 引用 `Owner#.static...value@.T`（或全局 `ns::#..value@.T`）直达 backing。

Singleton 的实例字段不得因为 owner 为 singleton 而使用 static 指令；是否 static 由字段声明决定。

### 13.5 间接字段

```bil
get.field.indirect OBJECT TARGET FIELDID_VAR
set.field.indirect SOURCE OBJECT FIELDID_VAR

get.field.static.indirect TARGET TYPEID_VAR FIELDID_VAR
set.field.static.indirect SOURCE TYPEID_VAR FIELDID_VAR
```

`FIELDID_VAR` 必须携带完整 owner、值类型和 instance/static 类别。验证器必须据此检查 TARGET/SOURCE 类型。

运行时字段 ID 不得是无签名的裸整数。

### 13.6 索引读取与写入

```bil
get.array COLLECTION INDEX RESULT
set.array COLLECTION INDEX ELEMENT
```

这些指令表示 Rigi 的 `getAtIndex` / `setAtIndex` 语义，不预先降为方法调用。

验证器使用严格三元组查询：

```text
get.array: collection type + index type + result type
set.array: collection type + index type + element type
```

必须存在唯一精确实现，且不得插入隐式转换。

Middleware 可将其 lower 为：

- `Array\<T>` 统一胖值槽访问；
- `Span\<T>` 原生 stride 地址计算；
- 用户索引运算符；
- wrapper 运算符代理；
- bounds check 与 runtime helper。

---
