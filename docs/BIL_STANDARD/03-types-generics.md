# 类型系统 / 泛型与可变参数规范签名（§6–§7）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 6. 类型系统

### 6.1 类型引用

每个参数、局部变量、字段、方法返回值和资源都必须具有一个 BIL 类型引用。

类型引用可以是：

- 固定内建类型；
- 用户 canonical 类型符号；
- 闭合泛型类型；
- 由运行时 typeid 指定的泛型类型引用；
- 编译器专用的 capability/handle 类型。

### 6.2 固定内建类型

标准内建类型包括：

```text
.void
.i8 .i16 .i32 .i64
.u8 .u16 .u32 .u64
.f32 .f64
.bool
.char
.string
.any
.object
.valuetype
.handle
.breakid
```

其中：

- `.void` 只能用作无结果方法的返回类型，不得声明普通变量；
- `.breakid` 是结构化控制 capability，不是普通整数和值类型；
- `.any`、`.object`、`.valuetype` 是 Rigi 根类型的标准 BIL 别名；
- `.handle` 是 `Handle\<T>` / `MutableHandle\<T>` 的固定能力投影，绝无 `.handle<T>` 形态。模块必须提供唯一、无泛型、无成员的 `class shared unsafe compiler-generated` 声明。源码的逻辑 T 由私有泛型 helper 的方法 typeid 保留，不进入 Handle 布局。禁止普通 `new`、动态构造、继承与伪造字段。
- `.string` 是**非 rich 值类型**（`SYNTAX.md` §3.1.2），赋值兼容与复制按值类型规则处理，不属于 `.object` 分支。它的物理表示是运行时特权裸缓冲区；BIL 与 BIL VM 一律按值语义（深拷贝）理解 `.string`，不得假设任何共享缓冲区、驻留或 copy-on-write 优化的存在——与「BIL 不得假设特定 GC 模型」同理。
- `.char` 是**非 rich 标量值类型**，32 位承载一个 Unicode 标量：U+0000–U+10FFFF，排除 U+D800–U+DFFF 代理区（`STDLIB/04-text.md` §4.3.1）。`.char` 资源与整数→`.char` 转换均须满足该值域，越界/代理区是运行期 cast 失败（`core::CastException`），不截断或回绕；String 的物理表示是 UTF-8 字节序列，与 `.char` 的标量值经标准编解码换算。

### 6.3 标准类型构造

标准类型构造包括：

```text
.array<T>
.map<TKey, TValue>
.pair<TFirst, TSecond>
.nullable<T>
.cell<T>
.readonly_cell<T>
.typeid<TBound>
.fieldid<TOwner, TValue, instance|static>
.generic<TYPEID_PLACE>
```

说明：

- `.array<T>` 对应源码层的 `Array\<T>`；
- `.map<K,V>` 对应标准运行时 Map；
- `.pair<A,B>` 对应标准 Pair；
- `.nullable<T>` 对应 `Nullable\<T>`；
- `.cell<T>` 对应标准库 `core::Cell\<T>`（抽象基类，抽象 `getValue`/`setValue`）的特权拼写，与 `.array<T>`、`.nullable<T>` 同类；
- `.readonly_cell<T>` 对应标准库 `core::ReadonlyCell\<T>`（抽象基类，仅抽象 `getValue`）的特权拼写；
- `.cell` / `.readonly_cell` 的 `T` 递归按类型构造规则解析；用途为闭包捕获与 wrapper 值统一 cell 存储（`SYNTAX.md` §5.2 / §14.3）；**基类抽象化后 BIL 中不再被直接 `new`**——实际 cell 对象恒为 `..cell..HASH` 隐藏子类实例（`.type` 声明 `extends .cell<T>` / `.readonly_cell<T>`）；读写经普通 `invoke` 虚派发 `getValue`/`setValue`，无专用指令；
- 特权拼写的定位 = Middleware 激进优化识别点（消除 cell 间接/直读槽位等）；验证规则不变——即使不做特判、按普通类烘焙也可正确工作；
- 共享安全性为 passthrough：`.cell<T>` / `.readonly_cell<T>` 的共享安全性等同于 `T`（与 Box 同例，需特殊判定，不按普通 class 闭包表）；
- `.typeid<TBound>` 是 BIL 中具类型边界的运行时类型句柄，对应源码 `Type\<TBound>` 的语义；
- 未写边界的 `.typeid` 等价于 `.typeid<.any>`；
- `.fieldid` 必须携带足以验证间接访问的签名；
- `.generic<...>` 表示“由指定 typeid 位置描述的实际类型”。

示例：

```bil
.args {
    .generic.TElement = .typeid<.any>,
    value = .generic<$.generic.TElement>
}
```

### 6.4 类型严格相等

以下情况不构成 BIL 类型相等：

- 派生类与基类；
- 实现类与接口；
- `T` 与 `T?`；
- `i32` 与 `i64`；
- `Array\<Dog>` 与 `Array\<Animal>`；
- `Box\<T>` 语义投影与普通 Object 类型；
- 具有不同 typeid 来源的两个 `.generic<...>`，除非验证器可以证明其 typeid 恒等。

需要改变视图或类型时必须使用 `cast` 或其他明确指令。

### 6.5 赋值兼容

`set.var`、`set.field` 和普通方法参数传递默认要求源类型与目标类型严格相同。

source-level 子类型赋值必须由 frontend 生成显式 `cast`，即使该 cast 在 Middleware 中最终只改写视图 typeid 或被优化消除。

### 6.6 特权类型

`Place<T>` 保留普通 local class 表示，保留构造参数必须为 `(target: Any, kind: i32)` 或 `(storage: Any, value: Any, kind: i32)`。kind 是常量 0（Object）、1（Cell）、2（ReadonlyCell）；验证器追溯单写 cast 临时的原类型，Object 分支须可证明是引用对象，值分支须沿继承链命中对应 Cell 根，拒绝临时 ValueType 装箱。动态泛型分支由实际 value 判对象/值，仍持有原 storage。

Handle 的 `load/asMutable/store` 调用落为 `core::$handle_load<T>`、`core::$handle_asMutable<T>`、`core::$handle_store<T>` 普通私有 unsafe 函数。创建、取目标、取种类、可写位与类型分类的 `handle_*` native 入口使用固定保留签名，只有这些 helper 与 `Place.expose` 可调用；隐藏 target release 不是 BIL/native 方法。VM 与 Middleware 必须拒绝动态构造 Place/Handle，不能用 typeid 绕过保留构造入口。

`Box\<T>`、`Span\<T>` 等编译器/运行时特权类型在 BIL 中是正常语义类型，但 BIL 不复制其 Native 物理布局。

BIL VM 应使用抽象值语义实现这些类型；Middleware 依照 `RUNTIME.md` 选择胖值、裸 buffer 或其他物理表示。

---

## 7. 泛型与可变参数的规范签名

BIL 函数签名必须按照 `SYNTAX.md` 和 `RUNTIME.md` 的具化规则生成隐藏参数。

隐藏参数名称以 `.` 开头，普通源码参数不得使用这些名称。

### 7.1 隐藏参数命名与类型

| 源声明形态 | BIL 隐藏参数名 | BIL 参数类型 |
|---|---|---|
| 固定泛型参数 `T` | `.generic.T` | `.typeid` |
| 位置可变泛型参数 `TArgs...` | `.generic.TArgs` | `.array<.typeid>` |
| 具名可变泛型参数 `named TArgs...` | `.generic.TArgs` | `.map<.string, .typeid>` |
| 位置值可变参数 `args...` | `.vargs.args` | `.array<.any>` |
| 具名值可变参数 `named args...` | `.kwargs.args` | `.array<.pair<.string, .any>>` |

具名泛型类型图与具名值参数包承担不同职责：

- `.generic.TArgs` 保存名称到实际 typeid 的映射；
- `.kwargs.args` 保存名称到统一 `Any` 值槽的映射/有序 pair 序列。

对于 `named TValues...` 与 `named values...` 成对出现的声明，两者都必须出现在规范签名中。

### 7.2 参数顺序

BIL 的规范参数顺序为：

1. `.this`，若存在 receiver；
2. 固定泛型隐藏参数，按声明顺序；
3. 泛型可变参数包，若存在；
4. 普通固定值参数，按声明顺序；
5. 位置值可变参数包，若存在；
6. 具名值可变参数包，若存在。

该顺序是 BIL 文本和 BIL VM 的**语义调用顺序**，不是 Native ABI。

### 7.3 receiver

实例方法具有：

```bil
.this = TYPE
```

扩展函数/扩展字段也使用 `.this` 表示被扩展值的语义 receiver。Middleware 可以将其 lower 为普通第一个参数或其他实现形式。

### 7.4 具名普通参数

普通具名调用在 frontend 中完成名称解析、默认参数填充和重排。进入 BIL 后，固定参数按被调用签名顺序排列。

参数名仍保存在方法签名与 canonical symbol 中，但 `invoke` 不再进行名称匹配。

### 7.5 泛型类型引用

函数体中对泛型参数 `T` 的值类型引用应使用：

```text
.generic<$.generic.T>
```

泛型类型声明中的实例字段可以引用该类型保存的泛型 typeid 字段：

```text
.generic<field(com.example::Box#.generic.T@.typeid)>
```

该字段如何物理保存属于 Middleware 和 Runtime；BIL 只要求 typeid 在语义上可取得。

---
