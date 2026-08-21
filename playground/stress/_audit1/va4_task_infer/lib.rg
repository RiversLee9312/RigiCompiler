// V-A probe p16b 配套库：priv shared HiddenS 经 pub 字段以 Task\<HiddenS> 泄出
import core.coroutine.*
shared class HiddenS {
    pub init()
    pub func n(): i32 { return 1 }
}
async func mkS(): HiddenS { return new HiddenS() }
pub shared class TaskHolder {
    pub init()
    pub var t: Task\<HiddenS> = mkS()
}
