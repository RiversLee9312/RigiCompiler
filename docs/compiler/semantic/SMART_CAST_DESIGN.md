# Smart Cast 专项设计定稿（S8b 施工底稿）

> **定位**：S8b（SEMANTIC_ROADMAP）动工前的专项定稿记录，2026-08-03 经用户逐条拍板。
> 本文件是**施工底稿与设计推理记录**；正式语言规则在 S8b 落地时写入 `docs/SYNTAX.md` §3.5（本文件 §8 的文本草案即写入蓝本），§3.4 补 null 判等。
> **跨会话交接说明**：本文件自包含，读者无需任何对话上下文。相关架构背景见 `VISITOR_REWRITE.md`。

## 1. 用户决策记录（四问）

| 问题 | 决策 | 含义 |
|---|---|---|
| Q1 收窄目标范围 | **B** | v1 即含字段收窄（不限于局部变量/参数） |
| Q2 guard 模式 | A | `if (x == null) { return }` 之后 x 收窄为 T（终止分支反向传播） |
| Q3 条件组合 | A | `and`/`or`/`not` 全支持（含 and/or 右侧绑定期间的上下文事实） |
| Q4 switch pattern | A | `(_ is T)` 分支体内 `_` 收窄为 T |

## 2. 事实模型（全部规则的核心）

条件表达式经**事实提取**得到一对事实集：`(真边事实, 假边事实)`。
**事实** = 收窄键 → 收窄类型 的映射条目（一个条件可产生多条）。

- **收窄键**（可被收窄的表达式身份）：
  - `LocalSymbol`（局部变量）、`ParameterSymbol`（参数）——符号引用相等即键相等
  - **字段链**：`(稳定 receiver 链, FieldSymbol)`——见 §4 安全规则
- **事实集运算**（沿控制流边传播）：
  - 进入分支：分支入口 = 当前事实 ∪ 该边事实（同键冲突：边事实覆盖当前事实）
  - 分支合并（if 后、非终止双分支）：`before ∪ (真尾 ∩ 假尾)`——∩ 按键与收窄类型**都相同**才保留
  - 无 else 的 if 之后：恢复 before（保守）

## 3. 收窄触发（条件形态 → 事实）

| 条件形态 | 真边事实 | 假边事实 |
|---|---|---|
| `x is T`（静态类型目标） | x→T（**蕴含非空**：x: String? 收窄为 String） | ∅（无差类型，表达不出「非 T」） |
| `x != null`（x: T?） | x→T | ∅ |
| `x == null`（x: T?） | ∅ | x→T |
| `A and B` | 左真 ∪ 右真 | 左假 ∩ 右假 |
| `A or B` | 左真 ∩ 右真 | 左假 ∪ 右假 |
| `not A` | A 的假边 | A 的真边 |

- **and/or 右侧绑定期间**的上下文事实（短路语义：`and` 右侧只在左真时求值、`or` 右侧只在左假时求值）：
  - `(x is String) and (x.length > 0)`——右侧绑定时 x 已收窄为 String（成员解析在其上进行）
  - `(x == null) or (x.length == 0)`——右侧绑定时 x 已收窄为非空
- 条件中 x 必须是**可收窄目标**（§4）；否则条件本身合法，但不产生事实。
- **不触发收窄**：`supers`、`with`、动态目标 `x is t`（`Type\<T>` 值）、`is .Case`（enum 判别，已定稿）、`x is T` 假边。
- **冗余检查**：x 静态类型已是 T（或其子类型）时不收窄不包装、不警告（与 §3.5「不做静态不可能性拒绝」口径一致）。

## 4. 收窄目标安全规则（Q1=B 的核心论证）

### 4.1 局部变量与参数

- `const` 局部：收窄永不失效（不可重新赋值）
- `var` 局部与参数：**重新赋值即失效**（含复合赋值 `+=` 等）——从赋值点起清除该键事实

### 4.2 字段（v1 即做，三条缺一不可）

1. **仅 `const` 字段**可收窄。var 字段不收窄——别名赋值不可控（任何方法调用都可能经别名修改该字段），与 Kotlin 对 var 属性不做 smart cast 一致。const 字段引用不变 ⇒ 其引用的对象运行类型不变 ⇒ 收窄永久有效，无需失效分析。
2. **仅 backing field 直访**。带 getter/setter 的属性（SYNTAX §9.4）不收窄——访问器可包含任意逻辑。
3. **receiver 稳定链**：`r.f` 可收窄 ⇔ `f` 满足 1+2 且 `r` 是稳定表达式——`this` / const 局部 / 参数 / （递归）稳定链上的 const 字段。链上任一 var 环节被赋值 ⇒ 该链所有收窄失效。

### 4.3 排除与前置

- **init 构造方法体内的 `this` 字段**：v1 保守排除（const 字段在构造期可能尚未初始化；归后续里程碑细化）
- **前置小项**：`FieldSymbol` 需 `IsConst` 标志（P1 收集修饰符时写入；顺带兑现 Binder.cs 现存「字段 const 判定」技术债两处——赋值检查也受益）

## 5. 控制流构造的分支语义

- **if 语句/表达式**：then 入口 = 当前 ∪ 真边；else 入口 = 当前 ∪ 假边；合并按 §2。
- **guard 模式（Q2）**：一分支经 `GuaranteesReturn` 判定**终止**（return/throw/全终止嵌套，现有基建），另一分支的事实继续流向后续语句：
  - `if (x == null) { return }` 之后：x→T（假边）
  - `if (not (x is String)) { return }` 之后：x→String（`not` 翻转后的假边 = is 真边）
- **while (C)**：体入口 = 真边 ∪ before 中「循环体内不被赋值」的键（保守）；循环**之后** = before（出口不收窄——条件为假蕴含的事实均无静态表达，见 §3 假边列）。
- **do-while**：体入口 = before（v1 简化）；之后 = before。
- **for**：循环变量 const（现有规则）；条件同 while 规则（for 范围形态的判定条件由脱糖承载，源级无用户条件表达式）。
- **switch（Q4）**：`(_ is T)` pattern 分支体入口，`_`（selector 占位）收窄为 T；selector 本身同时是可收窄目标时，selector 键同收窄。多 pattern 组合（`(_ is T) and (...)`）按 §3 组合规则。
- **try/catch**：v1 不做特殊处理——try 体内产生的收窄不流出 try（catch 可能拦截任何中途点，保守）；catch/finally 体内收窄按普通块处理。

## 6. null 判等（前置补齐项）

现状：`x == null`/`x != null` **无法绑定**（BindBinary 要求两侧同类型 + null 字面量需 expectedType 上下文），SYNTAX 无规范文字。定稿：

- **语法规则**：`==`/`!=` 一侧为 null 字面量、另一侧类型为 T0 时合法，结果 bool。null 定型为 `Nullable\<T0\>`；T0 本身可空时即其自身。
- **不可能性放行**：T0 非可空时（如 `i32 == null`）不报错不警告，运行期恒 false/true（与 `is`/`supers`「不做静态不可能性拒绝」口径一致）。
- **BIL 形态**：`cmp.eq`/`cmp.ne` + `null type(T)` 资源（§18.1：null 资源类型即 `.nullable<T>`，满足 §11.5 严格相同）。非空侧需装箱为 `Nullable\<T0\>`（§12.1 装箱视图，P4a 物化 EnsureDeclaredType 模式）——与 M52 `?.` 物化的 null 检查完全同形态。
- `null == null`：两侧皆字面量无锚定类型——编译错误（沿用「null requires a nullable type context」诊断）。

## 7. 与 `?.` / `if?` 的统一性（核查结论）

- `a?.b` **不产生**收窄：a 不因此变为非空（`?.` 判空只保护那一次成员访问）。
- `a?.b != null` 为真时**不反推** a 非空（v1 不利用此蕴含，与 Kotlin 一致）。
- `x if? y` 是值级回退表达式，无分支区域，不产生收窄。
- 两者与 smart cast **正交互补**：`x?.length if? 0` 等写法不受影响；需要收窄的场景用 `if (x != null)` 或 guard。
- 实现互不为对方特例：`?.`/`if?` 的 P4a 脱糖（M52 已落地）内部生成的 null 检查是 Lowered 层结构，不经过 P3 收窄机制。

## 8. SYNTAX §3.5 正式文本草案（落地时替换现「支持智能转换」一句）

> **智能转换（smart cast）**：`is` 检查或 null 判等为真的控制流分支中，编译器自动将被检查值视为收窄后的类型，无需显式 `as`。
>
> - **触发**：`x is T` 为真时 x 收窄为 T（含非空蕴含）；`x != null` 为真 / `x == null` 为假时 x 从 `T?` 收窄为 T。`and`/`or`/`not` 按短路语义组合（`and` 右侧以左真为上下文、`or` 右侧以左假为上下文）。条件为假且所在分支终止（return/throw 等）时，收窄对后续语句生效（guard 模式）。
> - **目标**：局部变量与参数（var 重新赋值后收窄失效）；const 字段（无自定义访问器、receiver 为 this/const 局部/参数/const 字段稳定链；构造方法 init 体内除外）。var 字段、带访问器的属性、任意方法调用结果均不可收窄。
> - **不触发**：`supers`、`with`、动态目标 `x is t`（`Type\<T>` 值）、`is .Case`；`is` 检查为假的分支（无「非 T」类型）。
> - **失效**：var 局部/参数被重新赋值（含复合赋值）；字段稳定链上任一 var 环节被赋值。
> - **switch**：`(_ is T)` 分支体内 `_` 收窄为 T。
> - **循环**：`while` 条件为真的收窄在循环体内有效（体内赋值照常失效）；循环结束后收窄不保留。
> - **边界**：收窄不跨 `await`/`yield` 挂起点；被 lambda 捕获且 lambda 内可能赋值的 var 收窄失效（两规则的实现归 S13）。

## 9. 实现映射（新 visitor 架构，见 VISITOR_REWRITE.md）

- **P3 标记**：
  - `FlowState`（BindContext 内）增收窄事实表：`Dictionary<键, TypeSymbol>`，与 DA assigned 集**同生命周期**分叉/合并/恢复（DA 基建已有，收窄表是它的平行维度）
  - **ConditionFactsVisitor**：条件事实提取器（独立 visitor，`TContext = IFlowContext`，返回 `(真边, 假边)` 对）——§3 表格的代码化
  - 引用绑定点（局部/参数引用、字段引用、switch 占位）查事实表，命中则包 **`BoundSmartCastExpression { Operand, NarrowedType }`**（`Type = NarrowedType`，成员解析自然按收窄类型）
  - guard：if 分支绑定后按 GuaranteesReturn 判定取对边事实续流
- **P4a 物化**：`BoundSmartCastExpression → LoweredCastExpression(origin, lower(Operand), NarrowedType, isSafe: false)`——复用 M51 EnsureDeclaredType / M52 unwrap 模式；T?→T 的 unwrap 即 §12.1 cast（事实保证非空，运行期检查不会触发）
- **P4b**：复用 S7e `cast` 发射，**零新增**
- **BoundDescribe**：支持 BoundSmartCastExpression（描述形如 `smartcast<x: String? -> String>`）

## 10. 测试清单（定稿规则逐条 ≥1 用例）

Binder（BoundTree 标记断言，经 BoundDescribe）：
1. `if (x is T)` then 分支内 x 引用包 SmartCast；分支外不包
2. else 分支无收窄；无 else 的 if 之后无收窄
3. `x: T?` + `x is T` → 收窄为 T（非空蕴含）
4. `if (x != null)` then 收窄为 T；`if (x == null)` else 收窄为 T
5. guard：`if (x == null) { return }` 后续收窄；`if (not (x is T)) { return }` 后续收窄；throw 终止同效
6. and 真边合取 + and 右侧绑定上下文（`(x is String) and (x.length > 0)` 中 length 解析在 String 上）
7. or 假边合取（guard：`if ((x == null) or (y == null)) { return }` 后续两者均非空）+ or 右侧上下文
8. not 翻转
9. var 赋值失效（含复合赋值）；const 不失效
10. const 字段收窄（this.f / const 局部 .f / 参数 .f）；var 字段不收窄；带访问器属性不收窄
11. 稳定链中断：`constA.varB.constC` 链不收窄；链上 var 环节被赋值后失效
12. init 体内 this 字段不收窄
13. while 体内收窄、出循环失效；do-while 体不收窄
14. switch `(_ is T)` 分支体内 `_` 收窄；selector 为局部时 selector 同收窄
15. 动态目标 `x is t` / `supers` / `with` 不触发
16. 分支合并：两分支收窄到同一类型才保留（`if (c) { if (x is T) {} } else { if (x is T) {} }` 之后仍收窄）
17. null 判等绑定：`x == null`（T?）合法 bool；`i32 == null` 放行；`null == null` 报错
18. 冗余 is（x 已是 T）不包装不警告
19. 收窄后成员解析/调用按收窄类型（调用 T 特有方法）
20. ErrorType 毒化静默（条件绑定失败不次生报错）

Lowerer（LoweredDescribe）：SmartCast → LoweredCastExpression 物化（is T 场景 + T?→T unwrap 场景）。
BilEmitter：cast 指令发射复用验证（无需新指令）。CLI 端到端：guard + and 组合样例出合法 BIL。

## 11. 明确不做（边界记录）

- else 边差类型（「非 T」类型）——类型系统无此概念，永久不做
- `a?.b != null` 反推 a 非空——v1 不做（同 Kotlin）
- init 体内 this 字段收窄——归后续
- lambda 捕获/await/yield 挂起点的收窄失效**实现**——规范先行（§8 草案已含），代码归 S13
- 多参数索引处的收窄、泛型类型参数作为 is 目标的收窄——归 S9/S8c 之后评估

## 12. 落地偏差记录（M56，2026-08-03 实现后补记）

与定稿相比的实际落地差异（均不改变已定稿规则本身，属实现层澄清）：

1. **Q4 澄清**：分支体内 `_` **不可用**——`_` 仅在 pattern 匹配
   表达式内有意义（SYNTAX §7.2 既有语义，Binder 现状即「分支体无
   `_` 语义」）。故 Q4 的实际覆盖 = `(_ is T)` 分支体内 **selector
   本身收窄**（selector 为可收窄目标时；不可收窄 selector 如
   `switch (foo())` 的分支体内本就无法引用 `_`，无收窄需求）。
   switch 编排处 `ApplyCaseNarrowing`（剥 SmartCast 壳）实现。
2. **var 根允许**：NarrowKey 的根可以是 var 局部/参数（定稿只明说
   const 局部/参数/this）——var 根被赋值时由失效规则（ClearRoot）
   清除以其为根的全部链键，语义等价且覆盖面完整。
3. **静态字段键**：全局/static const 字段的收窄键 = FieldSymbol
   符号本身作根（不可变 ⇒ 永不失效），定稿未单列此形态。
4. **访问器判定归 S8e**：「带 getter/setter 的属性不收窄」的判定
   依赖字段-访问器关联信息（S8e getter/setter 绑定时落地）；当前
   字段符号无访问器信息，`ConstFieldRules.IsNarrowable` 只查
   const + init 排除，与「保守视为纯字段」口径一致（SYNTAX §9.4
   的属性在 P3 现状即无法被 `x is T` 后当字段直接读——成员查找
   尚未接入访问器，无收窄通道）。
5. **多段路径头与调用链头收窄**：实现中发现两处初版遗漏
   （`a.b()` 的 `a`、`x.m()` 的 `x` 未包装）——经套件用例捕获修复；
   收窄包装点最终覆盖：单段局部/参数、多段路径头、调用链头、
   字段引用（this.f/静态）、实例字段访问、`_` 占位（嵌套收窄）。
6. **DA 规则不动**：guard 仅作用于收窄表——definite assignment
   合并保持 M46 规则（before∪(setT∩setF)，guard 场景同样保守），
   保证 1924 既有用例零回归。
