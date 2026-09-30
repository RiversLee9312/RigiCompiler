# §20 序列化（@Serializable / @SerializationBase / @Temporary / @Terminal 与 Parcel）

> MW11d 落地。本章描述「可跨 MessageQueue 复制边界」的编译期证明体系与
> Parcel 中间表示。消息语义见 RUNTIME §27（消息与 Messenger）。

## 20.1 概览

Rigi 的消息传输是**复制语义**：消息进入 MessageQueue 时被深复制为无别名快照
（经 Parcel 往返），发送方随后修改原对象不影响已接受的消息。`@Serializable`
是这一能力的编译期证明标注；`@Temporary` 显式切断不可序列化字段；
`@Terminal` 是 modifier 组合终点的一般机制；`Parcel` 是序列化对象的通用
中间表示（DTO 树）。

## 20.2 `@Serializable`（Entity wrapper）

```rigi
@Serializable
pub shared class Greeting {
    pub var code: i32
    pub var text: String
}
```

- 标在 class 声明上（`@WrapperTarget(.Entity)`），表示该类型允许跨
  MessageQueue 的复制边界。
  自动合成也支持具体 struct 宿主；enum struct 作为封闭判别值按值编码，
  可直接作为可序列化字段、容器元素或 Map 键值使用。
- 编译器为宿主合成序列化方法（源码层不可手写，经 wrapper 暴露面触达）：
  - `obj:Serializable.toParcel(loopedRefEnabled: bool = false): Parcel`；
  - `fromParcel\<T with Serializable>(parcel: Parcel, loopedRefEnabled: bool = false): T` —— 由 Parcel
    树重建（顶层函数，`core.serialization.fromParcel`）；
    **严格恢复**（§4.6.3 / D3，块 4-3）：表示中的类型与目标声明对应，
    不执行任何显式/隐式值类型转换（目标 i32 遇 wire 中的 i64 `3` 或
    double `3.0` 均报错），核验先于一切可能宽展/截断的 cast；集合逻辑
    类型与元素/键值逐层核验（List 不能当 Array）；业务字段集合与所恢复
    实际类型的应序列化字段集合完全一致（缺失、多余、可空字段缺项均
    报错，可空字段须显式存在，null 只可用于可空声明，不能用字段初始值
    填补缺失项；`@Temporary` 字段按既有排除及懒恢复规则；meta 槽不计入
    业务字段集合）；对象保留实际类型，多态恢复要求实际类型已登记、
    具备 Serializable 且可赋给目标声明。类型/字段集合不匹配抛
    `core.serialization.SerializationException`；未登记 wire 类型、
    图/树模式不匹配、节点编号错误等 wire 结构性状态错误仍抛
    `core.IllegalStateException`。动态解码只接受闭合可序列化候选的登记类型名：
    VM 类型引用拼写（内建别名/标准构造）与原生 TypeInfo 规范拼写；
    两种拼写相同只比较一次，不以运行期反射扩充候选，未知类型仍拒绝。
    此规则取代旧的宽松恢复行为，不保留默认转换，也不另加可选的
    严格模式；deepCopy、消息复制与显式图模式同规则收紧。
  - `deepCopy\<T with Serializable>(value: T, loopedRefEnabled: bool = false): T` —— toParcel/fromParcel
    往返的便捷组合；`obj:Serializable.deepCopy(loopedRefEnabled: bool = false)`
    投影到同一个顶层实现。布尔参数可按名称传入。
- **默认树模式**（`false`）：toParcel 结果不别名源图任何可变部分
  （数组/List/Map/嵌套对象全部新建）；fromParcel 重建出的兄弟字段是两个
  独立对象。往返后修改任一侧不影响另一侧。
- 默认模式只追踪当前递归路径：重复引用逐边独立复制，真环抛
  `core.IllegalStateException`，不无限递归。所有 Map（含 String 键）
  的内部表示统一为有序键值条目序列（§20.6），树/图模式同布局。
- **图模式**（`true`）：保留 class、Array、List、Map 之间的共享引用、自环
  与多节点环；复制图和源图独立。值类型按值复制，不参与引用身份表。
  每次公开调用拥有独立运行期上下文，编码用 `Place<Object>` 线性比较身份，
  外层 `finally` 在正常及异常路径释放全部 Place。解码先创建壳、登记编号，
  再填字段或元素；Array 在登记前已分配正确长度。

### 20.2.1 字段可序列性检查（编译期）

`@Serializable` 宿主的每个实例字段（非 static）必须可序列化，否则声明点
报错：

- 普通字段：类型须满足可序列（见 §20.3 清单与规则）；
- 泛型字段：由泛型约束证明（**最悲观规则**，见下）；
- 不可序列化的字段必须标 `@Temporary` 显式切断（§20.4）。

诊断形态：`可序列化类型 'X' 的字段 'f' 不可序列化：<原因>`。

### 20.2.2 最悲观泛型规则

编译器对未声明能力的泛型参数一律按其最小约束处理：`T` 只视为
`T with Any`，不能因某次实例化恰好是 `i32` 就假定它可序列化。

```rigi
// 错误：T 未声明 Serializable 约束
@Serializable
class Box<T> { value: T }

// 正确：
@Serializable
class Box<T with Serializable> { value: T }
```

同理，任何调用 MessageQueue/Messenger 序列化能力的泛型 API 都必须显式带
`with Serializable`；不存在「实例化后再隐式推导」的特例。

### 20.2.3 `with Serializable` 约束

泛型约束 `T with core.serialization.Serializable` 表示「T 具备序列化能力」，
要求类型实际携带源码声明的 `@Serializable`，或泛型参数明确具有该约束。
`SerializationBase` 不隐含 `Serializable`，两者也不存在 wrapper 继承关系。
标准库中的 SB 类型同时在源码中声明 `@Serializable`；Serializable 的实现
检测宿主是否携带 SB，再选择基元、String、Parcel 或集合的专用编解码路径。
这不允许类型擦除、任意接口转换或改变内建类型的固定 ABI。只有
`T with SerializationBase` 的泛型声明不能调用要求 `with Serializable` 的 API，
需要同时声明两种能力；消息 API 的其他形状约束仍由其声明决定。

## 20.3 `@SerializationBase`（可进入序列化图的基底清单）

`core.SerializationBase` 是源码声明的 `@Internal` 非 rich Entity
wrapper：应用限于 `core` 及其子命名空间，结合标准库命名空间保留规则，用户源码不能应用
（`@Internal` 语义：应用面收窄，API 签名暴露与 `pub` 可见性不变）。
内建基元、String、Array、List、Map、Parcel 均在标准库源码中同时应用
`@SerializationBase` 和 `@Serializable`，不存在隐式登记器。
新增 SB 宿主必须有 Serializable 中对应的编解码实现；没有实现时编译拒绝，
不能因为 SB 标记而跳过深复制或直接复制对象引用。

源码双重标注清单（集合内容仍须满足递归可序列性要求）：

- 全部整数基元（i8/i16/i32/i64/u8/u16/u32/u64）、**f32/f64**、char、
  bool、String；
- `Array\<T>`；
- `core.collections.List\<T>` / `Map\<K, V>`；
- `core.serialization.Parcel`（自身标 `@SerializationBase`，可嵌套）。

字段可序列性判定：标量/String 直收；Array/List/Map/Parcel 递归检查元素
（或键值）类型；`@Serializable` 宿主递归其字段闭包；`T?` 递归 T。
Map 的可序列化非 String 键也受支持，键和值分别检查闭包；集合自身仍按
既有 `equals`/`hash` 规则匹配键，不新增对象身份键语义。

## 20.4 `@Temporary`（切断序列化边的字段 wrapper）

```rigi
@Serializable
class Foo {
    path: String

    @Temporary(resume=() => rebuild(path))
    cache: Cache
}
```

- `@WrapperTarget(.Value)` + `@Terminal`（§20.5）的字段修饰器：被修饰字段
  **不进入序列化结果**，其 `resume` lambda 也不被序列化。
- **懒恢复**：`Temporary` 代理被修饰字段的读写；首次读取且尚未物化时调用
  `resume()` 重建并缓存；写直入。
- **深复制重建语义**：每次对象从序列化结果重新创建时，编译器重新创建新的
  `Temporary` 与新的 `resume` lambda——新 lambda 捕获的是**接收端新对象**
  的上下文（可捕获 `this`）；不复用发送端旧 Temporary/旧 closure。
  没有 wire metadata / closure 序列化 / 运行期恢复表等额外机制。

概念图：

```text
发送端：Object └─ Temporary(resume closure)   ← 不进入 serialized representation
接收端：new Object └─ new Temporary └─ new resume closure（捕获新 this）
```

## 20.5 `@Terminal`（modifier 组合终点的一般机制）

`@Terminal` 是内建 meta-annotation：标在 wrapper 声明上表示该 wrapper 位于
modifier 组合的**终点**，其内层不得再嵌套其它 wrapper。`Temporary` 是典型
用例（它已经代理内部字段的 getter/setter，再嵌套 wrapper 的 getter/setter
代理组合无意义——setter 代理可能永不生效、getter 代理值可能恒被忽略）。

编译期拒绝终点半径内的嵌套，诊断文案：

```text
<ModifierName> is terminal and cannot contain another modifier.
```

`@Terminal` 是一般机制，不硬编码只检查 `Temporary`；任何 wrapper 声明均可
标注。`@Internal`（命名空间内建应用限制）同为内建注解族：标在声明上后，
声明命名空间及其子命名空间之外的代码不得拿它修饰自己的声明；此规则适用于所有 `@Internal` wrapper，而非 SB 专用豁免。

## 20.6 Parcel（序列化中间表示）

`core.serialization.Parcel`：`@SerializationBase` class，键值 DTO 树，
`typeName` 承载类型名，业务元素槽为 `String → Any`。

```rigi
pub const typeName: String
pub func getElement\<T with SerializationBase>(key: String): T?
pub func setElement\<T with SerializationBase>(key: String, element: T?)
pub func elementCount(): i64
pub func keyAtIndex(index: i64): String?
pub func valueAtIndex(index: i64): Any?
// IEnumerable\<core.Pair\<String, Any>>：按插入序枚举键值对
```

语义钉死：

- `getElement`：键不存在（absent）抛 `core.NoSuchElementException`；
  **存入的 null 返回 null**（内部以哨兵区分 absent 与 null——两者不同）。
- 取回值从 Any 槽 cast 到 T；类型不符抛 `core.CastException`（
  `getElement` 自身的槽转换语义不变）。**严格恢复（§4.6.3 / D3，块
  4-3）下 `fromParcel` 解码路径不再依赖该 cast 做类型检查**：一切标量
  解码在 cast 之前先做名义核验（存储值运行时类型与字段声明类型逐项
  比对，`is` 判定——两宿主对数值均为精确判定，cast 才可能宽展/截断），
  不匹配抛 `core.serialization.SerializationException`，禁止整数宽度
  转换、浮点截断、溢出回绕、String/bool 与数值互转及用户定义转换。
- 嵌套 Parcel 合法（Parcel 自身满足 SerializationBase）。
- Parcel 作为被序列化对象时同样执行编解码，不是对象引用的透传通道。
  其内容与普通字段使用同一次图上下文；跨越 Parcel 边界的共享引用与环
  也必须保持图模式语义。解码结果不共享输入 Parcel 的可变内容。
  SB 只选择编码方式，不豁免类型检查、节点登记或深复制。
  严格恢复（§4.6.3 / D3）下其 wire 记录业务字段固定为
  `typeName`/`data`/`meta` 三元组，按字段集合完全匹配核验。

**字段键契约（§4.6.3 / D3）**：业务字段键须与源码字段声明使用同一
字符类别边界——非空；首字符为字母或 `_`；其余为字母、十进制数字或
`_`。此处字母固定为 BMP 的 Lu/Ll/Lt/Lm/Lo，数字固定为 BMP 的 Nd；
分类快照取自 Windows .NET 10.0.11 与 Linux .NET 10.0.12 一致的
`char.IsLetter`/`char.IsDigit` 真值（见 `Lexer/IdentifierCharacters.cs`，
同源生成 `stdlib/core/serialization.rg` 镜像）。按当前逐 UTF-16 `char`
词法，补充平面标量包括字母均不能作字段名；不把 Unicode 17 新增的
U+088F、U+0C5C、U+0CDC、U+A7CE、U+A7CF、U+A7D2、U+A7D4、
U+A7F1 擅自并入该固定快照。升级词法 Unicode 类别时必须同步更新
C# 真值、Rigi 镜像和边界测试，不能仅升级宿主运行时。
空字符串、`a.b`、首字符数字、标点、组合符与 emoji 等均拒绝；中文、
希腊字母及字母后阿拉伯十进制数字合法。`setElement` 与 `setDynamic`
在校验通过前均不落业务表，非法键抛 `core.IllegalArgumentException`。
不能把任意字符串字典当作 Parcel 字段表；Map 业务键不受此规则限制。

**元数据隔离（§4.6.3 / D3）**：类型判别（`typeName`）、枚举 case、
基元/集合载荷（`..value`）与图引用 envelope（`..id`、`..ref`、
`..data`）只进 Parcel 的受控元数据槽（internal 级读写面，供序列化
合成器与格式层使用），不进入公开业务字段表。`elementCount`、
`keyAtIndex`、`valueAtIndex` 与迭代只反映业务字段；元数据不计入
业务字段集合，也不与业务键参与同名比较。Parcel 自身的 `data`/`meta`
声明字段在 Parcel 被编码时经业务通道正常往返。

**wire null 表示（internal 受控面）**：可空集合元素/槽位的 null 在
wire 载荷（`.array<.any>`）中的表示是「`typeName` = NullSentinel wire
名的空 Parcel 记录」——由合成器编码（`EncodeOptionalElement`）、严格
恢复按同名判回真实 null（`DecodeOptionalElement`；非可空声明仍被
拒绝）。该表示的构造与判别经 Parcel 的 internal 受控助手对同编译
单元的格式层开放（`NullSentinel` 类保持 priv，不进用户公开面）；
文本格式边界（如 JSON）读侧按目标声明可空性把合法 null 造形为同一
wire 记录，写侧把该记录还原为格式 null——两种写出模式都不得把内部
哨兵文本或空记录对象泄漏进格式输出。

**SB 值 envelope 与动态解码分发（internal，wb-5-2d）**：SB 宿主
（标量/容器）作为元素或经 `..toParcel` 编码时的记录形态是
「`typeName` = 宿主 wire 名 + 受控 `..value` = 基元/容器载荷」；
开放泛型槽（合成恢复体的 `T` 元素）按记录形态解码——先经 Parcel
形状核验再按 wire 名分发重建。wire 名分发与反射查询面
（`typeNameOf`/`fieldsOf` 等）同纪律：VM BIL 别名形与 native
canonical 形双拼写并收，格式层按 `typeNameOf` 字面量拼写构造的
envelope 在两宿主都得命中等价候选；未登记 wire 名仍按既有
`IllegalStateException` 口径拒绝，不放宽为猜测构造。

**Type 装箱视图收集的物料边界（防御不变量）**：`.typeid<X>` 视图
（运行时 `typeOf(v)` 装箱/`toString` 的身份表）的收集准入口径是
「X 的布局 sheet 是否由该模块的收集/物化路径保证存在」——内建
Array 的闭合具化虽不经模板 plan（`ShouldMaterialize` 有意排除，
数组走内建数组布局），其 sheet 同样由内建路径按闭合实参恒出，故
其 `.typeid<.array<...>>` 视图必须与其它已物化构造一样补发。视图
收集不得依赖任何无关表达式的 `getid.type`/typeof 使用点碰巧覆盖
（历史缺陷：分发条件的运行时 typeof 比较被替换为静态字面量后，
`.array<.string>` 等视图随之丢失，运行时对已物化数组装箱即抛
`CastException`）；同样不得以全类型笛卡尔展开或 `Type<Type<...>>`
递归换取覆盖。

图模式的引用节点使用保留元数据键 `..id`、`..ref`、`..data`：首次
节点保存正编号、零引用编号及其负载，重复引用只保存目标编号。
`typeName` 仍是节点类型名。树/图双模式经元数据槽中的保留键判别
（`..ref` 的 i64 值认作 envelope），模式不匹配抛
`core.IllegalStateException`；编码与解码必须使用相同的模式。
所有 Map（含 String 键）的内部表示统一为按插入顺序交替保存键、值的
节点序列（有序键值条目数组），保留键值真实类型，不调用键的
`toString()`、不把业务键借作字段名；因而同一对象作为键、值及普通
字段时仍能恢复别名，`.rigi.type-identifier`、`content`、`a.b`、
空串等任意业务键树/图往返原样保留。未知引用、重复编号或非连续新
编号抛 `core.IllegalStateException`；负载类型不匹配沿 Parcel 的
类型检查报错。
图解码按原序填入 Map 键值存储，不在祖先对象仍未填完时调用键比较；
完整恢复后的查找与更新继续遵循 Map 原有 `equals`/`hash` 规则。

`..toParcel` / `..fromParcel` 是编译器为 `@Serializable` 宿主合成的序列化
方法（`obj:Serializable.toParcel()` 源码面经编译器改写转发宿主合成体）；
深复制不变量见 §20.2。

## 20.7 支撑集合 API 面（core.collections）

安全并发集合位于 `core`：`AtomicArray\<T>`、`AtomicList\<T>`、
`AtomicMap\<K,V>` 均为 safe shared class。元素（Map 的键和值分别）
须同时证明 shared-safe 与 `with Serializable`；标量/String 与裸集合
不会仅因其可作为序列化字段便自动满足此 wrapper 约束，而是依靠源码
上的真实 `@Serializable` 应用。共享安全通过公开的 `shared T` 型参
声明证明，例如 `SomeType\<shared T>`；标准库和用户源码使用同一规则。
显式与推断调用、嵌套类型以及尚未代入的外层型参均检查，不能附加隐藏证明。

构造入口为同步方法级泛型工厂 `AtomicArray.fromArray\<E>(source)`、
`AtomicList.fromList\<E>(source)`、`AtomicMap.fromMap\<A,B>(source)`；
可推断实参，不得经 `AtomicArray\<E>.fromArray` 访问 static。
工厂逐元素深复制，source 及其元素与新容器独立。

AtomicMap 和 AtomicList 分别以 `Atomic<Map<K,V>>` 与 `Atomic<List<T>>`
封装普通集合，单项删除在 Atomic 保护下直接调用底层集合。普通 List
以可空内部槽存储元素，删除后清空尾槽；Map 复用这一释放行为，包装层
不通过重建 Map 或 List 绕过引用残留。

对外状态操作全部为 async 方法（须 await 完成以观察生效）：

- Array：`length(): i32`、`getAtIndex(i32): T?`、`setAtIndex(i32,T)`、`iterate(): AtomicSnapshot\<T>`。
- List：`length(): i64`、`add(T)`、`getAtIndex(i64): T?`、`setAtIndex(i64,T)`、`removeAt(i64)`、`iterate(): AtomicSnapshot\<T>`。
- Map：`count(): i64`、`set(K,V)`、`tryGet(K): V?`、`containsKey(K): bool`、`remove(K): bool`、`keyAtIndex(i64): K?`、`valueAtIndex(i64): V?`、`iterate(): AtomicMapSnapshot\<K,V>`。

每次读写均在私有 Atomic.mutate 的持锁回调内完成；入站和出站元素
深复制，删除重建 backing 以释放普通 List 尾槽引用。读越界返回 null，
写越界与 List 删除越界沿普通集合抛异常；Map 键仍按下述 toString 规则。
返回的 snapshot 为 shared 可枚举值，每次同步 `iterate()` 新建独立
本地枚举器及元素副本，既不观察容器后续修改，也不共享可变游标。
Map 快照只保存独立键/值数组，不将普通 Pair 标记为 shared；其公开
构造的两个数组必须等长。容器不公开 Atomic、Handle 或可变 backing。

Parcel 与序列化合成代码使用的最小集合面（MW11d-B1）：

- `List\<T>`：`add / getAtIndex / setAtIndex / removeAt / length /
  iterate()`（`getAtIndex` 越界读 null；`removeAt` 越界抛
  `core.OutOfBoundException`）。
- `Map\<K, V>`：`set / tryGet / containsKey / remove / count /
  keyAtIndex / valueAtIndex / iterate()`。
- **K 相等口径（equals-or-hash 判等链，用户裁定）**：Map 键相等 =
  `==`——键类型声明了 `operator equals` 走它（运行期最派生）；未声明
  的类型走 Any 承诺的默认 `equals`（双虚调 `hash()` 比较，§3.8.1），
  hash 碰撞即判等，**绝不涉 `toString`**。String 键按内容、标量键按值
  判等语义不变；默认 `hash` 对对象是身份哈希——不同身份的对象键互不
  覆盖，需要值语义键的类型请 `override hash` 或实现 `operator equals`。
  需要纯对象身份键的场景也可用 `rigi_rt` 对象身份原语（RUNTIME §27.6，
  Receiver listener 表即一例）。
