// Rigi 标准库：core.coroutine 协程类型面（S10，SYNTAX §4.5/§7.5、
// RUNTIME.md §17–§20）。
// 协程运行时机制（async/await/yield、Task 终态、Executor 调度、Alarm
// 等待）是语言内建语义，BIL VM（S14）提供执行；本文件只声明类型与
// 最小运行时函数面的**形状**：
//   - Task/Task\<TResult> 是运行时内建的 shared Object 句柄（RUNTIME
//     §18.2；同名不同元数合法共存，SYNTAX §15.3）——await 是运算符
//     （S13 lowering），类型面无成员；abstract 防用户构造（P3 静态拒绝）。
//   - Executor 家族是协程永久绑定的调度域（RUNTIME §17.1），实例可被
//     全局持有（shared）；具体选择 API 随 S13 定稿，当前仅类型面。
//   - PollingAlarm.isReady 是同步探测方法（RUNTIME §19.2）——实例方法
//     不能 native（§4.6），以 abstract 表达运行时实现面（RangeEnumerator
//     先例）；EventAlarm 由事件源 callback 通知（§19.3）。
//   - CoroutineLocal\<TValue> 是 per-coroutine 上下文的唯一机制
//     （§20.2），实例通常经全局存储持有（shared）；get/set 成员随 S13 定稿。
//   - make_sleep_alarm 是 native 面（§19.4 / RUNTIME §26）：i64 毫秒 →
//     粘滞 EventAlarm；sleep 是 Rigi 层包装（i32 → i64 显式 as）。
//     §4.6 返回类型放宽（S10）的使用点；FFI ABI 归 Middleware。
namespace core.coroutine

pub shared abstract class Task\<TResult> {
}

pub shared abstract class Task {
}

pub shared abstract class Executor {
}

pub shared abstract class MainExecutor : Executor {
}

pub shared abstract class ComputeExecutor : Executor {
}

pub shared abstract class IOExecutor : Executor {
}

pub shared abstract class PollingAlarm {
    pub abstract func isReady(): bool
}

pub shared abstract class EventAlarm {
}

pub shared abstract class CoroutineLocal\<TValue> {
}

@NativeLibrary("rigi_rt")
@NativeSymbol("make_sleep_alarm")
priv native func make_sleep_alarm(milliseconds: i64): EventAlarm

pub func sleep(milliseconds: i32): EventAlarm {
    return make_sleep_alarm((milliseconds as i64))
}
