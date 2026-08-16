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
  - `alloc_array(typeid, size)`：分配元素零值初始化的 `Array\<T>`（T 由泛型 hidden typeid 物化，传参形态见 §10）。它只经标准库的私有 native 声明暴露：`Array\<T>` 的合法构造入口是 stdlib 的 `arrayOf\<T>(size)` 与 `arrayOfElements\<T>(elements...)`（后者在 Rigi 层把元素逐项放入），用户代码不直接调用 `alloc_array`。两个入口签名分离（长度 vs 元素包），不存在 `i32` 长度与 `i32` 元素的重载混淆。T 为 enum struct 时 `arrayOf` 由 frontend 在泛型实例化点拒绝（`BIL_STANDARD.md` §14.3「enum 无零值」）。
  - `make_sleep_alarm(milliseconds: i64): EventAlarm`：创建基于单调时钟、到期转 ready 的粘滞事件 Alarm（§19.3）。只经 stdlib `sleep` 的私有 native 声明暴露（§19.4），用户代码不直接调用。
- **GC 类设施（如 GCAlarm）不属于本表面，也不进 stdlib 与 VM**：BIL 明确规定不得对 GC 机制与实现作任何假设（`BIL_STANDARD.md` §1.1/§22.1），此类设施是 Middleware 的内部实现细节，没有任何跨层可见形态。
- **BIL VM 不链接原生库**：VM 对 `(lib, symbol)` 命中 `BIL_STANDARD.md` §22.5 内建 hook 表的 native 调用直接执行内建行为，因此在没有 Middleware 与 `rigi_rt` 实现的环境下也能完整执行程序。
- 标准库在 Rigi 层封装原生方法面（如 `core.io::Console.println` 调用 `print`），用户代码不直接依赖 `rigi_rt`；格式化、插值等逻辑全部在 Rigi 层演进，不进入原生方法面。
