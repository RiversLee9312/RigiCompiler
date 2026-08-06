# 交接文档（临时）——局部访问器路线 + S11 施工状态

> **性质**：临时交接文档，非项目长期文档。先例：M72 `docs/HANDOVER.md`
> 于 M73 完成后按约定删除。
> **给接手 agent 的提醒**：局部访问器落地完成（或路线再次变更）且
> S11b 落地后，**请删除本文件**，并在 PROGRESS_REPORT 记录落地结果。

**日期**：2026-08-06（M82 完成后，43 套件 3026/3026 + fuzz 6000 +
语义 fuzz 3000 全绿，build 0 错误 0 警告）

---

## 1. 当前进度快照与下一步（S11b 施工指引）

- **已完成**：M81（S11 proxy 烘焙细化为 S11a–S11g + 烘焙形态三项
  定稿 + 技术债 #26 ext 三事裁决，纯文档）→ M82（**S11a 落地**：
  WrapperApplication 记录升级（TTarget 代入显形）+ ProxyShapeChecker
  （形状校验）+ ProxyDispatchResolver（`.wrapper.` 隐藏字段 + Entity
  派发链 + 特化/原始体符号合成）+ 发射闸门）。
- **下一步 = S11b（P3 proxy 体逐组合绑定）**，ROADMAP S11 段定义：
  ① `self` 绑定 = 宿主角色的 this（BoundThis(宿主)，类型 = 应用记录
  Wrapper 构造代入结果）；② `inner` 绑定 = 对下一环符号的普通调用
  （链环 `TargetMember.WrapperChain[i+1]`，链末 = `OriginalBody`）；
  ③ proxy 体内 `this` 重写为 `BoundWrapperAccessExpression`（与使用点
  `obj:W` 同构）；④ 转发壳（被修饰成员原名 fn 的 body = invoke 链首）
  与 wildcard 解包 shim 的 BoundFunctionBody 合成；⑤ proxy 体诊断按
  (proxy, span, message) 去重。

### S11b 关键落点指引（M82 代码现状）

- **绑定驱动**：BindingDriver 走 AST 骨架；特化/原始体符号**无 AST
  条目**——需要新驱动路径：对被拦截成员（`MethodSymbol.WrapperChain
  != null`），把用户体绑定重定向到 `WrappedBodySymbol`，再逐链环绑定
  proxy 声明体（`ProxySpecialization.ProxyDeclaration` 的 AST 体）
  到特化符号。转发壳/解包 shim 直接构造 bound 节点（仿 M77 init
  映射赋值合成先例，Syntax 回指声明节点）。
- **proxy 声明体当前仍按普通 operator 体绑定**（BindingDriver 走
  AST 会绑它们，`inner`/`self` 报 Undefined function/name）——S11b
  需跳过 proxy 声明体的常规定位（改为逐组合绑定）。
- **wildcard 特化签名 = 成员签名**（M81 定稿）：proxy 体的
  symbol/namedArgs/unnamedArgs 三形参由 shim 前奏物化（symbol =
  canonical 字符串常量、参数包自 fn 形参打包）；`inner` 转发即把包
  解包调下一环（逐元素 cast，§14.7 同款 CastException 语义）。
- **发射闸门位置**（S11c/S11d 开闸点）：`LocalSymbolEmitters` 类型
  成员双循环跳过 `Name.StartsWith('.')`（合成 fn + proxy 声明）与
  `field.IsCompilerGenerated`（隐藏字段）；`EmittingDriver` 跳过
  `body.Method.Name.StartsWith('.')`。
- **规范锚点**：SYNTAX §14.2（形状）/§14.6（派发序）；RUNTIME §14
  （静态组合、M81 补特化 fn 独立发射句）；BIL §8.4
  `wrapper-proxy(PROXY_KIND)` 四值（specific/wildcard/router/original）
  + §5.1 合成保留名；ARCH §6.1 注记（pass 归属）。
- **技术债 #27（S11a 暂缓面）**：Value/Method wrapper 链、无访问器
  字段拦截、interface 实现者链继承/override 链、async 交互、全局/
  静态存储。

---

## 2. 局部访问器实现路线决策（用户裁决，2026-08-06）

**局部访问器（栈上 `var`/`const` 的 getter/setter，SYNTAX §9.4 第四类
位置）的实现路线定为 C：不做内联展开，随 / 借 S13 lambda 闭包机制落地
（闭包抬升）。** 捕获语义随之开放——访问器体可以引用外层局部变量/参数
（闭包机制天然支持，与 lambda 捕获同一语义）。

被否决的备选（M80 讨论记录，若未来路线变更可回捞）：

- **A. P3 使用点内联展开**：每处读/写重绑访问器体 AST（fresh 节点、
  作用域直通、无需闭包基建；需处理 return@ 改写、DA 贯通、体错误诊断
  多处重复的去重）。
- **B. 先只落地自动访问器**（无体 `get`/`set`，backing 直通零内联），
  带体访问器随后续。

### 背景速览

- **局部访问器是什么**：SYNTAX §9.4 规定 getter/setter 可定义于四类
  位置——类/struct 字段、全局变量、栈上 `var`/`const`。前三类已落地
  （M63 S8e），栈上位置归口：P3 遇带访问器的局部声明报
  `P3: local variable accessors are not supported yet (S11)` 后按普通
  局部降级绑定（`Semantic/Binding/Visitors/DeclarationVisitors.cs:27-31`；
  规范侧 §9.4.1 末条「栈上局部变量/常量的访问器暂未实现」；
  PROGRESS_REPORT 技术债 #22①）。
- **为什么栈上位置特殊**：字段/全局访问器靠 BIL `get.field`/`set.field`
  的运行时派发（§13.3），编译器零内联；BIL 的局部是裸栈槽（`$name`、
  `set.var`），指令集无「经访问器读局部」的钩子，必须由编译器消化——
  内联展开（路线 A）或闭包抬升（路线 C）。
- **与 lambda 的关系**：访问器体本质是一对匿名函数
  （getter ≈ `func(): T`，setter ≈ `func(value: T): void`）；允许捕获
  外层局部即引入闭包语义，与 lambda（§5，P3/P4 归口 S13）同一套问题。

### 施工指引（路线 C）

- **时机**：不在 S11 序列内施工。局部访问器随 S13（async lowering
  专项 / lambda 闭包基建）落地，或作为 S13 的一部分；S13 设计文档应
  把局部访问器列为闭包机制的**首批消费者**之一（另一个是 lambda
  本体）。
- **语义要点**（落地时兑现 §9.4.1 全套）：
  - backing 形态（`value: _`）：隐藏 backing 存储；getter 体内 value
    只读、setter 体内可读写，setter 进入隐含 `backing = value`；
  - computed 形态（`_: _`）无 backing；自动访问器（无体）合成
    `return value` / 空体；const 不得声明 setter；
  - 外部读写一律经访问器；读取结果不参与 smart cast 收窄（§3.5）；
  - 读写点的转调形态取决于 S13 闭包在 BIL 的表示（§17 修订联动）。
- **落地时解开的三处归口**：
  1. `Semantic/Binding/Visitors/DeclarationVisitors.cs:27-31` 的
     S11 归口诊断；
  2. SYNTAX §9.4.1 末条「暂未实现」句；
  3. PROGRESS_REPORT 技术债 #22①。

---

## 3. 其他未决事项（新对话继续时需用户裁决）

- ~~**技术债 #26（ext 规范未明三事，M80 登记）**~~（**M81 已裁决**，
  规范文本落地：① ext static 合法，SYNTAX §4.4 明文补例；②
  priv/protected ext 可见性按声明位置判定，ext 体不放开目标私有
  成员；③ ext 泛型目标禁止静默接受——元数 + 歧义诊断，隐式泛型
  参数留候补。代码落地归 ROADMAP S11g）。
- S11 剩余按序推进项（不受本决策影响）：**proxy 烘焙 lowering**
  （M81 已细化为 S11a–S11g：P2 形状校验与符号合成（✅ M82）→
  P3 proxy 体逐组合绑定 → P4a/P4b place 成员访问 → P4b 合成 fn
  发射 → `call???` 降级 → **派发链诊断工具** → 复核收尾，见
  `compiler/semantic/SEMANTIC_ROADMAP.md` S11 段）。
