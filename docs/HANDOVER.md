# 交接文档（临时）——局部访问器实现路线决策

> **性质**：临时交接文档，非项目长期文档。先例：M72 `docs/HANDOVER.md`
> 于 M73 完成后按约定删除。
> **给接手 agent 的提醒**：局部访问器落地完成（或路线再次变更）后，
> **请删除本文件**，并在 PROGRESS_REPORT 记录落地结果。

**日期**：2026-08-06（M80 提交 `f481317` 之后，工作树干净，43 套件
2978/2978 + fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告）

---

## 1. 决策内容（用户裁决，2026-08-06）

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

## 2. 背景速览

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

## 3. 施工指引（路线 C）

- **时机**：不在 S11 序列内施工（S11 剩余 = proxy 烘焙 lowering →
  派发链诊断工具）。局部访问器随 S13（async lowering 专项 / lambda
  闭包基建）落地，或作为 S13 的一部分；S13 设计文档应把局部访问器
  列为闭包机制的**首批消费者**之一（另一个是 lambda 本体）。
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

## 4. 其他未决事项（新对话继续时需用户裁决）

- **技术债 #26（ext 规范未明三事，M80 登记）**：① ext static 合法性
  是否 §4.4 明文补例（实现与 M80 样例已默认合法）；② priv/protected
  ext 的可见性语义（维持成员语义，还是对声明文件/ext 体放开目标私有
  成员访问）；③ ext 目标为泛型定义（禁止 / 隐式泛型参数 / 至少报元数
  与歧义诊断）。
- S11 剩余按序推进项（不受本决策影响）：**proxy 烘焙 lowering**
  （specific → wildcard → `call???` 降级 + `.wrapper.` 隐藏字段声明与
  get/set.field.embedded 发射）→ **派发链诊断工具**（RUNTIME §15）。

---

**再次提醒**：局部访问器落地完成后请删除本文件（并同步 PROGRESS_REPORT
技术债 #22① 勾销与里程碑记录）。
