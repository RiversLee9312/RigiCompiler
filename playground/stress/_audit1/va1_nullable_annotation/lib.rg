// V-A probe c1c 配套库：priv 类型 Hidden + pub 持有器（可空字段）
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class Holder {
    pub init()
    pub var hm: Hidden? = null
}
