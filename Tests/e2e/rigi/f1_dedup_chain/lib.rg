// F1/c6 e2e 配套库（负例·多文件同组）：priv Hidden 经 pub 字段泄出。
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class ChainHolder {
    pub init()
    pub var hm: Hidden = new Hidden()
}
