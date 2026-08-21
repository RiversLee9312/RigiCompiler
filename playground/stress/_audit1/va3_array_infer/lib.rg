// V-A probe p15a 配套库：priv Hidden 经 pub 字段以 Array\<Hidden> 泄出
import core.collections.*
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class ArrHolder {
    pub init()
    pub var arr: Array\<Hidden> = arrayOf\<Hidden>(1)
}
