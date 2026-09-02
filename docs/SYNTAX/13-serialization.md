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
- 编译器为宿主合成两个方法（源码层不可手写，经 wrapper 暴露面触达）：
  - `obj:Serializable.toParcel(): Parcel` —— 深快照为 Parcel 树；
  - `fromParcel\<T with Serializable>(parcel: Parcel): T` —— 由 Parcel
    树重建（顶层函数，`core.serialization.fromParcel`）；
  - `deepCopy\<T with Serializable>(value: T): T` —— toParcel/fromParcel
    往返的便捷组合。
- **深复制不变量（无别名快照）**：toParcel 结果不别名源图任何可变部分
  （数组/List/Map/嵌套对象全部新建）；fromParcel 重建出的兄弟字段是两个
  独立对象。往返后修改任一侧不影响另一侧。

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

泛型约束 `T with core.serialization.Serializable` 要求实参类型携带
`@Serializable`（Entity wrapper 体系，`with` 检查应用事实）。消息类 API
（`MessageQueue`/`Reader`/`Receiver`/`Messenger`，RUNTIME §27）的类型参数
统一带此约束。注意：Entity wrapper 不能挂标量——`i32` 等基元不满足
`with Serializable`（它们走 `@SerializationBase`，§20.3），因此
`Messenger\<i32>` 不是合法形态；消息负载应当是 `@Serializable` class。

## 20.3 `@SerializationBase`（可进入序列化图的基底清单）

`core.serialization.SerializationBase` 是 `@Internal` 的 Value 级
wrapper：非 `core.serialization` 命名空间的代码不得拿它修饰自己的声明
（`@Internal` 语义：应用面收窄，API 签名暴露与 `pub` 可见性不变）。
内建类型的登记由编译器在符号层合成（不受 @Internal 限制）。

当前登记清单（`with SerializationBase` 恒真）：

- 全部整数基元（i8/i16/i32/i64/u8/u16/u32/u64）、**f32/f64**、char、
  bool、String；
- `Array\<T>`；
- `core.collections.List\<T>` / `Map\<K, V>`；
- `core.serialization.Parcel`（自身标 `@SerializationBase`，可嵌套）。

字段可序列性判定：标量/String 直收；Array/List/Map/Parcel 递归检查元素
（或键值）类型；`@Serializable` 宿主递归其字段闭包；`T?` 递归 T。

## 20.4 `@Temporary`（切断序列化边的字段 wrapper）

```rigi
@Serializable
class Foo {
    path: String

    @Temporary(resume: () => rebuild(path))
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
非声明命名空间的代码不得拿它修饰自己的声明。

## 20.6 Parcel（序列化中间表示）

`core.serialization.Parcel`：`@SerializationBase` class，键值 DTO 树，
`typeName` 承载类型名，元素槽为 `String → Any`。

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
- 取回值从 Any 槽 cast 到 T；类型不符抛 `core.CastException`（预期行为）。
- 嵌套 Parcel 合法（Parcel 自身满足 SerializationBase）。

`..toParcel` / `..fromParcel` 是编译器为 `@Serializable` 宿主合成的序列化
方法（`obj:Serializable.toParcel()` 源码面经编译器改写转发宿主合成体）；
深复制不变量见 §20.2。

## 20.7 支撑集合 API 面（core.collections）

Parcel 与序列化合成代码使用的最小集合面（MW11d-B1）：

- `List\<T>`：`add / getAtIndex / setAtIndex / removeAt / length /
  iterate()`（`getAtIndex` 越界读 null；`removeAt` 越界抛
  `core.OutOfBoundException`）。
- `Map\<K, V>`：`set / tryGet / containsKey / remove / count /
  keyAtIndex / valueAtIndex / iterate()`。
- **K 相等口径（取舍注明）**：无约束泛型不可用 `==`（Any 只承诺
  toString），Map 键相等走「两侧 `toString()` 后 String 内建 ==」——
  String 键 toString 即自身、标量为十进制文本时语义正确；引用类型
  toString 为类型名，**不是对象身份**，不要把 Map 当身份索引使用。
  需要对象身份键的场景用 `rigi_rt` 对象身份原语（RUNTIME §27.6，
  Receiver listener 表即一例）。
