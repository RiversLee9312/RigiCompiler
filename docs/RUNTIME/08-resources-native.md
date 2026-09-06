# 确定性资源管理与 native 互操作（§25–§26）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 25. 确定性资源管理：`IDisposable`、`using` 与全局泄漏异常

Rigi 明确不支持 finalizer，也不允许运行时在对象回收阶段调用任意用户终结逻辑。对象内存由 microGC/microSGC/macroGC 管理；文件、句柄、流、锁封装等外部资源则由 `core.IDisposable` 确定性管理。

概念接口为：

```rigi
pub interface IDisposable {
    func dispose()
}
```

### 25.1 `using` 的 lowering

`seq using(...) ... named ... {}` 中的每个资源绑定由编译器 lowering 为与该 `seq` 词法退出绑定的清理记录：

- 初始化按源码顺序进行；
- 只有成功完成初始化的绑定才进入清理栈；
- 退出 `seq` 时按逆序调用 `dispose()`；
- 正常返回、`return@`、裸 `return`、异常展开及其他离开作用域的控制流共享同一清理路径；
- `await`/`yield` 只保存并挂起当前 Coroutine 状态，不触发清理，资源记录随 Coroutine frame 保留；
- `dispose()` 本身是普通函数，可以 `await` 或 `yield`。清理中的挂起保存当前 disposal 调用、逆序清理进度和尚未处理的资源；恢复后继续同一清理路径；
- 外层函数的 return、异常继续传播或 Coroutine 的终态发布，必须等待已建立的 `using` 清理全部完成，不能通过丢弃 frame 绕过可挂起 disposal。

`using` 提供的是编译器保证的确定性调用，不依赖引用计数何时归零，也不依赖 macroGC 是否运行。可挂起只延长清理路径，不降低“每个 `IDisposable` 必须由用户代码负责 dispose”的强制要求。

### 25.2 销毁时的强制检查

`TypeSheet.typeFlags` 的 `DISPOSABLE` 位标识类型是否实现 `core.IDisposable`；对应 Object 的生命周期元数据中带有运行时可检查的 disposal 状态。无论对象最终由 microGC、microSGC 还是 macroGC 销毁，运行时都必须在释放其内存前检查该状态：

- 已由用户代码直接或通过 `using` 调用 `dispose()`：正常继续销毁；
- 从未调用 `dispose()`：立即产生全局的 undisposed-resource 异常事件。

若 `dispose()` 正在挂起，对象及清理记录仍由对应 Coroutine frame 保持，尚未进入对象销毁检查；运行时不能把“正在执行可挂起 disposal”误当成未负责的遗失资源。真正到达 microGC、microSGC 或 macroGC 销毁点而 disposal 状态仍表明从未调用 `dispose()` 的对象必须爆炸上报。

这一后门是错误检测机制，不是隐式清理机制：

- GC **绝不**替对象调用 `dispose()`；
- 不建立 finalization queue；
- 不延迟对象释放来等待用户终结逻辑；
- 不允许对象复活；
- 上报后仍按正常内存生命周期完成销毁。

该异常不绑定到“恰好触发最后一次 release 或 macroGC”的普通用户调用栈，因此普通 `try/catch` 不能接住。它只能通过 `core.GlobalExceptionHandler` 提供的全局处理方法接收。运行时应至少携带对象实际类型；实现还可以附加创建位置、最后释放位置等诊断信息。

**MW12b 定稿形态**：

- **API 面**（stdlib `core/global_exceptions.rg`，namespace `core`）：
  ```rigi
  pub class GlobalExceptionHandler {
      pub static func register(handler: core.Action\<core.Exception>)
      pub static func dispatch(exc: core.Exception)
  }
  ```
  事件载荷类型为 `core.UndisposedResourceException : RuntimeException`，唯一 init `init(resourceType: String)`，message 模板「对象在销毁前从未调用 dispose()：${resourceType}」；`resourceType` 是违规对象的**实际类型全名**。处理器注册表存 native（rigi_rt `gexc.c` 三面 `gexc_register_handler/handler_count/handler_at`，+1 持有，注册序=下标序）——SYNTAX §3.1.1 共享安全闸门禁止静态字段持 local `Action`。
- **派发时机**：入口收尾统一派发——native 由生成代码 entry stub 在 main/drain 之后、失败汇总之前循环 `rigi_gexc_take` 逐条真构造异常并调 `dispatch`；VM 由 `BilVm.Run` 在同一时点经 C# 终结器（`VmObject.DisposedMarked` 未标记且类型 implements `IDisposable` → 入队）+ `GC.Collect`/`WaitForPendingFinalizers` 后逐条派发。每次 `dispatch` 调用后做 pending 检查，处理器自身抛异常走正常失败汇总。
- **默认行为**：注册表为空时 `dispatch` 打印默认 stderr 行 `core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：<类型全名>`。**进程继续，退出码不变**。
- **晚到事件**：native 侧 `globals_cleanup`（静态槽释放）与 GC 终轮收集阶段入队的事件不经用户处理器，由 `rigi_gexc_flush_default` 在 atexit 打印同文本默认行；VM 侧静态槽/单例保持根住、不模拟退出清理，因而不产生晚到事件。

这一分工保持三类生命周期彼此独立：

```text
托管内存：microGC / microSGC / macroGC
外部资源：dispose() / using
遗漏检测：destruction-time global exception
```

---

## 26. native 互操作与 `rigi_rt`

`native` 函数（`SYNTAX.md` §4.6）把 Rigi 调用路由到运行时原生方法面。原生方法面由一个 C 编写的 shim 库提供，库标识为 `rigi_rt`：它把 libc 风格的 C 函数包装为 Rigi 调用约定下的可调用入口，并负责 Rigi 值（如 `String` 的 native 表示）与 C 类型之间的转换。

- **调用约定**：fastcall；精确的寄存器/栈分配、胖值槽传递与 `String` 布局规则由 Middleware 定义。
- **第一版原生方法面**只有五个函数，不提供可变参数：
  - `print(text: String)`：把字符串写入标准输出；
  - `printErr(text: String)`：把字符串写入标准错误；
  - `any_to_string(value: Any): String`：`SYNTAX.md` §3.8 的 `toString` 内建承载——内建基本类型（数值/`bool`/`char`）返回标准文本（`String` 的 `toString` 即自身，不经此路由）；未覆写 `toString` 的对象返回其类型 canonical 名。它只经标准库 `.bootstrap.rg` 的文件级私有 native 全局声明暴露：`Any` 上声明 open `toString(): String`（全类型承诺，自带实现），`Object` 提供 open `override` 默认实现；二者的实现体由编译器合成为「装箱接收者后调用 `any_to_string`」的小函数，用户代码不直接调用 `any_to_string`。用户类型 `override` 后经普通虚派发执行自身实现，不再命中原生面。
  - `any_hash(value: Any): i64`：`SYNTAX.md` §3.8.1 的 `hash` 内建承载（Map 键判等）——`String` 按内容哈希（FNV-1a 64 over data 字节）、标量按值（payload 8 字节 FNV-1a）、对象与堆值按 payload（堆指针）FNV-1a（身份，不直接返回裸指针）、`null` 固定 `0`。只承诺同一进程内同值必同哈希，与 BIL VM hook 的哈希数值不要求一致；哈希不保证分布均匀，允许碰撞。与 `any_to_string` 同构：只经标准库 `.bootstrap.rg` 的文件级私有 native 全局声明暴露（`Any` open `hash(): i64` 全类型承诺 + `Object` open `override` 默认实现，实现体由编译器合成为「装箱接收者后调用 `any_hash`」的小函数），用户代码不直接调用 `any_hash`；用户类型 `override hash` 后经普通虚派发执行自身实现，不再命中原生面。
  - `alloc_array(typeid, size)`：分配元素零值初始化的 `Array\<T>`（T 由泛型 hidden typeid 物化，传参形态见 §10）。它只经标准库的私有 native 声明暴露：`Array\<T>` 的合法构造入口是 stdlib 的 `arrayOf\<T>(size)` 与 `arrayOfElements\<T>(elements...)`（后者在 Rigi 层把元素逐项放入），用户代码不直接调用 `alloc_array`。两个入口签名分离（长度 vs 元素包），不存在 `i32` 长度与 `i32` 元素的重载混淆。T 为 enum struct 时 `arrayOf` 由 frontend 在泛型实例化点拒绝（`BIL_STANDARD.md` §14.3「enum 无零值」）。**元素读写语义（Q6，`SYNTAX.md` §13.2）**：`a[i]` 读取语义上走 `getAtIndex`（返回 `T?`），实现上由编译器直发 `BIL_STANDARD.md` §13.6 `get.array`——界内得 `Nullable\<T\>` 包装的元素、**越界读取得 `null` 而非 trap**；`a[i] = v` 写入仍收非空 `T`，越界写入抛可捕获 `core.OutOfBoundException`（MW9b 起；此前为运行时 trap/abort）。
  - `timer_create` / `alarm_wait`：`sleep` 与 `Timer` 的时钟底座（§19.3/§19.4/§19.5）。`sleep` 构造内部 `SleepAlarm`，不另暴露 `make_sleep_alarm`。用户代码不直接调用。
- **GC 类设施（如 GCAlarm）不属于本表面，也不进 stdlib 与 VM**：BIL 明确规定不得对 GC 机制与实现作任何假设（`BIL_STANDARD.md` §1.1/§22.1），此类设施是 Middleware 的内部实现细节，没有任何跨层可见形态。
- **BIL VM 不链接原生库**：VM 对 `(lib, symbol)` 命中 `BIL_STANDARD.md` §22.5 内建 hook 表的 native 调用直接执行内建行为，因此在没有 Middleware 与 `rigi_rt` 实现的环境下也能完整执行程序。
- 标准库在 Rigi 层封装原生方法面（如 `core.io::Console.println` 调用 `print`），用户代码不直接依赖 `rigi_rt`；格式化、插值等逻辑全部在 Rigi 层演进，不进入原生方法面。
