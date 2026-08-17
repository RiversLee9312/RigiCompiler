# 结构化控制流 / 协程指令 / 提示指令（§16–§18）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 16. 结构化控制流

### 16.1 block 调用

```bil
call blk(BLOCK_ID) BREAK_ID_VAR
```

语义：

1. 绑定唯一 BREAK_ID 到 BREAK_ID_VAR（region-exit capability，见 §16.5）；
2. 执行目标 block；
3. 目标 block 正常到达末尾后返回；
4. 从 `call` 的下一条指令继续。

`call` 不创建函数调用栈帧，不涉及调用 ABI。目标 block 可以通过 `ret` 返回整个函数、通过 `throw` 传播异常、通过 `break BREAK_ID_VAR` 提前退出该 call region，或执行合法的结构化退出。

### 16.2 条件

```bil
if CONDITION blk(TRUE_BLOCK) blk(FALSE_BLOCK) BREAK_ID_VAR
```

规则：

- CONDITION 必须为 `.bool`；
- 进入 if 时绑定唯一 BREAK_ID 到 BREAK_ID_VAR（region-exit capability，见 §16.5）；
- 只执行一个分支 block；
- 分支 block 正常结束后，从 `if` 的下一条指令继续；
- `FALSE_BLOCK` 可以写 `none`，表示条件为 false 时无操作；
- 分支内 `break BREAK_ID_VAR` 提前退出该 if region，续 `if` 的下一条指令。

### 16.3 正向循环

```bil
loop CONDITION blk(BODY_BLOCK) blk(ENUMERATOR_BLOCK) blk(JUDGE_BLOCK) BREAK_ID_VAR
```

执行顺序：

1. 绑定唯一 BREAK_ID 到 BREAK_ID_VAR；
2. 执行 JUDGE_BLOCK；
3. 读取 CONDITION；
4. false：结束循环；
5. true：执行 BODY_BLOCK；
6. 若正常完成，执行 ENUMERATOR_BLOCK；
7. 回到步骤 2。

`ENUMERATOR_BLOCK` 可以为 `none`，适用于普通 while。

CONDITION 必须为 `.bool`。JUDGE_BLOCK 必须在每次读取前保证 CONDITION 已赋值。

### 16.4 反向循环

```bil
loop.rev CONDITION blk(BODY_BLOCK) blk(ENUMERATOR_BLOCK) blk(JUDGE_BLOCK) BREAK_ID_VAR
```

执行顺序：

1. 绑定唯一 BREAK_ID；
2. 执行 BODY_BLOCK；
3. 若正常完成，执行 ENUMERATOR_BLOCK；
4. 执行 JUDGE_BLOCK；
5. 读取 CONDITION；
6. true：回到步骤 2；
7. false：结束循环。

用于 do-while 及其他先执行 body 的循环。`ENUMERATOR_BLOCK` 可以为 `none`。

### 16.5 break 与 continue

```bil
break BREAK_ID_VAR
continue BREAK_ID_VAR
```

规则：

- `break` 可以引用 loop、loop.rev、switch、call、if 或 try 创建的 BREAK_ID——
  每条结构化 child-region 指令都在进入时把自己的 BREAK_ID 绑定为指向其
  region 帧的 region-exit capability；`break` 命中（matching，按 region 帧
  引用相等）时在目标 region 边界消费：弹出该 region 并续其下一条指令；
  未命中（nonmatching）的 abrupt completion 原样向外传播；
- `continue` 只能引用 loop/loop.rev 创建的 BREAK_ID（其余 region 的
  BREAK_ID 均不允许 continue）；
- token 必须在当前动态结构作用域内有效；
- BREAK_ID 不得跨函数、存入字段/数组、传给普通方法或从资源加载；
- `continue` 正向循环跳到 ENUMERATOR_BLOCK，然后 JUDGE_BLOCK；
- `continue` 反向循环跳到 ENUMERATOR_BLOCK，然后 JUDGE_BLOCK。

### 16.6 switch

```bil
switch SELECTOR res(TABLE_RESOURCE)
    [blk(ITEM_0), blk(ITEM_1), ...]
    blk(DEFAULT_BLOCK)
    BREAK_ID_VAR
```

TABLE_RESOURCE 是与 SELECTOR 类型一致的不可变常量数组，元素数必须等于 item block 数。

语义：

- 按表中顺序使用 `cmp.eq` 语义匹配；
- 第一个匹配项对应 ITEM block；
- 无匹配时执行 DEFAULT_BLOCK；
- DEFAULT_BLOCK 可以为 `none`；
- item/default block 正常结束后继续 switch 后的下一条指令；
- `break BREAK_ID_VAR` 提前退出该 switch。

`SYNTAX.md` 中包含 `_` 的 pattern switch 分支不能直接存入常量表；frontend 必须把它们降为 `if` 或多个结构化判断。

### 16.7 try/catch/finally

标准形式：

```bil
try blk(TRY_BLOCK)
    EXCEPTION_VAR
    res(CATCH_TABLE)
    blk(FINALLY_BLOCK)
    BREAK_ID_VAR
```

其中：

- EXCEPTION_VAR 必须是可容纳异常或 null 的类型；
- CATCH_TABLE 是按源码顺序排列的 `{ exception-type → block }` 表；
- FINALLY_BLOCK 可以为 `none`；
- BREAK_ID_VAR 是进入 try 时绑定的 region-exit capability（见 §16.5）。

语义：

1. 绑定唯一 BREAK_ID 到 BREAK_ID_VAR；
2. 执行 TRY_BLOCK；
3. 进入 finally 前恒写 EXCEPTION_VAR：仅当待处理 completion 为 Throw
   （带异常对象）时写入该异常对象；Normal/Return/Break/Continue 等一切
   非 Throw completion 一律显式写 null（不得遗留旧值）；
4. 抛出异常时，按顺序选择第一个兼容 catch 类型；
5. 命中时把异常写入 EXCEPTION_VAR 并执行 catch block；
6. catch 正常完成后，当前逃逸异常变为 null；
7. 未命中或 catch 再次抛出时，EXCEPTION_VAR 保存当前逃逸异常；
8. 执行 FINALLY_BLOCK；
9. finally 正常完成后，有逃逸异常则继续抛出，否则继续 try 后下一条指令；
10. finally 自身的 abrupt completion 覆盖此前待继续的 completion。

`break BREAK_ID_VAR`（try 的 BREAK_ID）在 try 边界消费：无论 break 源自
TRY_BLOCK、某个 catch handler 还是 FINALLY_BLOCK，都先完成必要的 finally
执行，再在 region 弹出时消费并续 try 后下一条指令。`break` 的 completion
不是 Throw，绝不参与 catch 类型匹配。

finally block 可读取 EXCEPTION_VAR，从而实现 `finally(e)` 中“无异常时为 null”的语义。

### 16.8 return

```bil
ret
ret VALUE
```

规则：

- `.return = .void` 的函数使用 `ret`；
- 非 void 函数使用 `ret VALUE`；
- VALUE 类型必须严格等于 `.return`；
- `ret` 退出整个函数，不只是当前 block；
- frontend 必须保证所有函数路径具有符合 `SYNTAX.md` 的显式返回。

### 16.9 throw

```bil
throw EXCEPTION
```

EXCEPTION 必须是 Rigi 异常根类型的兼容值。兼容性若需要视图转换，frontend 必须先生成 `cast`。

---

## 17. 协程指令

### 17.1 await

```bil
await TASK
await TASK RESULT
```

规则：

- `await TASK` 用于无结果 `core.coroutine.Task`；
- `await TASK RESULT` 用于 `Task\<TResult>`；
- RESULT 类型必须严格为 TResult；
- Task 成功时取得结果；
- Task 失败时在 await 点重新抛出保存异常；
- Task 取消时传播取消；
- Task 未完成时挂起当前 Coroutine，并在其原 Executor 恢复。
- Task 已终止时不要求实际挂起；未终止时必须先保存 continuation，再原子登记 waiter
  并转为 `Suspended`，避免终态与登记竞态丢失唤醒。
- waiter 终态恢复时重新发布到 waiter 自己永久绑定的 Executor，而不是 Task 所属 Coroutine
  的 Executor。

BIL 不规定 continuation frame 和 Worker 调度的物理实现。continuation frame、state 编号、
waiter 数据结构和 Native ABI 仍不属于 BIL；Middleware 可以将本指令降为状态机，但不得
改变上述可观察语义。

### 17.2 yield

```bil
yield
yield ALARM
```

规则：

- 裸 `yield` 结束当前 run-to-suspension 执行段并重新参与调度；
- `yield ALARM` 接受 `PollingAlarm` 或 `EventAlarm` 兼容值；
- 即使 Alarm 已就绪，带 Alarm 的 yield 仍结束当前执行段；
- 普通函数与普通 lambda 也可以包含 `await` / `yield`；
- `async` 只决定调用时是否创建新 Coroutine。

### 17.3 using 与可挂起清理

BIL 不定义单独的 `using` 指令。

frontend 必须把 `seq using(...)` 生成为结构化初始化、清理记录和 `try/finally` 路径，使：

- 初始化按源码顺序；
- 清理按逆序；
- return、异常、break 等离开路径均经过清理；
- await/yield 只挂起，不触发提前清理；
- `dispose()` 自身可 await/yield；
- 外层 completion 必须等待清理全部完成。
- 清理记录、当前 `dispose()` 调用和逆序清理游标属于 continuation 的活跃状态；finally
  中的挂起恢复后必须回到同一清理进度。BIL 不增加 `using` 或 cleanup opcode。

---

## 18. 提示指令

```bil
hint res(RESOURCE_ID)
```

`hint` 向 backend 提供一段可忽略的提示；RESOURCE_ID 的资源内容是一段 JSON 文本。

规则：

- `hint` 只能出现在 block 内；
- RESOURCE_ID 必须引用本模块已声明的 `string` 资源；
- 本标准不定义 JSON 内容的 schema，由生产方（frontend）与消费方（backend）另行约定；
- `hint` 无结果变量，不读写任何变量，不参与 definite assignment，不是终结指令，不影响控制流与异常传播；
- `hint` 仅是指令流中的位置标记，不附着于任何特定指令、block 或符号，位置含义由消费方按 JSON 内容自行解释。

**从模块中删除全部 `hint` 指令后，程序的 §22.2 可观察行为必须完全不变。**

VM 执行 `hint` 为 no-op。

Middleware 可以依据 `hint` 内容改进代码生成或产出附加元数据（如调试信息），也可以整体忽略；`hint` 内容不得影响可观察语义。`hint` 内容无法解析或不符合消费方预期时，消费方必须忽略该条 `hint`，不得因此拒绝编译。

---
