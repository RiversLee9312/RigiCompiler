// 可选压力语料，由 Run-MqStress.ps1 复制至 playground 并代入数量。
// 不属于 test --all；原生台账只用于测试观测，MQ 本体无 native。
import core.messaging.*
import core.coroutine.*
import core.collections.*
import core.serialization.Serializable
import core.io.Console
@NativeLibrary("rigi_rt")
@NativeSymbol("mem_live_bytes")
priv native func liveBytes(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("worker_live_count")
priv native func workers(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("worker_running_count")
priv native func running(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("gc_active")
priv native func gcActive(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("gc_debt_bytes")
priv native func gcDebt(): i64
func runtimeStatus(): String {
    return (((((((" workers=" + workers().toString()) + " running=") + running().toString()) + " gc-active=") + gcActive().toString()) + " gc-debt=") + gcDebt().toString())
}
@Serializable
pub shared class StressMessage {
    pub var sender: i32
    pub var sequence: i32
    pub init(_ -> sender, _ -> sequence)
}
func require(value: bool) {
    if (not value) { throw new core.RuntimeException("MQ压力断言失败") }
}
pub shared class Producer : core.AsyncAction {
    priv const sender: Messenger\<StressMessage>
    priv const id: i32
    pub init(_ -> sender, _ -> id)
    pub override async operator call() {
        var i: i32 = 0
        var progress: i32 = 12500
        while (i < 62500) {
            await sender.send(new StressMessage(id, i))
            i = i + 1
            if (i == progress) {
                Console.println(((("sender=" + id.toString()) + " accepted=") + i.toString()) + runtimeStatus())
                progress = progress + 12500
            }
        }
        Console.println(((("sender=" + id.toString()) + " accepted=") + i.toString()) + runtimeStatus())
    }
}
pub shared class Consumer : core.AsyncAction {
    priv const reader: Reader\<StressMessage>
    priv const slow: bool
    priv const id: i32
    pub const order: Array\<i32>
    pub init(_ -> reader, _ -> slow, _ -> id) { order = arrayOf\<i32>(250000) }
    pub override async operator call() {
        if (slow) { yield sleep(100) }
        const counts = arrayOf\<i32>(4)
        var i: i32 = 0
        var progress: i32 = 12500
        while (true) {
            const value = await reader.next()
            if (value.isEos) { break }
            const item = value.item as StressMessage
            require((item.sender >= 0) and (item.sender < 4))
            require(item.sequence == (counts[item.sender] as i32))
            counts[item.sender] = item.sequence + 1
            require(i < 250000)
            order[i] = (item.sender * 62500) + item.sequence
            i = i + 1
            if (i == progress) {
                Console.println(((("reader=" + id.toString()) + " received=") + i.toString()) + runtimeStatus())
                progress = progress + 12500
            }
        }
        require(i == 250000)
        Console.println(((("reader=" + id.toString()) + " received=") + i.toString()) + runtimeStatus())
        require(((counts[0] as i32) == 62500) and ((counts[1] as i32) == 62500))
        require(((counts[2] as i32) == 62500) and ((counts[3] as i32) == 62500))
        require((await reader.next()).isEos)
        reader.dispose()
    }
}
func batch() {
    const owner = new Messenger\<StressMessage>()
    const a = new Consumer(owner.createReader(), false, 0)
    const b = new Consumer(owner.createReader(), false, 1)
    const c = new Consumer(owner.createReader(), false, 2)
    const d = new Consumer(owner.createReader(), true, 3)
    const ca = new Task(a)
    const cb = new Task(b)
    const cc = new Task(c)
    const cd = new Task(d)
    const pa = new Task(new Producer(owner, 0))
    const pb = new Task(new Producer(owner, 1))
    const pc = new Task(new Producer(owner, 2))
    const pd = new Task(new Producer(owner, 3))
    ca.run(new IOExecutor())
    cb.run(new ComputeExecutor())
    cc.run(new IOExecutor())
    cd.run(new ComputeExecutor())
    pa.run(new ComputeExecutor())
    pb.run(new IOExecutor())
    pc.run(new ComputeExecutor())
    pd.run(new IOExecutor())
    await pa
    await pb
    await pc
    await pd
    owner.dispose()
    await ca
    await cb
    await cc
    await cd
    var i: i32 = 0
    while (i < 250000) {
        require((a.order[i] as i32) == (b.order[i] as i32))
        require((a.order[i] as i32) == (c.order[i] as i32))
        require((a.order[i] as i32) == (d.order[i] as i32))
        i = i + 1
    }
}
pub func main(): i32 {
    Console.println("live-before=" + liveBytes().toString())
    batch()
    Console.println("posts=250000")
    Console.println("deliveries=1000000")
    Console.println("live-after=" + liveBytes().toString())
    return 0
}
