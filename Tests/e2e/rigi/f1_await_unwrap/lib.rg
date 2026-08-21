// F1/V-B e2e 配套库（负例·多文件同组）：Task\<HiddenS\> 经 pub 字段泄出
//（await 解包得 HiddenS）。
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
