# Middleware 布局、泛型与调用 ABI

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

## 1. 导出、入口与元数据身份

`NativeBuildOptions` 明确区分 executable、static-library 和 dynamic-library。
C 导出 canonical 同时作为 MIR 可达根和 layout 的构造调用根，不能仅在 LLVM
阶段添加外部符号。CoroutineSplit 在原始 TaintAnalysis 集合上检查导出，避免
Plain tainted 函数降为 trap 后仍保留同步签名；随后遍历所有显式与构造器/enum/
wrapper 隐式调用，拒绝任务发布、间接/虚/接口目标。库的 eager singleton/global
初始化也接受相同闭包检查；仅可信标准库同步构造及准确绑定的同步锁创建可用。

外层 C wrapper 为 external linkage，内部 Rigi 函数仍 internal。wrapper 按内部
CallAbi 编组后调用真实函数体；bool 在 uint8_t 与 i1 间归一/扩展，Linux 窄整数
根据目标 C ABI 挂 signext/zeroext，属性只作用于 C 边界。异常检查复用 typed
uncaught reporter，报告并终止，不跨 C 栈恢复。shared 链接按显式导出 allowlist
隐藏运行时面；static 用 archiver 归档经过 runtime merge、default<O2> 的 PIC
最终对象，uv/mimalloc 由宿主另行链接。library runtime 变体不含 main，Linux
预处理和 codegen 同时使用 PIC，避免 executable TLS local-exec 重定位进入 DSO。
kind、完整 export map、ABI/visibility、runtime flags、archiver 内容身份参与缓存。

库通过 uv_once 完成 GC、singleton、DAG global 初始化与 atexit 注册；每次 API
检查同一宿主线程，不 per-call shutdown，不运行 rigi_entry/main，也不支持卸载。
可执行入口则从 argc/argv 构造真实 Array<String>，使用闭合 Array/String sheets
与严格 UTF-8 检查，将 owned String 初始引用转移给零初始化数组槽。合成入口
持有桥接数组根至 Dispatcher quiescence 后释放；正常与 typed reporter 路径都
清零根，避免异步 frame 拥有引用时泄漏或重复释放。Windows wmain UTF-16 转换
与工具链分支属于 Windows runner 验证范围，Linux 验收不能替代其动态证据。


LLVM 元数据全局名保留完整 canonical，由 LLVM API 处理文本引号转义，不将泛型逗号和类型名中的点折叠成同一字符。

内部 `MirGetClassTypeArgument` 按含元数的宿主布局键读取真实实例实参：普通类读取已规划的隐藏 typeid 槽，固定 ABI 数组读取前缀中的元素 sheet；不把数组伪装成具有普通隐藏字段的类。泛型函数的 coroutine frame 名虽包含原函数签名，但 frame 声明本身是固定布局的非泛型类，是否开放必须按声明判定。


### 1.1 Handle 隐藏布局与 Cell 虚槽

安全 Atomic 集合使用普通 Rigi 源码持锁回调与逐元素序列化复制。
静态泛型工厂帧只含方法级 typeid；开放 class 的 new 在 MIR 保留实际
泛型映射（例如工厂 E 到宿主 T），分配 sheet 可复用模板，实例隐藏
typeid 仍须写入。可挂起调用的嵌套开放 class 实参从接收者隐藏字段
取得 typeid，不按调用方同名型参猜测。协程 frame 名注册与 MirType
使用相同 canonical 归一，包含多参数泛型返回类型时也不产生双键。

`BuiltinToStringDispatchPass` 在 CoroutineSplit 前为 Any/Object 的
对象文本覆写生成实际类型分派；命中现有虚槽后走普通 MIR 调用，
未命中才使用默认 native 文本 helper。普通 Map 的泛型对象键因而
遵循用户 toString 覆写，分派中的挂起与引用生命周期仍走统一后续 pass。
同一 pass 以同构通道为 `hash` 生成 `$mw.any.hash` 分派链（§4.8
`any_hash` 面； CoroutineSplit 白名单同步放行该合成 fn），无任何
override 时不合成链、调用点直调 helper。

`==`/`!=` 的 Any 默认 equals 臂（equals-or-hash 判等链，用户裁定）：
未声明 `equals` 的类型比较直调 `stdlib/.intrinsics.rg` 中绑定、发射的内建默认体 `core::Any$$equals`（双虚调
`hash`，体内 `Any$hash` invoke 经 FlowBuilder 重定向 +
`$mw.any.hash` 分派链得 override 感知）——静态左操作数在
UserOperatorLowering 直调（MirReachability 对 Any/Object 两默认 equals 函数恒收编）、
泛型占位左操作数在 GenericOpEmitter 候选臂全落空后的末臂直调。
已知边界：静态链无 `equals` 而运行期实际类型（子类静默 hiding 再定义）
有 `equals` 时，native 直调默认体、VM 按实际类型派发用户 `equals`，
分歧仅限该组合（Map 主路径泛型臂两端一致）；后续可用
`$mw.any.equals` 双操作数通道闭合。

同步 callable 若可挂起，CoroutineSplit 的动态实现协议臂在写入具体 callee frame 前使用 MIR cast/box 适配参数；DONE 从具体结果字段先读到同类型临时槽，再转换到调用点类型。全部临时槽仍由既有生命周期 pass 管理，禁止把开放胖值直接写入具体标量/struct 字段或反向读取。

Handle 固定 `.handle` 布局（3b-β 双持有 capability，48B）：16B 对象头、+16 壳指针（8B）、+24 shellID（8B）、+32 可写能力位（1B，对齐补齐）、+36 kind（4B）；refMap 恒 0——target 不在 capability 内，由壳锚持有（RUNTIME §28），隐藏槽不进入可枚举 Fields。普通 ARC 析构经壳析构钩子原子减量、归零转移经属主通道释放目标；macroGC trace 经壳读 target 作代理边、fence 冻结期就地原始拆。保证正常销毁与环收集恰好释放一次，禁止额外 native release 面。

Cell/ReadonlyCell 的 `getValue/setValue` 与同步 Func/Action 的 `$$call` 虚槽统一使用胖值 ABI。`FatValueSlotAbi` 按真实继承和签名为槽生成适配器，复用普通参数编组、装拆箱和 ARC 临时销毁；封闭与开放泛型调用均使用同一槽约定，callable 的全部普通参数逐项适配，非 void 返回统一为胖值。同名 Cell 重载、接口独立段与 async callable 保留自身 ABI。适配器把异常 pending 原样传回普通调用点的异常边；异常出口从函数全局值类型读取真实返回类型，不能从 opaque pointer 推断。


## 7. 泛型、调用与 ABI

- reified 泛型：共享代码体 + typeid 隐藏参数（RUNTIME §1 既定取舍：不提供泛型
  热路径的单态化特化 pass）；typeid 参数的传递位置与求值顺序在 ABI 定稿；
- vargs/kwargs：包 = 单值胖引用，按 §7.2 序按位传递，无 shim——frontend
  打包（逐元素 BoxToAny），Middleware 直译；
- `invoke fn(..super)` → 直接基类原始实现；`..create` 仅属 Middleware/VM 生命
  周期阶段；
- `invoke.indirect` → callable 协议（`$$call` 虚调用；async `$$call` 同槽，结果为 `Task`/`Task<T>`，CoroutineSplit 改写目标 stub）；
- native 函数：直接生成对原生符号的调用——rigi_rt 面 C 名 = `rigi_` + symbol；
  非 rigi_rt 用户库（L6 起）C 名 = symbol 原文，链接输入经 `native --link`
  追加（RUNTIME §26 库解析留白的定稿）；返回用户引用类型的 FFI ABI
  在此定稿（SYNTAX §4.6 / RUNTIME §26 的留白）。

**String ABI（MW7 定稿）**：不可变值类型，槽仍为 `{ i8* data, i64 len }` UTF-8。
字符数据是 ARC 计数缓冲：堆块 `{ atomic u32 rc, u32 reserved, data[] }`，槽内
`data` = 块基址 + 8；字面量永生 `rc = 0xFFFFFFFF`（acquire/release 均跳过）。
可观察语义仍为按值深拷贝；native 计数对用户不可见。Rigi 内部按值传
`{i8*, i64}`；C 边界（native 面与运行时面）一律经 `rigi_string*` 传递
（StringOut 出参置首参），规避 16 字节 struct 按值传递的 win-x64/SysV ABI
分歧。Nullable 统一 tag0（内联空/值）/ tag1（堆值）分派，与其它值类型同一套
胖引用槽，不另开 String 特例。

**.typeid 构造 sheet（MW8c-1 定稿）**：`.typeid<X>` 是存储目标 TypeSheet* 的
值类型（Rigi 投影 `Type<T>`），按构造 canonical（`core::Type<X>`，无界 =
`core::Type<core::Any>`）各自出 TypeSheet/TypeInfo，不再擦除单键。形态：
typeSize 8、FlagInlineValue、refMap/iMap/vTable/wrappers/ifaceClosure 皆空。
收集点含 fn 局部、getid/cast、type.is 静态目标。`stdlib/.intrinsics.rg` 已声明
`Type<T>`（Span/SharedSpan 等固定 ABI 类型也有源码声明）；源码描述成员与能力，
TypeLayout/Emit 仍按固定 8B ABI 生成每个构造身份的 sheet，不能按空 struct
的普通字段布局推断大小。VM 对 `Type<X>`/`Type<Any>` 不变（无协变）→ `baseTypeId`
不指向无界成员。另内建 `.null` sheet（typeOf(null) 实际类型，typeSize 0）。

**typeid 与 C 边界胖引用**：BIL §7.2 六段序（`.this` → 固定泛型 typeid → 泛型包
→ 普通参数 → 值包 → 具名包）即泛型调用约定；typeid 的 LLVM 表示 = TypeSheet
指针（`getid.type` 物化）。String 与 16 字节胖引用的 C 边界一律 out 首参（Rigi
内部按值）：String 经 `rigi_string*`，用户引用经 16 字节对齐的胖引用槽指针。
RUNTIME §26 的调用约定留白在 x64 双平台上天然唯一（Win x64 / SysV AMD64）——
clang 编 `rigi_rt` 与 LLVM 生成代码各自按目标 C ABI lowering 一致。
NativeCallEmitter 的 CreateFunction/AddFunction/BuildCall2 不设置调用约定编号，
当前没有 LLVM fastcc 或独立跨平台 fastcall 协议；以宿主 x64 C ABI、CallAbi
参数编组与 bool/out 槽规则为准。

**构造类型具化计划**：从模块收集闭合构造类型（new / fn .vars.args / getid.type
与 cast 目标 / 数组元素 / 代入后的 extends·implements / 内层构造实参；
MwTypeKey.Normalize 归一，环保护）。具化计划独立入表：字段复用模板
canonical（`.generic` → 16B 胖值槽入 refMap，具体类型照旧）；vtable 槽 =
模板 fn canonical；基类链沿代入后的构造基类递归。iMap 按代入后的构造接口具化生成（接口 sheet 引用具化接口空壳 sheet）。
**泛型值类型（struct/enum struct）同法具化**：字段/vtable 槽 0/refMap/enum
判别表复用模板计划（占位字段恒 16B 胖值槽，构造与模板布局同构），无对象头
隐藏 typeid 槽、无 iMap（值类型不参与虚/接口派发）；ifaceClosure 按构造
canonical 重生。构造 interface/wrapper 的 new 保留受控拒绝（语言层非法，
P3 已拒；且 native 对「无 init 声明 + 零实参」构造本就整体受控拒绝）。

**类级 typeid ABI（被调方自取 / 值类型直传对偶）**：泛型类在 16B 对象头之后、用户字段之前
为每个类级类型参数留 i64 TypeSheet 指针槽（继承时基类隐藏字段在前；
typeid 不是托管引用，不进 refMap）。`new` 站在 rigi_alloc 之后把构造实参
的 TypeSheet（或当前 fn 的 `.generic.*` 局部）写入隐藏字段，再调
`..init.wrapper` / init——init 实参不传类级 typeid。实例方法（含 init）
的 LLVM 调用约定剔除类级 `.generic.X`；entry prologue 从 `.this` 隐藏字段
装入该局部。方法级 typeid 仍由调用点按 §7.2 序物化传递。
**泛型值类型宿主无对象头可藏**：其实例成员 fn 的类级 `.generic.X` 参数
**保留在 LLVM 调用约定内**（.this 槽指针之后、普通参数之前），调用点按
接收者/构造目标的构造形态代入直传——闭合实参 = TypeSheet 常量，外层占位
= 当前 fn 的 `.generic.*` 局部；frontend 对方法接收者的「构造 → 裸模板」
擦除 cast 由 MIR 构建期溯源（FlowBuilder._erasedValueHosts）回解构造形态。
值类型静态成员无类级 typeid 实参（SYNTAX §9.2.3 本就不用；BIL 仍声明的
形参槽落 core::Any sheet 常量，对齐 VM AlignGenericHiddenArgs 缺省 .any）。

**泛型占位操作数运算（G4 定稿）**：`T extends Bound` 内的 `a + b` 族
（操作数静态类型本身为顶层 `.generic<...>` 占位）直译为 MirGenericBinaryOp /
MirGenericUnaryOp，发射期运行期派发（GenericOpEmitter，VM ExecuteBinary
同口径）：内建标量/String 按实际 sheet 逐臂求值优先；否则按左操作数实际
typeid 经 rigi_type_is 逐候选臂判定（候选 = 模块内全部同名 operator，
派生深度降序；普通形参再经右臂 type_is 校验）；!= 调 equals 取反、
</<=/>/>= 调 compareTo 按 ComparisonResult 判别映射 bool。全落空抛
core.NoSuchMethodException（VM VmException「没有用户 operator …」对应面）。
已知构造宿主即使含嵌套开放实参，也走普通 operator 解析、可达边和调用路径。
泛型 class 候选按已具化的闭合宿主 sheet 分臂，普通形参类型同步代入；类级
typeid 由 receiver 隐藏槽恢复，不把裸模板当作对象的构造身份。
边界：泛型值类型宿主的 operator 候选编译期受控拒绝；方法级泛型 typeid
注入仅支持普通形参恰为占位的精确形态，其余
注入 core::Any（VM 推断失败缺省同口径）；Entity wrapper 的 operator
代理链不经此面；接口声明的 operator 不在候选集（VM FindOperator 的宿主
集只含 extends 链，同口径）。

**TypeSheet 全局名**：所有元数据全局名保留完整 canonical；
`GenericAbi.EscapeGlobalName(prefix, canonical)` 直接返回 prefix + canonical，LLVM API
负责文本 IR 引号转义。不能把 `<,>`、点和空格折叠，因为 `A<B,C>` 与 `A<B.C>`
等不同身份会因此撞名。

**is / supers / with（MW5 c3）**：MIR 直译 `type.is` / `type.supers` / `type.with`（含 `.indirect`）为 `MirTypeCheck`；发射调 `rigi_type_*` helper（typeid+payload 两枚 i64 + 目标 TypeSheet*，返 i32 0/1）。实际类型：tag2 取对象头 TypeSheet，tag0/tag1 掩码胖引用 typeid。TypeInfo 形态 `{name: rigi_string, sheet*, wrappers**, wrapperCount, ifaceClosure**, ifaceClosureCount, nullableElement*, typeIdBound*, destroyNative(void*)}` 与 TypeSheet 成对发射，`typeInfoId` 回指；wrappers 来自声明 `BilWrappedModifier`；ifaceClosure 为传递 implements 闭包（含接口的父接口）。泛型占位目标（`.generic<$.generic.T>`）降为 typeid 局部（与 `.indirect` 同 helper）。接口默认方法（有 fn 体）进入实现类 iMap 槽（未 override 时指向接口方法）；MirReachability 补默认方法可达边。

**Any/Box ABI（RUNTIME §2/§4 定稿）**：胖引用 128-bit = `{typeid: i64（最高字节
tag）, payload: i64}`，16B 对齐。tag0（ValueType ≤8B）payload 内联值；tag1
（ValueType >8B，含 string 的 `{i8*,i64}` 与大 struct）payload = `rigi_malloc`
+ memcpy 的 unique 裸数据块（无对象头、无块内 typeid）；tag2（Object）payload =
对象指针。装箱 = `cast` 值类型 → `.any`/`.object`；拆箱检查 tag 与掩码后
TypeSheet 指针，不符抛 `core.CastException`（MW9b-G 换真异常，经
ExceptionEmitter 共享抛出辅助：alloc + 真 init + `rigi_exc_raise` + 沿 MIR
异常边传播；fromType = 发射期静态源类型名常量、toType = 目标 sheet 运行期
显示名，拼写对齐 VM 消息口径）。native `.any` 参数/返回经
16B 对齐槽指针传递（D6：C 边界 16B 胖值一律指针）。`any_to_string` 经该槽指针
读 `{typeid, payload}` 分派（标量面 / String 拷贝 / TypeInfo.name）。Box 复制的
acquire/release 不在此发射（E4：内存正确性 MW7 RcInjection 统一收口）。

**动态 new（MW8b/MW8c 定稿）**：vtable 槽 0 为 per-类型 `mw.init.dispatch`（if 链比 argc 与
形参 TypeSheet*）；class thunk 胖返回（复用 EmitAllocAndInit）；struct thunk 为 sret
`void(ptr %out, fat...)`（零初始化 → 可选 wrapper → init 原地生效；内联槽直传 result alloca，胖槽按 typeSize 走 tag0/tag1）；
标量/String 零参 T() 在 vTable 查找前比对内建零值 sheet 直产零值（argc>0 落 miss）。
vTable/分发器/匹配落空抛 `core.NoSuchMethodException`（MW9b-G 换真异常；thunk
内部 miss 构造异常 + raise + ret undef，pending 由调用点检查接力）。可达性对 `new.indirect` 保守收模块内全部 init 族（含 stdlib `core*`；经该保守边引入且 MIR 不可构建的 init 族试探性跳过，运行期抛 NoSuchMethodException）。
MW9b-G 起三占位 abort 面（`rigi_abort_divided_by_zero` / `rigi_abort_invalid_cast` /
`rigi_abort_no_such_method`）与数组越界写 abort 面（`rigi_abort_array_oob`）已退场：
除零/越界写分别改抛 `core.DividedByZeroException` / `core.OutOfBoundException`
（守卫指令挂 MIR 异常边，core 异常类型 init 族 + getMessage 经
MirReachability 恒可达白名单保证可发射）；保留 `rigi_abort_arithmetic_overflow`
（i64 MIN/-1 基础设施溢出失败，VM 基准非语言级异常）与
`rigi_abort_array_negative_length`（分配负长度）。

**singleton 运行时（MW10 定稿）**：每个 singleton 类型合成三态取实例 fn
（合成静态槽 state：未构造/在途/就绪 + cache 胖引用槽；VM 的双表在 native
收敛为 state+cache 两槽，state==就绪 ⇔ cache 非空）；在途 = 构造环 → 抛
`core::RuntimeException`。全部 `new type(singleton)` 改写为 get 调用（实参
按 VM 口径丢弃——急切初始化保证用户代码执行时恒已构造）；无零参 init 者
合成空 init 凑齐构造尾，只有有参 init 者编译期受控拒绝（VM 急切初始化期
同口径必败）。`rigi_entry` 启动序：singleton 族急切初始化 →
`..globals.init` → main（此序经实证对齐 VM InitializeSingletons 启动序）；
当前 getter 状态为普通 i32 槽，依赖 Worker 启动之前的急切初始化；它没有独立
并发首次构造协议。Native 库通过 uv_once 完成一次性初始化，API 要求同一宿主线程。



### 内建声明源码与 helper 身份

固定 ABI 的内建类型仍由 bootstrap 描述身份/布局；Any/Object 的默认 toString、hash
与 equals 实现源在 `stdlib/.intrinsics.rg`，经普通 P3/P4 绑定与发射，并非后端手写或
硬编码合成 BIL 体。StdlibSources 将该文件标为 IsIntrinsicDeclarations，
LocalSymbolEmitters.EmitBuiltinNativeMethods 发射其内建成员声明；用户同名函数
不会获得编译器权限。EmittingDriver 为可信编译器库的 any_hash/any_to_string native
helper 发射身份 metadata，BilCompilerHelpers.Resolve 在独立模块私有 canonical
场景仍取得准确绑定。默认方法到 helper 的 MIR 重定向及 override 分派保持前文
规则；源码化不改变固定布局，也不能通过普通同名声明伪造 helper 绑定。

相关章节：[wrapper §5](WRAPPER_BAKING.md)、[协程 §6](COROUTINE_LOWERING.md)、[runtime 面 §4.8](RUNTIME_ABI.md)、[工具链/缓存](TOOLCHAIN_AND_CACHE.md)。
