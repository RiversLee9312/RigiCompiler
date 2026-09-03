// Rigi 标准库：全局异常通道（MW12b，RUNTIME §25.2）。
// UndisposedResourceException：IDisposable 对象销毁时 disposed 位未置位
// （从未调用 dispose()）产生的全局异常事件载荷。GC 只检查并上报，绝不
// 代替用户调用 dispose()。
// GlobalExceptionHandler：全局异常事件的静态注册/派发面。entry stub
//（native 生成代码）与 BilVm.Run 收尾（VM 侧）都在 main/drain 之后、
// 失败汇总之前统一 drain 事件队列，逐条构造 UndisposedResourceException
// 调 dispatch；处理器抛异常走正常失败汇总。晚到事件（native 侧
// globals_cleanup 与 GC 终轮收集阶段；VM 侧静态槽/单例保持根住不产生）
// 不经本类，由 rigi_rt C 侧默认打印（atexit flush），文本与 dispatch
// 空注册表分支一致。
// 注册表承载：SYNTAX §3.1.1 共享安全闸门禁止全局/静态字段持 local
// class（Action 非 shared），处理器本体由 rigi_rt gexc.c 注册表 +1 持有
// （core.coroutine 未观察失败注册表同先例），Rigi 侧只经三个 priv
// native 面访问，注册序 = 下标序。
namespace core

pub open class UndisposedResourceException : RuntimeException {
    pub var resourceType: String

    pub init(resourceType: String) {
        message = "对象在销毁前从未调用 dispose()：${resourceType}"
        this.resourceType = resourceType
    }
    pub override func getMessage(): String { return message }
}

pub class GlobalExceptionHandler {
    // C 符号 = rigi_ + 短名直拼（无大小写换算），故用 print_err 命中
    // shim.c 的 rigi_print_err（Console.rg 的 printErr 声明是死面——
    // 原生侧从未真调，一调即链接失败；VM 侧两键同 hook）
    @NativeLibrary("rigi_rt")
    @NativeSymbol("print_err")
    priv static native func printErr(text: String)

    @NativeLibrary("rigi_rt")
    @NativeSymbol("gexc_register_handler")
    priv static native func registerHandler(handler: Any): i64

    @NativeLibrary("rigi_rt")
    @NativeSymbol("gexc_handler_count")
    priv static native func handlerCount(): i64

    @NativeLibrary("rigi_rt")
    @NativeSymbol("gexc_handler_at")
    priv static native func handlerAt(index: i64): Any

    pub static func register(handler: core.Action\<core.Exception>) {
        registerHandler((handler as Any))
    }

    pub static func dispatch(exc: core.Exception) {
        const count = handlerCount()
        if (count == (0 as i64)) {
            // 空注册表默认行为：stderr 打印一行，进程继续（退出码不变）。
            // 文本与 rigi_rt gexc.c 的 atexit flush 一致
            if (exc is core.UndisposedResourceException) {
                printErr(("core::UndisposedResourceException: " + exc.getMessage()) + "\n")
            } else {
                printErr(exc.getMessage() + "\n")
            }
            return
        }
        var i: i64 = (0 as i64)
        while (i < count) {
            const handler = (handlerAt(i) as core.Action\<core.Exception>)
            handler(exc)
            i = (i + (1 as i64))
        }
    }
}
