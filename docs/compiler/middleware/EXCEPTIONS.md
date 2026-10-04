# Middleware 异常传输与清理

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

## 8. 异常机制

**传输模型（MW9a 定稿）：checked-flag 便携异常传输，零平台 EH 指令。**
throw 不触发任何 unwind：异常对象 acquire +1 后写入线程局部 pending 槽
（`rigi_exc_raise`）；每个可抛调用返回后由生成代码查 pending
（`rigi_exc_pending`），非空即沿 MIR 异常边（ExcTarget）跳传播路径；捕获
点 `rigi_exc_take` 取走并清空槽（+1 所有权随返回值移交捕获方）。整条路径
只是普通调用 + 分支 + TLS 槽读写，win-x64/linux-x64 同一份实现。

**为何弃 landingpad/SEH（取舍记录）**：

- catch 匹配是 TypeSheet 的 `is` 判定（RUNTIME §12），不是 C++ RTTI 的
  type_info 匹配——平台 EH 的 catch 选择器语义与本语言对不上，personality
  里仍得自己跑 `is` 链，landingpad/SEH 只剩「找到 landing 点」一项职能；
- Rigi 调用的 native 中间帧只有 FFI 叶调用（无外来帧回调再入 Rigi 的栈
  形），unwind 无需穿越外来帧，平台 unwind 器的跨帧能力无消费方；
- ARC 配对在 MIR 层显式化（RcInjection 传播垫按 ret 出口同口径 release
  全部托管槽），异常路径的每次 acquire/release 可被 `RIGI_RT_MEMTRACK=1`
  台账全路径验证；平台 EH 的 cleanup 路径绕开 MIR，台账口径对不齐；
- 与协程挂起点天然统一：挂起/恢复点已是「调用返回后查标志位」形态，
  pending 检查复用同一 codegen 骨架，不引入第二套控制流；
- 双平台单实现：免去 Itanium landingpad 与 SEH catchswitch 两套发射与两
  个人格函数，NativeE2E 对拍覆盖面不因平台分裂。

MIR 保持 ExcTarget 双目标抽象（正常后继 / 异常边），checked-flag 只是
Emit 层的一种 lowering；未来若切原生 EH，改动封闭在 Emit（调用点改
invoke、传播垫改 landingpad/catchswitch），MIR 与 RcInjection 不变。

**MIR 构件（MW9a）**：

- `MirThrow`（指令）：throw 语句本体——RcInjection 配平后 raise 并入
  pending，随后沿异常边传播；
- `MirTakePending`（指令）：派发垫首指令，`rigi_exc_take` 出异常对象写入
  合成局部 `$mw.exc.N`；
- `MirRetThrow`（终止符）：传播垫出口——release 配平后返回调用方，
  pending 槽保持置位（checked-flag 跨帧传播的最后一棒）；
- `ExcTarget`（MirCall / MirInvokeIndirect / MirThrow 的异常边字段）：本
  词法上下文的异常落点，由 TryExpander 按 BIL §16.7 十步语义解析；为
  null 时由 RcInjection 改写指向函数级共享传播垫 `mw.propagate`。
  MW9b-G 扩面到守卫型可抛指令（MirBinaryIntrinsic 整数除零 /
  MirCast / MirUnboxAny / MirGetField 拆箱守卫 / MirSetArray 越界写 /
  MirNewIndirect 无匹配 init）——守卫命中由 ExceptionEmitter 共享抛出
  辅助构造真异常（alloc + 真 init + `rigi_exc_raise`）后 br 进同一
  异常边，与用户 throw 同路；
- 派发垫 `mw.try.N.dispatch`：try 入口侧——TakePending 后按 catch 表序
  走 `is` 链（表序即匹配序，保序语义），命中进 catch 前置垫，未命中走
  finally/外层；
- finally 单块双入口 + completion 路由器：一切离开 try 的 completion
  （normal / return / break / continue / throw）经前置垫记路由码，finally
  体执行后由路由器按码续解析落点；`finally(e)` 的 e 仅 Throw completion
  时写入异常对象，其余 completion 一律见 null（BIL §16.7）。

**顶层 reporter**：合成 `rigi_entry` 在 main/drain 后汇总 main 保存的异常、
main settle 失败与未观察失败，按优先级进入 typed uncaught reporter；库 API
边界在 wrapper 内做同样的 pending 检查。`rigi_type_name_of` 取诊断名 +
虚派发 `getMessage()`，stderr 打
印 `{类型全名}: {message}`，`rigi_exc_halt` 收尾 exit 1。

**全局异常通道 carve-out**：RUNTIME §25.2 undisposed-resource 等不绑定用
户调用栈的事件不经 checked-flag、不可 try/catch，走
`core.GlobalExceptionHandler` API 通道（**MW12b 已定稿**：API 在
stdlib/core/global_exceptions.rg，register/dispatch 静态二面 +
UndisposedResourceException；事件队列与处理器注册表沉 rigi_rt gexc.c；
entry stub 在 main/drain 后、失败汇总前循环 `rigi_gexc_take` 统一派发；
晚到事件——globals_cleanup 与 GC 终轮收集阶段入队——不经用户处理器，
由 `rigi_gexc_flush_default` 在 atexit 打印默认文本）。

相关章节：[ARC §4](MEMORY_MANAGEMENT.md)、[协程 §6](COROUTINE_LOWERING.md)、[runtime 面 §4.8](RUNTIME_ABI.md)。
