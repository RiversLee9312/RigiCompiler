# VM值模型与方法派发

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](BIL_VM_DESIGN.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 3. 值模型（§22.1）

### 3.1 精确标量，独立表示

每种内建精确类型有独立的 `VmValue` 子类型，**不允许**统一装箱为
`long`/`double` 而丢失精确类型——§22.3 的运算查询键要求精确类型可分辨：

| BIL 类型 | VM 表示 |
|---|---|
| `.i8` `.i16` `.i32` `.i64` | `VmI8(sbyte)` `VmI16(short)` `VmI32(int)` `VmI64(long)` |
| `.u8` `.u16` `.u32` `.u64` | `VmU8(byte)` `VmU16(ushort)` `VmU32(uint)` `VmU64(ulong)` |
| `.f32` / `.f64` | `VmF32(float)` / `VmF64(double)` |
| `.bool` / `.char` | `VmBool(bool)` / `VmChar(uint)`（32 位 Unicode scalar，排除代理区） |
| `.string` | `VmString(string)`；非 rich 值类型，赋值/传参按值语义理解（§6.2），C# string 不可变性天然满足 |
| `.void` | `VmVoid.Instance` 单例，仅作占位 |

### 3.2 复合与运行时类型值

- `.handle` 使用 `VmObject` 的专用强引用 target 与 kind/mutable 元数据；这些槽不进入字段或 wrapper 隐藏字段枚举。CLR 对象图保持目标存活；Handle 无 dispose 追踪。Place 的普通字段持有与 Handle 独立，释放 Place 后 Handle 仍有效。VM 直接持 target 是壳协议的 hook 模拟：native 宿主以 capability + 计数壳等价实现（`RUNTIME.md` §28），双宿主行为对拍一致。

泛型宿主的 `$$call` 匹配先按实际 receiver 代入参数类型，宿主 hidden typeid 由入帧逻辑注入，不计入显式实参数量；方法级 hidden typeid 仍来自调用点。嵌套 Nullable 等类型的内建别名须递归归一化，null 与非空元素按 Nullable 可赋值规则匹配。

- `VmObject`：运行时类型 canonical 引用字符串（经 VmContext 解析到模块声明，
  BIL 层以 canonical 字符串为身份）+ 字段字典（字段符号 → VmValue）。class 为引用语义。
- 值类型（struct / 内建值类型）：赋值、传参、返回时**深拷贝**；`VmObject`
  对 struct 实例同样适用但拷贝语义不同，由类型声明的 rich/struct 属性区分。
- `VmEnum`：case 符号身份 + payload 值；身份比较按 `CaseSymbol` canonical 字符串相等。
- `VmArray`：元素精确类型 + `VmValue[]`。
- `.any` 胖值：`VmAny(VmTypeId typeid, VmValue payload)`，自描述。
- `VmTypeId`：**typeid 即 canonical 类型符号字符串值**；`.typeid<TBound>` 与 `.generic<...>`
  的运行时值都是它（`.generic<T>` 物化后就是 typeid）。`getid.type` /
  `getid.var` / `getid.field` 产生对应符号引用值。
- `.nullable<T>`：引用类型的 null 用 `VmNull.Instance`；值类型 nullable 用
  存在位包装。cell 存储由实际 Cell/ReadonlyCell 子类的 `VmObject` 承载
  （BIL `.cell<T>` / `.readonly_cell<T>` 类型视图），槽本身按引用共享。
- `.breakid`：结构化控制 capability，VM 内为指向目标块执行帧的不透明句柄。

### 3.3 运算实现查询（§22.3）

- 操作数为内建精确类型 → 语言规定的 primitive 语义（在家族基类中按
  精确类型分派，见 §5）。
- 操作数为用户类型 → 按精确类型解析到对应 operator fn，以普通调用语义执行
  （复用调用基建，不改变指令语义）。
- `==`/`!=` 的 Any 默认 equals 臂（equals-or-hash 判等链，用户裁定）：
  `DispatchUserBinary` 先经 `FindOperator` 按 receiver 精确类型与基类链、
  成员声明查询 `equals`；仅在查询结果为空且默认函数存在时，才从函数表
  直查 `.intrinsics.rg` 的 `core::Any$$equals` 源码体（双虚调 `hash`，
  不进行源码级重载排名）。完整 intrinsics 编译也发射内建源码成员声明，
  因此不能把“默认 equals 永远无声明/FindOperator 永远看不到”当作不变量。
  `!=` 取既有 equals 结果后反转；自声明的最派生 equals 命中后不经过
  fallback，绝不涉 `toString`。native 侧
  对应面（静态 Any 直调 / 泛型占位末臂）见
  MIDDLEWARE_ARCHITECTURE——分歧仅限「静态链无 equals 而运行期实际
  类型（子类静默 hiding 再定义）有 equals」组合；Map 主路径（泛型臂）
  两端一致。

### 3.4 方法派发：逻辑 TypeSheet（拍平 vtable + iMap）

方法派发（虚方法 / 接口方法 / callable `$$call` / `fn(..super)`）统一建立在
`Bil/Vm/VmTypeSheet.cs` 的逻辑 TypeSheet 抽象上——`RUNTIME.md` §6–§9 的
加载期拍平等价物：

- 每个类型声明一张 sheet（按声明 key 记忆化，可重入锁保护，多 Worker 并发
  构建）；槽序 = 继承槽 → 自有槽 → 各接口段。克隆式构建天然保证「继承中
  相同方法保持相同 vtable offset」（§7 不变量），派生 sheet 自带继承槽与
  iMap（§9 拍平，调用期不上溯）。
- 槽按归一化签名键匹配（名称 + 参数类型序列 + 返回类型；泛型参数按出现序
  归一为位置占位，参数名不参与）；有体成员替换全部同签名槽的实现（含继承
  来的接口段槽）。abstract/init/ext 成员与除 `$$call` 外的运算符不进 vtable
  （callable 协议例外，SYNTAX §9.2.1）。泛型基类/接口（`D : B<i32>`、
  `C : IFace<i32>`）的槽匹配按 extends/implements 实参代入——克隆基类槽
  与构建接口段时把槽的签名源按 `{T_i → arg_i}` 代入再重新归一化（
  `m(x:#0)` → `m(x:.i32)`；转发形态 `D<T2> : B<T2>` 代入后归一化不变），
  子类 `override m(x: i32)` 因而同 key 替换而非追加新槽；`SlotSymbol`/
  `ImplSymbol` 与 `OffsetBySymbol` 键保持声明级原样。多级链每跳各按直接
  extends 构造代入（`E : D<i32>` ← `D<X> : B<X>` 逐跳 T→X→i32）。
- iMap（`InterfaceBase`）记录接口声明 key → 接口段基址；接口派发 = 段基址 +
  接口内相对 offset（§8）。owner 是预定义根（core::Exception 等不进符号段）
  时按签名在 receiver 实际类型槽防御扫描（getMessage 多态路径）。
- 统一入口 `VmContext.ResolveDispatch(staticSymbol, receiver)`：静态符号经
  owner 声明 sheet 换算 offset，再取 receiver 实际类型 sheet 的槽实现 fn；
  不可派发（无 receiver/static/全局符号）退回直查。`invoke.indirect` 的
  `$$call` 同样按 receiver 实际类型的 sheet 槽匹配实参后经 `InvokeValues`
  落地（保持 Method wrapper 链与 receiver 首参 ABI）。
- `fn(..super)`（BIL §15.5）在直接基类 sheet 上按同签名槽取实现后**直接压帧**
  ——super 非虚、绕过 wrapper 派发链与二次派发；init 内 super 按基类 init
  重载选目标（实参剥 `$.this` 与隐藏泛型前缀后比对）。基类存在同名不同签名
  重载时的精确选择需要调用点静态类型（BIL 不泄露 base canonical 名），VM 以
  同签名优先、唯一按实参个数匹配兜底。
