// expect-output: owner-teardown-ok
// 3b-β 对拍：属主协程终止过户——expose 发生在协程内（壳属主 = 该
// 协程），属主 DONE 触发 rigi_ch_destroy_payload → teardown（子图
// promote + owner_reg 改指全局表）；他线程（Compute Worker 上的
// 后续协程）仍持 Handle，load 经全局壳路径正常；最终经全局直接路径
// 释放。壳锚保活的 box 在属主局部变量消亡后仍可 load（handle_value_
// positive 的 dispose 后 load 验收的跨协程强化版）。
import core.collections.*
import core.coroutine.*
import core.io.Console
shared class ShellCell {
    pub var v: i32 = 0
}
async func shellProduce(sink: Array\<Handle\<ShellCell>>): i32 {
    const box = new ShellCell()
    box.v = 7
    unsafe seq using(const place = placeOf box) {
        sink[0] = place.expose()
    }
    yield
    // 协程终态：属主终止 → 壳过户全局表（box 仅剩壳锚保活）
    return 0
}
async func shellConsumeLate(sink: Array\<Handle\<ShellCell>>): i32 {
    const h = sink[0] as Handle\<ShellCell>
    yield
    unsafe seq {
        if (h.load().v != 7) { throw new core.RuntimeException("过户后 load 值错误") }
    }
    return 0
}
pub func main(): i32 {
    const sink = arrayOf\<Handle\<ShellCell>>(1)
    const producer = new Task\<i32>(func{async (): i32 -> await shellProduce(sink)})
    producer.run(new ComputeExecutor())
    if ((await producer) != 0) { throw new core.RuntimeException("produce 失败") }
    const consumer = new Task\<i32>(func{async (): i32 -> await shellConsumeLate(sink)})
    consumer.run(new ComputeExecutor())
    if ((await consumer) != 0) { throw new core.RuntimeException("consume 失败") }
    Console.println("owner-teardown-ok")
    return 0
}
