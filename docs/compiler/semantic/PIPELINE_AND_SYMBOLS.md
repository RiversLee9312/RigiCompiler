# 中端管线与符号图

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](SEMANTIC_ARCHITECTURE.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 1. 定位与总体管线

中端的输入是前端产物：经 `ASTIntegrityValidator` 验证的 `RootASTNode`
（每编译单元多个源文件，每文件一棵）。输出是符合 `BIL_STANDARD.md` 的
BIL 模块（内存对象模型 + 文本序列化）。

`BIL_STANDARD.md` §3.3「frontend 不变量」是中端的**需求清单**：名称解析、
访问控制、类型推断、重载解析、默认参数填充、具名参数重排、泛型约束检查、
smart cast、rich/shared 闭包检查、async 共享安全、extension 注册、
wrapper 形状校验与应用标记、语法糖规范化——全部必须在中端完成。
wrapper **烘焙**（派发链合成、inner 链接、原始体替换、隐藏存储/router
体）归 Middleware（BIL §23 边界；见 §5.2），不在中端完成。

总体管线为四个 pass、两棵树、一个符号图：

```text
RootASTNode ×N（语法树，只读）
    ↓ P1 声明收集（DeclarationCollector）
    ↓ P2 声明解析（DeclarationResolver）
符号对象图（驻留的 SemanticSymbol 图，全局唯一）
    ↓ P3 函数体分析（Binder）
BoundTree（带类型语义树；BoundNode.Syntax → ASTNode）
    ↓ P4a 降级重写（Lowerer，树到树，可多个 rewriter）
LoweredTree（脱糖树；LoweredNode.Origin → BoundNode）
    ↓ P4b 发射（BilEmitter，线性化）
BilModule（BIL 内存对象模型；指令.Origin → LoweredNode?，可空）
    ↓ BilWriter
BIL 文本
```

核心决策（修改须重新过一遍取舍）：

1. **P3 与 P4 严格分离**。P3 产出完整的带类型语义结果，P4 只做机械翻译。
   每一步可独立验收，防止分析与发射互相渗透形成不可测试的整体。
2. **Roslyn 风格独立 Bound Tree，且 P3/P4 各一棵**。AST 保持只读
   （AST 无 annotations 挂点，这是刻意设计，不走回头路）；
   语义信息全部活在 BoundTree/LoweredTree 与符号图上。
3. **P4a 与 P4b 分离（LoweredTree 存在的理由）**：emit 之前要做的
   lowering 相当深——async/await 直接物化为 BIL §17 指令，由
   Middleware 降为状态机（见 §7），using 物化为清理记录与 try/finally 路径等；同时避免
   P4 内部实现与 BIL 对象模型（及独立的 BIL verifier/VM）互相捆绑，
   保持关注点分离。
4. **符号是驻留对象图**，引用相等即身份相等；canonical symbol 字符串
   只是序列化投影（§4.4）。
5. **诊断可恢复且可累积**，覆盖 P1–P4 全部阶段（§8）。

---

### 1.1 有界并行与发布屏障

`CompilerJobs` 每阶段只持一个共享资源 lease，完成全部 indexed job 后再发布结果。
`RIGI_JOBS` 限制共享预算的 CPU 槽数；嵌套 bootstrap、默认值和 lambda 使用
当前施工上下文的串行快路，不重复申请同一预算。小文件集合采用串行快路。
各 job 的诊断和日志使用 AsyncLocal sink，按输入序归并，保留 phase、severity、
sourceName、起止坐标和消息的原序；异常在 join 后按输入序重抛原异常。
取消等待释放 lease，取消的阶段不返回部分结果。

前端固定 immutable 文件输入，每个 job 独占 Lexer、Parser 和 ASTValidator。
P1 按文件并行收集声明事实和签名键，按文件序/声明先序集中驻留 namespace、
创建符号和检查重复。P2 的继承、接口与泛型约束等依赖 visitor 保持串行，
字段类型签名 join 后才能处理 init 映射与方法签名；override、extension、
wrapper 等检查仍在签名屏障之后执行，构造模板回填不与 P2 模板写入交错。

P3 先串行绑定参数默认值、固定 enum 参数、预置调用/构造器/like 转发体、
companion、wrapper cell 和早期 Serialization 合成。body job 具有独立
BindContext 和 BindEnvironment delta；nested lambda 与 companion 壳和原体
留在所属 job。声明 wrapper 参数冻结，需重试时写 job overlay。
含 `placeOf`（包括 nested lambda）的 body 按固定 job 序提前串行执行，
精确提升实际使用的 global cell；结果仍放回原 ordinal 槽。
body、诊断、generic use 和合成 delta 全部按槽序归并；全局初始化仍串行按
源码声明序绑定，最后保持 lambda/cell 分组附加与晚期检查顺序。
延迟默认构造诊断只保存不可变消息载荷，不捕获 worker 环境。

P4a 各函数独占 LowerContext、闭包存储计划和结构化退出路由，完成后按
body 序发布。P4b 模块 metadata、声明与枚举判别值先串行发射，函数独占
EmitEnvironment、资源池和块/临时变量表并行发射。join 后按函数 ordinal
和局部跨种类首次出现序回放 scalar/null/switch 的旧 intern 键，重建全局
`R_N` 与 load/hint/switch/try 的资源引用，保留 Origin 和所有 handler/block
对象。catch 表仅在所属函数内去重。下游 Middleware 的 CoroutineSplit
及 LLVM Context/O2 所有权保持各自的串行边界。

隐藏名称来自稳定 module identity、文件序/sourceName、声明路径、body 宿主
角色和 job 内创建 ordinal；global cell 只依字段声明身份，不能依竞争赢家。
独立模块可显式设置 module identity，默认值稳定且不从机器工作目录推导。
`Modules/ModuleBuildService` 按稳定依赖 DAG 准备产物，每个消费者用
`SymbolGraph.CreateArtifactOnly` 创建固定内建壳并导入已校验接口，再对自身
源码执行 P1–P4。内建 resolver 授权的标准库冷构建使用内嵌源；命中时只读
内容身份并消费接口/BIL，不调用 ParseIntrinsics 或 BindSourceSignatures。
单模块缓存锁之前依赖已准备完毕；builder 不递归构建依赖或取得 LLVM lease。
接口与 BIL 在一个原子 envelope 中发布，校验时在临时图导入依赖与候选，
事务性链接全部实现后运行 BIL verifier。最终应用的晚期 override 仅从可信
标准库审批的完整 canonical/ABI 集合恢复，缓存模型及其字节不被回写。
构造类型仍按 definition/arguments 的引用身份驻留；Get、Substitute、递归
成员回填、快照和 generic-use 表由同一个可重入 Monitor 保护。同线程递归
可取得施工 placeholder，其它线程只能取得完成对象；最外施工失败撤销本次
全部递归新增键。快照按 definition 稳定身份及有序实参身份排列，泛型参数
身份包括 owner/index，bootstrap 类型与方法使用固定身份。

---

## 2. Pass 职责分配

`BIL_STANDARD.md` §3.3 清单逐项归属如下。原则：**声明间的事实归 P1/P2，
函数体内的事实归 P3，形态变换归 P4**。

| 职责 | 归属 | 说明 |
|---|---|---|
| 全局符号表建立 | P1 | 只扫声明骨架，不进函数体 |
| 重复声明检查 | P1 | 同名类型/成员冲突 |
| 类型引用解析（TypeReference → TypeSymbol） | P2 | 含泛型实参递归解析 |
| 继承图 / implements 图 / 循环继承检查 | P2 | |
| 修饰符合法性（rich 仅 struct、shared struct 必 rich 等） | P2 | 对照 SYNTAX §3.1.1；含「非 rich struct 不得 open/abstract」「singleton 必 shared」；wrapper 默认非 rich，允许显式 rich，禁止 open/abstract/singleton |
| rich / shared 字段闭包检查 | P2 | SYNTAX §3.1.1 闭包表（含 wrapper 两行），递归应用 |
| rich / shared 单向传染检查 | P2 | 基类 rich/shared ⇒ 子类必须同标；反向靠继承字段闭包重校验兜底 |
| 全局/静态字段的共享安全闸门 | P2 | SYNTAX §3.1.1 闸门 1：全局变量/常量、静态字段及其访问器类型 |
| wrapper 目标矩阵检查 | P2 | SYNTAX §14.9：宿主可内嵌性 + shared 目标矩阵 A–D + interface 实现者传染 |
| 泛型约束检查（声明侧） | P2 | 约束自身良构 |
| 泛型型变声明位置检查 | P2 | `VarianceChecker`：类型泛型参数的读/写极性、嵌套 invariant 容器、getter/setter 与基类/interface 位置 |
| wrapper 适用性与应用登记 | P2 | `@WrapperTarget` 类别 × 目标声明；形状校验 + AppliedWrappers/WrapperApplication 登记（Freeze 前零合成符号；烘焙归 Middleware，见 §5.2） |
| wrapper 继承闭包检查 | P2 | 间接基类/interface、override 方法与 accessor 必须显式重复 wrapper 定义/实参/顺序 |
| extension 目标注册 | P2 | `ext` 成员挂到目标类型符号 |
| canonical symbol 定形 | P2 | 符号图建成即可打印（§4.4） |
| 名称解析（表达式内） | P3 | 作用域链：块 → 参数 → 成员 → 全局 → import |
| 类型推断（`var` / 字面量 / 表达式类型） | P3 | |
| 重载解析（source-level overload ranking） | P3 | 唯一一处做 ranking 的地方（BIL §3.3） |
| 运算 / getter / setter / 索引 / 构造的精确签名规范化 | P3 | |
| 默认参数填充、具名参数重排 | P3 | BoundCall 已是规范参数序 |
| 泛型约束检查（使用侧实参） | P3 | |
| 构造泛型类型型变赋值 | P3 | `SymbolLookup.IsAssignable` 按 `out`/`in` 递归比较实参，invariant 保持严格相等 |
| smart cast 分析 | P3 | 结果记录在 BoundTree，显式 cast 由 P4 物化 |
| 访问控制检查（使用点） | P3 | |
| definite assignment / 所有路径显式返回 | P3 | BIL §21.4 要求 frontend 保证 |
| async 边界共享安全检查 | P3 | SYNTAX §4.5 五项闸门：receiver / 参数 / TResult / 捕获 / 泛型实参 |
| 值块隐式取值 | P3 | seq/if/switch 值块：「块内恰好一条非赋值 ExpressionStatement」即隐式值 |
| 语法糖规范化（全部脱糖） | P4a | 清单见 §6.1 |
| 短路展开、smart cast / 子类型赋值的显式 `cast` 插入 | P4a | BIL §3.1/§6.5/§11.3 |
| async/await/yield 物化、using 物化 | P4a | 深度 lowering，见 §7 |
| 隐藏参数物化（`.generic.T` / `.vargs` / `.kwargs`） | P4a/P4b | 规范签名见 BIL §7 |
| 表达式线性化、临时变量物化、`.vars` 收集 | P4b | |
| Resources 提取（字面量 → `res(...)`） | P4b | BIL §4.2：指令不得内联字面量 |
| block 结构生成（if/loop/switch/try） | P4b | BIL §16 结构化控制流 |

P1 与 P2 分开的原因：Rigi 声明可以互相前向引用，必须先收齐全部名字
再解析类型引用。P2 结束后符号图**冻结声明侧契约**。P3/P4 不重新解析或改写源声明；
构造泛型驻留是受锁保护的透明派生缓存。P3 仍可生成局部、闭包、cell、
默认构造器、序列化辅助体等编译器合成产物：串行准备与 body job 的
`BindEnvironment` delta 按发布屏障归并，不能把“冻结”理解为禁止这些合成。

每个 pass 的失败策略：诊断累积、尽量继续（§8）；但存在 Error 级诊断时
**不进入 P4 的发射性工作**——CLI 将 P1–P3 的诊断统一输出后检查
`HasErrors`，P4 的输入必须是无错 BoundTree；不能声称每个阶段报错即
立即终止后续 P2/P3 的诊断收集。

---

## 3. 编译单元模型

- 一次低级 `compile` 调用处理一个**编译单元**：多个 `.rg` 源文件、
  bootstrap 身份与内嵌标准库声明。模块构建通过已准备的接口/BIL 导入依赖，
  编译单元仅分析本模块源码（见 §1.1）。P4b 提供 merged `BilModule`，
  `--emit-bil` 按命名空间输出多个可合并切片，不等于一个物理 BIL 文件。
- P1/P2 面向整个编译单元一次性执行（跨文件前向引用因此天然成立）；
  P3 以**函数体**（含字段/全局变量初始化器、enum case 判别值等表达式体）
  为独立分析单位，函数间诊断互不阻断。
- 编译单元内声明的符号进 BIL `LocalSymbols`；被引用但属于外部模块或预定义契约的符号进 `ExternalSymbols`（BIL §4.3/§4.4/§8.6）。

---

## 4. 符号对象图（P1/P2 产物）

### 4.1 符号家族

语义符号基类为 `SemanticSymbol`，派生（按需增补，遵守简洁三问）：

```text
SemanticSymbol
├── NamespaceSymbol
├── TypeSymbol            // class/struct/enum-struct/interface/wrapper + 内建
├── GenericParameterSymbol
├── FieldSymbol           // 含全局变量/常量；backing/computed/ext 以属性区分
├── MethodSymbol          // 含 init、operator、getter/setter、全局函数、ext
├── EnumCaseSymbol
├── ParameterSymbol
└── LocalSymbol           // 函数体局部变量（P3 产生，挂在函数分析结果上）
```

**命名注意**：语法侧已有 `Symbol` / `SymbolElement` / `SymbolASTNode`
（`AST/SymbolNodes.cs`，表示源码路径），语义符号一律用 `SemanticSymbol`
家族名称，禁止混用。语义期对语法 `Symbol` 的原地规范化使用其
`DeepClone()`（该方法即为此预留）。

### 4.2 驻留（interning）

- 每个声明实体在整个编译单元中**恰有一个**符号实例；引用相等即身份相等。
  比较符号一律 `ReferenceEquals` / `==`，禁止按名字字符串比较身份。
- 构造泛型类型（如 `List\<i32>`）同样驻留：同一 `(泛型定义, 实参列表)`
  必得同一实例（经编译单元级 cache）。`T?` 即构造类型 `Nullable\<T>`，
  不设独立的 nullable 表示（SYNTAX §3.4）。
- 源声明允许构造期两阶段（P1 建壳、P2 填内容），P2 结束后声明侧稳定；
  透明构造类型缓存与 P3 合成产物遵守 §1.1 的所有权及发布屏障。

### 4.3 bootstrap 与标准库源码（混合策略）

内建身份与源码声明采用**固定 compiler-owned 身份 + 内嵌标准库源码**
的混合。普通路径先解析 `stdlib/.intrinsics.rg`，`BootstrapSymbols` 按
声明构造内建类型与泛型参数，再绑定源签名；artifact-only 图改用
`CreateManifestTypes` 建固定内建壳并消费可信接口，禁止重新解析 intrinsics。

- **bootstrap 内建身份**：`Any`、`Object`、`ValueType`、`Enum`、`Wrapper`、
  SYNTAX §3.2 全部基本类型（`i8`–`u64`、`float`/`double`、`bool`、`char`、
  `String`、`Type\<T>`、`Span\<T>`）、`SharedSpan\<T>`、`Nullable\<T>`、
  `Box\<T>`、`Array\<T>`、`Map\<TKey,TValue>`，以及
  编译器特权关系（SYNTAX §3.1.2：`Box\<T> <: Object` 为内建事实、
  Span 的特权 lowering 标记等）。这些由 `BootstrapSymbols` 在符号图
  初始化时建立；普通路径的种类、修饰符和泛型形状取自 `.intrinsics.rg`，
  固定层级、BIL alias 与 intrinsic 操作键仍由编译器绑定。注意三条容易搞错的层级事实（2026-07-29 规范修订）：
  `String` 与 `Wrapper` 都在 `ValueType` 分支下（`String` 非 rich、
  `Wrapper` 默认非 rich）；`Nullable\<T>` 的 shared 属性由 `T` 推导而不是
  查声明修饰符；`Wrapper` 是全部 wrapper 声明的隐式基类。
- **标准库源码**：其余标准库表层（`core.io::Console`、`core.coroutine::Task`
  / `Executor` / Alarm 家族、`core::IDisposable`、异常类型、
  `core.ComparisonResult` 等）以 `stdlib/**/*.rg` 内嵌资源形式随编译器载入，
  用自己的前端解析后走同一条 P1/P2 路径。这同时构成前端的常驻回归测试。
- 划分原则：**类型系统与编译器本身依赖的进 bootstrap；只有语义分析的
  "用户"才依赖的进标准库源码**。基元类型上的运算符集合属于 bootstrap
  的一部分（BIL §11 的 intrinsic 键空间）。

### 4.4 canonical symbol 是投影，不是身份

`BIL_STANDARD.md` §5.2 的 canonical symbol 字符串格式是符号图的
**序列化投影**：实现为符号图上的打印函数（`CanonicalSymbolPrinter`），
供 BIL 发射、诊断消息与派发链诊断工具（RUNTIME §15）共用。源语义实体内部以驻留引用为身份，
不得用名字字符串代替实体比较；接口导入/链接等序列化边界允许以 canonical
键定位 `ImportedSymbols` 等映射，解析后仍回到同一驻留对象。BIL 发射
之后的工具世界以 canonical 字符串为身份。

---
