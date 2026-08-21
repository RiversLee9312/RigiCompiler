// V-B probe c11a 配套库：priv Hidden 经 pub 可空字段 + ?. 链泄出
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class SafeHolder {
    pub init()
    pub var hm: Hidden? = null
}
