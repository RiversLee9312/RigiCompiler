// 两个 executor 重复释放同一 Reader：幂等检查与 capability release 同锁。
// expect-output: disposed-races=50
import core.messaging.*
import core.coroutine.*
import core.serialization.Serializable
import core.io.Console
@Serializable
pub shared class Msg { pub var id: i32 = 0 }
pub shared class Release : core.AsyncAction {
    priv const reader: Reader\<Msg>
    pub init(_ -> reader)
    pub override async operator call() { reader.dispose() }
}
pub func main(): i32 {
    var i: i32 = 0
    while (i < 50) {
        const messenger = new Messenger\<Msg>()
        const reader = messenger.createReader()
        const a = new Task(new Release(reader))
        const b = new Task(new Release(reader))
        a.run(new IOExecutor())
        b.run(new ComputeExecutor())
        await a
        await b
        reader.dispose()
        messenger.dispose()
        i = i + 1
    }
    Console.println("disposed-races=50")
    return 0
}
